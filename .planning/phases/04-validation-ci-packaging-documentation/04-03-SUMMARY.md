---
phase: 04-validation-ci-packaging-documentation
plan: 03
subsystem: workers
tags: [file-watcher, debounce, config-reload, hosted-service, unit-tests]
dependency_graph:
  requires:
    - "04-01: ILiveEntitiesConfig.Swap + LogEvents.ConfigFileWatcherReloadFailed=7007 in LogEvents.cs"
  provides:
    - "ConfigFileWatcherService BackgroundService watching entitiesPath dir for Renamed(entities.yaml)"
    - "300ms timer-reset debounce — N rapid renames coalesce to one Load+Swap (SC4)"
    - "Invalid external edits logged and ignored — pipeline never crashes (T-04-09)"
    - "ConfigFileWatcherService registered in Program.cs DI"
  affects:
    - "orchestrator/Argus.Orchestrator/Program.cs — AddHostedService<ConfigFileWatcherService>()"
tech_stack:
  added: []
  patterns:
    - "sealed BackgroundService + null-guard constructor (HealthPublisherWorker pattern)"
    - "Interlocked.Exchange timer-reset debounce — dispose old timer, create new one atomically"
    - "stoppingToken.WaitHandle.WaitOne() blocking wait — correct for FSW-based service"
    - "Internal seams: InternalReload / SimulateRenamedEvent / SetDebounceIntervalMs for testing without inotify"
    - "Only watcher.Renamed subscribed — no watcher.Changed (Pitfall 2 avoidance)"
key_files:
  created:
    - orchestrator/Argus.Orchestrator/Workers/ConfigFileWatcherService.cs
    - orchestrator/Argus.Orchestrator.Tests/ConfigFileWatcherServiceTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Program.cs
decisions:
  - "LogEvents.ConfigFileWatcherReloadFailed=7007 was already present from Plan 04-01 — no change to LogEvents.cs needed"
  - "InternalReload exposed as internal method so tests call Reload directly without FSW timing dependencies"
  - "SimulateRenamedEvent respects the wrong-filename guard (OrdinalIgnoreCase path comparison) — exercises the same code path as real Renamed handler"
  - "SetDebounceIntervalMs(50ms) used in debounce coalescing test to avoid 300ms wait in CI"
metrics:
  duration: "~10 minutes"
  completed: "2026-07-01"
  tasks: 2
  files: 3
---

# Phase 04 Plan 03: ConfigFileWatcherService Summary

FileSystemWatcher-based BackgroundService with 300ms timer-reset debounce, watching for atomic renames to entities.yaml; invalid external edits logged and ignored; registered in DI.

---

## Tasks Completed

| Task | Name | Commit | Key Outputs |
|------|------|--------|-------------|
| 1 (RED) | ConfigFileWatcherService failing tests | cb51f76 | 9 test cases — debounce coalescing, invalid-edit-ignored, null-guards, wrong-filename |
| 1 (GREEN) | ConfigFileWatcherService implementation | 298a9e2 | ConfigFileWatcherService.cs with internal seams; 9/9 tests pass |
| 2 | Register ConfigFileWatcherService in DI | 152ba19 | Program.cs AddHostedService<ConfigFileWatcherService>(); build 0 errors |

---

## Deviations from Plan

### LogEvents.cs already had ConfigFileWatcherReloadFailed=7007

**Rule: no deviation — pre-condition already met**
- **Found during:** Task 1 setup
- **Issue:** The plan noted LogEvents.ConfigFileWatcherReloadFailed=new(7007,...) needed to be added. It was already present at line 56 from Plan 04-01's execution (04-01-SUMMARY.md confirms UiValidationBlocked=7008 was Plan 04-01's addition; 7007 was added in the same wave).
- **Fix:** No change needed. Service references LogEvents.ConfigFileWatcherReloadFailed directly.
- **Impact:** None — acceptance criterion (LogEvents.cs contains 7007) is satisfied without modification.

---

## Known Stubs

None. The service is fully implemented. The live inotify/FileSystemWatcher behavior on Linux (IN_MOVED_TO → Renamed) is a non-inferable item routed to human UAT (documented in plan's non_inferable section).

---

## Threat Flags

No new threat surface introduced. All four STRIDE threats from the plan's threat_model are mitigated:

| Threat | Mitigation | Status |
|--------|-----------|--------|
| T-04-09 — Invalid external edit DoS | Reload wraps Load+Swap in try/catch; invalid config logged+ignored | DONE |
| T-04-10 — Rapid-fire reload event storm | 300ms timer-reset debounce coalesces N events to one reload | DONE |
| T-04-11 — Watcher path traversal | Watch dir derived from ConnectionSettings.EntitiesPath only (no user input) | ACCEPT |
| T-04-12 — Post-shutdown reload | Debounce timer disposed on stoppingToken; catch handles FileNotFoundException | DONE |

---

## Verification

- `dotnet build Argus.Orchestrator/Argus.Orchestrator.csproj -c Debug` — PASS (0 warnings, 0 errors)
- `dotnet test --filter "FullyQualifiedName~ConfigFileWatcherServiceTests"` — PASS (9/9)
- `grep -n "watcher.Changed" ConfigFileWatcherService.cs` — 0 results (only comments; no Changed subscription)
- `grep -n "AddHostedService<ConfigFileWatcherService>" Program.cs` — line 124
- `grep -n "ConfigFileWatcherReloadFailed = new(7007" LogEvents.cs` — line 56
- `grep -n "File\.Write\|File\.Move\|ConfigWriter" ConfigFileWatcherService.cs` — 0 write calls (only in comments)

## TDD Gate Compliance

- RED gate: commit cb51f76 `test(04-03): add failing tests for ConfigFileWatcherService` — build errors confirmed
- GREEN gate: commit 298a9e2 `feat(04-03): implement ConfigFileWatcherService with 300ms debounce` — 9/9 pass

## Self-Check: PASSED

- `orchestrator/Argus.Orchestrator/Workers/ConfigFileWatcherService.cs` — FOUND
- `orchestrator/Argus.Orchestrator.Tests/ConfigFileWatcherServiceTests.cs` — FOUND
- `orchestrator/Argus.Orchestrator/Program.cs` (AddHostedService<ConfigFileWatcherService>) — FOUND
- `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` (ConfigFileWatcherReloadFailed=7007) — FOUND
- Commit cb51f76 (RED tests) — FOUND
- Commit 298a9e2 (GREEN implementation) — FOUND
- Commit 152ba19 (Task 2 DI registration) — FOUND
