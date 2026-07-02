---
phase: 06-batch-group-pipeline
verified: 2026-07-02T00:00:00Z
status: passed
score: 4/4 must-haves verified
behavior_unverified: 0
overrides_applied: 0
---

# Phase 6: Batch Group Pipeline Verification Report

**Phase Goal:** Operators define a group in config → time-aligned InfluxDB history → scored via Phase 5's detectors → published/retracted as MQTT-discovered HA entities — with unit and membership guards preventing broken groups from silently producing nonsense.
**Verified:** 2026-07-02
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Operator defines a named group in config, stable operator-assigned group_id, no auto-discovery (GRP-01) | VERIFIED | `EntitiesConfig.cs:9,23-42` — `GroupConfig` (GroupId, FriendlyName, Members, Mode, Detector, Params) + `EntitiesConfig.Groups` top-level list, deserialized from `groups:` YAML key. No discovery/inference logic anywhere in the loader — group membership comes only from operator-authored YAML. `EntitiesConfigTests.Load_ValidJointGroup_Survives` passes. |
| 2 | Members' history resampled onto a common grid server-side (aggregateWindow+pivot) before scoring, with a staleness cap so stale forward-filled gaps aren't scored as real data (GRP-02) | VERIFIED | `GroupInfluxReader.cs:107-114` builds `aggregateWindow(every, fn, createEmpty:true) \|> pivot(rowKey:["_time"], columnKey:["entity_id"])` — no `fill()` anywhere in the file (grep confirmed). Null pivot cells stay `null` (line 124). Companion `last()` freshness query populates `LastSeenUtc` (lines 130-145). Staleness cap applied at the scoring boundary in `BatchSchedulerWorker.BuildGroupMatrix` (lines 268-313): JOINT skips whole group on any stale member; PEER drops stale members and re-checks the 3-member floor; null-cell rows excluded so the matrix stays rectangular. Tests `JointGroup_OneMemberStale_ScoreGroupNotCalled`, `PeerGroup_OneMemberStale_ThreeFreshRemain_ScoresOnFreshSubset`, `PeerGroup_MembersStaleBelowFloor_Skipped` all pass. |
| 3 | Group anomaly entities (per-member peer / group-level joint) published via MQTT discovery on creation, retracted on membership change, no orphaning (GRP-08) | VERIFIED | `DiscoveryPublisher.cs:234-352` — `BuildGroupBinarySensorConfig`/`BuildGroupSensorConfig` branch on mode (peer: per-member with memberId; joint: group-level, memberId null); all share one `device.identifiers = argus_group_{slug}` (lines 261, 299). `RetractGroupAsync` (362-396) takes only the removed-members set. `MqttPublisherWorker.cs:87-113` computes `oldGroup.Members.Except(newGroup.Members)` and calls `RetractGroupAsync` BEFORE republishing entities/groups (lines 115-127); whole-group removal retracts all members/the joint pair. CR-01 race condition (two rapid ConfigChanged events racing on `_lastGroups`) fixed with `SemaphoreSlim(1,1)` (`_configChangeGate`, lines 42, 84-136) — read-modify-write of the diff snapshot is now serialized. Tests `RetractGroupAsync_PeerGroupShrink4To3_RetractsOnlyRemovedMemberTwoTopics`, `RetractGroupAsync_PeerGroupShrink4To3_DoesNotTouchSurvivingMembers`, `RetractGroupAsync_WholeJointGroupRemoved_RetractsSingleGroupPair` all pass. |
| 4 | Group with incompatible units (peer) or below min-N floor rejected/degraded safely at config-load, not silently-wrong (GRP-04 config-time guard) | VERIFIED | `EntitiesConfigLoader.ValidateGroups` (lines 73-180): `Members.Count < 3` → skip+warn (lines 111-117); duplicate members → skip+warn (119-129, WR-01 fix); unknown mode → skip+warn (134-140); peer_divergence with 2+ distinct non-null units (registry populated) → skip+warn (163-169); cold-boot (registry null) → skip-check, keep group with info log (156-162, WR-03 fix — message now correctly scoped only to the null-registry case). `ValidateGroups` never throws — confirmed by `Load_GroupBelowFloor_IsPrunedAndWarns_DoesNotThrow`, `Load_PeerDivergenceGroupWithMixedUnits_IsPrunedAndWarns`, `Load_PeerDivergenceGroupWithNullRegistry_IsKept_ColdBootDegrade`, `Load_MixedValidAndInvalidGroups_KeepsOnlyValid` — all pass. |

