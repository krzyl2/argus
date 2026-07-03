---
phase: 09-2-member-groups-algorithm-guidance-correction
verified: 2026-07-03T14:30:00Z
status: passed
score: 11/11 must-haves verified
behavior_unverified: 0
overrides_applied: 0
re_verification:
  previous_status: human_needed
  previous_score: 9/11
  gaps_closed:
    - "Membership-change retraction correctly handles a peer_divergence group crossing the 2/3+-member shape boundary (CR-02)"
    - "A group that shrinks from 3+ members to exactly 2 self-heals instead of silently losing anomaly detection for up to 24h (CR-01)"
  gaps_remaining: []
  regressions: []
---

# Phase 9: 2-Member Groups + Algorithm Guidance Correction Verification Report

**Phase Goal:** Operators can create valid anomaly-detection groups with exactly 2 members (e.g. two front-tire pressures, two boiler-room temperature sensors), and the guided algorithm chooser recommends detectors that are empirically well-suited to the operator's stated intent instead of a naive default.
**Verified:** 2026-07-03T14:30:00Z
**Status:** passed
**Re-verification:** Yes — after gap closure (commit `8c5d176`, "test(09): close verification gaps for CR-01 and CR-02 regressions")

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | A 2-member group passes client-side, server-side, and config-load validation (both joint and peer_divergence modes) | ✓ VERIFIED | Unchanged since prior verification. `groupParams.ts:8` `MIN_MEMBERS = 2`; `GroupInputValidator.cs:21` `MinMembers = 2`; `EntitiesConfigLoader.cs:111` guard is `Count < 2`. |
| 2 | A 1-member group is still rejected at all three layers with an "at least 2" message | ✓ VERIFIED | Unchanged. Same guard conditions reject 1-member lists by construction. |
| 3 | Guided chooser's "together" answer recommends copod, not ecod | ✓ VERIFIED | Unchanged. `DetectorCatalog.cs:158` `new GuidedAnswer("together", "copod")`. |
| 4 | All 5 DetectorCatalog BestFor entries reflect correlation-handling + attribution capability, with a 2-member peer_divergence no-attribution caveat | ✓ VERIFIED | Unchanged. |
| 5 | A 2-member peer_divergence FitGroup computes member_a - member_b, fits a PyOD MAD model, persists via save_pyod under (group_slug, "peer_divergence") | ✓ VERIFIED | Unchanged. `servicer.py:410-432`. Behavioral test `test_fit_group_persists_via_save_pyod` re-run and passing. |
| 6 | A 2-member peer_divergence ScoreGroupBatch returns exactly one group_verdict, empty per_member, empty contributions | ✓ VERIFIED | Unchanged. Test `test_fit_then_score_returns_group_verdict_with_empty_per_member` re-run and passing. |
| 7 | A 2-member peer_divergence ScoreGroupBatch with no fitted model aborts INVALID_ARGUMENT with the same message shape as the joint path | ✓ VERIFIED | Unchanged. Test `test_score_before_fit_aborts_call_fit_group_first` re-run and passing. |
| 8 | The classic N>=3 peer_divergence path (PeerDivergenceDetector median/MAD) is behaviorally unchanged | ✓ VERIFIED | Unchanged. `git diff` confirms zero changes to `detector/argus_detector/group/peer_divergence.py` or `detector/tests/test_peer_divergence.py` in the gap-closure commit either. |
| 9 | Runtime staleness/publish/nightly-fit logic in BatchSchedulerWorker and entity-shape logic in DiscoveryPublisher/MqttPublisherWorker are count-aware (not Mode-string-only), and N>=3/joint behavior is unchanged | ✓ VERIFIED | Unchanged. `BatchSchedulerWorker.cs` count-gated logic untouched by the gap-closure commit. |
| 10 | Membership-change retraction correctly retracts the OLD entity shape when a peer_divergence group crosses the 2-member/3+-member boundary (CR-02) | ✓ VERIFIED | **Gap closed.** Retraction decision logic extracted from `MqttPublisherWorker.OnConfigChanged` into a pure static `DiscoveryPublisher.ComputeRetractionEntities(oldGroup, newGroup)` (no MQTT I/O — see `DiscoveryPublisher.cs:237-270`). Diffed old inline logic vs. the new static method line-by-line: identical decision tree (shape-diff check, same-shape member diff, joint no-op, whole-group-removed branch), only relocated — no behavior change. `MqttPublisherWorker.cs:90-104` now calls the extracted method and performs only the I/O. 8 new tests in `orchestrator/Argus.Orchestrator.Tests/MqttRetractionTests.cs` (`ComputeRetractionEntities_*`) directly cover the exact 3→2 shrink, 2→3 grow, same-shape diff, same-shape no-change, joint no-op, and whole-group-removal (peer 3+, joint, and 2-member-peer) scenarios named in the prior report's human-verification item. All 8 independently executed and PASS (`dotnet test --filter FullyQualifiedName~ComputeRetractionEntities` → 8/8 passed). |
| 11 | A 2-member peer_divergence group that started with 3+ members and was later shrunk to 2 self-heals (aborts INVALID_ARGUMENT with a "call FitGroup first" message) instead of silently producing zero anomaly detection for up to 24h (CR-01) | ✓ VERIFIED | **Gap closed.** New test `test_score_with_stale_classic_registration_self_heals_instead_of_crashing` in `detector/tests/test_pairwise_delta.py:191-234` reproduces the exact scenario: a 3-member `FitGroup` registers a classic `PeerDivergenceDetector` at `(group_shrinking, peer_divergence)` (asserted via `isinstance`), then a 2-member `ScoreGroupBatch` for the same `group_id` is called before any 2-member `FitGroup` runs. Asserts `INVALID_ARGUMENT` with `"call FitGroup first"` in the abort details, matching `servicer.py:263` (`isinstance(model, PairwiseDeltaDetector)` guard). Independently executed: `pytest -k stale_classic` → 1/1 PASSED. |

