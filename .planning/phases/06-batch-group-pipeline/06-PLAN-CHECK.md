# Phase 6 Plan Check - Batch Group Pipeline

Verdict: PASS
Plans checked: 06-01, 06-02, 06-03, 06-04 (4 plans, Wave 1: 01/02/03 parallel, Wave 2: 04 depends_on [01,02,03])
Issues: 0 blockers, 1 warning, 1 info

## Dimension 1: Requirement Coverage

| Requirement | Plans | Status |
|---|---|---|
| GRP-01 | 06-01 | Covered |
| GRP-02 | 06-02, 06-04 | Covered |
| GRP-08 | 06-03, 06-04 | Covered |

All three roadmap-mapped requirement IDs appear in at least one plans requirements frontmatter. None dropped.

Note (info, not blocking): 06-01 also declares requirements GRP-01 and GRP-04. GRP-04 is officially owned by Phase 5 and already marked Complete in REQUIREMENTS.md. 06-01 re-implements the min-member floor at config-load time as defense-in-depth per CONTEXT.md Config-Load Validation decision -- legitimate, but the frontmatter citation of a Phase-5-complete requirement ID is a cross-phase reference, not a Phase 6 deliverable. Does not affect Phase 6 goal achievement.

## Dimension 2: Task Completeness

All 12 tasks (3 per plan x 4 plans) have Files, Action, Verify (automated dotnet build/test commands), and Done. Actions are concrete: exact method signatures, exact Flux query shape, exact log event numbers. No missing elements.

## Dimension 3: Dependency Correctness

- 06-01/02/03: depends_on empty, wave 1, disjoint files_modified -- confirmed no intra-Wave-1 overlap.
- 06-04: depends_on 06-01/06-02/06-03, wave 2, only plan touching BatchSchedulerWorker.cs and Program.cs.
- No cycles, no forward references, wave numbers consistent.
- Cross-wave overlaps confirmed acceptable: LogEvents.cs modified by both 06-01 (events 1004/1005) and 06-04 (events 5012-5015), non-conflicting ranges, sequenced correctly. BatchSchedulerWorkerTests.cs FakeStatePublisher extended by 06-04 to implement 06-03 new IStatePublisher members -- safe given the 06-03 to 06-04 ordering; 06-03 does not itself touch this test file, so it is not an intra-Wave-1 overlap.

## Dimension 4: Key Links Planned

All declared key_links have concrete implementing code in each plans action section. Interface handshake explicitly verified:
- EntitiesConfig.Groups + GroupConfig (06-01) consumed by 06-04 via _liveConfig.Get().Groups. Produced.
- IGroupInfluxDataSource.QueryGroupAsync / GroupInfluxReader + GroupAlignedData/GroupRow shape (06-02) consumed by 06-04 Task 1/2. Produced with matching shape.
- ScoreGroupBatchAsync / FitGroupAsync on IBatchDetectorClient (06-02) consumed by 06-04 Task 1. Produced with matching signatures.
- PublishGroupFlagAsync/PublishGroupScoreAsync + DiscoveryPublisher.PublishGroupAsync/RetractGroupAsync (06-03) consumed by 06-04 Task 1/2. Produced with matching signatures, including removed-members-only retraction contract.

No symbol 06-04 consumes is missing from an upstream artifacts section. Handshake coherent.

## Dimension 5: Scope Sanity

| Plan | Tasks | Files modified |
|---|---|---|
| 06-01 | 3 | 4 |
| 06-02 | 3 | 5 |
| 06-03 | 3 | 5 |
| 06-04 | 3 | 6 |

All within target/warning thresholds. No blocker.

## Dimension 6: Verification Derivation

All must_haves.truths across the four plans are operator/user-observable: operator can declare a top-level groups list, peer-divergence groups publish one binary_sensor+score per member, a joint group is skipped for the whole cycle when any member breaches the wall-clock staleness_cap. No bare implementation-detail truths. Artifacts map to truths; key_links cover critical wiring.

## Dimension 7: Context Compliance (06-CONTEXT.md)

