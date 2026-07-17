---
phase: 12-sensors-screen-rebuild
plan: 03
subsystem: frontend/sensors-detector-editor
tags: [preact, ui, detector-editor, algorithm-card, input, tdd]
dependency-graph:
  requires:
    - orchestrator/ui/src/components/AlgorithmCard.tsx (widened string props from 12-01)
    - orchestrator/ui/src/components/Input.tsx (extended id/step/ariaDescribedby from 12-01)
  provides:
    - AlgorithmCard radiogroup type picker in DetectorEntry.tsx (SEN-02/SC3)
    - Shared-Input param fields in DetectorParamGrid.tsx (D-07)
  affects:
    - orchestrator/ui/src/components/DetectorEntry.tsx
    - orchestrator/ui/src/components/DetectorParamGrid.tsx
tech-stack:
  added: []
  patterns:
    - "Radiogroup of AlgorithmCard mirroring AlgorithmChooser.tsx"
    - "External label + shared Input + FieldValidationError mirroring SettingsPage.tsx"
key-files:
  created:
    - orchestrator/ui/src/components/DetectorEntry.test.tsx
    - orchestrator/ui/src/components/DetectorParamGrid.test.tsx
  modified:
    - orchestrator/ui/src/components/DetectorEntry.tsx
    - orchestrator/ui/src/components/DetectorParamGrid.tsx
decisions:
  - "DETECTOR_TYPES kept client-hardcoded (no backend catalog exists for single-sensor detectors, per 12-CONTEXT.md Deferred) - bestFor text reuses prior timingCaption wording verbatim."
  - "DetectorDisclosure.tsx required no changes - multi-detector append/render model (D-03) was already correct pass-through."
metrics:
  duration: "~25 min"
  completed: 2026-07-17
status: complete
---

# Phase 12 Plan 03: Detector Editor Rebuild (AlgorithmCard + Shared Input) Summary

Rebuilt the hst/mad/stl detector-type picker as an `AlgorithmCard` radiogroup (replacing the raw
`<Select>`) and swapped `DetectorParamGrid`'s hand-authored `<input>` elements for the shared
`Input` component, while leaving `detectorParams.ts`, `state/sensors.ts`, and `DetectorDisclosure.tsx`
byte-identical — full frontend build and full 120-test vitest suite are green.

## What Was Built

### Task 1: Select -> AlgorithmCard radiogroup (`DetectorEntry.tsx`)

- Removed the `Select` import and `DETECTOR_TYPE_OPTIONS` array.
- Added a client-hardcoded `DETECTOR_TYPES` table (`hst`/`mad`/`stl` + `bestFor` text reusing the
  prior `timingCaption` wording verbatim - no new copy invented).
- Renders a `role="radiogroup"` of three `AlgorithmCard`s (mirroring `AlgorithmChooser.tsx`'s
  established pattern); the card whose `name` matches `detector.name` is `selected` (2px accent
  border - SC3); `onSelect` routes straight to `onTypeChange` with the string value, no `as any`
  cast.
- Dropped the now-redundant standalone `<span class="argus-timing-caption">` - the same string
  moved into the card's `bestFor` slot, no information lost.
- Preserved verbatim: the `<Button variant="destructive-ghost">Remove</Button>` block and the
  `<DetectorParamGrid ...>` call.
- `DetectorDisclosure.tsx` verified unchanged - it already renders one independent `DetectorEntry`
  per detector block and appends via `AddDetectorButton`; the multi-detector model (D-03) required
  no edits.
- New `DetectorEntry.test.tsx` (5 tests): radiogroup + 3 cards render, selected card matches
  current name, clicking a card calls `onTypeChange` with the right hst/mad/stl value, no bare
  `<select>` remains, Remove button still forwards `onRemove`.

### Task 2: Raw `<input>` -> shared `Input` (`DetectorParamGrid.tsx`)

- Replaced the bare `<input class="argus-param-field__input">` with the shared `<Input>`,
  preserving the surrounding `<div class="argus-param-field">` + `<label for={inputId}>` +
  `<FieldValidationError message={error} />` structure (SettingsPage.tsx convention).
