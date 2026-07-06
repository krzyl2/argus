# Phase 5: Group Detection Core (Proto + Python Detectors) - Pattern Map

**Mapped:** 2026-07-02
**Files analyzed:** 8 (new) + 4 (modified)
**Analogs found:** 8 / 8

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `proto/argus.proto` (modified) | config/schema | request-response | itself (existing `Point`/`ScoreBatchRequest`/`DetectorService`) | exact — additive to existing file |
| `detector/argus_detector/group/peer_divergence.py` | service (detector) | batch/transform | `detector/argus_detector/stl_detector.py` | exact — stateless, no fit(), `score_batch(values) -> (scores, error)` tuple contract |
| `detector/argus_detector/group/multivariate_detector.py` | service (detector) | batch/transform | `detector/argus_detector/pyod_detector.py` | exact — `fit()`/`score_batch()`/`is_fitted`, PyOD wrapper shape |
| `detector/argus_detector/model_store.py` (modified) | service (persistence) | file-I/O | itself (existing `save_pyod`/`load_pyod`/`load_all_into`) | exact — add `save_group_bundle`/`load_group_bundle`, extend glob scan |
| `detector/argus_detector/registry.py` (modified) | service (registry) | CRUD (in-memory) | itself (existing `_create_detector` factory + `fit_one`/`score_batch`) | exact — extend factory dispatch, key namespace |
| `detector/argus_detector/servicer.py` (modified) | controller (gRPC) | request-response | itself (existing `Fit`/`ScoreBatch` handlers) | exact — add `FitGroup`/`ScoreGroupBatch` handlers |
| `detector/tests/test_peer_divergence.py` | test | batch/transform | `detector/tests/test_stl_detector.py` (structure) + `test_pyod_detector.py` (class-grouped style) | exact |
| `detector/tests/test_group_multivariate.py` | test | batch/transform | `detector/tests/test_pyod_detector.py` | exact |
| `detector/tests/test_group_model_store.py` | test | file-I/O | `detector/tests/test_model_store.py` | exact |

## Pattern Assignments

### `proto/argus.proto` (modified)

**Analog:** itself — existing message/RPC shape (lines 1-80, full file read above)

**Convention to copy** (existing `ScoreBatchRequest`/`Verdict`/service block, lines 8-23, 37-48, 74-80):
```protobuf
syntax = "proto3";
package argus.v1;
option csharp_namespace = "Argus.Detector.V1";

import "google/protobuf/timestamp.proto";
import "google/protobuf/wrappers.proto";

message Verdict {
  string entity_id = 1;
  google.protobuf.DoubleValue score = 2;
  ...
  bool is_anomaly = 6;
  string detector = 7;
  google.protobuf.Timestamp timestamp = 8;
}

message ScoreBatchRequest {
  string entity_id = 1;
  string detector = 2;
  map<string, string> params = 3;
  repeated Point window = 4;
}

service DetectorService {
  rpc ScoreStream(stream Point) returns (stream Verdict);
  rpc Fit(FitRequest) returns (FitResponse);
  rpc ScoreBatch(ScoreBatchRequest) returns (ScoreBatchResponse);
  rpc SaveModel(SaveModelRequest) returns (SaveModelResponse);
  rpc LoadModel(LoadModelRequest) returns (LoadModelResponse);
}
```

**New additions (per CONTEXT.md/RESEARCH.md locked shape)** — append inside the same file, same `argus.v1` package, no new file:
```protobuf
message Series {
  string member_id = 1;
  repeated double values = 2;
}

message FeatureContribution {
  string member_id = 1;
  double contribution = 2;
}

message GroupScoreRequest {
  string group_id = 1;
  string detector = 2;          // "peer_divergence" | "ecod" | "copod" | "pca" | "iforest"
  map<string, string> params = 3;
  repeated Series series = 4;   // one Series per member; parallel value arrays, shared timestamp axis
}

message GroupScoreResponse {
  repeated Verdict per_member = 1;       // populated for peer-divergence mode
  Verdict group_verdict = 2;             // populated for joint-multivariate mode
  repeated FeatureContribution contributions = 3;  // ranked; empty for non-attributable detectors
  bool ok = 4;
  string error = 5;
}

message FitGroupRequest {
  string group_id = 1;
  string detector = 2;
  map<string, string> params = 3;
  repeated Series series = 4;
}

message FitGroupResponse {
  bool ok = 1;
  string error = 2;
}

service DetectorService {
  // ... existing RPCs unchanged ...
  rpc ScoreGroupBatch(GroupScoreRequest) returns (GroupScoreResponse);
  rpc FitGroup(FitGroupRequest) returns (FitGroupResponse);
}
```

