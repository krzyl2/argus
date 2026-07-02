---
phase: 07-spa-scaffolding
fixed_at: 2026-07-02T00:00:00Z
review_path: .planning/phases/07-spa-scaffolding/07-REVIEW.md
iteration: 1
findings_in_scope: 5
fixed: 5
skipped: 0
status: all_fixed
---

# Phase 7: Code Review Fix Report

**Fixed at:** 2026-07-02T00:00:00Z
**Source review:** .planning/phases/07-spa-scaffolding/07-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 5 (1 Critical/Blocker + 4 Warning; Info findings excluded per fix_scope)
- Fixed: 5
- Skipped: 0

## Fixed Issues

### CR-01: Server-side InputValidator silently accepts missing/blank/non-numeric detector params

**Files modified:** `orchestrator/Argus.Orchestrator/Config/InputValidator.cs`, `orchestrator/Argus.Orchestrator.Tests/InputValidatorTests.cs`, `orchestrator/Argus.Orchestrator.Tests/SaveEndpointJsonTests.cs`
**Commit:** aaa8dca
**Applied fix:** `TryGetDouble`/`TryGetInt`-based checks in `ValidateHst`/`ValidateMad`/`ValidateStl` and the shared `ValidateIntAtLeast` helper were inverted from "skip on parse failure" to "treat parse failure (missing key, blank, non-numeric) as a hard validation error" — mirroring `detectorParams.ts`'s `isBlankOrNonNumeric` → `MSG_REQUIRED` contract exactly. The HST cross-field high/low check was restructured to only evaluate once both individual fields parsed and passed their own range check (same guard the client's `validateHstParams` uses), preserving existing "no exception when only one of high/low is present" behavior while still flagging the missing field itself.

Test changes: added `Validate_HstParamEmptyString_ReturnsError`, `Validate_HstParamNonNumeric_ReturnsError`, `Validate_HstParamMissingKey_ReturnsError` (theory-driven across all 7 HST fields), `Validate_MadParamEmptyOrNonNumeric_ReturnsError`, `Validate_StlParamEmptyOrNonNumeric_ReturnsError`, plus `Validate_{Hst,Mad,Stl}AllParamsValid_ReturnsNoErrors` regression guards. Fixed `Validate_StlSeasonalNonNumeric_ReturnsNoError` (renamed to `..._ReturnsError`), which had asserted the pre-fix broken behavior directly — its intent inverted to match the corrected contract. Updated two `SaveEndpointJsonTests` fixtures (`SavePipeline_ValidHstEntity_ProducesSuccessResultWithHasHstTrue`, `SavePipeline_Success_CallsLiveConfigSwap`) that previously submitted an HST detector with only `window` or no params at all — these only "succeeded" because of the silent-skip bug; now supply the full valid HST param set matching what the real client always sends (`DETECTOR_DEFAULTS` in `sensors.ts` populates all 7 fields, and every field is rendered/required in `DetectorParamGrid.tsx`/`detectorParams.ts`, so there is no legitimately-optional HST field to preserve).

**Note (per verification_strategy):** This is a logic-condition fix (inverted several `if (TryGetX(...))` guards) — flagging as `fixed: requires human verification` for a final sanity read of the HST cross-field branch, even though 71 InputValidator unit tests (36 new + 35 pre-existing, all passing) and the full 321-test suite exercise it directly.

### WR-01: `apiPost` never checks `res.ok` — non-JSON error responses throw a confusing parse error

**Files modified:** `orchestrator/ui/src/api/client.ts`, `orchestrator/ui/src/api/client.test.ts`
**Commit:** 59e5bd0
**Applied fix:** `apiPost` now reads the response body as text first; if `!res.ok` and the body is empty (the actual shape of the `Results.StatusCode(403)` guard in `Program.cs`), it throws `POST {path} failed: {status}` instead of calling `res.json()` and letting it throw an opaque `SyntaxError` on empty input. Non-empty bodies (including `ok:false` JSON discriminant responses) still get `JSON.parse`d and returned to the caller unchanged — no behavior change for the existing validation-error/save-error JSON contract paths. `apiGet` already checked `res.ok` correctly (line 11) — no gap to mirror.

