---
phase: 02-batch-model-lifecycle
plan: "02"
subsystem: orchestrator
tags: [influxdb, batch, config, di]
dependency_graph:
  requires: [02-01]
  provides: [InfluxDbReader, ConnectionSettings-influx, LogEvents-5xxx]
  affects: [Program.cs, BatchSchedulerWorker]
tech_stack:
  added: [InfluxDB.Client 5.0.0]
  patterns: [IInfluxQueryApi-adapter, hand-written-fakes-tdd]
key_files:
  created:
    - orchestrator/Argus.Orchestrator/Batch/InfluxDbReader.cs
    - orchestrator/Argus.Orchestrator/Batch/IInfluxQueryApi.cs
    - orchestrator/Argus.Orchestrator/Batch/InfluxQueryApiAdapter.cs
    - orchestrator/Argus.Orchestrator.Tests/InfluxDbReaderTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Config/ConnectionSettings.cs
    - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj
decisions:
  - "IInfluxQueryApi interface introduced to decouple InfluxDbReader from concrete QueryApi for testability without mocking library"
  - "InfluxQueryApiAdapter wraps GetQueryApi() per-call per RESEARCH.md recommendation"
  - "InfluxDB.Client 5.0.0 added to csproj (was absent despite plan stating it existed from Phase 1)"
metrics:
  duration: "~10 minutes"
  completed: "2026-06-10"
  tasks_completed: 3
  files_created: 4
  files_modified: 4
---

# Phase 2 Plan 02: Batch Config + InfluxDbReader Foundations Summary

**One-liner:** InfluxDB rolling-window reader with IInfluxQueryApi abstraction, 8 new ConnectionSettings fields, and 11 batch log event IDs wired into Program.cs DI.

## Tasks Completed

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 | Extend ConnectionSettings and LogEvents | 2c07760 | ConnectionSettings.cs, LogEvents.cs, Argus.Orchestrator.csproj |
| 2 (RED) | Failing tests for InfluxDbReader | 829e3b9 | InfluxDbReaderTests.cs |
| 2 (GREEN) | Implement InfluxDbReader + IInfluxQueryApi | bbd7b29 | InfluxDbReader.cs, IInfluxQueryApi.cs, InfluxQueryApiAdapter.cs |
| 3 | Wire into Program.cs | 22b210f | Program.cs |

## Verification

- ConnectionSettings.cs: 8 new properties present (InfluxUrl, InfluxToken, InfluxOrg, InfluxBucket, InfluxMeasurement, InfluxValueField, BatchIntervalMinutes, NightlyFitHour) + env var comments
- LogEvents.cs: 5001-5011 event IDs present (BatchSchedulerStarted through ModelVersionMismatch)
- InfluxDbReader.cs: in Argus.Orchestrator.Batch namespace; QueryAsync returns IReadOnlyList<(DateTime, double)>; uses Convert.ToDouble; guards null/empty InfluxUrl and null InfluxBucket with LogEvents.BatchEntityNoData
- Program.cs: all 8 env vars bound; InfluxDBClient singleton; InfluxDbReader singleton
- dotnet build: exits 0, 0 warnings
- dotnet test: 65 pass, 2 pre-existing DiscoveryPayloadTests failures (not introduced by this plan)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Added InfluxDB.Client 5.0.0 to csproj**
- **Found during:** Task 1
- **Issue:** Plan stated InfluxDB.Client 5.0.0 was "already in csproj from Phase 1" but the package was absent from `Argus.Orchestrator.csproj`
- **Fix:** Added `<PackageReference Include="InfluxDB.Client" Version="5.0.0" />` to csproj
- **Files modified:** `orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj`
- **Commit:** 2c07760

**2. [Rule 2 - Missing critical functionality] Introduced IInfluxQueryApi interface**
- **Found during:** Task 2
- **Issue:** Plan noted "if mocking InfluxDBClient requires a wrapper interface, create a minimal IInfluxQueryApi interface." The test project has no mocking library (only Xunit); InfluxDB.Client's `QueryApi` is a concrete class with no interface that can be instantiated without HTTP. Without an abstraction layer, testing would require a live InfluxDB.
- **Fix:** Created `IInfluxQueryApi` interface + `InfluxQueryApiAdapter` production wrapper. `InfluxDbReader` accepts `IInfluxQueryApi` in testable constructor and `InfluxDBClient` in production constructor (delegates to adapter). Tests use hand-written `EmptyQueryApi` and `ThrowingQueryApi` fakes.
- **Files modified:** Created `IInfluxQueryApi.cs`, `InfluxQueryApiAdapter.cs`; `InfluxDbReader.cs` has two constructors
- **Commit:** bbd7b29

## Known Stubs

None — no stub data, placeholder text, or unwired components in this plan's output.

## Threat Flags

None — no new network endpoints, auth paths, or trust boundary changes introduced beyond what the plan's threat model covers.

## Deferred Items

- `DiscoveryPayloadTests.BinarySensorPayload_AvailabilityTopicIsBridgeLevel` and `BinarySensorPayload_PayloadAvailableOnline` are pre-existing test failures not introduced by this plan. Logged to deferred-items for investigation.

## Self-Check: PASSED

- FOUND: InfluxDbReader.cs
- FOUND: IInfluxQueryApi.cs
- FOUND: InfluxQueryApiAdapter.cs
- FOUND: InfluxDbReaderTests.cs
- FOUND: 02-02-SUMMARY.md
- FOUND: commit 2c07760 (Task 1)
- FOUND: commit 829e3b9 (Task 2 RED)
- FOUND: commit bbd7b29 (Task 2 GREEN)
- FOUND: commit 22b210f (Task 3)
