"""
River HalfSpaceTrees wrapper with online min-max normalization.

LEGACY / UNCALIBRATED — kept verbatim as the opt-in rollback path (D-F), NOT
as a peer of rmad_detector.RmadDetector. Nothing below is fixed, and nothing
below is going to be fixed; selecting it logs a warning once per entity in
servicer.py. Its checkpoints live under /data/models/<slug>/hst/, disjoint
from <slug>/rmad/, so switching back restores the old model with its n_seen
intact.

KNOWN DEFECTS (measured on the operator's live HA instance, 2026-09-03 —
docs/FIX-PLAN.md section 1):
  F4  HalfSpaceTrees.score_one returns 1 - mass/max_mass, i.e. RARITY, not
      deviation. On a quantized series the rare-but-perfectly-normal level
      101 W scores 0.997 while the MODAL level 107 W scores 0.560. The
      detector's opinion is inverted with respect to deviation, so no
      threshold placed anywhere separates anomalies from normal readings.
  F5  score_one below calls _normalizer.learn_one BEFORE transform_one, and
      river.preprocessing.MinMaxScaler keeps UNBOUNDED running min/max. After
      one 13.01 reading on a series whose p50 was 0.54, the whole normal band
      collapses to ~0.3% of [0,1] (0.54 -> 0.0032) and never recovers.
  F6  The resulting score distribution is per-sensor and uncalibrated
      (measured 24 h minima: memory 0.830, processor 0.562, load 0.480), so a
      single global high_threshold cannot be correct on all of them at once.
      Thresholds for this detector must be tuned by hand, per entity.
  F7  Nothing observes that distribution: is_warmed_up flips at
      n_seen >= window and that is the end of it.

  - Online min-max normalization (D-08, river.preprocessing.MinMaxScaler)
  - River HalfSpaceTrees scoring (window=250, n_trees=25 — these are this
    module's own defaults; the shipped defaults are rmad's, see D-B)
  - is_warmed_up tracks when n_seen >= window_size (PITFALL 8 mitigation)
  - from_params(): overrides from string params map (CONF-02)

Thread safety: each EntityDetector instance is owned by a single (entity_id, detector)
key in DetectorRegistry; the registry Lock guards creation only. Once an instance
exists it is used from a single thread per the gRPC ThreadPoolExecutor + per-stream
entity model.
"""

from __future__ import annotations

from river import anomaly, preprocessing

# This module's own defaults (D-09). NOT the shipped defaults — see D-B.
_DEFAULT_WINDOW = 250
_DEFAULT_N_TREES = 25
_HEIGHT = 8
_SEED = 42


def _cast_int(params: dict[str, str], key: str, default: int) -> int:
    """Cast a string param to int, returning default if key absent or invalid."""
    raw = params.get(key)
    if raw is None:
        return default
    try:
        return int(raw)
    except (ValueError, TypeError):
        return default


class EntityDetector:
    """Per-entity streaming anomaly detector (River HalfSpaceTrees + MinMaxScaler).

    Usage::

        det = EntityDetector(window=250, n_trees=25)
        score = det.score_one(21.5)  # float in [0, 1] from HST
        if det.is_warmed_up:
            ...  # reliable scores
    """

    def __init__(self, window: int = _DEFAULT_WINDOW, n_trees: int = _DEFAULT_N_TREES) -> None:
        self._normalizer = preprocessing.MinMaxScaler()
        self._model = anomaly.HalfSpaceTrees(
            n_trees=n_trees,
            height=_HEIGHT,
            window_size=window,
            seed=_SEED,
        )
        self._n_seen: int = 0

    @classmethod
    def from_params(cls, params: dict[str, str]) -> "EntityDetector":
        """Create an EntityDetector from a string params map (CONF-02).

        Supported keys: "window", "n_trees" (both cast to int).
        Absent keys fall back to D-09 defaults.
        """
        window = _cast_int(params, "window", _DEFAULT_WINDOW)
        n_trees = _cast_int(params, "n_trees", _DEFAULT_N_TREES)
        return cls(window=window, n_trees=n_trees)

    def score_one(self, value: float) -> float:
        """Score a single sensor reading.

        Steps:
          1. Normalize via MinMaxScaler (learn + transform).
          2. Query HalfSpaceTrees score_one (pre-learn).
          3. Update HalfSpaceTrees with learn_one.
          4. Increment n_seen.

        Returns:
            Anomaly score in [0, 1] (higher = more anomalous).
        """
        x = {"value": value}
        self._normalizer.learn_one(x)
        x_norm = self._normalizer.transform_one(x)
        score: float = float(self._model.score_one(x_norm))
        self._model.learn_one(x_norm)
        self._n_seen += 1
        return score

    @property
    def is_warmed_up(self) -> bool:
        """True when at least window_size readings have been processed."""
        return self._n_seen >= self._model.window_size

    @property
    def window_ready(self) -> bool:
        """True when the model as it stands now is past its warm-up window.

        Same value as `is_warmed_up` here — this detector learns and counts in
        one step, so there is no insert to lag behind. The property exists so
        the query-shaped callers (registry.warmup_one) can ask every streaming
        detector the same question; on RmadDetector the two DO differ.
        """
        return self.is_warmed_up

    @property
    def n_seen(self) -> int:
        """Number of readings processed so far (PERSIST-01/Verdict.n_seen)."""
        return self._n_seen

    @property
    def window(self) -> int:
        """The window_size this instance is actually configured with

        (RESEARCH.md Pitfall 4 — the caller sees the real value, not a guess).
        """
        return self._model.window_size
