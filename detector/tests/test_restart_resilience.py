"""
RES-02: Detector restart loads all saved models before health transitions to SERVING.

Tests:
  1. empty_model_root_is_noop — create_server with empty tmp_path does not raise;
     registry has no models after startup.
  2. preloaded_model_in_registry — save a PyOD model to tmp_path before calling
     create_server; assert registry.has_model(slug, detector) is True after startup.

create_server sets NOT_SERVING → loads models → sets SERVING (MDL-03 gate).
"""

import pathlib

import pytest

from argus_detector.model_store import ModelStore
from argus_detector.pyod_detector import PyODDetector
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
