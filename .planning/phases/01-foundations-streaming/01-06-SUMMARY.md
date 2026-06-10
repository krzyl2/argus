---
phase: 01-foundations-streaming
plan: "06"
subsystem: detector
tags: [river, hst, streaming, anomaly-detection, tdd, python, grpc, scoring]

requires:
  - "01-02: DetectorRegistry interface (score_one placeholder) + servicer.py call site"

provides:
  - "detector/argus_detector/normalizer.py: OnlineMinMaxScaler wrapping river.preprocessing.MinMaxScaler"
  - "detector/argus_detector/hst_detector.py: EntityDetector (River HalfSpaceTrees + online min-max, is_warmed_up, from_params)"
  - "detector/argus_detector/registry.py: real DetectorRegistry with threading.Lock lazy per-entity creation, _detectors dict, is_warmed_up delegate"

affects:
  - "01-08: integration test exercises real HST scoring through ScoreStream"
  - "servicer.py: already calls registry.score_one — no call site change needed (interface stable)"

tech-stack:
  added:
    - "river 0.25.0 — HalfSpaceTrees streaming anomaly detection + MinMaxScaler online normalization"
  patterns:
    - "River MinMaxScaler API in 0.25.0: learn_one(x) returns None; call learn_one then transform_one separately"
    - "HalfSpaceTrees.score_one returns int 0 before range widens; always cast to float()"
    - "EntityDetector.from_params(): string-map param cast with int() + D-09 defaults"
    - "DetectorRegistry: double-checked locking pattern (read without lock, create under lock)"
    - "TDD gate: RED (d8d9c25) → GREEN (e4db07d); no REFACTOR needed"

key-files:
  created:
    - detector/argus_detector/normalizer.py
    - detector/argus_detector/hst_detector.py
    - detector/tests/test_hst_detector.py
    - detector/tests/test_registry.py
    - detector/tests/test_score_zero_wire.py
  modified:
    - detector/argus_detector/registry.py

key-decisions:
  - "River MinMaxScaler.learn_one() returns None in 0.25.0 — fixed to call learn_one then transform_one separately (plan code example was from older API)"
  - "HalfSpaceTrees.score_one returns int 0 when all normalized values are 0.0 — cast to float() at score_one call site"
  - "test_score_zero_wire.py passes in RED phase: proto3 DoubleValue contract was already correct from Plan 01; test acts as a permanent regression guard"
  - "servicer.py call site unchanged: score_one(entity_id, value) signature is stable; no update needed"

metrics:
  duration: "15min"
  completed: "2026-06-10"
  tasks: 2
  files_modified: 6
---

# Phase 01 Plan 06: River HST Per-Entity Streaming Scorer Summary

**River HalfSpaceTrees scorer with online min-max normalization wired into DetectorRegistry; point spike scores above baseline (FAULT-01); score=0.0 survives DoubleValue wire (PITFALL 1 closed); per-entity isolation under threading.Lock (T-06-01); 30/30 pytest tests pass**

## Performance

- **Duration:** 15 min
- **Started:** 2026-06-10T07:45:00Z
- **Completed:** 2026-06-10T08:00:00Z
- **Tasks:** 2 (RED + GREEN)
- **Files modified:** 6

## Accomplishments

- `normalizer.py`: `OnlineMinMaxScaler` thin wrapper around `river.preprocessing.MinMaxScaler`; exposes `learn_transform(value) -> dict` for use by `EntityDetector`
- `hst_detector.py`: `EntityDetector` with `HalfSpaceTrees(n_trees=25, height=8, window_size=250, seed=42)`; `is_warmed_up` property (n_seen >= window_size); `from_params(params: dict[str,str])` factory casting window/n_trees from string map with D-09 defaults
- `registry.py`: full `DetectorRegistry` replacing Plan 02 placeholder; `dict[tuple[str,str], EntityDetector]` keyed by `(entity_id, detector)`; `threading.Lock` guards lazy creation (double-checked locking pattern); `is_warmed_up(entity_id, detector)` delegates to per-entity instance; `TODO(plan06)` marker removed
- `servicer.py`: no change required — `registry.score_one(entity_id, value)` signature was stable from Plan 02
- All 5 behaviors from plan tested: warm-up tracking, spike vs baseline, params override, per-entity isolation, score=0.0 wire
- 30/30 pytest tests pass (15 previous + 15 new)

## Task Commits

1. **Task 1 RED** — `d8d9c25` (test): failing tests for HST scorer + score-zero wire
2. **Task 2 GREEN** — `e4db07d` (feat): river HST per-entity streaming scorer

## Files Created/Modified

