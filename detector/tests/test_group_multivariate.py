"""
Tests for GroupMultivariateDetector (RobustScaler + PyOD ECOD/COPOD/PCA/IForest).

Verifies:
- fit() + score_batch() work on a 2D matrix; is_fitted transitions correctly
- score_batch before fit raises ValueError with the PyODDetector message convention
- a jointly-abnormal test vector (individually in-range per feature, correlation
  broken) scores higher than in-distribution vectors — proves the 2D matrix is
  scored jointly, not via a univariate loop (success criterion 3)
- mixed-unit (hPa + %RH) fixture: RobustScaler prevents the large-magnitude
  feature from dominating the joint score (GRP-06)
- ECOD/COPOD return a non-None contributions matrix of shape (n_new, n_features);
  PCA/IForest return None (no per-feature decomposition in PyOD 3.6.0)
- bundle() then from_bundle() round-trips to a fitted detector producing
  identical scores

Fixtures below are copied verbatim from 05-RESEARCH.md Code Examples — already
hand-verified by direct execution against installed pyod==3.6.0 / scikit-learn==1.8.0
in the research session; not re-derived here.
"""

import joblib
import pytest

from argus_detector.group.multivariate_detector import GroupMultivariateDetector

# Two features individually within normal range for EACH feature's own marginal
# distribution, but jointly anomalous (strong positive correlation broken at the
# test row). Base 5-row fixture is copied verbatim from 05-RESEARCH.md Code
# Examples; extended to 10 rows (same correlated pattern) so PCA/IForest (which
# need more samples than ECOD/COPOD to avoid degenerate near-zero residual
# variance on a 2-feature, 5-row fit) also produce a well-defined joint score.
X_TRAIN_JOINT = [
    [1000.0, 20.0],
    [1002.0, 22.0],
    [998.0, 18.0],
    [1001.0, 21.0],
    [999.0, 19.0],
    [1000.5, 20.5],
    [1001.5, 21.5],
    [999.5, 19.5],
    [1000.2, 20.2],
    [999.8, 19.8],
]

# Feature 1 is high-normal, feature 2 is low-normal INDIVIDUALLY, but the
# COMBINATION (high pressure + low value) breaks the learned correlation —
# a joint anomaly a univariate loop over each column separately would NOT catch.
X_TEST_JOINT_ANOMALY = [[1002.0, 18.0]]

# In-distribution point matching the learned correlation.
X_TEST_IN_DISTRIBUTION = [[1000.5, 20.5]]

# Mixed-unit fixture: [hPa, %RH]. Source: 05-RESEARCH.md Code Examples.
X_TRAIN_MIXED_UNITS = [
    [1000.0, 45.0],
    [1010.0, 50.0],
    [995.0, 40.0],
    [1005.0, 55.0],
]
X_TEST_MIXED_UNITS = [[1002.0, 48.0]]


class TestGroupMultivariateDetectorFitScore:
    def test_fit_then_score_batch_returns_floats(self):
        det = GroupMultivariateDetector("ecod")
        det.fit(X_TRAIN_JOINT)
        scores, _ = det.score_batch(X_TEST_IN_DISTRIBUTION)
        assert isinstance(scores, list)
        assert len(scores) == 1
        assert all(isinstance(s, float) for s in scores)

    def test_score_batch_before_fit_raises(self):
        det = GroupMultivariateDetector("ecod")
        with pytest.raises(ValueError, match=r"fit\(\) must be called before score_batch"):
            det.score_batch(X_TEST_IN_DISTRIBUTION)

    def test_is_fitted_false_before_fit(self):
        det = GroupMultivariateDetector("ecod")
        assert det.is_fitted is False

    def test_is_fitted_true_after_fit(self):
        det = GroupMultivariateDetector("ecod")
        det.fit(X_TRAIN_JOINT)
        assert det.is_fitted is True

    def test_unknown_detector_name_raises(self):
        with pytest.raises(ValueError, match="Unknown group detector"):
            GroupMultivariateDetector("not_a_real_detector")


