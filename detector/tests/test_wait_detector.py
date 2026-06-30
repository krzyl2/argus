"""
Tests for wait-detector.py — the synchronous gRPC health poller.

Covers PROC-02: check_serving returns True against a SERVING loopback detector
and False against a closed port.

Import strategy: the script lives in argus/rootfs/usr/local/bin/ which is not
on the Python package path.  We use importlib to load it by file path.
"""

import importlib.util
import pathlib
import socket
import time

import pytest

# ---------------------------------------------------------------------------
# Import wait-detector.py by path (not a package)
# ---------------------------------------------------------------------------
_SCRIPT_PATH = (
    pathlib.Path(__file__).parent.parent.parent
    / "argus" / "rootfs" / "usr" / "local" / "bin" / "wait-detector.py"
)


def _load_wait_detector():
    spec = importlib.util.spec_from_file_location("wait_detector", _SCRIPT_PATH)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


wait_detector = _load_wait_detector()


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _find_free_port() -> int:
    """Bind to port 0 on loopback and return the OS-assigned port number."""
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------

class TestCheckServingAgainstLiveDetector:
    """PROC-02: check_serving returns True when the detector is SERVING."""

    def test_returns_true_for_serving_detector(self):
        """Start a real detector server on loopback; check_serving must return True."""
        from argus_detector.server import create_server

        port = _find_free_port()
        server = create_server(port=port, tls=False)
        server.start()
        time.sleep(0.15)  # give server time to become ready

        try:
            result = wait_detector.check_serving(
                f"127.0.0.1:{port}",
                "argus.v1.DetectorService",
            )
            assert result is True, "Expected SERVING but check_serving returned False"
        finally:
            server.stop(grace=0)


class TestCheckServingAgainstClosedPort:
    """PROC-02: check_serving returns False when no server listens on the port."""

    def test_returns_false_for_closed_port(self):
        """Obtain a free port, keep it unbound, expect False."""
        port = _find_free_port()
        # Port is not bound — connection will be refused immediately
        result = wait_detector.check_serving(
            f"127.0.0.1:{port}",
            "argus.v1.DetectorService",
            timeout=1.0,
        )
        assert result is False, "Expected False for closed port but got True"


class TestModuleImportability:
    """Importing wait-detector.py must not start the poll loop."""

    def test_module_imports_without_side_effects(self):
        """Re-importing the module must not block or raise."""
        mod = _load_wait_detector()
        assert hasattr(mod, "check_serving")
        assert hasattr(mod, "wait_until_serving")
        assert hasattr(mod, "main")

    def test_default_service_name(self):
        """Default service in main() must be argus.v1.DetectorService."""
        import inspect

        src = inspect.getsource(wait_detector.main)
        assert "argus.v1.DetectorService" in src


class TestWaitUntilServingMaxAttempts:
    """wait_until_serving raises after max_attempts when service never answers."""

    def test_raises_after_max_attempts(self):
        port = _find_free_port()
        with pytest.raises(RuntimeError, match="not SERVING after"):
            wait_detector.wait_until_serving(
                f"127.0.0.1:{port}",
                "argus.v1.DetectorService",
                interval=0.05,
                max_attempts=2,
            )