- `detector/argus_detector/normalizer.py` — OnlineMinMaxScaler; learn_transform(value)
- `detector/argus_detector/hst_detector.py` — EntityDetector; score_one; is_warmed_up; from_params
- `detector/argus_detector/registry.py` — DetectorRegistry; _detectors dict; threading.Lock; is_warmed_up; TODO(plan06) removed
- `detector/tests/test_hst_detector.py` — 7 tests: warm-up (3), spike vs baseline (1), params override (3)
- `detector/tests/test_registry.py` — 5 tests: per-entity isolation, float return, lazy creation, is_warmed_up, params propagation
- `detector/tests/test_score_zero_wire.py` — 3 tests: score=0.0 HasField round-trip, score absent when not set, non-zero round-trip

## Decisions Made

- **River MinMaxScaler API**: `learn_one()` returns `None` in River 0.25.0 — must call `learn_one(x)` then `transform_one(x)` as separate calls. The plan's code example used the older chained API (`learn_one(x).transform_one(x)`). Fixed at implementation time.
- **HalfSpaceTrees score_one return type**: Returns `int 0` before the observed value range has any spread (all normalized values are 0.0). Cast to `float()` at the score_one call site to guarantee float return type.
- **test_score_zero_wire passes in RED**: The proto3 DoubleValue contract was already correctly implemented in Plan 01 (argus_pb2.Verdict.score is a DoubleValue field). The test is a permanent regression guard for PITFALL 1 / D-01, not a new capability.
- **servicer.py unchanged**: The Plan 02 servicer.py calls `registry.score_one(entity_id, value)` — this signature is fully compatible with the new registry. No update needed.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] River MinMaxScaler.learn_one() returns None in 0.25.0**
- **Found during:** Task 2 (first GREEN run)
- **Issue:** Plan code example used `self._normalizer.learn_one(x).transform_one(x)` — chained API that was valid in older River versions. In 0.25.0, `learn_one()` returns `None`, causing `AttributeError: 'NoneType' object has no attribute 'transform_one'`
- **Fix:** Split into `self._normalizer.learn_one(x)` then `x_norm = self._normalizer.transform_one(x)`. Same behavior, correct API for 0.25.0.
- **Files modified:** `detector/argus_detector/hst_detector.py`, `detector/argus_detector/normalizer.py`
- **Commit:** e4db07d

**2. [Rule 1 - Bug] HalfSpaceTrees.score_one returns int 0 before range stabilizes**
- **Found during:** Task 2 (second fix iteration)
- **Issue:** `HalfSpaceTrees.score_one()` returns Python `int 0` when all inputs normalize to the same value. `isinstance(0, float)` is `False`, causing `test_score_one_returns_float` to fail.
- **Fix:** `float(self._model.score_one(x_norm))` at the call site in EntityDetector.score_one.
- **Files modified:** `detector/argus_detector/hst_detector.py`
- **Commit:** e4db07d

## TDD Gate Compliance

- RED gate: `d8d9c25` — test(01-06) commit with failing tests
- GREEN gate: `e4db07d` — feat(01-06) commit with all tests passing
- REFACTOR: Not needed — implementation is already minimal and allocation-light

## Known Stubs

None. The `TODO(plan06)` placeholder in registry.py has been replaced with the real River HST implementation.

## Threat Flags

No new security-relevant surface introduced. All STRIDE mitigations applied:
- T-06-01: `threading.Lock` guards lazy EntityDetector creation; per-entity `_detectors` dict isolates state; `test_registry::test_two_entities_have_independent_state` asserts isolation
- T-06-02: HalfSpaceTrees `window_size` bounds per-entity model memory; entity set bounded by entities.yaml; accepted
- T-06-03: DoubleValue wrapper (D-01) transmits score=0.0; `test_score_zero_wire::test_score_zero_survives_roundtrip` asserts `HasField("score")` after round-trip
- T-06-04: Sensor values logged at INFO level only; accepted (non-sensitive environmental readings)

## Self-Check: PASSED

- `detector/argus_detector/normalizer.py` — exists, contains MinMaxScaler, learn_transform
- `detector/argus_detector/hst_detector.py` — exists, contains HalfSpaceTrees, MinMaxScaler, window_size, n_trees, is_warmed_up, from_params
- `detector/argus_detector/registry.py` — exists, contains score_one, Lock, _detectors; does NOT contain TODO(plan06)
- `detector/tests/test_hst_detector.py` — exists, contains is_warmed_up, spike vs baseline assertion
- `detector/tests/test_registry.py` — exists, contains _detectors, is_warmed_up
- `detector/tests/test_score_zero_wire.py` — exists, contains HasField, 0.0
- Commits d8d9c25 (RED), e4db07d (GREEN) — verified in git log
- `python -m pytest detector/tests/` — 30/30 PASSED