**Regen step (mandatory, Pitfall 6 in RESEARCH.md):** after editing, run `python detector/scripts/gen_proto.py` before any Python test relying on new messages. `.NET` side regenerates automatically via MSBuild — no manual step there.

---

### `detector/argus_detector/group/peer_divergence.py` (new)

**Analog:** `detector/argus_detector/stl_detector.py`

**Module docstring + stateless-no-fit pattern** (stl_detector.py lines 1-18, 29-47):
```python
"""
STL residual-based batch anomaly detector (step-change / FAULT-03).
...
No persistent model — no fit() / no serialization needed
Thread safety: stateless; safe to call from multiple threads concurrently.
"""
from __future__ import annotations
import numpy as np

class StlDetector:
    """Stateless STL residual scorer. No fit(), no saved model."""

    def score_batch(self, values: list[float], period: int = _PERIOD_DAILY) -> tuple[list[float], str | None]:
        """
        Returns:
            (scores, None) on success.
            ([], error_string) when insufficient data.
        """
```

**Apply to `PeerDivergenceDetector`:** same "(scores_or_none, error_or_none)" tuple-return contract, but shaped for a 2D matrix. Use RESEARCH.md's verified `modified_zscore`/`score_group` functions directly (Code Examples section, lines 256-304 of 05-RESEARCH.md) — they are pre-verified against numpy, do not re-derive the MAD/meanAD constants. Wrap them in a class matching the `StlDetector` shape:
```python
class PeerDivergenceDetector:
    """Stateless robust peer-divergence scorer. No fit(), no saved model."""

    def score_batch(self, matrix: list[list[float]]) -> tuple[list[list[float]] | None, list[list[bool]] | None, str | None]:
        """matrix: (n_timestamps, n_members). Returns (scores, flags, error).
        error set (scores/flags None) when n_members < 3 (GRP-04 floor)."""
```
Note: return-shape differs from StlDetector's `(list[float], str|None)` because peer-divergence is 2D (per-member, per-timestamp) — match the *pattern* (stateless, tuple return, no exception for the "not enough data" case), not the exact signature.

---

### `detector/argus_detector/group/multivariate_detector.py` (new)

**Analog:** `detector/argus_detector/pyod_detector.py`

**Imports + fit/score_batch/is_fitted pattern** (pyod_detector.py lines 16-19, 37-111):
```python
from __future__ import annotations
import numpy as np
from pyod.models.mad import MAD  # -> swap for ECOD/COPOD/PCA/IForest via factory

class PyODDetector:
    def __init__(self, threshold=..., contamination=...) -> None:
        self._model = MAD(threshold=threshold, contamination=contamination)
        self._fitted = False

    def fit(self, values: list[float]) -> None:
        X = np.array(values, dtype=float).reshape(-1, 1)
        self._model.fit(X)
        self._fitted = True

    def score_batch(self, values: list[float]) -> list[float]:
        if not self._fitted:
            raise ValueError("fit() must be called before score_batch()")
        X = np.array(values, dtype=float).reshape(-1, 1)
        return self._model.decision_function(X).tolist()

    @property
    def is_fitted(self) -> bool:
        return self._fitted
```

**Apply to `GroupMultivariateDetector`:** same `fit()`/`score_batch()`/`is_fitted` shape and same "raise ValueError before fit" guard, but 2D matrix + RobustScaler + bundle persistence. Use RESEARCH.md's pre-verified `GroupMultivariateDetector` implementation verbatim (05-RESEARCH.md lines 313-385) — includes the `_DETECTOR_FACTORY` lazy-import-per-branch pattern (mirrors `registry.py`'s `_create_detector`), the `PCA(standardization=False)` pitfall fix, and the `self._model.O[-len(matrix):]` attribution-slicing pitfall fix. Do not re-derive; both pitfalls were verified by direct execution against installed packages.

**Error handling pattern to copy** (pyod_detector.py line 101-102): raise `ValueError("fit() must be called before score_batch()")` — same message convention, same exception type.

---

### `detector/argus_detector/model_store.py` (modified)

**Analog:** itself — existing `save_pyod`/`load_pyod`/`load_all_into`/`_model_dir` (lines 63-94, 127-144, 179-231, 236-237)

**Pattern to extend, not replace:**
```python
def save_pyod(self, entity_slug, detector, version, model, entity_id=None) -> None:
    d = self._model_dir(entity_slug, detector, version)
    d.mkdir(parents=True, exist_ok=True)
    joblib.dump(model, d / "model.joblib")
    self._write_version_json(d, entity_slug, detector, version)
    self._write_entity_id(d, entity_id if entity_id is not None else entity_slug)
    self._update_latest(entity_slug, detector, version)
    self._prune(entity_slug, detector)
```

