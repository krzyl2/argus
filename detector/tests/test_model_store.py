"""
Tests for ModelStore (versioned disk persistence).

Uses tmp_path (pytest built-in) for test isolation — no writes to /var/argus.

Verifies:
- save_pyod writes model.joblib + version.json; updates latest file
- save_river writes model.pkl + version.json; updates latest file
- load_pyod with version=None reads from latest
- Prune: after 4 saves, only versions 2,3,4 remain
- version.json contains all 7 required fields
- latest file updated atomically (tmp→rename pattern)
- next_version returns 1 when no latest; N+1 when latest exists
- load_all_into calls registry.register for each found model
"""

import json
import pathlib
import pickle
from unittest.mock import MagicMock, call

import joblib
import numpy as np
import pytest

from argus_detector.model_store import ModelStore
from argus_detector.pyod_detector import PyODDetector


def _make_fitted_pyod_model() -> PyODDetector:
    """Return a fitted PyODDetector for use in save/load tests."""
    det = PyODDetector()
    det.fit([1.0, 2.0, 3.0, 4.0, 5.0])
    return det


class TestModelStoreSavePyOD:
    def test_save_pyod_creates_joblib_file(self, tmp_path):
        store = ModelStore(root=tmp_path)
        model = _make_fitted_pyod_model()
        store.save_pyod("sensor_salon_temp", "mad", 1, model)

        expected_dir = tmp_path / "sensor_salon_temp" / "mad" / "v1"
        assert (expected_dir / "model.joblib").exists()

    def test_save_pyod_creates_version_json(self, tmp_path):
        store = ModelStore(root=tmp_path)
        model = _make_fitted_pyod_model()
        store.save_pyod("sensor_salon_temp", "mad", 1, model)

        version_file = tmp_path / "sensor_salon_temp" / "mad" / "v1" / "version.json"
        assert version_file.exists()

    def test_save_pyod_updates_latest_file(self, tmp_path):
        store = ModelStore(root=tmp_path)
        model = _make_fitted_pyod_model()
        store.save_pyod("sensor_salon_temp", "mad", 1, model)

        latest_file = tmp_path / "sensor_salon_temp" / "mad" / "latest"
        assert latest_file.exists()
        assert latest_file.read_text().strip() == "1"

    def test_save_pyod_entity_dot_replaced(self, tmp_path):
        """entity_id with dots uses underscore slug for directory."""
        store = ModelStore(root=tmp_path)
        model = _make_fitted_pyod_model()
        # Caller is responsible for passing the slug; ModelStore stores as given.
        store.save_pyod("sensor.salon_temp", "mad", 1, model)
        # Directory key includes the dot — callers must pass slug (entity_id.replace('.', '_'))
        expected_dir = tmp_path / "sensor.salon_temp" / "mad" / "v1"
        assert (expected_dir / "model.joblib").exists()


class TestModelStoreSaveRiver:
    def test_save_river_creates_pkl_file(self, tmp_path):
        from river import anomaly
        store = ModelStore(root=tmp_path)
        # River HST model (not fitted; pickle works regardless)
        model = anomaly.HalfSpaceTrees(n_trees=5, height=4, window_size=10, seed=42)
        store.save_river("sensor_temp", "hst", 1, model)

        expected_dir = tmp_path / "sensor_temp" / "hst" / "v1"
        assert (expected_dir / "model.pkl").exists()

    def test_save_river_creates_version_json(self, tmp_path):
        from river import anomaly
        store = ModelStore(root=tmp_path)
        model = anomaly.HalfSpaceTrees(n_trees=5, height=4, window_size=10, seed=42)
        store.save_river("sensor_temp", "hst", 1, model)

        version_file = tmp_path / "sensor_temp" / "hst" / "v1" / "version.json"
        assert version_file.exists()

    def test_save_river_updates_latest(self, tmp_path):
        from river import anomaly
        store = ModelStore(root=tmp_path)
        model = anomaly.HalfSpaceTrees(n_trees=5, height=4, window_size=10, seed=42)
        store.save_river("sensor_temp", "hst", 1, model)

        latest_file = tmp_path / "sensor_temp" / "hst" / "latest"
        assert latest_file.read_text().strip() == "1"


