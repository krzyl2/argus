"""
DetectorServiceServicer — gRPC servicer implementation.

ScoreStream: streams Verdict messages back for each incoming Point.
  Phase 1: placeholder score=0.0, is_anomaly=False, detector="hst".
  Plan 06: registry.score_one will return real River HalfSpaceTrees scores.

Fit: returns ok=False, error="not implemented in phase 1".

Threat mitigations:
  T-02-02: context.is_active() checked on every iteration to exit dead streams.
  T-02-03: logs only entity_id, score, latency_ms, detector — no raw secrets.
"""

import logging
import time

import grpc
from google.protobuf import timestamp_pb2, wrappers_pb2

from argus_detector.proto import argus_pb2, argus_pb2_grpc
from argus_detector.registry import DetectorRegistry

logger = logging.getLogger(__name__)


class DetectorServicer(argus_pb2_grpc.DetectorServiceServicer):
    """Implements DetectorService gRPC interface."""

    def __init__(self, registry: DetectorRegistry) -> None:
        self._registry = registry

    def ScoreStream(self, request_iterator, context):  # noqa: N802
        """Stream a Verdict for each incoming Point.

        Placeholder: score=0.0, is_anomaly=False, detector="hst".
        TODO(plan06): real River HST scoring wired through registry.
        """
        for point in request_iterator:
            # T-02-02: exit immediately if the client disconnected
            if not context.is_active():
                return

            if not point.entity_id:
                logger.warning("received Point with empty entity_id � skipping")
                continue

            try:
                t_start = time.monotonic()

                entity_id: str = point.entity_id
                value: float = point.value.value  # unwrap DoubleValue

                score: float = self._registry.score_one(entity_id, value)

                ts = timestamp_pb2.Timestamp()
                ts.GetCurrentTime()

                verdict = argus_pb2.Verdict(
                    entity_id=entity_id,
                    score=wrappers_pb2.DoubleValue(value=score),
                    is_anomaly=False,
                    detector="hst",
                    timestamp=ts,
                )

                latency_ms = (time.monotonic() - t_start) * 1000

                # T-02-03: log only safe fields
                logger.info(
                    "scored",
                    extra={
                        "entity_id": entity_id,
                        "score": score,
                        "latency_ms": round(latency_ms, 3),
                        "detector": "hst",
                    },
                )

                yield verdict
            except Exception:
                logger.exception("unexpected error scoring point for %s", point.entity_id)
                context.abort(grpc.StatusCode.INTERNAL, "scoring error")
                return

    def Fit(self, request, context):  # noqa: N802
        """Batch fit — not implemented in Phase 1."""
        return argus_pb2.FitResponse(ok=False, error="not implemented in phase 1")
