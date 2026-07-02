---
phase: 05-group-detection-core-proto-python-detectors
verified: 2026-07-02T00:00:00Z
status: passed
score: 4/4 must-haves verified
behavior_unverified: 0
overrides_applied: 0
---

# Phase 5: Group Detection Core (Proto + Python Detectors) Verification Report

**Phase Goal:** Peer-divergence and joint-multivariate detection produce correct, independently-verifiable scores at the Python/proto layer before any orchestrator or UI code depends on them.
**Verified:** 2026-07-02
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths (ROADMAP Success Criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Peer-divergence identifies which member diverges from group's robust median/MAD consensus; no verdict below min-member floor (3) | ✓ VERIFIED | `detector/argus_detector/group/peer_divergence.py:35-56` implements `0.6745*(x-median)/MAD` modified z-score; `_MIN_MEMBERS=3` guard at line 108 returns `(None, None, "insufficient members...")` for n<3. Tests `TestPeerDivergenceFloor::test_below_floor_returns_no_verdict`, `test_single_member_returns_no_verdict`, `test_exactly_min_members_returns_verdict` and servicer-level `TestScoreGroupBatchFloor::test_below_floor_returns_no_verdict` all pass (ran individually, not just full-suite). Servicer (`servicer.py:252-254`) returns `ok=True, error=...` with empty `per_member` rather than a false not-anomalous verdict (GRP-04). |
| 2 | Joint-multivariate (PyOD PCA/ECOD/COPOD/IForest) flags jointly-abnormal mixed-unit vectors without one feature dominating — RobustScaler applied before fitting and persisted with the model | ✓ VERIFIED | `multivariate_detector.py:66-75` fits `RobustScaler` then the PyOD model on scaled data; `bundle()` (line 123-125) persists `{scaler, detector, name}` together. `test_group_multivariate.py::TestGroupMultivariateDetectorJointAnomaly` (all 4 detectors, parametrized) proves a jointly-abnormal vector scores higher than an in-distribution one. `TestGroupMultivariateDetectorMixedUnits::test_robust_scaler_is_fit_on_mixed_units` proves the scaler's `center_` reflects each feature's own median (hPa ~1000 vs %RH ~45-55), not a shared/global scale. PCA constructed with `standardization=False` (line 40) so RobustScaler is the sole scaler (GRP-06, Pitfall 2). All targeted tests re-run individually and passed. |
| 3 | Proto contract carries a REAL 2D matrix (repeated Series), not a loop of univariate calls | ✓ VERIFIED | `proto/argus.proto:74-97`: `message Series { string member_id; repeated double values; }`, `GroupScoreRequest.repeated Series series`. `test_proto_codegen.py::test_series_roundtrips_member_id_and_values` confirms field round-trip. Detector code (`multivariate_detector.py:70-75`, `peer_divergence.py:106-116`) fits/scores the full 2D matrix in one call — no per-member loop over `ScoreBatch`. `servicer.py:242` builds the matrix via `zip(*(s.values for s in request.series))`, a single transpose, not N sequential univariate calls. |
| 4 | Group models Fit/Save/Load keyed `group_{group_id}__{detector}__v{version}`, never colliding with per-entity keys | ✓ VERIFIED (see note) | `model_store.py:47-54` `group_slug(group_id)` returns `f"group_{group_id}"`, the sole prefix builder, used by `save_group_bundle`/`load_group_bundle` (lines 163, 188) with the existing versioned-directory shape `{slug}/{detector}/v{N}/`. `test_group_model_store.py::TestModelStoreGroupPrefixCollision` explicitly tests the documented edge case (pathological entity literally named `group_x`) and proves the store does not silently merge two different bundle dicts. **Note:** actual key shape is a nested path (`group_{id}/{detector}/v{N}`) rather than the flat `group_{id}__{detector}__v{N}` string mentioned in 05-CONTEXT.md — functionally equivalent (same three discriminating components, mirrors the pre-existing per-entity `{slug}/{detector}/v{N}` convention) and does not change the collision-avoidance property. Not a gap; documented below under Anti-Patterns/Notes for awareness. |

**Score:** 4/4 truths verified (0 present, behavior-unverified)

### PLAN Frontmatter Must-Haves (merged, superset of above)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 5 | gRPC caller can construct GroupScoreRequest with multiple Series (05-01) | ✓ VERIFIED | `test_proto_codegen.py` — Series message + round-trip test pass |
| 6 | Regenerated Python stubs import without ImportError (05-01) | ✓ VERIFIED | `gen_proto.py` regen + `test_proto_codegen.py` (12/12 pass) |
| 7 | DetectorService stub advertises ScoreGroupBatch/FitGroup alongside existing RPCs (05-01) | ✓ VERIFIED | `argus.proto:111-119` — 5 original RPCs untouched, 2 new RPCs added; `test_detector_service_stub_exposes_group_rpcs` passes |
| 8 | MAD=0 all-identical returns concrete zeros, not NaN, distinct from below-floor (05-02) | ✓ VERIFIED | `peer_divergence.py:48-55`; `TestPeerDivergenceEdgeCases::test_all_identical_returns_zeros_not_nan`, `test_mad_zero_meanad_fallback_flags_outlier`, `test_no_runtime_warning_on_mad_zero_path` all pass |
| 9 | ECOD/COPOD emit ranked contributions from `det.O[-len(X_new):]`; PCA/IForest return none (05-03) | ✓ VERIFIED | `multivariate_detector.py:96-103`; `TestGroupMultivariateDetectorAttribution` (parametrized ecod/copod + pca/iforest-none case) passes |
| 10 | Ragged Series matrix / empty group_id rejected with INVALID_ARGUMENT (05-04) | ✓ VERIFIED | `servicer.py:215-237, 333-350`; guards for empty group_id, unknown detector, ragged series, AND empty series list (WR-01 fix) all abort INVALID_ARGUMENT before matrix construction. `TestScoreGroupBatchGuards` (4 tests) + `TestFitGroupPersistence` empty-input tests pass |

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `proto/argus.proto` | Series/GroupScoreRequest/GroupScoreResponse/FitGroupRequest/FitGroupResponse + 2 RPCs | ✓ VERIFIED | All 6 messages + 2 RPCs present; 5 original RPCs and field numbers unchanged |
| `detector/argus_detector/proto/argus_pb2.py` (+ grpc/pyi) | Regenerated stubs | ✓ VERIFIED | Regenerates cleanly via `gen_proto.py`; gitignored build artifact, confirmed present at verification time |
| `detector/tests/test_proto_codegen.py` | Group message/RPC assertions | ✓ VERIFIED | 12/12 tests pass |
| `detector/argus_detector/group/peer_divergence.py` | `PeerDivergenceDetector` class, ≥40 lines | ✓ VERIFIED | 117 lines; class present with `score_batch` tuple contract |
| `detector/tests/test_peer_divergence.py` | Scoring/floor/edge-case tests | ✓ VERIFIED | `TestPeerDivergenceScoring`/`Floor`/`EdgeCases` — 9 tests pass |
| `detector/argus_detector/group/multivariate_detector.py` | `GroupMultivariateDetector`, ≥50 lines | ✓ VERIFIED | 135 lines; bundle()/from_bundle()/is_anomaly() all present |
| `detector/argus_detector/model_store.py` | `save_group_bundle`/`load_group_bundle`/`group_slug` | ✓ VERIFIED | All three present, mirror existing `save_pyod`/`load_pyod` pattern |
| `detector/requirements.txt` | scikit-learn pin | ✓ VERIFIED | `scikit-learn==1.8.0` explicit pin present |
| `detector/tests/test_group_multivariate.py` / `test_group_model_store.py` | Joint/mixed-unit/attribution/collision tests | ✓ VERIFIED | 20 + 14 tests respectively, all pass |
| `detector/argus_detector/servicer.py` | `ScoreGroupBatch`/`FitGroup` handlers | ✓ VERIFIED | Both handlers implemented with validation, dispatch, response building |
| `detector/argus_detector/registry.py` | Group factory branches + stateless peer_divergence fit path | ✓ VERIFIED | `_create_detector` branches for all 5 group detector names; `fit_one` no-fit path extended to `peer_divergence` |
| `detector/tests/test_servicer.py` | Group RPC handler tests | ✓ VERIFIED | 5 new test classes (peer/floor/joint/guards/persistence), 10+ new tests pass |

### Key Link Verification

| From | To | Via | Status | Details |
|------|-----|-----|--------|---------|
| `argus_pb2_grpc.py` | `proto/argus.proto` | gen_proto.py regen of DetectorServiceStub | ✓ WIRED | Pattern `ScoreGroupBatch` found in regenerated source |
| `test_peer_divergence.py` | `group/peer_divergence.py` | imports + exercises `score_batch` | ✓ WIRED | Pattern found |
| `model_store.py` | `group/multivariate_detector.py` | `save_group_bundle` persists `bundle()` dict | ✓ WIRED | Pattern found |
| `servicer.py` | `group/multivariate_detector.py` | FitGroup/ScoreGroupBatch dispatch via registry factory | ✓ WIRED | Pattern found |
| `servicer.py` | `model_store.py` | FitGroup persists via `save_group_bundle` | ✓ WIRED | Pattern found |

### Behavioral Spot-Checks (independent-verifiability tests, run individually)

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Joint-anomaly-no-single-feature (all 4 detectors) | `pytest tests/test_group_multivariate.py::TestGroupMultivariateDetectorJointAnomaly -v` | 4/4 passed | ✓ PASS |
| Mixed-unit (hPa/%RH) RobustScaler proof | `pytest tests/test_group_multivariate.py::TestGroupMultivariateDetectorMixedUnits -v` | 5/5 passed | ✓ PASS |
| Below-floor (<3 members) no-verdict (unit) | `pytest tests/test_peer_divergence.py::TestPeerDivergenceFloor -v` | 3/3 passed | ✓ PASS |
| Below-floor no-verdict (servicer/RPC level) | `pytest tests/test_servicer.py::TestScoreGroupBatchFloor -v` | 1/1 passed | ✓ PASS |
| Group_ prefix collision guard | `pytest tests/test_group_model_store.py::TestModelStoreGroupPrefixCollision -v` | 2/2 passed | ✓ PASS |
| Joint RPC end-to-end (Fit + Score) | `pytest tests/test_servicer.py::TestScoreGroupBatchJoint -v` | 1/1 passed | ✓ PASS |
| Full detector suite (single run) | `cd detector && python -m pytest -q` | 177 passed, 0 failed, 8 pre-existing unrelated warnings | ✓ PASS |
| Proto codegen round-trip | `pytest tests/test_proto_codegen.py -q` | 12/12 passed | ✓ PASS |

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| GRP-03 | 05-01, 05-02, 05-04 | Peer-divergence flags WHICH member diverges via robust median/MAD | ✓ SATISFIED | `peer_divergence.py` + servicer per-member Verdict building |
| GRP-04 | 05-02, 05-04 | Min-member-count floor, safe degradation | ✓ SATISFIED | `_MIN_MEMBERS=3` guard, no-verdict path at both unit and RPC level |
| GRP-05 | 05-01, 05-03, 05-04 | Joint-multivariate flags jointly-abnormal vector, single group-level verdict | ✓ SATISFIED | `multivariate_detector.py` + `group_verdict` in servicer |
| GRP-06 | 05-03, 05-04 | Feature scaling before fitting, scaler persisted | ✓ SATISFIED | RobustScaler in `fit()`, persisted in `bundle()` |
| GRP-07 | 05-01, 05-03, 05-04 | Group Fit/Save/Load lifecycle, no per-entity key collision | ✓ SATISFIED | `group_slug()` + `save_group_bundle`/`load_group_bundle`; collision edge case explicitly tested |

REQUIREMENTS.md cross-reference: all 5 phase-5 requirement IDs (GRP-03..07) marked `[x]` and status `Complete` in REQUIREMENTS.md, and all 5 are declared in at least one plan's `requirements:` frontmatter (confirmed in 05-PLAN-CHECK.md Dimension 1). No orphaned requirements for Phase 5.

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `detector/argus_detector/servicer.py` | 240, 353 | `group_slug = f"group_{request.group_id}"` inlined instead of calling `model_store.group_slug()` helper | ℹ️ Info | Plan 05-04 prohibited "string-format the group_ model key ad hoc in multiple places — build it through a single group_slug helper." The servicer builds the identical string inline in two places rather than importing and calling `model_store.group_slug()`. Functionally identical output (both produce `f"group_{group_id}"`), no correctness bug, but is a soft violation of the stated single-source-of-truth convention. Does not block the phase goal — the format is consistent everywhere it's used, tests pass, and both string constructions produce the same value that model_store.py's helper produces. |

No blocker-level anti-patterns (TBD/FIXME/XXX) found in phase-5-modified files. One pre-existing `TODO(plan06)` exists in `servicer.py` line 46 but belongs to unrelated Phase-1-era `ScoreStream` code, not touched by this phase's diff.

### Human Verification Required

None. This is a Python/proto-layer-only phase with no UI and no .NET consumer code (explicitly out of scope per CONTEXT.md and ROADMAP). All success criteria are code-and-test verifiable, and all targeted tests were re-run individually (not just trusted from SUMMARY.md) to confirm they pass.

### Gaps Summary

No blocking gaps. All 4 ROADMAP success criteria and all 10 PLAN-frontmatter must-haves are verified against actual code and passing tests (177/177 full suite, plus 16 targeted independent-verifiability tests re-run individually). The one code-review-identified deviation (inlined `group_slug` string in servicer.py instead of calling the shared helper) is an info-level convention gap, not a correctness or goal-blocking issue — it does not affect the collision-avoidance property, which is separately and directly tested in `test_group_model_store.py`.

The model-key shape documented in 05-CONTEXT.md (`group_{group_id}__{detector}__v{version}`) was implemented as a nested directory path (`group_{group_id}/{detector}/v{version}`) rather than a flat double-underscore string — this preserves the same three discriminating components and mirrors the pre-existing per-entity model layout exactly, so it satisfies the intent of success criterion 4 without altering behavior.

---

_Verified: 2026-07-02_
_Verifier: Claude (gsd-verifier)_
