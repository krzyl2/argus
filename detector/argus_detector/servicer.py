"""
DetectorServiceServicer — gRPC servicer implementation.

ScoreStream: streams Verdict messages back for each incoming Point.
  The algorithm is selected per Point from params["algorithm"] (falling back to
  params["detector"], then "hst"). params is a map<string,string> on the wire,
  so this needs no proto change and no stub regeneration — precedent:
  pyod_detector.py:63-65.

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
from argus_detector.registry import STREAMING_DETECTORS, DetectorRegistry

logger = logging.getLogger(__name__)


class DetectorServicer(argus_pb2_grpc.DetectorServiceServicer):
    """Implements DetectorService gRPC interface."""

    def __init__(self, registry: DetectorRegistry, model_store: ModelStore) -> None:
        self._registry = registry
        self._model_store = model_store
        # One warning per (entity_id, requested algorithm), never per point:
        # ScoreStream runs on every reading of every entity, so an undeduplicated
        # warning here is the same log-spam defect this release is fixing.
        self._algo_warned: set[tuple[str, str]] = set()

    def _select_algorithm(self, entity_id: str, params: dict[str, str]) -> str:
        """Pick the scoring algorithm for one Point, warning at most once.

        Unknown names degrade to "hst" instead of raising: registry.score_one
        now propagates _create_detector's ValueError, and the blanket except
        below turns any exception into context.abort(INTERNAL), which kills the
        whole multiplexed stream — every entity loses scoring because one entity
        is misconfigured. The fallback is mandatory, not defensive.
        """
        algo = params.get("algorithm") or params.get("detector") or "hst"

        if algo not in STREAMING_DETECTORS:
            if (entity_id, algo) not in self._algo_warned:
                self._algo_warned.add((entity_id, algo))
                logger.warning(
                    "unknown streaming algorithm %r for entity_id=%s; "
                    "falling back to 'hst'",
                    algo, entity_id,
                )
            algo = "hst"

        if algo == "hst" and (entity_id, "hst") not in self._algo_warned:
            self._algo_warned.add((entity_id, "hst"))
            logger.warning(
                "entity_id=%s is scoring with the legacy, uncalibrated 'hst' "
                "detector: it scores RARITY, not deviation (F4), its normalizer "
                "collapses the normal band after one excursion (F5), and its "
                "score distribution is per-sensor so no single threshold is "
                "correct (F6). Thresholds must be tuned by hand.",
                entity_id,
            )

        return algo

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

                # WARM-02 (D3 fix): forward point.params so a configured window
                # actually reaches the detector's from_params/apply_params.
                params = dict(point.params)
                algo = self._select_algorithm(entity_id, params)

                score: float = self._registry.score_one(
                    entity_id, value, detector=algo, params=params
                )

                # WARM-01/D-01: read warm-up state AFTER scoring so n_seen reflects
                # the point just processed. The detector is the single source of
                # truth for warm-up — the orchestrator only reads these three fields.
                warmed_up, n_seen, window = self._registry.get_warmup_state(entity_id, algo)

                ts = timestamp_pb2.Timestamp()
                ts.GetCurrentTime()

                verdict = argus_pb2.Verdict(
                    entity_id=entity_id,
                    score=wrappers_pb2.DoubleValue(value=score),
                    is_anomaly=False,
                    detector=algo,
                    timestamp=ts,
                    warmed_up=warmed_up,
                    n_seen=n_seen,
                    window=window,
                )

                latency_ms = (time.monotonic() - t_start) * 1000

                # T-02-03: log only safe fields
                logger.info(
                    "scored",
                    extra={
                        "entity_id": entity_id,
                        "score": score,
                        "latency_ms": round(latency_ms, 3),
                        "detector": algo,
                        "warmed_up": warmed_up,
                        "n_seen": n_seen,
                        "window": window,
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

    def ScoreGroupBatch(self, request, context):  # noqa: N802
        """Score a group of members for (group_id, detector).

        Dispatches on request.detector alone (no separate mode enum, per
        05-RESEARCH.md Open Question 2): "peer_divergence" -> per-member
        Verdicts (GRP-03/04); "ecod"/"copod"/"pca"/"iforest" -> a single
        group_verdict plus ranked FeatureContributions for attributable
        detectors (GRP-05/06).

        Design decision (05-04-PLAN.md objective): unlike ScoreBatch's
        per-entity Verdicts (where is_anomaly=False and the orchestrator's
        hysteresis gate decides), group Verdicts here have is_anomaly set
        directly by the servicer — peer-divergence from the locked
        |z| > 3.5 threshold, joint-multivariate from the PyOD detector's own
        predict(). Phase 5 owns the final threshold for groups; there is no
        per-entity hysteresis layer for group verdicts yet (that is
        Phase 6+ territory for per-entity scores only).
        """
        if not request.group_id:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, "empty group_id")
            return None  # WR-06: after abort, gRPC ignores the return value

        detector = request.detector
        if detector not in ("peer_divergence", "ecod", "copod", "pca", "iforest"):
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, f"unknown detector: {detector!r}")
            return None

        # RESEARCH V5 / T-05-09: validate all Series have identical value-array
        # length BEFORE constructing the numpy matrix — ragged input must not
        # reach np.array() where it would silently misbehave or crash deep in
        # numpy/PyOD. WR-01: an empty series list must also be rejected here —
        # `lengths` would otherwise be an empty set (len(lengths) == 0), which
        # slips past the `len(lengths) > 1` check and later crashes inside
        # np.array()/zip() as an uncontrolled ValueError instead of aborting.
        lengths = {len(s.values) for s in request.series}
        if not request.series or len(lengths) > 1:
            context.abort(
                grpc.StatusCode.INVALID_ARGUMENT,
                "empty series list" if not request.series else f"ragged series: mismatched value-array lengths {sorted(lengths)}",
            )
            return None

        try:
            group_slug = f"group_{request.group_id}"
            # Matrix: rows = timestamps, columns = members (parallel Series.values arrays).
            matrix = [list(col) for col in zip(*(s.values for s in request.series))]

            ts = timestamp_pb2.Timestamp()
            ts.GetCurrentTime()

            if detector == "peer_divergence" and len(request.series) == 2:
                # GRP-11: 2-member pairwise-delta path. Mirrors the joint-mode
                # has_model -> abort -> get_model -> score_batch -> is_anomaly
                # idiom below exactly; delta cannot attribute to either member
                # (same degeneracy as the classic N=2 case), so per_member and
                # contributions are deliberately left empty — never fabricate.
                from argus_detector.group.pairwise_delta import PairwiseDeltaDetector

                if not self._registry.has_model(group_slug, detector):
                    context.abort(
                        grpc.StatusCode.INVALID_ARGUMENT,
                        f"no fitted model for group {request.group_id!r}/{detector}; call FitGroup first",
                    )
                    return None

                model = self._registry.get_model(group_slug, detector)
                if not isinstance(model, PairwiseDeltaDetector):
                    # CR-01: classic N>=3 peer_divergence registers a
                    # PeerDivergenceDetector under the SAME (group_slug,
                    # "peer_divergence") key. If a group shrinks 3+ -> 2
                    # members after a classic nightly fit but before the next
                    # 2-member FitGroup runs, the stale entry would otherwise
                    # reach score_batch() below and raise a type-confusion
                    # exception (swallowed into ok=False). Abort with the same
                    # "call FitGroup first" message instead — self-heals on
                    # the next FitGroup, which always overwrites via register().
                    context.abort(
                        grpc.StatusCode.INVALID_ARGUMENT,
                        f"no fitted 2-member model for group {request.group_id!r}/{detector}; call FitGroup first",
                    )
                    return None
                delta = PairwiseDeltaDetector.compute_delta(
                    request.series[0].values, request.series[1].values
                )
                scores = model.score_batch(delta)
                group_score = scores[-1]
                is_anomaly = model.is_anomaly(group_score)
                group_verdict = argus_pb2.Verdict(
                    entity_id=group_slug,
                    score=wrappers_pb2.DoubleValue(value=group_score),
                    is_anomaly=is_anomaly,
                    detector=detector,
                    timestamp=ts,
                )
                return argus_pb2.GroupScoreResponse(
                    group_verdict=group_verdict,
                    per_member=[],
                    contributions=[],
                    ok=True,
                )

            if detector == "peer_divergence":
                # Stateless — no registry state needed, construct fresh per call.
                # request.params threads the threshold knob (ALGO-01/02);
                # dict(request.params) casts the protobuf map to a plain dict —
                # from_params()'s _cast_float handles any non-numeric string.
                from argus_detector.group.peer_divergence import PeerDivergenceDetector
                model = PeerDivergenceDetector.from_params(dict(request.params))
                scores, flags, error = model.score_batch(matrix)
                if error:
                    # GRP-04: below-floor group -> no verdict, NOT a false not-anomalous result.
                    return argus_pb2.GroupScoreResponse(ok=True, error=error)

                member_ids = [s.member_id for s in request.series]
                # One Verdict per member, using the LAST timestamp's row (most recent score).
                last_scores = scores[-1]
                last_flags = flags[-1]
                per_member = [
                    argus_pb2.Verdict(
                        entity_id=member_ids[i],
                        score=wrappers_pb2.DoubleValue(value=last_scores[i]),
                        is_anomaly=bool(last_flags[i]),  # locked |z|>3.5 threshold (see docstring)
                        detector=detector,
                        timestamp=ts,
                    )
                    for i in range(len(member_ids))
                ]
                return argus_pb2.GroupScoreResponse(per_member=per_member, ok=True)

            # Joint-multivariate: ecod/copod/pca/iforest — fitted model required.
            if not self._registry.has_model(group_slug, detector):
                context.abort(
                    grpc.StatusCode.INVALID_ARGUMENT,
                    f"no fitted model for group {request.group_id!r}/{detector}; call FitGroup first",
                )
                return None

            model = self._registry.get_model(group_slug, detector)
            scores, contributions = model.score_batch(matrix)

            # Single group-level verdict from the last timestamp's score;
            # is_anomaly derived from the detector's own predict() threshold
            # (score > threshold_ — PyOD's predict() decision rule, applied to
            # the score already computed by score_batch() above rather than
            # calling predict() again, which would re-invoke decision_function()
            # and corrupt the just-extracted ECOD/COPOD attribution — RESEARCH.md
            # Pitfall 1: self._model.O is mutated on every decision_function call).
            # WR-02: use the public is_anomaly() accessor instead of reaching
            # into the private _model attribute.
            group_score = scores[-1]
            is_anomaly = model.is_anomaly(group_score)
            group_verdict = argus_pb2.Verdict(
                entity_id=group_slug,
                score=wrappers_pb2.DoubleValue(value=group_score),
                is_anomaly=is_anomaly,
                detector=detector,
                timestamp=ts,
            )

            feature_contributions = []
            if contributions:
                member_ids = [s.member_id for s in request.series]
                last_contribution = contributions[-1]
                feature_contributions = [
                    argus_pb2.FeatureContribution(
                        member_id=member_ids[i],
                        contribution=last_contribution[i],
                    )
                    for i in range(len(member_ids))
                ]

            return argus_pb2.GroupScoreResponse(
                group_verdict=group_verdict,
                contributions=feature_contributions,
                ok=True,
            )

        except Exception as e:
            logger.exception("unexpected error in ScoreGroupBatch for %s", request.group_id)
            return argus_pb2.GroupScoreResponse(ok=False, error=str(e))

    def FitGroup(self, request, context):  # noqa: N802
        """Train a group model for (group_id, detector) on request.series.

        peer_divergence is stateless (GRP-03/04) — FitGroup registers it
        without training and does NOT call save_group_bundle. Joint-
        multivariate detectors (ecod/copod/pca/iforest) fit via the registry
        then persist a {"scaler", "detector", "name"} bundle via
        model_store.save_group_bundle (GRP-05/06/07).
        """
        if not request.group_id:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, "empty group_id")
            return None  # WR-06: after abort, gRPC ignores the return value

        detector = request.detector
        if detector not in ("peer_divergence", "ecod", "copod", "pca", "iforest"):
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, f"unknown detector: {detector!r}")
            return None

        # RESEARCH V5 / T-05-09: same ragged-input guard as ScoreGroupBatch.
        # WR-01: also reject an empty series list (see ScoreGroupBatch comment).
        lengths = {len(s.values) for s in request.series}
        if not request.series or len(lengths) > 1:
            context.abort(
                grpc.StatusCode.INVALID_ARGUMENT,
                "empty series list" if not request.series else f"ragged series: mismatched value-array lengths {sorted(lengths)}",
            )
            return None

        try:
            group_slug = f"group_{request.group_id}"
            matrix = [list(col) for col in zip(*(s.values for s in request.series))]

            if detector == "peer_divergence" and len(request.series) == 2:
                # GRP-11: 2-member pairwise-delta path IS stateful (wraps
                # PyODDetector, which requires fit() before score_batch()) —
                # unlike the classic N>=3 no-op below. Persist via save_pyod
                # (not save_group_bundle — single derived feature, no scaler).
                from argus_detector.group.pairwise_delta import PairwiseDeltaDetector
                delta = PairwiseDeltaDetector.compute_delta(
                    request.series[0].values, request.series[1].values
                )
                model = PairwiseDeltaDetector.from_params(dict(request.params))
                model.fit(delta)
                # WR-02: fit happens above, outside any lock (mirrors fit_one's
                # train-outside-lock idiom); swap_model takes the per-entity
                # lock for just the atomic write, so a concurrent ScoreGroupBatch
                # reading via get_model() is synchronized per the registry's
                # documented concurrency contract (MDL-04) — register() only
                # takes the coarse dict-resize lock.
                self._registry.swap_model(group_slug, detector, model)
                version = self._model_store.next_version(group_slug, detector)
                self._model_store.save_pyod(
                    group_slug, detector, version, model, entity_id=group_slug
                )
                return argus_pb2.FitGroupResponse(ok=True)

            if detector == "peer_divergence":
                # Stateless — register without training, no persistence (RESEARCH.md
                # CONTEXT.md: peer_divergence Fit/Save is a no-op registration).
                self._registry.fit_one(group_slug, detector, matrix, params=dict(request.params))
                return argus_pb2.FitGroupResponse(ok=True)

            # Joint-multivariate: fit via the registry, then persist the bundle.
            version = self._model_store.next_version(group_slug, detector)
            self._registry.fit_one(group_slug, detector, matrix, params=dict(request.params))
            model = self._registry.get_model(group_slug, detector)
            self._model_store.save_group_bundle(request.group_id, detector, version, model.bundle())

            return argus_pb2.FitGroupResponse(ok=True)

        except Exception as e:
            logger.exception("unexpected error in FitGroup for %s", request.group_id)
            return argus_pb2.FitGroupResponse(ok=False, error=str(e))

    def Warmup(self, request, context):  # noqa: N802
        """Prime a cold detector from historical points (D-12/BACKFILL-01..03).

        Feeds request.history through registry.warmup_one, which owns the
        n_seen == 0 idempotency gate (D-12 — enforced detector-side so it
        holds regardless of caller). Never emits verdicts, never publishes
        anything; the only effect is model state plus the returned counters.
        """
        if not request.entity_id:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, "empty entity_id")
            return None  # WR-06: after abort, gRPC ignores the return value — return None

        try:
            entity_id = request.entity_id
            detector = request.detector or "hst"
            values = [p.value.value for p in request.history]

            warmed_up, n_seen, window, skipped = self._registry.warmup_one(
                entity_id, detector, values, params=dict(request.params)
            )

            # T-02-03: log only safe fields — never raw sensor values.
            logger.info(
                "warmup",
                extra={
                    "entity_id": entity_id,
                    "history_points": len(values),
                    "n_seen": n_seen,
                    "skipped": skipped,
                },
            )

            return argus_pb2.WarmupResponse(
                ok=True, n_seen=n_seen, warmed_up=warmed_up, skipped=skipped
            )

        except Exception as e:
            logger.exception("unexpected error in Warmup for %s", request.entity_id)
            return argus_pb2.WarmupResponse(ok=False, error=str(e))

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
