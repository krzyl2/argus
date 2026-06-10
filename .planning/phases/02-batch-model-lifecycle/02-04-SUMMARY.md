---
phase: 02-batch-model-lifecycle
plan: "04"
subsystem: orchestrator
tags: [batch, grpc, influxdb, backgroundservice, periodictimer, tdd]
dependency_graph:
  requires:
    - phase: 02-01
      provides: proto ScoreBatch/Fit RPCs, DetectionGateway
    - phase: 02-02
      provides: InfluxDbReader, ConnectionSettings batch fields, LogEvents 5xxx
  provides:
    - BatchSchedulerWorker (PeriodicTimer batch loop + nightly fit)
    - IInfluxDataSource (testability interface over InfluxDbReader)
    - IBatchDetectorClient (testability interface over DetectorServiceClient)
    - BatchDetectorClientAdapter (production gRPC wrapper)
  affects: [Program.cs, BatchSchedulerWorker, any plan reading 02-04 context]
tech_stack:
  added: []
  patterns:
    - PeriodicTimer-based BackgroundService with per-entity try/catch isolation
    - Test-seam interfaces (IInfluxDataSource, IBatchDetectorClient) for concrete gRPC/InfluxDB types
    - Production gateway-aware constructor alongside test constructor for INFRA-07 gate
key_files:
  created:
    - orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs
    - orchestrator/Argus.Orchestrator/Batch/IInfluxDataSource.cs
    - orchestrator/Argus.Orchestrator/Batch/IBatchDetectorClient.cs
    - orchestrator/Argus.Orchestrator/Batch/BatchDetectorClientAdapter.cs
    - orchestrator/Argus.Orchestrator.Tests/BatchSchedulerWorkerTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Batch/InfluxDbReader.cs
    - orchestrator/Argus.Orchestrator/Program.cs
key_decisions:
  - "IInfluxDataSource and IBatchDetectorClient extracted as test seams — concrete types (InfluxDbReader, DetectorServiceClient) cannot be faked without interfaces since test project has no mocking library"
  - "BatchSchedulerWorker has two constructors: 6-arg (tests, no gateway gate) and 7-arg (production, includes DetectionGateway for INFRA-07 health gate)"
  - "Program.cs uses factory AddHostedService to invoke the 7-arg production constructor explicitly"
  - "google.protobuf.DoubleValue fields (Score on Verdict, Value on Point) are generated as double? in C# by Grpc.Tools — not as DoubleValue wrapper classes"
  - "Nightly fit calls FitAsync only; Python Fit RPC saves model internally (no separate SaveModel call in orchestrator per plan interface note)"
  - "Only the last verdict per entity/detector is published (most recent window point)"
requirements_completed: [BTCH-01, BTCH-02, BTCH-03, BTCH-04, FAULT-03]
duration: ~15min
completed: "2026-06-10"
---

# Phase 2 Plan 04: BatchSchedulerWorker Summary

**BatchSchedulerWorker BackgroundService with PeriodicTimer batch scoring and nightly FitAsync, backed by IInfluxDataSource + IBatchDetectorClient test seams wired into Program.cs DI**

## Performance

- **Duration:** ~15 min
- **Started:** 2026-06-10T00:00:00Z
- **Completed:** 2026-06-10
- **Tasks:** 2 (Task 1 TDD: RED + GREEN; Task 2: Program.cs wiring)
- **Files modified:** 7

## Accomplishments

- BatchSchedulerWorker: PeriodicTimer loop, per-entity/detector ScoreBatch dispatch, last-verdict publish, nightly FitAsync with _fitRunToday flag, INFRA-07 health gate
- IInfluxDataSource + IBatchDetectorClient interfaces extracted for testability without mocking library
- 5 unit tests pass: skip-on-empty, publishes verdicts, per-entity exception isolation (2 entities, both throw, both caught, worker continues), nightly fit flag suppression
- Program.cs: all adapters registered, BatchSchedulerWorker added as hosted service with production gateway

## Task Commits

1. **Task 1 (RED): Failing tests for BatchSchedulerWorker** - `61b1757` (test)
2. **Task 1 (GREEN): Implement BatchSchedulerWorker + interfaces** - `e9c86e7` (feat)
3. **Task 2: Register in Program.cs** - `9c994d3` (feat)

## Files Created/Modified

- `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` — PeriodicTimer BackgroundService, RunBatchAsync, RunNightlyFitAsync, SimulateNightlyFitTicksAsync test helper
- `orchestrator/Argus.Orchestrator/Batch/IInfluxDataSource.cs` — QueryAsync abstraction over InfluxDbReader
- `orchestrator/Argus.Orchestrator/Batch/IBatchDetectorClient.cs` — ScoreBatchAsync + FitAsync abstraction over DetectorServiceClient
- `orchestrator/Argus.Orchestrator/Batch/BatchDetectorClientAdapter.cs` — production wrapper around DetectionGateway
- `orchestrator/Argus.Orchestrator/Batch/InfluxDbReader.cs` — added `: IInfluxDataSource`
- `orchestrator/Argus.Orchestrator/Program.cs` — IInfluxDataSource alias, IBatchDetectorClient singleton, AddHostedService<BatchSchedulerWorker> factory
- `orchestrator/Argus.Orchestrator.Tests/BatchSchedulerWorkerTests.cs` — 5 tests

