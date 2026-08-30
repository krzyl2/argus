"""
Tests for the gRPC server thread-pool size (ARGUS_GRPC_MAX_WORKERS).

ScoreStream is a long-lived bidi call: each open stream holds one worker thread for
its whole lifetime. With the old fixed pool of 10, ~10 watched entities exhausted the
pool and Health/Check queued behind the streams until the orchestrator's 5s deadline —
reported as detector=False forever in the composite health entity (HEALTH-01), while
scoring itself kept working.
"""

from concurrent import futures

from argus_detector.config import DetectorConfig


class TestGrpcMaxWorkersConfig:
    def test_default_leaves_headroom_for_health_checks(self, monkeypatch):
        """Unset → a pool far larger than the old 10, so streams cannot starve Check."""
        monkeypatch.delenv("ARGUS_GRPC_MAX_WORKERS", raising=False)
        config = DetectorConfig()
        assert config.grpc_max_workers == 64

    def test_reads_env_var(self, monkeypatch):
        monkeypatch.setenv("ARGUS_GRPC_MAX_WORKERS", "128")
        config = DetectorConfig()
        assert config.grpc_max_workers == 128


class TestServerUsesConfiguredPool:
    def test_create_server_pool_matches_config(self, monkeypatch, tmp_path):
        """create_server sizes its ThreadPoolExecutor from config.grpc_max_workers."""
        from argus_detector.server import create_server

        captured = {}
        real_executor = futures.ThreadPoolExecutor

        def recording_executor(*args, **kwargs):
            captured["max_workers"] = kwargs.get("max_workers", args[0] if args else None)
            return real_executor(*args, **kwargs)

        monkeypatch.setattr(
            "argus_detector.server.futures.ThreadPoolExecutor", recording_executor
        )
        monkeypatch.setenv("ARGUS_GRPC_MAX_WORKERS", "33")

        config = DetectorConfig()
        create_server(port=0, tls=False, config=config, model_root=tmp_path)

        assert captured["max_workers"] == 33
