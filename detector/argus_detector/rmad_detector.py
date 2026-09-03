"""
Rolling median/MAD robust-z detector (D-A/D-B) — stdlib only.

RmadDetector: per-entity streaming *deviation* detector.
  - Rolling window of the last `window` raw readings (default 720).
  - Scale estimate = 1.4826 * MAD over that window, recomputed every tick.
    The window IS the calibration (D-M) — there is no separate calibration
    phase, no ECDF, no distribution observer.
  - score = z / (z + 5.0) where z = |value - median| / sigma.
    The score is therefore bounded, dimensionless and comparable across
    sensors: score > 0.5 <=> z > 5, score < 0.375 <=> z < 3 (D-B). One
    threshold table is arithmetically correct on every sensor regardless of
    unit or range — that is the removal of F6.
  - is_warmed_up / window report `min_samples` (60), not the baseline window,
    because min_samples is the gate that actually decides whether a verdict
    is emitted (D-M).

Why this exists (F4): river's HalfSpaceTrees scores *rarity*
(1 - mass/max_mass), so on a quantized series a rare-but-perfectly-normal
level outscores the modal level (101 W -> 0.997 vs modal 107 W -> 0.560).
No threshold or calibration layered on a rarity statistic can invert that.
RmadDetector computes deviation instead, which is the statistic the ground
truth (robust z over median/MAD) is defined in terms of.

Scale ladder (score_one step 3) — every rung was chosen from a measurement,
not from theory:
  1. sigma = 1.4826 * MAD                     (the normal case)
  2. sigma = MeanAD, when MAD == 0            (a window that is mostly one
     level: the 88%-zeros fridge power series. Half-the-smallest-gap was
     measured to give the fridge z=2.0 and ZERO firings, i.e. it destroys
     the only sensor with real precision.)
  3. sigma = max(sigma, scale_floor)          (scale_floor is in SENSOR
     UNITS; it damps rung 1 too, which is the rung that actually bites on
     1-decimal percent series: MAD=0.1 -> sigma=0.148 -> a 1.1 pp move is
     z=7.4. Measured: floor 0.0/0.05/0.1 all give 4 episodes / 7.02%
     on-time; 0.3 gives 0.)
  4. sigma still <= 0 => the window is a single constant. Return 0.0 when
     the reading equals it, 1.0 otherwise. Never divide by zero: the whole
     stream is aborted by servicer.py's blanket handler if this raises.

Thread safety: identical to EntityDetector — one instance per
(entity_id, detector) key, created under the registry lock, mutated from a
single thread thereafter. score_one mutates two containers (deque + sorted
list), so a concurrent deepcopy can observe a torn snapshot; __setstate__
rebuilds `_sorted` from `_values` to heal it (the race itself is unresolved
blocker #2 in docs/FIX-PLAN.md).
"""

from __future__ import annotations

import bisect
import logging
from collections import deque

logger = logging.getLogger(__name__)

# D-A/D-B defaults
_DEFAULT_WINDOW = 720
_DEFAULT_MIN_SAMPLES = 60
_DEFAULT_SCALE_FLOOR = 0.0

# NOT a parameter (D-B): z_scale and high_threshold are the same degree of
# freedom, so exposing both would be a tuning trap. 0.5 <=> z 5, 0.375 <=> z 3.
_Z_SCALE = 5.0

# MAD -> sigma consistency constant for a normal distribution.
_MAD_CONST = 1.4826

# Bumped whenever the pickled state layout changes; __setstate__ refuses a
# NEWER schema so a downgrade discards the checkpoint instead of misreading it.
_SCHEMA_VERSION = 1

# Params keys that carry the algorithm selection on the wire (params is a
# map<string,string>, so the orchestrator threads the detector name through
# it). They are never detector state and must not enter the apply_params
# fingerprint.
_WIRE_KEYS = ("algorithm", "detector")


def _cast_int(params: dict[str, str], key: str, default: int) -> int:
    """Cast a string param to int, returning default if key absent or invalid."""
    raw = params.get(key)
    if raw is None:
        return default
    try:
        return int(raw)
    except (ValueError, TypeError):
        return default


def _cast_float(params: dict[str, str], key: str, default: float) -> float:
    """Cast a string param to float, returning default if key absent or invalid."""
    raw = params.get(key)
    if raw is None:
        return default
    try:
        return float(raw)
    except (ValueError, TypeError):
        return default


def _median(sorted_vals: list[float]) -> float:
    """Median of an already-sorted list (statistics.median convention)."""
    n = len(sorted_vals)
    mid = n // 2
    if n % 2:
        return sorted_vals[mid]
    return (sorted_vals[mid - 1] + sorted_vals[mid]) / 2.0


