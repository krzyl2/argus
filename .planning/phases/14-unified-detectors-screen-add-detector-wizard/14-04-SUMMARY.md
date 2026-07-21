---
phase: 14-unified-detectors-screen-add-detector-wizard
plan: 04
subsystem: ui
tags: [preact, detectors-list, routing, integration]

requires:
  - phase: 14-01-router-sidebar-detector-rows
    provides: "detectorRows computed signal (state/detectors.ts), routeSensorEntityId signal, /detectors default route + redirects"
  - phase: 14-02-single-sensor-editor-add-wizard
    provides: "AddDetectorWizard.tsx and SingleDetectorEditorForm.tsx (both mount-load the full sensor set per D-07)"
provides:
  - "DetectorsPage.tsx — /detectors list screen loading groups + full sensor set, rendering the unified DetectorList"
  - "DetectorList.tsx + DetectorListRow.tsx — Card-wrapped unified list with two navigate-only row variants (group/sensor)"
  - "main.tsx route table wired end-to-end: /detectors, /detectors/add, /detectors/sensor/:id, plus fallback -> DetectorsPage"
affects: []

tech-stack:
  added: []
  patterns:
    - "Discriminated-union row dispatch (row.kind === 'group' | 'sensor') rendered as sibling functions inside one component file, mirroring the analog split of GroupListRow/SensorListRow but unified under one <li> shape"
    - "Route-switch integration gate via tsc -b — proves all Wave 1/Wave 2 exports resolve before runtime"

key-files:
  created:
    - orchestrator/ui/src/components/DetectorsPage.tsx
    - orchestrator/ui/src/components/DetectorsPage.test.tsx
    - orchestrator/ui/src/components/DetectorList.tsx
    - orchestrator/ui/src/components/DetectorList.test.tsx
    - orchestrator/ui/src/components/DetectorListRow.tsx
    - orchestrator/ui/src/components/DetectorListRow.test.tsx
  modified:
    - orchestrator/ui/src/main.tsx

key-decisions:
  - "Omitted the optional 'assigned detector name(s)' badge on the sensor row variant — DetectorRow (14-01) carries only the SensorEntry, not its detector list; deriving it would require importing entityEdits into DetectorListRow, adding coupling the plan explicitly marked optional ('if readily derivable, else omit'). Kept the tracked Badge only."
  - "Left SensorsPage.tsx and its import fully removed from main.tsx but the file itself undeleted on disk, per the plan's explicit surgical instruction (copy-source for 14-02/14-03 pattern excerpts; a later cleanup pass removes it)."
  - "Did not add a dedicated main.tsx/App render-switch test file — the plan's files_modified/task-3 <files> scope names only main.tsx, and the task's own <verify> block only requires tsc -b (the stated integration gate proving all three imported page components resolve). The route-switch behavior is exercised indirectly: DetectorsPage/AddDetectorWizard/SingleDetectorEditorForm/GroupsPage each have their own mount-render test, and the full 195-test suite confirms no regression. Flagged below as the one D-05/DET-05 must-have truth not covered by a dedicated automated test."

patterns-established:
  - "Unified list assembly: DetectorsPage (loader) -> DetectorList (shell) -> DetectorListRow (two-variant row) — the terminal integration point for 14-01's detectorRows + 14-02's editor routes."

requirements-completed: [DET-01, DET-02, DET-03, DET-05]

