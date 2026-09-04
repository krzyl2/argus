"""
Tests for the Simulate RPC and its sandbox guarantees (WS6).

The three rules this module pins are not implementation details — each one is a
defect the simulator would otherwise reintroduce:

  1. F14: a "what if?" panel must not mutate the model that is scoring the
     house. ScoreBatch does exactly that (it resolves and cold-start-fits the
     live (entity_id, detector) model), which is why Simulate exists at all
     instead of reusing it.
  2. The gate must know where the scorable region starts. Before
     warmed_up_from_index the detector returns a structural 0.0, and feeding
     those zeros to a hysteresis gate manufactures a release edge that never
     happened on the sensor.
  3. Simulate must never abort. ScoreStream multiplexes every tracked entity
     onto one bidi call and servicer.py's blanket handler aborts it wholesale,
     so an aborting Simulate would let a typo in the detector name stop scoring
     for every entity in the house.
"""

from __future__ import annotations

import pytest
from google.protobuf import wrappers_pb2

from argus_detector.proto import argus_pb2
from argus_detector.registry import DetectorRegistry


# ---------------------------------------------------------------------------
# Fixtures / helpers (mirrors test_servicer.py's _FakeContext convention)
# ---------------------------------------------------------------------------


class _FakeContext:
    """Minimal grpc.ServicerContext stub for unit tests."""

    def __init__(self):
        self.aborted = False
        self.abort_code = None
        self.abort_details = None

    def abort(self, code, details):
        self.aborted = True
        self.abort_code = code
        self.abort_details = details

    def is_active(self):
        return not self.aborted


@pytest.fixture()
def servicer(tmp_path):
    from argus_detector.model_store import ModelStore
    from argus_detector.servicer import DetectorServicer

    registry = DetectorRegistry()
    store = ModelStore(root=tmp_path)
    return DetectorServicer(registry, store), registry, tmp_path


def _history(values: list[float], entity_id: str = "sensor.sim") -> list[argus_pb2.Point]:
    return [
        argus_pb2.Point(entity_id=entity_id, value=wrappers_pb2.DoubleValue(value=v))
        for v in values
    ]


def _flat_series(n: int) -> list[float]:
    """A benign quantized series: level 107 with +/-2 jitter, like the fridge idle band."""
    return [107.0 + (i % 5) - 2.0 for i in range(n)]


# ---------------------------------------------------------------------------
# 1. Sandbox (F14)
# ---------------------------------------------------------------------------


def test_simulate_does_not_register_model(servicer):
    """Simulate must leave the registry and /data/models untouched (F14).

    If the instance were registered, registry._streaming_keys() would pick it up
    and the checkpoint sweep would write a model directory for a sensor the
    operator only previewed — and, far worse, the next live reading would score
    against a window built from whatever lookback the panel happened to request.
    """
    svc, registry, root = servicer

    before = sorted(p.relative_to(root) for p in root.rglob("*"))

    request = argus_pb2.SimulateRequest(
        entity_id="sensor.sim",
        detector="rmad",
        history=_history(_flat_series(120)),
    )
    response = svc.Simulate(request, _FakeContext())

    assert response.ok is True
    assert registry._detectors == {}
    assert registry._streaming_keys() == []
    assert sorted(p.relative_to(root) for p in root.rglob("*")) == before


def test_simulate_leaves_a_live_model_unchanged(servicer):
    """A tracked entity already being scored must not move because of a preview."""
    svc, registry, _ = servicer

    for value in _flat_series(80):
        registry.score_one("sensor.sim", value, detector="rmad", params={})
    n_seen_before = registry.get_warmup_state("sensor.sim", "rmad")[1]

    svc.Simulate(
        argus_pb2.SimulateRequest(
            entity_id="sensor.sim",
            detector="rmad",
            history=_history(_flat_series(500)),
        ),
        _FakeContext(),
    )

    assert registry.get_warmup_state("sensor.sim", "rmad")[1] == n_seen_before


# ---------------------------------------------------------------------------
# 2. Alignment and the scorable region
# ---------------------------------------------------------------------------


def test_simulate_scores_align_with_history(servicer):
    """scores is 1:1 with history, and warm-up is reported as the gate that applies.

    The orchestrator indexes scores against its own timestamp array to compute
    on-time percent, so any length mismatch silently shifts every episode in the
    chart. warmed_up_from_index is min_samples for rmad (D-M: the rolling
    median/MAD IS the calibration) and window for hst.
    """
    svc, _, _ = servicer
    values = _flat_series(200)

    rmad = svc.Simulate(
        argus_pb2.SimulateRequest(
            entity_id="sensor.sim", detector="rmad", history=_history(values)
        ),
        _FakeContext(),
    )
    assert rmad.ok is True
    assert len(rmad.scores) == len(values)
    assert rmad.window == 60
    assert rmad.warmed_up_from_index == 60
    assert all(s == 0.0 for s in rmad.scores[:60])

    hst = svc.Simulate(
        argus_pb2.SimulateRequest(
            entity_id="sensor.sim", detector="hst", history=_history(_flat_series(300))
        ),
        _FakeContext(),
    )
    assert hst.ok is True
    assert hst.window == 250
    assert hst.warmed_up_from_index == 250


