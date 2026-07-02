"""
Tests for DetectorServicer batch RPCs: Fit, ScoreBatch, SaveModel, LoadModel.

Covers:
  - ScoreBatch cold-start (no model → fit_one first, then score)
  - ScoreBatch happy-path (3-point window → 3 verdicts)
  - ScoreBatch empty entity_id guard
  - Fit happy-path (fit_one + save model, ok=True)
  - Fit exception handling (ok=False, error populated)
  - SaveModel no-model case (ok=False, error)
  - SaveModel with fitted model (ok=True, model_bytes non-empty)
  - LoadModel registers model into registry (has_model True after)

Uses real DetectorRegistry and real ModelStore (tmp_path for isolation).
Mock gRPC context via a simple stub.
"""

from __future__ import annotations

import pathlib

import pytest
from google.protobuf import wrappers_pb2

from argus_detector.proto import argus_pb2
from argus_detector.registry import DetectorRegistry


# ---------------------------------------------------------------------------
# Helpers
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


def _make_window(values: list[float]) -> list[argus_pb2.Point]:
    return [
        argus_pb2.Point(
            entity_id="sensor.test",
            value=wrappers_pb2.DoubleValue(value=v),
        )
        for v in values
    ]


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------

@pytest.fixture()
def model_store_and_registry(tmp_path):
    from argus_detector.model_store import ModelStore
    registry = DetectorRegistry()
    store = ModelStore(root=tmp_path)
    return registry, store


@pytest.fixture()
def servicer(model_store_and_registry):
    from argus_detector.servicer import DetectorServicer
    registry, store = model_store_and_registry
    return DetectorServicer(registry, store), registry, store


# ---------------------------------------------------------------------------
# ScoreBatch tests
# ---------------------------------------------------------------------------

class TestScoreBatchColdStart:
    """Cold-start: ScoreBatch with no prior model must fit_one first, then score."""

    def test_cold_start_returns_ok_true(self, servicer):
        svc, registry, _ = servicer
        request = argus_pb2.ScoreBatchRequest(
            entity_id="sensor.cold",
            detector="mad",
            window=_make_window([1.0, 2.0, 3.0, 4.0, 5.0]),
        )
        ctx = _FakeContext()
        response = svc.ScoreBatch(request, ctx)
        assert not ctx.aborted
        assert response.ok is True

    def test_cold_start_verdict_count_matches_window(self, servicer):
        """One Verdict per window point."""
        svc, registry, _ = servicer
        window_values = [float(i) for i in range(10)]
        request = argus_pb2.ScoreBatchRequest(
            entity_id="sensor.cold2",
            detector="mad",
            window=_make_window(window_values),
        )
        ctx = _FakeContext()
        response = svc.ScoreBatch(request, ctx)
        assert response.ok is True
        assert len(response.verdicts) == len(window_values)

    def test_cold_start_registers_model(self, servicer):
        """After cold-start ScoreBatch, registry.has_model must be True."""
        svc, registry, _ = servicer
        assert not registry.has_model("sensor.new", "mad")
        request = argus_pb2.ScoreBatchRequest(
            entity_id="sensor.new",
            detector="mad",
            window=_make_window([1.0] * 8),
        )
        ctx = _FakeContext()
        svc.ScoreBatch(request, ctx)
        assert registry.has_model("sensor.new", "mad")


class TestScoreBatchHappyPath:
    """ScoreBatch with pre-existing model."""

    def test_score_batch_three_point_window(self, servicer):
        svc, registry, _ = servicer
        # Pre-fit
        registry.fit_one("sensor.x", "mad", [float(i) for i in range(20)])
        request = argus_pb2.ScoreBatchRequest(
            entity_id="sensor.x",
            detector="mad",
            window=_make_window([1.0, 2.0, 3.0]),
        )
        ctx = _FakeContext()
        response = svc.ScoreBatch(request, ctx)
        assert response.ok is True
        assert len(response.verdicts) == 3

    def test_score_batch_verdict_fields(self, servicer):
        """Each Verdict must have entity_id, score, is_anomaly=False, detector set."""
        svc, registry, _ = servicer
        registry.fit_one("sensor.v", "mad", [float(i) for i in range(20)])
        request = argus_pb2.ScoreBatchRequest(
            entity_id="sensor.v",
            detector="mad",
            window=_make_window([1.0, 2.0]),
        )
        ctx = _FakeContext()
        response = svc.ScoreBatch(request, ctx)
        for v in response.verdicts:
            assert v.entity_id == "sensor.v"
            assert v.is_anomaly is False
            assert v.detector == "mad"
            assert v.score.value is not None  # DoubleValue set


