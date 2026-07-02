---
phase: 06-batch-group-pipeline
plan: 04
subsystem: batch
tags: [batch-scheduler, mqtt-discovery, influxdb, grpc, staleness-policy, dotnet]

# Dependency graph
requires:
  - phase: 06-01
    provides: GroupConfig schema + EntitiesConfig.Groups + ValidateGroups (floor, mode, unit checks)
  - phase: 06-02
    provides: IGroupInfluxDataSource/GroupInfluxReader (aligned matrix + LastSeenUtc), ScoreGroupBatchAsync/FitGroupAsync on IBatchDetectorClient
  - phase: 06-03
    provides: IStatePublisher group publish methods, DiscoveryPublisher.PublishGroupAsync/RetractGroupAsync
provides:
  - "BatchSchedulerWorker group scoring loop (RunGroupBatchAsync) appended to RunBatchAsync, reading _liveConfig.Get().Groups fresh per cycle"
  - "Staleness-cap boundary policy (BuildGroupMatrix): joint skips whole group on any stale member; peer drops stale members with a re-checked min-3 floor; null-cell rows excluded for a rectangular matrix"
  - "Joint-only nightly fit (RunGroupFitAsync) — peer_divergence groups skipped before the try (stateless)"
  - "MqttPublisherWorker group discovery publish on start + ConfigChanged, with removed-members-first retraction via a stored _lastGroups snapshot"
  - "Program.cs DI: GroupInfluxReader/IGroupInfluxDataSource registered inside the InfluxUrl-configured block, threaded into BatchSchedulerWorker"
affects: []

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Group loop mirrors the entity loop's exact fault-isolation shape (per-item try/catch, OperationCanceledException rethrow) — added after, never interleaved with, the entity loop"
    - "Staleness policy centralized in a single BuildGroupMatrix helper shared by both RunGroupBatchAsync and RunGroupFitAsync (isPeer flag branches behavior)"
    - "MqttPublisherWorker stores a _lastGroups snapshot updated only at the end of each publish pass, used as the diff basis for removed-members-first retraction"

key-files:
  created:
    - orchestrator/Argus.Orchestrator.Tests/GroupBatchSchedulerTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs
    - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
    - orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/Argus.Orchestrator.Tests/BatchSchedulerWorkerTests.cs

key-decisions:
  - "BuildGroupMatrix is a single shared static helper for both the score path and the fit path — joint fit reuses the exact same skip-whole-group-on-stale-member policy as joint scoring (isPeer: false in both call sites)"
  - "Default staleness_cap of 30 minutes applied when a group's Params omits the key or the value fails TimeSpan.TryParse — degrades safely rather than throwing at scoring time"
  - "GroupScoreResponse.Contributions carried through and logged at info level (top contributor only) for joint groups — no MQTT publish this phase, per GRP-09 being Phase 8 scope"
  - "MqttPublisherWorker's ConfigChanged handler orders operations: retract-removed-groups -> republish-entities (existing) -> republish-groups -> update _lastGroups snapshot, matching the plan's locked retraction-before-publish requirement"

patterns-established:
  - "Wall-clock staleness exclusion policy (joint skip-group vs peer drop-member+floor) — reusable if a future phase adds more group-consuming batch paths"

requirements-completed: [GRP-02, GRP-08]

# Metrics
duration: 14min
completed: 2026-07-02
status: complete
---

# Phase 6 Plan 4: Batch Group Pipeline Integration Summary

**Group scoring loop, joint-only nightly fit, and wall-clock staleness-cap boundary policy wired into BatchSchedulerWorker; group MQTT discovery/retraction wired into MqttPublisherWorker; DI wiring in Program.cs completes the end-to-end group anomaly pipeline.**

## Performance

- **Duration:** ~14 min
- **Started:** 2026-07-02T13:48:00Z (approx, per STATE.md session continuation)
- **Completed:** 2026-07-02T14:02:00Z
- **Tasks:** 3 completed
- **Files modified:** 6 (1 created, 5 modified)

## Accomplishments
- `RunGroupBatchAsync` scores every group in `_liveConfig.Get().Groups` per-cycle after the existing entity loop, with identical per-group fault isolation (GRP-02)
- `BuildGroupMatrix` implements the staleness-cap boundary: joint skips the whole group on any stale member (fixed feature-vector safety); peer drops stale members and re-checks the 3-member floor before scoring; null-cell rows are excluded so the matrix passed to the detector stays rectangular
- Peer-divergence responses publish one score+flag pair per member (`GroupScoreResponse.PerMember`); joint responses publish one group-level pair (`GroupScoreResponse.GroupVerdict`) with `null` memberId — mode-branched exactly as GRP-08 requires
- `RunNightlyFitAsync` skips `peer_divergence` groups entirely (stateless detector, no RPC) and calls `FitGroupAsync` only for joint groups via `RunGroupFitAsync`
- `MqttPublisherWorker` publishes group discovery on start and on `ConfigChanged`, retracting only removed members (or whole removed groups) BEFORE republishing the current set, using a stored `_lastGroups` snapshot as the diff basis
- `Program.cs` registers `GroupInfluxReader`/`IGroupInfluxDataSource` inside the existing `InfluxUrl`-configured block, reusing the already-registered `InfluxDBClient` singleton — no second client, no new NuGet package

