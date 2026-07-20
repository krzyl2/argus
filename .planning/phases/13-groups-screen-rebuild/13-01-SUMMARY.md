---
phase: 13-groups-screen-rebuild
plan: 01
subsystem: ui
tags: [preact, design-system, groups, member-picker]

requires:
  - phase: 12-sensors-screen-rebuild
    provides: Card/Badge/Select/Input/Checkbox shared primitives, .argus-page-header/.argus-section-label CSS conventions, external-label+FieldValidationError pattern
provides:
  - Card-wrapped Groups list with mode + detector Badges per row
  - GroupEditorForm shell rebuilt to DS page-header + shared Input/Select
  - MemberPicker rebuilt to Card + Checkbox + Badge, MIN_QUERY_LENGTH=2 gate committed
affects: [13-02-groups-screen-rebuild, 13-03-groups-screen-rebuild]

tech-stack:
  added: []
  patterns:
    - "Groups list/member-picker follow the same Card+Badge/Checkbox adoption pattern as Phase 12's Sensors screen"

key-files:
  created:
    - orchestrator/ui/src/components/GroupList.test.tsx
    - orchestrator/ui/src/components/GroupListRow.test.tsx
    - orchestrator/ui/src/components/GroupEditorForm.test.tsx
    - orchestrator/ui/src/components/MemberPicker.test.tsx
  modified:
    - orchestrator/ui/src/components/GroupsPage.tsx
    - orchestrator/ui/src/components/GroupList.tsx
    - orchestrator/ui/src/components/GroupListRow.tsx
    - orchestrator/ui/src/components/GroupEditorForm.tsx
    - orchestrator/ui/src/components/MemberPicker.tsx

key-decisions:
  - "Folded the pre-existing uncommitted MIN_QUERY_LENGTH=2 diff on MemberPicker.tsx into this phase's first MemberPicker commit as the D-07-locked target behavior, not reverted"
  - "GroupListRow's detector Badge uses tone=accent alongside the existing mode Badge tone=neutral, matching the DS reference's two-badge row (Assumption A1)"
  - "GroupEditorForm's Back-to-groups control only sets location.hash — the hash-router mechanism itself is untouched (D-01)"

patterns-established:
  - "Card-wrapped list + two Badge row (mode + detector), preserving two-step delete/status meta verbatim"
  - "External <label> + shared Input + FieldValidationError for the group-name field (mirrors Phase 12's DetectorParamGrid convention)"

requirements-completed: [GRP-12]

coverage:
  - id: D1
    description: "Groups list renders in a Card, each row shows a mode Badge and a detector Badge, custom empty-state branch for zero groups"
    requirement: "GRP-12"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/GroupList.test.tsx"
        status: pass
    human_judgment: false
  - id: D2
    description: "Two-step delete confirm (arm/confirm/timeout) and status/no-status meta preserved after the row restyle"
    requirement: "GRP-12"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/GroupListRow.test.tsx"
        status: pass
    human_judgment: false
  - id: D3
    description: "GroupEditorForm name field via shared Input+FieldValidationError, mode field via shared Select, DS page-header with Back-to-groups affordance, child slots (MemberPicker/AlgorithmChooser/SaveBar) composed unchanged"
    requirement: "GRP-12"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/GroupEditorForm.test.tsx"
        status: pass
    human_judgment: false
  - id: D4
    description: "MemberPicker MIN_QUERY_LENGTH=2 gate (both branches), rows via shared Checkbox + Badge, wrapped in Card, toggle wiring calls onToggleMember correctly"
    requirement: "GRP-12"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/MemberPicker.test.tsx"
        status: pass
    human_judgment: false
  - id: D5
    description: "Both themes render the rebuilt list/editor/member-picker correctly (visual sign-off)"
    requirement: "GRP-12"
    verification: []
    human_judgment: true
    rationale: "Visual/theme parity requires human eyeballing in a live browser session; not something a unit test can assert."

duration: 4min
completed: 2026-07-20
status: complete
---

# Phase 13 Plan 01: Groups List, Editor Shell & Member Picker Summary

**Groups list/editor/member-picker rebuilt to the Argus Design System — Card-wrapped rows with mode+detector Badges, shared Input/Select for the name/mode fields, and Card+Checkbox+Badge member rows behind the folded-in MIN_QUERY_LENGTH=2 gate.**

## Performance

- **Duration:** 4 min
- **Started:** 2026-07-20T06:55:20Z
- **Tasks:** 3
- **Files modified:** 5 (+ 4 new test files)