## Decisions Made

- **IInfluxDataSource / IBatchDetectorClient interfaces:** test project has no mocking library; concrete `InfluxDbReader` and `DetectorService.DetectorServiceClient` cannot be faked without abstraction layers. Two thin interfaces added — minimal scope, no production behavior change.
- **Two-constructor pattern:** 6-arg constructor for tests (no gateway, gate skipped); 7-arg for production (gateway present, INFRA-07 gate runs). Program.cs uses factory to select the 7-arg path.
- **double? not DoubleValue:** Grpc.Tools 2.80.0 generates `google.protobuf.DoubleValue` proto fields as `double?` in C# — the plan's interface documentation was written assuming `DoubleValue` wrapper objects. All usages corrected to `double?` (Score, Point.Value).
- **Last verdict only:** Per plan action spec — publish the last verdict in ScoreBatchResponse (most recent window point) not all verdicts.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] google.protobuf.DoubleValue generates as double? not DoubleValue**
- **Found during:** Task 1 GREEN (build failure)
- **Issue:** Plan interface docs showed `new DoubleValue { Value = val }` for Point.Value and `last.Score?.Value` for Verdict.Score. Grpc.Tools 2.80.0 generates these as `double?` in C#, not wrapper classes.
- **Fix:** Used `val` directly for `Point.Value = val` and `last.Score ?? 0.0` for publish. Removed `DoubleValue` constructor calls.
- **Files modified:** BatchSchedulerWorker.cs, BatchSchedulerWorkerTests.cs
- **Verification:** `dotnet build` exits 0, 0 warnings
- **Committed in:** e9c86e7

**2. [Rule 2 - Missing Critical] Extracted IInfluxDataSource and IBatchDetectorClient**
- **Found during:** Task 1 RED (test design)
- **Issue:** Plan note said "if DetectorClient is not mockable, create an IBatchDetectorClient interface." The test project has no mocking library (xunit only). `InfluxDbReader` is also concrete. Both need interfaces for hand-written fakes.
- **Fix:** Created `IInfluxDataSource` (1 method), `IBatchDetectorClient` (2 methods), `BatchDetectorClientAdapter` (production wrapper). `InfluxDbReader` implements `IInfluxDataSource`.
- **Files modified:** IInfluxDataSource.cs, IBatchDetectorClient.cs, BatchDetectorClientAdapter.cs, InfluxDbReader.cs
- **Verification:** Tests compile and pass without live gRPC or InfluxDB
- **Committed in:** e9c86e7

---

**Total deviations:** 2 auto-fixed (1 bug, 1 missing critical)
**Impact on plan:** Both necessary — type generation is a runtime fact, interfaces are correctness requirements for testability given the test project's constraints.

## Issues Encountered

- `ScoreStreamPipelineTests.RunAsync_CompleteAsyncCalledBeforeReadTaskAwaited` appeared as a failure on first test run but passed on subsequent runs — intermittent timing-sensitive test, pre-existing, not introduced by this plan.
- Pre-existing: `DiscoveryPayloadTests.BinarySensorPayload_AvailabilityTopicIsBridgeLevel` and `BinarySensorPayload_PayloadAvailableOnline` — 2 failures unchanged from 02-02.

## Known Stubs

None — BatchSchedulerWorker dispatches real gRPC and publishes real MQTT; no placeholder data in production path.

## Threat Flags

None — no new network endpoints or auth paths. All threat model items from plan are addressed:
- T-02-04-01: PeriodicTimer.WaitForNextTickAsync skips accumulated ticks — no burst
- T-02-04-02: CancellationToken propagated through QueryAsync and ScoreBatchAsync
- T-02-04-04: Per-entity try/catch in RunBatchAsync — worker never crashes from single entity

## Self-Check: PASSED

- FOUND: orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs
- FOUND: orchestrator/Argus.Orchestrator/Batch/IInfluxDataSource.cs
- FOUND: orchestrator/Argus.Orchestrator/Batch/IBatchDetectorClient.cs
- FOUND: orchestrator/Argus.Orchestrator/Batch/BatchDetectorClientAdapter.cs
- FOUND: orchestrator/Argus.Orchestrator.Tests/BatchSchedulerWorkerTests.cs
- FOUND: commit 61b1757 (RED tests)
- FOUND: commit e9c86e7 (GREEN implementation)
- FOUND: commit 9c994d3 (Task 2 Program.cs)
- dotnet build: 0 errors, 0 warnings
- dotnet test: 70 pass, 2 pre-existing failures (DiscoveryPayload)

## Next Phase Readiness

- BatchSchedulerWorker is live as a hosted service; requires running detector (Python) and InfluxDB to function end-to-end
- Plan 02-05 (Fit/SaveModel servicer in Python) can proceed — FitAsync on the orchestrator side is already wired
- IInfluxDataSource and IBatchDetectorClient are available for reuse in any future batch-related plans

---
*Phase: 02-batch-model-lifecycle*
*Completed: 2026-06-10*
