"""
Tests for DetectorRegistry per-entity isolation and batch methods.

T-06-01: per-entity state is isolated (scoring sensor.a does not advance sensor.b's n_seen).
MDL-04: fit_one trains outside entity lock; score_batch reads ref under lock.
"""
import threading

import pytest

from argus_detector.registry import DetectorRegistry


class TestRegistryPerEntityIsolation:
    """Each entity_id gets its own independent EntityDetector instance."""

    def test_two_entities_have_independent_state(self):
        """Scoring sensor.a must not advance sensor.b's n_seen counter."""
        registry = DetectorRegistry()

        # Score sensor.a 10 times
        for _ in range(10):
            registry.score_one("sensor.a", 21.0)

        # Access sensor.b for the first time — it must have n_seen=1 after this call
        registry.score_one("sensor.b", 21.0)

        # Retrieve the internal EntityDetector instances
        det_a = registry._detectors[("sensor.a", "hst")]
        det_b = registry._detectors[("sensor.b", "hst")]

        assert det_a._n_seen == 10, f"sensor.a n_seen={det_a._n_seen}, expected 10"
        assert det_b._n_seen == 1, f"sensor.b n_seen={det_b._n_seen}, expected 1"
        assert det_a is not det_b, "sensor.a and sensor.b share the same EntityDetector instance"

    def test_score_one_returns_float(self):
        registry = DetectorRegistry()
        result = registry.score_one("sensor.test", 21.0)
        assert isinstance(result, float)

    def test_lazy_creation_on_first_call(self):
        registry = DetectorRegistry()
        assert len(registry._detectors) == 0
        registry.score_one("sensor.new", 21.0)
        assert ("sensor.new", "hst") in registry._detectors

    def test_is_warmed_up_delegates_to_entity_detector(self):
        registry = DetectorRegistry()
        # Before any call: entity doesn't exist yet
        # After one call: n_seen=1, not warmed up (default window=250)
        registry.score_one("sensor.x", 21.0)
        assert not registry.is_warmed_up("sensor.x")

    def test_custom_params_propagated(self):
        """score_one with params overrides creates a detector with those params."""
        registry = DetectorRegistry()
        # Use small window so we can exhaust it quickly
        for _ in range(50):
            registry.score_one("sensor.p", 21.0, params={"window": "50", "n_trees": "5"})
        det = registry._detectors[("sensor.p", "hst")]
        assert det.is_warmed_up
        assert det._model.window_size == 50


class TestRegistryFitOne:
    """fit_one trains a model and makes has_model return True (MDL-04)."""

    def test_fit_one_makes_has_model_true(self):
        """After fit_one, has_model returns True for that (entity_id, detector)."""
        registry = DetectorRegistry()
        assert not registry.has_model("sensor.test", "mad")
        registry.fit_one("sensor.test", "mad", [1.0] * 10)
        assert registry.has_model("sensor.test", "mad")

    def test_fit_one_model_can_score(self):
        """After fit_one, score_batch succeeds (no NotFittedError)."""
        registry = DetectorRegistry()
        registry.fit_one("sensor.test", "mad", [1.0] * 10)
        scores, error = registry.score_batch("sensor.test", "mad", [1.0] * 5)
        assert error is None
        assert len(scores) == 5
        assert all(isinstance(s, float) for s in scores)

    def test_fit_one_robust_zscore_alias(self):
        """fit_one with 'robust_zscore' creates a PyODDetector (alias for MAD)."""
        registry = DetectorRegistry()
        registry.fit_one("sensor.test", "robust_zscore", [1.0] * 10)
        assert registry.has_model("sensor.test", "robust_zscore")

    def test_fit_one_concurrent_no_exception(self):
        """Concurrent fit_one and score_batch for same entity must not raise or deadlock.

        MDL-04: training runs outside lock; scoring holds a snapshot ref.
        """
        registry = DetectorRegistry()
        registry.fit_one("sensor.c", "mad", [float(i) for i in range(20)])

        errors = []

        def do_fit():
            try:
                registry.fit_one("sensor.c", "mad", [float(i) for i in range(30)])
            except Exception as e:
                errors.append(e)

        def do_score():
            try:
                registry.score_batch("sensor.c", "mad", [1.0] * 5)
            except Exception as e:
                errors.append(e)

        t1 = threading.Thread(target=do_fit)
        t2 = threading.Thread(target=do_score)
        t1.start()
        t2.start()
        t1.join(timeout=10)
        t2.join(timeout=10)

        assert not errors, f"Concurrent fit/score raised: {errors}"


