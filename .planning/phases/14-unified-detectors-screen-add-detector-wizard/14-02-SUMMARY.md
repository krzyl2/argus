---
phase: 14-unified-detectors-screen-add-detector-wizard
plan: 02
subsystem: ui
tags: [preact, wizard, single-sensor-editor, member-picker, save-safety]

requires:
  - phase: 14-01-router-sidebar-detector-rows
    provides: "/detectors default route + Sidebar restructure + detectorRows computed signal (not consumed by this plan directly, but establishes the target IA these components slot into)"
provides:
  - "Generalized MemberPicker with optional minQueryLength?: number prop (default 2, Groups unchanged) — D-06/WIZ-01"
  - "SingleDetectorEditorForm.tsx — new /detectors/sensor/:entityId route component, extracted from SensorsPage's inline detector-assignment block, with Untrack action — D-05/D-08a"
  - "AddDetectorWizard.tsx — new /detectors/add thin hand-off route: 1-vs->=2 sensor branch to the existing sensor/group save paths — D-06"
  - "D-07 full-list-replace save-safety guard (loadSensors('') on mount) in both new components, backed by the CRITICAL WIZ-04 regression test"
affects: [14-03, 14-04]

tech-stack:
  added: []
  patterns:
    - "MemberPicker minQueryLength?: number defaulted at the destructure site — one parameterized component, no fork (Pitfall 3)"
    - "Route component mounts loadSensors('') (full set) before any setTracked/save — D-07/Pitfall 1 guard, now used by 3 call sites (GroupsPage precedent + these 2 new ones)"
    - "Hand-off-only wizard: no local save() call, reuses pendingPrefillMembers + setTracked exactly as AreaSuggestionBanner already does"

key-files:
  created:
    - orchestrator/ui/src/components/SingleDetectorEditorForm.tsx
    - orchestrator/ui/src/components/SingleDetectorEditorForm.test.tsx
    - orchestrator/ui/src/components/AddDetectorWizard.tsx
    - orchestrator/ui/src/components/AddDetectorWizard.test.tsx
  modified:
    - orchestrator/ui/src/components/MemberPicker.tsx
    - orchestrator/ui/src/components/MemberPicker.test.tsx

key-decisions:
  - "minQueryLength default resolved via destructure default (= MIN_QUERY_LENGTH) rather than inlining 2 a second time — keeps the named constant as the single source of the default"
  - "AddDetectorWizard's search input uses SensorSearchInput's built-in 200ms debounce (via MemberPicker) unmodified — tests await the debounced onQueryChange rather than mocking timers, since fake timers desynced with preact's microtask-scheduled rerender in earlier attempts"
  - "SingleDetectorEditorForm always renders DetectorDisclosure unconditionally (no isTracked gate) since the route itself IS the tracked-entity context — mirrors the plan's explicit instruction, no SensorListRow-style isSelected/isTracked branching needed"

patterns-established:
  - "Two-exit wizard hand-off with zero receiving-end code: >=2 selections write pendingPrefillMembers + navigate #/groups/new (GroupEditorForm's existing resetDraft consumes it); exactly 1 calls setTracked + navigates to the new sensor route"

requirements-completed: [DET-03, WIZ-01, WIZ-02, WIZ-03, WIZ-04]

