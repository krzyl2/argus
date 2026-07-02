---
phase: 08-group-config-ui-algorithm-chooser
fixed_at: 2026-07-02T22:07:00Z
review_path: .planning/phases/08-group-config-ui-algorithm-chooser/08-REVIEW.md
iteration: 1
findings_in_scope: 7
fixed: 7
skipped: 0
status: all_fixed
---

# Phase 08: Code Review Fix Report

**Fixed at:** 2026-07-02T22:07:00Z
**Source review:** .planning/phases/08-group-config-ui-algorithm-chooser/08-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 7 (3 Critical/Blocker + 4 Warning; Info excluded per fix_scope)
- Fixed: 7
- Skipped: 0

## Fixed Issues

### CR-01: Joint-mode sensitivity presets are cosmetic after the first fit — params never re-applied on re-fit

**Files modified:** `detector/argus_detector/registry.py`, `detector/tests/test_registry.py`
**Commit:** fc04138
**Applied fix:** In `DetectorRegistry.fit_one`, joint-multivariate detectors (`ecod`/`copod`/`pca`/`iforest`) now always reconstruct via `_create_detector(detector, params)` instead of `copy.deepcopy(current)` when a prior instance exists. These detectors are refit from scratch nightly regardless, so there is no warm-start state worth preserving across a param change. `peer_divergence` and per-entity detectors keep the existing deepcopy/warm-start path unchanged. Concurrency pattern (train outside lock, atomic swap under lock, MDL-04) is preserved. Added `test_fit_one_joint_detector_reapplies_changed_params_on_refit`, which fits an `iforest` group detector twice with different `contamination` values and asserts the second fit's model reflects the new value.

### CR-02: "Choose an algorithm to continue" validation does not actually block Save — silently defaults to peer_divergence

**Files modified:** `orchestrator/ui/src/components/GroupEditorForm.tsx`, `orchestrator/ui/src/state/groups.ts`, `orchestrator/ui/src/state/groups.test.ts`
**Commit:** a376f66
**Applied fix:** `hasErrors` in `GroupEditorForm.tsx` now includes `noAlgorithmError`, so the Save button is disabled while no algorithm is chosen. `saveGroup()` in `groups.ts` no longer defaults `detector` to `'peer_divergence'` when `draftDetector.value` is `null` — it refuses to build/POST the request and sets an error `saveState` instead (defense in depth beyond the UI gate). Added two tests: one confirming `saveGroup()` refuses and does not call `apiPost` when no algorithm is chosen, and one confirming the explicitly chosen detector (not a default) is what gets posted.

### CR-03: No mode/detector consistency check — a joint-mode group can be saved with detector="peer_divergence" (or vice versa), causing a fabricated verdict to be published

**Files modified:** `orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs`, `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs`, `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs`, `orchestrator/Argus.Orchestrator.Tests/GroupsEndpointsTests.cs`, `orchestrator/Argus.Orchestrator.Tests/BatchSchedulerWorkerTests.cs`
**Commit:** 1e475de
**Applied fix:** Added `GroupInputValidator.IsModeDetectorConsistent(mode, detector)` (public, shared) and enforced it inside `Validate` for both mismatch directions: `peer_divergence` mode requires `detector == "peer_divergence"`; `joint` mode requires `detector` in `{ecod, copod, pca, iforest}`. This closes the gap independently of CR-02's client-side fix — server remains the authoritative boundary. Also wired the same check into `BatchSchedulerWorker.RunGroupBatchAsync` as a defense-in-depth guard (new `LogEvents.GroupModeDetectorMismatch`): if a mismatch reaches the scheduler anyway (e.g. via a hand-edited `entities.yaml` that bypassed the validator), the cycle is skipped and logged instead of publishing a fabricated `Score=0.0/IsAnomaly=false` verdict. Added validator tests for both mismatch directions, a happy-path theory test across all four joint detectors, a direct `IsModeDetectorConsistent` unit test, and two scheduler-level tests proving the mismatch case skips `ScoreGroupBatchAsync`/publishing while a consistent case scores normally.

## Warnings — Fixed

### WR-01: No duplicate-groupId detection on save — one group can silently overwrite another

**Files modified:** `orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs`, `orchestrator/Argus.Orchestrator.Tests/GroupsEndpointsTests.cs`
**Commit:** 2bc0166
**Applied fix:** `Validate` now detects duplicate `GroupId` values (case-insensitive) within the submitted list and rejects the save with an error naming each colliding id, before any member/mode/detector checks run for those groups. Added tests for a duplicate-id save (rejected) and a distinct-ids save (accepted).

### WR-02: Joint-detector params are never bounds-validated server-side — catalog Min/Max are UI-only decoration

**Files modified:** `orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs`, `orchestrator/Argus.Orchestrator.Tests/GroupsEndpointsTests.cs`
**Commit:** a3c06a6
**Applied fix:** `Validate` now looks up each group's detector in `DetectorCatalog.All()`'s `ParamSchema` and, for every param present in the submitted `Params`, validates it is numeric and within the schema's `Min`/`Max` bounds, rejecting the save with a specific message otherwise. Absent params still fall back to the detector's own default (no behavior change for the common case). Added tests: `contamination` above the catalog max is rejected, `contamination` within bounds is accepted, and `n_estimators` below the catalog min is rejected.

### WR-03: AttributionPanel does not URL-encode groupId in the status poll path

**Files modified:** `orchestrator/ui/src/components/AttributionPanel.tsx`, `orchestrator/ui/src/components/AttributionPanel.test.tsx`
**Commit:** 21e6fae
**Applied fix:** Wrapped `groupId` in `encodeURIComponent` when building the `api/groups/{id}/status` fetch path, matching the existing pattern already used in `GroupListRow.tsx`'s route href. Added a test asserting `apiGet` is called with the encoded path for a `groupId` containing `/` and `?`.

### WR-04: peer_divergence group saved with a joint-only detector is a silent no-op, not a validation error

**Commit:** 1e475de (covered by CR-03)
**Applied fix:** No separate change — REVIEW.md explicitly notes this is the same root cause as CR-03 (mode/detector mismatch) and is fully covered by `IsModeDetectorConsistent`'s bidirectional check landed in that commit. Verified via the `Validate_PeerDivergenceModeWithJointDetector_ReturnsValidationError` test added under CR-03.

## Test Results (post-fix, zero regressions)

- `detector`: `python -m pytest -q` — **194 passed** (baseline 193; +1 new CR-01 test)
- `orchestrator`: `dotnet test Argus.Orchestrator.sln -c Release` — **367 passed** (baseline 353; +14 new tests across CR-01/CR-02/CR-03/WR-01/WR-02)
- `orchestrator/ui`: `npx vitest run` — **84 passed** (baseline 81; +3 new tests across CR-02/WR-03)

All three suites ran clean with no skips and no pre-existing-failure regressions. (Two environment-only `.NET` test failures caused by a missing, gitignored `deploy/certs/` directory in the fresh worktree were resolved by copying the existing local dev certs before running — unrelated to any of the fixes above.)

---

_Fixed: 2026-07-02T22:07:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
