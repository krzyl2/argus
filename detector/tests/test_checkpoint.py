"""
Tests for streaming-detector checkpoint persistence (Phase 15-01).

Task 1 covers:
- EntityDetector.n_seen / .window accessors (RESEARCH.md Pitfall 4)
- In-situ measurement of pickle size + deepcopy latency (confirms RESEARCH.md's
  measured 419291 bytes / 56-96 ms; the per-entity yield in Task 2 is baseline
  design, not a conditional fallback, per CONTEXT.md's >50ms trigger)
- ModelStore.save_checkpoint / load_checkpoint round-trip (D-02/D-03/D-07)
- load_checkpoint river_version mismatch discard (D-03)
- load_all_into checkpoint glob pass: corrupt-checkpoint fault isolation (D-09)
- load_all_into ordering guarantee: checkpoint wins over stale versioned model
  (D-01 / RESEARCH.md Pitfall 2)

Task 2/3 tests (checkpoint_dirty, CheckpointWriter, SIGTERM) are appended to
this module by later tasks in the same plan.
"""

import copy
import json
import pickle
import time

from river import anomaly

from argus_detector.hst_detector import EntityDetector
from argus_detector.model_store import ModelStore
from argus_detector.registry import DetectorRegistry


class TestEntityDetectorAccessors:
    def test_window_default(self):
        assert EntityDetector().window == 250

    def test_window_from_params(self):
        det = EntityDetector.from_params({"window": "50"})
        assert det.window == 50

    def test_n_seen_matches_call_count(self):
        det = EntityDetector()
        for _ in range(17):
            det.score_one(20.0)
        assert det.n_seen == 17


class TestPickleSizeAndDeepcopyLatency:
    """In-situ confirmation of RESEARCH.md's measured 419291 bytes / 56-96 ms
    on this machine (not a discovery exercise) — CONTEXT.md's Risk table
    ">50ms" trigger fires at defaults, so Task 2's per-entity yield is
    baseline design, not a conditional mitigation."""

    def test_pickle_size_and_deepcopy_latency(self):
        det = EntityDetector()
        for _ in range(500):
            det.score_one(20.0)

        blob = pickle.dumps(det)
        size = len(blob)
        print(f"MEASURED pickle size: {size} bytes")
        assert 200000 <= size <= 1200000

        t0 = time.perf_counter()
        copy.deepcopy(det)
        elapsed_ms = (time.perf_counter() - t0) * 1000
        print(f"MEASURED deepcopy latency: {elapsed_ms:.1f} ms")
        assert elapsed_ms < 500


class TestSaveLoadCheckpointRoundTrip:
    def test_round_trip_preserves_n_seen_and_window(self, tmp_path):
        det = EntityDetector.from_params({"window": "50"})
        for _ in range(30):
            det.score_one(21.0)

        store = ModelStore(root=tmp_path)
        store.save_checkpoint("sensor_x", "hst", det, "sensor.x", det.n_seen)

        # Fresh ModelStore on the same root — proves the round-trip is via disk.
        store2 = ModelStore(root=tmp_path)
        result = store2.load_checkpoint("sensor_x", "hst")
        assert result is not None
        model, sidecar = result
        assert model.n_seen == 30
        assert model.window == 50
        assert sidecar["entity_id"] == "sensor.x"
        assert sidecar["detector"] == "hst"
        assert sidecar["n_seen"] == 30
        assert sidecar["river_version"]
        assert sidecar["saved_at"]

    def test_load_checkpoint_returns_none_when_absent(self, tmp_path):
        store = ModelStore(root=tmp_path)
        assert store.load_checkpoint("sensor_missing", "hst") is None

    def test_load_checkpoint_returns_none_on_river_version_mismatch(self, tmp_path):
        det = EntityDetector()
        det.score_one(1.0)
        store = ModelStore(root=tmp_path)
        store.save_checkpoint("sensor_y", "hst", det, "sensor.y", det.n_seen)

        sidecar_path = tmp_path / "sensor_y" / "hst" / "checkpoint.json"
        sidecar = json.loads(sidecar_path.read_text())
        sidecar["river_version"] = "0.0.0-bogus"
        sidecar_path.write_text(json.dumps(sidecar))

        assert store.load_checkpoint("sensor_y", "hst") is None


class TestLoadAllIntoCheckpointPass:
    def test_truncated_checkpoint_does_not_block_other_entities(self, tmp_path):
        det_a = EntityDetector()
        det_a.score_one(1.0)
        det_b = EntityDetector()
        det_b.score_one(2.0)

        store = ModelStore(root=tmp_path)
        store.save_checkpoint("sensor_a", "hst", det_a, "sensor.a", det_a.n_seen)
        store.save_checkpoint("sensor_b", "hst", det_b, "sensor.b", det_b.n_seen)

        # Truncate sensor_a's checkpoint.pkl to 100 bytes.
        pkl_path = tmp_path / "sensor_a" / "hst" / "checkpoint.pkl"
        with open(pkl_path, "rb") as f:
            truncated = f.read(100)
        pkl_path.write_bytes(truncated)

        registry = DetectorRegistry()
        store.load_all_into(registry)  # must not raise

        assert registry.has_model("sensor.b", "hst")

    def test_checkpoint_wins_over_stale_versioned_model(self, tmp_path):
        """D-01/Pitfall 2: when both a versioned 'latest' model and a checkpoint
        exist for the same (slug, detector), the checkpoint's n_seen wins —
        asserted through the restored n_seen, not through call order."""
        store = ModelStore(root=tmp_path)

        stale_model = anomaly.HalfSpaceTrees(n_trees=5, height=4, window_size=10, seed=42)
        store.save_river("sensor_c", "hst", 1, stale_model, entity_id="sensor.c")

        det = EntityDetector()
        for _ in range(7):
            det.score_one(3.0)
        store.save_checkpoint("sensor_c", "hst", det, "sensor.c", det.n_seen)

        registry = DetectorRegistry()
        store.load_all_into(registry)

        loaded = registry.get_model("sensor.c", "hst")
        assert loaded.n_seen == 7
