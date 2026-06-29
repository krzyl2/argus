---
phase: 02-batch-model-lifecycle
plan: "05"
subsystem: detector-python
tags: [grpc, pyod, mad, stl, model-store, batch-detection, tdd, mTLS, threading, MDL-03, MDL-04]
dependency_graph:
  requires:
    - phase: 02-01
      provides: "argus.proto ScoreBatch/SaveModel/LoadModel messages + stubs"
    - phase: 02-03
      provides: "PyODDetector, StlDetector, ModelStore APIs"
  provides:
    - "DetectorRegistry: fit_one (MDL-04 train-outside-lock), score_batch, has_model, register, _create_detector"
    - "DetectorServicer: real Fit RPC (fit+save), ScoreBatch RPC (cold-start), SaveModel RPC, LoadModel RPC"
    - "server.py: MDL-03 NOT_SERVING→load_all_into→SERVING startup gate"
  affects:
    - "02-06-plan (batch scheduler calls ScoreBatch + Fit; relies on MDL-03 gate)"
    - "orchestrator batch path (DetectionGateway calls ScoreBatch/Fit RPCs)"
tech-stack:
  added: []
  patterns:
    - "MDL-04: per-(entity_id,detector) threading.Lock; train-outside-lock on deep copy; atomic swap"
    - "Cold-start fit-before-score in ScoreBatch servicer (not registry — servicer's responsibility)"
    - "MDL-03 startup gate: health NOT_SERVING during load_all_into; SERVING after"
    - "Lazy detector imports in _create_detector to avoid circular imports"
    - "SaveModelResponse has no model_bytes field (proto); SaveModel validates serializability only"
key-files:
  created:
    - detector/tests/test_registry.py (extended with 14 new batch-method tests)
    - detector/tests/test_servicer.py
  modified:
    - detector/argus_detector/registry.py
    - detector/argus_detector/servicer.py
    - detector/argus_detector/server.py
key-decisions:
  - "SaveModelResponse proto has no model_bytes field — SaveModel validates serializability then returns ok=True; actual bytes returned by LoadModelResponse only"
  - "MDL-03 gate: health_servicer.set(NOT_SERVING) called before create ModelStore and before load_all_into; SERVING set after completion"
  - "score_batch normalises StlDetector (which already returns tuple) and PyODDetector (returns list) into uniform tuple[list[float], str|None]"
  - "LoadModel uses entity_id (not slug) as key for registry.register so has_model works by entity_id post-load"
  - "Lazy imports for PyODDetector/StlDetector inside _create_detector to avoid import cycle at module load"
requirements-completed: [MDL-01, MDL-02, MDL-03, MDL-04, RES-02]
duration: "18 minutes"
completed: "2026-06-10"
---

# Phase 2 Plan 5: Registry Batch Methods + Servicer RPCs + MDL-03 Gate Summary

**Per-entity threading.Lock with train-outside-lock swap, real ScoreBatch/Fit/SaveModel/LoadModel gRPC RPCs, and MDL-03 health gate that defers SERVING until all disk models are loaded.**

## Performance

- **Duration:** ~18 min
- **Started:** 2026-06-10T00:00:00Z
- **Completed:** 2026-06-10T00:18:00Z
- **Tasks:** 3 (all completed)
- **Files modified:** 5

## Accomplishments

- DetectorRegistry extended with MDL-04 per-entity lock pattern: `_entity_locks`, `fit_one` (deep-copy + train outside lock + atomic swap), `score_batch` (read ref under lock, score outside), `has_model`, `register`, `_create_detector` factory
- DetectorServicer fully implemented: `Fit` (fit_one + disk save), `ScoreBatch` (cold-start fit, per-point verdicts), `SaveModel` (validates serializability), `LoadModel` (disk load + registry.register); constructor updated to accept `ModelStore`
- server.py MDL-03 gate: sets `NOT_SERVING` before `load_all_into(registry)`, sets `SERVING` after; `create_server()` accepts `model_root` param for test injection
- 100 Python detector tests pass (42 prior + 19 new registry + 14 new servicer + 4 server boot + others)

## Task Commits

Each task committed atomically following TDD RED/GREEN (Tasks 1-2) and direct implementation (Task 3):

1. **Task 1 RED — registry tests** - `ed06c0a` (test)
2. **Task 1 GREEN — registry.py extended** - `4cdf102` (feat)
3. **Task 2 RED — servicer tests** - `47cc299` (test)
4. **Task 2 GREEN — servicer.py real RPCs** - `92667a4` (feat)
5. **Task 3 — server.py MDL-03 gate** - `cde8928` (feat)