## Task Commits

Each task was committed atomically:

1. **Task 1: Group scoring loop + joint-only fit + staleness-cap boundary in BatchSchedulerWorker** - `749f460` (feat)
2. **Task 2: Group discovery publish + removed-members-first retraction in MqttPublisherWorker; Program.cs DI** - `1c94efc` (feat)
3. **Task 3: Integration tests — staleness branch, publish layout, nightly fit mode-branch** - `97d43ab` (test)

## Files Created/Modified
- `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` - Added `IGroupInfluxDataSource` to both ctors; group loop in `RunBatchAsync`; `RunGroupBatchAsync`/`RunGroupFitAsync`/`BuildGroupMatrix`/`BuildGroupScoreRequest`/`BuildFitGroupRequest`; joint-only group loop in `RunNightlyFitAsync`
- `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` - Added `GroupScored`/`GroupSkippedStale`/`GroupSchedulerError`/`GroupNoData` (5012–5015)
- `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs` - Added `_lastGroups` snapshot field; group discovery publish in the initial publish sequence; removed-members-first retraction + group republish in `OnConfigChanged`
- `orchestrator/Argus.Orchestrator/Program.cs` - Registered `GroupInfluxReader`/`IGroupInfluxDataSource` inside the `InfluxUrl` block; threaded into the `BatchSchedulerWorker` factory
- `orchestrator/Argus.Orchestrator.Tests/BatchSchedulerWorkerTests.cs` - `FakeStatePublisher` extended with `GroupFlagCalls`/`GroupScoreCalls` recorders; added `FakeGroupInfluxDataSource`; updated all 6 `BatchSchedulerWorker` constructor call sites for the new parameter
- `orchestrator/Argus.Orchestrator.Tests/GroupBatchSchedulerTests.cs` (new) - 7 tests: joint-skip-on-stale, peer-drop-member-with-floor-scoring, peer-below-floor-skip, per-member publish layout, group-level publish layout, nightly-fit mode-branch, per-group fault isolation

## Decisions Made
- `BuildGroupMatrix` is shared between the score path and the fit path (both pass `isPeer: false` for joint fit) rather than duplicating the staleness logic — keeps the exclusion policy in exactly one place
- Default `staleness_cap` of 30 minutes when the group's `Params` omits the key or the value fails to parse as a `TimeSpan` — matches the plan's "fall back to a sane default when absent" instruction without inventing a new config surface
- `GroupScoreResponse.Contributions` (joint mode) logged at info level with the top-ranked contributor only — carried through the RPC response per the plan's note that GRP-09 (HA surfacing) is Phase 8 scope, no MQTT publish added this phase
- MqttPublisherWorker's ConfigChanged handler treats a peer group's partial membership change and a whole-group removal as two distinct retraction paths (`Except` diff vs. all-members-or-single-null retraction) to match `DiscoveryPublisher.RetractGroupAsync`'s existing signature

## Deviations from Plan

None - plan executed exactly as written. Task 2's `dotnet build` verification intentionally only covers the main project (the plan's own acceptance criteria for Task 1 states "Build succeeds (Program.cs DI update lands in Task 2)"); the test project's compile break from the constructor signature change was expected sequencing, resolved in Task 3 as planned.

## Issues Encountered
None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- Phase 6 (Batch Group Pipeline) is now fully wired end-to-end: config (06-01) -> aligned InfluxDB matrix + gRPC client (06-02) -> MQTT group entity layer (06-03) -> live scheduler integration (06-04)
- `dotnet build` clean; full test suite 305/305 passing, zero regressions
- Assumption A1 (06-RESEARCH.md): the `aggregateWindow`+`pivot` null-on-gap semantics remain doc-verified but not live-verified against a real InfluxDB instance — non-blocking for this plan's offline unit tests, flagged for confirmation before production sign-off
- Manual end-to-end verification (peer group -> 3 HA entities under one device; joint group -> 1 HA entity; staleness drop/skip behavior; membership-change retraction) is deferred to live HA bring-up, consistent with the milestone's existing UAT deferral pattern

---
*Phase: 06-batch-group-pipeline*
*Completed: 2026-07-02*

## Self-Check: PASSED

All created/modified files verified present on disk; all 3 task commit hashes (749f460, 1c94efc, 97d43ab) verified present in git log.
