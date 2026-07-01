---
phase: 02-live-sensor-discovery-entity-selection-ui
plan: "01"
subsystem: ha-sensor-registry
tags: [sensor-discovery, registry, websocket, thread-safety, unit-tests]
dependency_graph:
  requires: []
  provides: [IHaSensorRegistry, HaSensorEntry, HaStateDto-5arg]
  affects: [NetDaemonHaEventSource, HaWebSocketClient, Program.cs]
tech_stack:
  added: []
  patterns: [volatile-immutable-reference-swap, invariant-culture-double-tryparse, tdd]
key_files:
  created:
    - orchestrator/Argus.Orchestrator/Ha/IHaSensorRegistry.cs
    - orchestrator/Argus.Orchestrator/Ha/HaSensorRegistry.cs
    - orchestrator/Argus.Orchestrator.Tests/HaSensorRegistryTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Ha/HaWebSocketClient.cs
    - orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs
    - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
    - orchestrator/Argus.Orchestrator/Program.cs
decisions:
  - "HaStateDto changed from internal to public — required because IHaSensorRegistry (public) exposes UpdateSnapshot(IReadOnlyList<HaStateDto>, ...) and C# accessibility rules disallow a public method parameter with an internal type"
  - "Volatile immutable-list reference swap (no locks) for thread safety — mirrors ArgusHealthSignals pattern; single writer + many readers, no torn reads"
  - "UpdateSnapshot called on BOTH first connect and reconnect after GetStatesAsync — ensures registry is always fresh; no second WebSocket (ADR-4)"
metrics:
  duration: "~4 minutes"
  completed: 2026-07-01
  tasks_completed: 3
  files_changed: 7
---

# Phase 02 Plan 01: Sensor Registry Backend — Summary

**One-liner:** Thread-safe `IHaSensorRegistry` volatile-snapshot singleton fed from the existing `get_states` call, with `HaStateDto` extended to carry `unit_of_measurement` and `friendly_name` from HA attributes.

## What Was Built

- **Extended `HaStateDto`** from 3 to 5 positional parameters by appending nullable `UnitOfMeasurement` and `FriendlyName`. Both construction sites in `HaWebSocketClient` updated to parse the HA `attributes` object using `JsonElement.TryGetProperty` (missing attributes → null, no throw).

- **`IHaSensorRegistry`** — public interface with `GetAll()`, `GetFiltered(string q)`, and `UpdateSnapshot(IReadOnlyList<HaStateDto>, HashSet<string>)`. Also defines `HaSensorEntry` record (EntityId, CurrentValue, UnitOfMeasurement, FriendlyName, IsTracked).

- **`HaSensorRegistry`** — implementation using `private volatile IReadOnlyList<HaSensorEntry> _snapshot`. Single volatile assignment after filtering + ordering. No locks, no torn reads. Filters numeric states with invariant-culture `double.TryParse` (same rule as `SelectDiscoverableSensors`). Entries ordered by EntityId OrdinalIgnoreCase. IsTracked computed from `trackedEntityIds.Contains(entityId)`.

- **`LogEvents.SensorRegistryUpdated = new(7001, ...)`** — Phase 2 UI log event in the 7xxx range.

- **`HaSensorRegistryTests`** — 12 unit tests covering numeric filter, non-numeric exclusion, GetFiltered (empty/matching/case-insensitive/no-match), IsTracked (true/false), ordering, and concurrent UpdateSnapshot+GetAll thread-safety.

- **Wiring in `NetDaemonHaEventSource`** — new `IHaSensorRegistry registry` ctor parameter with null-guard; `_sensorRegistry.UpdateSnapshot(states, _configuredEntities)` called immediately after `GetStatesAsync` (before first-connect/reconnect branch). Runs on BOTH first connect and every reconnect.

- **DI in `Program.cs`** — `builder.Services.AddSingleton<IHaSensorRegistry, HaSensorRegistry>()` registered before `IHaEventSource`.

## Task Commits

| Task | Description | Commit |
|------|-------------|--------|
| 1 | Extend HaStateDto (3→5 args), fix both construction sites | c3ae66b |
| 2 | IHaSensorRegistry + HaSensorRegistry + LogEvents 7001 + 12 tests | 521c487 |
| 3 | Wire registry into NetDaemonHaEventSource ctor + DI registration | 248c31a |

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] `HaStateDto` visibility changed from `internal` to `public`**
- **Found during:** Task 2 (first test run)
- **Issue:** C# accessibility rules require that a public method's parameter types are at least as accessible as the method. `IHaSensorRegistry.UpdateSnapshot` is public and takes `IReadOnlyList<HaStateDto>` — but `HaStateDto` was `internal`. Compiler error CS0051.
- **Fix:** Changed `internal sealed record HaStateDto` to `public sealed record HaStateDto`. The record is defined in `HaWebSocketClient.cs` (which itself remains `internal sealed class`), so the public `HaStateDto` type is only practically accessible within the assembly via `HaWebSocketClient`'s methods.
- **Files modified:** `orchestrator/Argus.Orchestrator/Ha/HaWebSocketClient.cs`
- **Commit:** 521c487

## Known Stubs

None. All data wiring is functional: `HaStateDto` carries real attributes parsed from HA JSON; `HaSensorRegistry` holds a real snapshot populated from `GetStatesAsync`.

## Threat Surface Scan

No new network endpoints introduced. `HaStateDto` attributes parsing uses `JsonElement.TryGetProperty` + `GetString()` only (T-02-01 mitigated). Registry uses volatile reference swap with no lock contention (T-02-02 mitigated). No secrets in the registry (T-02-03 accepted per plan).

## Pre-existing Flaky Test (Out of Scope)

`ScoreStreamPipelineTests.RunAsync_CompleteAsyncCalledBeforeReadTaskAwaited` occasionally fails when the full test suite runs in parallel (timing-sensitive ordering assertion). Passes every time in isolation. Not introduced by this plan — pre-existing issue in `ScoreStreamPipeline.cs`. Logged in deferred-items for the team.

## Self-Check: PASSED

Files exist:
- FOUND: orchestrator/Argus.Orchestrator/Ha/IHaSensorRegistry.cs
- FOUND: orchestrator/Argus.Orchestrator/Ha/HaSensorRegistry.cs
- FOUND: orchestrator/Argus.Orchestrator.Tests/HaSensorRegistryTests.cs

Commits exist: c3ae66b, 521c487, 248c31a (verified via git log)

Build: 0 errors, 0 warnings. All 12 HaSensorRegistryTests pass. Full suite: 130 pass (1 pre-existing flaky test on parallel run, passes in isolation).
