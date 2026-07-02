---
phase: 08-group-config-ui-algorithm-chooser
plan: 03
subsystem: ui
tags: [preact, signals, vitest, group-config, search, area-grouping]

# Dependency graph
requires:
  - phase: 08-group-config-ui-algorithm-chooser
    provides: "08-02 GET/POST /api/groups, GET /api/detectors/catalog, GET /api/groups/{id}/status endpoint contracts + HA area/domain sensor enrichment"
provides:
  - "#/groups, #/groups/new, #/groups/:id routes and nav; group list, editor, member-picker screens"
  - "state/groups.ts (groups signal, loadGroups, saveGroup, deleteGroup) and validation/groupParams.ts (floor-3 + peer-unit-consistency)"
  - "SRCH-01 friendly_name-or-entity_id search (client predicate + server-side HaSensorRegistry.GetFiltered fix) and SRCH-02 area/domain browse grouping in SensorList"
  - "Two-click staged 'Delete group' confirm on GroupListRow, reusing the full-list-replace POST api/groups/save (no new backend endpoint)"
affects: [08-04]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "state/groups.ts mirrors state/sensors.ts's signal + monotonic-seq-guard + discriminated-union saveState pattern exactly"
    - "deleteGroup composes on the same saveGroup POST path (full-list-replace minus one group) rather than a dedicated delete endpoint"
    - "GroupSaveResultBanner is a separate component from SaveResultBanner (same ok/kind branching logic, group-specific copy/fields — no shared component forced across incompatible response shapes)"

key-files:
  created:
    - orchestrator/ui/src/state/groups.ts
    - orchestrator/ui/src/validation/groupParams.ts
    - orchestrator/ui/src/components/GroupList.tsx
    - orchestrator/ui/src/components/GroupListRow.tsx
    - orchestrator/ui/src/components/GroupsPage.tsx
    - orchestrator/ui/src/components/GroupEditorForm.tsx
    - orchestrator/ui/src/components/MemberPicker.tsx
    - orchestrator/ui/src/components/GroupSaveResultBanner.tsx
    - orchestrator/ui/src/components/sensorMatch.ts
    - orchestrator/ui/src/state/groups.test.ts
    - orchestrator/ui/src/validation/groupParams.test.ts
  modified:
    - orchestrator/ui/src/api/types.ts
    - orchestrator/ui/src/router.ts
    - orchestrator/ui/src/main.tsx
    - orchestrator/ui/src/components/AppShell.tsx
    - orchestrator/ui/src/components/SensorSearchInput.tsx
    - orchestrator/ui/src/components/SensorSearchInput.test.tsx
    - orchestrator/ui/src/components/SensorList.tsx
    - orchestrator/ui/src/components/SensorListRow.test.tsx
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/Argus.Orchestrator/Ha/HaSensorRegistry.cs
    - orchestrator/Argus.Orchestrator.Tests/HaSensorRegistryTests.cs

key-decisions:
  - "HaSensorRegistry.GetFiltered extended to match friendly_name OR entity_id server-side — SRCH-01 could not work end-to-end from client copy alone since #/sensors's search is server-filtered via GET /api/sensors?q="
  - "GET /api/sensors payload now serializes areaName/domain — HaSensorEntry already carried these fields since 08-02 but Program.cs never put them in the JSON, so SRCH-02 area grouping had no data to render"
  - "MemberPicker renders its own lightweight checkbox rows (reusing .argus-list/.argus-checkbox classes) instead of wrapping SensorListRow, since SensorListRow's detector-disclosure UI does not apply to member selection"
  - "GroupSaveResultBanner is a new component, not a shared one with SaveResultBanner — group save copy/fields (memberCount, no hasHst) differ from the sensor save response shape"
  - "GroupEditorForm's member-picker search query is local component state (useState), not a shared/module-level signal — avoids leaking query state across route/editor-instance switches"

patterns-established:
  - "Group draft signals (draftGroupId/draftFriendlyName/draftMembers/draftMode/draftDetector/draftParams) live in state/groups.ts alongside the groups list signal, one draft edited at a time (single-screen editor)"

requirements-completed: [SRCH-01, SRCH-02, SRCH-03]

# Metrics
duration: 15min
completed: 2026-07-02
status: complete
---

# Phase 8 Plan 03: Group authoring SPA foundation + search/browse Summary

**Group list/editor/member-picker screens wired to the 08-02 endpoints with client-side floor/unit validation, plus a server-side bug fix that unblocked friendly_name search and area-grouped sensor browse.**

