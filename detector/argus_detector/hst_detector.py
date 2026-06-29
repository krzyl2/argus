"""
River HalfSpaceTrees wrapper with online min-max normalization.

EntityDetector: per-entity streaming anomaly detector.
  - Online min-max normalization (D-08, river.preprocessing.MinMaxScaler)
  - River HalfSpaceTrees scoring (D-09, default window=250, n_trees=25)
  - is_warmed_up tracks when n_seen >= window_size (PITFALL 8 mitigation)
  - from_params(): overrides from string params map (CONF-02)

Thread safety: each EntityDetector instance is owned by a single (entity_id, detector)
key in DetectorRegistry; the registry Lock guards creation only. Once an instance
exists it is used from a single thread per the gRPC ThreadPoolExecutor + per-stream
entity model.
"""

from __future__ import annotations

from river import anomaly, preprocessing

# D-09 defaults
_DEFAULT_WINDOW = 250
_DEFAULT_N_TREES = 25
_HEIGHT = 8
_SEED = 42


def _cast_int(params: dict[str, str], key: str, default: int) -> int:
    """Cast a string param to int, returning default if key absent or invalid."""
    raw = params.get(key)
    if raw is None:
        return default
    try:
        return int(raw)
    except (ValueError, TypeError):
        return default


class EntityDetector:
    """Per-entity streaming anomaly detector (River HalfSpaceTrees + MinMaxScaler).

    Usage::

        det = EntityDetector(window=250, n_trees=25)
        score = det.score_one(21.5)  # float in [0, 1] from HST
        if det.is_warmed_up:
            ...  # reliable scores
    """

    def __init__(self, window: int = _DEFAULT_WINDOW, n_trees: int = _DEFAULT_N_TREES) -> None:
        self._normalizer = preprocessing.MinMaxScaler()
        self._model = anomaly.HalfSpaceTrees(
            n_trees=n_trees,
            height=_HEIGHT,
            window_size=window,
            seed=_SEED,
        )
        self._n_seen: int = 0

    @classmethod
    def from_params(cls, params: dict[str, str]) -> "EntityDetector":
        """Create an EntityDetector from a string params map (CONF-02).

        Supported keys: "window", "n_trees" (both cast to int).
        Absent keys fall back to D-09 defaults.
        """
        window = _cast_int(params, "window", _DEFAULT_WINDOW)
        n_trees = _cast_int(params, "n_trees", _DEFAULT_N_TREES)
        return cls(window=window, n_trees=n_trees)

    def score_one(self, value: float) -> float:
        """Score a single sensor reading.

        Steps:
          1. Normalize via MinMaxScaler (learn + transform).
          2. Query HalfSpaceTrees score_one (pre-learn).
          3. Update HalfSpaceTrees with learn_one.
          4. Increment n_seen.

        Returns:
            Anomaly score in [0, 1] (higher = more anomalous).
        """
        x = {"value": value}
        self._normalizer.learn_one(x)
        x_norm = self._normalizer.transform_one(x)
        score: float = float(self._model.score_one(x_norm))
        self._model.learn_one(x_norm)
        self._n_seen += 1
        return score

    @property
    def is_warmed_up(self) -> bool:
        """True when at least window_size readings have been processed."""
        return self._n_seen >= self._model.window_size
