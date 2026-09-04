"""
Sandboxed replay of a stored history through a real detector (WS6, F14).

`run_simulation` builds a detector instance in a LOCAL variable and throws it
away when the call returns. That is the whole point of this module, and it is
what separates it from `ScoreBatch`:

  ScoreBatch resolves the model for (entity_id, detector) out of
  DetectorRegistry._detectors and cold-start-fits it when absent, so running it
  against a tracked entity mutates the model that is scoring production traffic
  (F14). A "what if I set window to 240?" panel MUST NOT be able to do that.

Because the instance is never registered:
  - it is invisible to registry._streaming_keys(), so the checkpoint sweep never
    sees it and no directory or file appears under /data/models;
  - it takes no per-entity lock, so a simulation cannot stall live scoring;
  - it never sets checkpoint_dirty.

The registry is still used as the FACTORY (_create_detector), so a simulation
scores through exactly the same construction path as production — a simulator
that built detectors its own way would answer a question nobody asked.
"""

from __future__ import annotations

import logging

logger = logging.getLogger(__name__)

# Mirrors rmad_detector._Z_SCALE. Imported lazily in _robust_z so this module
# stays importable when only "hst" is ever simulated.
_ROBUST_Z_DETECTORS = ("rmad",)

# score == 1.0 is rung 4 of rmad's scale ladder (a window that is a single
# constant, and a reading that differs) — genuinely "infinitely far". The wire
# type is a double and the value crosses a JSON boundary on the .NET side, where
# Infinity is not serializable, so the inverse is clamped to a large finite
# number instead. Chosen over dropping the point: the operator must see that the
# reading was off-scale, not that it was missing.
_MAX_ROBUST_Z = 1.0e6


def _robust_z(detector: str, scores: list[float]) -> list[float]:
    """Invert score = z / (z + z_scale) back to z, or [] when z is undefined.

    Only rmad publishes a squashed robust z, so only rmad can be inverted. hst
    scores rarity (F4) — there is no deviation to report and inventing one would
    be the same category error this release exists to remove.
    """
    if detector not in _ROBUST_Z_DETECTORS:
        return []

    from argus_detector.rmad_detector import _Z_SCALE  # lazy import

    out: list[float] = []
    for score in scores:
        if score <= 0.0:
            out.append(0.0)
        elif score >= 1.0:
            out.append(_MAX_ROBUST_Z)
        else:
            out.append(min(_MAX_ROBUST_Z, _Z_SCALE * score / (1.0 - score)))
    return out


def run_simulation(
    detector: str,
    params: dict[str, str],
    values: list[float],
    registry: object | None = None,
) -> tuple[list[float], list[float], int, int]:
    """Replay `values` through a throwaway `detector` instance.

    Args:
        detector: Detector name, as it would be written to entities.yaml.
        params: The params map exactly as the orchestrator would send it.
        values: Raw readings, oldest first.
        registry: DetectorRegistry used ONLY as the _create_detector factory, so
            the simulation constructs its detector through the same path as
            production. The instance it returns stays local. None builds a
            throwaway registry, which keeps the plan's 3-argument call shape
            usable from tests.

    Returns:
        (scores, robust_z, window, warmed_up_from_index):
          scores  — one per input value, 1:1 by index; 0.0 before warm-up.
          robust_z — same length as scores for rmad, [] for every other detector.
          window  — the effective warm-up gate (hst: window, rmad: min_samples).
          warmed_up_from_index — first scorable index; scores below it are 0.0
            by construction and must not be fed to the gate.

    Raises:
        ValueError: unknown detector name (propagated from _create_detector).
            The servicer turns this into ok=false, never into context.abort.
    """
    if registry is None:
        from argus_detector.registry import DetectorRegistry  # lazy import

        registry = DetectorRegistry()

    # LOCAL variable — never registry.register(), never registry._detectors.
    model = registry._create_detector(detector, params)  # noqa: SLF001

    scores = [float(model.score_one(value)) for value in values]

    window = int(getattr(model, "window", 0) or 0)
    warmed_up_from_index = min(window, len(values))

    # SimulateResponse declares "scores[i < idx] == 0.0" (proto/argus.proto),
    # and both consumers read it that way: ReplaySimulator refuses to gate the
    # prefix, and the panel greys it out on the chart.
    #
    # Today both detectors happen to honour it unaided — rmad returns the
    # structural 0.0 below min_samples, and river's HalfSpaceTrees returns a
    # literal 0 until it has learned window_size points (its `_first_window`
    # flag). "Happens to" is not a contract: that flag is an internal detail of
    # a pinned transitive dependency, and a detector added later need not have a
    # warm-up phase at all. Enforcing the prefix here is what makes the declared
    # shape true by construction rather than by coincidence — and it runs before
    # _robust_z, so the inverted z series cannot disagree with the scores.
    for i in range(warmed_up_from_index):
        scores[i] = 0.0

    return scores, _robust_z(detector, scores), window, warmed_up_from_index
