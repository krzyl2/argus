---
phase: 06-batch-group-pipeline
plan: 03
subsystem: mqtt
tags: [mqtt, home-assistant, discovery, mqttnet, group-detection]

# Dependency graph
requires:
  - phase: 06-01
    provides: GroupConfig model (GroupId, FriendlyName, Members, Mode, Detector, Params)
  - phase: 06-02
    provides: GroupInfluxReader + ScoreGroupBatchAsync/FitGroupAsync gRPC client methods
provides:
  - UniqueId.GroupFlagId/GroupScoreId (mode-branching unique_id formula)
  - StatePublisher.GroupFlagTopic/GroupScoreTopic + PublishGroupFlagAsync/PublishGroupScoreAsync (argus/group/... namespace)
  - DiscoveryPublisher.BuildGroupBinarySensorConfig/BuildGroupSensorConfig (shared-device discovery payloads)
  - DiscoveryPublisher.PublishGroupAsync/RetractGroupAsync (publish current members / retract removed-only)
affects: [06-04]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Group MQTT topics live in a distinct argus/group/{slug}/... namespace, never colliding with per-entity argus/{slug}/..."
    - "One HA device per group_id: device.identifiers = argus_group_{slug} shared across every member pair, never per-member"
    - "Retraction is precise: caller passes only removed members (oldMembers.Except(newMembers)); survivors untouched"

key-files:
  created: []
  modified:
    - orchestrator/Argus.Orchestrator/Mqtt/UniqueId.cs
    - orchestrator/Argus.Orchestrator/Mqtt/StatePublisher.cs
    - orchestrator/Argus.Orchestrator/Mqtt/IStatePublisher.cs
    - orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs
    - orchestrator/Argus.Orchestrator.Tests/MqttRetractionTests.cs
    - orchestrator/Argus.Orchestrator.Tests/BatchSchedulerWorkerTests.cs
    - orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs

key-decisions:
  - "Group discovery peer/joint branching determined by string.Equals(group.Mode, \"peer_divergence\", OrdinalIgnoreCase), mirroring 06-01's dispatch convention"
  - "RetractGroupAsync takes IEnumerable<string?> removedMembers — a single null entry retracts the joint group-level pair, non-null entries retract specific peer members"
  - "PublishGroupAsync iterates group.Members for peer mode or a single [null] for joint mode — one code path serving both layouts"

requirements-completed: [GRP-08]

# Metrics
duration: 3min
completed: 2026-07-02
status: complete
---

# Phase 6 Plan 3: MQTT Group Entity Layer Summary

**Group MQTT discovery/state layer: peer-divergence emits per-member binary_sensor+score pairs, joint emits one group-level pair, all sharing a single HA device per group_id, with removed-members-only retraction.**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-07-02T13:41:42Z
- **Completed:** 2026-07-02T13:44:11Z
- **Tasks:** 3
- **Files modified:** 7

## Accomplishments
- `UniqueId.GroupFlagId`/`GroupScoreId` implement the locked unique_id scheme (`argus_group_{slug}_flag|score` joint, `argus_group_{slug}_{memberSlug}_flag|score` peer)
- `StatePublisher` gained group topic helpers and publish methods in the distinct `argus/group/...` namespace, added to `IStatePublisher`
- `DiscoveryPublisher.BuildGroupBinarySensorConfig`/`BuildGroupSensorConfig` produce mode-branching discovery payloads, all sharing one `device.identifiers = argus_group_{slug}` per group
- `PublishGroupAsync`/`RetractGroupAsync` (both with MqttConnection + testable-delegate overloads) publish current members and retract only removed members
- Extended `MqttRetractionTests` with 6 new group cases (peer shrink 4→3, joint whole-group removal, empty-payload/retain-true invariants, empty-list no-op)

## Task Commits

Each task was committed atomically:

1. **Task 1: UniqueId group helpers + StatePublisher group topics/publishers** - `7e107ff` (feat)
2. **Task 2: DiscoveryPublisher group builders + removed-members-only retraction** - `9e5f97b` (feat)
3. **Task 3: MqttRetractionTests — group membership-change retraction** - `02a879d` (test)

## Files Created/Modified
- `orchestrator/Argus.Orchestrator/Mqtt/UniqueId.cs` - Added GroupFlagId/GroupScoreId
- `orchestrator/Argus.Orchestrator/Mqtt/StatePublisher.cs` - Added GroupFlagTopic/GroupScoreTopic + PublishGroupFlagAsync/PublishGroupScoreAsync
- `orchestrator/Argus.Orchestrator/Mqtt/IStatePublisher.cs` - Extended interface with group publish methods
- `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs` - Added BuildGroupBinarySensorConfig/BuildGroupSensorConfig/PublishGroupAsync/RetractGroupAsync
- `orchestrator/Argus.Orchestrator.Tests/MqttRetractionTests.cs` - Added group retraction test cases + MakePeerGroup/MakeJointGroup helpers
- `orchestrator/Argus.Orchestrator.Tests/BatchSchedulerWorkerTests.cs` - FakeStatePublisher implements new interface members (compile fix)
- `orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs` - FakeStatePublisher implements new interface members (compile fix)

## Decisions Made
- Peer vs joint dispatch via `string.Equals(group.Mode, "peer_divergence", OrdinalIgnoreCase)` — matches the existing server-side dispatch convention from Phase 5/06-01, no new mode enum
- `RetractGroupAsync` signature takes `IEnumerable<string?> removedMembers` rather than separate peer/joint methods — a single `null` entry retracts the joint group pair, keeping one code path for both layouts
- Group discovery availability list uses only the bridge-level topic (no per-member/per-group availability topic) — group entities don't have an individual liveness signal the way per-entity ones do; simplification kept in scope, not a plan deviation

## Deviations from Plan

None - plan executed exactly as written. The two test-fake compile fixes (`BatchSchedulerWorkerTests.cs`, `ScoreStreamPipelineTests.cs`) were required because both files predate this plan and implement `IStatePublisher`; extending the interface in Task 1 required adding the two new methods to each fake to keep the test project compiling (Rule 3 — blocking issue, not listed in the plan's `files_modified` but directly caused by Task 1's interface change).

## Issues Encountered
None.

## Next Phase Readiness
- Plan 06-04 can now call `PublishGroupAsync`/`RetractGroupAsync` and the `StatePublisher` group publish methods from the batch scheduling loop to surface scored group verdicts in HA
- `dotnet build` clean across both projects; full test suite green (298/298, including the 14 in MqttRetractionTests)

---
*Phase: 06-batch-group-pipeline*
*Completed: 2026-07-02*

## Self-Check: PASSED

All created/modified files and task commit hashes verified present in the repository.