coverage:
  - id: D1
    description: "/detectors renders one unified list containing both a group row and a tracked-sensor row, sourced from detectorRows"
    requirement: "DET-01"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/DetectorsPage.test.tsx#renders one unified list containing both a group row and a tracked-sensor row (DET-01)"
        status: pass
      - kind: unit
        ref: "orchestrator/ui/src/components/DetectorList.test.tsx#renders one row per entry across the unified group + sensor list (DET-01)"
        status: pass
    human_judgment: false
  - id: D2
    description: "Group row's Edit link points to #/groups/<encoded groupId> (unchanged GroupEditorForm)"
    requirement: "DET-02"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/DetectorListRow.test.tsx#group variant: Edit link points to #/groups/<encoded groupId>, no delete/untrack control (D-04/D-08a)"
        status: pass
    human_judgment: false
  - id: D3
    description: "Sensor row's Edit link points to #/detectors/sensor/<encoded entityId>; rows only navigate — no checkbox, no inline disclosure, no untrack/delete on any row (D-08a)"
    requirement: "DET-03"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/DetectorListRow.test.tsx#sensor variant: Edit link points to #/detectors/sensor/<encoded entityId>, no checkbox, no untrack/delete control (D-03/D-08a)"
        status: pass
    human_judgment: false
  - id: D4
    description: "main.tsx routes /detectors -> DetectorsPage, /detectors/add -> AddDetectorWizard, /detectors/sensor/:id -> SingleDetectorEditorForm, and the fallback branch renders DetectorsPage (not SensorsPage)"
    requirement: "DET-05"
    verification:
      - kind: other
        ref: "npx tsc -b (integration gate: all three imported page components + routeSensorEntityId resolve); grep confirms SensorsPage import removed and fallback renders DetectorsPage"
        status: pass
    human_judgment: true
    rationale: "No dedicated App render/switch test exists (out of this task's stated file scope — see key-decisions). tsc -b + grep prove the wiring compiles and is structurally correct, but the reactive route-switch behavior itself (clicking through /detectors -> /detectors/add -> back, etc.) has not been exercised by an automated render test — recommend a quick manual click-through or a follow-up main.test.tsx."
  - id: D5
    description: "Zero backend changes — no files under orchestrator/Argus.Orchestrator/ modified"
    requirement: "DET-05"
    verification:
      - kind: other
        ref: "git diff --name-only -- orchestrator/Argus.Orchestrator/ (0 files)"
        status: pass
    human_judgment: false
  - id: D6
    description: "Unified group-row and sensor-row variants render as one visually-consistent list matching the Argus Design System reference (shared .argus-list rhythm, badge tones, row meta layout)"
    verification: []
    human_judgment: true
    rationale: "Visual/layout fidelity backstop must_have — requires human visual review against the Design System reference, not automatable from unit tests alone."

duration: 12min
completed: 2026-07-21
status: complete
---

# Phase 14 Plan 04: Unified Detectors Screen Assembly + Route Wiring Summary

**Assembled `/detectors` as the unified group+sensor list (`DetectorsPage` -> `DetectorList` -> `DetectorListRow`) and wired all three new routes into `main.tsx`, repointing the app's default fallback from `SensorsPage` to `DetectorsPage`.**

## Performance

- **Duration:** ~12 min
- **Started:** 2026-07-21T20:44:00Z
- **Completed:** 2026-07-21T20:56:00Z
- **Tasks:** 3 completed
- **Files modified:** 7 (1 modified, 6 created)

## Accomplishments
- `DetectorList.tsx` + `DetectorListRow.tsx`: unified, Card-wrapped `<ul class="argus-list">` with two navigate-only row variants dispatched on `row.kind` — group rows relocate `GroupListRow`'s look (minus the delete-with-confirm control) linking to the unchanged `#/groups/:id`; sensor rows relocate `SensorListRow`'s look (minus checkbox/inline-disclosure) linking to `#/detectors/sensor/:id`. Neither variant exposes any destructive action (D-08a).
- `DetectorsPage.tsx`: the `/detectors` list screen — mount effect calls `loadGroups()` + `loadSensors('')` (full set, D-07 guard), renders the DS header, an "Add detector" primary CTA to `#/detectors/add`, and `<DetectorList rows={detectorRows.value} />`.
- `main.tsx`: added `/detectors/add` -> `AddDetectorWizard`, `/detectors/sensor/*` -> `SingleDetectorEditorForm`, `/detectors` -> `DetectorsPage`; fallback branch now renders `DetectorsPage` instead of `SensorsPage`; `SensorsPage` import removed. `/dashboard`, `/algorithms`, `/settings`, and the groups routes (`/groups`, `/groups/new`, `/groups/:id`) are untouched.

