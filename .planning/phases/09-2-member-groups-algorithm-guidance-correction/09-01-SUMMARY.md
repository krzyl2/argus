---
phase: 09-2-member-groups-algorithm-guidance-correction
plan: 01
subsystem: api
tags: [validation, groupParams, GroupInputValidator, EntitiesConfigLoader, DetectorCatalog, pyod, dotnet, vitest]

# Dependency graph
requires:
  - phase: 08-group-config-ui-algorithm-chooser
    provides: GroupInputValidator.cs, groupParams.ts, EntitiesConfigLoader.ValidateGroups, DetectorCatalog.cs (all pre-existing, floor-of-3 + ecod default)
provides:
  - Uniform member-count floor of 2 (both joint and peer_divergence modes) at all three config-validation enforcement points
  - Guided chooser "together" answer now recommends copod (was ecod)
  - Rewritten DetectorCatalog BestFor copy (draft) reflecting correlation-handling/attribution accuracy + 2-member peer_divergence caveat
affects: [09-02-python-pairwise-delta, 09-03-csharp-wiring]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Config-validation member floor is uniform across modes (no mode branching) — the algorithm-level floor-of-3 for classic peer_divergence median/MAD lives entirely inside PeerDivergenceDetector.py, untouched by this plan"

key-files:
  created: []
  modified:
    - orchestrator/ui/src/validation/groupParams.ts
    - orchestrator/ui/src/validation/groupParams.test.ts
    - orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs
    - orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs
    - orchestrator/Argus.Orchestrator/Web/DetectorCatalog.cs
    - orchestrator/Argus.Orchestrator.Tests/GroupsEndpointsTests.cs
    - orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs

key-decisions:
  - "Config-validation member floor lowered to 2 for BOTH joint and peer_divergence modes (Assumption A1 from 09-RESEARCH.md), reversing the literal text of ROADMAP scope item 1 — required so 2-member peer_divergence groups (Plan 09-02/09-03's pairwise-delta feature) can be saved at all through the UI"
  - "Added new acceptance tests (2-member joint, 2-member peer_divergence same-units) at both GroupInputValidator and EntitiesConfigLoader layers, beyond the plan's minimum ask, to directly verify the plan's must_haves truths rather than relying only on updated below-floor tests"
  - "peer_divergence BestFor copy revised to state the 2-member caveat (single pair verdict, no per-member attribution) rather than universally claiming 'know WHICH member is diverging'"

patterns-established:
  - "Test intent encoding (Rule 9): below-floor tests now use 1-member/empty lists (the true edge case at floor=2), not 2-member lists (now a valid case) — the old 2-member below-floor assertions were rewritten to acceptance assertions instead of just changing numbers"

requirements-completed: [GRP-10, GRP-12, ALGO-05, ALGO-06]

# Metrics
duration: 12min
completed: 2026-07-03
status: complete
---

# Phase 9 Plan 1: 2-Member Group Floor + Algorithm Guidance Correction Summary

