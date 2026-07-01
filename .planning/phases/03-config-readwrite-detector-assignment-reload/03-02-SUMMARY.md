---
phase: 03-config-readwrite-detector-assignment-reload
plan: "02"
subsystem: orchestrator
tags: [reload, live-config, inner-cts, retraction, mqtt, di-migration]
dependency_graph:
  requires: [03-01]
  provides: [CFG-04-consumers, CFG-04-restart-loop, CFG-04-retraction]
  affects: [Program.cs, HaListenerWorker, ScoreStreamPipeline, BatchSchedulerWorker, MqttPublisherWorker]
tech_stack:
  added: []
  patterns:
    - inner-CTS restart loop (ConfigChanged → cancel inner CTS → re-enter RunAsync)
    - null-before-Dispose (Pitfall 3 guard for CTS disposal race)
    - virtual seam pattern (WaitForDetectorHealthAsync / RunPipelineAsync / RetractPublishAsync)
    - MakeLive() test helper (wraps EntitiesConfig in LiveEntitiesConfig for ctor injection)
key_files:
  modified:
    - orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs
    - orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs
    - orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs
    - orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/Argus.Orchestrator.Tests/BatchSchedulerWorkerTests.cs
    - orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs
  created:
    - orchestrator/Argus.Orchestrator.Tests/HaListenerWorkerReloadTests.cs
decisions:
  - "HaListenerWorker uses virtual seams (WaitForDetectorHealthAsync, RunPipelineAsync, RetractPublishAsync) to enable unit testing of the restart loop without a live gRPC channel or MQTT broker"
  - "TestableHaListenerWorker subclass (in tests) overrides all three virtual seams — keeps ScoreStreamPipeline sealed, no interface extraction needed"
  - "Protected ctor allows null gateway/pipeline for test subclasses (gateway null is safe when WaitForDetectorHealthAsync is overridden)"
  - "MqttPublisherWorker stores stoppingToken as field; ConfigChanged handler fire-and-forgets via Task.Run with catch-and-log — matches research Q1/Q2 resolution"
  - "RetractAndRepublishAsync uses RetractPublishAsync delegate overload of DiscoveryPublisher so retraction is testable without live MqttConnection"
metrics:
  duration: "9m10s"
  completed: "2026-07-01"
  tasks: 3
  files_changed: 8
---

# Phase 03 Plan 02: Consumer Migration + HaListenerWorker Restart Loop Summary

**One-liner:** Migrated all three EntitiesConfig consumers to ILiveEntitiesConfig and replaced HaListenerWorker's one-shot ExecuteAsync with an inner-CTS restart loop that reloads the streaming pipeline on ConfigChanged, retracts removed entities from MQTT, and republishes discovery for added ones (CFG-04 hot-reload mechanism).

## Tasks Completed

| Task | Name | Commit | Files Changed |
|------|------|--------|---------------|
| 1 | Migrate ScoreStreamPipeline + BatchSchedulerWorker + MqttPublisherWorker | 98442c5 | 5 |
| 2 | HaListenerWorker inner-CTS restart loop + retraction tests | b2b7d31 | 2 |
| 3 | Program.cs DI registration swap + consumer rewiring + GET /sensors live read | fa7795f | 1 |

## What Was Built

### Task 1: Three-Consumer Migration (98442c5)

**ScoreStreamPipeline:**
- Both production and test constructors now accept `ILiveEntitiesConfig liveConfig` instead of `EntitiesConfig entitiesConfig`
- `BuildEntityStates()` reads `_liveConfig.Get().Entities` at `RunAsync` entry (not ctor-captured — Pitfall 2)

**BatchSchedulerWorker:**
- `private readonly ILiveEntitiesConfig _liveConfig` replaces `_entities`
- Both ctors updated; `RunBatchAsync` and `RunNightlyFitAsync` read `_liveConfig.Get().Entities` per cycle (CFG-04)

**MqttPublisherWorker:**
- Migrated to `ILiveEntitiesConfig _liveConfig`
- Subscribes `ConfigChanged` before the keep-alive delay; on event fires `Task.Run` that republishes discovery + `online` availability for all current entities (Pitfall 4 / Research Q2/Q8)
- Stores `stoppingToken` as field for use in the fire-and-forget handler

**Tests:**
- `BatchSchedulerWorkerTests` + `ScoreStreamPipelineTests`: all ctors updated to use `MakeLive()` wrapper
- New test `RunBatchAsync_AfterSwap_UsesNewEntitySet` proves per-cycle live read

### Task 2: HaListenerWorker Restart Loop (b2b7d31)

**Inner-CTS loop structure:**
- Subscribe `ConfigChanged` once before loop; unsubscribe in `finally`
- Handler: `var cts = innerCts; if (cts is not null && !cts.IsCancellationRequested) cts.Cancel();` (Pitfall 1)
- Each iteration: read `_liveConfig.Get()`, run `RetractAndRepublishAsync` if `lastConfig != null`, create new linked `innerCts`, call `RunPipelineAsync`
- Catch: `OperationCanceledException when (innerCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)` → re-loop (T-03-05)
- Finally: `var toDispose = innerCts; innerCts = null; toDispose?.Dispose()` (Pitfall 3 / T-03-08)