**Add `save_group_bundle`/`load_group_bundle`:** same shape as `save_pyod`/`load_pyod` but dumps/loads the `{"scaler":..., "detector":..., "name":...}` dict (from `GroupMultivariateDetector.bundle()`/`from_bundle()`). Key builder must be a single helper (RESEARCH.md Pitfall 5) — e.g. `group_slug(group_id) -> f"group_{group_id}"` — never string-formatted ad hoc in multiple call sites. Peer-divergence Fit/Save is a no-op or config-only per CONTEXT.md — do not call `save_group_bundle` for `detector == "peer_divergence"`.

**`load_all_into` glob** (lines 196-231): existing `self._root.glob("*/*/latest")` already matches `group_{id}/{detector}/latest` structurally (RESEARCH.md confirms no schema validation needed — `group_` prefix is just another slug to this glob). No change required to the glob itself; only the load-time joblib bundle needs group-aware unpacking before calling `registry.register`.

---

### `detector/argus_detector/registry.py` (modified)

**Analog:** itself — existing `_create_detector` factory (lines 224-246) and `fit_one`/`score_batch` (lines 117-193)

**Factory dispatch pattern to extend** (lines 236-246):
```python
def _create_detector(self, detector: str) -> object:
    if detector in ("mad", "robust_zscore"):
        from argus_detector.pyod_detector import PyODDetector
        return PyODDetector()
    if detector == "stl":
        from argus_detector.stl_detector import StlDetector
        return StlDetector()
    if detector == "hst":
        return EntityDetector()
    raise ValueError(f"Unknown detector: {detector!r}")
```
Add `"peer_divergence"`, `"ecod"`, `"copod"`, `"pca"`, `"iforest"` branches — each a lazy import from `argus_detector.group.*`, same style. Per RESEARCH.md Open Question #2 recommendation: dispatch mode server-side purely on `detector` string, no new enum field.

**Key namespace pattern:** existing registry keys are `(entity_id, detector)` tuples. For groups, either (a) reuse the same dict with key `(f"group_{group_id}", detector)` — simplest, matches ModelStore's slug-as-string convention — or (b) a parallel `_group_detectors` dict. Recommend (a) for minimal surface change, consistent with `group_` prefix being the sole collision-avoidance mechanism (RESEARCH.md Pitfall 5).

**Stateless-detector special-case to mirror** (lines 132-139, the `stl` no-fit branch in `fit_one`):
```python
if detector == "stl":
    key = (entity_id, detector)
    lock = self._entity_lock(key)
    with lock:
        if key not in self._detectors:
            self._detectors[key] = self._create_detector(detector)
    return
```
Apply the identical special-case for `detector == "peer_divergence"` in the group fit path — register without training.

---

### `detector/argus_detector/servicer.py` (modified)

**Analog:** itself — existing `Fit`/`ScoreBatch` handlers (lines 95-172)

**Request validation + cold-start + error pattern to copy** (lines 100-135):
```python
def Fit(self, request, context):
    if not request.entity_id:
        context.abort(grpc.StatusCode.INVALID_ARGUMENT, "empty entity_id")
        return None  # WR-06: after abort, gRPC ignores the return value
    try:
        ...
        return argus_pb2.FitResponse(ok=True)
    except Exception as e:
        logger.exception("unexpected error in Fit for %s", request.entity_id)
        return argus_pb2.FitResponse(ok=False, error=str(e))
```

**Apply to `FitGroup`/`ScoreGroupBatch`:** same `context.abort(INVALID_ARGUMENT, ...)` for empty `group_id`, same try/except wrapping with `logger.exception` + `ok=False, error=str(e)` response shape. Additional validation per RESEARCH.md Security Domain (V5): validate all `Series.values` have identical length before building the numpy matrix — abort `INVALID_ARGUMENT` with a clear message on ragged input (no existing analog for this specific check; new code, but same `context.abort` idiom as line 101/134).

