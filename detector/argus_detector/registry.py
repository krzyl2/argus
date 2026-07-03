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
import threading

from argus_detector.hst_detector import EntityDetector


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