- Forwards `id`, `value`, `onChange`, `type="number"`, `step={field.step}`, `invalid={!!error}`,
  `ariaDescribedby={`${inputId}-err`}`, `ariaLabel={field.label}` - `step` and `ariaDescribedby`
  are the passthrough props added to `Input.tsx` in 12-01.
- `HST_FIELDS`/`MAD_FIELDS`/`STL_FIELDS`, `fieldsFor`, and the `validateDetectorParams` call are
  untouched (D-08).
- New `DetectorParamGrid.test.tsx` (6 tests): field count + `aria-label` forwarded by the shared
  `Input` (distinguishes it from the previous hand-authored `<input>`), `aria-describedby` linkage,
  `aria-invalid` + error-message rendering on a bad value, valid field stays `aria-invalid=false`,
  `step` passthrough (threshold `step="0.1"`), and `onParamChange` firing with the right key/value.

### Task 3: Phase regression gate

- `npm run build` (`tsc -b && vite build`) - green.
- `npm run test -- --run` (full suite) - **120/120 tests passed across 17 files**, including the
  unchanged `detectorParams.test.ts` and `state/sensors.test.ts` regression guards.
- No fixes were required; nothing needed to be re-run.
- Verified via `git diff` against the plan's base commit: `detectorParams.ts` and `state/sensors.ts`
  have zero changes across the whole phase (D-08 confirmed).

## TDD Gate Compliance

Both behavior-adding tasks followed RED -> GREEN:

- Task 1: `test(12-03): add failing test for AlgorithmCard radiogroup in DetectorEntry` (RED,
  4/5 tests failed pre-implementation) -> `feat(12-03): replace Select detector-type picker with
  AlgorithmCard radiogroup` (GREEN, 5/5 pass).
- Task 2: `test(12-03): add failing test for DetectorParamGrid shared-Input swap` (RED, 1/6 tests
  failed - the aria-label-forwarded-by-shared-Input assertion, since the prior hand-authored
  `<input>` already matched the other aria/step/onChange behaviors byte-for-byte) -> `feat(12-03):
  swap DetectorParamGrid raw input for shared Input component` (GREEN, 6/6 pass).

Note: most of Task 2's assertions (aria-describedby, aria-invalid, step, onParamChange wiring)
passed even before the swap, because the pre-existing raw `<input>` already forwarded those
attributes correctly - only the `aria-label` (added by the shared `Input` component per the
SettingsPage convention) was a true behavioral delta. This is expected: the plan's D-08 intent for
this task is a component-authorship swap for consistency/reuse, not a behavior fix, so the RED
signal is narrower than for Task 1.

## Deviations from Plan

None - plan executed exactly as written. `orchestrator/ui/node_modules` was missing in this fresh
worktree checkout; ran `npm install` (not a plan deviation, just environment setup required to run
the verification commands - no package.json/lockfile changes).

## Environment Note

`node_modules` was not present in this git worktree (worktrees don't carry `node_modules`, which is
gitignored). Ran `npm install` in `orchestrator/ui` before running tests/build; `package-lock.json`
was unchanged (`npm install` resolved from the existing lockfile, 0 vulnerabilities, no diff).

## Self-Check: PASSED

- `orchestrator/ui/src/components/DetectorEntry.tsx` - FOUND
- `orchestrator/ui/src/components/DetectorEntry.test.tsx` - FOUND
- `orchestrator/ui/src/components/DetectorParamGrid.tsx` - FOUND
- `orchestrator/ui/src/components/DetectorParamGrid.test.tsx` - FOUND
- Commit `f9048be` (test: DetectorEntry RED) - FOUND
- Commit `9880be2` (feat: DetectorEntry GREEN) - FOUND
- Commit `f849231` (test: DetectorParamGrid RED) - FOUND
- Commit `9f9ec8a` (feat: DetectorParamGrid GREEN) - FOUND
- `detectorParams.ts` / `state/sensors.ts` diff vs base commit - EMPTY (D-08 confirmed)
- Full build + full vitest suite - GREEN (120/120)