def test_simulate_reports_robust_z_for_rmad_only(servicer):
    """robust_z is populated for rmad and EMPTY for hst — hst scores rarity, not deviation.

    Deriving a "z" from an hst score would put a deviation label on a rarity
    statistic, which is precisely the confusion F4 documents.
    """
    svc, _, _ = servicer
    values = _flat_series(120)

    rmad = svc.Simulate(
        argus_pb2.SimulateRequest(entity_id="e", detector="rmad", history=_history(values)),
        _FakeContext(),
    )
    hst = svc.Simulate(
        argus_pb2.SimulateRequest(entity_id="e", detector="hst", history=_history(values)),
        _FakeContext(),
    )

    assert len(rmad.robust_z) == len(rmad.scores)
    assert list(hst.robust_z) == []


# ---------------------------------------------------------------------------
# 3. Fail-soft (never abort)
# ---------------------------------------------------------------------------


def test_simulate_unknown_detector_returns_error_not_abort(servicer):
    """An unknown name is an error message, never context.abort.

    _create_detector raises ValueError, and servicer.py:104-107 turns any
    exception on the streaming path into abort(INTERNAL), which kills the whole
    multiplexed stream. Simulate has to swallow the same failure locally.
    """
    svc, _, _ = servicer
    ctx = _FakeContext()

    response = svc.Simulate(
        argus_pb2.SimulateRequest(
            entity_id="sensor.sim", detector="does_not_exist", history=_history([1.0, 2.0])
        ),
        ctx,
    )

    assert ctx.aborted is False
    assert response.ok is False
    assert response.error != ""


def test_simulate_empty_history_returns_error_not_abort(servicer):
    """Zero points is a client mistake, not a reason to abort the call."""
    svc, _, _ = servicer
    ctx = _FakeContext()

    response = svc.Simulate(
        argus_pb2.SimulateRequest(entity_id="sensor.sim", detector="rmad"), ctx
    )

    assert ctx.aborted is False
    assert response.ok is False
    assert response.error != ""


# ---------------------------------------------------------------------------
# run_simulation directly (the plan's 3-argument shape stays callable)
# ---------------------------------------------------------------------------


def test_run_simulation_without_registry_builds_its_own_factory():
    from argus_detector.simulate import run_simulation

    scores, robust_z, window, warmed_from = run_simulation("rmad", {}, _flat_series(100))

    assert len(scores) == 100
    assert len(robust_z) == 100
    assert (window, warmed_from) == (60, 60)


def test_run_simulation_honours_params():
    """min_samples from params moves the scorable region — the panel's whole point."""
    from argus_detector.simulate import run_simulation

    _, _, window, warmed_from = run_simulation(
        "rmad", {"window": "240", "min_samples": "30"}, _flat_series(100)
    )

    assert (window, warmed_from) == (30, 30)


def test_run_simulation_zeroes_the_prefix_whatever_the_detector_returns():
    """The zero prefix is a CONTRACT, not an observation about today's detectors.

    proto/argus.proto declares "scores[i < idx] == 0.0" and both consumers act
    on it: ReplaySimulator refuses to gate the prefix (feeding it manufactures a
    release edge the sensor never produced) and the panel greys it on the chart.
    Right now rmad and hst both honour it unaided — hst only because river's
    HalfSpaceTrees returns a literal 0 until it has learned window_size points,
    which is an internal detail of a pinned transitive dependency. A detector
    that scores from its first reading must not be able to break the declared
    shape, so the stub here does exactly that.
    """
    from argus_detector.simulate import run_simulation

    class _AlwaysHot:
        window = 10

        def score_one(self, value: float) -> float:
            return 1.0

    class _StubRegistry:
        def _create_detector(self, detector, params):
            return _AlwaysHot()

    scores, _, window, warmed_from = run_simulation(
        "always_hot", {}, [1.0] * 30, _StubRegistry()
    )

    assert (window, warmed_from) == (10, 10)
    assert scores[:10] == [0.0] * 10
    # And only the prefix: the scorable region is left exactly as scored.
    assert scores[10:] == [1.0] * 20


def test_run_simulation_unknown_detector_raises():
    """The ValueError must still surface — the servicer, not this function, softens it."""
    from argus_detector.simulate import run_simulation

    with pytest.raises(ValueError):
        run_simulation("nope", {}, [1.0, 2.0])