class TestRegistryScoreBatch:
    """score_batch returns (scores, error); raises ValueError when no model exists."""

    def test_score_batch_no_model_raises_value_error(self):
        """score_batch with no prior fit_one raises ValueError (cold-start is servicer's job)."""
        registry = DetectorRegistry()
        with pytest.raises(ValueError, match="No model"):
            registry.score_batch("sensor.missing", "mad", [1.0] * 5)

    def test_score_batch_stl_insufficient_history_returns_error_string(self):
        """score_batch on StlDetector returns ([], error_string) when values insufficient."""
        registry = DetectorRegistry()
        # Inject a StlDetector directly (no fit_one needed for stateless STL)
        from argus_detector.stl_detector import StlDetector
        registry.register("sensor.stl", "stl", StlDetector())
        scores, error = registry.score_batch("sensor.stl", "stl", [1.0] * 5)
        assert scores == []
        assert error is not None
        assert "insufficient" in error

    def test_score_batch_pyod_returns_list_none(self):
        """score_batch on PyODDetector returns (list[float], None)."""
        registry = DetectorRegistry()
        registry.fit_one("sensor.x", "mad", [float(i) for i in range(20)])
        scores, error = registry.score_batch("sensor.x", "mad", [1.0, 2.0, 3.0])
        assert error is None
        assert len(scores) == 3


class TestRegistryCreateDetector:
    """_create_detector maps detector names to correct classes."""

    def test_create_detector_mad(self):
        """'mad' -> PyODDetector instance."""
        registry = DetectorRegistry()
        det = registry._create_detector("mad")
        from argus_detector.pyod_detector import PyODDetector
        assert isinstance(det, PyODDetector)

    def test_create_detector_robust_zscore_alias(self):
        """'robust_zscore' -> PyODDetector instance (same as 'mad')."""
        registry = DetectorRegistry()
        det = registry._create_detector("robust_zscore")
        from argus_detector.pyod_detector import PyODDetector
        assert isinstance(det, PyODDetector)

    def test_create_detector_stl(self):
        """'stl' -> StlDetector instance."""
        registry = DetectorRegistry()
        det = registry._create_detector("stl")
        from argus_detector.stl_detector import StlDetector
        assert isinstance(det, StlDetector)

    def test_create_detector_hst(self):
        """'hst' -> EntityDetector instance."""
        registry = DetectorRegistry()
        det = registry._create_detector("hst")
        from argus_detector.hst_detector import EntityDetector
        assert isinstance(det, EntityDetector)

    def test_create_detector_unknown_raises(self):
        """Unknown detector name raises ValueError."""
        registry = DetectorRegistry()
        with pytest.raises(ValueError, match="Unknown detector"):
            registry._create_detector("nonexistent_detector")


class TestRegistryRegister:
    """register() allows direct model injection (used by ModelStore.load_all_into)."""

    def test_register_sets_has_model_true(self):
        """After register(), has_model returns True."""
        from argus_detector.pyod_detector import PyODDetector
        registry = DetectorRegistry()
        model = PyODDetector()
        registry.register("sensor.test", "hst", model)
        assert registry.has_model("sensor.test", "hst")

    def test_register_injected_model_is_used(self):
        """score_batch uses the injected model (not a new one)."""
        from argus_detector.pyod_detector import PyODDetector
        registry = DetectorRegistry()
        model = PyODDetector()
        model.fit([float(i) for i in range(15)])
        registry.register("sensor.r", "mad", model)
        # score_batch should succeed because model is already fitted
        scores, error = registry.score_batch("sensor.r", "mad", [1.0, 2.0])
        assert error is None
        assert len(scores) == 2
