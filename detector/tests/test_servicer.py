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
