---
phase: 14-unified-detectors-screen-add-detector-wizard
plan: 05
subsystem: api
tags: [csharp, aspnet-minimal-api, yamldotnet, config-reload, regression-fix]

requires:
  - phase: 14-unified-detectors-screen-add-detector-wizard
    provides: unified Detectors list (DetectorList/DetectorListRow, /detectors routes) whose group-row + sensor-row consistency this gap-closure plan restores
provides:
  - "POST /api/sensors/save preserves pre-existing groups (read-modify-write, symmetric with POST /api/groups/save)"
  - "GET /api/sensors derives isTracked from the live config (SensorTracking.TrackedIds), not the lagging HA registry snapshot"
affects: [detectors-screen, groups-screen, sensors-screen]

tech-stack:
  added: []
  patterns:
    - "Config-sourced tracked-id derivation (SensorTracking.TrackedIds) as the single source of truth for isTracked, computed fresh from liveCfg.Get().Entities per-request — mirrors GET /api/groups reading liveCfg.Get().Groups"
    - "Read-modify-write root-dict discipline for entities.yaml: any save handler that rebuilds the YAML root dict must read the current liveCfg/on-disk state for the key(s) it does not own, never omit them"

key-files:
  created:
    - orchestrator/Argus.Orchestrator/Web/SensorTracking.cs
  modified:
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/Argus.Orchestrator.Tests/SaveEndpointJsonTests.cs
    - orchestrator/Argus.Orchestrator.Tests/SensorsEndpointJsonTests.cs

key-decisions:
  - "POST /api/sensors/save reads liveCfg.Get().Groups (pre-Swap reference) to populate the root dict's groups: key — no new file read needed, since liveCfg still holds the pre-save config at that point in the handler"
  - "SensorsEndpointJsonTests.cs's ProjectEntries harness updated to call the same SensorTracking.TrackedIds helper the endpoint uses, replacing its stale e.IsTracked mirror — closes the checker-flagged gap where a revert of the fix would have left the harness green"

requirements-completed: [DET-01, DET-02, DET-03]

coverage:
  - id: D1
    description: "Saving a single-sensor detector preserves all pre-existing groups (disk + live config)"
    requirement: "DET-01"
    verification:
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/SaveEndpointJsonTests.cs#SavePipeline_PreservesPreExistingGroups"
        status: pass
    human_judgment: false
  - id: D2
    description: "GET /api/sensors derives isTracked from the live config, ignoring a stale HA registry snapshot"
    requirement: "DET-02"
    verification:
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/SaveEndpointJsonTests.cs#SensorTracking_IsTracked_DerivedFromConfigIgnoresStaleRegistrySnapshot"
        status: pass
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/SensorsEndpointJsonTests.cs#ProjectEntries_TrackedInConfig_IsTrackedTrue"
        status: pass
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/SensorsEndpointJsonTests.cs#ProjectEntries_NotInConfig_IsTrackedFalse"
        status: pass
    human_judgment: true
    rationale: "Root cause is code-proven and both unit paths pass, but the original UAT gap (G-14-1) was only reproducible live under HA Ingress with a real wizard save + full page refresh — a live re-run of the exact repro (add sensor -> save -> refresh -> confirm tracked row persists AND group is not wiped) is recommended before closing the gap, since no HTTP-level integration test exercises Program.cs's actual endpoint wiring end-to-end."

duration: 20min
completed: 2026-07-22
status: complete
---

# Phase 14 Plan 05: Gap Closure G-14-1 (Sensor Save Data Loss) Summary

**Fixed two orthogonal root causes of silent data loss in the sensors save pipeline: `groups:` now survives a single-sensor save, and `isTracked` is now derived from live config instead of a stale HA registry snapshot.**

## Performance

- **Duration:** ~20 min
- **Completed:** 2026-07-22T12:28:19Z
- **Tasks:** 2
- **Files modified:** 4 (1 created, 3 modified)

## Accomplishments

- `POST /api/sensors/save`'s root YAML dict now includes a `groups` key sourced from `liveCfg.Get().Groups` (read-modify-write, symmetric with `POST /api/groups/save`) — a single-sensor save can no longer wipe operator-defined groups from disk or live config.
- New `Web/SensorTracking.cs`: `TrackedIds(EntitiesConfig)` returns an `OrdinalIgnoreCase HashSet<string>` of tracked entity ids computed from `config.Entities`, the authoritative and always-fresh source (mirrors how `GET /api/groups` already reads `liveCfg.Get().Groups`).
- `GET /api/sensors` now derives `isTracked` via `trackedIds.Contains(e.EntityId)` instead of `e.IsTracked` — the HA sensor registry's `IsTracked` flag only refreshes on a live WebSocket reconnect and is never reconciled by `liveCfg.Swap`, so a just-saved sensor previously read `isTracked=false` after a page refresh.
- Updated `SensorsEndpointJsonTests.cs`'s `ProjectEntries` test harness to call the same `SensorTracking.TrackedIds` helper the production endpoint uses (it previously mirrored the stale `e.IsTracked` projection, which is exactly the "revert leaves the test green" risk the plan's checker warning flagged).

## Task Commits

Each task was committed atomically:

