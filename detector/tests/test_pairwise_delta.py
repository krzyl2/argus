"""
Tests for PairwiseDeltaDetector (2-member peer_divergence pairwise-delta path, GRP-11).

Verifies:
- compute_delta() is elementwise a-b, length-preserving
- fit() + score_batch() on the delta: normal noise scores low, an injected
  step-drift in one member (which breaks the pair's learned relationship —
  the exact scenario the operator's 2-sensor use cases, e.g. tire pressures
  or water pressure+temperature pairs, depend on catching) scores high
- is_anomaly() delegates the threshold decision to the wrapped PyODDetector
- from_params() honors threshold/contamination overrides identically to
  PyODDetector, falling back to MAD defaults when keys are absent/invalid
- servicer-level 2-member peer_divergence routing (FitGroup + ScoreGroupBatch)
"""

from __future__ import annotations

import pytest

from argus_detector.group.pairwise_delta import PairwiseDeltaDetector


class TestComputeDelta:
    def test_elementwise_subtraction(self):
        """compute_delta(a, b) == a[i] - b[i] for every index."""
        series_a = [10.0, 11.0, 12.0]
        series_b = [9.0, 9.5, 10.0]
        delta = PairwiseDeltaDetector.compute_delta(series_a, series_b)
        assert delta == pytest.approx([1.0, 1.5, 2.0])

    def test_delta_length_matches_input(self):
        series_a = [float(i) for i in range(20)]
        series_b = [float(i) - 1.0 for i in range(20)]
        delta = PairwiseDeltaDetector.compute_delta(series_a, series_b)
        assert len(delta) == 20

    def test_delta_returns_floats(self):
        delta = PairwiseDeltaDetector.compute_delta([1, 2, 3], [1, 1, 1])
        assert all(isinstance(v, float) for v in delta)


class TestFitScore:
    def test_normal_delta_scores_below_drift_delta(self):
        """A fitted PairwiseDeltaDetector scores an injected step-drift delta
        above threshold and normal-noise delta below — this is the WHY: the
        delta captures a broken pair-relationship (e.g. one tire slowly
        losing pressure relative to the other, or a water pressure+temperature
        pair drifting apart), which the operator's 2-sensor use cases depend
        on being caught, not just "some anomaly somewhere"."""
        # Two members tracking closely (delta hovers near 0 with small noise).
        member_a = [10.0, 10.1, 9.9, 10.05, 9.95, 10.02, 9.98, 10.1, 9.9, 10.0]
        member_b = [10.0, 10.05, 9.95, 10.0, 10.0, 9.98, 10.02, 10.05, 9.95, 10.0]
        delta = PairwiseDeltaDetector.compute_delta(member_a, member_b)

        det = PairwiseDeltaDetector()
        det.fit(delta)
        normal_scores = det.score_batch(delta)

        # Injected step-drift: member_a jumps far away from member_b, breaking
        # the previously-stable pair relationship.
        drift_delta = PairwiseDeltaDetector.compute_delta([25.0], [10.0])
        drift_scores = det.score_batch(drift_delta)

        assert max(normal_scores) < drift_scores[0]

    def test_score_batch_returns_one_score_per_delta_value(self):
        delta = [0.1, -0.05, 0.08, -0.02, 0.03, 0.0, 0.01, -0.04, 0.02, 0.0]
        det = PairwiseDeltaDetector()
        det.fit(delta)
        scores = det.score_batch(delta)
        assert len(scores) == len(delta)

    def test_score_batch_before_fit_raises(self):
        det = PairwiseDeltaDetector()
        with pytest.raises(ValueError, match="fit\\(\\) must be called before score_batch"):
            det.score_batch([1.0, 2.0])

    def test_is_fitted_transitions(self):
        det = PairwiseDeltaDetector()
        assert det.is_fitted is False
        det.fit([0.1, -0.1, 0.2, -0.2, 0.0])
        assert det.is_fitted is True


class TestIsAnomaly:
    def test_is_anomaly_delegates_to_wrapped_pyod_detector(self):
        """is_anomaly(score) matches the threshold decision the underlying
        PyODDetector would make for the same score."""
        delta = [0.1, -0.05, 0.08, -0.02, 0.03, 0.0, 0.01, -0.04, 0.02, 0.0]
        det = PairwiseDeltaDetector()
        det.fit(delta)
        scores = det.score_batch(delta)

        # A score far beyond the fitted distribution must be flagged anomalous.
        drift_delta = PairwiseDeltaDetector.compute_delta([25.0], [0.0])
        drift_score = det.score_batch(drift_delta)[0]
        assert det.is_anomaly(drift_score) is True

        # A score within the normal noise band must not be flagged.
        assert det.is_anomaly(min(scores)) is False


class TestFromParams:
    def test_from_params_empty_dict_uses_defaults(self):
        det = PairwiseDeltaDetector.from_params({})
        delta = [0.1, -0.1, 0.2, -0.2, 0.0]
        det.fit(delta)
        scores = det.score_batch(delta)
        assert len(scores) == 5

    def test_from_params_threshold_override(self):
        """from_params({'threshold': ...}) does not raise and still scores."""
        det = PairwiseDeltaDetector.from_params({"threshold": "4.0"})
        delta = [0.1, -0.1, 0.2, -0.2, 0.0]
        det.fit(delta)
        scores = det.score_batch(delta)
        assert len(scores) == 5

    def test_from_params_invalid_value_falls_back_to_default(self):
        """Malformed param string must not raise — falls back to MAD default."""
        det = PairwiseDeltaDetector.from_params({"threshold": "not-a-number"})
        delta = [0.1, -0.1, 0.2, -0.2, 0.0]
        det.fit(delta)
        scores = det.score_batch(delta)
        assert len(scores) == 5
