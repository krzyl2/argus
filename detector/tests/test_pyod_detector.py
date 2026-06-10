"""
Tests for PyODDetector (MAD-backed batch anomaly detector).

Verifies:
- fit() + score_batch() work on a float list
- score_batch before fit raises ValueError
- from_params() casts string params to float
- robust_zscore alias (via from_params) creates a working PyODDetector
- zero-variance input handled without raising
- is_fitted property transitions correctly
"""

import pytest

from argus_detector.pyod_detector import PyODDetector


class TestPyODDetectorFitScore:
    def test_fit_then_score_batch_returns_floats(self):
        """fit + score_batch on a small list → returns list of floats, no exception."""
        det = PyODDetector()
        values = [21.5, 22.0, 21.8, 22.3, 21.9, 22.1, 21.7, 22.4, 21.6, 22.2]
        det.fit(values)
        scores = det.score_batch(values)
        assert isinstance(scores, list)
        assert len(scores) == len(values)
        assert all(isinstance(s, float) for s in scores)

    def test_score_batch_before_fit_raises(self):
        """score_batch before fit → ValueError with expected message."""
        det = PyODDetector()
        with pytest.raises(ValueError, match="fit\\(\\) must be called before score_batch"):
            det.score_batch([1.0, 2.0, 3.0])

    def test_is_fitted_false_before_fit(self):
        det = PyODDetector()
        assert det.is_fitted is False

    def test_is_fitted_true_after_fit(self):
        det = PyODDetector()
        det.fit([1.0, 2.0, 3.0, 4.0, 5.0])
        assert det.is_fitted is True


class TestPyODDetectorFromParams:
    def test_from_params_threshold_cast(self):
        """from_params({'threshold': '4.0'}) → threshold=4.0."""
        det = PyODDetector.from_params({"threshold": "4.0"})
        # Verify it works by fitting and scoring; threshold affects predict() but not decision_function()
        det.fit([1.0, 2.0, 3.0, 4.0, 5.0])
        scores = det.score_batch([1.0, 2.0, 3.0, 4.0, 5.0])
        assert len(scores) == 5

    def test_from_params_robust_zscore_alias_works(self):
        """from_params({'detector': 'robust_zscore'}) → creates a working PyODDetector.

        The robust_zscore alias maps to MAD (CRITICAL FINDING — RobustZScore does not
        exist in PyOD 3.6.0; MAD is the correct implementation).
        The 'detector' key is silently ignored by from_params; it is handled at
        registry._create_detector() level (Plan 05).
        """
        det = PyODDetector.from_params({"detector": "robust_zscore"})
        assert isinstance(det, PyODDetector)
        det.fit([10.0, 11.0, 10.5, 10.8, 11.2, 10.3, 10.9, 11.1, 10.6, 10.7])
        scores = det.score_batch([10.0, 11.0, 10.5, 10.8, 11.2, 10.3, 10.9, 11.1, 10.6, 10.7])
        assert len(scores) == 10
        assert all(isinstance(s, float) for s in scores)

    def test_from_params_empty_dict_uses_defaults(self):
        det = PyODDetector.from_params({})
        det.fit([1.0, 2.0, 3.0, 4.0, 5.0])
        scores = det.score_batch([1.0, 2.0, 3.0, 4.0, 5.0])
        assert len(scores) == 5

    def test_from_params_invalid_threshold_uses_default(self):
        """Invalid string param falls back to default without raising."""
        det = PyODDetector.from_params({"threshold": "not_a_number"})
        det.fit([1.0, 2.0, 3.0, 4.0, 5.0])
        scores = det.score_batch([1.0, 2.0, 3.0, 4.0, 5.0])
        assert len(scores) == 5


class TestPyODDetectorEdgeCases:
    def test_zero_variance_input_no_raise(self):
        """All-equal values → MAD handles zero-variance without raising.

        MAD should not raise on constant input; decision_function still returns
        floats (likely all zeros or equal values).
        """
        value = 21.5
        values = [value] * 10
        det = PyODDetector()
        det.fit(values)
        scores = det.score_batch(values)
        assert isinstance(scores, list)
        assert len(scores) == 10
        assert all(isinstance(s, float) for s in scores)

    def test_score_batch_length_matches_input(self):
        """Returned scores list length equals input values length."""
        det = PyODDetector()
        values = [float(i) for i in range(1, 21)]  # 20 points
        det.fit(values)
        scores = det.score_batch(values)
        assert len(scores) == 20