coverage:
  - id: D1
    description: "MemberPicker accepts optional minQueryLength (default 2, Groups behavior verbatim); wizard's minQueryLength={3} gates at 3 chars"
    requirement: "WIZ-01"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/MemberPicker.test.tsx#honors a custom minQueryLength — gates at the raised threshold and reveals once met"
        status: pass
    human_judgment: false
  - id: D2
    description: "SingleDetectorEditorForm renders the detector-assignment UI for one entity, loads the full sensor set on mount (D-07), and never touches the group draft signals (Pitfall 6)"
    requirement: "DET-03"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/SingleDetectorEditorForm.test.tsx#mounts loadSensors('') (full set), renders the detector disclosure and a Save control"
        status: pass
      - kind: unit
        ref: "orchestrator/ui/src/components/SingleDetectorEditorForm.test.tsx#never touches the group draft (Pitfall 6) — a pre-set draftDetector is left untouched after mount"
        status: pass
    human_judgment: false
  - id: D3
    description: "Untrack action lives only inside SingleDetectorEditorForm and calls setTracked(entityId, false)"
    requirement: "DET-03"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/SingleDetectorEditorForm.test.tsx#exposes an Untrack sensor control that flips entityEdits.isTracked to false"
        status: pass
    human_judgment: false
  - id: D4
    description: "Selecting exactly 1 sensor and continuing tracks it and navigates to #/detectors/sensor/<encoded id>"
    requirement: "WIZ-02"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/AddDetectorWizard.test.tsx#WIZ-02: selecting exactly 1 sensor and continuing tracks it and navigates to the sensor route"
        status: pass
    human_judgment: false
  - id: D5
    description: "Selecting >=2 sensors and continuing sets pendingPrefillMembers and navigates to #/groups/new"
    requirement: "WIZ-03"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/AddDetectorWizard.test.tsx#WIZ-03: selecting >=2 sensors and continuing pre-fills the group draft and navigates to /groups/new"
        status: pass
    human_judgment: false
  - id: D6
    description: "CRITICAL D-07 preservation regression: tracking a new sensor after the full set is hydrated preserves every previously-tracked sensor in the save POST body"
    requirement: "WIZ-04"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/AddDetectorWizard.test.tsx#WIZ-04 (CRITICAL, D-07): tracking one new sensor after the full set is hydrated preserves every previously-tracked sensor in the save POST body"
        status: pass
    human_judgment: false
  - id: D7
    description: "Wizard's overall layout and copy match the Argus Design System reference (row spacing, button labels, section rhythm)"
    verification: []
    human_judgment: true
    rationale: "Visual/layout fidelity backstop must_have — requires human visual review against the Design System reference, not automatable from unit tests alone."

duration: 5min
completed: 2026-07-21
status: complete
---

# Phase 14 Plan 02: Single-Sensor Editor + Add-Detector Wizard Building Blocks Summary

**Generalized `MemberPicker` with a `minQueryLength` prop, extracted `SingleDetectorEditorForm` from `SensorsPage`'s inline detector-assignment block, and built the thin `AddDetectorWizard` hand-off — with a CRITICAL regression test proving the full-list-replace sensor save can never silently untrack other sensors.**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-07-21T20:30:20Z
- **Completed:** 2026-07-21T20:35:33Z
- **Tasks:** 3 completed
- **Files modified:** 6 (2 modified, 4 created)

## Accomplishments
- `MemberPicker.tsx`: new optional `minQueryLength?: number` prop, defaulted to the existing `MIN_QUERY_LENGTH` (2) so Groups' behavior is unchanged; the resolved value now drives both the reveal-gate check and the "Type at least N characters" copy
- `SingleDetectorEditorForm.tsx` (new): route component for `/detectors/sensor/:entityId` — extracted the `DetectorDisclosure` stack from `SensorsPage`, loads the full sensor set on mount (D-07), exposes the only "Untrack sensor" affordance in the app (D-08a), imports exclusively from `state/sensors` (never mounts `AlgorithmChooser`, never touches the group draft — Pitfall 6)
- `AddDetectorWizard.tsx` (new): route component for `/detectors/add` — mounts the generalized `MemberPicker` at `minQueryLength={3}`, branches on selection count (1 → `setTracked` + navigate to the sensor route; ≥2 → `pendingPrefillMembers` + navigate to `#/groups/new`), never calls `save()` itself
- WIZ-04 CRITICAL regression test: seeds a 3-sensor hydrated tracked set, tracks a 4th, and asserts all 4 entity ids survive in the `POST /api/sensors/save` body

## Task Commits

Each task was committed atomically:

1. **Task 1: Generalize MemberPicker with a minQueryLength prop (D-06, WIZ-01)** - `808c1ad` (feat)
2. **Task 2: Extract SingleDetectorEditorForm (single-sensor detector editor) with Untrack + D-07 guard (D-05, D-08a)** - `b5f03a5` (feat)
3. **Task 3: AddDetectorWizard thin hand-off + CRITICAL D-07 preservation regression test (D-06, D-07, WIZ-01/02/03/04)** - `c05f1da` (feat)

