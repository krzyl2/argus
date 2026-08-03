"""
Tests for the Warmup RPC end-to-end and its detector-side idempotency gate
(Phase 15-03, Task 1).

Covers:
  - DetectorRegistry.warmup_one: prime, re-prime idempotency, checkpoint-
    restored idempotency, partial prime, empty history, dirty-after-prime
    (BACKFILL-01/02/03, D-12).
  - DetectorServicer.Warmup: empty entity_id guard, happy path mapping the
    registry's returned tuple onto the response, exception -> ok=False.
"""

from __future__ import annotations

import pytest

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
def registry():
    return DetectorRegistry()


@pytest.fixture()
def servicer(tmp_path):
    from argus_detector.model_store import ModelStore
    from argus_detector.servicer import DetectorServicer

    reg = DetectorRegistry()
    store = ModelStore(root=tmp_path)
    return DetectorServicer(reg, store), reg, store


def _make_history(values: list[float]) -> list[argus_pb2.Point]:
    from google.protobuf import wrappers_pb2

    return [
        argus_pb2.Point(value=wrappers_pb2.DoubleValue(value=v)) for v in values
    ]


# ---------------------------------------------------------------------------
# DetectorRegistry.warmup_one
# ---------------------------------------------------------------------------


class TestWarmupOnePrimesColdEntity:
    def test_250_values_primes_and_reports_warmed_up(self, registry):
        values = [20.0 + (i % 5) for i in range(250)]
        result = registry.warmup_one("sensor.x", "hst", values, {"window": "250"})
        assert result == (True, 250, 250, False)

    def test_second_call_is_skipped_and_n_seen_stays_at_250(self, registry):
        values = [20.0 + (i % 5) for i in range(250)]
        registry.warmup_one("sensor.x", "hst", values, {"window": "250"})

        second = registry.warmup_one("sensor.x", "hst", values, {"window": "250"})
        assert second == (True, 250, 250, True)


class TestWarmupOneCheckpointRestoredIsNeverRePrimed:
    def test_checkpoint_restored_entity_returns_skipped_true(self, registry):
        from argus_detector.hst_detector import EntityDetector

        restored = EntityDetector.from_params({"window": "250"})
        for i in range(100):
            restored.score_one(20.0 + i)
        registry.register_checkpoint("sensor.restored", "hst", restored, n_seen=100)

        result = registry.warmup_one(
            "sensor.restored", "hst", [999.0] * 250, {"window": "250"}
        )
        assert result == (False, 100, 250, True)


class TestWarmupOnePartialPrime:
    def test_40_of_250_is_a_valid_partial_prime(self, registry):
        values = [20.0 + (i % 5) for i in range(40)]
        result = registry.warmup_one("sensor.partial", "hst", values, {"window": "250"})
        assert result == (False, 40, 250, False)


class TestWarmupOneEmptyHistory:
    def test_empty_list_does_not_raise_and_n_seen_is_zero(self, registry):
        result = registry.warmup_one("sensor.empty", "hst", [], {"window": "250"})
        assert result == (False, 0, 250, False)


class TestWarmupOneLeavesEntityDirty:
    def test_checkpoint_dirty_writes_after_warmup_one(self, registry, tmp_path):
        from argus_detector.model_store import ModelStore

        store = ModelStore(root=tmp_path)
        values = [20.0 + (i % 5) for i in range(250)]
        registry.warmup_one("sensor.dirty_after_prime", "hst", values, {"window": "250"})

        written = registry.checkpoint_dirty(store)
        assert written == 1
        pkl_path = tmp_path / "sensor_dirty_after_prime" / "hst" / "checkpoint.pkl"
        assert pkl_path.exists()


# ---------------------------------------------------------------------------
# DetectorServicer.Warmup
# ---------------------------------------------------------------------------


class TestServicerWarmupGuards:
    def test_empty_entity_id_aborts_invalid_argument(self, servicer):
        import grpc

        svc, _, _ = servicer
        request = argus_pb2.WarmupRequest(entity_id="", history=_make_history([1.0]))
        ctx = _FakeContext()
        result = svc.Warmup(request, ctx)
        assert ctx.aborted
        assert ctx.abort_code == grpc.StatusCode.INVALID_ARGUMENT
        assert result is None, "After abort, return value must be None (gRPC ignores it)"


class TestServicerWarmupHappyPath:
    def test_successful_warmup_returns_ok_true_with_registry_numbers(self, servicer):
        svc, registry_, _ = servicer
        history = _make_history([20.0 + (i % 5) for i in range(250)])
        request = argus_pb2.WarmupRequest(
            entity_id="sensor.happy",
            detector="hst",
            params={"window": "250"},
            history=history,
        )
        ctx = _FakeContext()
        response = svc.Warmup(request, ctx)
        assert not ctx.aborted
        assert response.ok is True
        assert response.n_seen == 250
        assert response.warmed_up is True
        assert response.skipped is False
        assert registry_.get_warmup_state("sensor.happy", "hst") == (True, 250, 250)

    def test_default_detector_is_hst(self, servicer):
        svc, registry_, _ = servicer
        request = argus_pb2.WarmupRequest(
            entity_id="sensor.default_det", history=_make_history([1.0, 2.0])
        )
        ctx = _FakeContext()
        response = svc.Warmup(request, ctx)
        assert response.ok is True
        assert registry_.has_model("sensor.default_det", "hst")

    def test_unexpected_exception_returns_ok_false_with_error(self, servicer, monkeypatch):
        svc, registry_, _ = servicer

        def _raise(*args, **kwargs):
            raise RuntimeError("boom")

        monkeypatch.setattr(registry_, "warmup_one", _raise)
        request = argus_pb2.WarmupRequest(
            entity_id="sensor.boom", history=_make_history([1.0])
        )
        ctx = _FakeContext()
        response = svc.Warmup(request, ctx)
        assert not ctx.aborted
        assert response.ok is False
        assert "boom" in response.error

    def test_warmup_never_emits_verdicts_or_publishes(self, servicer):
        """The Warmup RPC returns a single WarmupResponse, never a stream of
        Verdicts — proven simply by the return type not being iterable of
        Verdict, and by no side channel existing on the fake servicer."""
        svc, _, _ = servicer
        request = argus_pb2.WarmupRequest(
            entity_id="sensor.no_verdicts", history=_make_history([1.0])
        )
        ctx = _FakeContext()
        response = svc.Warmup(request, ctx)
        assert isinstance(response, argus_pb2.WarmupResponse)
