---
phase: 10-design-system-foundation
plan: 06
subsystem: ui
tags: [preact, components, forms, retrofit, a11y]

# Dependency graph
requires: [10-02]
provides:
  - "SaveBar, AddDetectorButton, DetectorEntry, SensorListRow, SensorSearchInput retrofitted to the shared Button/Select/Checkbox/SearchInput components from Plan 10-02"
affects: [11, 12, 13]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Retrofit-in-place: five existing call sites now import and render the shared form-control components instead of raw .argus-* markup, with zero prop/behavior change to their own exported API"

key-files:
  created: []
  modified:
    - orchestrator/ui/src/components/SaveBar.tsx
    - orchestrator/ui/src/components/AddDetectorButton.tsx
    - orchestrator/ui/src/components/DetectorEntry.tsx
    - orchestrator/ui/src/components/SensorListRow.tsx
    - orchestrator/ui/src/components/SensorSearchInput.tsx

key-decisions:
  - "SaveBar's #argus-spinner span removed entirely — Button.tsx already renders its own .argus-btn__spinner when loading, so keeping both would have shown two spinners"
  - "AddDetectorButton's dedicated .argus-btn--add-detector modifier class is dropped in favor of Button's variant=\"secondary\" — the plan's action spec explicitly named variant=\"secondary\", not a custom modifier"
  - "SensorSearchInput's local debounce/ref/useEffect logic is deleted outright (not kept as a fallback) — SearchInput from Plan 10-02 is a verbatim port of the same logic, so delegation is a pure pass-through with the same 200ms debounce and placeholder text"

requirements-completed: [COMP-01, A11Y-01]

# Metrics
duration: ~10min
completed: 2026-07-08
status: complete
---

# Phase 10 Plan 06: Form Call-Site Retrofit Summary

**Retrofitted five existing Sensors-screen call sites (SaveBar, AddDetectorButton, DetectorEntry, SensorListRow, SensorSearchInput) to consume the shared Button/Select/Checkbox/SearchInput components from Plan 10-02, with zero behavior change and the D-06 "Save configuration" CTA label preserved verbatim.**

## Performance

- **Duration:** ~10 min
- **Completed:** 2026-07-08
- **Tasks:** 3
- **Files modified:** 5

## Accomplishments

- `SaveBar.tsx` renders `<Button variant="primary" loading={saving} disabled={disabled} onClick={onSave}>Save configuration</Button>`, removing the now-redundant `#argus-spinner` span (Button owns its own spinner)
- `AddDetectorButton.tsx` renders `<Button variant="secondary" onClick={onAdd} ariaLabel={...}>+ Add detector</Button>`
- `DetectorEntry.tsx` renders `<Select value={detector.name} options=[hst/mad/stl] onChange={...}>` for the detector-type dropdown and `<Button variant="destructive-ghost" size="xs">Remove</Button>` for the remove action, preserving the `'hst'|'mad'|'stl'` type cast on `onTypeChange`
- `SensorListRow.tsx` renders `<Checkbox checked={isTracked} ariaLabel={entry.entityId} onChange={onToggleTracked} />` for the tracked-toggle, keeping the surrounding `<label style={{display:'contents'}}>` wrapper unchanged
- `SensorSearchInput.tsx` is now a thin instantiation of `SearchInput` (value/onChange/placeholder/ariaLabel/debounceMs=200) — its local `useRef`/`useEffect`/`setTimeout` debounce implementation is deleted, since `SearchInput` already carries a verbatim port of the same logic
- `SensorSearchInput.test.tsx` passes unchanged (8/8); full UI test suite passes 92/92 tests across 13 files with zero regressions

## Task Commits

Each task was committed atomically:

1. **Task 1: SaveBar + AddDetectorButton → shared Button (D-06 label preserved)** - `02aae41` (feat)
2. **Task 2: DetectorEntry → shared Select + Button** - `27a2df5` (feat)
3. **Task 3: SensorListRow → Checkbox; SensorSearchInput → SearchInput** - `2ea01c2` (feat)

## Files Created/Modified

- `orchestrator/ui/src/components/SaveBar.tsx` - now imports and renders shared `Button` (primary, loading, disabled); `#argus-spinner` span removed
- `orchestrator/ui/src/components/AddDetectorButton.tsx` - now imports and renders shared `Button` (secondary)
- `orchestrator/ui/src/components/DetectorEntry.tsx` - now imports and renders shared `Select` (detector type) and `Button` (destructive-ghost, xs, Remove)
- `orchestrator/ui/src/components/SensorListRow.tsx` - now imports and renders shared `Checkbox` for the tracked toggle
- `orchestrator/ui/src/components/SensorSearchInput.tsx` - rewritten as a thin wrapper delegating to shared `SearchInput`; local debounce logic deleted

## Decisions Made

- Kept the plan's exact literal API calls (`Select`'s `options` array with hst/mad/stl labels, `Button`'s `variant`/`size`/`ariaLabel` props) rather than introducing any new prop shapes
- Removed `SaveBar`'s duplicate spinner element outright since `Button` now owns spinner rendering — leaving both would have produced two visible spinners during `saving`
- Dropped the bespoke `.argus-btn--add-detector` CSS modifier class in favor of `Button`'s `variant="secondary"`, per the plan's explicit action spec

## Deviations from Plan

None — plan executed exactly as written. All acceptance criteria met without needing Rule 1-4 fixes.

## Issues Encountered

- `orchestrator/ui/node_modules` was absent in this fresh worktree (git worktrees do not carry gitignored directories, consistent with the same note in Plan 10-02's summary) — ran `npm install` locally before running `tsc -b`/`vitest`. Dev-environment step only, nothing committed for it (`node_modules/` stays gitignored).

## Next Phase Readiness

- All five Sensors-screen form call sites now consume the shared component library; no raw `.argus-btn`/`.argus-detector-select`/`.argus-checkbox` markup remains in these files
- `cd orchestrator/ui && npx tsc -b` exits 0; `npx vitest run` passes 92/92 across 13 files
- No blockers for later phases (11-13) that build on this shared component library

## Self-Check: PASSED

- FOUND: orchestrator/ui/src/components/SaveBar.tsx
- FOUND: orchestrator/ui/src/components/AddDetectorButton.tsx
- FOUND: orchestrator/ui/src/components/DetectorEntry.tsx
- FOUND: orchestrator/ui/src/components/SensorListRow.tsx
- FOUND: orchestrator/ui/src/components/SensorSearchInput.tsx
- FOUND: 02aae41 (Task 1)
- FOUND: 27a2df5 (Task 2)
- FOUND: 2ea01c2 (Task 3)

---
*Phase: 10-design-system-foundation*
*Completed: 2026-07-08*