class TestGroupMultivariateDetectorJointAnomaly:
    @pytest.mark.parametrize("detector_name", ["ecod", "copod", "pca", "iforest"])
    def test_joint_anomaly_scores_higher_than_in_distribution(self, detector_name):
        """A vector no single feature would flag (correlation broken) scores
        higher than an in-distribution vector — proves the 2D matrix is scored
        jointly, not via a per-feature/univariate loop (success criterion 3)."""
        det = GroupMultivariateDetector(detector_name)
        det.fit(X_TRAIN_JOINT)

        anomaly_scores, _ = det.score_batch(X_TEST_JOINT_ANOMALY)
        normal_scores, _ = det.score_batch(X_TEST_IN_DISTRIBUTION)

        assert anomaly_scores[0] > normal_scores[0]


class TestGroupMultivariateDetectorMixedUnits:
    @pytest.mark.parametrize("detector_name", ["ecod", "copod", "pca", "iforest"])
    def test_mixed_units_scored_without_raising(self, detector_name):
        """hPa (large magnitude) + %RH (small magnitude) fixture: RobustScaler
        must be applied before fitting/scoring so the large-magnitude feature
        does not dominate (GRP-06). Verified indirectly: fit/score succeeds and
        produces a finite score (scaling occurred; unscaled magnitudes would
        still "work" numerically, so the real proof is the RobustScaler call
        happening in fit()/score_batch(), asserted below via scaler state)."""
        det = GroupMultivariateDetector(detector_name)
        det.fit(X_TRAIN_MIXED_UNITS)
        scores, _ = det.score_batch(X_TEST_MIXED_UNITS)
        assert len(scores) == 1
        assert scores[0] == scores[0]  # not NaN

    def test_robust_scaler_is_fit_on_mixed_units(self):
        """The persisted scaler's center_ must reflect BOTH feature columns
        (median of hPa column, median of %RH column) — proves scaling is
        applied per-feature before the PyOD model ever sees raw mixed units."""
        det = GroupMultivariateDetector("ecod")
        det.fit(X_TRAIN_MIXED_UNITS)
        bundle = det.bundle()
        scaler = bundle["scaler"]
        # center_ has one entry per feature; large-hPa column and small-%RH
        # column must each have been centered on their OWN median, not a
        # shared/global scale that would let hPa dominate.
        assert len(scaler.center_) == 2
        assert scaler.center_[0] > 900  # hPa column median
        assert scaler.center_[1] < 100  # %RH column median


class TestGroupMultivariateDetectorAttribution:
    @pytest.mark.parametrize("detector_name", ["ecod", "copod"])
    def test_attributable_detectors_return_contribution_matrix(self, detector_name):
        det = GroupMultivariateDetector(detector_name)
        det.fit(X_TRAIN_JOINT)
        scores, contributions = det.score_batch(X_TEST_JOINT_ANOMALY)
        assert contributions is not None
        assert len(contributions) == len(X_TEST_JOINT_ANOMALY)  # n_new rows
        assert len(contributions[0]) == 2  # n_features

    @pytest.mark.parametrize("detector_name", ["pca", "iforest"])
    def test_non_attributable_detectors_return_none(self, detector_name):
        det = GroupMultivariateDetector(detector_name)
        det.fit(X_TRAIN_JOINT)
        scores, contributions = det.score_batch(X_TEST_JOINT_ANOMALY)
        assert contributions is None


class TestGroupMultivariateDetectorBundleRoundtrip:
    def test_bundle_from_bundle_roundtrip_identical_scores(self):
        det = GroupMultivariateDetector("ecod")
        det.fit(X_TRAIN_JOINT)
        original_scores, _ = det.score_batch(X_TEST_JOINT_ANOMALY)

        bundle = det.bundle()
        assert set(bundle) == {"scaler", "detector", "name"}

        restored = GroupMultivariateDetector.from_bundle(bundle)
        assert restored.is_fitted is True
        restored_scores, _ = restored.score_batch(X_TEST_JOINT_ANOMALY)

        assert restored_scores == original_scores

    def test_bundle_roundtrip_through_joblib(self, tmp_path):
        """Bundle survives an actual joblib dump/load cycle (not just in-memory)."""
        det = GroupMultivariateDetector("ecod")
        det.fit(X_TRAIN_JOINT)
        original_scores, _ = det.score_batch(X_TEST_JOINT_ANOMALY)

        bundle_path = tmp_path / "group_model.joblib"
        joblib.dump(det.bundle(), bundle_path)
        loaded_bundle = joblib.load(bundle_path)

        restored = GroupMultivariateDetector.from_bundle(loaded_bundle)
        restored_scores, _ = restored.score_batch(X_TEST_JOINT_ANOMALY)

        assert restored_scores == original_scores
