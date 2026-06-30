---
phase: 03-process-supervision-runtime-integration
plan: 03
subsystem: orchestrator
tags: [health-entity, mqtt-discovery, startup-log, grpc-health]
status: complete

requires:
  - 03-02  # MqttConnection, Program.cs, LogEvents.cs changes committed

provides:
  - HEALTH-01 composite health binary_sensor (argus_addon_health)
  - UICFG-05 startup discovered-sensors log (SelectDiscoverableSensors)

affects:
  - orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs
  - orchestrator/Argus.Orchestrator/Mqtt/HealthEvaluator.cs
  - orchestrator/Argus.Orchestrator/Health/ArgusHealthSignals.cs
  - orchestrator/Argus.Orchestrator/Workers/HealthPublisherWorker.cs
  - orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs
  - orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs
  - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
  - orchestrator/Argus.Orchestrator/Program.cs
  - orchestrator/Argus.Orchestrator/Detection/DetectionGateway.cs

tech-stack:
  added: []
  patterns:
    - pure-static-evaluator
    - volatile-shared-signal
    - fake-inject-delegate-testing

key-files:
  created:
    - orchestrator/Argus.Orchestrator/Mqtt/HealthEvaluator.cs
    - orchestrator/Argus.Orchestrator/Health/ArgusHealthSignals.cs
    - orchestrator/Argus.Orchestrator/Workers/HealthPublisherWorker.cs
    - orchestrator/Argus.Orchestrator.Tests/HealthEntityTests.cs
    - orchestrator/Argus.Orchestrator.Tests/StartupSensorLogTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs
    - orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs
    - orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs
    - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/Argus.Orchestrator/Detection/DetectionGateway.cs

decisions:
  - "Health discovery payload uses single availability_topic (bridge only) not the per-entity array — health entity's own availability is just the bridge"
  - "Grpc.Health.V1 aliased as GrpcHealth to avoid ambiguity with new Argus.Orchestrator.Health namespace"
  - "HealthPublisherWorker.ExecuteHealthCycleAsync is internal static with delegate injection for testability (mirrors fake-credential-source pattern)"
  - "SelectDiscoverableSensors accepts IEnumerable<(string EntityId, string? State)> tuples for offline unit-testing without NetDaemon types"

metrics:
  duration: ~35 minutes
  completed: 2026-06-30
  tasks_completed: 3
  tasks_total: 3
  files_created: 5
  files_modified: 6
  tests_added: 27
  tests_passing: 27
---

# Phase 03 Plan 03: Health Entity + Startup Sensor Log Summary

Composite Argus health `binary_sensor` (HEALTH-01) and startup discovered-sensor log (UICFG-05) implemented and unit-tested; orchestrator builds clean with 0 errors.

## What Was Built

### Task 1 — HealthEvaluator + health discovery payload (commit fcd1b6e)

`HealthEvaluator.Evaluate(bool, bool, bool)` returns "OFF" only when all three signals are true (detector SERVING AND HA connected AND MQTT connected); any false → "ON" (problem). Pure static function with no I/O.

