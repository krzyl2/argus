"""
Tests for proto3 score=0.0 wire survival (PITFALL 1 mitigation).

D-01: Verdict.score uses google.protobuf.DoubleValue wrapper, not raw double.
This means score=0.0 survives the wire as a present field, not silently dropped.

RED phase: these tests MUST fail before implementation.
GREEN phase: passes if protobuf stubs are correctly generated (independent of HST).
"""
import pytest

from google.protobuf import wrappers_pb2

from argus_detector.proto import argus_pb2


class TestScoreZeroWire:
    """PITFALL 1: proto3 drops raw double 0.0; DoubleValue wrapper preserves it."""

    def test_score_zero_survives_roundtrip(self):
        """Serialize Verdict with score=0.0, parse back; HasField('score') must be True."""
        verdict = argus_pb2.Verdict(
            entity_id="sensor.test",
            score=wrappers_pb2.DoubleValue(value=0.0),
            is_anomaly=False,
            detector="hst",
        )

        # Serialize to bytes (simulates wire transport)
        data = verdict.SerializeToString()

        # Parse back (simulates receiver)
        parsed = argus_pb2.Verdict()
        parsed.ParseFromString(data)

        assert parsed.HasField("score"), (
            "score=0.0 was dropped on the wire — DoubleValue wrapper not working"
        )
        assert parsed.score.value == 0.0, (
            f"score.value expected 0.0, got {parsed.score.value}"
        )

    def test_score_absent_when_not_set(self):
        """A Verdict without score set must NOT have the field present."""
        verdict = argus_pb2.Verdict(entity_id="sensor.test", is_anomaly=False, detector="hst")
        data = verdict.SerializeToString()
        parsed = argus_pb2.Verdict()
        parsed.ParseFromString(data)
        assert not parsed.HasField("score"), (
            "score field should be absent when not explicitly set"
        )

    def test_score_nonzero_survives_roundtrip(self):
        """Confirm non-zero scores also round-trip correctly."""
        verdict = argus_pb2.Verdict(
            entity_id="sensor.test",
            score=wrappers_pb2.DoubleValue(value=0.75),
            is_anomaly=True,
            detector="hst",
        )
        data = verdict.SerializeToString()
        parsed = argus_pb2.Verdict()
        parsed.ParseFromString(data)
        assert parsed.HasField("score")
        assert abs(parsed.score.value - 0.75) < 1e-9
