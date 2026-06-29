---
phase: 01-foundations-streaming
plan: "08"
subsystem: detection-pipeline
tags: [grpc, bidi-stream, hysteresis, frozen-sensor, mqtt, pitfall-3, pitfall-8]
dependency_graph:
  requires: ["01-05", "01-06", "01-07"]
  provides: ["end-to-end scoring pipeline", "frozen detection", "hysteresis gate", "RES-01 unavailable"]
  affects: ["HaListenerWorker", "ScoreStreamPipeline", "Program.cs"]
tech_stack:
  added: ["IScoreStreamCall abstraction", "ScoreStreamPipeline", "EntityRuntimeState", "HysteresisGate", "FrozenSensorDetector"]
  patterns: ["bidi gRPC streaming", "CompleteAsync-before-readTask (PITFALL 3)", "per-entity runtime state", "warm-up suppression (PITFALL 8)"]
key_files:
  created:
    - orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs
    - orchestrator/Argus.Orchestrator/Detection/IScoreStreamCall.cs
    - orchestrator/Argus.Orchestrator/Detection/HysteresisGate.cs
    - orchestrator/Argus.Orchestrator/Detection/FrozenSensorDetector.cs
    - orchestrator/Argus.Orchestrator/Detection/EntityRuntimeState.cs
    - orchestrator/Argus.Orchestrator.Tests/HysteresisGateTests.cs
    - orchestrator/Argus.Orchestrator.Tests/FrozenSensorDetectorTests.cs
    - orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/Argus.Orchestrator/Mqtt/IStatePublisher.cs
    - orchestrator/Argus.Orchestrator/Mqtt/StatePublisher.cs
decisions:
  - "One bidi stream per entity (D-04 isolation) — simpler failure isolation vs multiplexed; each RpcException only affects one entity"
  - "ScoreStreamPipeline has a test constructor (no DetectionGateway) + IScoreStreamCall abstraction for unit testing without live gRPC"
  - "FakeStatePublisher implements IStatePublisher directly (StatePublisher is sealed — cannot be subclassed)"
  - "Intermediate channel buffer removed from HaListenerWorker — haEventSource.ReadAllAsync passed directly to RunAsync"
metrics:
  duration: "~30 min"
  completed: "2026-06-10"
  tasks_completed: 3
  files_created: 8
  files_modified: 4
---

# Phase 1 Plan 08: ScoreStreamPipeline (bidi loop, hysteresis, frozen, MQTT) Summary

End-to-end scoring pipeline: HA state_changed → bidi gRPC ScoreStream → hysteresis gate + frozen detection → MQTT binary_sensor + score entities, with PITFALL 3 deadlock prevention and RES-01 availability degradation.

## What Was Built

### Task 1 (committed prior): HysteresisGate + FrozenSensorDetector + EntityRuntimeState
- `HysteresisGate`: N-consecutive logic preventing flapping (D-11 defaults: high=0.7, low=0.3, min_consecutive=3)
- `FrozenSensorDetector`: rolling variance window — IsFrozen when variance < 0.001 over last 10 readings (D-12)
- `EntityRuntimeState`: per-entity aggregate (Hysteresis + FrozenDetector + warm-up counter + last flag)

### Task 2: ScoreStreamPipeline
- `IScoreStreamCall`: abstraction for bidi duplex calls enabling test injection
- `ScoreStreamPipeline.RunAsync(IScoreStreamCall, ...)`: bidi loop with PITFALL 3 ordering — `CompleteAsync()` called before `await readTask` (never reversed)
- `ProcessVerdictAsync`: score always published; binary_sensor flag only when `!SuppressBinarySensor && WarmedUp` (PITFALL 8/D-07)
- `PublishFrozenAsync`: frozen sensor → binary_sensor ON + availability online (FAULT-02)
- `HandleDetectorFailureAsync`: RpcException → `PublishAvailabilityAsync(offline)` per entity (RES-01)
- Per-verdict structured log with `latency_ms` (OBS-01/STRM-04)
- `LiveScoreStreamCall`: production adapter wrapping `AsyncDuplexStreamingCall<Point,Verdict>`