**Score:** 11/11 truths verified (0 present, behavior-unverified)

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `orchestrator/ui/src/validation/groupParams.ts` | Client-side floor of 2 | ✓ VERIFIED | Unchanged from prior verification. |
| `orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs` | Authoritative server floor of 2 | ✓ VERIFIED | Unchanged. |
| `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` | Config-load floor of 2 | ✓ VERIFIED | Unchanged. |
| `orchestrator/Argus.Orchestrator/Web/DetectorCatalog.cs` | copod guided default + rewritten BestFor | ✓ VERIFIED | Unchanged. |
| `detector/argus_detector/group/pairwise_delta.py` | PairwiseDeltaDetector wrapping PyODDetector | ✓ VERIFIED | Unchanged. |
| `detector/argus_detector/pyod_detector.py` | `is_anomaly(score)` public accessor | ✓ VERIFIED | Unchanged. |
| `detector/argus_detector/servicer.py` | `len(request.series) == 2` sub-branches with isinstance guard | ✓ VERIFIED | Unchanged; `isinstance(model, PairwiseDeltaDetector)` guard at line 263 now behaviorally proven by the new test. |
| `detector/tests/test_pairwise_delta.py` | Unit coverage for delta/fit/score/is_anomaly/servicer routing, incl. CR-01 stale-model scenario | ✓ VERIFIED | 15 test functions (13 prior + 2 new: `test_score_before_fit_aborts_call_fit_group_first` was already present; `test_score_with_stale_classic_registration_self_heals_instead_of_crashing` is new). Full file re-run: 15/15 pass. |
| `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` | Count-aware staleness/publish/fit-skip removal | ✓ VERIFIED | Unchanged. |
| `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs` | UsesPerMemberEntities helper + new ComputeRetractionEntities pure decision method | ✓ VERIFIED | `UsesPerMemberEntities` unchanged; new `ComputeRetractionEntities` (lines 237-270) verified equivalent to prior inline logic. Substantive, wired from `MqttPublisherWorker.cs`, and now covered by 8 dedicated unit tests (no MQTT/broker dependency required). |
| `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs` | Count-aware retract branch, CR-02 shape-diff fix | ✓ VERIFIED | Refactored to delegate the decision to `DiscoveryPublisher.ComputeRetractionEntities`, retaining only the I/O loop. Confirmed line-by-line equivalence with the pre-refactor inline logic (see Key Link Verification). |
| `orchestrator/Argus.Orchestrator.Tests/MqttRetractionTests.cs` | New CR-02 regression coverage | ✓ VERIFIED | 8 new `ComputeRetractionEntities_*` tests added, covering all scenarios named in the prior human-verification item. Independently executed: 8/8 PASS. |

