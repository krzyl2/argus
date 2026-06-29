"""
STL residual-based batch anomaly detector (step-change / FAULT-03).

StlDetector: stateless decomposition detector using statsmodels STL.
  - score_batch(values, period): decomposes window, returns abs(residual) scores normalised [0,1]
  - robust=True mandatory: standard STL absorbs anomalies into seasonal component;
    robust STL downweights outliers so step changes appear in the residual
  - MINIMUM DATA GUARD: requires len(values) >= 2 * period; returns ([], error) if insufficient
  - No persistent model — no fit() / no serialization needed
  - For 60s-interval sensors: period=1440 (daily), minimum=2880 points (48h of data)

NOTE on Phase 2 behavior:
  The 24h rolling window provides at most 1440 points. With period=1440, the guard fires
  for ALL STL scoring calls in Phase 2 (insufficient history). This is correct behavior,
  not a bug — STL becomes useful when the orchestrator queries a 48h+ window (Phase 3+).

Thread safety: stateless; safe to call from multiple threads concurrently.
"""

from __future__ import annotations

import numpy as np
from statsmodels.tsa.seasonal import STL

# Period for 60s-interval daily seasonality: 1440 readings/day
_PERIOD_DAILY = 1440


class StlDetector:
    """Stateless STL residual scorer. No fit(), no saved model.

    Usage::

        det = StlDetector()
        scores, error = det.score_batch(values, period=1440)
        if error is not None:
            # insufficient history — expected in Phase 2 with 24h window
            log.warning(error)
        else:
            # scores is list[float] in [0.0, 1.0], one per input value
    """

    def score_batch(
        self,
        values: list[float],
        period: int = _PERIOD_DAILY,
    ) -> tuple[list[float], str | None]:
        """Decompose values via STL and return normalised residual scores.

        Args:
            values: Time-ordered sensor readings.
            period: Seasonality period in samples. Default 1440 (daily for 60s sensors).

        Returns:
            (scores, None) on success — list[float] in [0, 1], length == len(values).
            ([], error_string) when len(values) < 2 * period.

        Note:
            robust=True is mandatory — standard STL absorbs outliers/step-changes into
            the seasonal component. Robust STL downweights them so they appear as
            large residuals, enabling FAULT-03 step-change detection.
        """
        n = len(values)
        if n < 2 * period:
            return [], f"insufficient history: got {n} points, need >= {2 * period}"

        x = np.array(values, dtype=float)
        result = STL(x, period=period, robust=True).fit()
        residuals = np.abs(result.resid)

        rng = float(residuals.max() - residuals.min())
        # Use absolute tolerance to handle floating-point noise from STL on constant input.
        # STL on all-equal values produces residuals ~O(1e-14) due to LOESS numerical errors.
        # Any rng below 1e-10 is effectively zero — return uniform zero scores.
        if rng < 1e-10:
            return [0.0] * n, None

        scores = ((residuals - residuals.min()) / rng).tolist()
        return scores, None
