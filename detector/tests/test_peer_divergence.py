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