## Performance

- **Duration:** ~15 min
- **Started:** 2026-07-02T19:22:00Z
- **Completed:** 2026-07-02T19:35:07Z
- **Tasks:** 3
- **Files modified:** 22 (11 created, 11 modified)

## Accomplishments
- New hash routes `#/groups`, `#/groups/new`, `#/groups/:id` (hand-rolled, no router library) with a Sensors/Groups nav row in `AppShell`; `GroupsPage` routes internally between `GroupList` and `GroupEditorForm`
- `api/types.ts` gained `GroupConfig`/`GroupSaveRequest`/`DetectorCatalog`/`GroupStatus` DTOs verified field-for-field against 08-02's shipped C# shapes (`GroupSaveRequest.cs`, `DetectorCatalog.cs`, `GroupStatusCache.cs`, and the exact anonymous-object projections in `Program.cs`'s 4 endpoints)
- `state/groups.ts` mirrors `state/sensors.ts`'s signal + monotonic-sequence stale-response guard + discriminated-union `saveState` pattern; `saveGroup`/`deleteGroup` both POST the full `groups:` list (upsert-by-id / filter-out-by-id) since the backend only exposes a full-list-replace save endpoint
- `validation/groupParams.ts`: `validateGroupMembers` (floor 3) and `validateUnitConsistency` (peer-mode only) return the verbatim UI-SPEC copy, mirroring `GroupInputValidator.cs`'s server-side rules
- `GroupEditorForm` + `MemberPicker`: name/mode fields, member multi-select with live floor/unit-mismatch validation surfaced via `FieldValidationError`, a slot for 08-04's `AlgorithmChooser`, and save wired through `saveGroup()` with a group-specific `GroupSaveResultBanner`
- `GroupListRow` ships the "Delete group" -> "Confirm delete" staged two-click affordance (armed boolean + ~3s revert timer, no `window.confirm()`), calling `deleteGroup(groupId)` which posts the groups list minus that id then refreshes
- SRCH-01/02: `SensorSearchInput` placeholder updated to "Filter by name or entity ID…"; `SensorList` gained an optional `groupByArea` mode rendering one `<details>`/`.argus-disclosure-toggle` section per HA area (alphabetical, domain/"Ungrouped" fallback last, `"{Area} ({count})"` header)

## Task Commits

Each task was committed atomically:

1. **Task 1: Types, router, app-shell nav, group state + validation** - `9a7ec49` (feat)
2. **Task 2: Group list + editor + member picker; SRCH-01/02 extensions** - `90bc627` (feat)
3. **Task 3: Delete group (staged two-click confirm, full-list-replace save)** - `d77368d` (feat)

**Plan metadata:** (this commit)

## Files Created/Modified
- `orchestrator/ui/src/api/types.ts` - Group/catalog/status DTOs; `SensorEntry` gains `areaName`/`domain`
- `orchestrator/ui/src/router.ts` - `#/groups`, `#/groups/new`, `#/groups/:id` hash parsing + `routeGroupId` signal
- `orchestrator/ui/src/main.tsx` / `AppShell.tsx` - route to `GroupsPage`; Sensors/Groups nav links
- `orchestrator/ui/src/state/groups.ts` - groups signal, draft signals, `loadGroups`/`saveGroup`/`deleteGroup`
- `orchestrator/ui/src/validation/groupParams.ts` - `validateGroupMembers`, `validateUnitConsistency`
- `orchestrator/ui/src/components/GroupList.tsx` / `GroupListRow.tsx` - group list + delete-confirm row
- `orchestrator/ui/src/components/GroupsPage.tsx` / `GroupEditorForm.tsx` / `MemberPicker.tsx` - editor screens
- `orchestrator/ui/src/components/GroupSaveResultBanner.tsx` - group-specific save result banner
- `orchestrator/ui/src/components/sensorMatch.ts` - shared `matchesSensorQuery` (entity_id OR friendly_name) predicate
- `orchestrator/ui/src/components/SensorSearchInput.tsx` / `SensorList.tsx` - SRCH-01 placeholder; SRCH-02 area-grouping mode
- `orchestrator/Argus.Orchestrator/Program.cs` - `/api/sensors` payload now includes `areaName`/`domain`
- `orchestrator/Argus.Orchestrator/Ha/HaSensorRegistry.cs` - `GetFiltered` matches `friendly_name` too

