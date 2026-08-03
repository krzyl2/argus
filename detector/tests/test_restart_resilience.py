"""
RES-02: Detector restart loads all saved models before health transitions to SERVING.

Tests:
  1. empty_model_root_is_noop — create_server with empty tmp_path does not raise;
     registry has no models after startup.
  2. preloaded_model_in_registry — save a PyOD model to tmp_path before calling
     create_server; assert registry.has_model(slug, detector) is True after startup.

create_server sets NOT_SERVING → loads models → sets SERVING (MDL-03 gate).
"""

import json
import os
import pathlib
import signal
import socket
import subprocess
import sys
import time

import grpc
import pytest
from google.protobuf import wrappers_pb2
from grpc_health.v1 import health_pb2, health_pb2_grpc

from argus_detector.hst_detector import EntityDetector
from argus_detector.model_store import ModelStore
from argus_detector.proto import argus_pb2, argus_pb2_grpc
from argus_detector.pyod_detector import PyODDetector
from argus_detector.registry import DetectorRegistry
from argus_detector.server import create_server


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _slug(entity_id: str) -> str:
    """Convert entity_id to slug (same formula as ModelStore callers)."""
    return entity_id.replace(".", "_")


def save_test_model(
    model_root: pathlib.Path,
    entity_id: str,
    detector: str,
    version: int = 1,
) -> None:
    """Create, fit, and save a PyODDetector to model_root under the slug path."""
    slug = _slug(entity_id)
    model = PyODDetector()
    model.fit([float(v) for v in range(10)])  # minimal fit
    ms = ModelStore(root=model_root)
    ms.save_pyod(slug, detector, version, model)


def _extract_registry(server) -> object:
    """Extract the DetectorRegistry from a server built by create_server.

    create_server stores the registry on server._argus_registry after 02-06.
    """
    return server._argus_registry


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------

_FREE_PORT = 0  # port=0 lets the OS pick an ephemeral port; no bind conflict between tests


class TestRestartResilience:
    def test_empty_model_root_is_noop(self, tmp_path):
        """create_server with an empty model root does not raise and returns a server."""
        server = create_server(port=_FREE_PORT, tls=False, model_root=tmp_path)
        assert server is not None

    def test_empty_model_root_registry_has_no_models(self, tmp_path):
        """Registry should be empty when no models are saved."""
        server = create_server(port=_FREE_PORT, tls=False, model_root=tmp_path)
        registry = _extract_registry(server)
        # No entity/detector combinations should exist
        assert not registry.has_model("sensor.test", "mad")
        assert not registry.has_model("sensor_test", "mad")

    def test_nonexistent_model_root_is_noop(self, tmp_path):
        """create_server with a non-existent model_root directory does not raise."""
        missing = tmp_path / "does_not_exist"
        server = create_server(port=_FREE_PORT, tls=False, model_root=missing)
        assert server is not None

    def test_preloaded_model_in_registry(self, tmp_path):
        """Pre-saved model is loaded into registry during create_server startup (MDL-03).

        load_all_into registers by slug, so has_model(slug, detector) must be True.
        """
        entity_id = "sensor.test_entity"
        detector = "mad"
        save_test_model(tmp_path, entity_id, detector)

        server = create_server(port=_FREE_PORT, tls=False, model_root=tmp_path)
        registry = _extract_registry(server)

        slug = _slug(entity_id)
        assert registry.has_model(slug, detector), (
            f"Registry must contain ({slug!r}, {detector!r}) after create_server "
            f"with pre-saved model (MDL-03 gate)"
        )

    def test_multiple_preloaded_models_all_in_registry(self, tmp_path):
        """All models from disk are loaded before SERVING — not just the first."""
        save_test_model(tmp_path, "sensor.a", "mad", version=1)
        save_test_model(tmp_path, "sensor.b", "mad", version=1)

        server = create_server(port=_FREE_PORT, tls=False, model_root=tmp_path)
        registry = _extract_registry(server)

        assert registry.has_model("sensor_a", "mad")
        assert registry.has_model("sensor_b", "mad")


def _find_free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("", 0))
        return s.getsockname()[1]


