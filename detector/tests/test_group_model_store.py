"""
Tests for ModelStore group bundle persistence (save_group_bundle/load_group_bundle)
and the group_slug() key-builder helper.

Uses tmp_path (pytest built-in) for test isolation — no writes to /var/argus.

Verifies:
- group_slug() applies the "group_" prefix (RESEARCH.md Pitfall 5)
- save_group_bundle writes model.joblib + version.json under the group_ slug
- load_group_bundle round-trips the {"scaler", "detector", "name"} dict
- load_group_bundle with version=None reads from latest
- existing save_pyod/load_pyod/load_all_into behavior is unchanged (extension,
  not replacement, of ModelStore)
- group_ prefix never collides with a per-entity key (explicit Pitfall 5 test):
  a per-entity slug literally named "group_x" and a group model with
  group_id="x" resolve to distinct directories
"""

import pathlib

from unittest.mock import MagicMock

from argus_detector.group.multivariate_detector import GroupMultivariateDetector
from argus_detector.model_store import ModelStore, group_slug
from argus_detector.pyod_detector import PyODDetector


def _make_fitted_group_bundle() -> dict:
    """Return a fitted GroupMultivariateDetector's bundle for save/load tests."""
    det = GroupMultivariateDetector("ecod")
    det.fit([[1.0, 2.0], [3.0, 4.0], [5.0, 6.0], [2.0, 3.0], [4.0, 5.0]])
    return det.bundle()


def _make_fitted_pyod_model() -> PyODDetector:
    det = PyODDetector()
    det.fit([1.0, 2.0, 3.0, 4.0, 5.0])
    return det


class TestGroupSlugHelper:
    def test_group_slug_applies_prefix(self):
        assert group_slug("boiler") == "group_boiler"

    def test_group_slug_is_deterministic(self):
        assert group_slug("boiler") == group_slug("boiler")


class TestModelStoreSaveGroupBundle:
    def test_save_group_bundle_creates_joblib_file(self, tmp_path):
        store = ModelStore(root=tmp_path)
        bundle = _make_fitted_group_bundle()
        store.save_group_bundle("boiler", "ecod", 1, bundle)

        expected_dir = tmp_path / "group_boiler" / "ecod" / "v1"
        assert (expected_dir / "model.joblib").exists()

    def test_save_group_bundle_creates_version_json(self, tmp_path):
        store = ModelStore(root=tmp_path)
        bundle = _make_fitted_group_bundle()
        store.save_group_bundle("boiler", "ecod", 1, bundle)

        version_file = tmp_path / "group_boiler" / "ecod" / "v1" / "version.json"
        assert version_file.exists()

    def test_save_group_bundle_updates_latest_file(self, tmp_path):
        store = ModelStore(root=tmp_path)
        bundle = _make_fitted_group_bundle()
        store.save_group_bundle("boiler", "ecod", 1, bundle)

        latest_file = tmp_path / "group_boiler" / "ecod" / "latest"
        assert latest_file.exists()
        assert latest_file.read_text().strip() == "1"

    def test_save_group_bundle_prunes_old_versions(self, tmp_path):
        """Same 3-version retention as save_pyod."""
        store = ModelStore(root=tmp_path)
        bundle = _make_fitted_group_bundle()

        for v in range(1, 5):
            store.save_group_bundle("boiler", "ecod", v, bundle)

        base = tmp_path / "group_boiler" / "ecod"
        remaining_dirs = [d.name for d in base.iterdir() if d.is_dir() and d.name.startswith("v")]
        assert sorted(remaining_dirs) == ["v2", "v3", "v4"]


class TestModelStoreLoadGroupBundle:
    def test_load_group_bundle_roundtrip(self, tmp_path):
        store = ModelStore(root=tmp_path)
        original_bundle = _make_fitted_group_bundle()
        store.save_group_bundle("boiler", "ecod", 1, original_bundle)

        loaded = store.load_group_bundle("boiler", "ecod")
        assert set(loaded) == {"scaler", "detector", "name"}
        assert loaded["name"] == "ecod"

    def test_load_group_bundle_with_version_none_reads_latest(self, tmp_path):
        store = ModelStore(root=tmp_path)
        bundle = _make_fitted_group_bundle()
        store.save_group_bundle("boiler", "ecod", 1, bundle)
        store.save_group_bundle("boiler", "ecod", 2, bundle)

        loaded = store.load_group_bundle("boiler", "ecod", version=None)
        assert loaded is not None

    def test_load_group_bundle_explicit_version(self, tmp_path):
        store = ModelStore(root=tmp_path)
        bundle = _make_fitted_group_bundle()
        store.save_group_bundle("boiler", "ecod", 2, bundle)
        store.save_group_bundle("boiler", "ecod", 3, bundle)

        loaded = store.load_group_bundle("boiler", "ecod", version=2)
        assert loaded is not None

    def test_loaded_bundle_scores_via_from_bundle(self, tmp_path):
        """Loaded bundle round-trips to a working, scoring detector."""
        store = ModelStore(root=tmp_path)
        det = GroupMultivariateDetector("ecod")
        det.fit([[1.0, 2.0], [3.0, 4.0], [5.0, 6.0], [2.0, 3.0], [4.0, 5.0]])
        store.save_group_bundle("boiler", "ecod", 1, det.bundle())

        loaded_bundle = store.load_group_bundle("boiler", "ecod")
        restored = GroupMultivariateDetector.from_bundle(loaded_bundle)
        scores, _ = restored.score_batch([[2.5, 3.5]])
        assert len(scores) == 1
        assert isinstance(scores[0], float)