class TestModelStoreVersionJson:
    def test_version_json_has_all_required_fields(self, tmp_path):
        """version.json must contain all 7 required fields (MDL-02)."""
        store = ModelStore(root=tmp_path)
        model = _make_fitted_pyod_model()
        store.save_pyod("sensor_salon_temp", "mad", 1, model)

        version_file = tmp_path / "sensor_salon_temp" / "mad" / "v1" / "version.json"
        meta = json.loads(version_file.read_text())

        required_keys = {
            "version", "entity_id", "detector",
            "created_at", "grpcio_version", "pyod_version", "river_version"
        }
        assert required_keys.issubset(set(meta.keys()))

    def test_version_json_values(self, tmp_path):
        """version.json contains correct values for version, entity_id, detector."""
        store = ModelStore(root=tmp_path)
        model = _make_fitted_pyod_model()
        store.save_pyod("sensor_salon_temp", "mad", 3, model)

        version_file = tmp_path / "sensor_salon_temp" / "mad" / "v3" / "version.json"
        meta = json.loads(version_file.read_text())

        assert meta["version"] == 3
        assert meta["entity_id"] == "sensor_salon_temp"
        assert meta["detector"] == "mad"
        assert meta["created_at"]  # non-empty ISO timestamp
        assert meta["grpcio_version"]  # non-empty
        assert meta["pyod_version"]  # non-empty
        assert meta["river_version"]  # non-empty


class TestModelStoreLoad:
    def test_load_pyod_with_version_none_reads_latest(self, tmp_path):
        """load_pyod(version=None) reads from latest pointer."""
        store = ModelStore(root=tmp_path)
        model = _make_fitted_pyod_model()
        store.save_pyod("sensor_salon_temp", "mad", 1, model)

        loaded = store.load_pyod("sensor_salon_temp", "mad", version=None)
        assert loaded is not None

    def test_load_pyod_roundtrip(self, tmp_path):
        """Save then load a PyOD model — loaded model scores correctly."""
        store = ModelStore(root=tmp_path)
        original = _make_fitted_pyod_model()
        store.save_pyod("sensor_salon_temp", "mad", 1, original)

        loaded = store.load_pyod("sensor_salon_temp", "mad", version=1)
        scores = loaded.score_batch([1.0, 2.0, 3.0, 4.0, 5.0])
        assert len(scores) == 5
        assert all(isinstance(s, float) for s in scores)

    def test_load_pyod_explicit_version(self, tmp_path):
        """load_pyod with explicit version loads that version."""
        store = ModelStore(root=tmp_path)
        model = _make_fitted_pyod_model()
        store.save_pyod("sensor_temp", "mad", 2, model)
        store.save_pyod("sensor_temp", "mad", 3, model)

        # Explicitly load version 2
        loaded = store.load_pyod("sensor_temp", "mad", version=2)
        assert loaded is not None

    def test_load_river_roundtrip(self, tmp_path):
        """Save then load a River model — pickle roundtrip works."""
        from river import anomaly
        store = ModelStore(root=tmp_path)
        model = anomaly.HalfSpaceTrees(n_trees=5, height=4, window_size=10, seed=42)
        store.save_river("sensor_temp", "hst", 1, model)

        loaded = store.load_river("sensor_temp", "hst", version=1)
        assert loaded is not None


class TestModelStoreNextVersion:
    def test_next_version_returns_1_when_no_latest(self, tmp_path):
        """next_version → 1 when no model exists yet."""
        store = ModelStore(root=tmp_path)
        v = store.next_version("sensor_new", "mad")
        assert v == 1

    def test_next_version_increments_from_latest(self, tmp_path):
        """next_version → latest + 1 after a save."""
        store = ModelStore(root=tmp_path)
        model = _make_fitted_pyod_model()
        store.save_pyod("sensor_temp", "mad", 1, model)
        assert store.next_version("sensor_temp", "mad") == 2

        store.save_pyod("sensor_temp", "mad", 2, model)
        assert store.next_version("sensor_temp", "mad") == 3