**Verdict-building pattern to copy** (lines 155-167, `ScoreBatch`):
```python
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
```
Reuse verbatim for `GroupScoreResponse.per_member` (peer-divergence, one Verdict per member) and `GroupScoreResponse.group_verdict` (joint-multivariate, single Verdict). `is_anomaly=False` convention holds — orchestrator decides flagging, Phase 5 emits only scores (per CONTEXT.md's flag threshold `|z|>3.5` is a Phase-5-computed default but final `is_anomaly` gating logic mirrors the existing "orchestrator decides" comment, unless CONTEXT.md's own threshold IS the flag source — confirm at plan time whether peer-divergence sets `is_anomaly` directly since GRP-03's threshold is locked in Phase 5, unlike per-entity hysteresis which is Phase 6+ orchestrator logic).

**Model dispatch helper pattern to copy** (`_save_model_to_store`, lines 219-233): same `isinstance(model, X)` branching style — extend with a `GroupMultivariateDetector` / peer-divergence branch, or add a dedicated `_save_group_model_to_store` mirroring the same shape.

---

### `detector/tests/test_peer_divergence.py` (new)

**Analog:** `detector/tests/test_stl_detector.py` (stateless-detector test shape) + `detector/tests/test_pyod_detector.py` (class-grouped naming convention, lines 1-17, 18-43)

**Test class grouping convention to copy** (test_pyod_detector.py lines 18, 45, 83):
```python
class TestPyODDetectorFitScore: ...
class TestPyODDetectorFromParams: ...
class TestPyODDetectorEdgeCases: ...
```
Mirror as `TestPeerDivergenceScoring`, `TestPeerDivergenceFloor`, `TestPeerDivergenceEdgeCases` (MAD=0 guard, meanAD fallback, below-floor NaN/no-verdict — per CONTEXT.md "Specifics" and RESEARCH.md Pitfalls 3/4). Use the exact hand-verified fixtures from 05-RESEARCH.md Code Examples (lines 540-554) for the MAD=0/meanAD case — already numerically confirmed, do not re-derive.

---

### `detector/tests/test_group_multivariate.py` (new)

**Analog:** `detector/tests/test_pyod_detector.py` (full file, structure above)

**Fixture to copy verbatim** (05-RESEARCH.md lines 556-607, "verified in this session"): the RobustScaler+ECOD joblib round-trip and the jointly-abnormal-but-marginally-normal `X_train`/`X_test_joint_anomaly` fixture. This is the required "independent verifiability" test per CONTEXT.md Specifics — a vector no single feature would trigger, caught only by the 2D joint detector.

**Mixed-units fixture (GRP-06 required test):** reuse the `[hPa, %RH]` shaped array from RESEARCH.md line 565 (`[[1000.0, 45.0], [1010.0, 50.0], ...]`) to prove RobustScaler prevents one feature dominating.

---

### `detector/tests/test_group_model_store.py` (new)

**Analog:** `detector/tests/test_model_store.py`

Not read in full this pass (file exists at `detector/tests/test_model_store.py`, mirrors `model_store.py`'s save/load/prune/load_all_into API 1:1). Mirror its structure for `save_group_bundle`/`load_group_bundle`, plus the explicit collision test noted in RESEARCH.md Pitfall 5 (a per-entity slug literally named `group_x` vs. a group model with `group_id=x` — documented edge case, one test).

## Shared Patterns

### Stateless-detector no-fit registration
**Source:** `detector/argus_detector/registry.py` lines 132-139 (the `stl` special-case in `fit_one`)
**Apply to:** `peer_divergence` detector registration in the group fit path — register without training, mirror the identical branch shape.

### PyOD wrapper fit/score_batch/is_fitted contract
**Source:** `detector/argus_detector/pyod_detector.py` lines 53-111
**Apply to:** `GroupMultivariateDetector` — same method names, same `ValueError("fit() must be called before score_batch()")` guard message.

### gRPC handler validate/try-except/error-response shape
**Source:** `detector/argus_detector/servicer.py` lines 100-125 (`Fit`) and 133-172 (`ScoreBatch`)
**Apply to:** `FitGroup` and `ScoreGroupBatch` — `context.abort(INVALID_ARGUMENT, ...)` for missing `group_id` / ragged series; `try/except Exception` wrapping with `logger.exception` + `ok=False, error=str(e)` on the response message.

### Versioned joblib persistence with atomic latest pointer
**Source:** `detector/argus_detector/model_store.py` lines 63-94 (`save_pyod`), 261-270 (`_update_latest`)
**Apply to:** new `save_group_bundle`/`load_group_bundle` — same `.tmp` + `Path.replace()` atomic-write pattern, same `version.json` sidecar, same 3-version retention via `_prune`.

### Lazy-import factory dispatch
**Source:** `detector/argus_detector/registry.py` lines 236-246 (`_create_detector`)
**Apply to:** group detector factory (`peer_divergence`/`ecod`/`copod`/`pca`/`iforest`) and RESEARCH.md's `_DETECTOR_FACTORY` dict (lines 317-322) — both use lazy per-branch imports to avoid importing all PyOD submodules eagerly.

## No Analog Found

None — all 8 new/modified files have a strong existing-codebase analog. The only genuinely novel algorithmic work (modified z-score, RobustScaler bundling) is pre-verified in 05-RESEARCH.md's Code Examples section and should be copied from there rather than re-derived.

## Metadata

**Analog search scope:** `detector/argus_detector/*.py`, `detector/tests/*.py`, `proto/argus.proto`
**Files scanned:** `pyod_detector.py`, `stl_detector.py`, `model_store.py`, `registry.py`, `servicer.py`, `argus.proto`, `test_pyod_detector.py`, test file listing (13 files in `detector/tests/`)
**Pattern extraction date:** 2026-07-02
