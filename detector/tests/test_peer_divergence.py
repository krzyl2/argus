"""
Tests for PeerDivergenceDetector (stateless robust cross-member scorer).

Verifies:
- Outlier member flagged, consensus members not flagged (GRP-03)
- Below-floor (<3 members) returns explicit no-verdict error tuple (GRP-04)
- MAD=0 with meanAD>0 flags the outlier via the meanAD fallback constant
- All-identical row returns zeros, never NaN, no RuntimeWarning
"""

import warnings

import pytest

from argus_detector.group.peer_divergence import PeerDivergenceDetector


class TestPeerDivergenceScoring:
    def test_outlier_member_flagged_consensus_not(self):
        """One member clearly diverges at a timestamp; only it is flagged."""
        det = PeerDivergenceDetector()
        matrix = [[10.0, 10.5, 9.8, 50.0]]
        scores, flags, error = det.score_batch(matrix)

        assert error is None
        assert len(scores) == 1
        assert len(flags) == 1
        row_flags = flags[0]
        # Outlier (index 3) flagged, consensus members (0,1,2) not flagged
        assert row_flags[3] is True
        assert row_flags[0] is False
        assert row_flags[1] is False
        assert row_flags[2] is False

    def test_scores_and_flags_shape_matches_matrix(self):
        """scores/flags both shape (n_timestamps, n_members)."""
        det = PeerDivergenceDetector()
        matrix = [
            [10.0, 10.5, 9.8, 50.0],
            [11.0, 11.2, 10.9, 11.1],
        ]
        scores, flags, error = det.score_batch(matrix)

        assert error is None
        assert len(scores) == 2
        assert len(flags) == 2
        assert all(len(row) == 4 for row in scores)
        assert all(len(row) == 4 for row in flags)

    def test_no_divergence_row_not_flagged(self):
        """A timestamp where all members are close together — no flags."""
        det = PeerDivergenceDetector()
        matrix = [[21.0, 21.1, 20.9, 21.2]]
        scores, flags, error = det.score_batch(matrix)

        assert error is None
        assert all(f is False for f in flags[0])


class TestPeerDivergenceFloor:
    def test_below_floor_returns_no_verdict(self):
        """Fewer than 3 members → explicit no-verdict error tuple (GRP-04)."""
        det = PeerDivergenceDetector()
        scores, flags, error = det.score_batch([[10.0, 10.0]])

        assert scores is None
        assert flags is None
        assert error is not None
        assert "insufficient members" in error

    def test_single_member_returns_no_verdict(self):
        det = PeerDivergenceDetector()
        scores, flags, error = det.score_batch([[10.0]])

        assert scores is None
        assert flags is None
        assert error is not None

    def test_exactly_min_members_returns_verdict(self):
        """Exactly 3 members (the floor) → verdict is produced, not no-verdict."""
        det = PeerDivergenceDetector()
        scores, flags, error = det.score_batch([[10.0, 10.5, 9.8]])

        assert error is None
        assert scores is not None
        assert flags is not None


class TestPeerDivergenceEdgeCases:
    def test_mad_zero_meanad_fallback_flags_outlier(self):
        """MAD=0 but meanAD>0 (hand-verified RESEARCH fixture [10,10,10,50]).

        Expected z for the outlier (50.0) is ~3.1916 via the meanAD fallback
        (0.7979 constant) — numerically verified in 05-RESEARCH.md.
        """
        det = PeerDivergenceDetector()
        with warnings.catch_warnings():
            warnings.simplefilter("error", RuntimeWarning)
            scores, flags, error = det.score_batch([[10.0, 10.0, 10.0, 50.0]])

        assert error is None
        row = scores[0]
        assert row[0] == pytest.approx(0.0)
        assert row[1] == pytest.approx(0.0)
        assert row[2] == pytest.approx(0.0)
        assert row[3] == pytest.approx(3.1916, abs=1e-3)

    def test_all_identical_returns_zeros_not_nan(self):
        """All-identical row (MAD=0 and meanAD=0) → concrete zeros, never NaN."""
        det = PeerDivergenceDetector()
        with warnings.catch_warnings():
            warnings.simplefilter("error", RuntimeWarning)
            scores, flags, error = det.score_batch([[10.0, 10.0, 10.0]])

        assert error is None
        assert scores[0] == [0.0, 0.0, 0.0]
        assert all(s == s for s in scores[0])  # not NaN (NaN != NaN)
        assert all(f is False for f in flags[0])

    def test_no_runtime_warning_on_mad_zero_path(self):
        """MAD=0 path must never raise/emit a divide-by-zero RuntimeWarning."""
        det = PeerDivergenceDetector()
        with warnings.catch_warnings():
            warnings.simplefilter("error", RuntimeWarning)
            # Should not raise — guard prevents divide-by-zero
            det.score_batch([[10.0, 10.0, 10.0, 50.0]])
            det.score_batch([[5.0, 5.0, 5.0]])


class TestPeerDivergenceFromParams:
    """ALGO-01/02: from_params() makes the flag threshold a genuine, tunable knob."""

    # Borderline member (index 3) has z ~= 4.34 on this fixture — flagged at a
    # lower threshold (2.5), not flagged at a higher one (4.5): same data,
    # different verdict (hand-verified via modified_zscore).
    _BORDERLINE_MATRIX = [[10.0, 10.5, 9.8, 12.5]]

    def test_lower_threshold_flags_more_members(self):
        """Same fixture, lower threshold -> more members flagged."""
        low = PeerDivergenceDetector.from_params({"threshold": "2.5"})
        high = PeerDivergenceDetector.from_params({"threshold": "4.5"})

        _, low_flags, error_low = low.score_batch(self._BORDERLINE_MATRIX)
        _, high_flags, error_high = high.score_batch(self._BORDERLINE_MATRIX)

        assert error_low is None
        assert error_high is None
        low_count = sum(1 for f in low_flags[0] if f)
        high_count = sum(1 for f in high_flags[0] if f)
        assert low_count > high_count

    def test_from_params_empty_uses_default_threshold(self):
        """from_params({}) must match the pre-change hardcoded-3.5 behavior
        (regression guard) — identical flags to a bare PeerDivergenceDetector()."""
        default_det = PeerDivergenceDetector()
        from_params_det = PeerDivergenceDetector.from_params({})

        matrix = [[10.0, 10.5, 9.8, 50.0]]
        _, default_flags, _ = default_det.score_batch(matrix)
        _, from_params_flags, _ = from_params_det.score_batch(matrix)

        assert from_params_flags == default_flags

    def test_from_params_non_numeric_threshold_falls_back_to_default(self):
        """Malformed threshold value must not raise — falls back to 3.5."""
        det = PeerDivergenceDetector.from_params({"threshold": "not-a-number"})
        default_det = PeerDivergenceDetector()

        matrix = [[10.0, 10.5, 9.8, 50.0]]
        _, flags, error = det.score_batch(matrix)
        _, default_flags, _ = default_det.score_batch(matrix)

        assert error is None
        assert flags == default_flags