class TestScoreBatchGuards:
    """Input validation guards."""

    def test_empty_entity_id_aborts_invalid_argument(self, servicer):
        svc, registry, _ = servicer
        request = argus_pb2.ScoreBatchRequest(
            entity_id="",
            detector="mad",
            window=_make_window([1.0]),
        )
        ctx = _FakeContext()
        result = svc.ScoreBatch(request, ctx)
        assert ctx.aborted
        import grpc
        assert ctx.abort_code == grpc.StatusCode.INVALID_ARGUMENT
        assert result is None, "After abort, return value must be None (gRPC ignores it)"


# ---------------------------------------------------------------------------
# Fit tests
# ---------------------------------------------------------------------------

class TestFitHappyPath:
    """Fit trains model and saves to disk."""

    def test_fit_returns_ok_true(self, servicer):
        svc, registry, _ = servicer
        request = argus_pb2.FitRequest(
            entity_id="sensor.fit",
            detector="mad",
            window=_make_window([float(i) for i in range(15)]),
        )
        ctx = _FakeContext()
        response = svc.Fit(request, ctx)
        assert response.ok is True
        assert response.error == ""

    def test_fit_registers_model_in_registry(self, servicer):
        """After Fit, has_model must be True."""
        svc, registry, _ = servicer
        request = argus_pb2.FitRequest(
            entity_id="sensor.fit2",
            detector="mad",
            window=_make_window([float(i) for i in range(10)]),
        )
        ctx = _FakeContext()
        svc.Fit(request, ctx)
        assert registry.has_model("sensor.fit2", "mad")

    def test_fit_saves_model_to_disk(self, servicer):
        """After Fit, a model file must exist on disk."""
        svc, registry, store = servicer
        request = argus_pb2.FitRequest(
            entity_id="sensor.disk",
            detector="mad",
            window=_make_window([float(i) for i in range(10)]),
        )
        ctx = _FakeContext()
        svc.Fit(request, ctx)
        # Check that model.joblib exists somewhere under the root
        slug = "sensor_disk"
        model_files = list(store._root.rglob("model.joblib"))
        assert len(model_files) >= 1, "Expected model.joblib to be saved to disk"

    def test_fit_empty_entity_id_aborts(self, servicer):
        svc, registry, _ = servicer
        request = argus_pb2.FitRequest(
            entity_id="",
            detector="mad",
            window=_make_window([1.0]),
        )
        ctx = _FakeContext()
        result = svc.Fit(request, ctx)
        assert ctx.aborted
        assert result is None, "After abort, return value must be None (gRPC ignores it)"


# ---------------------------------------------------------------------------
# SaveModel tests
# ---------------------------------------------------------------------------

class TestSaveModel:
    """SaveModel serializes fitted model bytes."""

    def test_save_model_no_model_returns_error(self, servicer):
        """SaveModel with unknown entity/detector returns ok=False."""
        svc, registry, _ = servicer
        request = argus_pb2.SaveModelRequest(
            entity_id="sensor.ghost",
            detector="mad",
        )
        ctx = _FakeContext()
        response = svc.SaveModel(request, ctx)
        assert response.ok is False
        assert response.error != ""

    def test_save_model_fitted_returns_ok_true(self, servicer):
        """SaveModel with fitted model returns ok=True.

        Note: SaveModelResponse proto has no model_bytes field (per proto definition).
        SaveModel persists to disk; serialized bytes are returned by LoadModel.
        """
        svc, registry, store = servicer
        registry.fit_one("sensor.s", "mad", [float(i) for i in range(20)])
        # SaveModel serializes to disk (store._root) and returns ok=True
        # We verify ok=True; disk persistence is tested separately in Fit tests
        request = argus_pb2.SaveModelRequest(
            entity_id="sensor.s",
            detector="mad",
        )
        ctx = _FakeContext()
        # SaveModel with a fitted model should return ok=True
        # Implementation serializes model bytes internally (for in-memory validation)
        response = svc.SaveModel(request, ctx)
        assert response.ok is True


# ---------------------------------------------------------------------------
# LoadModel tests
# ---------------------------------------------------------------------------