**Score:** 4/4 truths verified (0 present, behavior-unverified)

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs` | GroupConfig type + EntitiesConfig.Groups list | VERIFIED | Present, substantive, wired (used by loader, scheduler, MQTT layer) |
| `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` | ValidateGroups() skip-and-warn + nullable registry unit check | VERIFIED | Present, substantive; WR-01/WR-03 fixes applied |
| `orchestrator/Argus.Orchestrator/Batch/IGroupInfluxDataSource.cs` + `GroupInfluxReader.cs` | aggregateWindow+pivot matrix + last()-freshness query | VERIFIED | No fill(); _safeFluxString guard extended to \r\n (CR-02) and every/aggFn (WR-04) |
| `orchestrator/Argus.Orchestrator/Batch/IBatchDetectorClient.cs` + `BatchDetectorClientAdapter.cs` | ScoreGroupBatchAsync/FitGroupAsync | VERIFIED | One-liner RPC wrappers, identical shape to existing entity RPCs |
| `orchestrator/Argus.Orchestrator/Mqtt/UniqueId.cs` | GroupFlagId/GroupScoreId | VERIFIED | Mode-branching unique_id formula present |
| `orchestrator/Argus.Orchestrator/Mqtt/StatePublisher.cs` + `IStatePublisher.cs` | GroupFlagTopic/GroupScoreTopic + PublishGroupFlagAsync/PublishGroupScoreAsync | VERIFIED | Distinct argus/group/... namespace confirmed |
| `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs` | BuildGroupBinarySensorConfig/BuildGroupSensorConfig + PublishGroupAsync/RetractGroupAsync | VERIFIED | One shared device per group; removed-members-only retraction |
| `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` | RunGroupBatchAsync/RunGroupFitAsync/BuildGroupMatrix | VERIFIED | Group loop after entity loop; staleness policy; joint-only fit; fault isolation |
| `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs` | Group discovery publish + retraction ordering | VERIFIED | Retract-before-republish confirmed; CR-01 semaphore fix applied |
| `orchestrator/Argus.Orchestrator/Program.cs` | DI registration of GroupInfluxReader/IGroupInfluxDataSource | VERIFIED | Registered inside InfluxUrl block, reuses singleton InfluxDBClient, threaded into BatchSchedulerWorker factory |

### Key Link Verification

| From | To | Via | Status | Details |
|------|-----|-----|--------|---------|
| EntitiesConfigLoader.cs | EntitiesConfig.cs | Load() deserializes Groups, ValidateGroups() prunes in place | WIRED | Confirmed at lines 29-33 |
| EntitiesConfigLoader.cs | IHaSensorRegistry.cs | nullable registry threaded through Load() | WIRED | Confirmed, `registry?.GetAll()` at line 81 |
| GroupInfluxReader.cs | IInfluxQueryApi.cs | reuses existing query-API abstraction | WIRED | Confirmed, dual ctor at lines 33-46 |
| BatchDetectorClientAdapter.cs | proto/argus.proto | ScoreGroupBatchAsync/FitGroupAsync RPC stubs | WIRED | Build succeeds (309/309 tests); proto types resolve |
| BatchSchedulerWorker.cs | IGroupInfluxDataSource.cs | QueryGroupAsync call | WIRED | Line 191 |
| BatchSchedulerWorker.cs | IBatchDetectorClient.cs | ScoreGroupBatchAsync/FitGroupAsync | WIRED | Lines 212, 517 |
| BatchSchedulerWorker.cs | IStatePublisher.cs | PublishGroupFlagAsync/PublishGroupScoreAsync | WIRED | Lines 225-226, 236-237 |
| MqttPublisherWorker.cs | DiscoveryPublisher.cs | PublishGroupAsync + RetractGroupAsync ordering | WIRED | Lines 102-127 — retract before republish, confirmed |

### Behavioral Spot-Checks / Test Execution

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Full orchestrator suite | `dotnet test Argus.Orchestrator.Tests.csproj -c Debug` | 309/309 passed, 0 failed | PASS |
| JOINT skip-whole-group on stale member | `GroupBatchSchedulerTests.JointGroup_OneMemberStale_ScoreGroupNotCalled` | Passed | PASS |
| PEER drop-member, score on fresh subset | `GroupBatchSchedulerTests.PeerGroup_OneMemberStale_ThreeFreshRemain_ScoresOnFreshSubset` | Passed | PASS |
| PEER below min-N floor → skip | `GroupBatchSchedulerTests.PeerGroup_MembersStaleBelowFloor_Skipped` | Passed | PASS |
| Membership-change retraction, removed-only, no orphan | `MqttRetractionTests.RetractGroupAsync_PeerGroupShrink4To3_RetractsOnlyRemovedMemberTwoTopics` + `DoesNotTouchSurvivingMembers` | Passed | PASS |
| Config-load guard: below-floor | `EntitiesConfigTests.Load_GroupBelowFloor_IsPrunedAndWarns_DoesNotThrow` | Passed | PASS |
| Config-load guard: mixed peer units | `EntitiesConfigTests.Load_PeerDivergenceGroupWithMixedUnits_IsPrunedAndWarns` | Passed | PASS |
| Config-load guard: cold-boot degrade | `EntitiesConfigTests.Load_PeerDivergenceGroupWithNullRegistry_IsKept_ColdBootDegrade` | Passed | PASS |
| Peer stateless — no FitGroup in nightly | `GroupBatchSchedulerTests.RunNightlyFit_JointGroupFitCalled_PeerGroupNeverFit` | Passed | PASS |
| CR-02 Flux injection guard (newline/CR/quote) | `GroupInfluxReaderTests.QueryGroupAsync_Unsafe{MemberIdWithNewline,MemberIdWithCarriageReturn,EveryWithNewline,AggFnWithQuote}_ThrowsArgumentException` | 4 tests, all passed | PASS |

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|-------------|-------------|--------|----------|
| GRP-01 | 06-01 | Operator-defined named group, stable group_id, no auto-discovery | SATISFIED | GroupConfig + EntitiesConfig.Groups; no discovery logic present |
| GRP-02 | 06-02, 06-04 | Time-aligned history (aggregateWindow+pivot), staleness cap | SATISFIED | GroupInfluxReader (no fill) + BuildGroupMatrix staleness policy |
| GRP-04 | 06-01 | Min-member floor + safe degrade | SATISFIED | ValidateGroups floor/unit/duplicate checks, never throws |
| GRP-08 | 06-03, 06-04 | MQTT discovery publish/retract, no orphaning | SATISFIED | DiscoveryPublisher group builders + RetractGroupAsync + retract-before-republish ordering |

Note: REQUIREMENTS.md's traceability table lists GRP-04 as "Phase 5" while GRP-01/02/08 are listed as "Phase 6"; the phase's own plan (06-01-PLAN.md) declares and implements GRP-04 (the config-load floor/unit guard), and REQUIREMENTS.md marks it `[x]` Complete regardless of the phase-number cell. This is a pre-existing documentation inconsistency in REQUIREMENTS.md's traceability table, not a Phase 6 gap — informational only, does not block this verification.

### Anti-Patterns Found

None. Grepped all phase-modified files (`EntitiesConfig.cs`, `EntitiesConfigLoader.cs`, `GroupInfluxReader.cs`, `BatchSchedulerWorker.cs`, `DiscoveryPublisher.cs`, `StatePublisher.cs`, `UniqueId.cs`, `MqttPublisherWorker.cs`, `Program.cs`) for TBD/FIXME/XXX/TODO/HACK/PLACEHOLDER and stub-language patterns — zero matches.

### Code Review Findings (06-REVIEW.md / 06-REVIEW-FIX.md)

2 critical + 4 warning findings identified by code review, all 6 fixed and verified in source:
- **CR-01** (race condition in `MqttPublisherWorker.OnConfigChanged`): fixed with `SemaphoreSlim(1,1)` — confirmed present at `MqttPublisherWorker.cs:42,84-136`. Regression test explicitly documented as infeasible (sealed `MqttConnection`, no fake/interface seam for worker-level concurrency testing) — this is a judgment-tier gap, not a code gap; the fix itself follows an established production pattern (`_connectGate` in `MqttConnection`).
- **CR-02** (Flux injection guard incomplete — allowed `\r`/`\n`): fixed, regex extended to `^[^"\\\r\n]+$`, confirmed at `GroupInfluxReader.cs:23-24`. 4 regression tests added and passing.
- **WR-01** (no duplicate-member check): fixed, confirmed at `EntitiesConfigLoader.cs:119-129`.
- **WR-02** (stalenessCap not validated against zero/negative): fixed, confirmed at `BatchSchedulerWorker.cs:186-189, 498-501`.
- **WR-03** (misleading "registry not populated" log on healthy single-unit case): fixed, confirmed at `EntitiesConfigLoader.cs:156-170`.
- **WR-04** (every/aggFn missing the same Flux guard as other interpolated fields): fixed, bundled with CR-02, confirmed at `GroupInfluxReader.cs:91-94`.

WR-05 (pre-existing, non-group entity retraction gap) and IN-01..IN-04 (info-level: unused using, code duplication, magic constant) were explicitly out of scope for this phase's fix pass and left untouched — correctly so, as WR-05 predates this phase and the IN items are non-blocking style notes.

### Human Verification Required

None. This phase is entirely .NET orchestrator batch-path code (no UI, no Python changes) and all success criteria are verifiable through code inspection + automated tests. The plans themselves flag two items as deferred to a later live/manual verification step (non-blocking for this phase's automated verification):
- Assumption A1 (06-RESEARCH.md): `aggregateWindow`+`pivot` null-on-gap Flux semantics are doc-verified but not live-verified against a real InfluxDB instance — flagged by the plan itself as "non-blocking for offline unit verification," to be confirmed before production sign-off. This is a pre-acknowledged limitation of the phase's own verification scope, not a gap introduced by this verification pass.
- End-to-end HA bring-up (peer group → 3 entities under one device; joint group → 1 entity) is deferred to live HA testing per the milestone's existing UAT deferral pattern — consistent with all other phases in this milestone.

### Gaps Summary

No gaps. All 4 success criteria are verified against actual source code (not just SUMMARY.md claims): group config schema with no auto-discovery (GRP-01), server-side time-alignment with staleness-cap enforcement (GRP-02), MQTT discovery publish/retract with no orphaning and a fixed concurrency race (GRP-08), and config-load-time unit/floor guards that degrade safely rather than crash or silently misbehave (GRP-04). The full orchestrator suite passes 309/309 with zero regressions. All 6 code-review findings (2 critical, 4 warning) were fixed and the fixes are present in source, not just claimed. The one unaddressed review item (CR-01's dedicated regression test) is an explicitly-scoped, well-justified test-infrastructure limitation — the underlying fix is in place and follows an established codebase pattern.

---

_Verified: 2026-07-02_
_Verifier: Claude (gsd-verifier)_
