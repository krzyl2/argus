---
phase: 14-unified-detectors-screen-add-detector-wizard
plan: 01
subsystem: ui
tags: [preact, router, navigation, signals, sidebar]

requires:
  - phase: 13-groups-screen-rebuild
    provides: state/groups.ts (groups signal, GroupConfig), stable GroupEditorForm reused via /groups/:id
provides:
  - "/detectors default route + legacy redirects for bare /sensors and /groups (D-01/D-05)"
  - "parseSensorEntityId parser + routeSensorEntityId signal for /detectors/sensor/:entityId (D-01)"
  - "Sidebar restructured to Detectors + Add detector nav items (D-02/D-04)"
  - "state/detectors.ts detectorRows computed signal merging groups + tracked sensors (D-03/DET-01)"
affects: [14-02, 14-03, 14-04]

tech-stack:
  added: []
  patterns:
    - "Computed-signal merge (mirrors state/sensors.ts's validationErrors/hasValidationErrors pair)"
    - "decodeURIComponent + try/catch defensive parser (mirrors parseGroupId idiom)"

key-files:
  created:
    - orchestrator/ui/src/router.test.ts
    - orchestrator/ui/src/state/detectors.ts
    - orchestrator/ui/src/state/detectors.test.ts
  modified:
    - orchestrator/ui/src/router.ts
    - orchestrator/ui/src/components/Sidebar.tsx
    - orchestrator/ui/src/components/Sidebar.test.tsx

key-decisions:
  - "normalizeHash and parseSensorEntityId exported (were module-internal) so router.test.ts can import them directly, per plan's explicit fallback instruction"
  - "Redirect kept inside normalizeHash rather than a separate redirectLegacyRoutes helper — simpler, same net effect, no location.hash rewrite (avoids hashchange reentrancy)"
  - "detectorRows returns groups-first then sensors — Claude's discretion per 14-CONTEXT.md, simplest stable order"

patterns-established:
  - "Unified DetectorRow discriminated union (kind: 'group' | 'sensor') with namespaced keys — the shape 14-04's DetectorsPage/DetectorList consume"

requirements-completed: [DET-01, DET-04, DET-05]

coverage:
  - id: D1
    description: "Bare #/sensors and #/groups redirect to /detectors; empty hash defaults to /detectors; /groups/new and /groups/:id parse unchanged"
    requirement: "DET-05"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/router.test.ts#normalizeHash (D-01/D-05 default route + legacy redirects)"
        status: pass
    human_judgment: false
  - id: D2
    description: "parseSensorEntityId decodes /detectors/sensor/:entityId and returns null on malformed percent-encoding"
    requirement: "DET-05"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/router.test.ts#parseSensorEntityId (D-01)"
        status: pass
    human_judgment: false
  - id: D3
    description: "Sidebar shows Detectors + Add detector, no Sensors/Groups; /detectors/* sub-routes highlight Detectors"
    requirement: "DET-04"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/Sidebar.test.tsx#Sidebar (D-02 nav items + THEME-02 toggle)"
        status: pass
    human_judgment: false
  - id: D4
    description: "detectorRows computed signal merges groups + tracked-only sensors with namespaced keys and discriminant"
    requirement: "DET-01"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/state/detectors.test.ts#detectorRows (D-03/DET-01 merge)"
        status: pass
    human_judgment: false

duration: 5min
completed: 2026-07-21
status: complete
---

# Phase 14 Plan 01: Router + Sidebar + Detector-Row Data Plumbing Summary

**Repointed the hash router's default route to `/detectors` with legacy `/sensors`/`/groups` redirects, restructured the sidebar nav, and added a computed `detectorRows` signal merging groups + tracked sensors into one row list.**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-07-21T18:25:00Z
- **Completed:** 2026-07-21T18:29:00Z
- **Tasks:** 3 completed
- **Files modified:** 6 (3 modified, 3 created)

## Accomplishments
- `router.ts`: default route is now `/detectors`; bare `/sensors`/`/groups` redirect to `/detectors` (exact-match only); `/groups/new`/`/groups/:id` unchanged; new `parseSensorEntityId` + `routeSensorEntityId` signal for `/detectors/sensor/:entityId`
- `Sidebar.tsx`: `Sensors`/`Groups` nav items removed; `Detectors` + `Add detector` items added; `/detectors/*` sub-routes highlight the Detectors item
- `state/detectors.ts`: new `detectorRows` computed signal — pure derivation over `groups`/`sensors`/`entityEdits`, no new fetch logic, namespaced `group:`/`sensor:` keys

## Task Commits

Each task was committed atomically:

1. **Task 1: Router — new default route, legacy redirects, and single-sensor entity-id parser** - `9198aa3` (feat)
2. **Task 2: Sidebar nav restructure** - `e4dd8bf` (feat)
3. **Task 3: Merged detector-row computed signal** - `e8f170c` (feat)

_No TDD tasks in this plan — all tasks were `type="auto"` without `tdd="true"`._

## Files Created/Modified
- `orchestrator/ui/src/router.ts` - default route `/detectors`, redirect logic in `normalizeHash`, new `parseSensorEntityId`/`routeSensorEntityId`, boot effect retargeted; `parseGroupId` untouched (verified via diff)
- `orchestrator/ui/src/router.test.ts` - new; asserts redirects, pass-throughs, default, and parse success/failure
- `orchestrator/ui/src/components/Sidebar.tsx` - `NAV_ITEMS`/`isActive` restructured per D-02/D-04
- `orchestrator/ui/src/components/Sidebar.test.tsx` - added label-presence assertions and an active-route assertion for `/detectors/*`
- `orchestrator/ui/src/state/detectors.ts` - new; `DetectorRow` interface + `detectorRows` computed signal
- `orchestrator/ui/src/state/detectors.test.ts` - new; merge count, discriminant, namespaced keys, tracked-only filtering (including entityEdits-only tracking)

## Decisions Made
- Exported `normalizeHash`/`parseSensorEntityId` from `router.ts` (previously module-internal) so the new test file can import them directly — plan explicitly authorized this fallback.
- Kept the legacy-redirect logic inline inside `normalizeHash` rather than a separate `redirectLegacyRoutes` helper; the URL bar keeps the typed bare path (cosmetic-only, per plan's explicit instruction to avoid a `location.hash` rewrite and hashchange reentrancy).
- `detectorRows` order is groups-first then sensors (Claude's discretion per 14-CONTEXT.md).

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- `routeSensorEntityId`, `detectorRows`, and the restructured Sidebar nav are ready for 14-04 (route wiring + `DetectorsPage`/`DetectorList` consumption).
- Full frontend suite (`npx vitest run`) passes: 27 test files, 175 tests, no regressions.
- D-09 zero-backend-changes guard confirmed: no files under `orchestrator/Argus.Orchestrator/` touched in this plan's commits.

---
*Phase: 14-unified-detectors-screen-add-detector-wizard*
*Completed: 2026-07-21*

## Self-Check: PASSED

All created/modified files confirmed present on disk; all three task commit hashes (`9198aa3`, `e4dd8bf`, `e8f170c`) confirmed in `git log`.