Test changes: updated existing `apiPost` fixture mocks from `json: async () => ...` to `text: async () => JSON.stringify(...)` (matches the new implementation), and added two new tests: success-path JSON parsing, and the 403/empty-body error-message assertion.

### WR-02: `DetectorDefaults.Get` and client `DETECTOR_DEFAULTS` are duplicated with no shared source of truth

**Files modified:** `orchestrator/Argus.Orchestrator/Web/DetectorDefaults.cs`, `orchestrator/ui/src/state/sensors.ts`
**Commit:** 99f5b9a
**Applied fix:** Confirmed `GET /api/detectors/defaults` has no SPA call site (grepped `orchestrator/ui/src` for `detectors/defaults` — no matches) and confirmed the two tables' values match exactly (HST: window=250, n_trees=25, high=0.7, low=0.3, min_consecutive=3, frozen_window=10, frozen_variance=0.001; MAD: threshold=3.5, window=20; STL: period=24, seasonal=7, threshold=3.0). Per the review's own "prefer the smaller change" guidance, left both tables in place (removing the endpoint or wiring a round-trip fetch would be disproportionate to the actual risk) and added cross-reference comments in both files pointing at each other, so a future edit to one side is more likely to prompt an update to the other. Did not add a cross-language contract test — judged as over-engineering for two small literal tables that are now explicitly cross-referenced; a lighter true fix wasn't available without either removing the endpoint or adding a runtime fetch, both of which the review flagged as the heavier option.

### WR-03: `SensorSearchInput` debounce can deliver a stale value after unmount/rapid changes

**Files modified:** `orchestrator/ui/src/components/SensorSearchInput.tsx`, `orchestrator/ui/src/components/SensorSearchInput.test.tsx` (new)
**Commit:** c71d956
**Applied fix:** Added a `useEffect` cleanup that clears the pending `setTimeout` on unmount, exactly as suggested in the review. Added a new test file (none existed previously) covering both the cleanup behavior (fake timers + unmount, asserts `onChange` never fires) and the baseline debounce-still-works case.

### WR-04: `loadSensors` race condition — out-of-order responses can overwrite newer results

**Files modified:** `orchestrator/ui/src/state/sensors.ts`, `orchestrator/ui/src/state/sensors.test.ts` (new)
**Commit:** 66b9c05
**Applied fix:** Added a module-level monotonic `loadSensorsSeq` counter. Each `loadSensors` call captures its own sequence number; after the `apiGet` resolves, the result is only applied to `sensors.value`/`entityEdits.value` if the captured sequence still matches the latest — otherwise it's a stale/out-of-order response and is silently dropped. The `finally` block's `loading.value = false` is similarly guarded so a slow, now-stale request can't flip `loading` back on/off after a newer request has already settled it.

Test changes: added a new test file (none existed for `sensors.ts` previously) with three cases — normal single-request population, the actual out-of-order race (older request resolves after a newer one, asserts the newer data wins), and a `loading` flicker regression guard.

## Skipped Issues

None — all in-scope findings were fixed.

## Verification

- `cd orchestrator && dotnet test Argus.Orchestrator.sln -c Release` — **321/321 passed** (baseline 292; +29 from CR-01's new/adjusted InputValidator/SaveEndpointJsonTests coverage plus prior phase work already on this branch before the fix pass).
- `cd orchestrator/ui && npx vitest run` — **41/41 passed** (baseline 34; +7 from WR-01/WR-03/WR-04 new test coverage).
- Zero regressions in either suite.

**Environment note:** The isolated fixer worktree did not have `deploy/certs/` (untracked, locally-generated TLS fixtures required by `DetectorChannelFactoryTests`) or `orchestrator/ui/node_modules` populated. Both were provisioned locally in the worktree only (certs copied, node_modules linked via an NTFS junction) purely to run the verification suites; neither is a source change and both were cleaned up before worktree teardown. This is a worktree-isolation artifact, not a code defect.

---

_Fixed: 2026-07-02T00:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
