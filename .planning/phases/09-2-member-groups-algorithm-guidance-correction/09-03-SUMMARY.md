---
phase: 09-2-member-groups-algorithm-guidance-correction
plan: 03
subsystem: batch
tags: [csharp, dotnet, mqtt, grpc, group-detection, peer-divergence]

# Dependency graph
requires:
  - phase: 09-2-member-groups-algorithm-guidance-correction
    provides: "09-01 lowered the config-validation member floor to 2 for both modes; 09-02 (parallel wave) added the Python pairwise-delta path for 2-member peer_divergence groups"
provides:
  - "Count-aware BuildGroupMatrix staleness policy — 2-member peer_divergence groups use skip-whole-group-on-any-staleness instead of the unreachable drop-stale-then-require-3-fresh floor"
  - "Response-shape-aware publish routing in RunGroupBatchAsync (keys on response.PerMember.Count / response.GroupVerdict, not Mode string)"
  - "RunNightlyFitAsync no longer unconditionally skips peer_divergence groups — RunGroupFitAsync runs for every group"
  - "DiscoveryPublisher.UsesPerMemberEntities(group) — single count-aware entity-shape helper reused by MqttPublisherWorker's retract branch"
affects: [09-02-summary-verification, phase-9-completion, group-detection-runtime]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Mode-string-only branch -> count-aware / response-shape-aware branch (applied uniformly across BatchSchedulerWorker, DiscoveryPublisher, MqttPublisherWorker)"

key-files:
  created: []
  modified:
    - orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs
    - orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs
    - orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs
    - orchestrator/Argus.Orchestrator.Tests/GroupBatchSchedulerTests.cs
    - orchestrator/Argus.Orchestrator.Tests/DiscoveryPayloadTests.cs

key-decisions:
  - "Gated BOTH BuildGroupMatrix staleness guards on group.Members.Count >= 3, not just the first one — the plan text claimed the second guard (isPeer && activeMembers.Count < PeerMinFreshMembers) becomes 'unreachable' for 2-member groups once the first guard catches them, but that is only true when staleness exists; a fully-fresh 2-member peer group has activeMembers.Count == 2 (< PeerMinFreshMembers == 3) unconditionally, so without also gating the second guard a healthy 2-member group would be skipped every tick forever, contradicting the plan's own must_have. Fixed as Rule 1 (bug)."
  - "UsesPerMemberEntities made internal static on DiscoveryPublisher (not duplicated) so MqttPublisherWorker's retract branch reuses the same source of truth, per the plan's preferred option."

requirements-completed: [GRP-11, GRP-12]

# Metrics
duration: 8min
completed: 2026-07-03
status: complete
---

# Phase 9 Plan 03: C# Orchestrator Wiring for 2-Member Peer Groups Summary

**Count-aware BatchSchedulerWorker staleness/publish/nightly-fit branches plus a shared `DiscoveryPublisher.UsesPerMemberEntities` helper so 2-member peer_divergence groups get fitted, scored, published, and retracted as a single group-level relationship check instead of silently misbehaving.**

## Performance

- **Duration:** ~8 min
- **Tasks:** 2 completed
- **Files modified:** 5 (3 source, 2 test)

## Accomplishments

- `BuildGroupMatrix` now routes any peer group with fewer than 3 members into the same
  skip-whole-group-on-any-staleness policy joint mode already uses, and the pre-existing
  drop-stale-then-require-3-fresh floor check is scoped to `Members.Count >= 3` so it can never
  fire spuriously for a healthy 2-member group.
- `RunGroupBatchAsync`'s publish branch now keys on `response.PerMember.Count > 0` /
  `response.GroupVerdict != null` instead of the `isPeer` Mode-string flag, so a 2-member
  peer_divergence group's `GroupVerdict`-only response correctly falls into the existing
  group-level publish path.
- `RunNightlyFitAsync`'s unconditional `peer_divergence -> continue` skip is removed; `RunGroupFitAsync`
  now runs for every group, letting Python's `FitGroup` (Plan 09-02) decide fit semantics by
  member count.
