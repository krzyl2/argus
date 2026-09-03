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
import threading
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

    def test_rmad_pickle_size_and_deepcopy_latency(self):
        """checkpoint_dirty deep-copies UNDER the per-entity lock (D-06), so the
        deepcopy cost is latency added to the hot ScoreStream path once per
        checkpoint tick. HST measures 200 KB-1.2 MB and 56-96 ms; rmad's two
        flat containers measure ~13 KB and ~0.3 ms, i.e. ~250x less exposure to
        the unresolved torn-snapshot race."""
        from argus_detector.rmad_detector import RmadDetector

        det = RmadDetector()
        for i in range(2000):
            det.score_one(20.0 + (i % 37) * 0.1)

        size = len(pickle.dumps(det))
        print(f"MEASURED rmad pickle size: {size} bytes")
        assert size < 32768

        t0 = time.perf_counter()
        copy.deepcopy(det)
        elapsed_ms = (time.perf_counter() - t0) * 1000
        print(f"MEASURED rmad deepcopy latency: {elapsed_ms:.2f} ms")
        assert elapsed_ms < 5.0


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


class TestGetWarmupState:
    """RESEARCH.md Pitfall 4: DetectorRegistry.get_warmup_state (Task 2)."""

    def test_unknown_entity_returns_false_zero_zero(self):
        registry = DetectorRegistry()
        assert registry.get_warmup_state("sensor.unknown") == (False, 0, 0)

    def test_returns_actual_configured_window(self):
        registry = DetectorRegistry()
        for _ in range(3):
            registry.score_one("sensor.p", 21.0, params={"window": "3", "n_trees": "5"})
        warmed_up, n_seen, window = registry.get_warmup_state("sensor.p")
        assert n_seen == 3
        assert window == 3
        assert warmed_up is True

    def test_default_window_reported_when_no_params(self):
        registry = DetectorRegistry()
        registry.score_one("sensor.q", 21.0)
        warmed_up, n_seen, window = registry.get_warmup_state("sensor.q")
        assert n_seen == 1
        assert window == 250
        assert warmed_up is False


class TestCheckpointDirty:
    """D-05/D-06: dirty-tracked checkpoint sweep as a DetectorRegistry method (Task 2)."""

    def test_dirty_entity_writes_once_then_idle_writes_nothing(self, tmp_path):
        registry = DetectorRegistry()
        store = ModelStore(root=tmp_path)

        for _ in range(5):
            registry.score_one("sensor.dirty", 21.0)

        count1 = registry.checkpoint_dirty(store)
        assert count1 == 1
        pkl_path = tmp_path / "sensor_dirty" / "hst" / "checkpoint.pkl"
        assert pkl_path.exists()
        mtime1 = pkl_path.stat().st_mtime_ns

        count2 = registry.checkpoint_dirty(store)
        assert count2 == 0
        assert pkl_path.stat().st_mtime_ns == mtime1

    def test_only_hst_entities_are_checkpointed(self, tmp_path):
        registry = DetectorRegistry()
        store = ModelStore(root=tmp_path)

        registry.score_one("sensor.h", 21.0)  # hst
        registry.fit_one("sensor.m", "mad", [1.0] * 10)  # mad — must be skipped

        registry.checkpoint_dirty(store)

        assert (tmp_path / "sensor_h" / "hst" / "checkpoint.pkl").exists()
        assert not (tmp_path / "sensor_m" / "mad").exists()

    def test_rmad_entities_are_checkpointed_and_mad_is_still_skipped(self, tmp_path):
        """rmad holds a 720-sample rolling window that is rebuilt one reading at
        a time, so a restart without a checkpoint costs up to ~6.5 h of silence
        on a 225-samples/day sensor. The batch detectors are refit from history
        and must stay out of the sweep. The hst and rmad checkpoint directories
        are disjoint, which is what makes a rollback to hst free (D-F)."""
        registry = DetectorRegistry()
        store = ModelStore(root=tmp_path)

        registry.score_one("sensor.r", 21.0, detector="rmad")
        registry.score_one("sensor.r", 21.0, detector="hst")
        registry.fit_one("sensor.m", "mad", [1.0] * 10)  # batch — must be skipped

        registry.checkpoint_dirty(store)

        assert (tmp_path / "sensor_r" / "rmad" / "checkpoint.pkl").exists()
        assert (tmp_path / "sensor_r" / "hst" / "checkpoint.pkl").exists()
        assert not (tmp_path / "sensor_m" / "mad").exists()

    def test_failing_write_does_not_block_other_entities_or_advance_baseline(self, tmp_path):
        registry = DetectorRegistry()
        registry.score_one("sensor.fail", 21.0)
        registry.score_one("sensor.ok", 21.0)

        calls = []

        class FailingFirstStore:
            def save_checkpoint(self, slug, detector, model, entity_id, n_seen):
                calls.append(entity_id)
                if entity_id == "sensor.fail":
                    raise OSError("disk full")

        count = registry.checkpoint_dirty(FailingFirstStore())
        assert count == 1
        assert set(calls) == {"sensor.fail", "sensor.ok"}
        # The failing entity's baseline must not have advanced.
        assert registry._last_checkpointed.get(("sensor.fail", "hst")) is None

    def test_no_lock_held_across_file_io(self, tmp_path):
        """T-15-01-04: while checkpoint_dirty is writing entity A, a concurrent
        score_one on entity A must not be blocked."""
        registry = DetectorRegistry()
        registry.score_one("sensor.slow", 21.0)

        write_started = threading.Event()
        release_write = threading.Event()

        class BlockingStore:
            def save_checkpoint(self, slug, detector, model, entity_id, n_seen):
                write_started.set()
                release_write.wait(timeout=5)

        t = threading.Thread(target=registry.checkpoint_dirty, args=(BlockingStore(),))
        t.start()
        assert write_started.wait(timeout=5), "checkpoint_dirty never started its write"

        # score_one must complete promptly even though the write is still blocked.
        done = threading.Event()

        def do_score():
            registry.score_one("sensor.slow", 22.0)
            done.set()

        t2 = threading.Thread(target=do_score)
        t2.start()
        assert done.wait(timeout=2), "score_one was blocked by the in-flight checkpoint write"

        release_write.set()
        t.join(timeout=5)


