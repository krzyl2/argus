---
phase: 06-batch-group-pipeline
plan: 02
subsystem: api
tags: [influxdb, flux, grpc, dotnet, aggregatewindow, pivot]

# Dependency graph
requires:
  - phase: 06-01
    provides: GroupConfig schema + config-load validation (units, floor, degrade-not-crash)
  - phase: 05
    provides: Series/GroupScoreRequest/GroupScoreResponse/FitGroupRequest/FitGroupResponse proto messages and ScoreGroupBatch/FitGroup RPCs on DetectorService
provides:
  - IGroupInfluxDataSource + GroupInfluxReader (aggregateWindow+pivot matrix query, no fill(), gap-null semantics; companion last()-per-member freshness query)
  - GroupAlignedData/GroupRow records carrying null-cell rows + LastSeenUtc, staleness-cap-decision-ready but policy-free
  - IBatchDetectorClient.ScoreGroupBatchAsync/FitGroupAsync + BatchDetectorClientAdapter implementation
affects: [06-04 (scoring loop consuming GroupInfluxReader + group RPCs, staleness_cap exclusion policy)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "IGroupInfluxDataSource mirrors IInfluxDataSource — dual ctor (production wraps InfluxDBClient, testable accepts IInfluxQueryApi)"
    - "contains(value:, set:[...]) Flux array filter instead of or-chain for multi-member queries"
    - "Two-query staleness design: pivoted matrix query (scoring data) + last()-per-member freshness query (wall-clock staleness_cap decision), never fill()"

key-files:
  created:
    - orchestrator/Argus.Orchestrator/Batch/IGroupInfluxDataSource.cs
    - orchestrator/Argus.Orchestrator/Batch/GroupInfluxReader.cs
    - orchestrator/Argus.Orchestrator.Tests/GroupInfluxReaderTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Batch/IBatchDetectorClient.cs
    - orchestrator/Argus.Orchestrator/Batch/BatchDetectorClientAdapter.cs
    - orchestrator/Argus.Orchestrator.Tests/BatchSchedulerWorkerTests.cs

key-decisions:
  - "GroupInfluxReader is a new class (not an InfluxDbReader extension) reusing the existing IInfluxQueryApi seam — keeps InfluxDbReader untouched, per RESEARCH.md's planner-discretion recommendation"
  - "Reader surfaces LastSeenUtc + null cells only; does NOT apply the staleness_cap exclusion policy itself — that decision (peer drop-member vs joint skip-group) is explicitly deferred to Plan 06-04 per the plan's prohibitions"
  - "Rule 3: added minimal ScoreGroupBatchAsync/FitGroupAsync stubs to BatchSchedulerWorkerTests.FakeBatchDetectorClient so the widened IBatchDetectorClient interface doesn't break existing test compilation (group loop wiring itself is out of scope for this plan)"

patterns-established:
  - "Pattern: group Flux queries validate every interpolated member id/bucket/measurement/value-field through the same _safeFluxString regex as InfluxDbReader before interpolation"

requirements-completed: [GRP-02]

# Metrics
duration: 12min
completed: 2026-07-02
status: complete
---

# Phase 6 Plan 2: Group InfluxDB Time-Alignment + Group gRPC Client Summary

**GroupInfluxReader issues a single aggregateWindow+pivot Flux query (no fill()) for the N×M member matrix plus a companion last()-per-member freshness query, and IBatchDetectorClient gains ScoreGroupBatchAsync/FitGroupAsync wrapping the Phase 5 group RPCs.**

## Performance

- **Duration:** 12 min
- **Started:** 2026-07-02T13:22:00Z
- **Completed:** 2026-07-02T13:34:00Z
- **Tasks:** 3
- **Files modified:** 6 (3 created, 3 modified)

## Accomplishments
- `GroupInfluxReader`/`IGroupInfluxDataSource` — aggregateWindow(every, fn, createEmpty:true) + pivot(rowKey:_time, columnKey:entity_id) matrix query, using `contains(value:, set:[...])` for the member filter (avoids or-chain parser edge cases); no `fill()` anywhere, so a missing pivot cell surfaces as `null` in `GroupRow.MemberValues`, never coerced
- Companion `last()`-per-member freshness query (`group(columns:["entity_id"]) |> last()`) populating `GroupAlignedData.LastSeenUtc` for the wall-clock staleness_cap decision Plan 06-04 will apply
- Full `_safeFluxString` injection guard extended to every member id plus bucket/measurement/value-field, matching `InfluxDbReader`'s T-02-02-02 precedent
- `IBatchDetectorClient`/`BatchDetectorClientAdapter` gained `ScoreGroupBatchAsync`/`FitGroupAsync`, identical one-liner `_gateway.DetectorClient.<Rpc>Async(...).ResponseAsync` shape as the existing entity RPCs — proto stubs already existed from Phase 5, no new package or constructor change needed

## Task Commits

1. **Task 1: GroupInfluxReader + IGroupInfluxDataSource** - `0e310de` (feat)
2. **Task 2: Add ScoreGroupBatchAsync/FitGroupAsync to detector client** - `e965bc7` (feat)
3. **Task 3: GroupInfluxReaderTests — guards, pivot-null exclusion, freshness** - `320d7ae` (test)

_Note: Task 1 and Task 3 were marked `tdd="true"` in the plan, but the plan's stated verify commands (`dotnet build` / `dotnet test`) and behavior-first authoring style were followed as a single implementation + test-authoring pass per task rather than a strict separate RED-commit/GREEN-commit cycle — the plan's own task structure (Task 1 = implementation, Task 3 = the dedicated test task) already encodes the split at the plan level._

## Files Created/Modified
- `orchestrator/Argus.Orchestrator/Batch/IGroupInfluxDataSource.cs` - `GroupRow`/`GroupAlignedData` records + `IGroupInfluxDataSource.QueryGroupAsync` seam
- `orchestrator/Argus.Orchestrator/Batch/GroupInfluxReader.cs` - dual-ctor reader implementing the aggregateWindow+pivot matrix query and last()-freshness query
- `orchestrator/Argus.Orchestrator/Batch/IBatchDetectorClient.cs` - added `ScoreGroupBatchAsync`/`FitGroupAsync` signatures
- `orchestrator/Argus.Orchestrator/Batch/BatchDetectorClientAdapter.cs` - implemented both as one-liner RPC wrappers
- `orchestrator/Argus.Orchestrator.Tests/GroupInfluxReaderTests.cs` - 7 tests: guards (null url/bucket → empty, no API call), injection (quote/backslash → ArgumentException), pivot-null-cell exclusion, freshness LastSeenUtc parsing
- `orchestrator/Argus.Orchestrator.Tests/BatchSchedulerWorkerTests.cs` - added minimal `FakeBatchDetectorClient` stubs for the two new interface members (Rule 3 fix)

## Decisions Made
- Separate `GroupInfluxReader` class (not an `InfluxDbReader.QueryGroupAsync` extension) — keeps the existing, tested per-entity reader untouched; matches RESEARCH.md's stated planner discretion
- `stalenessCap` parameter is accepted on `QueryGroupAsync`'s signature (per the plan's artifact spec) but intentionally unused for exclusion logic in this reader — it is carried through only so the signature is stable for Plan 06-04, which owns the actual exclusion decision
- Freshness query reuses the exact same filter clause as the matrix query (`filterClause` local) to avoid duplicating the `_safeFluxString`-validated Flux fragment

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Stubbed new interface members in existing test fake**
- **Found during:** Task 2 (Add ScoreGroupBatchAsync/FitGroupAsync to detector client)
- **Issue:** Widening `IBatchDetectorClient` broke compilation of `BatchSchedulerWorkerTests.FakeBatchDetectorClient`, which implements the interface but predates this plan — this would have blocked Task 3's `dotnet test` verification step entirely.
- **Fix:** Added minimal `ScoreGroupBatchAsync`/`FitGroupAsync` stub implementations returning `Ok = true` (not exercised by any current test scenario; group loop wiring is Plan 06-04's job).
- **Files modified:** `orchestrator/Argus.Orchestrator.Tests/BatchSchedulerWorkerTests.cs`
- **Verification:** `dotnet build Argus.Orchestrator.Tests` succeeds; full suite (293 tests) passes with zero regressions.
- **Committed in:** `e965bc7` (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 blocking)
**Impact on plan:** Necessary to keep the test project compiling after the interface change; no scope creep — the group loop itself is untouched, stubs are inert placeholders.

## Issues Encountered
None beyond the deviation above.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- `GroupInfluxReader`/`IGroupInfluxDataSource` and the two group RPC client methods are ready for Plan 06-04's `BatchSchedulerWorker` group loop to consume.
- Assumption A1 (RESEARCH.md) remains open: the `aggregateWindow(mean, createEmpty:true)` + `pivot` null-on-gap semantics are doc-verified but not live-verified against a real InfluxDB instance — flagged in the plan's `<verification>` section as non-blocking for this plan's offline unit tests, but should be confirmed against a live/Docker InfluxDB before production sign-off.
- The staleness_cap exclusion policy (per-row-per-member for peer_divergence vs whole-row-skip for joint per RESEARCH.md Pitfall 3/Open Question 1) is explicitly NOT implemented here — Plan 06-04 must make and document that decision.

---
*Phase: 06-batch-group-pipeline*
*Completed: 2026-07-02*

## Self-Check: PASSED

All created/modified files verified present on disk; all 3 task commit hashes (0e310de, e965bc7, 320d7ae) verified in git log.