- New `DiscoveryPublisher.UsesPerMemberEntities(GroupConfig)` (`internal static`,
  `IsPeerDivergence(group) && group.Members.Count >= 3`) replaces `IsPeerDivergence` at all 3
  entity-shape call sites (`BuildGroupBinarySensorConfig`, `BuildGroupSensorConfig`,
  `PublishGroupAsync`'s memberIds ternary) and is reused by `MqttPublisherWorker`'s retract branch
  — single source of truth, no duplicated logic.
- A 2-member peer_divergence group now produces exactly one group-level `binary_sensor` + `sensor`
  pair (memberId=null) and is correctly retracted as a unit on membership change; classic N>=3
  peer_divergence and joint-mode behavior is unchanged (verified by existing + new tests).

## Task Commits

Each task was committed atomically:

1. **Task 1: Make BatchSchedulerWorker count-aware and response-shape-aware (Pitfalls 1, 2, 5)** - `186ac45` (feat)
2. **Task 2: Add UsesPerMemberEntities count-aware helper for MQTT entity shape + retract (Pitfalls 3, 4)** - `d8e73e3` (feat)

**Plan metadata:** committed with this SUMMARY.md (worktree mode — orchestrator finalizes after merge)

## Files Created/Modified

- `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` - BuildGroupMatrix staleness gating (both guards), response-shape publish branch, removed nightly-fit peer_divergence skip
- `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs` - new `UsesPerMemberEntities` helper, applied at 3 entity-shape call sites
- `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs` - retract branch reuses `DiscoveryPublisher.UsesPerMemberEntities`
- `orchestrator/Argus.Orchestrator.Tests/GroupBatchSchedulerTests.cs` - renamed nightly-fit test to reflect both modes now fit (2 calls, not 1); added 2-member peer staleness-skip and group-level-publish tests
- `orchestrator/Argus.Orchestrator.Tests/DiscoveryPayloadTests.cs` - added 2-member group-level entity-shape tests and an N>=3 per-member regression test

## Decisions Made

- Gated both `BuildGroupMatrix` staleness guards (not just the first) on `group.Members.Count >= 3` — see Deviations below.
- `UsesPerMemberEntities` kept `internal static` on `DiscoveryPublisher` per the plan's preferred single-source-of-truth option, rather than duplicating the helper in `MqttPublisherWorker`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Second BuildGroupMatrix staleness guard also needed count-gating**
- **Found during:** Task 1 (BatchSchedulerWorker count-aware edits) — the new
  `TwoMemberPeerGroup_GroupVerdictResponse_PublishesOneScoreAndFlagWithNullMemberId` test failed
  with `ScoreGroupCallCount == 0` even though the fixture had zero stale members.
- **Issue:** The plan's literal instruction said to leave `isPeer && activeMembers.Count <
  PeerMinFreshMembers` (the second guard) unchanged, claiming it becomes "unreachable" for
  2-member peer groups once the first guard (now `(!isPeer || Members.Count < 3) &&
  staleMembers.Count > 0`) catches them. That's only true when staleness actually exists — a
  fully-fresh 2-member peer group has `staleMembers.Count == 0` (first guard doesn't fire) but
  `activeMembers.Count == 2` (== `Members.Count`, since nothing was dropped), which is always
  `< PeerMinFreshMembers (3)`. The second guard would therefore always skip a healthy 2-member
  peer group, directly contradicting the plan's own must_have ("2-member peer group... uses the
  skip-whole-group-on-any-staleness policy," implying it scores when there's no staleness).
- **Fix:** Added `group.Members.Count >= 3 &&` to the second guard's condition so it only applies
  to the N>=3 floor case it was originally designed for, mirroring the same gating already applied
  to the first guard.
- **Files modified:** `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs`
- **Verification:** All 21 `GroupBatchSchedulerTests` pass, including the new 2-member
  zero-staleness scoring test and the pre-existing N=4 below-floor-skip test.
- **Committed in:** `186ac45` (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 bug)
**Impact on plan:** Necessary correctness fix — without it, GRP-11's core deliverable (a healthy
2-member peer_divergence group actually scoring) would not work. No scope creep; the fix is a
one-line condition tightening within the same guard the plan already targeted.

## Issues Encountered

None beyond the deviation above.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Both C# and Python (09-02) halves of the 2-member peer_divergence pairwise-delta feature are
  now wired end-to-end: fit runs, scoring uses the correct staleness policy, publish/retract use
  the correct entity shape.
- Full orchestrator test suite (377 tests) passes; solution builds clean with 0 warnings/errors.
- No known blockers for Phase 9 completion; orchestrator (parent agent) should confirm 09-02's
  Python-side pairwise-delta implementation is compatible with this plan's response-shape
  assumptions (`GroupVerdict` set, `PerMember` empty, `Contributions` empty for the 2-member case)
  when merging both waves.

## Self-Check: PASSED

- FOUND: orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs
- FOUND: orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs
- FOUND: orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs
- FOUND: orchestrator/Argus.Orchestrator.Tests/GroupBatchSchedulerTests.cs
- FOUND: orchestrator/Argus.Orchestrator.Tests/DiscoveryPayloadTests.cs
- FOUND: commit 186ac45
- FOUND: commit d8e73e3

---
*Phase: 09-2-member-groups-algorithm-guidance-correction*
*Completed: 2026-07-03*