`DiscoveryPublisher.BuildHealthBinarySensorConfig()` produces a retained MQTT discovery payload for a `binary_sensor` named "Argus — status" (D8), `device_class: problem`, `unique_id == object_id == argus_addon_health` (D-14), `state_topic: argus/addon/health/state`, grouped under an Argus device with `identifiers: ["argus_addon"]`. Single `availability_topic` (bridge only — no per-entity availability for the add-on's own health).

Three public constants on `DiscoveryPublisher`: `HealthObjectId`, `HealthStateTopic`, `HealthDiscoveryTopic`.

14 unit tests: evaluator truth table (5 cases) + discovery payload JSON assertions (9 properties).

### Task 2 — ArgusHealthSignals + HealthPublisherWorker + DI (commit e5e719c)

`ArgusHealthSignals` singleton: `volatile bool HaConnected` shared between `NetDaemonHaEventSource` (writer) and `HealthPublisherWorker` (reader). `MqttConnection.IsConnected` added as a one-liner property (`_client.IsConnected`).

`NetDaemonHaEventSource` injected with `ArgusHealthSignals`: sets `HaConnected = true` immediately after successful ConnectAsync (before reconnect/backoff logic), clears it in the catch block. No changes to the reconnect/backoff or TryMap.

`HealthPublisherWorker` (BackgroundService): polls `mqtt.IsConnected` until connected, publishes discovery config once (retained), then loops every 15 seconds evaluating composite health via `HealthEvaluator.Evaluate` and publishing to `argus/addon/health/state`. Detector health is checked with a 5-second gRPC deadline; any exception (deadline exceeded, transport failure) treated as not-SERVING (T-03-08 mitigation).

`LogEvents` additions: `DiscoveredSensorsLogged (3003)`, `HealthEntityPublished (6001)`.

`Program.cs`: `ArgusHealthSignals` registered as singleton before `IHaEventSource`; `HealthPublisherWorker` registered as hosted service next to `MqttPublisherWorker`.

`DetectionGateway.cs`: aliased `using GrpcHealth = Grpc.Health.V1` to eliminate ambiguity with the new `Argus.Orchestrator.Health` namespace.

6 fake-inject tests for `HealthPublisherWorker.ExecuteHealthCycleAsync` (internal static method with delegate injection): all-healthy → OFF; detector not serving → ON; HA not connected → ON; MQTT not connected → ON; detector throws → ON (treated as not-SERVING); correct topic published.

### Task 3 — SelectDiscoverableSensors + startup log (commit bec3f97)

`NetDaemonHaEventSource.SelectDiscoverableSensors(IEnumerable<(string EntityId, string? State)>, HashSet<string>)` pure static helper: returns unconfigured numeric sensors (double-parseable via InvariantCulture, entity_id not in configured set) with their parsed values.

On first successful HA connect only (guarded by `isFirstConnection` flag), `LogDiscoverableSensorsAsync` calls `GetStatesAsync`, projects to tuples, runs `SelectDiscoverableSensors`, logs one INFO line per sensor and a total-count line. The reconnect path (D-07 snapshot + cooldown) is unchanged.

7 unit tests: numeric unconfigured included; configured excluded; non-numeric excluded; mixed input filtered correctly; empty input; negative values; case-insensitive exclusion.

## Deviations from Plan

**1. [Rule 1 - Bug] DetectionGateway namespace collision**
- **Found during:** Task 2 build
- **Issue:** Adding `Argus.Orchestrator.Health` namespace caused `Health.HealthClient` in `DetectionGateway.cs` to resolve as `Argus.Orchestrator.Health.HealthClient` instead of `Grpc.Health.V1.Health.HealthClient`
- **Fix:** Aliased `using GrpcHealth = Grpc.Health.V1` in `DetectionGateway.cs` and `HealthPublisherWorker.cs`; updated all `Health.*` references to `GrpcHealth.*`
- **Files modified:** `Detection/DetectionGateway.cs`
- **Commit:** e5e719c

## Human-Verify Items (live HA OS required)

- **HEALTH-01 live check:** After orchestrator startup with a live HA + MQTT, confirm "Argus — status" `binary_sensor` appears in HA via MQTT discovery, reads OFF when healthy; stopping the detector service should flip it to ON within ~15 seconds.
- **UICFG-05 live check:** On first startup, add-on log should list unconfigured numeric HA sensors (entity_id = value) and a total count before detection begins.

## Verification Results

```
dotnet test --filter "FullyQualifiedName~HealthEntity|FullyQualifiedName~StartupSensorLog"
PASS: 27/27

dotnet build orchestrator/Argus.Orchestrator.sln
Warnings: 0, Errors: 0

dotnet test orchestrator/Argus.Orchestrator.sln
PASS: 110/112 (2 pre-existing DiscoveryPayloadTests failures — known v1 issue, not regressed)
```

## Known Stubs

None. All wired to real implementations.

## Self-Check: PASSED