All locked decisions have implementing tasks; no contradictions found:
- Group config schema shape -- 06-01 Task 1, exact match.
- InfluxDB alignment (aggregateWindow+pivot, defaults, staleness_cap exclusion, reused lookback) -- 06-02 Task 1, exact match.
- MQTT layout (peer per-member / joint group-level, unique_id scheme, retract-before-publish, one HA device per group) -- 06-03 Tasks 1-2, exact match including the critical device.identifiers deviation as an explicit prohibition.
- Config-load validation (units via IHaSensorRegistry, floor of 3, degrade-not-crash, scoring inside existing BatchSchedulerWorker) -- 06-01 Task 2 + 06-04 Task 1, exact match.

No deferred idea (STRM-01/02, group config UI, GRP-09 UI surfacing, sensitivity presets) appears in any plan. 06-04 carries Contributions through only for logging, correctly deferring GRP-09 UI treatment to Phase 8.

## Dimension 7b: Scope Reduction Detection

Scanned all plans for scope-reduction language (v1/v2, static-for-now, future-enhancement, not-wired-to, stub, etc.). All matches found reference the legitimate, locked retirement of the dead EntityConfig.Covariates/Groups placeholders -- not a reduction of a new decision scope. No blocker.

## Dimension 7c: Architectural Tier Compliance

RESEARCH.md Architectural Responsibility Map assigns all five capabilities to API/Backend (.NET orchestrator) tier only. All four plans place work exclusively in orchestrator/Argus.Orchestrator/Config,Batch,Mqtt and Program.cs. No tier mismatch.

## Dimension 8: Nyquist Compliance

SKIPPED (nyquist_validation is false in .planning/config.json).

## Dimension 9: Cross-Plan Data Contracts

06-02 (raw Flux to GroupAlignedData, null equals gap, no fill()) feeds 06-04 (staleness exclusion applied at the scoring boundary). 06-02 explicitly states it does NOT apply the staleness cap itself; 06-04 explicitly states it only APPLIES the exclusion policy. Single source of raw data, single consumer of the exclusion policy -- no conflicting transforms.

## Dimension 10: CLAUDE.md Compliance

All plans stay within the .NET orchestrator (no ML/Python changes, correctly, since Phase 5 already built the detectors), reuse the existing gRPC transport unchanged, and 06-03 Polish friendly-name convention matches D8. Zero new NuGet packages per RESEARCH.md. Compliant.

## Dimension 11: Research Resolution

06-RESEARCH.md Open Questions header lacks the (RESOLVED) suffix and lists 3 open questions.

Assessment (WARNING, not blocker): all three questions are substantively resolved elsewhere and the resolution is correctly carried into the plans verbatim:
1. Staleness exclusion granularity -- RESEARCH recommendation (peer drops member, joint skips whole group) adopted verbatim in 06-04 behavior/action (Pitfall 3 compliance).
2. Nullable registry threading -- RESEARCH recommendation adopted verbatim in 06-01 Task 2.
3. Placeholder deletion timing -- RESEARCH recommendation (delete now) adopted verbatim in 06-01 Task 1/2.

A planner reading only PLAN.md would still execute the correct, already-decided behavior in all three cases. Recommend appending (RESOLVED) to the RESEARCH.md section header for process hygiene; does not block execution.

## Dimension 12: Pattern Compliance

06-PATTERNS.md exists with full File Classification (11/11 analogs found). Cross-checked each plans action section against its assigned analog -- all plans explicitly reference the correct analog file/pattern: EntityConfig shape, EmptyEntitiesWarning branch, InfluxDbReader guard/dual-ctor, IBatchDetectorClient adapter shape, UniqueId/StatePublisher/DiscoveryPublisher analogs including the explicit critical-deviation callout for shared device.identifiers, and the RunBatchAsync/RunNightlyFitAsync fault-isolation loop. Shared Patterns items (fault isolation, per-cycle live-config read, Flux injection guard, retract-before-publish, DoubleValue to double, Validate-before-Swap) are each referenced by name in the relevant plan(s). No gap found.

## Recommendation

No blockers. One warning (Dimension 11: RESEARCH.md Open Questions section lacks the formal RESOLVED marker, though all three questions are substantively resolved and correctly carried into the plans) and one info note (Dimension 1: 06-01 cross-phase GRP-04 requirement citation is harmless but technically references a Phase-5-owned, already-complete requirement).

Plans are cleared for execution. Run /gsd-execute-phase 6 to proceed.
