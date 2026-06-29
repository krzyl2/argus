---
phase: 02-batch-model-lifecycle
fixed_at: 2026-06-10T20:00:00Z
review_path: .planning/phases/02-batch-model-lifecycle/02-REVIEW.md
iteration: 1
findings_in_scope: 9
fixed: 9
skipped: 0
status: all_fixed
---

# Phase 02: Code Review Fix Report

**Fixed at:** 2026-06-10T20:00:00Z
**Source review:** .planning/phases/02-batch-model-lifecycle/02-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 9 (3 Critical + 6 Warning)
- Fixed: 9
- Skipped: 0

## Fixed Issues

### CR-01: Flux Query Injection via entityId and Config Fields

**Files modified:** `orchestrator/Argus.Orchestrator/Batch/InfluxDbReader.cs`
**Commit:** 26700e8
**Applied fix:** Added `_safeFluxString` compiled regex (`^[^"\\]+$`) and per-value guards before building the Flux query string. Any value containing `"` or `\` throws `ArgumentException`. Per threat model T-02-02-02 (accepted operator-controlled trust), the check is narrow — reject only the characters that break Flux string literals.

### CR-02: Registry Key Inconsistency — Startup-Loaded Models Invisible to ScoreBatch

**Files modified:** `detector/argus_detector/model_store.py`, `detector/argus_detector/servicer.py`
**Commit:** ddaa2d7
**Applied fix:** Used the sidecar approach as specified: added `entity_id.txt` sidecar written alongside `version.json` at save time (`_write_entity_id()` helper added). `load_all_into` reads the sidecar to get the unambiguous original entity_id (dots intact) and calls `registry.register(entity_id, ...)` instead of `registry.register(slug, ...)`. `save_pyod` and `save_river` gained an optional `entity_id` parameter; `Fit` passes it through `_save_model_to_store`. Backwards-compatible: falls back to slug when sidecar is absent.

### CR-03: Unsafe pickle.load of River Model Files

**Files modified:** `detector/argus_detector/model_store.py`
**Commit:** 026afe0
**Applied fix:** Added code comment on all three `pickle.load` call sites (`load_river`, `load_all_into`) citing T-02-03-01 accepted risk for the single-operator self-hosted deployment. No implementation change per threat model acceptance.

### WR-01: fit_one("stl") Crashes with AttributeError at Runtime

**Files modified:** `detector/argus_detector/registry.py`
**Commit:** 92eeb45
**Applied fix:** Added early-return path in `fit_one` for `detector == "stl"`: registers a fresh `StlDetector` instance under the entity lock if not already present, then returns without calling `fit()`. StlDetector is stateless and produces scores without fitting.

### WR-02: Direct Access to _detectors Private Dict Bypasses Entity Lock

**Files modified:** `detector/argus_detector/registry.py`, `detector/argus_detector/servicer.py`
**Commit:** ec913b9
**Applied fix:** Added `get_model(entity_id, detector)` public method to `DetectorRegistry` that reads under the per-entity lock. Replaced both `self._registry._detectors.get(...)` calls in `servicer.py` (in `Fit` and `SaveModel`) with `self._registry.get_model(...)`.

### WR-03: SaveModel Computes and Discards All Serialized Bytes

**Files modified:** `detector/argus_detector/servicer.py`
**Commit:** 1c04540
**Applied fix:** Replaced the serialize-and-discard implementation with a real persistence call: `SaveModel` now calls `self._model_store.next_version(entity_slug, detector)` and `self._save_model_to_store(...)` to write the model to disk. Returns `ok=True` on success, `ok=False` with error on failure.

### WR-04: BatchIntervalMinutes=0 Causes Tight Spin Loop

**Files modified:** `orchestrator/Argus.Orchestrator/Program.cs`
**Commit:** 330c186
**Applied fix:** Added startup validation: `if (connectionSettings.BatchIntervalMinutes <= 0) throw new InvalidOperationException(...)`. Placed before `AddSingleton(connectionSettings)` so the process fails fast with a clear message.

### WR-05: NightlyFitHour Out of Range Silently Disables Nightly Fit

**Files modified:** `orchestrator/Argus.Orchestrator/Program.cs`
**Commit:** 330c186
**Applied fix:** Added startup validation: `if (connectionSettings.NightlyFitHour < 0 || connectionSettings.NightlyFitHour > 23) throw new InvalidOperationException(...)`. Committed in the same commit as WR-04 (same file, same location).

### WR-06: _FakeContext.abort() Does Not Stop Execution — Tests Miss Post-Abort Contract

**Files modified:** `detector/argus_detector/servicer.py`, `detector/tests/test_servicer.py`
**Commit:** dd104db
**Applied fix:** Changed `Fit` to `return None` after `context.abort(...)` instead of returning a `FitResponse` (dead code — gRPC ignores the return value after abort). Added `assert result is None` assertions to `test_fit_empty_entity_id_aborts` and `test_empty_entity_id_aborts_invalid_argument`. `ScoreBatch` already returned `None` via bare `return`.

---

## Test Results After All Fixes

- `.NET build`: clean (0 warnings, 0 errors)
- `.NET tests`: 73 passed, 2 pre-existing failures in `DiscoveryPayloadTests` (unrelated to this phase — `BinarySensorPayload_AvailabilityTopicIsBridgeLevel` and `BinarySensorPayload_PayloadAvailableOnline` were failing before any fix was applied)
- `Python pytest`: 105 passed, 0 failures

---

_Fixed: 2026-06-10T20:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