**Lowered group member-count floor from 3 to 2 at all three config-validation layers (client TS, server C#, config-load C#) for both joint and peer_divergence modes, switched the guided chooser's "together" recommendation from ecod to copod, and rewrote all 5 DetectorCatalog BestFor entries with accurate correlation-handling/attribution copy including a 2-member peer_divergence caveat.**

## Performance

- **Duration:** 12 min
- **Started:** 2026-07-03T09:22:00Z
- **Completed:** 2026-07-03T09:34:00Z
- **Tasks:** 2 completed
- **Files modified:** 7

## Accomplishments
- A 2-member group (joint or peer_divergence) now passes client-side, server-side, and config-load validation — previously blocked at all three layers regardless of downstream detector support
- Guided "together" answer now correctly recommends copod, matching the empirical PyOD finding that ECOD/PCA produce ~90% false positives on correlated-pair relationship-break scenarios
- All 5 BestFor entries rewritten to honestly distinguish which detectors suit correlated-pair relationships (copod, iforest) vs which tend to false-positive on them (ecod, pca), and which support per-member attribution
- peer_divergence's BestFor copy no longer implies universal per-member attribution — explicitly states the 2-member case reports a single pair-relationship verdict

## Task Commits

Each task was committed atomically:

1. **Task 1: Lower config-validation member-count floor from 3 to 2 (both modes)** - `d657c1b` (feat)
2. **Task 2: Switch guided "together" default to copod and rewrite BestFor copy** - `ceb0c83` (feat)

**Plan metadata:** (this commit)

## Files Created/Modified
- `orchestrator/ui/src/validation/groupParams.ts` - MIN_MEMBERS=2, MSG_BELOW_FLOOR updated to "at least 2 members"
- `orchestrator/ui/src/validation/groupParams.test.ts` - below-floor tests now use 1-member/empty lists; added explicit "accepts exactly 2 members" test
- `orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs` - MinMembers=2 (authoritative boundary; interpolated error message updates automatically)
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` - member-count guard lowered from `< 3` to `< 2`, LogWarning text updated
- `orchestrator/Argus.Orchestrator/Web/DetectorCatalog.cs` - `Guided()` "together" -> copod; all 5 BestFor entries rewritten (draft, pending operator redaction per ROADMAP scope item 4)
- `orchestrator/Argus.Orchestrator.Tests/GroupsEndpointsTests.cs` - updated below-floor test to 1-member; added 2-member joint/peer_divergence acceptance tests; updated Guided test to copod; added peer_divergence BestFor 2-member-caveat test
- `orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs` - updated below-floor test to 1-member; fixed a "mixed valid/invalid" test whose invalid group (2 members) would otherwise now incorrectly survive; added 2-member joint/peer_divergence load-survival tests

## Decisions Made
- Config-validation floor is 2 for both modes uniformly, no mode branching — per 09-RESEARCH.md's Assumption A1 resolution of the ROADMAP's internal contradiction (scope item 1 said "stays at 3" for peer_divergence, but scope item 2 requires accepting 2-member peer_divergence groups). The algorithm-level floor of 3 inside `PeerDivergenceDetector._MIN_MEMBERS` (Python) is untouched by this plan.
- Added acceptance tests beyond the plan's literal minimum (which only required updating existing below-floor assertions) to directly verify the plan's `must_haves.truths` — that a 2-member group passes validation in both modes — at both the `GroupInputValidator` and `EntitiesConfigLoader` layers, not just inferred from the below-floor test change.

## Deviations from Plan

None - plan executed as written. The two additional acceptance-test pairs (2-member joint/peer_divergence at both GroupInputValidator and EntitiesConfigLoader layers) are within the plan's `<action>` guidance ("update tests... to expect acceptance") and directly verify the plan's own `must_haves.truths`; no scope beyond the 2 listed files per task was touched.

## Issues Encountered

Two pre-existing tests would have silently broken if left unchanged after lowering the floor to 2 (both correctly flagged before making changes, by reading the test files rather than only running them post-edit):
- `GroupsEndpointsTests.Validate_GroupBelowFloor_ReturnsValidationError` used a 2-member group as the "below floor" case — now valid, so the test would have failed; updated to use a 1-member group.
- `EntitiesConfigTests.Load_MixedValidAndInvalidGroups_KeepsOnlyValid`'s "invalid_group" had 2 members — now valid, which would have made the test assert `Single(config.Groups)` incorrectly (both groups would survive); updated its member list to 1.

Both were caught and fixed before running tests, then confirmed via the full test run (372/372 passing).

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- Plan 09-02 (Python pairwise-delta) and Plan 09-03 (C# wiring for the 5 count-blind branches identified in 09-RESEARCH.md Pitfalls 1-5) are unblocked: 2-member peer_divergence groups can now be saved through the UI, which is a prerequisite for those plans' end-to-end verification.
- The `BestFor` copy is explicitly flagged as draft (code comment + STATE.md decision) pending operator sign-off before ship — not a blocker for Plan 09-02/09-03, but should not be treated as final wording.
- No blockers.

---
*Phase: 09-2-member-groups-algorithm-guidance-correction*
*Completed: 2026-07-03*

## Self-Check: PASSED