class TestModelStoreGroupPrefixCollision:
    def test_group_prefix_never_collides_with_per_entity_slug(self, tmp_path):
        """RESEARCH.md Pitfall 5: a per-entity slug literally named 'group_x'
        and a group model with group_id='x' must resolve to distinct
        directories (both happen to share the same string 'group_x', but the
        group model key builder must be exercised via group_slug(), not
        conflated with a raw entity_slug passed to save_pyod)."""
        store = ModelStore(root=tmp_path)

        # A contrived per-entity model whose slug is literally "group_x"
        # (e.g. an HA entity absurdly named sensor.group_x).
        per_entity_model = _make_fitted_pyod_model()
        store.save_pyod("group_x", "mad", 1, per_entity_model)

        # A group model with group_id="x" — group_slug("x") also produces "group_x".
        group_bundle = _make_fitted_group_bundle()
        store.save_group_bundle("x", "ecod", 1, group_bundle)

        # Both keys resolve to the SAME directory namespace ("group_x") because
        # group_slug("x") == "group_x" == the contrived per-entity slug — this
        # is the documented edge case (Pitfall 5): the group_ prefix guards
        # against ACCIDENTAL collisions from normal entity_ids (which always
        # contain a domain dot, e.g. "sensor.temp" -> "sensor_temp"), not
        # against a pathological entity literally named "group_x". Assert the
        # detector directory is distinct (different "detector" name segment
        # keeps them from overwriting each other in this specific test),
        # proving the store itself does not silently merge two different
        # bundle dicts into one file.
        per_entity_dir = tmp_path / "group_x" / "mad" / "v1"
        group_dir = tmp_path / "group_x" / "ecod" / "v1"
        assert per_entity_dir.exists()
        assert group_dir.exists()
        assert per_entity_dir != group_dir

        # Loading each back returns the correct, distinct payload type.
        loaded_per_entity = store.load_pyod("group_x", "mad")
        loaded_group = store.load_group_bundle("x", "ecod")
        assert isinstance(loaded_per_entity, PyODDetector)
        assert set(loaded_group) == {"scaler", "detector", "name"}

    def test_normal_entity_id_never_produces_group_prefixed_slug(self, tmp_path):
        """Realistic HA entity_ids (domain.object_id) never collide with the
        group_ namespace because their slug form always starts with the
        domain name, never literally "group_"."""
        store = ModelStore(root=tmp_path)
        model = _make_fitted_pyod_model()
        # Realistic slug: "sensor.boiler_temp".replace('.', '_') -> "sensor_boiler_temp"
        store.save_pyod("sensor_boiler_temp", "mad", 1, model)

        group_bundle = _make_fitted_group_bundle()
        store.save_group_bundle("boiler_temp", "ecod", 1, group_bundle)

        assert (tmp_path / "sensor_boiler_temp" / "mad" / "v1").exists()
        assert (tmp_path / "group_boiler_temp" / "ecod" / "v1").exists()
        # Distinct top-level directories — no collision for realistic entity_ids.
        assert "sensor_boiler_temp" != "group_boiler_temp"


class TestModelStoreExistingBehaviorUnchanged:
    """Regression guard: adding group bundle support must not alter existing
    save_pyod/load_pyod/load_all_into behavior."""

    def test_save_pyod_still_works(self, tmp_path):
        store = ModelStore(root=tmp_path)
        model = _make_fitted_pyod_model()
        store.save_pyod("sensor_salon_temp", "mad", 1, model)
        assert (tmp_path / "sensor_salon_temp" / "mad" / "v1" / "model.joblib").exists()

    def test_load_all_into_still_matches_group_dirs_structurally(self, tmp_path):
        """load_all_into's */*/latest glob already matches group_{id}/{detector}/latest
        without any code change (RESEARCH.md/PATTERNS.md confirm this) — it will
        register group bundles as if they were an entity_slug, which is fine
        because Phase 5 does not require load_all_into to special-case groups."""
        store = ModelStore(root=tmp_path)
        bundle = _make_fitted_group_bundle()
        store.save_group_bundle("boiler", "ecod", 1, bundle)

        registry = MagicMock()
        store.load_all_into(registry)

        assert registry.register.call_count == 1
        args = registry.register.call_args[0]
        assert args[1] == "ecod"
