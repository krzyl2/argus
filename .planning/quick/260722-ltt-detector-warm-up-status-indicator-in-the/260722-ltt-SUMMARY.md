---
phase: 260722-ltt
plan: 01
subsystem: ui
tags: [preact, signals, dotnet, minimal-api, concurrent-dictionary, hst]

requires:
  - phase: 14-unified-detectors-screen-add-detector-wizard
    provides: DetectorsPage, DetectorListRow, DetectorRow model, GET /api/sensors JSON contract
provides:
  - Public EntityRuntimeState.ReadingCount/WarmUpWindow getters
  - EntityStatusCache singleton (Detection namespace), pipeline-fed per reading
  - GET /api/sensors warmedUp/readingCount/warmUpWindow fields (tracked-only)
  - SensorEntry warm-up fields + warm-up chip on the Detectors sensor row
  - 5s loadSensors('') polling on DetectorsPage
affects: [detectors-screen, sensors-endpoint]

tech-stack:
  added: []
  patterns:
    - "EntityStatusCache mirrors the existing Batch/GroupStatusCache single-writer/many-reader ConcurrentDictionary pattern, generalized to Detection namespace for per-entity (not per-group) state"

key-files:
  created:
    - orchestrator/Argus.Orchestrator/Detection/EntityStatusCache.cs
    - orchestrator/Argus.Orchestrator.Tests/EntityRuntimeStateTests.cs
    - orchestrator/Argus.Orchestrator.Tests/EntityStatusCacheTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Detection/EntityRuntimeState.cs
    - orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/Argus.Orchestrator.Tests/SensorsEndpointJsonTests.cs
    - orchestrator/ui/src/api/types.ts
    - orchestrator/ui/src/components/DetectorListRow.tsx
    - orchestrator/ui/src/components/DetectorsPage.tsx
    - orchestrator/ui/src/components/DetectorListRow.test.tsx
    - orchestrator/ui/src/components/DetectorsPage.test.tsx

key-decisions:
  - "IEntityStatusCache injected as a trailing optional constructor param (default null) on both ScoreStreamPipeline constructors — DI fills it in production, existing tests that construct the pipeline without a cache keep compiling/passing unchanged"
  - "Warm-up status looked up only for tracked entities (tracked ? statusCache.Get(...) : null) — isTracked itself stays sourced from SensorTracking.TrackedIds(liveCfg.Get()), preserving the G-14-1 fix"
  - "DetectorsPage.test.tsx polling test reuses the file's existing apiGet-mock pattern (real fetches already blocked) plus a setInterval/clearInterval spy, rather than the plan's suggested vi.mock('../state/sensors', ...) partial-mock — a module-level vi.mock would have been file-scoped and broken the file's three pre-existing tests that rely on real loadSensors/loadGroups behavior"

requirements-completed: [QUICK-warmup-status]

coverage:
  - id: D1
    description: "EntityRuntimeState exposes public ReadingCount/WarmUpWindow getters tracking the existing private warm-up fields"
    verification:
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/EntityRuntimeStateTests.cs"
        status: pass
    human_judgment: false
  - id: D2
    description: "EntityStatusCache singleton round-trips EntityStatusEntry (case-insensitive keys, replace-on-Set)"
    verification:
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/EntityStatusCacheTests.cs"
        status: pass
    human_judgment: false
  - id: D3
    description: "ScoreStreamPipeline write loop publishes a per-reading warm-up snapshot to the cache (null-safe, existing pipeline tests unaffected)"
    verification:
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs (full suite unaffected — 404/404 backend tests pass)"
        status: pass
    human_judgment: false
  - id: D4
    description: "GET /api/sensors projects warmedUp/readingCount/warmUpWindow for tracked entities only (null for untracked, null pre-first-reading); isTracked stays config-sourced (G-14-1 not regressed)"
    verification:
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/SensorsEndpointJsonTests.cs#ProjectEntries_TrackedWithCachedWarmingStatus_ProjectsWarmUpFields, #ProjectEntries_TrackedWithCachedWarmedUpStatus_ProjectsWarmedUpTrue, #ProjectEntries_UntrackedWithCachedEntry_WarmUpFieldsAreNull, #ProjectEntries_TrackedWithEmptyCache_WarmUpFieldsAreNull, #ProjectEntries_TrackedInConfig_IsTrackedTrue"
        status: pass
    human_judgment: false
  - id: D5
    description: "Sensor row shows 'Rozgrzewka N/window' while warming and 'Działa' once warmed; group rows and no-status sensor rows never show a chip"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/DetectorListRow.test.tsx (4 new cases)"
        status: pass
    human_judgment: false
  - id: D6
    description: "DetectorsPage polls loadSensors('') every 5s and clears the interval on unmount"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/DetectorsPage.test.tsx#sets a 5s loadSensors(\"\") interval on mount and clears it on unmount"
        status: pass
    human_judgment: false
  - id: D7
    description: "Live end-to-end behavior: N climbs across real ~5s refreshes against a real HA sensor, flipping to Działa at 250"
    verification: []
    human_judgment: true
    rationale: "Requires a live HA instance with a freshly-tracked sensor and wall-clock observation across the warm-up window; not reproducible in the unit-test harness."

duration: 10min
completed: 2026-07-22
status: complete
---

# Quick Task 260722-ltt: Detector Warm-Up Status Indicator Summary