def _mad_sorted(sorted_vals: list[float], med: float) -> float:
    """Exact median absolute deviation of a sorted window, without re-sorting.

    The deviations |x - med| are emitted in ascending order by a two-pointer
    outward march from the median: first the (hi - lo) exact ties as zeros,
    then whichever side is closer. Only ranks up to n // 2 are ever produced,
    so this is O(n / 2) instead of the O(n log n) of sorting a fresh
    deviation list on every tick.

    Equals statistics.median([abs(x - med) for x in sorted_vals]).
    """
    n = len(sorted_vals)
    if n == 0:
        return 0.0

    lo = bisect.bisect_left(sorted_vals, med)
    hi = bisect.bisect_right(sorted_vals, med)
    zeros = hi - lo

    left = lo - 1
    right = hi

    upper = n // 2
    want_lower = (n % 2 == 0)
    lower_dev = 0.0
    dev = 0.0

    for rank in range(upper + 1):
        if rank < zeros:
            dev = 0.0
        elif left >= 0 and right < n:
            d_left = med - sorted_vals[left]
            d_right = sorted_vals[right] - med
            if d_left <= d_right:
                dev = d_left
                left -= 1
            else:
                dev = d_right
                right += 1
        elif left >= 0:
            dev = med - sorted_vals[left]
            left -= 1
        else:
            dev = sorted_vals[right] - med
            right += 1

        if want_lower and rank == upper - 1:
            lower_dev = dev

    if want_lower:
        return (lower_dev + dev) / 2.0
    return dev


def _mean_ad(sorted_vals: list[float], med: float) -> float:
    """Mean absolute deviation — scale ladder rung 2, used only when MAD == 0."""
    n = len(sorted_vals)
    if n == 0:
        return 0.0
    total = 0.0
    for v in sorted_vals:
        total += v - med if v >= med else med - v
    return total / n


def _insert_into(
    values: deque[float], sorted_vals: list[float], value: float, window: int
) -> None:
    """Append `value` to both containers and evict everything past `window`.

    A `while` (not an `if`): after apply_params shrinks the window the surplus
    has to drain over one call, not one element per reading.
    """
    values.append(value)
    bisect.insort(sorted_vals, value)
    while len(values) > window:
        old = values.popleft()
        sorted_vals.pop(bisect.bisect_left(sorted_vals, old))


