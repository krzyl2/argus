"""
GroupMultivariateDetector — joint-multivariate anomaly detector for a group of
sensors (GRP-05/GRP-06/GRP-07).

Wraps a RobustScaler (median/IQR scaling) fitted alongside a PyOD detector
(ECOD, COPOD, PCA, or IForest) so mixed-unit member matrices (e.g. hPa + %RH)
do not let one feature dominate the joint anomaly score. The scaler and the
fitted PyOD model are persisted together in a single joblib bundle via
bundle()/from_bundle() (ModelStore.save_group_bundle/load_group_bundle).

Only ECOD and COPOD expose per-feature attribution in PyOD 3.6.0 (their
internal `self.O` matrix of tail probabilities). PCA and IForest have no
per-feature decomposition — score_batch() returns None for their
contributions rather than fabricating one.

CRITICAL (RESEARCH.md Pitfall 1): ECOD/COPOD's `self.O` is a mutable
instance attribute that grows (via internal concatenation with X_train) and
is overwritten on every decision_function() call. Attribution for a scored
batch MUST be read as `self._model.O[-len(matrix):]` synchronously, right
after the decision_function() call that produced it — never cached or read
later.

CRITICAL (RESEARCH.md Pitfall 2): PyOD's PCA defaults to
`standardization=True`, which would double-scale data already scaled by our
RobustScaler. The factory below constructs PCA with standardization=False so
RobustScaler remains the single scaling owner (GRP-06).

Thread safety: mirrors PyODDetector — instances are swapped atomically by
the registry; scoring runs outside the lock on a snapshot reference.
"""

from __future__ import annotations

import numpy as np
from sklearn.preprocessing import RobustScaler

_DETECTOR_FACTORY = {
    "ecod": lambda: __import__("pyod.models.ecod", fromlist=["ECOD"]).ECOD(),
    "copod": lambda: __import__("pyod.models.copod", fromlist=["COPOD"]).COPOD(),
    "pca": lambda: __import__("pyod.models.pca", fromlist=["PCA"]).PCA(standardization=False),
    "iforest": lambda: __import__("pyod.models.iforest", fromlist=["IForest"]).IForest(),
}
# PCA standardization=False is REQUIRED — PyOD's PCA standardizes internally by
# default (standardization=True), which would double-scale on top of RobustScaler
# and defeat GRP-06's intent (scaler is our single source of truth for scaling).

_ATTRIBUTABLE = {"ecod", "copod"}  # only these expose self.O for per-feature attribution


class GroupMultivariateDetector:
    """Joint-multivariate group anomaly detector (RobustScaler + PyOD).

    Usage::

        det = GroupMultivariateDetector("ecod")
        det.fit(matrix)  # matrix: (n_timestamps, n_features)
        scores, contributions = det.score_batch(new_matrix)
        # contributions is None for pca/iforest; a (n_new, n_features)
        # matrix of per-feature tail probabilities for ecod/copod.
    """

    def __init__(self, detector_name: str) -> None:
        if detector_name not in _DETECTOR_FACTORY:
            raise ValueError(f"Unknown group detector: {detector_name!r}")
        self._name = detector_name
        self._scaler = RobustScaler()
        self._model = _DETECTOR_FACTORY[detector_name]()
        self._fitted = False

    def fit(self, matrix: list[list[float]]) -> None:
        """matrix: (n_timestamps, n_features) — one column per member/feature."""
        X = np.array(matrix, dtype=float)
        Xs = self._scaler.fit_transform(X)
        self._model.fit(Xs)
        self._fitted = True

    def score_batch(
        self, matrix: list[list[float]]
    ) -> tuple[list[float], list[list[float]] | None]:
        """Returns (group_scores, per_feature_contributions_or_None).

        per_feature_contributions is only populated for ECOD/COPOD (self.O).
        CRITICAL: must read self._model.O immediately after decision_function —
        it is a mutable attribute overwritten (and grown by concatenation with
        X_train) on every call.

        Raises:
            ValueError: if fit() has not been called yet.
        """
        if not self._fitted:
            raise ValueError("fit() must be called before score_batch()")
        X = np.array(matrix, dtype=float)
        Xs = self._scaler.transform(X)
        scores = self._model.decision_function(Xs).tolist()

        if self._name in _ATTRIBUTABLE:
            # O has shape (n_train + n_new, n_features) after this call —
            # slice the LAST len(matrix) rows to get attribution for the
            # points just scored, not the training data.
            o_matrix = self._model.O[-len(matrix):]
            contributions = o_matrix.tolist()
        else:
            contributions = None

        return scores, contributions

    @property
    def is_fitted(self) -> bool:
        """True after fit() has been called at least once."""
        return self._fitted

    def bundle(self) -> dict:
        """Return the persistable state — passed to ModelStore.save as one object."""
        return {"scaler": self._scaler, "detector": self._model, "name": self._name}

    @classmethod
    def from_bundle(cls, bundle: dict) -> "GroupMultivariateDetector":
        instance = cls.__new__(cls)
        instance._name = bundle["name"]
        instance._scaler = bundle["scaler"]
        instance._model = bundle["detector"]
        instance._fitted = True
        return instance
