"""
Tests for PairwiseDeltaDetector (2-member peer_divergence pairwise-delta path, GRP-11).

Verifies:
- compute_delta() is elementwise a-b, length-preserving
- fit() + score_batch() on the delta: normal noise scores low, an injected
  step-drift in one member (which breaks the pair's learned relationship —
  the exact scenario the operator's 2-sensor use cases, e.g. tire pressures
  or water pressure+temperature pairs, depend on catching) scores high
- is_anomaly() delegates the threshold decision to the wrapped PyODDetector
- from_params() honors threshold/contamination overrides identically to
  PyODDetector, falling back to MAD defaults when keys are absent/invalid
- servicer-level 2-member peer_divergence routing (FitGroup + ScoreGroupBatch)
"""

from __future__ import annotations

import pytest

from argus_detector.group.pairwise_delta import PairwiseDeltaDetector
from argus_detector.model_store import ModelStore
from argus_detector.proto import argus_pb2
from argus_detector.registry import DetectorRegistry
from argus_detector.servicer import DetectorServicer


class TestComputeDelta:
    def test_elementwise_subtraction(self):
        """compute_delta(a, b) == a[i] - b[i] for every index."""
        series_a = [10.0, 11.0, 12.0]
        series_b = [9.0, 9.5, 10.0]
        delta = PairwiseDeltaDetector.compute_delta(series_a, series_b)
        assert delta == pytest.approx([1.0, 1.5, 2.0])

    def test_delta_length_matches_input(self):
        series_a = [float(i) for i in range(20)]
        series_b = [float(i) - 1.0 for i in range(20)]
        delta = PairwiseDeltaDetector.compute_delta(series_a, series_b)
        assert len(delta) == 20

    def test_delta_returns_floats(self):
        delta = PairwiseDeltaDetector.compute_delta([1, 2, 3], [1, 1, 1])
        assert all(isinstance(v, float) for v in delta)


class TestFitScore:
    def test_normal_delta_scores_below_drift_delta(self):
        """A fitted PairwiseDeltaDetector scores an injected step-drift delta
        above threshold and normal-noise delta below — this is the WHY: the
        delta captures a broken pair-relationship (e.g. one tire slowly
        losing pressure relative to the other, or a water pressure+temperature
        pair drifting apart), which the operator's 2-sensor use cases depend
        on being caught, not just "some anomaly somewhere"."""
        # Two members tracking closely (delta hovers near 0 with small noise).
        member_a = [10.0, 10.1, 9.9, 10.05, 9.95, 10.02, 9.98, 10.1, 9.9, 10.0]
        member_b = [10.0, 10.05, 9.95, 10.0, 10.0, 9.98, 10.02, 10.05, 9.95, 10.0]
        delta = PairwiseDeltaDetector.compute_delta(member_a, member_b)

        det = PairwiseDeltaDetector()
        det.fit(delta)
        normal_scores = det.score_batch(delta)

        # Injected step-drift: member_a jumps far away from member_b, breaking
        # the previously-stable pair relationship.
        drift_delta = PairwiseDeltaDetector.compute_delta([25.0], [10.0])
        drift_scores = det.score_batch(drift_delta)

        assert max(normal_scores) < drift_scores[0]

    def test_score_batch_returns_one_score_per_delta_value(self):
        delta = [0.1, -0.05, 0.08, -0.02, 0.03, 0.0, 0.01, -0.04, 0.02, 0.0]
        det = PairwiseDeltaDetector()
        det.fit(delta)
        scores = det.score_batch(delta)
        assert len(scores) == len(delta)

    def test_score_batch_before_fit_raises(self):
        det = PairwiseDeltaDetector()
        with pytest.raises(ValueError, match="fit\\(\\) must be called before score_batch"):
            det.score_batch([1.0, 2.0])

    def test_is_fitted_transitions(self):
        det = PairwiseDeltaDetector()
        assert det.is_fitted is False
        det.fit([0.1, -0.1, 0.2, -0.2, 0.0])
        assert det.is_fitted is True


class TestIsAnomaly:
    def test_is_anomaly_delegates_to_wrapped_pyod_detector(self):
        """is_anomaly(score) matches the threshold decision the underlying
        PyODDetector would make for the same score."""
        delta = [0.1, -0.05, 0.08, -0.02, 0.03, 0.0, 0.01, -0.04, 0.02, 0.0]
        det = PairwiseDeltaDetector()
        det.fit(delta)
        scores = det.score_batch(delta)

        # A score far beyond the fitted distribution must be flagged anomalous.
        drift_delta = PairwiseDeltaDetector.compute_delta([25.0], [0.0])
        drift_score = det.score_batch(drift_delta)[0]
        assert det.is_anomaly(drift_score) is True

        # A score within the normal noise band must not be flagged.
        assert det.is_anomaly(min(scores)) is False