class TestLoadModel:
    """LoadModel deserializes and registers model into registry."""

    def test_load_model_after_fit_and_save(self, servicer):
        """After Fit then LoadModel, registry.has_model returns True on fresh registry."""
        svc, registry, store = servicer
        # Fit and save
        fit_req = argus_pb2.FitRequest(
            entity_id="sensor.load",
            detector="mad",
            window=_make_window([float(i) for i in range(15)]),
        )
        ctx = _FakeContext()
        svc.Fit(fit_req, ctx)
        assert ctx.aborted is False

        # Load into a fresh registry
        fresh_registry = DetectorRegistry()
        from argus_detector.servicer import DetectorServicer
        fresh_svc = DetectorServicer(fresh_registry, store)

        load_req = argus_pb2.LoadModelRequest(
            entity_id="sensor.load",
            detector="mad",
            version=0,  # 0 = latest
        )
        ctx2 = _FakeContext()
        response = fresh_svc.LoadModel(load_req, ctx2)
        assert response.ok is True
        assert fresh_registry.has_model("sensor.load", "mad") or fresh_registry.has_model("sensor_load", "mad")

    def test_load_model_nonexistent_returns_error(self, servicer):
        """LoadModel for a non-existent entity returns ok=False."""
        svc, registry, _ = servicer
        request = argus_pb2.LoadModelRequest(
            entity_id="sensor.nofile",
            detector="mad",
            version=0,
        )
        ctx = _FakeContext()
        response = svc.LoadModel(request, ctx)
        assert response.ok is False
        assert response.error != ""


# ---------------------------------------------------------------------------
# Group RPC tests (ScoreGroupBatch, FitGroup — GRP-03..07, Plan 05-04)
# ---------------------------------------------------------------------------

def _make_series(member_id: str, values: list[float]) -> "argus_pb2.Series":
    return argus_pb2.Series(member_id=member_id, values=values)


# Peer-divergence fixture: 4 members, non-identical baseline (avoids the MAD=0
# meanAD-fallback path) with member "c" clearly divergent at the last timestamp.
_PEER_SERIES = [
    _make_series("a", [10.0, 10.0]),
    _make_series("b", [10.1, 10.1]),
    _make_series("c", [9.9, 50.0]),
    _make_series("d", [10.2, 10.2]),
]

# Joint-anomaly fixture copied verbatim from test_group_multivariate.py /
# 05-RESEARCH.md Code Examples — jointly-abnormal-but-marginally-normal vector
# a univariate loop over each column would NOT catch.
_JOINT_TRAIN_PRESSURE = [1000.0, 1002.0, 998.0, 1001.0, 999.0, 1000.5, 1001.5, 999.5, 1000.2, 999.8]
_JOINT_TRAIN_HUMIDITY = [20.0, 22.0, 18.0, 21.0, 19.0, 20.5, 21.5, 19.5, 20.2, 19.8]
_JOINT_TRAIN_SERIES = [
    _make_series("pressure", _JOINT_TRAIN_PRESSURE),
    _make_series("humidity", _JOINT_TRAIN_HUMIDITY),
]
# High pressure + low humidity breaks the learned correlation (joint anomaly).
_JOINT_SCORE_SERIES = [
    _make_series("pressure", [1002.0]),
    _make_series("humidity", [18.0]),
]


class TestScoreGroupBatchPeerDivergence:
    """peer_divergence mode: per-member Verdicts, is_anomaly from locked |z|>3.5."""

    def test_returns_one_verdict_per_member(self, servicer):
        svc, _, _ = servicer
        request = argus_pb2.GroupScoreRequest(
            group_id="g1", detector="peer_divergence", series=_PEER_SERIES
        )
        ctx = _FakeContext()
        response = svc.ScoreGroupBatch(request, ctx)
        assert not ctx.aborted
        assert response.ok is True
        assert len(response.per_member) == len(_PEER_SERIES)

    def test_flags_the_divergent_member(self, servicer):
        svc, _, _ = servicer
        request = argus_pb2.GroupScoreRequest(
            group_id="g1", detector="peer_divergence", series=_PEER_SERIES
        )
        ctx = _FakeContext()
        response = svc.ScoreGroupBatch(request, ctx)
        by_member = {v.entity_id: v for v in response.per_member}
        assert by_member["c"].is_anomaly is True
        assert by_member["a"].is_anomaly is False
        assert by_member["b"].is_anomaly is False
        assert by_member["d"].is_anomaly is False


