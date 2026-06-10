"""
Tests for StlDetector (stateless STL residual batch scorer).

Verifies:
- Insufficient-history guard fires when len(values) < 2*period
- All-constant input returns [0.0]*n with no error
- Step-change input produces non-zero residual scores (FAULT-03)
- Default period is 1440 (daily for 60s sensors)
- StlDetector has no fit() method (stateless)
"""

import pytest

from argus_detector.stl_detector import StlDetector


class TestStlDetectorGuard:
    def test_guard_fires_at_exactly_one_period(self):
        """1440 values with period=1440 → error (1440 < 2*1440=2880)."""
        det = StlDetector()
        scores, error = det.score_batch([1.0] * 1440, period=1440)
        assert scores == []
        assert error is not None
        assert "insufficient history" in error
        assert "1440" in error  # got
        assert "2880" in error  # need

    def test_guard_fires_below_minimum(self):
        """Any count below 2*period → error returned."""
        det = StlDetector()
        scores, error = det.score_batch([1.0] * 100, period=1440)
        assert scores == []
        assert error is not None
        assert "insufficient history" in error

    def test_guard_does_not_fire_at_minimum(self):
        """Exactly 2*period values → no error, returns scores."""
        det = StlDetector()
        # Use a small period for speed in tests
        period = 4
        values = [1.0, 2.0, 1.5, 2.5] * 2  # exactly 2*period=8
        scores, error = det.score_batch(values, period=period)
        assert error is None
        assert isinstance(scores, list)
        assert len(scores) == 8


class TestStlDetectorAllConstant:
    def test_all_constant_returns_zero_scores(self):
        """2880 equal values → ([ 0.0]*2880, None) — zero-range guard."""
        det = StlDetector()
        values = [1.0] * 2880
        scores, error = det.score_batch(values, period=1440)
        assert error is None
        assert isinstance(scores, list)
        assert len(scores) == 2880
        assert all(s == 0.0 for s in scores)

    def test_all_constant_small_period_zero_scores(self):
        """All-constant data with small period → zero scores, no error."""
        det = StlDetector()
        period = 4
        values = [5.0] * (2 * period)
        scores, error = det.score_batch(values, period=period)
        assert error is None
        assert all(s == 0.0 for s in scores)


class TestStlDetectorStepChange:
    def test_step_change_produces_nonzero_scores(self):
        """Step change detectable via STL residual (FAULT-03).

        Construct values = [0.0]*1440 + [1.0]*1440 (level shift at midpoint).
        After STL decomposition, at least one residual near the step boundary
        must be > 0, confirming STL detects level shifts.
        """
        det = StlDetector()
        values = [0.0] * 1440 + [1.0] * 1440  # 2880 points, step at index 1440
        scores, error = det.score_batch(values, period=1440)

        assert error is None
        assert isinstance(scores, list)
        assert len(scores) == 2880
        # All scores in [0.0, 1.0] (normalised)
        assert all(0.0 <= s <= 1.0 for s in scores), "Scores must be in [0, 1]"
        # At least one score > 0 — STL residual detects the level shift
        assert max(scores) > 0, "Expected non-zero residual near step boundary"

    def test_step_change_scores_normalised(self):
        """Step-change scores are normalised to [0, 1]."""
        det = StlDetector()
        values = [0.0] * 1440 + [2.0] * 1440
        scores, error = det.score_batch(values, period=1440)
        assert error is None
        assert min(scores) >= 0.0
        assert max(scores) <= 1.0


class TestStlDetectorStateless:
    def test_no_fit_method(self):
        """StlDetector is stateless — calling fit raises AttributeError."""
        det = StlDetector()
        with pytest.raises(AttributeError):
            det.fit([1.0, 2.0, 3.0])  # type: ignore[attr-defined]

    def test_default_period_is_1440(self):
        """Default period=1440 matches daily seasonality for 60s sensors.

        With only 1440 values and default period, the guard must fire
        (confirming the default period is 1440, not something smaller).
        """
        det = StlDetector()
        scores, error = det.score_batch([1.0] * 1440)
        assert scores == []
        assert error is not None
        assert "2880" in error  # need >= 2*1440
