"""
Tests for DetectorRegistry per-entity isolation.

RED phase: these tests MUST fail before implementation.
GREEN phase: all pass after registry.py is updated to use EntityDetector.

T-06-01: per-entity state is isolated (scoring sensor.a does not advance sensor.b's n_seen).
"""
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
