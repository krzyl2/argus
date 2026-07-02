"""
Peer-divergence detector: robust modified z-score across group members (GRP-03/04).

PeerDivergenceDetector: stateless cross-member scorer using median/MAD.
  - score_batch(matrix): per-timestamp modified z-score across members, returns
    (scores, flags, error) tuple.
  - Modified z-score: 0.6745 * (x - median) / MAD (Iglewicz-Hoaglin robust statistic).
  - MINIMUM-MEMBER GUARD: requires n_members >= 3; returns (None, None, error) if
    below floor — this is a genuine "no verdict possible" state, never a false
    not-anomalous verdict (GRP-04).
  - MAD=0 GUARD: falls back to mean absolute deviation (meanAD) with the
    corresponding 0.7979 constant; if meanAD is ALSO 0 (all members identical),
    returns concrete all-zero scores — a real "no divergence" result, distinct
    from the below-floor no-verdict case (RESEARCH Pitfall 4).
  - No persistent model — no fit() / no serialization needed (stateless per
    CONTEXT.md; Fit/Save is a no-op, wired in Plan 05-04).

Thread safety: stateless; safe to call from multiple threads concurrently.
"""

from __future__ import annotations

import numpy as np

_THRESHOLD = 3.5
_MIN_MEMBERS = 3
_MAD_CONST = 0.6745  # Iglewicz-Hoaglin constant for MAD-based modified z-score
# 0.7979 is the corresponding Iglewicz-Hoaglin constant for the meanAD fallback
# (Iglewicz, B. and Hoaglin, D.C. 1993, "How to Detect and Handle Outliers",
# ASQC Basic References in Quality Control) — used only when MAD == 0, since
# 0.6745 is calibrated for MAD specifically, not meanAD.
_MEAN_AD_CONST = 0.7979


def modified_zscore(row: np.ndarray) -> np.ndarray:
    """Compute modified z-score for one timestamp's values across members.

    Args:
        row: 1-D array, one value per group member at a single timestamp.

    Returns:
        1-D array of modified z-scores, same length as row.
        All-zero array if median absolute deviation is 0 (no divergence possible).
    """
    median = np.median(row)
    abs_dev = np.abs(row - median)
    mad = np.median(abs_dev)
    if mad == 0:
        # MAD=0 guard: fall back to mean absolute deviation (Iglewicz-Hoaglin
        # recommendation §3); if meanAD is ALSO 0 (all values identical),
        # every member is normal by definition — return zeros, not NaN/inf.
        mean_ad = np.mean(abs_dev)
        if mean_ad == 0:
            return np.zeros_like(row)
        return _MEAN_AD_CONST * (row - median) / mean_ad
    return _MAD_CONST * (row - median) / mad


def score_group(matrix: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Score a (n_timestamps, n_members) matrix with per-timestamp robust z-scores.

    WR-03: the minimum-member floor (GRP-04) is enforced exclusively by
    PeerDivergenceDetector.score_batch() BEFORE this function is called —
    the only production caller. This function does not re-check the floor
    to avoid a second, divergence-prone copy of the `_MIN_MEMBERS` check.
    Callers invoking score_group() directly (e.g. tests) are responsible
    for enforcing the floor themselves; passing n_members < _MIN_MEMBERS
    is outside this function's contract.

    Returns:
        (scores, flags) both shape (n_timestamps, n_members).
    """
    scores = np.apply_along_axis(modified_zscore, axis=1, arr=matrix)
    flags = np.abs(scores) > _THRESHOLD
    return scores, flags


class PeerDivergenceDetector:
    """Stateless robust peer-divergence scorer. No fit(), no saved model.

    Usage::

        det = PeerDivergenceDetector()
        scores, flags, error = det.score_batch(matrix)
        if error is not None:
            # below the minimum-member floor — no verdict possible (GRP-04)
            log.warning(error)
        else:
            # scores/flags are list[list[float]]/list[list[bool]],
            # shape (n_timestamps, n_members)
    """

    def score_batch(
        self, matrix: list[list[float]]
    ) -> tuple[list[list[float]] | None, list[list[bool]] | None, str | None]:
        """Score a (n_timestamps, n_members) matrix via per-timestamp modified z-score.

        Args:
            matrix: 2-D list, one row per timestamp, one column per group member.

        Returns:
            (scores, flags, None) on success — both shape (n_timestamps, n_members).
            (None, None, error_string) when n_members < 3 (GRP-04 floor) — a
            distinct no-verdict state, never a false not-anomalous verdict.
        """
        x = np.array(matrix, dtype=float)
        n_timestamps, n_members = x.shape
        if n_members < _MIN_MEMBERS:
            return (
                None,
                None,
                f"insufficient members: got {n_members}, need >= {_MIN_MEMBERS}",
            )

        scores, flags = score_group(x)
        return scores.tolist(), flags.tolist(), None
