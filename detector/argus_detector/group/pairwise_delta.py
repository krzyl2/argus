"""
PairwiseDeltaDetector — 2-member `peer_divergence` scoring path (GRP-11).

Computes the elementwise delta between exactly two group members
(`member_a - member_b`) and scores that derived univariate signal with the
existing production-proven `PyODDetector` (PyOD MAD). This is NOT a new
statistical model — it delegates to `PyODDetector` unmodified (no
subclassing, no reimplementation of MAD), matching the roadmap's explicit
"reusing proven univariate anomaly detection, not inventing new group math"
directive.

The classic N>=3 `PeerDivergenceDetector` median/MAD path is untouched by
this class; the servicer selects between the two purely on member count
(`len(request.series)`), never inside `PeerDivergenceDetector` itself (see
09-RESEARCH.md "Pattern: Servicer-level count branching, not detector-class
branching").

Attribution: a 2-point delta cannot say WHICH of the two members is
responsible for a broken pair-relationship (same degeneracy the classic
N=2 case has) — callers must never fabricate `contributions` for this path.
"""

from __future__ import annotations

import numpy as np

from argus_detector.pyod_detector import PyODDetector

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


class PairwiseDeltaDetector:
    """2-member peer_divergence scorer: delta(a, b) + PyODDetector (MAD).

    Usage::

        det = PairwiseDeltaDetector()
        delta = PairwiseDeltaDetector.compute_delta(series_a, series_b)
        det.fit(delta)
        scores = det.score_batch(delta)
        det.is_anomaly(scores[-1])
    """

    def __init__(
        self,
        threshold: float = _DEFAULT_THRESHOLD,
        contamination: float = _DEFAULT_CONTAMINATION,
    ) -> None:
        self._detector = PyODDetector(threshold=threshold, contamination=contamination)

    @classmethod
    def from_params(cls, params: dict[str, str]) -> "PairwiseDeltaDetector":
        """Create a PairwiseDeltaDetector from a string params map.

        Supported keys: "threshold", "contamination" (both cast to float),
        identical to PyODDetector.from_params(). Absent or invalid keys fall
        back to module-level defaults.
        """
        threshold = _cast_float(params, "threshold", _DEFAULT_THRESHOLD)
        contamination = _cast_float(params, "contamination", _DEFAULT_CONTAMINATION)
        return cls(threshold=threshold, contamination=contamination)

    @staticmethod
    def compute_delta(series_a: list[float], series_b: list[float]) -> list[float]:
        """Elementwise a-b delta, length-preserving.

        Args:
            series_a: 1-D list of floats (member A's values).
            series_b: 1-D list of floats (member B's values), same length as series_a.

        Returns:
            list[float] of the same length: series_a[i] - series_b[i].
        """
        return (np.array(series_a, dtype=float) - np.array(series_b, dtype=float)).tolist()

    def fit(self, delta: list[float]) -> None:
        """Train the underlying MAD detector on the delta series."""
        self._detector.fit(delta)

    def score_batch(self, delta: list[float]) -> list[float]:
        """Return anomaly scores for a batch of delta values.

        Raises:
            ValueError: if fit() has not been called yet (via PyODDetector).
        """
        return self._detector.score_batch(delta)

    @property
    def is_fitted(self) -> bool:
        """True after fit() has been called at least once."""
        return self._detector.is_fitted

    def is_anomaly(self, score: float) -> bool:
        """True if score exceeds the underlying MAD detector's fitted threshold_."""
        return self._detector.is_anomaly(score)
