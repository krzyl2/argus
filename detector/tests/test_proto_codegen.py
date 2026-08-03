"""
Tests that verify Python proto codegen produces importable stubs.

Run gen_proto.py before running these tests, or let conftest.py do it automatically.
"""

import subprocess
import sys
from pathlib import Path

import pytest

# Repo root for invoking gen_proto
REPO_ROOT = Path(__file__).resolve().parent.parent.parent
GEN_PROTO = REPO_ROOT / "detector" / "scripts" / "gen_proto.py"
PROTO_OUT_DIR = REPO_ROOT / "detector" / "argus_detector" / "proto"


def _run_gen_proto():
    """Invoke gen_proto.py and return (returncode, stdout, stderr)."""
    result = subprocess.run(
        [sys.executable, str(GEN_PROTO)],
        capture_output=True,
        text=True,
    )
    return result.returncode, result.stdout, result.stderr


@pytest.fixture(scope="session", autouse=True)
def generate_stubs():
    """Generate proto stubs once per test session."""
    ret, out, err = _run_gen_proto()
    if ret != 0:
        pytest.fail(f"gen_proto.py failed (exit {ret}):\nstdout: {out}\nstderr: {err}")
    yield


class TestProtoCodegen:
    def test_gen_proto_exits_zero(self):
        """gen_proto.py must exit 0 (idempotent)."""
        ret, _, err = _run_gen_proto()
        assert ret == 0, f"gen_proto.py exited {ret}: {err}"

    def test_argus_pb2_file_exists(self):
        """argus_pb2.py must be generated."""
        assert (PROTO_OUT_DIR / "argus_pb2.py").exists(), "argus_pb2.py not generated"

    def test_argus_pb2_grpc_file_exists(self):
        """argus_pb2_grpc.py must be generated."""
        assert (PROTO_OUT_DIR / "argus_pb2_grpc.py").exists(), "argus_pb2_grpc.py not generated"

    def test_argus_pb2_importable(self):
        """argus_pb2 must be importable from the package."""
        from argus_detector.proto import argus_pb2  # noqa: F401

    def test_point_message_exists(self):
        """argus_pb2.Point must exist (verifies proto file was parsed correctly)."""
        from argus_detector.proto import argus_pb2
        assert hasattr(argus_pb2, "Point"), "argus_pb2.Point not found"

    def test_verdict_message_exists(self):
        """argus_pb2.Verdict must exist."""
        from argus_detector.proto import argus_pb2
        assert hasattr(argus_pb2, "Verdict"), "argus_pb2.Verdict not found"

    def test_detector_service_stub_exists(self):
        """argus_pb2_grpc.DetectorServiceStub must exist."""
        from argus_detector.proto import argus_pb2_grpc
        assert hasattr(argus_pb2_grpc, "DetectorServiceStub"), "DetectorServiceStub not found"

    def test_point_has_entity_id_field(self):
        """Point.entity_id field must be present (proves field numbers were preserved)."""
        from argus_detector.proto import argus_pb2
        p = argus_pb2.Point()
        p.entity_id = "sensor.test"
        assert p.entity_id == "sensor.test"

    def test_fit_request_and_response_exist(self):
        """FitRequest and FitResponse must exist (Phase 2 stubs defined now)."""
        from argus_detector.proto import argus_pb2
        assert hasattr(argus_pb2, "FitRequest"), "FitRequest not found"
        assert hasattr(argus_pb2, "FitResponse"), "FitResponse not found"

    def test_group_messages_exist(self):
        """New group messages (Phase 5) must exist after regeneration."""
        from argus_detector.proto import argus_pb2
        assert hasattr(argus_pb2, "Series"), "Series not found"
        assert hasattr(argus_pb2, "FeatureContribution"), "FeatureContribution not found"
        assert hasattr(argus_pb2, "GroupScoreRequest"), "GroupScoreRequest not found"
        assert hasattr(argus_pb2, "GroupScoreResponse"), "GroupScoreResponse not found"
        assert hasattr(argus_pb2, "FitGroupRequest"), "FitGroupRequest not found"
        assert hasattr(argus_pb2, "FitGroupResponse"), "FitGroupResponse not found"

    def test_series_roundtrips_member_id_and_values(self):
        """Series must round-trip member_id and a repeated double values list
        (proves field numbers were preserved and repeated-double works)."""
        from argus_detector.proto import argus_pb2
        s = argus_pb2.Series(member_id="sensor.outdoor_temp", values=[1.0, 2.5, 3.75])
        assert s.member_id == "sensor.outdoor_temp"
        assert list(s.values) == [1.0, 2.5, 3.75]

    def test_detector_service_stub_exposes_group_rpcs(self):
        """DetectorServiceStub instances must expose ScoreGroupBatch and FitGroup
        callables (verifies the RPC stubs regenerated)."""
        import grpc
        from argus_detector.proto import argus_pb2_grpc
        channel = grpc.insecure_channel("localhost:1")
        stub = argus_pb2_grpc.DetectorServiceStub(channel)
        assert callable(getattr(stub, "ScoreGroupBatch", None)), "ScoreGroupBatch not found on stub"
        assert callable(getattr(stub, "FitGroup", None)), "FitGroup not found on stub"

    def test_point_params_and_verdict_warmup_fields_roundtrip(self):
        """Phase 15-02: Point.params (map, field 4) and Verdict.warmed_up/n_seen/window
        (fields 9-11) must survive a serialize/parse round-trip. This is the regeneration
        gate for WARM-01/WARM-02 — it fails loudly if the Python stubs are stale."""
        from argus_detector.proto import argus_pb2

        point = argus_pb2.Point(entity_id="sensor.test")
        point.params["window"] = "50"

        verdict = argus_pb2.Verdict(
            entity_id="sensor.test",
            warmed_up=True,
            n_seen=137,
            window=250,
        )

        point_roundtrip = argus_pb2.Point.FromString(point.SerializeToString())
        verdict_roundtrip = argus_pb2.Verdict.FromString(verdict.SerializeToString())

        assert point_roundtrip.params["window"] == "50"
        assert verdict_roundtrip.warmed_up is True
        assert verdict_roundtrip.n_seen == 137
        assert verdict_roundtrip.window == 250
