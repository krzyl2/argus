---
phase: 08-group-config-ui-algorithm-chooser
plan: 02
subsystem: api
tags: [dotnet, minimal-api, yamldotnet, home-assistant-websocket, pyod, group-detection]

# Dependency graph
requires:
  - phase: 08-group-config-ui-algorithm-chooser
    provides: "08-01 param-aware group detectors (threshold/contamination/n_estimators param keys honored by Python)"
provides:
  - "GET /api/groups, POST /api/groups/save, GET /api/detectors/catalog, GET /api/groups/{id}/status — the 4 endpoints the Phase 8 SPA (Wave 2) consumes, contracts locked exactly as authored in 08-02-PLAN.md"
  - "IGroupStatusCache — in-memory last-verdict cache populated by BatchSchedulerWorker's joint branch, with contributions sorted descending (RESEARCH Pitfall 4 fix)"
  - "DetectorCatalog.All()/Guided() — static Low/Med/High presets, honest contamination-vs-score copy, guided answer->detector map"
  - "HaSensorEntry.AreaName/Domain enrichment via new HaWebSocketClient.GetAreaRegistryAsync/GetEntityRegistryAsync calls"
affects: [08-03, 08-04]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "GroupInputValidator mirrors EntitiesConfigLoader.ValidateGroups server-side (client is UX-only, server is authority split, same as InputValidator.cs)"
    - "_patterns: YAML block is re-read from raw on-disk YAML (Dictionary<object,object>) on group save, since EntitiesConfig does not model it and IgnoreUnmatchedProperties would otherwise drop it"

key-files:
  created:
    - orchestrator/Argus.Orchestrator/Web/DetectorCatalog.cs
    - orchestrator/Argus.Orchestrator/Web/GroupSaveRequest.cs
    - orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs
    - orchestrator/Argus.Orchestrator/Batch/GroupStatusCache.cs
    - orchestrator/Argus.Orchestrator.Tests/GroupStatusCacheTests.cs
    - orchestrator/Argus.Orchestrator.Tests/GroupsEndpointsTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs
    - orchestrator/Argus.Orchestrator/Ha/IHaSensorRegistry.cs
    - orchestrator/Argus.Orchestrator/Ha/HaSensorRegistry.cs
    - orchestrator/Argus.Orchestrator/Ha/HaWebSocketClient.cs
    - orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs
    - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
    - orchestrator/Argus.Orchestrator.Tests/GroupBatchSchedulerTests.cs
    - orchestrator/Argus.Orchestrator.Tests/HaSensorRegistryTests.cs
    - orchestrator/Argus.Orchestrator.Tests/GlobExpanderTests.cs
    - orchestrator/Argus.Orchestrator.Tests/SensorsEndpointJsonTests.cs
    - orchestrator/Argus.Orchestrator.Tests/SaveEndpointJsonTests.cs
    - orchestrator/Argus.Orchestrator.Tests/NetDaemonHaEventSourceLiveFilterTests.cs
    - orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs

key-decisions:
  - "HaSensorEntry.UpdateSnapshot's 3rd param (entityAreaNames) is optional/defaulted to null on the single interface method rather than a second overload — keeps every pre-existing fake IHaSensorRegistry implementation in the test suite compiling without touching files outside this plan's scope"
  - "HaSensorEntry.AreaName/Domain are non-default positional record params (compile-time safety net per plan) — all 3 direct positional-constructor call sites in tests were updated, not defaulted away"
  - "_patterns: is re-derived from the raw on-disk YAML (Dictionary<object,object>) on every group save rather than modeled in EntitiesConfig, since IgnoreUnmatchedProperties silently drops it on load — avoids losing include/exclude patterns on a groups-only save"
  - "GroupInputValidator extracted as its own file (not listed in the plan's files_modified) mirroring the existing InputValidator.cs convention, rather than inlining ~80 lines of validation logic into Program.cs's POST /api/groups/save handler"

patterns-established:
  - "GroupStatusCache: ConcurrentDictionary<string, GroupStatusEntry>, single writer (BatchSchedulerWorker joint branch), many readers (Kestrel) — generalizes the ArgusHealthSignals/HaSensorRegistry volatile-cache precedent to an open key set"

