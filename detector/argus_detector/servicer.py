"""
DetectorServiceServicer — gRPC servicer implementation.

ScoreStream: streams Verdict messages back for each incoming Point.
  Phase 1: placeholder score=0.0, is_anomaly=False, detector="hst".
  Plan 06: registry.score_one will return real River HalfSpaceTrees scores.

Fit: trains model via registry.fit_one, saves to disk via model_store.
ScoreBatch: reads model from registry; cold-start fit if no model exists.
SaveModel: serializes model from registry; returns bytes.
LoadModel: loads model from disk; registers into registry.

Threat mitigations:
  T-02-02: context.is_active() checked on every iteration to exit dead streams.
  T-02-03: logs only entity_id, score, latency_ms, detector — no raw secrets.
  T-02-05-05: cold-start fit is logged with entity_id and detector (MDL-05 mitigate).
"""

import io
import logging
import time

import grpc
import joblib
import pickle
from google.protobuf import timestamp_pb2, wrappers_pb2

from argus_detector.model_store import ModelStore
from argus_detector.proto import argus_pb2, argus_pb2_grpc
from argus_detector.registry import DetectorRegistry

logger = logging.getLogger(__name__)


class DetectorServicer(argus_pb2_grpc.DetectorServiceServicer):
    """Implements DetectorService gRPC interface."""

    def __init__(self, registry: DetectorRegistry, model_store: ModelStore) -> None:
        self._registry = registry
        self._model_store = model_store

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
                logger.warning("received Point with empty entity_id - skipping")
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
        """Train a batch model for (entity_id, detector) on request.window.

        Saves the fitted model to disk via model_store after training.
        """
        if not request.entity_id:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, "empty entity_id")
            return None  # WR-06: after abort, gRPC ignores the return value — return None

        try:
            entity_id = request.entity_id
            detector = request.detector or "mad"
            values = [p.value.value for p in request.window]
            entity_slug = entity_id.replace(".", "_")

            # Get next version BEFORE fitting (per plan spec: version increments correctly)
            version = self._model_store.next_version(entity_slug, detector)

            # Train model (MDL-04: train-outside-lock handled inside registry.fit_one)
            self._registry.fit_one(entity_id, detector, values)

            # Access fitted model for persistence (WR-02: use get_model to respect entity lock)
            model = self._registry.get_model(entity_id, detector)
            if model is not None:
                self._save_model_to_store(entity_slug, detector, version, model, entity_id=entity_id)

            return argus_pb2.FitResponse(ok=True)

        except Exception as e:
            logger.exception("unexpected error in Fit for %s", request.entity_id)
            return argus_pb2.FitResponse(ok=False, error=str(e))

    def ScoreBatch(self, request, context):  # noqa: N802
        """Score a batch window for (entity_id, detector).

        Cold-start: if no model exists, fit_one first using the window data.
        Returns one Verdict per input Point (BTCH anti-pattern: NOT one per entity).
        """
        if not request.entity_id:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, "empty entity_id")
            return

        try:
            entity_id = request.entity_id
            detector = request.detector or "mad"
            values = [p.value.value for p in request.window]

            # Cold-start: fit if no model (T-02-05-05: log cold start)
            if not self._registry.has_model(entity_id, detector):
                logger.info(
                    "cold start fit",
                    extra={"entity_id": entity_id, "detector": detector},
                )
                self._registry.fit_one(entity_id, detector, values)

            scores, error = self._registry.score_batch(entity_id, detector, values)
            if error:
                return argus_pb2.ScoreBatchResponse(ok=False, error=error)

            # Build one Verdict per window point
            ts = timestamp_pb2.Timestamp()
            ts.GetCurrentTime()
            verdicts = [
                argus_pb2.Verdict(
                    entity_id=entity_id,
                    score=wrappers_pb2.DoubleValue(value=s),
                    is_anomaly=False,  # orchestrator's hysteresis gate decides
                    detector=detector,
                    timestamp=ts,
                )
                for s in scores
            ]
            return argus_pb2.ScoreBatchResponse(verdicts=verdicts, ok=True)

        except Exception:
            logger.exception("unexpected error in ScoreBatch for %s", request.entity_id)
            context.abort(grpc.StatusCode.INTERNAL, "scoring error")
            return

    def SaveModel(self, request, context):  # noqa: N802
        """Persist fitted model from registry to disk (WR-03).

        Uses model_store.save_pyod / save_river to write the model file.
        Returns ok=False if no model is registered for the entity/detector.
        """
        entity_id = request.entity_id
        detector = request.detector
        entity_slug = entity_id.replace(".", "_")

        # WR-02: use get_model() to respect the per-entity lock
        model = self._registry.get_model(entity_id, detector)
        if model is None:
            return argus_pb2.SaveModelResponse(ok=False, error="no model for entity/detector")

        try:
            version = self._model_store.next_version(entity_slug, detector)
            self._save_model_to_store(entity_slug, detector, version, model, entity_id=entity_id)
            return argus_pb2.SaveModelResponse(ok=True)
        except Exception as e:
            logger.exception("SaveModel failed for %s/%s", entity_id, detector)
            return argus_pb2.SaveModelResponse(ok=False, error=str(e))

    def LoadModel(self, request, context):  # noqa: N802
        """Load a model from disk and register it into the registry."""
        entity_id = request.entity_id
        detector = request.detector
        version_arg = request.version  # 0 = load latest

        entity_slug = entity_id.replace(".", "_")
        version = None if version_arg == 0 else version_arg

        try:
            model = self._load_model_from_store(entity_slug, detector, version)
            # Register using the entity_id (not slug) so has_model works by entity_id
            self._registry.register(entity_id, detector, model)
            return argus_pb2.LoadModelResponse(ok=True, model_bytes=b"")
        except Exception as e:
            logger.exception("LoadModel failed for %s/%s", entity_id, detector)
            return argus_pb2.LoadModelResponse(ok=False, error=str(e))

    # -------------------------------------------------------------------------
    # Private helpers
    # -------------------------------------------------------------------------

    def _save_model_to_store(
        self,
        entity_slug: str,
        detector: str,
        version: int,
        model: object,
        entity_id: str | None = None,
    ) -> None:
        """Persist model to disk. Uses joblib for PyOD, pickle for River."""
        from argus_detector.pyod_detector import PyODDetector
        if isinstance(model, PyODDetector):
            self._model_store.save_pyod(entity_slug, detector, version, model, entity_id=entity_id)
        else:
            # River HST or other — use pickle
            self._model_store.save_river(entity_slug, detector, version, model, entity_id=entity_id)

    def _serialize_model(self, model: object) -> bytes:
        """Serialize a model to bytes for in-band gRPC transport (SaveModel)."""
        from argus_detector.pyod_detector import PyODDetector
        buf = io.BytesIO()
        if isinstance(model, PyODDetector):
            joblib.dump(model, buf)
        else:
            pickle.dump(model, buf)
        return buf.getvalue()

    def _load_model_from_store(
        self,
        entity_slug: str,
        detector: str,
        version: int | None,
    ) -> object:
        """Load model from disk — try joblib (PyOD) first, fall back to pickle (River)."""
        # Determine path without loading to pick the right loader
        if version is None:
            # Read latest version number
            version = self._model_store._read_latest(entity_slug, detector)

        model_dir = self._model_store._model_dir(entity_slug, detector, version)
        if (model_dir / "model.joblib").exists():
            return self._model_store.load_pyod(entity_slug, detector, version)
        return self._model_store.load_river(entity_slug, detector, version)
