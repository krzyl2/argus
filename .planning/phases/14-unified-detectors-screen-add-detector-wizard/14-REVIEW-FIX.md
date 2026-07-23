---
phase: 14-unified-detectors-screen-add-detector-wizard
fixed_at: 2026-07-23T15:00:00Z
review_path: .planning/phases/14-unified-detectors-screen-add-detector-wizard/14-REVIEW.md
iteration: 1
findings_in_scope: 6
fixed: 6
skipped: 0
status: all_fixed
---

# Phase 14: Code Review Fix Report

**Fixed at:** 2026-07-23T15:00:00Z
**Source review:** .planning/phases/14-unified-detectors-screen-add-detector-wizard/14-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 6 (Warning tier only, `fix_scope: critical_warning`; Info findings IN-01..IN-04 out of scope)
- Fixed: 6
- Skipped: 0

## Fixed Issues

### WR-01: `MemberPicker`'s group-only "needs at least 2 members" validation renders — and is actively wrong — during the wizard's valid 1-sensor path

**Files modified:** `orchestrator/ui/src/components/MemberPicker.tsx`, `orchestrator/ui/src/components/AddDetectorWizard.tsx`, `orchestrator/ui/src/components/AddDetectorWizard.test.tsx`
**Commit:** 6fc3db1
**Applied fix:** Added `showGroupValidation?: boolean` prop to `MemberPicker` (default `true`, preserving `GroupEditorForm`'s existing behavior unchanged) that gates rendering of the member-floor and unit-mismatch `FieldValidationError` messages. `AddDetectorWizard` now passes `showGroupValidation={false}`. Added a regression test asserting the "needs at least 2 members" message is absent when exactly 1 sensor is selected in the wizard.

### WR-02: `SettingsPage`'s pattern-filters `SaveBar` doesn't gate on `hasValidationErrors`

**Files modified:** `orchestrator/ui/src/components/SettingsPage.tsx`
**Commit:** ab8dc2f
**Applied fix:** Imported `hasValidationErrors` from `state/sensors` and added it to the `SaveBar`'s `disabled` expression (`disabled={patternsSaving || hasValidationErrors.value}`), matching every other save surface (e.g. `SingleDetectorEditorForm`).

### WR-03: Sidebar highlights both "Detectors" and "Add detector" simultaneously when on `/detectors/add`

**Files modified:** `orchestrator/ui/src/components/Sidebar.tsx`, `orchestrator/ui/src/components/Sidebar.test.tsx`
**Commit:** ccdd823
**Applied fix:** Excluded `/detectors/add` from the generic `/detectors/*` prefix match used by the `detectors` nav item, so only `add-detector` highlights on that route. Added a regression test asserting `Detectors` is inactive and `Add detector` is active on `/detectors/add`.

### WR-04: `main.tsx`'s `/detectors/sensor/:entityId` route branch does not fall back to the Detectors list when the id fails to parse

**Files modified:** `orchestrator/ui/src/main.tsx`
**Commit:** 8a432b1
**Applied fix:** Split the `/detectors/sensor/` branch in two: the first requires both the path prefix match and a truthy `routeSensorEntityId.value` (successful parse) before rendering `SingleDetectorEditorForm`; a second branch matches the same prefix with a falsy parsed value and falls back to `DetectorsPage`, per the documented D-01/T-14-01-01 contract. No test file exists for `main.tsx`'s routing branches (not covered by any existing suite), so no test was added — verified via `tsc --noEmit` and a Tier-1 re-read only.

### WR-05: "Untrack sensor" in `SingleDetectorEditorForm` gives no immediate visual feedback

**Files modified:** `orchestrator/ui/src/components/SingleDetectorEditorForm.tsx`
**Commit:** 5ccc3a7
**Applied fix:** Gated the `DetectorDisclosure` render on `edit?.isTracked`; when the sensor is not tracked (e.g. immediately after clicking "Untrack sensor"), a `<p class="argus-label">This sensor will be untracked on next save.</p>` message renders in its place instead of the editable detector editor.

### WR-06: `SingleDetectorEditorForm` hardcodes `entityIdx={0}`, producing a meaningless "Detector type for entity 0" ARIA label

**Files modified:** `orchestrator/ui/src/components/DetectorEntry.tsx`, `orchestrator/ui/src/components/DetectorDisclosure.tsx`, `orchestrator/ui/src/components/SingleDetectorEditorForm.tsx`
**Commit:** e3c3484
**Applied fix:** Added an optional `entityLabel?: string` prop, threaded from `SingleDetectorEditorForm` (passing `entityId`) through `DetectorDisclosure` into `DetectorEntry`, which now renders `aria-label={\`Detector type for ${entityLabel ?? \`entity ${entityIdx}\`}\`}`. `entityIdx` is retained unchanged for DOM-id uniqueness in `DetectorParamGrid`. Falls back to prior "entity N" wording for callers that don't pass it (e.g. the orphaned `SensorListRow.tsx`), so no other call site's behavior changed.

## Skipped Issues

None — all 6 in-scope findings were fixed.

## Verification

- `npx tsc --noEmit` clean after every fix (no errors introduced in any modified file).
- Full frontend suite (`npx vitest run`) after all fixes: **34 test files, 205 tests, all passing.**
- Two regression tests added per the task's explicit low-risk callouts (WR-01 absence-of-message, WR-03 dual-highlight test).

---

_Fixed: 2026-07-23T15:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