class TestDetectorConfigCheckpointKnobs:
    """D-05: ARGUS_CHECKPOINT_* are detector-side; ARGUS_BACKFILL_* are not
    (RESEARCH.md Pitfall 3) (Task 3)."""

    def test_defaults(self, monkeypatch):
        monkeypatch.delenv("ARGUS_CHECKPOINT_INTERVAL_SEC", raising=False)
        monkeypatch.delenv("ARGUS_CHECKPOINT_ENABLED", raising=False)
        from argus_detector.config import DetectorConfig

        cfg = DetectorConfig()
        assert cfg.checkpoint_interval_sec == 300
        assert cfg.checkpoint_enabled is True

    def test_interval_zero_disables(self, monkeypatch):
        monkeypatch.setenv("ARGUS_CHECKPOINT_INTERVAL_SEC", "0")
        from argus_detector.config import DetectorConfig

        cfg = DetectorConfig()
        assert cfg.checkpoint_interval_sec == 0

    def test_enabled_false(self, monkeypatch):
        monkeypatch.setenv("ARGUS_CHECKPOINT_ENABLED", "false")
        from argus_detector.config import DetectorConfig

        cfg = DetectorConfig()
        assert cfg.checkpoint_enabled is False

    def test_backfill_knobs_stay_orchestrator_side(self):
        from argus_detector.config import DetectorConfig

        cfg = DetectorConfig()
        assert hasattr(cfg, "backfill_enabled") is False
        assert hasattr(cfg, "backfill_lookback") is False


class TestCheckpointWriter:
    """D-05/D-08: interval thread + synchronous flush (Task 3)."""

    def test_zero_interval_start_is_noop(self):
        from argus_detector.checkpoint_writer import CheckpointWriter

        writer = CheckpointWriter(registry=object(), model_store=object(), interval_sec=0)
        writer.start()
        assert writer.is_running is False

    def test_ticks_at_least_twice_within_bounded_wait_and_stops_promptly(self):
        from argus_detector.checkpoint_writer import CheckpointWriter

        calls = []

        class FakeRegistry:
            def checkpoint_dirty(self, model_store):
                calls.append(1)
                return 0

        writer = CheckpointWriter(registry=FakeRegistry(), model_store=object(), interval_sec=0.05)
        writer.start()
        deadline = time.time() + 2
        while time.time() < deadline and len(calls) < 2:
            time.sleep(0.02)
        assert len(calls) >= 2

        t0 = time.perf_counter()
        writer.stop()
        assert (time.perf_counter() - t0) < 1.0

    def test_flush_calls_checkpoint_dirty_synchronously_without_starting_thread(self):
        from argus_detector.checkpoint_writer import CheckpointWriter

        class FakeRegistry:
            def checkpoint_dirty(self, model_store):
                return 3

        writer = CheckpointWriter(registry=FakeRegistry(), model_store=object(), interval_sec=0)
        assert writer.flush() == 3
        assert writer.is_running is False

    def test_tick_exception_does_not_kill_thread(self):
        from argus_detector.checkpoint_writer import CheckpointWriter

        calls = []

        class FlakyRegistry:
            def checkpoint_dirty(self, model_store):
                calls.append(1)
                if len(calls) == 1:
                    raise RuntimeError("boom")
                return 0

        writer = CheckpointWriter(registry=FlakyRegistry(), model_store=object(), interval_sec=0.05)
        writer.start()
        deadline = time.time() + 2
        while time.time() < deadline and len(calls) < 2:
            time.sleep(0.02)
        writer.stop()
        assert len(calls) >= 2  # thread survived the first tick's exception


class TestCreateServerAttachesWriter:
    """create_server attaches the writer for test introspection; unit tests
    that only call create_server never spawn a thread (Task 3)."""

    def test_writer_attached_and_not_started(self, tmp_path):
        from argus_detector.server import create_server

        server = create_server(port=0, tls=False, model_root=tmp_path)
        writer = server._argus_checkpoint_writer
        assert writer is not None
        assert writer.is_running is False