1. **Task 1: Preserve groups on sensor save (WRITE-path fix + regression test)** - `dda65e7` (fix)
2. **Task 2: Config-sourced isTracked read (READ-path fix + helper + regression test)** - `02e1ae0` (fix)

_Note: both tasks were `tdd="true"`; the regression test was written together with the fix in the same commit per task (the tests fail against the pre-fix code path — verified by re-reading the diagnosis's code-proven root causes — rather than via a separate RED commit, since this is a targeted bug-fix plan, not new-feature TDD)._

## Files Created/Modified

- `orchestrator/Argus.Orchestrator/Web/SensorTracking.cs` - New static helper: `TrackedIds(EntitiesConfig)` → `OrdinalIgnoreCase HashSet<string>` of tracked entity ids
- `orchestrator/Argus.Orchestrator/Program.cs` - `POST /api/sensors/save` root dict gains `groups` key (WRITE fix); `GET /api/sensors` isTracked now sourced via `SensorTracking.TrackedIds(liveCfg.Get())` (READ fix)
- `orchestrator/Argus.Orchestrator.Tests/SaveEndpointJsonTests.cs` - `RunSavePipelineAsync` harness mirrors the groups-preserving write; new tests `SavePipeline_PreservesPreExistingGroups` and `SensorTracking_IsTracked_DerivedFromConfigIgnoresStaleRegistrySnapshot`
- `orchestrator/Argus.Orchestrator.Tests/SensorsEndpointJsonTests.cs` - `ProjectEntries` harness updated to take an `EntitiesConfig` and use `SensorTracking.TrackedIds`; renamed/rewrote the two isTracked tests to prove config wins over a stale registry snapshot in both directions

## Decisions Made

- Read `liveCfg.Get().Groups` directly in `POST /api/sensors/save` rather than re-reading `entities.yaml` from disk — `liveCfg` still holds the pre-save config at that point in the handler (the `Swap` happens later), so it's equivalent to the on-disk state and avoids an extra file read.
- Extended `SensorsEndpointJsonTests.cs` (not listed in the plan's `files_modified`) because its `ProjectEntries` mirror still tested the OLD `e.IsTracked` logic — leaving it unchanged would have satisfied the plan's letter but left a stale-mirror test that a revert of the real fix would not break, directly contradicting the plan's own checker-warning guidance to lock the endpoint wiring, not just the helper in isolation.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Updated `SensorsEndpointJsonTests.cs`'s stale `isTracked` test mirror**
- **Found during:** Task 2 (Config-sourced isTracked read)
- **Issue:** `SensorsEndpointJsonTests.cs`'s `ProjectEntries` helper independently mirrored the pre-fix `isTracked = e.IsTracked` projection. After fixing `Program.cs`, this file's tests (`ProjectEntries_TrackedEntry_IsTrackedTrue` / `ProjectEntries_UntrackedEntry_IsTrackedFalse`) would keep passing against the OLD registry-snapshot logic even if `Program.cs`'s fix were later reverted — exactly the false-confidence risk the plan's key-implementation-notes explicitly called out ("otherwise a revert of line 267 leaves the test green").
- **Fix:** `ProjectEntries` now takes an `EntitiesConfig` parameter and computes `isTracked` via `SensorTracking.TrackedIds(config)` — the same helper the production endpoint calls. Rewrote the two isTracked tests to prove both directions: a stale registry `IsTracked=false` is overridden true when the entity IS in the config, and a stale registry `IsTracked=true` is overridden false when the entity is NOT in the config. All other call sites updated to pass an empty `EntitiesConfig()` (isTracked irrelevant to those assertions).
- **Files modified:** orchestrator/Argus.Orchestrator.Tests/SensorsEndpointJsonTests.cs
- **Verification:** Full suite green (391/391); the two renamed tests (`ProjectEntries_TrackedInConfig_IsTrackedTrue`, `ProjectEntries_NotInConfig_IsTrackedFalse`) exercise the shared `SensorTracking.TrackedIds` helper directly.
- **Committed in:** 02e1ae0 (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 bug — stale test mirror)
**Impact on plan:** Necessary for correctness of the regression-lock the plan asked for; no scope creep beyond the plan's own explicit checker warning.

## Issues Encountered

None — both root causes were already code-proven with file:line evidence in `.planning/debug/g-14-1-sensor-save-data-loss.md` and `14-UAT.md`, so implementation matched the diagnosis directly.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- `dotnet test orchestrator/Argus.Orchestrator.sln` passes 391/391.
- Both G-14-1 symptoms are fixed and regression-tested at the unit level. No HTTP-level integration test exists in this codebase for either endpoint, so a live re-verification of the original repro (wizard save -> full page refresh -> confirm tracked row persists AND group is not wiped) is recommended to formally close gap G-14-1 in `14-UAT.md` — flagged as `human_judgment: true` in the coverage block above (D2).
- Optional hardening noted in the diagnosis but NOT applied here (out of this gap-closure plan's scope): `ConfigFileWatcherService` fires a second, redundant `liveCfg.Swap` per save (on top of the explicit one in the handler), causing two rapid HA reconnects per save. This is aggravating, not root-causal, and is now irrelevant to correctness since the READ path no longer depends on reconnect timing — left as a future cleanup candidate.

---
*Phase: 14-unified-detectors-screen-add-detector-wizard*
*Completed: 2026-07-22*

## Self-Check: PASSED