@pytest.mark.skipif(
    sys.platform == "win32",
    reason=(
        "Windows has no catchable SIGTERM delivery from another process — "
        "Popen.send_signal(SIGTERM)/os.kill(pid, SIGTERM) both call "
        "TerminateProcess() directly on Windows, bypassing Python signal "
        "handlers entirely (CPython's own implementation). The production "
        "target is the Linux s6-overlay add-on container "
        "(argus/rootfs/etc/services.d/detector/run uses `exec`), where a "
        "real SIGTERM is delivered to the Python process and this test's "
        "assumption holds. _install_sigterm_handler's underlying "
        "signal.signal(SIGTERM, ...) call is exercised for real registration "
        "by TestCreateServerAttachesWriter and CheckpointWriter's own unit "
        "tests on every platform; only the OS-level delivery is unverifiable "
        "here."
    ),
)
class TestSigtermFlush:
    """PERSIST-03/SC-3: SIGTERM to the detector flushes every dirty entity to
    disk before the process exits — a clean add-on restart loses zero readings."""

    def test_sigterm_flushes_dirty_checkpoint_before_exit(self, tmp_path):
        port = _find_free_port()

        env = os.environ.copy()
        env["ARGUS_GRPC_PORT"] = str(port)
        env["ARGUS_MODEL_ROOT"] = str(tmp_path)
        env["ARGUS_GRPC_BIND"] = "127.0.0.1"
        env.pop("ARGUS_TLS_CERT", None)
        env.pop("ARGUS_TLS_KEY", None)
        env.pop("ARGUS_TLS_CA", None)
        # Interval irrelevant here — the SIGTERM flush is synchronous, not
        # dependent on the interval thread having ticked.
        env["ARGUS_CHECKPOINT_INTERVAL_SEC"] = "300"

        repo_detector_dir = pathlib.Path(__file__).resolve().parent.parent
        proc = subprocess.Popen(
            [sys.executable, "-m", "argus_detector.server"],
            cwd=str(repo_detector_dir),
            env=env,
        )
        try:
            channel = grpc.insecure_channel(f"127.0.0.1:{port}")
            grpc.channel_ready_future(channel).result(timeout=10)
            stub = argus_pb2_grpc.DetectorServiceStub(channel)

            points = iter(
                [
                    argus_pb2.Point(
                        entity_id="sensor.sigterm_test",
                        value=wrappers_pb2.DoubleValue(value=21.0 + i),
                    )
                    for i in range(5)
                ]
            )
            verdicts = list(stub.ScoreStream(points))
            assert len(verdicts) == 5
            channel.close()

            proc.send_signal(signal.SIGTERM)
            proc.wait(timeout=10)
        finally:
            if proc.poll() is None:
                proc.kill()
                proc.wait(timeout=5)

        pkl_path = tmp_path / "sensor_sigterm_test" / "hst" / "checkpoint.pkl"
        json_path = tmp_path / "sensor_sigterm_test" / "hst" / "checkpoint.json"
        assert pkl_path.exists(), "checkpoint.pkl was not written before process exit"
        sidecar = json.loads(json_path.read_text())
        assert sidecar["n_seen"] == 5


# ---------------------------------------------------------------------------
# 15-04 Task 1: cross-plan restart and crash cases no single plan owns
# ---------------------------------------------------------------------------