### Task 3: Wiring
- `HaListenerWorker`: injects `ScoreStreamPipeline`, calls `RunAsync(_haEventSource.ReadAllAsync(stoppingToken), stoppingToken)` after `WaitForHealthyAsync`
- `Program.cs`: registers `ScoreStreamPipeline` singleton; `IStatePublisher` resolves from existing `StatePublisher` singleton

## Test Results

All 62 tests pass (`dotnet test` exits 0):
- 6 ScoreStreamPipelineTests: bidi ordering, warm-up suppression, cooldown suppression, frozen flag, RpcException offline
- 12 HysteresisGateTests + FrozenSensorDetectorTests (committed prior)
- All prior tests (discovery payload, entities config, mTLS channel factory) still green

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 (RED) | `90a64f0` | test(01-08): failing tests HysteresisGate + FrozenSensorDetector |
| 1 (GREEN) | `65377db` | feat(01-08): implement HysteresisGate, FrozenSensorDetector, EntityRuntimeState |
| 2 (WIP merge) | `967e2ed` | chore: merge partial worktree (IStatePublisher + test skeleton) |
| 2 (GREEN) | `5926401` | feat(01-08): ScoreStreamPipeline bidi loop with PITFALL 3 ordering |
| 3 | `3c0f980` | feat(01-08): wire ScoreStreamPipeline into HaListenerWorker |

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] FakeStatePublisher could not extend sealed StatePublisher**
- **Found during:** Task 2 test compilation
- **Issue:** Test file inherited `FakeStatePublisher : StatePublisher` but `StatePublisher` is `sealed`
- **Fix:** Changed `FakeStatePublisher : IStatePublisher` — direct interface implementation
- **Files modified:** `ScoreStreamPipelineTests.cs`
- **Commit:** `5926401`

**2. [Rule 1 - Bug] DoubleValue.ForNumber does not exist in C# protobuf mapping**
- **Found during:** Task 2 compilation
- **Issue:** Test and pipeline used `Google.Protobuf.WellKnownTypes.DoubleValue.ForNumber(v)` but the C# codegen maps `google.protobuf.DoubleValue` to `double?` (nullable double), not a wrapper object
- **Fix:** Replaced with direct `double?` assignment: `Score = score` and `Value = reading.Value`
- **Files modified:** `ScoreStreamPipeline.cs`, `ScoreStreamPipelineTests.cs`
- **Commit:** `5926401`

**3. [Rule 2 - Missing] IStatePublisher not registered in DI container**
- **Found during:** Task 3 wiring
- **Issue:** `ScoreStreamPipeline` constructor takes `IStatePublisher` but only `StatePublisher` (concrete) was registered
- **Fix:** Added `builder.Services.AddSingleton<IStatePublisher>(sp => sp.GetRequiredService<StatePublisher>())` to `Program.cs`
- **Files modified:** `Program.cs`
- **Commit:** `3c0f980`

## Deploy Note

To run end-to-end: `docker compose up` both hosts (orchestrator + detector). Integration against real HA is blocked on Q1 (placeholder entity_ids in `entities.yaml`) — entity_ids must be filled in before first live test. mTLS certs use placeholder values (from plan 03) — regenerate with real GPU host IP/hostname before deployment.

## Known Stubs

None — all functional paths are wired. No hardcoded empty returns or placeholders that block the plan's goal.

## Threat Flags

No new security surface beyond the plan's threat model. All T-08-xx mitigations implemented:
- T-08-01: CompleteAsync before readTask (ordering test asserts it)
- T-08-02: RpcException → availability offline (RES-01)
- T-08-03: Hysteresis min_consecutive + warm-up suppression
- T-08-04: No buffering on hot path; O(1) per-verdict work
- T-08-05: Per-verdict structured log with latency_ms (OBS-01)

## Self-Check: PASSED

Files exist:
- orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs — FOUND
- orchestrator/Argus.Orchestrator/Detection/IScoreStreamCall.cs — FOUND
- orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs — FOUND (contains ScoreStreamPipeline, RunAsync, WaitForHealthyAsync)

Commits exist:
- 5926401 — FOUND (ScoreStreamPipeline)
- 3c0f980 — FOUND (wire into HaListenerWorker)

Tests: 62/62 pass, dotnet build 0 errors.
