"""
Tests for configurable bind address (ARGUS_GRPC_BIND) and model root (ARGUS_MODEL_ROOT).

Covers CODE-02 and CODE-03:
- DetectorConfig.grpc_bind reads ARGUS_GRPC_BIND; defaults to [::].
- DetectorConfig.model_root reads ARGUS_MODEL_ROOT; defaults to /var/argus/models.
- create_server bound to 127.0.0.1 answers health Check with SERVING.
- ModelStore save/load round-trips under a tmp ARGUS_MODEL_ROOT path.
"""

import pathlib
import socket
import time

import grpc
import pytest
from grpc_health.v1 import health_pb2, health_pb2_grpc

from argus_detector.config import DetectorConfig
from argus_detector.model_store import ModelStore
from argus_detector.pyod_detector import PyODDetector
from argus_detector.server import create_server


def _find_free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


# ---------------------------------------------------------------------------
# Config: grpc_bind
# ---------------------------------------------------------------------------

class TestGrpcBindConfig:
    def test_grpc_bind_default_is_all_interfaces(self, monkeypatch):
        """ARGUS_GRPC_BIND unset → grpc_bind == [::]  (v1 backward-compatible default)."""
        monkeypatch.delenv("ARGUS_GRPC_BIND", raising=False)
        config = DetectorConfig()
        assert config.grpc_bind == "[::]"

    def test_grpc_bind_reads_env_var(self, monkeypatch):
        """ARGUS_GRPC_BIND=127.0.0.1 → grpc_bind == 127.0.0.1."""
        monkeypatch.setenv("ARGUS_GRPC_BIND", "127.0.0.1")
        config = DetectorConfig()
        assert config.grpc_bind == "127.0.0.1"

    def test_grpc_bind_arbitrary_address(self, monkeypatch):
        """ARGUS_GRPC_BIND accepts arbitrary address strings."""
        monkeypatch.setenv("ARGUS_GRPC_BIND", "0.0.0.0")
        config = DetectorConfig()
        assert config.grpc_bind == "0.0.0.0"


# ---------------------------------------------------------------------------
# Config: model_root
# ---------------------------------------------------------------------------

class TestModelRootConfig:
    def test_model_root_default_is_v1_path(self, monkeypatch):
        """ARGUS_MODEL_ROOT unset → model_root == /var/argus/models  (v1 default)."""
        monkeypatch.delenv("ARGUS_MODEL_ROOT", raising=False)
        config = DetectorConfig()
        assert config.model_root == "/var/argus/models"

    def test_model_root_reads_env_var(self, monkeypatch, tmp_path):
        """ARGUS_MODEL_ROOT=<path> → model_root == that path."""
        monkeypatch.setenv("ARGUS_MODEL_ROOT", str(tmp_path))
        config = DetectorConfig()
        assert config.model_root == str(tmp_path)


# ---------------------------------------------------------------------------
# Functional: 127.0.0.1-bound server answers health Check with SERVING
# ---------------------------------------------------------------------------

class TestLocalModeBind:
    def test_server_bound_to_loopback_answers_health_serving(self, monkeypatch):
        """create_server with grpc_bind=127.0.0.1 starts and health Check returns SERVING."""
        monkeypatch.setenv("ARGUS_GRPC_BIND", "127.0.0.1")
        monkeypatch.delenv("ARGUS_TLS_CERT", raising=False)
        monkeypatch.delenv("ARGUS_TLS_KEY", raising=False)
        monkeypatch.delenv("ARGUS_TLS_CA", raising=False)

        config = DetectorConfig()
        assert config.grpc_bind == "127.0.0.1"

        port = _find_free_port()
        server = create_server(port=port, tls=False, config=config)
        server.start()
        time.sleep(0.1)

        try:
            channel = grpc.insecure_channel(f"127.0.0.1:{port}")
            stub = health_pb2_grpc.HealthStub(channel)
            response = stub.Check(
                health_pb2.HealthCheckRequest(service=""),
                timeout=5.0,
            )
            assert response.status == health_pb2.HealthCheckResponse.SERVING
            channel.close()
        finally:
            server.stop(grace=0)


# ---------------------------------------------------------------------------
# Functional: ModelStore round-trip under configurable model_root
# ---------------------------------------------------------------------------

class TestModelRootRoundTrip:
    def test_model_store_save_load_under_configured_root(self, monkeypatch, tmp_path):
        """ModelStore(root=config.model_root) saves and loads correctly under tmp_path."""
        monkeypatch.setenv("ARGUS_MODEL_ROOT", str(tmp_path))
        config = DetectorConfig()

        root = pathlib.Path(config.model_root)
        store = ModelStore(root=root)

        # Save a fitted PyOD model
        det = PyODDetector()
        det.fit([1.0, 2.0, 3.0, 4.0, 5.0])
        store.save_pyod("sensor_test", "mad", 1, det)

        # Verify file is under tmp_path
        expected = tmp_path / "sensor_test" / "mad" / "v1" / "model.joblib"
        assert expected.exists(), f"Expected model file not found at {expected}"

        # Load and score
        loaded = store.load_pyod("sensor_test", "mad", version=1)
        scores = loaded.score_batch([1.0, 2.0, 3.0])
        assert len(scores) == 3