class TestHardKillRestoresCheckpoint:
    """SC-1/D-08: a hard, non-catchable kill bypasses the SIGTERM flush path
    entirely — Popen.kill() sends SIGKILL on POSIX and calls TerminateProcess
    directly on Windows, so unlike TestSigtermFlush above, this test needs no
    platform skip; the kill is non-catchable on every platform by construction.
    Restore from the last periodic checkpoint tick must recover a positive
    n_seen no more than one checkpoint interval's worth of readings behind the
    last value actually scored before the kill (the assertion is on the bound,
    not an exact count — one interval of loss is the accepted design)."""

    def test_hard_kill_restores_n_seen_within_one_interval(self, tmp_path):
        port = _find_free_port()

        # 2s interval: long enough that the burst below (well under a second)
        # cannot span two ticks, so the timing is deterministic rather than a
        # tolerated race — the second tick is not due until ~4s after start.
        interval_sec = 2

        env = os.environ.copy()
        env["ARGUS_GRPC_PORT"] = str(port)
        env["ARGUS_MODEL_ROOT"] = str(tmp_path)
        env["ARGUS_GRPC_BIND"] = "127.0.0.1"
        env.pop("ARGUS_TLS_CERT", None)
        env.pop("ARGUS_TLS_KEY", None)
        env.pop("ARGUS_TLS_CA", None)
        env["ARGUS_CHECKPOINT_INTERVAL_SEC"] = str(interval_sec)

        repo_detector_dir = pathlib.Path(__file__).resolve().parent.parent
        entity_id = "sensor.hard_kill_test"
        slug = _slug(entity_id)
        pkl_path = tmp_path / slug / "hst" / "checkpoint.pkl"
        json_path = tmp_path / slug / "hst" / "checkpoint.json"

        proc = subprocess.Popen(
            [sys.executable, "-m", "argus_detector.server"],
            cwd=str(repo_detector_dir),
            env=env,
        )
        try:
            channel = grpc.insecure_channel(f"127.0.0.1:{port}")
            grpc.channel_ready_future(channel).result(timeout=10)
            stub = argus_pb2_grpc.DetectorServiceStub(channel)

            def _score(values):
                points = iter(
                    argus_pb2.Point(
                        entity_id=entity_id,
                        value=wrappers_pb2.DoubleValue(value=v),
                    )
                    for v in values
                )
                return list(stub.ScoreStream(points))

            first_batch = 20
            _score([21.0 + i for i in range(first_batch)])

            # Poll for the first interval tick to persist n_seen == first_batch,
            # bounded well inside the ~4s window before a second tick could land.
            deadline = time.time() + interval_sec + 5.0
            sidecar = None
            while time.time() < deadline:
                if json_path.exists():
                    try:
                        sidecar = json.loads(json_path.read_text())
                    except (OSError, json.JSONDecodeError):
                        sidecar = None
                    if sidecar is not None and sidecar.get("n_seen") == first_batch:
                        break
                time.sleep(0.05)
            assert sidecar is not None and sidecar.get("n_seen") == first_batch, (
                "First checkpoint tick did not persist n_seen before the deadline"
            )

            second_batch = 5
            _score([21.0 + first_batch + i for i in range(second_batch)])
            last_scored_count = first_batch + second_batch

            channel.close()
            # Non-catchable kill (D-08's flush path is SIGTERM-only — this
            # deliberately bypasses it): SIGKILL on POSIX, TerminateProcess on
            # Windows.
            proc.kill()
            proc.wait(timeout=10)
        finally:
            if proc.poll() is None:
                proc.kill()
                proc.wait(timeout=5)

        assert pkl_path.exists(), "checkpoint.pkl from the first tick must survive the hard kill"
        restored_sidecar = json.loads(json_path.read_text())
        restored_n_seen = restored_sidecar["n_seen"]

        assert restored_n_seen > 0, "restored n_seen must be positive after a hard kill"
        assert restored_n_seen <= last_scored_count
        lost = last_scored_count - restored_n_seen
        assert lost <= second_batch, (
            f"lost {lost} readings — more than the {second_batch} sent after the last "
            f"confirmed checkpoint tick, i.e. more than one interval's worth was lost"
        )


