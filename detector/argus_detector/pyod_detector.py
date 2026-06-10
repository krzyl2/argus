"""
PyOD MAD batch anomaly detector.

PyODDetector: per-entity batch anomaly detector using pyod.models.mad.MAD.
  - Univariate constraint: X must be shape (n, 1) — enforced by MAD (CRITICAL FINDING)
  - fit(values): trains MAD on float list; sets _fitted flag
  - score_batch(values): returns decision_function() scores (continuous, NOT predict() labels)
  - from_params(): overrides threshold/contamination from string params map (CONF-02)
  - robust_zscore alias: 'detector' key in params is silently ignored here;
    mapping of 'robust_zscore' -> PyODDetector is handled at registry._create_detector() (Plan 05)

Thread safety: instances are swapped atomically by DetectorRegistry.fit_one();
scoring runs outside the lock on a snapshot reference (MDL-04).
"""

from __future__ import annotations

import numpy as np
from pyod.models.mad import MAD

# Module-level defaults (D-09 equivalent for MAD parameters)
_DEFAULT_THRESHOLD = 3.5
_DEFAULT_CONTAMINATION = 0.1


def _cast_float(params: dict[str, str], key: str, default: float) -> float:
    """Cast a string param to float, returning default if key absent or invalid."""
    raw = params.get(key)
    if raw is None:
        return default
    try:
        return float(raw)
    except (ValueError, TypeError):
        return default


class PyODDetector:
    """Per-entity batch anomaly detector (PyOD MAD).

    Usage::

        det = PyODDetector(threshold=3.5)
        det.fit([21.5, 22.0, 21.8, ...])
        scores = det.score_batch([21.5, 22.0, 21.8, ...])
        # scores is a list[float] — one per input value

    Notes:
    - Uses decision_function() for continuous scores, NOT predict() (binary 0/1).
    - MAD requires X.shape == (n, 1) — reshape is enforced in fit() and score_batch().
    - robust_zscore is an alias for this class (handled by registry, not here).
    """

    def __init__(
        self,
        threshold: float = _DEFAULT_THRESHOLD,
        contamination: float = _DEFAULT_CONTAMINATION,
    ) -> None:
        self._model = MAD(threshold=threshold, contamination=contamination)
        self._fitted = False

    @classmethod
    def from_params(cls, params: dict[str, str]) -> "PyODDetector":
        """Create a PyODDetector from a string params map (CONF-02).

        Supported keys: "threshold", "contamination" (both cast to float).
        The "detector" key is silently ignored — alias mapping is handled at
        the registry level (Plan 05) not here.
        Absent or invalid keys fall back to module-level defaults.
        """
        threshold = _cast_float(params, "threshold", _DEFAULT_THRESHOLD)
        contamination = _cast_float(params, "contamination", _DEFAULT_CONTAMINATION)
        return cls(threshold=threshold, contamination=contamination)

    def fit(self, values: list[float]) -> None:
        """Train MAD on a list of float values.

        Args:
            values: 1-D list of floats. Must have at least 1 element.

        Note:
            MAD requires X.shape == (n, 1) — univariate constraint. Reshape
            is applied here so callers never need to handle it.
        """
        X = np.array(values, dtype=float).reshape(-1, 1)
        self._model.fit(X)
        self._fitted = True

    def score_batch(self, values: list[float]) -> list[float]:
        """Return anomaly scores for a batch of values.

        Args:
            values: 1-D list of floats.

        Returns:
            list[float] of length len(values). Higher = more anomalous.
            Uses decision_function() (continuous) not predict() (binary).

        Raises:
            ValueError: if fit() has not been called yet.
        """
        if not self._fitted:
            raise ValueError("fit() must be called before score_batch()")
        X = np.array(values, dtype=float).reshape(-1, 1)
        # decision_function() returns raw scores (higher = more anomalous)
        # Do NOT use predict() — returns binary 0/1 labels
        return self._model.decision_function(X).tolist()

    @property
    def is_fitted(self) -> bool:
        """True after fit() has been called at least once."""
        return self._fitted