requirements-completed: [GRP-09, ALGO-01, ALGO-02, ALGO-03, SRCH-01, SRCH-02, SRCH-03]

# Metrics
duration: 10min
completed: 2026-07-02
status: complete
---

# Phase 8 Plan 02: Group endpoints, catalog, status cache, HA area enrichment Summary

**Four auth-guarded Minimal API endpoints (group CRUD, static detector catalog, attribution status) plus HA area/domain sensor enrichment — the exact backend contract Phase 8's SPA consumes, with the Pitfall-4 contribution-sort bug fixed before any UI reads it.**

## Performance

- **Duration:** 10 min
- **Started:** 2026-07-02T21:10:10Z
- **Completed:** 2026-07-02T21:20:17Z
- **Tasks:** 3
- **Files modified:** 20 (6 created, 14 modified)

## Accomplishments
- `DetectorCatalog.cs` returns 5 group-detector entries (peer_divergence/ecod/copod/pca/iforest), each with exactly 3 Low/Med/High presets using the exact param keys honored by 08-01's Python change, honest "best for…" copy that never claims contamination changes the anomaly score (RESEARCH Pitfall 2), and the guided `together→ecod` / `diverges→peer_divergence` map (ALGO-04)
- `GroupStatusCache` (ConcurrentDictionary-backed `IGroupStatusCache`) is now populated every batch cycle by `BatchSchedulerWorker`'s joint-mode branch, with `response.Contributions` sorted descending **before** either the "top contributor" log line or the cache write — fixes the pre-existing bug where `Contributions[0]` was not reliably the top contributor (RESEARCH Pitfall 4)
- `HaSensorEntry` gains `AreaName`/`Domain`; `HaWebSocketClient.GetAreaRegistryAsync`/`GetEntityRegistryAsync` fetch HA's area/entity registries once per connect (and on reconnect), joined into an entity_id→area_name map with entity-only `area_id` + domain fallback (no `device_registry` resolution this phase, degrades safely to an empty map on any WS failure)
- 4 new endpoints, each with `IsAuthorizedRequest` as the literal first line: `GET /api/groups` (live-config read), `POST /api/groups/save` (validates via `GroupInputValidator` — floor 3, peer-divergence unit consistency, 100-member cap — then full-list-replaces `groups:` while preserving `entities:`/`_patterns:` byte-for-byte via the existing `ConfigWriter`+`LiveEntitiesConfig.Swap` hot-reload pipeline), `GET /api/detectors/catalog` (zero gRPC calls), `GET /api/groups/{id}/status` (200-with-null for unknown id, no existence oracle)

## Task Commits

Each task was committed atomically:

1. **Task 1: DetectorCatalog + GroupStatusCache + HA registry enrichment** - `59dfad9` (feat)
2. **Task 2: Populate GroupStatusCache from BatchSchedulerWorker joint branch (GRP-09 + sort fix)** - `1f1f1bb` (feat)
3. **Task 3: Four endpoints in Program.cs + GroupSaveRequest DTO + server validation** - `a03d4f8` (feat)

**Plan metadata:** (this commit)

## Files Created/Modified
- `orchestrator/Argus.Orchestrator/Web/DetectorCatalog.cs` - static 5-entry catalog (presets, best-for copy, param schema, guided map)
- `orchestrator/Argus.Orchestrator/Web/GroupSaveRequest.cs` - nested save DTO matching the AUTHORITATIVE JSON contract
- `orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs` - server-side floor/unit-consistency/member-cap validation
- `orchestrator/Argus.Orchestrator/Batch/GroupStatusCache.cs` - `IGroupStatusCache`, `GroupStatusEntry`, `FeatureContributionDto`
- `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` - optional `IGroupStatusCache?` ctor param (both ctors), sort-then-cache in joint branch
- `orchestrator/Argus.Orchestrator/Program.cs` - DI registration for `IGroupStatusCache`, factory wiring, 4 new endpoints
- `orchestrator/Argus.Orchestrator/Ha/IHaSensorRegistry.cs` - `HaSensorEntry` gains `AreaName`/`Domain`; `UpdateSnapshot` gains optional `entityAreaNames`
- `orchestrator/Argus.Orchestrator/Ha/HaSensorRegistry.cs` - derives `Domain` from `EntityId.Split('.')[0]`, resolves `AreaName` from the new map
- `orchestrator/Argus.Orchestrator/Ha/HaWebSocketClient.cs` - `GetAreaRegistryAsync`/`GetEntityRegistryAsync`, `HaAreaDto`/`HaEntityRegistryDto`
- `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` - `BuildEntityAreaNamesAsync` joins area+entity registries once per connect
- `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` - `GroupUiValidationBlocked` (7009)
- Test files updated for the `HaSensorEntry` positional-constructor breaking change and the new `GroupStatusCache`/`GroupsEndpoints`/`GroupBatchScheduler` coverage