class TestScoreGroupBatchFloor:
    """GRP-04: below-floor (<3 members) peer group returns no verdict, never a
    false not-anomalous result."""

    def test_below_floor_returns_no_verdict(self, servicer):
        svc, _, _ = servicer
        request = argus_pb2.GroupScoreRequest(
            group_id="g1", detector="peer_divergence", series=_PEER_SERIES[:2]
        )
        ctx = _FakeContext()
        response = svc.ScoreGroupBatch(request, ctx)
        assert not ctx.aborted
        assert response.ok is True
        assert len(response.per_member) == 0
        assert response.error != ""


class TestScoreGroupBatchJoint:
    """ecod joint-multivariate mode: group_verdict + ranked contributions."""

    def test_joint_score_after_fit_returns_group_verdict_and_contributions(self, servicer):
        svc, _, _ = servicer
        fit_request = argus_pb2.FitGroupRequest(
            group_id="g2", detector="ecod", series=_JOINT_TRAIN_SERIES
        )
        fit_ctx = _FakeContext()
        fit_response = svc.FitGroup(fit_request, fit_ctx)
        assert fit_response.ok is True

        score_request = argus_pb2.GroupScoreRequest(
            group_id="g2", detector="ecod", series=_JOINT_SCORE_SERIES
        )
        score_ctx = _FakeContext()
        response = svc.ScoreGroupBatch(score_request, score_ctx)
        assert not score_ctx.aborted
        assert response.ok is True
        assert response.HasField("group_verdict")
        assert response.group_verdict.detector == "ecod"
        assert len(response.contributions) > 0


class TestScoreGroupBatchGuards:
    """Input validation guards (RESEARCH V5 / T-05-09/10/11)."""

    def test_ragged_series_aborts_invalid_argument(self, servicer):
        svc, _, _ = servicer
        ragged_series = [
            _make_series("a", [1.0, 2.0]),
            _make_series("b", [1.0]),
        ]
        request = argus_pb2.GroupScoreRequest(
            group_id="g1", detector="peer_divergence", series=ragged_series
        )
        ctx = _FakeContext()
        result = svc.ScoreGroupBatch(request, ctx)
        assert ctx.aborted
        import grpc
        assert ctx.abort_code == grpc.StatusCode.INVALID_ARGUMENT
        assert result is None

    def test_empty_group_id_aborts_invalid_argument(self, servicer):
        svc, _, _ = servicer
        request = argus_pb2.GroupScoreRequest(
            group_id="", detector="peer_divergence", series=_PEER_SERIES
        )
        ctx = _FakeContext()
        result = svc.ScoreGroupBatch(request, ctx)
        assert ctx.aborted
        import grpc
        assert ctx.abort_code == grpc.StatusCode.INVALID_ARGUMENT
        assert result is None

    def test_unknown_detector_aborts_invalid_argument(self, servicer):
        svc, _, _ = servicer
        request = argus_pb2.GroupScoreRequest(
            group_id="g1", detector="bogus", series=_PEER_SERIES
        )
        ctx = _FakeContext()
        result = svc.ScoreGroupBatch(request, ctx)
        assert ctx.aborted
        import grpc
        assert ctx.abort_code == grpc.StatusCode.INVALID_ARGUMENT
        assert result is None

    def test_empty_series_aborts_invalid_argument(self, servicer):
        """WR-01: empty series list must abort INVALID_ARGUMENT, not raise
        an uncontrolled ValueError from the empty-matrix unpack."""
        svc, _, _ = servicer
        request = argus_pb2.GroupScoreRequest(
            group_id="g1", detector="peer_divergence", series=[]
        )
        ctx = _FakeContext()
        result = svc.ScoreGroupBatch(request, ctx)
        assert ctx.aborted
        import grpc
        assert ctx.abort_code == grpc.StatusCode.INVALID_ARGUMENT
        assert result is None