**Retraction diff (RetractAndRepublishAsync):**
- Computes removed = old entities not in new (OrdinalIgnoreCase)
- Calls `DiscoveryPublisher.RetractAsync(delegate, removed, stoppingToken)` (uses stoppingToken — not innerCts — so broker calls survive the pipeline cancel)
- Computes added = new entities not in old; calls `DiscoveryPublisher.PublishAllAsync(delegate, added, stoppingToken)` (T-03-07)

**Virtual seams:**
- `WaitForDetectorHealthAsync(ct)` → delegates to `_gateway.WaitForHealthyAsync(ct)` in production; overridden in tests to pass through immediately
- `RunPipelineAsync(readings, ct)` → delegates to `_scoreStreamPipeline.RunAsync(...)` in production; overridden in tests with recording runner
- `RetractPublishAsync(topic, payload, retain, ct)` → delegates to `_mqtt.PublishAsync(...)` in production; overridden in tests with capture delegate

**HaListenerWorkerReloadTests (new file):**
- `ConfigChanged_RestartsRunAsync`: proves second RunAsync invocation after swap
- `StoppingToken_ExitsLoop`: proves exactly 1 RunAsync call when host shuts down
- `ConfigChanged_RetractsRemovedEntities`: proves empty retained payloads for removed entity topics
- `RapidConfigChanged_NoObjectDisposedException`: two concurrent Swaps do not throw ObjectDisposedException

### Task 3: Program.cs DI Migration (fa7795f)

- `AddSingleton(entitiesConfig)` → `var liveConfig = new LiveEntitiesConfig(entitiesConfig); builder.Services.AddSingleton<ILiveEntitiesConfig>(liveConfig);`
- `BatchSchedulerWorker` factory: `GetRequiredService<EntitiesConfig>()` → `GetRequiredService<ILiveEntitiesConfig>()`
- `GET /sensors`: `EntitiesConfig config` parameter → `ILiveEntitiesConfig liveCfg`; passes `liveCfg.Get()` to `BuildFullPage`
- `HaListenerWorker` registration unchanged (`AddHostedService<HaListenerWorker>()`) — DI auto-resolves new constructor including `MqttConnection`
- No remaining `GetRequiredService<EntitiesConfig>` or `AddSingleton(entitiesConfig)` calls

## Verification Results

```
dotnet build orchestrator/Argus.Orchestrator.sln → 0 errors, 0 warnings
dotnet test orchestrator/Argus.Orchestrator.sln  → 182/182 passed
```

Grep confirms no remaining `GetRequiredService<EntitiesConfig>` or raw `AddSingleton(entitiesConfig)`.

## Deviations from Plan

### Auto-decisions (not deviations — design choices made during implementation)

**1. Protected ctor for test seams instead of interface extraction**
- **Found during:** Task 2 (HaListenerWorker testability)
- **Issue:** `ScoreStreamPipeline` is sealed and `DetectionGateway` has no virtual `WaitForHealthyAsync`, making it impossible to subclass either for test injection
- **Fix:** Added a protected ctor to `HaListenerWorker` accepting nullable `DetectionGateway?` and `ScoreStreamPipeline?`, plus three virtual seam methods (`WaitForDetectorHealthAsync`, `RunPipelineAsync`, `RetractPublishAsync`). `TestableHaListenerWorker` (in tests) overrides all three.
- **Rule:** Rule 3 (blocking issue — tests could not compile without this)
- **Files modified:** `HaListenerWorker.cs`, `HaListenerWorkerReloadTests.cs`

**2. RetractAndRepublishAsync uses DiscoveryPublisher delegate overload**
- The plan referenced `DiscoveryPublisher.RetractAsync(MqttConnection, ...)` but since `RetractPublishAsync` is virtual, using the delegate overload `RetractAsync(Func<...>, ...)` was cleaner and kept retraction testable without a live `MqttConnection`.
- This is within plan intent — same result, better test coverage.

**None** - Plan executed as specified. All correctness properties encoded (null-before-dispose, OCE guard, retraction diff, per-cycle live reads, MakeLive test helper).

## Known Stubs

None. All live-config reads are fully wired. The `GET /sensors` handler passes `liveCfg.Get()` to `BuildFullPage`, so the page always reflects the current entity set.

## Threat Flags

No new network endpoints, auth paths, or schema changes introduced beyond what is in the plan's threat model. T-03-05, T-03-06, T-03-07, T-03-08, T-03-09 are all mitigated as designed.

## Self-Check

Files exist:
- orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs ✓
- orchestrator/Argus.Orchestrator.Tests/HaListenerWorkerReloadTests.cs ✓
- orchestrator/Argus.Orchestrator/Program.cs ✓

Commits exist (git log):
- 98442c5 Task 1 ✓
- b2b7d31 Task 2 ✓
- fa7795f Task 3 ✓
