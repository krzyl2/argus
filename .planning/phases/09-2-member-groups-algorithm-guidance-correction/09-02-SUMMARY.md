---
phase: 09-2-member-groups-algorithm-guidance-correction
plan: 02
subsystem: detection
tags: [pyod, mad, grpc, python-detector, peer-divergence]

# Dependency graph
requires:
  - phase: 05
    provides: DetectorServicer.ScoreGroupBatch/FitGroup peer_divergence + joint-mode dispatch idiom, PeerDivergenceDetector (classic N>=3), GroupMultivariateDetector.is_anomaly() as the WR-02 accessor pattern
provides:
  - PyODDetector.is_anomaly(score) public accessor (WR-02 convention extended to per-entity MAD)
  - PairwiseDeltaDetector — delta(member_a, member_b) + PyODDetector (MAD) wrapper for 2-member peer_divergence groups
  - servicer.py len(request.series) == 2 sub-branches in FitGroup and ScoreGroupBatch, routed before PeerDivergenceDetector construction
  - ModelStore key (group_slug, "peer_divergence") now actually written (via save_pyod) for 2-member groups
affects: [09-03 (.NET orchestrator wiring for the new group_verdict-only response shape)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Servicer-level count branching (len(request.series) == 2), not detector-class branching — keeps PeerDivergenceDetector's floor-of-3 contract and its test untouched"
    - "PairwiseDeltaDetector delegates to PyODDetector (composition, not subclassing) — reuses proven MAD math, no new statistics"

key-files:
  created:
    - detector/argus_detector/group/pairwise_delta.py
    - detector/tests/test_pairwise_delta.py
  modified:
    - detector/argus_detector/pyod_detector.py
    - detector/argus_detector/servicer.py
    - detector/tests/test_servicer.py

key-decisions:
  - "PairwiseDeltaDetector wraps PyODDetector unmodified (delegation) rather than subclassing MAD or reimplementing threshold logic — matches ROADMAP's explicit 'reuse proven detection, don't invent new group math' directive"
  - "2-member peer_divergence persists via ModelStore.save_pyod (not save_group_bundle) — a single derived feature needs no scaler/bundle"
  - "2-member ScoreGroupBatch returns group_verdict populated, per_member and contributions both EMPTY — a 2-point delta cannot attribute the anomaly to either member (same degeneracy as classic peer_divergence at N=2); never fabricate attribution"
  - "TestScoreGroupBatchFloor's below-floor fixture moved from 2 to 1 member in test_servicer.py — 2 members is no longer a floor case now that it routes to the new pairwise path (Rule 1 fix: pre-existing test asserted the exact old behavior this plan intentionally changes)"

requirements-completed: [GRP-11]

# Metrics
duration: 6min
completed: 2026-07-03
status: complete
---

# Phase 9 Plan 2: Pairwise-Delta Peer Divergence (Python Detector) Summary

**New `PairwiseDeltaDetector` (delegates to the existing PyOD MAD detector) scores `member_a - member_b` for 2-member `peer_divergence` groups; servicer routes on `len(request.series) == 2` before the classic `PeerDivergenceDetector` path, leaving the N>=3 algorithm and its locked floor test completely untouched.**

## Performance

- **Duration:** ~6 min (11:38 → 11:44)
- **Tasks:** 2
- **Files modified:** 5 (2 new, 3 modified)

## Accomplishments
- `PyODDetector.is_anomaly(score)` added — mirrors `GroupMultivariateDetector.is_anomaly()`'s WR-02 public-accessor convention so `servicer.py` never reaches into `_model.threshold_` directly.
- New `PairwiseDeltaDetector` (`detector/argus_detector/group/pairwise_delta.py`) computes `compute_delta(a, b)` and delegates `fit`/`score_batch`/`is_fitted`/`is_anomaly`/`from_params` to an internal `PyODDetector` — zero new ML math.
- `servicer.py`'s `FitGroup` and `ScoreGroupBatch` both gained a `len(request.series) == 2` sub-branch for `detector == "peer_divergence"`, placed before any `PeerDivergenceDetector` construction. `FitGroup` fits+registers+persists (via `save_pyod`); `ScoreGroupBatch` mirrors the joint-mode `has_model` → abort → `get_model` → `score_batch` → `is_anomaly` → build-one-Verdict idiom exactly, with `per_member`/`contributions` left empty.
- Classic N>=3 `peer_divergence` (`PeerDivergenceDetector`) and joint-mode (`ecod`/`copod`/`pca`/`iforest`) servicer paths are behaviorally unchanged — confirmed by the full existing test suites passing.

## Task Commits

Each task was committed atomically:

1. **Task 1: Add PyODDetector.is_anomaly() and create PairwiseDeltaDetector** - `ca24f50` (feat)
2. **Task 2: Add len==2 sub-branch to servicer ScoreGroupBatch and FitGroup** - `35efb65` (feat)

**Plan metadata:** (this commit, docs: complete plan)

## Files Created/Modified
- `detector/argus_detector/pyod_detector.py` - added `is_anomaly(score)` public accessor
- `detector/argus_detector/group/pairwise_delta.py` - NEW: `PairwiseDeltaDetector` (delta computation + PyODDetector delegation)
- `detector/argus_detector/servicer.py` - NEW `len(request.series) == 2` sub-branches in `FitGroup`/`ScoreGroupBatch` for `peer_divergence`
- `detector/tests/test_pairwise_delta.py` - NEW: unit coverage for delta/fit/score/is_anomaly/from_params + servicer-level 2-member routing tests
- `detector/tests/test_servicer.py` - `TestScoreGroupBatchFloor`'s fixture changed from 2→1 member (see Deviations)

## Decisions Made
See `key-decisions` in frontmatter. In short: delegate-don't-reimplement for the ML, `save_pyod` over `save_group_bundle` for persistence (no scaler needed), and empty attribution by design (mathematically unavailable, never fabricated).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug/Test correctness] Updated a pre-existing test that asserted the exact old behavior this plan intentionally replaces**
- **Found during:** Task 2 verification run
- **Issue:** `test_servicer.py::TestScoreGroupBatchFloor::test_below_floor_returns_no_verdict` used a 2-member fixture to assert the OLD "below floor → no verdict" behavior at the servicer level. Once the new `len(request.series) == 2` branch was added, this same fixture now correctly aborts `INVALID_ARGUMENT` (no fitted model yet) instead of returning the old no-verdict response — the plan's own behavior spec requires this new outcome for 2-member requests.
- **Fix:** Changed the fixture from `_PEER_SERIES[:2]` to `_PEER_SERIES[:1]` (genuinely below the classic floor, and not caught by the new `len == 2` branch), preserving the test's original intent (exercise the classic `PeerDivergenceDetector` floor-of-3 no-verdict path at the servicer level). Added dedicated new tests in `test_pairwise_delta.py` (`TestServicerPairwiseDeltaRouting`) covering the new 2-member behavior explicitly: abort-before-fit, fit-then-score returning a populated `group_verdict` with empty `per_member`, and `save_pyod`-based persistence.
- **Files modified:** `detector/tests/test_servicer.py`
- **Verification:** `pytest tests/test_pairwise_delta.py tests/test_servicer.py tests/test_peer_divergence.py tests/test_pyod_detector.py` — 66 passed.
- **Committed in:** `35efb65` (Task 2 commit)

**2. [Rule 3 - Blocking] Generated missing Python gRPC proto stubs**
- **Found during:** Task 2 verification run
- **Issue:** This worktree had no `argus_pb2*.py` files under `detector/argus_detector/proto/` (gitignored generated artifacts, not present in a fresh worktree checkout) — `test_servicer.py` failed to import.
- **Fix:** Ran the existing `detector/scripts/gen_proto.py` generator (no proto source changes; this plan makes no proto changes per its own scope).
- **Files modified:** none tracked by git (generated files are `.gitignore`d, confirmed via `git status`)
- **Verification:** Proto imports succeeded; all test files collected and ran.

---

**Total deviations:** 2 auto-fixed (1 test-correctness/Rule 1, 1 blocking/Rule 3)
**Impact on plan:** Both were necessary to make the plan's own required behavior (and its own listed verification command) actually pass. No scope creep — no proto changes, no unrelated code touched.

## Issues Encountered
None beyond the deviations above.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Plan 09-03 (.NET orchestrator wiring) can now rely on: 2-member `peer_divergence` `ScoreGroupBatch` returns `group_verdict` populated with `per_member`/`contributions` both empty; `FitGroup` for 2-member `peer_divergence` groups is no longer a no-op (the C#-side `RunNightlyFitAsync` skip-all-peer_divergence guard, per 09-RESEARCH.md Pitfall 5, must be removed for this to ever be reached in production).
- Classic N>=3 `peer_divergence` and joint-mode (`ecod`/`copod`/`pca`/`iforest`) paths verified unchanged; `test_peer_divergence.py` (including its locked `TestPeerDivergenceFloor::test_below_floor_returns_no_verdict`) is untouched (confirmed via `git diff` against the plan's base commit).
- No blockers for 09-03.

---
*Phase: 09-2-member-groups-algorithm-guidance-correction*
*Completed: 2026-07-03*
