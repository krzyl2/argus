"""
DetectorRegistry — maps (entity_id, detector) to per-entity detector state.

Phase 1: returns a placeholder score of 0.0 for all inputs.
TODO(plan06): wire River HalfSpaceTrees — swap score_one body only; interface is stable.
"""


class DetectorRegistry:
    """
    Keyed by (entity_id, detector_name).

    score_one(entity_id, value) -> float
      Returns an anomaly score for the given sensor reading.
      Phase 1: always returns 0.0 (placeholder).
      Plan 06: replaces with River HalfSpaceTrees online learning.
    """

    def score_one(self, entity_id: str, value: float) -> float:  # noqa: ARG002
        """Return a placeholder anomaly score.

        Interface is intentionally stable; Plan 06 replaces the body.
        """
        # TODO(plan06): wire River HalfSpaceTrees
        return 0.0