## Task Commits

Each task was committed atomically:

1. **Task 1: DetectorList + DetectorListRow — unified two-variant navigate-only rows (D-03, D-08a, DET-01/DET-02/DET-03)** - `4c55bd1` (feat)
2. **Task 2: DetectorsPage — load both sources, render the unified list (D-03, DET-01)** - `75f15cd` (feat)
3. **Task 3: Wire the new routes in main.tsx; repoint the fallback to DetectorsPage (D-05, DET-05)** - `540979a` (feat)

_No TDD tasks in this plan — all tasks were `type="auto"` without `tdd="true"`._

## Files Created/Modified
- `orchestrator/ui/src/components/DetectorListRow.tsx` - two-variant navigate-only row (group -> `#/groups/:id`, sensor -> `#/detectors/sensor/:id`); no delete/untrack/checkbox on either variant
- `orchestrator/ui/src/components/DetectorListRow.test.tsx` - asserts both Edit hrefs (including URL-encoding), and the absence of delete/untrack controls and the sensor checkbox
- `orchestrator/ui/src/components/DetectorList.tsx` - Card-wrapped `<ul class="argus-list">` mapping `DetectorRow[]`, custom `.argus-empty` branch
- `orchestrator/ui/src/components/DetectorList.test.tsx` - Card wrap, mixed group+sensor row count, empty-state branch
- `orchestrator/ui/src/components/DetectorsPage.tsx` - `/detectors` route page; mount-loads groups + full sensor set, renders header/CTA/DetectorList
- `orchestrator/ui/src/components/DetectorsPage.test.tsx` - mount-fetch assertions (both `api/groups` and `api/sensors?q=`), unified two-row render, CTA presence
- `orchestrator/ui/src/main.tsx` - route switch gains three `/detectors*` branches; fallback -> `DetectorsPage`; `SensorsPage` import removed

## Decisions Made
- Omitted the optional "assigned detector(s)" badge on the sensor row — `DetectorRow` doesn't carry detector data, and the plan explicitly permitted omission when not readily derivable.
- Left `SensorsPage.tsx` on disk (unreferenced) rather than deleting it — plan's explicit surgical instruction to avoid a same-wave source race, since 14-02/14-03 pattern excerpts still reference it.
- Did not add a dedicated main.tsx/App render-switch test — see `coverage` D4's `rationale` above. This is the one gap worth a human's attention before sign-off.

## Deviations from Plan

None - plan executed exactly as written. All three tasks' acceptance criteria (grep checks, vitest runs, `tsc -b`) verified directly against the final code.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- `/detectors` is now the app's default and unified list screen; `/detectors/add` and `/detectors/sensor/:entityId` are reachable end-to-end; `/groups/new` and `/groups/:id` remain reachable (not from the sidebar, per 14-01's D-02, but via direct navigation/hand-off).
- Full frontend suite (`npx vitest run`) passes: 33 test files, 195 tests, no regressions.
- `npx tsc -b` type-checks clean — the integration gate proving 14-01 + 14-02 + 14-04 wiring is consistent.
- D-09 zero-backend-changes guard confirmed: `git diff --name-only -- orchestrator/Argus.Orchestrator/` returns 0 files.
- Recommend a quick manual click-through of `/detectors` -> `/detectors/add` -> (1-sensor and >=2-sensor exits) -> back, and a visual comparison against the Argus Design System reference, before closing out Phase 14 (covers the two `human_judgment: true` coverage items above).

---
*Phase: 14-unified-detectors-screen-add-detector-wizard*
*Completed: 2026-07-21*

## Self-Check: PASSED

All created/modified files confirmed present on disk (`DetectorsPage.tsx`, `DetectorList.tsx`,
`DetectorListRow.tsx`, `main.tsx`, plus their test files); all three task commit hashes
(`4c55bd1`, `75f15cd`, `540979a`) confirmed in `git log`.
