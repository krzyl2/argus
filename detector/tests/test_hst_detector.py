"""
Tests for EntityDetector (River HalfSpaceTrees + online min-max normalization).

RED phase: these tests MUST fail before implementation.
GREEN phase: all pass after hst_detector.py is implemented.

FAULT-01: point spike after warm-up scores strictly higher than stable-window baseline.
PITFALL 8: is_warmed_up tracks window_size readings.
CONF-02: window/n_trees params overridable from string params map.
"""
import statistics

import pytest

from argus_detector.hst_detector import EntityDetector


class TestEntityDetectorWarmUp:
    """is_warmed_up tracks n_seen vs window_size."""

    def test_not_warmed_up_before_window_size(self):
        det = EntityDetector(window=50, n_trees=10)
        for i in range(49):
            det.score_one(21.0 + i * 0.01)
        assert not det.is_warmed_up

    def test_warmed_up_at_window_size(self):
        det = EntityDetector(window=50, n_trees=10)
        for i in range(50):
            det.score_one(21.0 + i * 0.01)
        assert det.is_warmed_up

    def test_warmed_up_after_window_size(self):
        det = EntityDetector(window=50, n_trees=10)
        for i in range(60):
            det.score_one(21.0 + i * 0.01)
        assert det.is_warmed_up


class TestEntityDetectorSpike:
    """FAULT-01: a point spike after warm-up scores above the stable baseline."""

    def test_spike_scores_above_baseline_median(self):
        """Feed 300 stable readings (~21.0 + tiny noise), then a spike at 99.0.

        The spike score must be strictly greater than the median of the last
        window's stable scores. We use a small window=50 to keep the test fast.
        """
        det = EntityDetector(window=50, n_trees=10)
        import random
        rng = random.Random(0)

        stable_scores = []
        # Fill well past warm-up to build a stable model
        n_stable = 300
        for _ in range(n_stable):
            v = 21.0 + rng.gauss(0, 0.05)
            s = det.score_one(v)
            stable_scores.append(s)

        # Capture median of scores from the last window (model is warm)
        baseline_median = statistics.median(stable_scores[-50:])

        spike_score = det.score_one(99.0)

        assert spike_score > baseline_median, (
            f"Spike score {spike_score:.4f} not above baseline median {baseline_median:.4f}"
        )


class TestEntityDetectorParamsOverride:
    """CONF-02: window and n_trees are overridable from a string params map."""

    def test_from_params_sets_window_size(self):
        det = EntityDetector.from_params({"window": "50", "n_trees": "10"})
        # Exhaust window to confirm window_size=50
        for i in range(50):
            det.score_one(21.0)
        assert det.is_warmed_up
        # Would NOT be warmed up if window was the default 250
        assert det._model.window_size == 50

    def test_from_params_uses_defaults_when_absent(self):
        det = EntityDetector.from_params({})
        assert det._model.window_size == 250
        assert det._model.n_trees == 25

    def test_from_params_n_trees_override(self):
        det = EntityDetector.from_params({"n_trees": "5"})
        assert det._model.n_trees == 5