class TestCorruptCheckpointDoesNotBlockStartup:
    """SC-6/D-09: one corrupt checkpoint.pkl in the model root must not prevent
    the detector from reaching SERVING or from loading the other, healthy
    entities (a mix of a streaming checkpoint and a versioned batch model)."""

    def test_one_garbage_checkpoint_others_still_load_and_serving(self, tmp_path):
        store = ModelStore(root=tmp_path)

        # Entity A: valid streaming checkpoint.
        det_a = EntityDetector()
        for _ in range(5):
            det_a.score_one(20.0)
        store.save_checkpoint("sensor_a", "hst", det_a, "sensor.a", det_a.n_seen)

        # Entity B: checkpoint.pkl corrupted to outright garbage bytes (not a
        # truncated-but-still-partially-valid pickle prefix) so unpickling
        # fails unconditionally rather than partially succeeding.
        det_b = EntityDetector()
        det_b.score_one(20.0)
        store.save_checkpoint("sensor_b", "hst", det_b, "sensor.b", det_b.n_seen)
        garbage_pkl = tmp_path / "sensor_b" / "hst" / "checkpoint.pkl"
        garbage_pkl.write_bytes(b"not-a-valid-pickle-blob-just-garbage-bytes-1234567890")

        # Entity C: valid versioned (batch) model — unrelated to the checkpoint path.
        save_test_model(tmp_path, "sensor.c", "mad")

        port = _find_free_port()
        server = create_server(port=port, tls=False, model_root=tmp_path)
        server.start()
        try:
            channel = grpc.insecure_channel(f"127.0.0.1:{port}")
            grpc.channel_ready_future(channel).result(timeout=10)
            health_stub = health_pb2_grpc.HealthStub(channel)
            response = health_stub.Check(
                health_pb2.HealthCheckRequest(service="argus.v1.DetectorService")
            )
            assert response.status == health_pb2.HealthCheckResponse.SERVING

            registry = _extract_registry(server)
            assert registry.has_model("sensor.a", "hst"), "healthy checkpoint entity must be registered"
            assert registry.has_model("sensor_c", "mad"), "healthy versioned model entity must be registered"
            assert not registry.has_model("sensor.b", "hst"), "corrupt entity must not be registered"
            channel.close()
        finally:
            server.stop(grace=0)


class TestBogusRiverVersionSidecarSkipped:
    """SC-6 variant/D-03: a checkpoint whose sidecar names a river_version that
    does not match the installed river version is discarded with a WARN — the
    entity starts cold; other entities on the same root are unaffected."""

    def test_bogus_river_version_entity_absent_others_present(self, tmp_path):
        store = ModelStore(root=tmp_path)

        det_bad = EntityDetector()
        det_bad.score_one(20.0)
        store.save_checkpoint("sensor_bad", "hst", det_bad, "sensor.bad", det_bad.n_seen)
        sidecar_path = tmp_path / "sensor_bad" / "hst" / "checkpoint.json"
        sidecar = json.loads(sidecar_path.read_text())
        sidecar["river_version"] = "0.0.0-bogus"
        sidecar_path.write_text(json.dumps(sidecar))

        det_good = EntityDetector()
        det_good.score_one(20.0)
        store.save_checkpoint("sensor_good", "hst", det_good, "sensor.good", det_good.n_seen)

        registry = DetectorRegistry()
        store.load_all_into(registry)  # must not raise

        assert not registry.has_model("sensor.bad", "hst"), "bogus river_version entity must not load"
        assert registry.has_model("sensor.good", "hst"), "other entity on the same root must still load"


class TestIdleEntityNoCheckpointWrites:
    """SC-4/D-05: two checkpoint ticks with no intervening score_one leave the
    checkpoint.pkl mtime and byte length unchanged — an idle entity produces
    zero disk writes."""

    def test_two_idle_ticks_leave_checkpoint_file_unchanged(self, tmp_path):
        registry = DetectorRegistry()
        store = ModelStore(root=tmp_path)

        for _ in range(5):
            registry.score_one("sensor.idle", 21.0)

        written = registry.checkpoint_dirty(store)
        assert written == 1  # baseline write establishing the file

        pkl_path = tmp_path / "sensor_idle" / "hst" / "checkpoint.pkl"
        assert pkl_path.exists()
        stat1 = pkl_path.stat()
        mtime1, size1 = stat1.st_mtime_ns, stat1.st_size

        # Tick 1 with no intervening score_one — must be a no-op.
        assert registry.checkpoint_dirty(store) == 0
        stat2 = pkl_path.stat()
        assert stat2.st_mtime_ns == mtime1
        assert stat2.st_size == size1

        # Tick 2 with no intervening score_one — still a no-op.
        assert registry.checkpoint_dirty(store) == 0
        stat3 = pkl_path.stat()
        assert stat3.st_mtime_ns == mtime1
        assert stat3.st_size == size1