class RmadDetector:
    """Per-entity rolling-median/MAD robust-z detector (D-A).

    Usage::

        det = RmadDetector(window=720, min_samples=60)
        score = det.score_one(21.5)  # 0.0 until min_samples readings are in
        if det.is_warmed_up:
            ...  # score > 0.5 means the reading is more than 5 robust sigma out
    """

    def __init__(
        self,
        window: int = _DEFAULT_WINDOW,
        min_samples: int = _DEFAULT_MIN_SAMPLES,
        scale_floor: float = _DEFAULT_SCALE_FLOOR,
    ) -> None:
        self._schema: int = _SCHEMA_VERSION
        self._window: int = max(1, int(window))
        self._min_samples: int = max(1, int(min_samples))
        self._scale_floor: float = max(0.0, float(scale_floor))
        self._values: deque[float] = deque()
        self._sorted: list[float] = []
        self._n_seen: int = 0
        self._warned_degenerate: bool = False

    # -------------------------------------------------------------------------
    # Construction / reconfiguration
    # -------------------------------------------------------------------------

    @classmethod
    def from_params(cls, params: dict[str, str]) -> "RmadDetector":
        """Create an RmadDetector from a string params map (CONF-02).

        Supported keys: "window", "min_samples" (int), "scale_floor" (float).
        The wire-only keys ("algorithm"/"detector") are stripped first so they
        can never enter the apply_params fingerprint.
        """
        window, min_samples, scale_floor = cls._read_params(params)
        return cls(window=window, min_samples=min_samples, scale_floor=scale_floor)

    @staticmethod
    def _read_params(params: dict[str, str]) -> tuple[int, int, float]:
        clean = {k: v for k, v in params.items() if k not in _WIRE_KEYS}
        return (
            max(1, _cast_int(clean, "window", _DEFAULT_WINDOW)),
            max(1, _cast_int(clean, "min_samples", _DEFAULT_MIN_SAMPLES)),
            max(0.0, _cast_float(clean, "scale_floor", _DEFAULT_SCALE_FLOOR)),
        )

    def apply_params(self, params: dict[str, str]) -> bool:
        """Reconfigure a LIVE instance from a params map; True if anything changed.

        Without this, params reach a detector only at creation time
        (registry._get_or_create / model_store's checkpoint restore), so an
        operator editing the window in the UI would see no effect until the
        checkpoint was deleted. O(1) on the unchanged fast path — this runs on
        every point, under the registry's creation lock.
        """
        window, min_samples, scale_floor = self._read_params(params)
        if (window, min_samples, scale_floor) == (
            self._window,
            self._min_samples,
            self._scale_floor,
        ):
            return False

        self._window = window
        self._min_samples = min_samples
        self._scale_floor = scale_floor
        while len(self._values) > self._window:
            old = self._values.popleft()
            self._sorted.pop(bisect.bisect_left(self._sorted, old))
        return True

    # -------------------------------------------------------------------------
    # Scoring
    # -------------------------------------------------------------------------

    def _scale(self, sorted_vals: list[float], med: float) -> float:
        """Scale-ladder rungs 1-3. Returns sigma; <= 0.0 means rung 4 applies."""
        sigma = _MAD_CONST * _mad_sorted(sorted_vals, med)  # rung 1
        if sigma <= 0.0:
            sigma = _mean_ad(sorted_vals, med)  # rung 2
        if sigma < self._scale_floor:  # rung 3
            sigma = self._scale_floor
        return sigma

    def _score_from(self, sorted_vals: list[float], value: float) -> float:
        """Score `value` against a sorted window without touching it."""
        if len(sorted_vals) < self._min_samples:
            return 0.0

        med = _median(sorted_vals)
        sigma = self._scale(sorted_vals, med)
        if sigma <= 0.0:  # rung 4: the window is a single constant
            if value == med:
                return 0.0
            if not self._warned_degenerate:
                self._warned_degenerate = True
                logger.warning(
                    "degenerate scale: window is a single constant %r and the "
                    "reading %r differs; scoring 1.0 (scale_floor=%r would damp this)",
                    med, value, self._scale_floor,
                )
            return 1.0

        z = abs(value - med) / sigma
        return z / (z + _Z_SCALE)

    def score_one(self, value: float) -> float:
        """Score a single sensor reading, then learn it (score-then-learn).

        Returns exactly 0.0 while the window holds fewer than min_samples
        readings — the orchestrator suppresses the flag on !warmed_up anyway,
        and 0.0 survives the wire because Verdict.score is a DoubleValue.
        """
        score = self._score_from(self._sorted, value)
        _insert_into(self._values, self._sorted, value, self._window)
        self._n_seen += 1
        return score

    def score_batch(self, values: list[float]) -> list[float]:
        """Replay `values` through a COPY of the window; the live model is untouched.

        registry.score_batch hands out the live model reference, so a mutating
        batch score would corrupt the streaming baseline of a running entity.
        Returns a bare list — registry normalises that to (scores, None).
        """
        values_copy: deque[float] = deque(self._values)
        sorted_copy: list[float] = list(self._sorted)
        scores: list[float] = []
        for value in values:
            scores.append(self._score_from(sorted_copy, value))
            _insert_into(values_copy, sorted_copy, value, self._window)
        return scores

    def fit(self, values: list[float]) -> None:
        """Prime the live window from historical values (registry.fit_one path)."""
        for value in values:
            self.score_one(value)

    # -------------------------------------------------------------------------
    # Accessors read by the orchestrator (Verdict population)
    # -------------------------------------------------------------------------

    @property
    def is_warmed_up(self) -> bool:
        """True once min_samples readings have been processed (D-M)."""
        return self._n_seen >= self._min_samples

    @property
    def n_seen(self) -> int:
        """Number of readings processed so far (PERSIST-01/Verdict.n_seen)."""
        return self._n_seen

    @property
    def window(self) -> int:
        """The gate that actually applies: min_samples, NOT the baseline window.

        Verdict.window drives the "Rozgrzewka N/window" chip; reporting 720
        there would tell the operator to wait ~78 h on a 225-samples/day sensor
        for a verdict that arrives after 60 (D-M).
        """
        return self._min_samples

    @property
    def baseline_window(self) -> int:
        """Size of the rolling median/MAD window, in samples."""
        return self._window

    @property
    def scale_floor(self) -> float:
        """Floor on the scale estimate, in sensor units (D-I)."""
        return self._scale_floor

    # -------------------------------------------------------------------------
    # Pickle / deepcopy
    # -------------------------------------------------------------------------

    def __setstate__(self, state: dict) -> None:
        """Restore from a checkpoint (or a deepcopy), healing a torn snapshot.

        (a) A checkpoint written by a NEWER schema is refused with ValueError —
            model_store.load_all_into isolates that per entity, so a downgraded
            image re-warms one sensor instead of misreading its state.
        (b) Every field is setdefault-ed, so a checkpoint written before a field
            existed restores instead of raising AttributeError mid-stream.
        (c) `_sorted` is rebuilt whenever it disagrees with `_values`. score_one
            mutates both containers outside every lock while checkpoint_dirty
            deepcopies under the entity lock, so a snapshot can be torn. There
            is deliberately no raise here: __setstate__ also runs on every
            deepcopy, and the deque is the authoritative container.
        """
        schema = state.get("_schema", _SCHEMA_VERSION)
        if schema > _SCHEMA_VERSION:
            raise ValueError(
                f"RmadDetector checkpoint schema {schema} is newer than "
                f"the supported {_SCHEMA_VERSION}"
            )

        self.__dict__.update(state)
        self.__dict__.setdefault("_schema", _SCHEMA_VERSION)
        self.__dict__.setdefault("_window", _DEFAULT_WINDOW)
        self.__dict__.setdefault("_min_samples", _DEFAULT_MIN_SAMPLES)
        self.__dict__.setdefault("_scale_floor", _DEFAULT_SCALE_FLOOR)
        self.__dict__.setdefault("_n_seen", 0)
        self.__dict__.setdefault("_warned_degenerate", False)

        values = self.__dict__.setdefault("_values", deque())
        if not isinstance(values, deque):
            values = deque(values)
            self.__dict__["_values"] = values

        rebuilt = sorted(values)
        if self.__dict__.get("_sorted") != rebuilt:
            self.__dict__["_sorted"] = rebuilt