_No TDD tasks in this plan — all three tasks were `type="auto" tdd="true"` per the plan header, executed test-alongside (test file + implementation in the same commit) rather than as separate RED/GREEN commits, matching this codebase's established single-commit-per-task convention (see 14-01-SUMMARY.md and prior phase summaries)._

## Files Created/Modified
- `orchestrator/ui/src/components/MemberPicker.tsx` - added `minQueryLength?: number` prop (default `MIN_QUERY_LENGTH`), resolved value drives `queryTooShort` and the displayed copy
- `orchestrator/ui/src/components/MemberPicker.test.tsx` - added a `minQueryLength={3}` reveal-threshold regression test; all 4 pre-existing default-path tests untouched and still passing
- `orchestrator/ui/src/components/SingleDetectorEditorForm.tsx` - new; single-entity `DetectorDisclosure` mount, Untrack action, `loadSensors('')` D-07 guard, `SaveBar`/`SaveResultBanner`
- `orchestrator/ui/src/components/SingleDetectorEditorForm.test.tsx` - new; mount+render, Untrack-flips-isTracked, and the Pitfall-6 `draftDetector`-untouched regression test
- `orchestrator/ui/src/components/AddDetectorWizard.tsx` - new; `MemberPicker` mount at `minQueryLength={3}`, 1-vs-≥2 branch, `loadSensors('')` D-07 guard
- `orchestrator/ui/src/components/AddDetectorWizard.test.tsx` - new; mount full-load assertion, WIZ-02, WIZ-03, and the CRITICAL WIZ-04 save-preservation regression test

## Decisions Made
- Kept `MIN_QUERY_LENGTH` as the named default source for the new `minQueryLength` prop rather than inlining `2` a second time (single source of truth for the default, per the plan's explicit instruction).
- Dropped fake-timer-based debounce simulation in the wizard's tests after it produced two failures (state updates from a `setTimeout` callback under `vi.useFakeTimers()` didn't flush through preact's microtask-scheduled rerender before assertions ran); switched to firing the real debounced input event and awaiting `waitFor`/`findByLabelText`, which passed cleanly and matches the async style already used elsewhere in this suite (e.g. `AttributionPanel.test.tsx`).
- `SingleDetectorEditorForm` renders `DetectorDisclosure` unconditionally (no `isTracked` branch) since the route's existence for a given `entityId` already implies the tracked-entity context; this differs cosmetically from `SensorListRow`'s `isSelected && isTracked` guard but is intentional per the plan's extraction instructions.

## Deviations from Plan

None - plan executed exactly as written. All three tasks' acceptance criteria (grep checks, import-boundary checks, and the CRITICAL WIZ-04 assertion) were verified directly against the final code.

## Issues Encountered
- Initial `AddDetectorWizard.test.tsx` draft combined `vi.useFakeTimers()` with `vi.advanceTimersByTime(200)` to fast-forward the `SearchInput` debounce, expecting the checkbox rows to appear synchronously afterward. They did not — preact's hook-triggered rerender runs on the microtask queue, which fake timers don't drive. Resolved by removing fake timers entirely and using `await screen.findByLabelText(...)` (real debounce, real wait) — no production code was affected, this was purely a test-authoring fix within Task 3, well under the 3-attempt auto-fix limit.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- `AddDetectorWizard` and `SingleDetectorEditorForm` are ready for 14-04's `main.tsx` route-table wiring (import by name, per the plan's stated purpose).
- The generalized `MemberPicker` is safe for `GroupEditorForm` to keep using unmodified (default `minQueryLength=2` preserved verbatim, confirmed by the untouched pre-existing test suite).
- Full frontend suite (`npx vitest run`) passes: 29 test files, 183 tests, no regressions.
- D-09 zero-backend-changes guard confirmed: no files under `orchestrator/Argus.Orchestrator/` touched in this plan's commits.

---
*Phase: 14-unified-detectors-screen-add-detector-wizard*
*Completed: 2026-07-21*

## Self-Check: PASSED

All 6 created/modified source files plus this SUMMARY.md confirmed present on disk; all three
task commit hashes (`808c1ad`, `b5f03a5`, `c05f1da`) confirmed in `git log`.