### Key Link Verification

| From | To | Via | Status | Details |
|------|-----|-----|--------|---------|
| `groupParams.ts` | `GroupInputValidator.cs` | Floor value + message kept consistent | ✓ WIRED | Unchanged. |
| `servicer.py` (ScoreGroupBatch/FitGroup) | `pairwise_delta.py` | `PairwiseDeltaDetector` constructed/imported inline | ✓ WIRED | Unchanged. |
| `pairwise_delta.py` | `pyod_detector.py` | Delegates to internal `PyODDetector` | ✓ WIRED | Unchanged. |
| `servicer.py` FitGroup (2-member) | `model_store.py` | `save_pyod(...)` | ✓ WIRED | Unchanged. |
| `DiscoveryPublisher.cs` | `MqttPublisherWorker.cs` | Single `UsesPerMemberEntities` source of truth reused | ✓ WIRED | Unchanged. |
| `DiscoveryPublisher.ComputeRetractionEntities` | `MqttPublisherWorker.OnConfigChanged` | Pure decision method called, result used to drive `RetractGroupAsync` I/O | ✓ WIRED | `MqttPublisherWorker.cs:100-102`: `var toRetract = DiscoveryPublisher.ComputeRetractionEntities(oldGroup, newGroup); if (toRetract is not null) await DiscoveryPublisher.RetractGroupAsync(_mqtt, oldGroup, toRetract, _stoppingToken);` — behavior-preserving extraction confirmed by manual line-by-line diff against the pre-refactor inline branch structure (shape-diff / same-shape-diff / joint-no-op / whole-group-removed all preserved exactly). |
| `BatchSchedulerWorker.cs` publish branch | `GroupScoreResponse` (proto) | Keys on `response.PerMember.Count`/`response.GroupVerdict`, not `group.Mode` | ✓ WIRED | Unchanged. |

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| CR-01 regression test passes in isolation | `pytest tests/test_pairwise_delta.py -k stale_classic -v` | `1 passed` | ✓ PASS |
| CR-02 regression tests pass in isolation | `dotnet test Argus.Orchestrator.Tests --filter "FullyQualifiedName~ComputeRetractionEntities"` | `8/8 passed` | ✓ PASS |
| Full .NET suite green | `dotnet test Argus.Orchestrator.Tests` | `385 passed, 0 failed` | ✓ PASS (matches SUMMARY claim exactly — was 377 before this gap-closure commit) |
| Full Python suite green | `pytest` (detector/) | `209 passed, 0 failed` | ✓ PASS (matches SUMMARY claim exactly — was 208 before this gap-closure commit) |

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|-------------|-------------|--------|----------|
| GRP-10 | 09-01 | Joint-mode groups accept exactly 2 members | ✓ SATISFIED | Floor lowered to 2 at all 3 config-validation layers, uniformly for both modes (Truth 1). |
| GRP-11 | 09-02, 09-03 | 2-member peer_divergence groups score via pairwise-delta, publishing one group-level entity pair | ✓ SATISFIED | End-to-end path verified: Python fit/score (Truths 5-7) + .NET count-aware wiring (Truth 9) + one group-level entity via `UsesPerMemberEntities` (Truth 9) + retraction on shape crossing now behaviorally proven (Truth 10). No longer partially satisfied — the previously-flagged gap is closed. |
| GRP-12 | 09-01, 09-03 | Client/server/runtime floors consistent with mode-dependent membership rules | ✓ SATISFIED | Unchanged from prior verification. |
| ALGO-05 | 09-01 | Guided "together" recommends COPOD not ECOD | ✓ SATISFIED | Truth 3. |
| ALGO-06 | 09-01 | BestFor copy reflects correlation-handling + attribution, incl. 2-member caveat | ✓ SATISFIED | Truth 4. |