**Per-entity HST warm-up progress ("Rozgrzewka N/250" -> "Działa") surfaced end-to-end: pipeline-fed EntityStatusCache, GET /api/sensors projection, and a live-polling chip on the unified Detectors sensor row.**

## Performance

- **Duration:** ~10 min
- **Completed:** 2026-07-22
- **Tasks:** 3
- **Files modified:** 12 (3 created, 9 modified)

## Accomplishments
- EntityRuntimeState.ReadingCount/WarmUpWindow are now public; EntityStatusCache (new, Detection namespace) mirrors the existing GroupStatusCache single-writer/many-reader pattern
- ScoreStreamPipeline's write loop publishes a warm-up snapshot to the cache on every reading, via a null-safe optional DI param that doesn't break either existing constructor
- GET /api/sensors now returns warmedUp/readingCount/warmUpWindow for tracked entities only (null otherwise), without touching the config-sourced isTracked derivation (G-14-1)
- Detectors sensor row renders a Polish warm-up/working chip (Rozgrzewka N/window or Działa); group rows and untracked/no-status rows are unaffected
- DetectorsPage polls loadSensors('') every 5s (cleared on unmount) so the count advances live with no manual refresh

## Task Commits

1. **Task 1: Expose warm-up getters + EntityStatusCache fed by the pipeline** - `0fa24ef` (feat)
2. **Task 2: Register cache in DI + project warm-up status in GET /api/sensors** - `f72f5d0` (feat)
3. **Task 3: SensorEntry status fields + warm-up chip + 5s polling on Detectors page** - `96d994d` (feat)

_No RED/GREEN/REFACTOR TDD gates — plan was `type: auto` (non-TDD)._

## Files Created/Modified
- `orchestrator/Argus.Orchestrator/Detection/EntityRuntimeState.cs` - public ReadingCount/WarmUpWindow getters over existing private fields
- `orchestrator/Argus.Orchestrator/Detection/EntityStatusCache.cs` - new EntityStatusEntry record + IEntityStatusCache/EntityStatusCache (ConcurrentDictionary, OrdinalIgnoreCase)
- `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs` - trailing optional IEntityStatusCache? param on both constructors; write loop calls `_statusCache?.Set(...)` per reading
- `orchestrator/Argus.Orchestrator/Program.cs` - DI registration `AddSingleton<IEntityStatusCache, EntityStatusCache>()`; GET /api/sensors handler extended with warmedUp/readingCount/warmUpWindow (tracked-only)
- `orchestrator/Argus.Orchestrator.Tests/EntityRuntimeStateTests.cs` - new: WarmUpWindow/ReadingCount/WarmedUp transition tests
- `orchestrator/Argus.Orchestrator.Tests/EntityStatusCacheTests.cs` - new: Get/Set round-trip, replace, case-insensitivity
- `orchestrator/Argus.Orchestrator.Tests/SensorsEndpointJsonTests.cs` - ProjectEntries mirror extended with optional cache param + 4 new warm-up projection tests
- `orchestrator/ui/src/api/types.ts` - SensorEntry gains optional warmedUp/readingCount/warmUpWindow
- `orchestrator/ui/src/components/DetectorListRow.tsx` - SensorRow renders warm-up/Działa chip gated on `readingCount != null && warmUpWindow != null`
- `orchestrator/ui/src/components/DetectorsPage.tsx` - mount effect adds `setInterval(() => loadSensors(''), 5000)` with `clearInterval` cleanup
- `orchestrator/ui/src/components/DetectorListRow.test.tsx` - 4 new sensor/group warm-up chip cases
- `orchestrator/ui/src/components/DetectorsPage.test.tsx` - 1 new polling-wiring test (setInterval/clearInterval spy)

## Decisions Made
- IEntityStatusCache is a trailing optional constructor parameter (default null) on both ScoreStreamPipeline constructors, so DI auto-fills it in production while every existing test constructor call site keeps compiling unchanged.
- Warm-up status is looked up only when `tracked` is true; `isTracked` itself remains sourced from `SensorTracking.TrackedIds(liveCfg.Get())` — the G-14-1 fix is untouched.
- For the DetectorsPage polling test, reused the file's existing `apiGet`-mock pattern (already blocks real fetches) plus a `setInterval`/`clearInterval` spy, instead of the plan's suggested `vi.mock('../state/sensors', ...)` partial-mock. A module-level `vi.mock` is file-scoped (hoisted) and would have silently broken this file's three pre-existing tests, which depend on the real `loadSensors`/`loadGroups` implementations to populate `sensors.value`/`groups.value` from the mocked `apiGet`.

## Deviations from Plan

None - plan executed exactly as written, with one adapted (not skipped) test-implementation detail documented above (state-module mocking strategy for the polling test) to avoid breaking pre-existing tests in the same file.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Backend: `dotnet test orchestrator/Argus.Orchestrator.sln` — 404/404 passed.
- Frontend: `cd orchestrator/ui && npm test -- --run` — 200/200 passed (33 test files).
- Manual live-HA sanity check (N climbing across ~5s refreshes, flipping to Działa at 250) not performed in this session — flagged as D7 (human_judgment) above; no blocker for merge, deferred to live-HA observation.

---
*Phase: 260722-ltt*
*Completed: 2026-07-22*

## Self-Check: PASSED
