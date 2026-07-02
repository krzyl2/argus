---
phase: 05-group-detection-core-proto-python-detectors
plan: 03
subsystem: detector
tags: [pyod, scikit-learn, robustscaler, ecod, copod, pca, iforest, joblib, model-store]

# Dependency graph
requires:
  - phase: 05-group-detection-core-proto-python-detectors (05-01, 05-02)
    provides: proto GroupScoreRequest/Response contract, peer-divergence detector (this plan is independent wave-1 work alongside them)
provides:
  - GroupMultivariateDetector class (RobustScaler + PyOD ECOD/COPOD/PCA/IForest wrapper)
  - ModelStore.save_group_bundle / load_group_bundle + group_slug() key-builder helper
  - Explicit scikit-learn==1.8.0 pin in detector/requirements.txt
affects: [05-04 (servicer FitGroup/ScoreGroupBatch wiring), Phase 8 (algorithm chooser, attribution UI)]

# Tech tracking
tech-stack:
  added: ["scikit-learn==1.8.0 (explicit direct pin; was transitive via PyOD)"]
  patterns:
    - "PyOD wrapper mirrors PyODDetector: fit()/score_batch()/is_fitted, same ValueError guard message"
    - "Lazy-import _DETECTOR_FACTORY dict for per-branch PyOD submodule imports"
    - "joblib bundle dict ({scaler, detector, name}) persisted as one object via ModelStore"
    - "Single group_slug() helper is the sole group_ prefix builder — never string-formatted ad hoc"

key-files:
  created:
    - detector/argus_detector/group/multivariate_detector.py
    - detector/tests/test_group_multivariate.py
    - detector/tests/test_group_model_store.py
  modified:
    - detector/requirements.txt
    - detector/argus_detector/model_store.py

key-decisions:
  - "Extended the joint-anomaly test fixture from RESEARCH.md's 5 rows to 10 rows (same correlated pattern) — PCA/IForest produced degenerate near-zero residual variance / divide-by-zero on the tiny 5-row fit; ECOD/COPOD were fine at 5 rows. 10 rows made all four detectors rank the joint anomaly correctly without warnings."
  - "group_slug(group_id) implemented as a plain module-level function (not a ModelStore static method) — matches the plan's 'single group_slug helper' requirement while keeping the call site trivial (group_slug(x) vs ModelStore.group_slug(x))."

requirements-completed: [GRP-05, GRP-06, GRP-07]

# Metrics
duration: 12min
completed: 2026-07-02
status: complete
---

# Phase 5 Plan 3: Group Multivariate Detector + ModelStore Extension Summary

**GroupMultivariateDetector (RobustScaler + PyOD ECOD/COPOD/PCA/IForest) with joblib bundle persistence via ModelStore.save_group_bundle/load_group_bundle, keyed under a group_ namespace that never collides with per-entity model keys**

## Performance

- **Duration:** 12 min
- **Started:** 2026-07-02T12:12:31Z
- **Completed:** 2026-07-02T12:17:58Z
- **Tasks:** 3
- **Files modified:** 5

## Accomplishments
- `GroupMultivariateDetector` wraps RobustScaler + PyOD (ECOD/COPOD/PCA/IForest) with a common `fit()`/`score_batch()`/`is_fitted` contract mirroring `PyODDetector`
- Joint-multivariate detection verified to catch a jointly-abnormal vector that no single feature would flag on its own, across all 4 detectors
- Mixed-unit (hPa + %RH) fixture proves RobustScaler prevents one feature from dominating — confirmed via `scaler.center_` asserting per-feature medians
- ECOD/COPOD return ranked per-feature attribution (`det.O` tail slice, read synchronously); PCA/IForest correctly return `None` (no fabricated attribution)
- `ModelStore.save_group_bundle`/`load_group_bundle` extend the existing versioned joblib persistence pattern unchanged (same `_model_dir`, `_update_latest`, `_prune`, `version.json` sidecar) under a `group_` prefixed slug
- `group_slug()` is the single, explicit prefix-builder — collision test confirms group and per-entity keys never accidentally overwrite each other's directory contents