class TestFromParams:
    def test_from_params_empty_dict_uses_defaults(self):
        det = PairwiseDeltaDetector.from_params({})
        delta = [0.1, -0.1, 0.2, -0.2, 0.0]
        det.fit(delta)
        scores = det.score_batch(delta)
        assert len(scores) == 5

    def test_from_params_threshold_override(self):
        """from_params({'threshold': ...}) does not raise and still scores."""
        det = PairwiseDeltaDetector.from_params({"threshold": "4.0"})
        delta = [0.1, -0.1, 0.2, -0.2, 0.0]
        det.fit(delta)
        scores = det.score_batch(delta)
        assert len(scores) == 5

    def test_from_params_invalid_value_falls_back_to_default(self):
        """Malformed param string must not raise — falls back to MAD default."""
        det = PairwiseDeltaDetector.from_params({"threshold": "not-a-number"})
        delta = [0.1, -0.1, 0.2, -0.2, 0.0]
        det.fit(delta)
        scores = det.score_batch(delta)
        assert len(scores) == 5


# ---------------------------------------------------------------------------
# Servicer-level 2-member peer_divergence routing (GRP-11)
# ---------------------------------------------------------------------------

class _FakeContext:
    """Minimal grpc.ServicerContext stub for unit tests (mirrors test_servicer.py)."""

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


def _make_series(member_id: str, values: list[float]) -> "argus_pb2.Series":
    return argus_pb2.Series(member_id=member_id, values=values)


# Two members tracking closely, then a step-drift at the last timestamp that
# breaks the pair's relationship (e.g. one tire pressure sensor drifting
# relative to the other).
_MEMBER_A = [10.0, 10.1, 9.9, 10.05, 9.95, 10.02, 9.98, 10.1, 9.9, 25.0]
_MEMBER_B = [10.0, 10.05, 9.95, 10.0, 10.0, 9.98, 10.02, 10.05, 9.95, 10.0]
_TWO_MEMBER_SERIES = [
    _make_series("tire_fl", _MEMBER_A),
    _make_series("tire_fr", _MEMBER_B),
]


class TestServicerPairwiseDeltaRouting:
    """2-member peer_divergence routes to PairwiseDeltaDetector, not the
    classic median/MAD PeerDivergenceDetector (GRP-11)."""

    @pytest.fixture()
    def servicer(self, tmp_path):
        registry = DetectorRegistry()
        store = ModelStore(root=tmp_path)
        return DetectorServicer(registry, store), registry, store

    def test_score_before_fit_aborts_call_fit_group_first(self, servicer):
        svc, _, _ = servicer
        request = argus_pb2.GroupScoreRequest(
            group_id="tires", detector="peer_divergence", series=_TWO_MEMBER_SERIES
        )
        ctx = _FakeContext()
        result = svc.ScoreGroupBatch(request, ctx)
        assert ctx.aborted
        import grpc
        assert ctx.abort_code == grpc.StatusCode.INVALID_ARGUMENT
        assert "call FitGroup first" in ctx.abort_details
        assert result is None

    def test_fit_then_score_returns_group_verdict_with_empty_per_member(self, servicer):
        svc, registry, _ = servicer
        fit_request = argus_pb2.FitGroupRequest(
            group_id="tires", detector="peer_divergence", series=_TWO_MEMBER_SERIES
        )
        fit_ctx = _FakeContext()
        fit_response = svc.FitGroup(fit_request, fit_ctx)
        assert fit_response.ok is True
        assert registry.has_model("group_tires", "peer_divergence")

        score_request = argus_pb2.GroupScoreRequest(
            group_id="tires", detector="peer_divergence", series=_TWO_MEMBER_SERIES
        )
        score_ctx = _FakeContext()
        response = svc.ScoreGroupBatch(score_request, score_ctx)
        assert not score_ctx.aborted
        assert response.ok is True
        assert response.HasField("group_verdict")
        assert response.group_verdict.entity_id == "group_tires"
        assert response.group_verdict.detector == "peer_divergence"
        assert response.group_verdict.is_anomaly is True  # injected step-drift
        assert len(response.per_member) == 0
        assert len(response.contributions) == 0

    def test_fit_group_persists_via_save_pyod(self, servicer):
        """FitGroup for a 2-member peer_divergence group persists a model.joblib
        under the group_slug/peer_divergence key (via save_pyod, not
        save_group_bundle — no scaler for a single derived feature)."""
        svc, _, store = servicer
        fit_request = argus_pb2.FitGroupRequest(
            group_id="tires2", detector="peer_divergence", series=_TWO_MEMBER_SERIES
        )
        fit_ctx = _FakeContext()
        fit_response = svc.FitGroup(fit_request, fit_ctx)
        assert fit_response.ok is True

        loaded = store.load_pyod("group_tires2", "peer_divergence")
        assert isinstance(loaded, PairwiseDeltaDetector)