No orphaned requirements — all 5 IDs declared in `.planning/REQUIREMENTS.md` for Phase 9 (`REQUIREMENTS.md:23-25,33-34`) are claimed by at least one of the three plans and satisfied.

**Note (unchanged, informational only):** `.planning/REQUIREMENTS.md`'s tracking table (lines 102-106) still lists all 5 Phase 9 requirement IDs as status "Planned", while the checklist above it (lines 23-25, 33-34) marks them `[x]` complete. Documentation-staleness inconsistency, not a functional gap — does not block phase completion.

### Anti-Patterns Found

None. Re-scanned the 4 files touched by the gap-closure commit (`8c5d176`) — `detector/tests/test_pairwise_delta.py`, `orchestrator/Argus.Orchestrator.Tests/MqttRetractionTests.cs`, `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs`, `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs` — for `TBD`/`FIXME`/`XXX`/`TODO`/`HACK`/`PLACEHOLDER` markers. Zero matches.

### Post-Review Fix Verification (CR-01, CR-02, WR-01, WR-02) — updated

| Finding | Commit | Code Match | Test Coverage Added |
|---------|--------|------------|----------------------|
| CR-01 (registry key collision / stale-typed model) | `710982f` (fix) + `8c5d176` (test) | ✓ Exact match to reviewer's prescribed `isinstance` guard | ✓ **Now covered** — `test_score_with_stale_classic_registration_self_heals_instead_of_crashing`, independently re-run and passing |
| CR-02 (orphaned MQTT discovery entities on shape crossing) | `544a6fd` (fix) + `8c5d176` (test + refactor) | ✓ Exact match — decision logic extracted verbatim into `ComputeRetractionEntities`, no behavior change (confirmed by diff) | ✓ **Now covered** — 8 tests in `MqttRetractionTests.cs`, independently re-run and passing |
| WR-01 (silent GRP-04 no-verdict logging gap) | `e15da41` | ✓ Exact match to reviewer's prescribed `else if` branch | Still no dedicated test asserting the new log branch fires — not part of this re-verification's flagged scope (was not a Human Verification item in the prior report; simple single-branch change confirmed correct by inspection) |
| WR-02 (FitGroup registration bypasses locking discipline) | `f8aa8ae` | ✓ `swap_model()` correctly takes `_entity_lock` | Still no dedicated test — concurrency behavior, not part of this re-verification's flagged scope |

Both previously-flagged gaps (CR-01, CR-02) are now closed with direct, independently-executed, passing behavioral tests that reproduce the exact scenarios named in the prior VERIFICATION.md's human-verification items. WR-01/WR-02 remain code-inspection-verified only, as they were in the prior report — they were not raised as human-verification items and are not re-litigated here.

### Human Verification Required

None. Both items from the prior verification (CR-01, CR-02) now have direct behavioral test evidence matching their exact repro scenarios.

### Gaps Summary

No gaps. All 11 must-have truths are verified with direct, independently-executed passing test evidence. The prior verification's 2 behavior-unverified items are closed:

- **CR-01** (self-heal on 3→2 shrink with stale registry entry): closed by `test_score_with_stale_classic_registration_self_heals_instead_of_crashing`, which pre-populates the registry with a real classic `PeerDivergenceDetector` via a 3-member `FitGroup` before exercising the 2-member `ScoreGroupBatch` path — exactly the scenario the prior report described as untested.
- **CR-02** (MQTT entity retraction across the 2/3+-member shape boundary): closed by extracting the retraction decision into pure, dependency-free `DiscoveryPublisher.ComputeRetractionEntities` and adding 8 tests that directly exercise the shrink/grow boundary crossings, same-shape diffs, and whole-group-removal cases. The extraction was verified to preserve the exact decision logic of the original inline code (no MQTT I/O needed to test the decision; the I/O-performing caller in `MqttPublisherWorker` is unchanged in behavior, only in structure).

Full test suites independently re-run and green: 385/385 .NET tests, 209/209 Python tests — matching the SUMMARY's claimed counts exactly.

---

_Verified: 2026-07-03T14:30:00Z_
_Verifier: Claude (gsd-verifier)_