## Decisions Made
- Kept `UpdateSnapshot` as a single interface method with an optional trailing param instead of adding a second interface member — avoids forcing every pre-existing fake `IHaSensorRegistry` in the test suite (5 files) to implement a second method outside this plan's stated file list
- `_patterns:` is re-derived from the raw on-disk YAML on every group save rather than modeled in `EntitiesConfig` — `IgnoreUnmatchedProperties()` silently drops unknown top-level keys on load, so a naive groups-only save using only `liveCfg.Get()` would have clobbered `include`/`exclude` patterns
- `GroupInputValidator` extracted as its own file (not in the plan's declared `files_modified` list) mirroring the existing `InputValidator.cs` convention — keeps `Program.cs`'s `POST /api/groups/save` handler proportionate to the existing `/api/sensors/save` handler rather than inlining ~80 lines of validation

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] Extracted GroupInputValidator.cs (not in plan's files_modified)**
- **Found during:** Task 3
- **Issue:** The plan's `<action>` describes server-side validation "mirroring EntitiesConfigLoader.ValidateGroups" inline in the `POST /api/groups/save` handler, but the codebase's own established convention (`InputValidator.cs` for `/api/sensors/save`) is a standalone testable class, not inline logic
- **Fix:** Added `orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs` following the `InputValidator.cs` shape exactly (static class, `Validate(...)` returning `List<string>`)
- **Files modified:** `orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs` (new), `Program.cs` (calls it)
- **Verification:** 7 dedicated validation tests in `GroupsEndpointsTests.cs` (floor, unit consistency, member cap, unknown mode, duplicates) all pass
- **Committed in:** `a03d4f8` (Task 3 commit)

---

**Total deviations:** 1 auto-fixed (1 missing-critical/convention-consistency)
**Impact on plan:** Matches existing codebase conventions; no scope creep — the validation logic itself is exactly what the plan specified, just in a separate file per Rule 11 (match codebase conventions).

## Issues Encountered
None.

## User Setup Required

None - no external service configuration required. The HA area/entity registry WS calls (`config/area_registry/list`, `config/entity_registry/list`) are LOW-confidence field shapes per 08-RESEARCH.md A1 — live-HA verification of area enrichment is recommended but not a blocker (degrades safely to domain-only grouping on any parse mismatch).

## Next Phase Readiness
- All 4 endpoint contracts are locked exactly as declared AUTHORITATIVE in 08-02-PLAN.md — Wave 2 (SPA `types.ts`) can implement against them without further backend changes
- `GroupStatusCache` sort-before-cache fix means `AttributionPanel`'s "top-rank gets accent color" contract (UI-SPEC) is now honestly satisfied
- HA area/entity registry field shapes remain LOW-confidence (RESEARCH A1) — recommend a live-HA smoke test during 08-UAT to confirm `SRCH-02`/`SRCH-03` area grouping actually populates on a real instance (falls back to domain-only grouping if not, per documented v1 scope)
- No blockers for 08-03/08-04

---
*Phase: 08-group-config-ui-algorithm-chooser*
*Completed: 2026-07-02*

## Self-Check: PASSED

All 7 created files verified present on disk; all 3 task commit hashes (59dfad9, 1f1f1bb, a03d4f8) verified in git log.
