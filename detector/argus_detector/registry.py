"""
DetectorRegistry — maps (entity_id, detector) to per-entity detector instances.

Thread safety:
  - self._lock: guards lazy creation of entries in _detectors and _entity_locks dicts.
  - self._entity_locks: per-(entity_id, detector) lock guards atomic model swap (MDL-04).
  - fit_one(): acquires entity lock to snapshot current, trains OUTSIDE lock, swaps under lock.
  - score_batch(): acquires entity lock to read model ref, scores OUTSIDE lock.

T-06-01: threading.Lock guards creation; per-entity instances isolate state.
MDL-04: per-entity locks for Fit vs ScoreStream concurrency.
"""

from __future__ import annotations

import copy
import logging
import threading
import time

from argus_detector.hst_detector import EntityDetector

logger = logging.getLogger(__name__)


class DetectorRegistry:
    """Registry keyed by (entity_id, detector_name) -> detector instance.

    score_one(entity_id, value, detector="hst", params=None) -> float
      Lazily creates an EntityDetector on first sight of a (entity_id, detector) pair.
      Returns the anomaly score from that detector.

    is_warmed_up(entity_id, detector="hst") -> bool
      Returns whether the entity's detector has processed >= window_size readings.

    fit_one(entity_id, detector, values) -> None
      Trains a model on values. Uses deep-copy + atomic swap (MDL-04).

    score_batch(entity_id, detector, values) -> tuple[list[float], str | None]
      Reads model ref under lock; scores outside lock.

    has_model(entity_id, detector) -> bool
    register(entity_id, detector, model_obj) -> None
      Direct injection (used by ModelStore.load_all_into).

    _create_detector(detector) -> object
      Factory: "mad"/"robust_zscore" -> PyODDetector; "stl" -> StlDetector; "hst" -> EntityDetector;
      "peer_divergence" -> PeerDivergenceDetector; "ecod"/"copod"/"pca"/"iforest" -> GroupMultivariateDetector.

    Group entries (Plan 05-04, GRP-03..07) reuse this same registry/_detectors dict —
    keyed as (group_slug, detector) where group_slug = f"group_{group_id}" (see
    model_store.group_slug()). The "group_" prefix is the sole collision-avoidance
    mechanism against per-entity keys (RESEARCH.md Pitfall 5); callers (servicer.py)
    are responsible for passing an already-namespaced slug as the "entity_id" arg.
    """

    def __init__(self) -> None:
        # T-06-01: Lock guards lazy creation only
        self._lock = threading.Lock()
        self._detectors: dict[tuple[str, str], object] = {}
        # MDL-04: per-(entity_id, detector) locks for fit_one / score_batch concurrency
        self._entity_locks: dict[tuple[str, str], threading.Lock] = {}
        # D-05: last n_seen written to disk per key — lives on the registry,
        # never on the pickled EntityDetector (RESEARCH.md anti-pattern note:
        # storing it on the model would restore a stale baseline on every
        # restart and corrupt the first dirty check after boot).
        self._last_checkpointed: dict[tuple[str, str], int] = {}

    def _get_or_create(
        self,
        entity_id: str,
        detector: str,
        params: dict[str, str] | None,
    ) -> EntityDetector:
        key = (entity_id, detector)
        # T-06-01: always hold the lock for both read and write to avoid
        # unsafe concurrent dict access during a resize (WR-01).
        with self._lock:
            if key not in self._detectors:
                self._detectors[key] = EntityDetector.from_params(params or {})
            return self._detectors[key]

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

    def get_warmup_state(
        self, entity_id: str, detector: str = "hst"
    ) -> tuple[bool, int, int]:
        """Return (warmed_up, n_seen, window) for (entity_id, detector).

        Returns (False, 0, 0) when no entry exists — the 0 window is
        deliberate (RESEARCH.md Pitfall 4): the caller (15-02's Verdict
        population) treats 0 as "detector has no opinion yet" rather than
        inventing a 250 default.

        Read under the per-(entity_id, detector) lock the way get_model does,
        so this cannot race a concurrent fit_one/checkpoint_dirty swap.
        """
        key = (entity_id, detector)
        lock = self._entity_lock(key)
        with lock:
            det = self._detectors.get(key)
            if det is None:
                return (False, 0, 0)
            return (det.is_warmed_up, det.n_seen, det.window)

    # -------------------------------------------------------------------------
    # Batch methods (Phase 2 — MDL-04)
    # -------------------------------------------------------------------------

    def _entity_lock(self, key: tuple[str, str]) -> threading.Lock:
        """Return the per-(entity_id, detector) lock, creating it if needed.

        Uses self._lock to guard creation — safe under concurrent calls.
        """
        with self._lock:
            if key not in self._entity_locks:
                self._entity_locks[key] = threading.Lock()
            return self._entity_locks[key]

    def _hst_keys(self) -> list[tuple[str, str]]:
        """Snapshot list of registry keys whose detector is "hst".

        Taken under self._lock so the checkpoint sweep never iterates a dict
        another thread may resize.
        """
        with self._lock:
            return [key for key in self._detectors if key[1] == "hst"]

    def checkpoint_dirty(self, model_store: object) -> int:
        """Write a checkpoint for every dirty "hst" entity (D-05/D-06).

        Dirty = current n_seen differs from the value recorded at the last
        successful checkpoint. For each dirty entity: snapshot (deepcopy)
        under the entity lock, then pickle + atomic write OUTSIDE the lock —
        never hold _entity_lock across file I/O (D-06). A per-entity yield
        (time.sleep(0)) follows each write: the measured 56-96ms deepcopy
        cost at defaults means N dirty entities in one tick would otherwise
        show up as cumulative lock-holding / a periodic ScoreStream latency
        spike (RESEARCH.md Pitfall 1) — this is baseline design, not a
        conditional optimization.

        A save_checkpoint failure for one entity is logged (WARN,
        exc_info=True) and does not prevent the remaining dirty entities from
        being written, mirroring load_all_into's fault-isolation shape; the
        failing entity's _last_checkpointed is not advanced, so it is retried
        on the next tick.

        Args:
            model_store: Object with a save_checkpoint(entity_slug, detector,
                model, entity_id, n_seen) method.

        Returns:
            Count of entities actually written this call.
        """
        written = 0
        for key in self._hst_keys():
            entity_id, detector = key
            lock = self._entity_lock(key)

            with lock:
                det = self._detectors.get(key)
                if det is None:
                    continue
                current_n_seen = det.n_seen
                if current_n_seen == self._last_checkpointed.get(key):
                    continue  # D-05: not dirty — skip
                snapshot = copy.deepcopy(det)  # under lock (D-06) — MEASURED 56-96ms

            # Pickle + atomic write happen OUTSIDE the lock (D-06).
            try:
                model_store.save_checkpoint(
                    entity_id.replace(".", "_"), detector, snapshot, entity_id, current_n_seen
                )
            except Exception:
                logger.warning(
                    "Failed to write checkpoint for entity_id=%s detector=%s; skipping",
                    entity_id, detector,
                    exc_info=True,
                )
                continue

            self._last_checkpointed[key] = current_n_seen
            written += 1
            time.sleep(0)  # Pitfall 1: yield between entities — deepcopy is 56-96ms at defaults

        return written

    def warmup_one(
        self,
        entity_id: str,
        detector: str,
        values: list[float],
        params: dict[str, str] | None = None,
    ) -> tuple[bool, int, int, bool]:
        """Prime a cold detector from historical values (D-12/BACKFILL-01..03).

        The n_seen == 0 gate lives HERE, not in the servicer: D-12 requires it
        to hold no matter who calls (orchestrator restart, CFG-04 hot-reload,
        or any future tool), and this mirrors _get_or_create's "registry owns
        the gate" idiom (RESEARCH.md Open Question 2). The whole check-then-
        prime is held under the per-entity lock so it is atomic — deviating
        from the usual train-outside-lock idiom (fit_one/checkpoint_dirty) is
        correct here because this call happens once, before the entity's
        ScoreStream opens, and the work is bounded by the (small) window size;
        without holding the lock across the feed, two concurrent Warmup calls
        for the same cold entity could both pass the n_seen==0 check and
        double-prime it.

        Deliberately does NOT set _last_checkpointed for the primed key —
        leaving it unset makes the primed entity dirty, so the next
        checkpoint_dirty tick persists the prime (SC-7 survives a subsequent
        restart without a second Influx round trip).

        Args:
            entity_id: HA entity ID.
            detector: Detector name (e.g. "hst").
            values: Historical values, chronologically ascending.
            params: optional string param overrides, applied only when a
                fresh detector is created (mirrors fit_one's semantics).

        Returns:
            (warmed_up, n_seen, window, skipped) — skipped is True when an
            existing entry already had n_seen > 0 (already primed or
            restored from a checkpoint); in that case n_seen/window/warmed_up
            reflect the EXISTING entry, untouched.
        """
        key = (entity_id, detector)
        lock = self._entity_lock(key)
        with lock:
            existing = self._detectors.get(key)
            if existing is not None and existing.n_seen > 0:
                return (existing.is_warmed_up, existing.n_seen, existing.window, True)

            det = existing if existing is not None else EntityDetector.from_params(params or {})
            for value in values:
                det.score_one(value)
            self._detectors[key] = det
            return (det.is_warmed_up, det.n_seen, det.window, False)

    def fit_one(
        self,
        entity_id: str,
        detector: str,
        values: list[float],
        params: dict[str, str] | None = None,
    ) -> None:
        """Train a model on values using train-outside-lock pattern (MDL-04).

        Snapshots the current model under the entity lock, deep-copies it
        (or creates a fresh one), trains outside the lock, then swaps atomically.

        StlDetector is stateless and has no fit() method — for "stl", this method
        registers the detector instance without fitting (WR-01).

        Args:
            entity_id: HA entity ID.
            detector: Detector name ("mad", "robust_zscore", "stl", "hst").
            values: Training values.
            params: optional string param overrides, only applied when a fresh
                detector is created (mirrors the D-06-01 optional-3rd-param
                precedent) — default None keeps all existing per-entity call
                sites unchanged.
        """
        # WR-01: StlDetector is stateless; it has no fit() method.
        # peer_divergence (GRP-03/04) is likewise stateless — no fit() method
        # (mirrors the stl no-fit branch, per 05-PATTERNS.md).
        # Register it as-is so score_batch can use it.
        if detector in ("stl", "peer_divergence"):
            key = (entity_id, detector)
            lock = self._entity_lock(key)
            with lock:
                if key not in self._detectors:
                    self._detectors[key] = self._create_detector(detector, params)
            return

        key = (entity_id, detector)
        lock = self._entity_lock(key)

        # Snapshot current model reference under lock
        with lock:
            current = self._detectors.get(key)

        # Deep-copy before training — CPU-bound; runs OUTSIDE lock (MDL-04).
        # CR-01: joint-multivariate detectors (ecod/copod/pca/iforest) are
        # always refit from scratch nightly anyway — there is no warm-start
        # state worth preserving across a param change. Deep-copying `current`
        # here would silently discard a changed `params` (e.g. an operator's
        # sensitivity preset change), since the stale instance already baked
        # its old params into its constructor. Always reconstruct via the
        # factory for these so `params` actually takes effect on re-fit.
        if current is not None and detector in ("ecod", "copod", "pca", "iforest"):
            candidate = self._create_detector(detector, params)
        else:
            candidate = copy.deepcopy(current) if current else self._create_detector(detector, params)
        candidate.fit(values)

        # Atomic swap
        with lock:
            self._detectors[key] = candidate

    def score_batch(
        self, entity_id: str, detector: str, values: list[float]
    ) -> tuple[list[float], str | None]:
        """Score a batch of values for (entity_id, detector).

        Reads the model reference under the entity lock; scoring runs outside.

        Args:
            entity_id: HA entity ID.
            detector: Detector name.
            values: Values to score.

        Returns:
            (scores, None) on success — list[float], one per input value.
            ([], error_string) when the model signals insufficient data (StlDetector).

        Raises:
            ValueError: if no model exists for (entity_id, detector).
                        Cold-start logic is the servicer's responsibility.
        """
        key = (entity_id, detector)
        lock = self._entity_lock(key)

        # Read model ref under lock (O(1)); score outside lock
        with lock:
            model = self._detectors.get(key)

        if model is None:
            raise ValueError(
                f"No model for {key!r}; call fit_one first (cold-start is servicer's job)"
            )

        # StlDetector already returns tuple[list[float], str | None]
        # PyODDetector returns list[float] — normalise to tuple
        result = model.score_batch(values)
        if isinstance(result, tuple):
            return result
        return result, None

    def has_model(self, entity_id: str, detector: str) -> bool:
        """True if a model exists for (entity_id, detector)."""
        return (entity_id, detector) in self._detectors

    def get_model(self, entity_id: str, detector: str) -> object | None:
        """Return the model for (entity_id, detector), or None if not present.

        Reads under the per-entity lock to avoid TOCTOU races with fit_one (WR-02).
        """
        key = (entity_id, detector)
        lock = self._entity_lock(key)
        with lock:
            return self._detectors.get(key)

    def register(self, entity_id: str, detector: str, model_obj: object) -> None:
        """Directly set a model in the registry (used by ModelStore.load_all_into).

        No training — model must already be fitted. Safe for CPython due to GIL
        on dict assignment; also guards under self._lock for clarity.

        Args:
            entity_id: HA entity ID (or slug — caller normalises).
            detector: Detector name.
            model_obj: Fitted model instance.
        """
        key = (entity_id, detector)
        with self._lock:
            self._detectors[key] = model_obj

    def register_checkpoint(
        self, entity_id: str, detector: str, model_obj: object, n_seen: int
    ) -> None:
        """Directly set a restored checkpoint model in the registry (D-09).

        Like register(), but additionally seeds _last_checkpointed with the
        checkpoint's saved n_seen, so the checkpoint writer's first dirty
        check after a restart does not immediately consider this
        just-restored entity dirty and rewrite it unnecessarily.

        Args:
            entity_id: HA entity ID (dots intact).
            detector: Detector name (e.g. "hst").
            model_obj: Restored, already-fitted detector instance.
            n_seen: The n_seen recorded in the checkpoint's sidecar at save time.
        """
        key = (entity_id, detector)
        with self._lock:
            self._detectors[key] = model_obj
            self._last_checkpointed[key] = n_seen

    def swap_model(self, entity_id: str, detector: str, model_obj: object) -> None:
        """Atomically swap in an already-fitted model, under the per-entity lock (WR-02).

        Unlike register() (self._lock only — dict-resize guard), this takes the
        same per-(entity_id, detector) _entity_lock that fit_one()/get_model()
        use, so a concurrent get_model() reader is properly synchronized against
        this writer per the class's documented concurrency contract (MDL-04).
        Intended for live-RPC call sites (e.g. FitGroup's 2-member pairwise-delta
        path) that already train the model outside any lock and only need the
        final atomic swap — register() remains for the LoadModel/bulk-load path.

        Args:
            entity_id: HA entity ID (or slug — caller normalises).
            detector: Detector name.
            model_obj: Fitted model instance.
        """
        key = (entity_id, detector)
        lock = self._entity_lock(key)
        with lock:
            self._detectors[key] = model_obj

    def _create_detector(
        self, detector: str, params: dict[str, str] | None = None
    ) -> object:
        """Factory: map detector name to a fresh (unfitted) detector instance.

        Args:
            detector: "mad" | "robust_zscore" | "stl" | "hst" |
                      "peer_divergence" | "ecod" | "copod" | "pca" | "iforest"
            params: optional string param overrides (ALGO-01/02) — threaded
                into the two group-detector branches; ignored by per-entity
                branches (default None keeps existing call sites unchanged).

        Returns:
            Fresh detector instance.

        Raises:
            ValueError: if detector name is not recognised.
        """
        if detector in ("mad", "robust_zscore"):
            # CRITICAL FINDING: RobustZScore does NOT exist in PyOD 3.6.0.
            # Both names map to PyODDetector(MAD) — see RESEARCH.md Pitfall 2.
            from argus_detector.pyod_detector import PyODDetector  # lazy import
            return PyODDetector()
        if detector == "stl":
            from argus_detector.stl_detector import StlDetector  # lazy import
            return StlDetector()
        if detector == "hst":
            return EntityDetector()
        if detector == "peer_divergence":
            # GRP-03/04: stateless cross-member robust-statistic scorer.
            from argus_detector.group.peer_divergence import PeerDivergenceDetector  # lazy import
            return PeerDivergenceDetector.from_params(params or {})
        if detector in ("ecod", "copod", "pca", "iforest"):
            # GRP-05/06: RobustScaler + PyOD joint-multivariate wrapper.
            from argus_detector.group.multivariate_detector import GroupMultivariateDetector  # lazy import
            return GroupMultivariateDetector(detector, params or {})
        raise ValueError(f"Unknown detector: {detector!r}")