## Accomplishments
- Groups list (`GroupsPage.tsx`/`GroupList.tsx`/`GroupListRow.tsx`) now uses a DS `.argus-page-header`, a `Card`-wrapped row list, and a second detector `Badge` alongside the mode `Badge`, while the two-step delete confirm and status meta stay byte-identical.
- `GroupEditorForm.tsx`'s name field now uses the shared `Input` (external label + `FieldValidationError`) and the mode field uses the shared `Select`; the heading became a `.argus-page-header` with a `Back to groups` control, and section headings switched to `.argus-section-label`.
- `MemberPicker.tsx` folded the previously-uncommitted `MIN_QUERY_LENGTH = 2` gate into this plan's history as the correct target behavior, and its rows now use the shared `Checkbox` + `Badge` wrapped in a `Card`.
- All business logic (`state/groups.ts`, `validation/groupParams.ts`) is untouched — confirmed unmodified in every commit's diff.

## Task Commits

Each task was committed atomically:

1. **Task 1: Rebuild the Groups list — Card-wrapped GroupList + two-Badge GroupListRow + DS page-header** - `e2ee832` (feat)
2. **Task 2: Rebuild GroupEditorForm shell — name Input, mode Select, DS page-header + Back affordance** - `0db3a8e` (feat)
3. **Task 3: Rebuild MemberPicker — Card + Checkbox + Badge rows, fold the MIN_QUERY_LENGTH gate** - `c955f92` (feat)

_Note: no separate RED/GREEN test commits — `tdd="true"` tasks here wrote the render/behavior implementation and its test file together per task, matching Phase 12's convention for markup-refactor-with-tests plans._

## Files Created/Modified
- `orchestrator/ui/src/components/GroupsPage.tsx` - list branch now renders `.argus-page-header`
- `orchestrator/ui/src/components/GroupList.tsx` - Card-wrapped `<ul>`, custom empty-state branch kept
- `orchestrator/ui/src/components/GroupListRow.tsx` - added detector `Badge`; delete/status logic unchanged
- `orchestrator/ui/src/components/GroupEditorForm.tsx` - Input/Select adoption, page-header + Back affordance
- `orchestrator/ui/src/components/MemberPicker.tsx` - Card/Checkbox/Badge adoption; MIN_QUERY_LENGTH gate committed
- `orchestrator/ui/src/components/GroupList.test.tsx` - new: Card wrap, row count, empty-state
- `orchestrator/ui/src/components/GroupListRow.test.tsx` - new: two-Badge row, delete arm/confirm/timeout, status meta
- `orchestrator/ui/src/components/GroupEditorForm.test.tsx` - new: page-header title switch, Input/Select wiring, child slots
- `orchestrator/ui/src/components/MemberPicker.test.tsx` - new: MIN_QUERY_LENGTH gate both branches, Checkbox/Badge, toggle wiring

## Decisions Made
- Detector Badge uses `tone="accent"` per Assumption A1 in 13-RESEARCH.md (low-risk, easily adjustable if design review disagrees).
- Kept the existing `<a href="#/groups/{id}">Edit</a>` link as the row's navigation target rather than adding whole-row click-to-edit (Assumption A4 / Claude's discretion) — zero risk, D-02 only mandates the Card+two-Badge visual, not a click-target change.
- Section headings in `GroupEditorForm.tsx` ("Members", "Choose algorithm") switched to the `.argus-section-label` raw-class convention established in Phase 11, consistent with the action's instruction.

## Deviations from Plan

None - plan executed exactly as written. The MemberPicker MIN_QUERY_LENGTH gate was folded in per the plan's explicit instruction (not a deviation — it was the plan's stated starting point).

## Issues Encountered
- `GroupListRow.test.tsx`'s timeout-revert assertion initially failed because Preact's `setState` inside a `vi`-faked `setTimeout` callback doesn't flush synchronously under jsdom; wrapped the timer advance in `@testing-library/preact`'s `act()` to fix. No production code change was needed — test-only fix.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- 13-02 (algorithm wizard) and 13-03 (attribution panel) can proceed — `GroupEditorForm.tsx`'s `AlgorithmChooser`/`AttributionPanel` slots are composed with identical props, untouched by this plan.
- Live-HA visual/theme sign-off for this plan's rendered rows/fields remains deferred to `/gsd-verify-work` (coverage D5), consistent with the phase's verification section.

---
*Phase: 13-groups-screen-rebuild*
*Completed: 2026-07-20*

## Self-Check: PASSED

All 9 created/modified files confirmed present on disk; all 3 task commits (e2ee832, 0db3a8e, c955f92) confirmed present in git history.