## Decisions Made
- Extended `HaSensorRegistry.GetFiltered` (server-side, backs `GET /api/sensors?q=`) to match `friendly_name` OR `entity_id` — the plan's client-only search copy change could not satisfy SRCH-01 on its own, since `#/sensors`'s actual filtering happens server-side, not client-side
- Added `areaName`/`domain` to the `/api/sensors` JSON projection in `Program.cs` — `HaSensorEntry` carried these fields since 08-02 but they were never serialized, so `SensorList`'s new area-grouping mode had no data to group by
- Built `MemberPicker`'s row rendering directly (reusing `.argus-list`/`.argus-checkbox`/`.argus-row-*` classes) rather than wrapping `SensorListRow`, since that component's detector-disclosure panel is specific to per-entity detector assignment and does not apply to group membership selection
- `GroupSaveResultBanner` is a new, separate component rather than generalizing `SaveResultBanner` — the success copy and response fields (`memberCount` vs `count`/`hasHst`) genuinely differ per the UI-SPEC Copywriting Contract

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] HaSensorRegistry.GetFiltered did not match friendly_name (SRCH-01 blocker)**
- **Found during:** Task 2
- **Issue:** `GET /api/sensors?q=` (the actual search backing `#/sensors` and `MemberPicker`'s server-loaded list) filtered on `entity_id` only. The plan's Task 2 action only specified a placeholder/client-predicate change, but the client never re-filters server results for `#/sensors` — the server-side filter is authoritative. Without this fix, SRCH-01 ("SensorSearchInput matches friendly_name OR entity_id") would be unreachable for the one screen where search is live (server round-trip), silently failing the plan's own must-have truth.
- **Fix:** Extended `HaSensorRegistry.GetFiltered` to `OR` against `FriendlyName` (case-insensitive), a strict superset of the existing `entity_id` match.
- **Files modified:** `orchestrator/Argus.Orchestrator/Ha/HaSensorRegistry.cs`, `orchestrator/Argus.Orchestrator.Tests/HaSensorRegistryTests.cs` (added `GetFiltered_MatchesFriendlyNameWhenEntityIdDoesNotMatch_SRCH01`)
- **Verification:** All 353 orchestrator tests pass (16/16 `HaSensorRegistryTests` including the new case)
- **Committed in:** `90bc627` (Task 2 commit)

**2. [Rule 1 - Bug] GET /api/sensors never serialized areaName/domain (SRCH-02 blocker)**
- **Found during:** Task 2
- **Issue:** `HaSensorEntry.AreaName`/`Domain` were added in 08-02 but the `/api/sensors` endpoint's anonymous-object JSON projection in `Program.cs` only mapped `entityId`/`friendlyName`/`currentValue`/`unitOfMeasurement`/`isTracked` — the SPA could never receive area/domain data, so the newly-built `SensorList` area-grouping mode (SRCH-02) would render with no grouping information regardless of client code correctness.
- **Fix:** Added `areaName = e.AreaName, domain = e.Domain` to the projection.
- **Files modified:** `orchestrator/Argus.Orchestrator/Program.cs`
- **Verification:** Orchestrator builds clean; all 353 tests pass (the existing `SensorsEndpointJsonTests.cs` mirrors the projection locally and was unaffected, since it doesn't assert against the live endpoint)
- **Committed in:** `90bc627` (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (both Rule 1/2 — backend gaps that would have silently defeated this plan's own SRCH-01/SRCH-02 must-haves if left unfixed)
**Impact on plan:** Both fixes were required for the plan's stated success criteria to be achievable at all; no scope creep beyond the two exact gaps found, and both are covered by tests.

## Issues Encountered
None beyond the two auto-fixed deviations above.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- Group authoring path (list -> create/edit -> member picker with validation -> save/delete) works end-to-end against the 08-02 endpoints
- `#/groups/:id`'s `GroupEditorForm` already reads/writes `draftDetector`/`draftParams` and has an `#algorithm-chooser-slot` mount point — Plan 08-04 (`AlgorithmChooser`, `AttributionPanel`) can fill this in without further plumbing changes
- `#/sensors` verified unchanged (zero regression): `SensorSearchInput`'s placeholder change and predicate extension are additive; `SensorList`'s area-grouping mode defaults to off (`groupByArea` prop, unused by `SensorsPage`)
- No blockers for 08-04

---
*Phase: 08-group-config-ui-algorithm-chooser*
*Completed: 2026-07-02*

## Self-Check: PASSED

All 10 created/referenced files verified present on disk; all 4 commit hashes (9a7ec49, 90bc627, d77368d, 9c53409) verified in git log.
