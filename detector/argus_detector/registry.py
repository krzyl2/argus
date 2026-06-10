"""
DetectorRegistry — maps (entity_id, detector) to per-entity EntityDetector instances.

Thread safety: a threading.Lock guards lazy instance creation.
Once an EntityDetector exists, it is accessed from a single gRPC stream thread
(one bidi stream per entity per D-04), so the Lock covers only the dict write.

T-06-01: threading.Lock guards creation; per-entity instances isolate state.
"""

from __future__ import annotations

import threading

from argus_detector.hst_detector import EntityDetector


class DetectorRegistry:
    """Registry keyed by (entity_id, detector_name) -> EntityDetector.

    score_one(entity_id, value, detector="hst", params=None) -> float
      Lazily creates an EntityDetector on first sight of a (entity_id, detector) pair.
      Returns the anomaly score from that detector.

    is_warmed_up(entity_id, detector="hst") -> bool
      Returns whether the entity's detector has processed >= window_size readings.
    """

    def __init__(self) -> None:
        # T-06-01: Lock guards lazy creation only
        self._lock = threading.Lock()
        self._detectors: dict[tuple[str, str], EntityDetector] = {}

    def _get_or_create(
        self,
        entity_id: str,
        detector: str,
        params: dict[str, str] | None,
    ) -> EntityDetector:
        key = (entity_id, detector)
        # Fast path: already exists (no lock needed for read after creation)
        det = self._detectors.get(key)
        if det is not None:
            return det
        # Slow path: create under lock (T-06-01)
        with self._lock:
            # Re-check after acquiring lock (double-checked locking)
            det = self._detectors.get(key)
            if det is None:
                det = EntityDetector.from_params(params or {})
                self._detectors[key] = det
        return det

    def score_one(
        self,
        entity_id: str,
        value: float,
        detector: str = "hst",
        params: dict[str, str] | None = None,
    ) -> float:
        """Score a single sensor reading for the given entity.

        Lazily creates an EntityDetector on first call for (entity_id, detector).
        Params are only applied at creation time; subsequent calls with the same
        (entity_id, detector) reuse the existing instance.

        Args:
            entity_id: HA entity ID (e.g. "sensor.salon_temperatura")
            value: raw sensor reading
            detector: detector name (default "hst")
            params: optional string param overrides (e.g. {"window": "50"})

        Returns:
            Anomaly score float in [0, 1].
        """
        det = self._get_or_create(entity_id, detector, params)
        return det.score_one(value)

    def is_warmed_up(self, entity_id: str, detector: str = "hst") -> bool:
        """True when the entity's detector has processed >= window_size readings.

        Returns False if the entity has never been scored.
        """
        key = (entity_id, detector)
        det = self._detectors.get(key)
        if det is None:
            return False
        return det.is_warmed_up