class TestModelStorePrune:
    def test_prune_keeps_3_most_recent(self, tmp_path):
        """After saving 4 versions, only v2, v3, v4 remain; v1 pruned."""
        store = ModelStore(root=tmp_path)
        model = _make_fitted_pyod_model()

        for v in range(1, 5):  # save v1, v2, v3, v4
            store.save_pyod("sensor_temp", "mad", v, model)

        base = tmp_path / "sensor_temp" / "mad"
        remaining_dirs = [d.name for d in base.iterdir() if d.is_dir() and d.name.startswith("v")]
        assert sorted(remaining_dirs) == ["v2", "v3", "v4"], (
            f"Expected [v2, v3, v4], got {sorted(remaining_dirs)}"
        )

    def test_prune_exactly_3_versions_no_removal(self, tmp_path):
        """Saving exactly 3 versions → all 3 kept."""
        store = ModelStore(root=tmp_path)
        model = _make_fitted_pyod_model()

        for v in range(1, 4):
            store.save_pyod("sensor_temp", "mad", v, model)

        base = tmp_path / "sensor_temp" / "mad"
        remaining_dirs = [d.name for d in base.iterdir() if d.is_dir() and d.name.startswith("v")]
        assert sorted(remaining_dirs) == ["v1", "v2", "v3"]

    def test_prune_5_versions_keeps_3(self, tmp_path):
        """After 5 saves, v3+v4+v5 remain; v1+v2 pruned."""
        store = ModelStore(root=tmp_path)
        model = _make_fitted_pyod_model()

        for v in range(1, 6):
            store.save_pyod("sensor_temp", "mad", v, model)

        base = tmp_path / "sensor_temp" / "mad"
        remaining_dirs = [d.name for d in base.iterdir() if d.is_dir() and d.name.startswith("v")]
        assert sorted(remaining_dirs) == ["v3", "v4", "v5"]


class TestModelStoreAtomicLatest:
    def test_latest_file_contains_correct_version(self, tmp_path):
        """latest file must contain the most recently saved version number."""
        store = ModelStore(root=tmp_path)
        model = _make_fitted_pyod_model()

        store.save_pyod("sensor_temp", "mad", 1, model)
        latest_file = tmp_path / "sensor_temp" / "mad" / "latest"
        assert int(latest_file.read_text().strip()) == 1

        store.save_pyod("sensor_temp", "mad", 2, model)
        assert int(latest_file.read_text().strip()) == 2

    def test_no_tmp_file_left_after_save(self, tmp_path):
        """Atomic write: .tmp file must not exist after save (it was renamed)."""
        store = ModelStore(root=tmp_path)
        model = _make_fitted_pyod_model()
        store.save_pyod("sensor_temp", "mad", 1, model)

        tmp_file = tmp_path / "sensor_temp" / "mad" / "latest.tmp"
        assert not tmp_file.exists(), "Temporary .tmp file should not exist after atomic rename"


class TestModelStoreLoadAllInto:
    def test_load_all_into_calls_register_for_each_model(self, tmp_path):
        """load_all_into scans MODEL_ROOT and calls registry.register for each model."""
        store = ModelStore(root=tmp_path)
        model = _make_fitted_pyod_model()

        # Save two different entities
        store.save_pyod("sensor_salon_temp", "mad", 1, model)
        store.save_pyod("sensor_outdoor_temp", "mad", 1, model)

        registry = MagicMock()
        store.load_all_into(registry)

        # registry.register should have been called once per (slug, detector) pair
        assert registry.register.call_count == 2

    def test_load_all_into_empty_root_no_crash(self, tmp_path):
        """Empty model root → load_all_into does nothing without raising."""
        store = ModelStore(root=tmp_path)
        registry = MagicMock()
        store.load_all_into(registry)  # must not raise
        registry.register.assert_not_called()

    def test_load_all_into_passes_slug_and_detector(self, tmp_path):
        """load_all_into calls register with correct slug and detector name."""
        store = ModelStore(root=tmp_path)
        model = _make_fitted_pyod_model()
        store.save_pyod("sensor_salon_temp", "mad", 1, model)

        registry = MagicMock()
        store.load_all_into(registry)

        # First positional arg to register should be slug, second should be detector
        args = registry.register.call_args[0]  # positional args of first call
        assert args[0] == "sensor_salon_temp"
        assert args[1] == "mad"