## Task Commits

Each task was committed atomically:

1. **Task 1: Pin scikit-learn and implement GroupMultivariateDetector** - `e16b687` (feat)
2. **Task 2: Extend ModelStore with group bundle save/load** - `0e27622` (feat)
3. **Task 3: Test joint detection, mixed units, attribution, and group persistence** - `84447c3` (test)

## Files Created/Modified
- `detector/argus_detector/group/multivariate_detector.py` - `GroupMultivariateDetector` class (RobustScaler + PyOD wrapper, bundle/from_bundle, attribution extraction)
- `detector/argus_detector/model_store.py` - `group_slug()` helper + `save_group_bundle`/`load_group_bundle` methods
- `detector/requirements.txt` - added explicit `scikit-learn==1.8.0` pin
- `detector/tests/test_group_multivariate.py` - joint-anomaly, mixed-units, attribution, fit-guard, bundle round-trip tests (20 tests)
- `detector/tests/test_group_model_store.py` - save/load round-trip, prune, group_ prefix collision, regression-guard tests (14 tests)

## Decisions Made
- Extended the RESEARCH.md 5-row joint-anomaly training fixture to 10 rows (same correlation pattern, just more samples) after PCA/IForest produced `RuntimeWarning: divide by zero` and non-deterministic tie-breaks on the tiny original fixture. ECOD/COPOD attribution and scoring were unaffected either way; only the cross-detector parametrized ranking assertion needed the larger fixture. This is a test-fixture-only change — production code paths are identical to RESEARCH.md's verified implementation, copied verbatim.
- `group_slug` implemented as a module-level function rather than a `ModelStore` static/class method — simpler call sites, same single-source-of-truth guarantee.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Enlarged joint-anomaly test fixture to avoid PCA/COPOD near-tie/divide-by-zero on 5-row training data**
- **Found during:** Task 3 (test authoring) — running the parametrized joint-anomaly test across all 4 detectors against RESEARCH.md's literal 5-row `X_train` fixture
- **Issue:** PCA raised `RuntimeWarning: divide by zero encountered in divide` and produced `inf > inf` (assertion failure); COPOD produced a near-exact tie (`1.0986122886681096 > 1.0986122886681098`, failing by float precision) — both due to the training set being too small (5 rows, 2 features) for PCA's residual-variance computation and COPOD's empirical CDF resolution
- **Fix:** Extended the training fixture to 10 rows following the identical correlated pattern (same relationship between the two features, just more samples) — verified by direct execution that all 4 detectors then correctly rank the joint anomaly above the in-distribution point with no warnings
- **Files modified:** detector/tests/test_group_multivariate.py
- **Verification:** `pytest tests/test_group_multivariate.py -q` — 20/20 pass, zero warnings
- **Committed in:** 84447c3 (Task 3 commit)

---

**Total deviations:** 1 auto-fixed (1 bug — test fixture sizing, not production code)
**Impact on plan:** No production code changed from RESEARCH.md's verified implementation. Only the test's training-data fixture size was adjusted to make the joint-anomaly assertion deterministic across all 4 detector algorithms. No scope creep.

## Issues Encountered
None beyond the fixture-sizing deviation above.

## User Setup Required
None - no external service configuration required. `scikit-learn` was already present in the environment as a PyOD transitive dependency; the explicit pin only updates `requirements.txt`, no new install action needed locally (already verified present via `pip show`).

## Next Phase Readiness
- `GroupMultivariateDetector` and `ModelStore.save_group_bundle`/`load_group_bundle` are ready for `servicer.py`'s `FitGroup`/`ScoreGroupBatch` handlers (Plan 05-04) to wire up
- `registry.py` factory dispatch (adding `"ecod"`/`"copod"`/`"pca"`/`"iforest"`/`"peer_divergence"` branches) is the next integration point, per 05-PATTERNS.md
- Full detector test suite (163 tests) passes with no regressions

---
*Phase: 05-group-detection-core-proto-python-detectors*
*Completed: 2026-07-02*

## Self-Check: PASSED