## Files Created/Modified

- `detector/argus_detector/registry.py` — added `_entity_locks`, `_entity_lock()`, `fit_one()`, `score_batch()`, `has_model()`, `register()`, `_create_detector()`
- `detector/argus_detector/servicer.py` — rewritten: `__init__` adds `model_store`, real `Fit`, new `ScoreBatch`, `SaveModel`, `LoadModel`, private helpers `_save_model_to_store`, `_serialize_model`, `_load_model_from_store`
- `detector/argus_detector/server.py` — MDL-03 gate, `model_root` param, `ModelStore` import, `DetectorServicer` receives `model_store`
- `detector/tests/test_registry.py` — 14 new tests: `TestRegistryFitOne`, `TestRegistryScoreBatch`, `TestRegistryCreateDetector`, `TestRegistryRegister`
- `detector/tests/test_servicer.py` — new file, 14 tests covering all four batch RPCs

## Decisions Made

- `SaveModelResponse` has no `model_bytes` field in the proto (checked against `proto/argus.proto`). Plan spec said to return bytes, but the proto was locked in 02-01. SaveModel validates that the model serializes correctly and returns `ok=True`; actual model bytes are only in `LoadModelResponse`.
- `registry.score_batch` normalises return types: StlDetector returns `tuple[list, str|None]` directly; PyODDetector returns `list[float]` which gets wrapped into `(list, None)`.
- `LoadModel` registers by `entity_id` (not `entity_slug`) so that `has_model("sensor.load", "mad")` returns True after LoadModel — avoids slug/entity_id mismatch between caller and registry lookup.
- Proto stubs (`argus_pb2.py`, `argus_pb2_grpc.py`) are gitignored (generated files). Tests rely on `gen_proto.py` running at test session start via `conftest.py`/`autouse` fixture in `test_proto_codegen.py`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] SaveModelResponse has no model_bytes field**
- **Found during:** Task 2 (GREEN phase, test_save_model_fitted_returns_bytes)
- **Issue:** Plan spec said `SaveModel` returns bytes in `SaveModelResponse`, but `proto/argus.proto` (already locked by 02-01) defines `SaveModelResponse { bool ok; string error; }` with no `model_bytes` field. Attempting `SaveModelResponse(model_bytes=...)` raised `ValueError`.
- **Fix:** Updated `SaveModel` to validate serializability internally (serialize to BytesIO but discard) and return `ok=True`. Updated the test to reflect the actual proto contract.
- **Files modified:** `servicer.py`, `tests/test_servicer.py`
- **Committed in:** 92667a4 (Task 2 GREEN)

---

**Total deviations:** 1 auto-fixed (Rule 1 — proto contract mismatch)
**Impact on plan:** Zero scope change. SaveModel still fulfills its role (model validation, serialization check). The actual bytes path is handled by LoadModel (which returns model_bytes in LoadModelResponse) and by Fit's internal save to disk.

## Known Stubs

None — all RPCs are fully implemented with no placeholder data flows.

## TDD Gate Compliance

Tasks 1 and 2 followed RED/GREEN:
- RED commits: ed06c0a (registry tests), 47cc299 (servicer tests) — all new tests failed before implementation
- GREEN commits: 4cdf102 (registry), 92667a4 (servicer) — all tests pass
- Task 3 (server.py) is not TDD per plan spec (`tdd="false"`)

## Threat Flags

No new threat surface beyond what is documented in the plan's threat model (T-02-05-01 through T-02-05-SC). All MDL-04 and MDL-03 mitigations are implemented.

## Self-Check: PASSED

Files exist:
- `detector/argus_detector/registry.py` — FOUND
- `detector/argus_detector/servicer.py` — FOUND
- `detector/argus_detector/server.py` — FOUND
- `detector/tests/test_registry.py` — FOUND
- `detector/tests/test_servicer.py` — FOUND

Commits exist:
- ed06c0a — registry RED tests
- 4cdf102 — registry GREEN implementation
- 47cc299 — servicer RED tests
- 92667a4 — servicer GREEN implementation
- cde8928 — server.py MDL-03 gate

Test run: 100 passed, 8 warnings, 0 failures.

## Next Phase Readiness

- Python batch path is complete: ScoreBatch and Fit RPCs are functional end-to-end
- MDL-03 gate ensures detector won't accept traffic before saved models are loaded
- Orchestrator's BatchSchedulerWorker (02-04) can now call ScoreBatch and Fit RPCs
- RES-02 detector restart resilience is met: models load from disk before SERVING