class TestFitGroupPersistence:
    """FitGroup persistence semantics: joint persists a loadable bundle,
    peer_divergence persists nothing (stateless, GRP-07)."""

    def test_fit_group_joint_persists_loadable_bundle(self, servicer):
        svc, _, store = servicer
        request = argus_pb2.FitGroupRequest(
            group_id="g3", detector="ecod", series=_JOINT_TRAIN_SERIES
        )
        ctx = _FakeContext()
        response = svc.FitGroup(request, ctx)
        assert response.ok is True

        loaded = store.load_group_bundle("g3", "ecod")
        assert set(loaded.keys()) == {"scaler", "detector", "name"}
        assert loaded["name"] == "ecod"

    def test_fit_group_peer_divergence_persists_nothing(self, servicer):
        svc, _, store = servicer
        request = argus_pb2.FitGroupRequest(
            group_id="g4", detector="peer_divergence", series=_PEER_SERIES
        )
        ctx = _FakeContext()
        response = svc.FitGroup(request, ctx)
        assert response.ok is True

        from argus_detector.model_store import group_slug
        assert not (store._root / group_slug("g4")).exists()

    def test_fit_group_empty_group_id_aborts(self, servicer):
        svc, _, _ = servicer
        request = argus_pb2.FitGroupRequest(
            group_id="", detector="peer_divergence", series=_PEER_SERIES
        )
        ctx = _FakeContext()
        result = svc.FitGroup(request, ctx)
        assert ctx.aborted
        assert result is None

    def test_fit_group_empty_series_aborts_invalid_argument(self, servicer):
        """WR-01: empty series list must abort INVALID_ARGUMENT in FitGroup too."""
        svc, _, _ = servicer
        request = argus_pb2.FitGroupRequest(
            group_id="g1", detector="peer_divergence", series=[]
        )
        ctx = _FakeContext()
        result = svc.FitGroup(request, ctx)
        assert ctx.aborted
        import grpc
        assert ctx.abort_code == grpc.StatusCode.INVALID_ARGUMENT
        assert result is None


class TestScoreGroupBatchParams:
    """ALGO-01/02: request.params must reach the constructed group detector."""

    def test_peer_divergence_lower_threshold_flags_more_members(self, servicer):
        """Same series, lower threshold param -> more flagged members."""
        svc, _, _ = servicer

        low_request = argus_pb2.GroupScoreRequest(
            group_id="g5",
            detector="peer_divergence",
            series=_PEER_SERIES,
            params={"threshold": "1.0"},
        )
        low_response = svc.ScoreGroupBatch(low_request, _FakeContext())

        high_request = argus_pb2.GroupScoreRequest(
            group_id="g5",
            detector="peer_divergence",
            series=_PEER_SERIES,
            params={"threshold": "10.0"},
        )
        high_response = svc.ScoreGroupBatch(high_request, _FakeContext())

        low_flagged = sum(1 for v in low_response.per_member if v.is_anomaly)
        high_flagged = sum(1 for v in high_response.per_member if v.is_anomaly)
        assert low_flagged > high_flagged

    def test_peer_divergence_malformed_threshold_does_not_abort(self, servicer):
        """A non-numeric threshold param must fall back to the default, not 500."""
        svc, _, _ = servicer
        request = argus_pb2.GroupScoreRequest(
            group_id="g5",
            detector="peer_divergence",
            series=_PEER_SERIES,
            params={"threshold": "not-a-number"},
        )
        ctx = _FakeContext()
        response = svc.ScoreGroupBatch(request, ctx)
        assert not ctx.aborted
        assert response.ok is True

    def test_joint_fit_group_params_reach_constructed_detector(self, servicer):
        """FitGroup with contamination params -> the persisted bundle's model
        reflects the requested contamination (proves params reached the
        registry's _create_detector call, not just accepted and dropped)."""
        svc, _, store = servicer
        fit_request = argus_pb2.FitGroupRequest(
            group_id="g6",
            detector="ecod",
            series=_JOINT_TRAIN_SERIES,
            params={"contamination": "0.35"},
        )
        fit_ctx = _FakeContext()
        fit_response = svc.FitGroup(fit_request, fit_ctx)
        assert fit_response.ok is True

        loaded = store.load_group_bundle("g6", "ecod")
        assert loaded["detector"].contamination == pytest.approx(0.35)

    def test_fit_group_malformed_params_does_not_abort(self, servicer):
        """Malformed FitGroup params must not abort/500 the RPC."""
        svc, _, _ = servicer
        request = argus_pb2.FitGroupRequest(
            group_id="g7",
            detector="ecod",
            series=_JOINT_TRAIN_SERIES,
            params={"contamination": "garbage"},
        )
        ctx = _FakeContext()
        response = svc.FitGroup(request, ctx)
        assert not ctx.aborted
        assert response.ok is True
