---
phase: 03-config-readwrite-detector-assignment-reload
plan: 01
subsystem: config-reload
tags: [volatile-swap, mqtt-retraction, live-config, thread-safety, tdd]
dependency_graph:
  requires: []
  provides: [ILiveEntitiesConfig, LiveEntitiesConfig, DiscoveryPublisher.RetractAsync, LogEvents.ConfigReloadTriggered, LogEvents.ConfigReloadComplete, LogEvents.MqttRetractionPublished]
  affects: [DiscoveryPublisher, LogEvents]
tech_stack:
  added: []
  patterns: [volatile-reference-swap, Interlocked.Exchange, delegate-overload-for-testability, tdd-red-green]
key_files:
  created:
    - orchestrator/Argus.Orchestrator/Config/LiveEntitiesConfig.cs
    - orchestrator/Argus.Orchestrator.Tests/LiveEntitiesConfigTests.cs
    - orchestrator/Argus.Orchestrator.Tests/MqttRetractionTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs
    - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
decisions:
  - "RetractAsync uses delegate overload for testability, mirroring PublishAllAsync pattern — avoids needing an IMqttConnection interface"
  - "Named parameters dropped from delegate invocation (Func<> does not support named args) — positional bool retain=true used instead"
metrics:
  duration: "~10m"
  completed_date: "2026-07-01"
  tasks: 2
  files: 5
---

# Phase 03 Plan 01: LiveEntitiesConfig + DiscoveryPublisher.RetractAsync Summary

**One-liner:** `ILiveEntitiesConfig` volatile-swap singleton (Interlocked.Exchange + ConfigChanged event) plus `DiscoveryPublisher.RetractAsync` delegate-overload for removed-entity MQTT discovery retraction.

## Tasks Completed

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 (RED) | LiveEntitiesConfig failing tests | 0270b39 | LiveEntitiesConfigTests.cs |
| 1 (GREEN) | ILiveEntitiesConfig + LiveEntitiesConfig + LogEvents | 0d26d2f | LiveEntitiesConfig.cs, LogEvents.cs |
| 2 (RED) | DiscoveryPublisher.RetractAsync failing tests | 583258e | MqttRetractionTests.cs |
| 2 (GREEN) | DiscoveryPublisher.RetractAsync implementation | 4739077 | DiscoveryPublisher.cs |

## Verification

- `dotnet build orchestrator/Argus.Orchestrator.sln` — 0 errors, 0 warnings
- `dotnet test orchestrator/Argus.Orchestrator.Tests` — 177 passed (baseline 160 + 7 LiveEntitiesConfigTests + 9 MqttRetractionTests + 1 existing known test = 177)
- No existing constructor signatures or DI registrations changed

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Named parameters not supported on Func delegate invocations**
- **Found during:** Task 2 GREEN compilation
- **Issue:** `await publish(..., retain: true, ct)` — the `retain:` named argument is invalid for `Func<string, string, bool, CancellationToken, Task>` (delegates use positional args, not named)
- **Fix:** Changed to positional: `await publish(..., string.Empty, true, ct)`
- **Files modified:** `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs`
- **Commit:** 4739077 (same GREEN commit)

## TDD Gate Compliance

RED commits precede GREEN commits for both tasks:
1. `test(03-01)` at 0270b39 (Task 1 RED)
2. `feat(03-01)` at 0d26d2f (Task 1 GREEN)
3. `test(03-01)` at 583258e (Task 2 RED)
4. `feat(03-01)` at 4739077 (Task 2 GREEN)

## Known Stubs

None — this plan produces only production-ready implementation code. No hardcoded empty values or placeholder text flow to UI rendering.

## Threat Surface Scan

No new network endpoints, auth paths, file access patterns, or schema changes introduced. The `RetractAsync` method publishes to the MQTT broker (existing trust boundary from `DiscoveryPublisher`). T-03-01 mitigated: only the passed `removedEntities` set is iterated; test `RetractAsync_OnlyRetractsPassedEntities_NotOthers` asserts zero publishes for non-removed entities.

## Self-Check

Files exist:
- FOUND: orchestrator/Argus.Orchestrator/Config/LiveEntitiesConfig.cs
- FOUND: orchestrator/Argus.Orchestrator.Tests/LiveEntitiesConfigTests.cs
- FOUND: orchestrator/Argus.Orchestrator.Tests/MqttRetractionTests.cs

Commits exist: 0270b39, 0d26d2f, 583258e, 4739077 — all present in git log above.
