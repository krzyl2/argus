---
phase: 08-group-config-ui-algorithm-chooser
plan: 01
subsystem: detector
tags: [pyod, python, grpc, group-detection, anomaly-detection]

# Dependency graph
requires:
  - phase: 05-group-detection-core-proto-python-detectors
    provides: peer_divergence.py, multivariate_detector.py, registry.py, servicer.py group RPC plumbing (ScoreGroupBatch, FitGroup)
provides:
  - PeerDivergenceDetector.from_params(threshold) — genuine tunable flag boundary, no longer a hardcoded 3.5 module constant
  - GroupMultivariateDetector(name, params) — contamination (all 4 PyOD variants) + n_estimators (iforest) honored
  - registry._create_detector / fit_one optional params dict, threaded into both group branches
  - servicer.py ScoreGroupBatch + FitGroup forward dict(request.params) into group detector construction
affects: [08-02-detector-catalog-and-presets, 08-03, 08-04]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "from_params(params: dict[str, str]) classmethod + module-local _cast_float/_cast_int helper duplicated per detector file (codebase convention, not a shared module)"

key-files:
  created: []
  modified:
    - detector/argus_detector/group/peer_divergence.py
    - detector/argus_detector/group/multivariate_detector.py
    - detector/argus_detector/registry.py
    - detector/argus_detector/servicer.py
    - detector/tests/test_peer_divergence.py
    - detector/tests/test_group_multivariate.py
    - detector/tests/test_servicer.py

key-decisions:
  - "peer_divergence threshold moved from a module constant read inside score_group to an instance field on PeerDivergenceDetector, set via from_params — score_group/score_batch now take/pass the threshold explicitly"
  - "multivariate _DETECTOR_FACTORY lambdas changed to accept a params dict and read contamination (all 4) / n_estimators (iforest) via the pyod_detector.py _cast_float idiom, duplicated locally per Rule 2 (no new shared module)"
  - "PCA standardization=False stays hardcoded — documented as a correctness constant, not a tunable knob (RESEARCH Pitfall 2)"
  - "registry._create_detector and fit_one both gained an optional params dict (default None) — mirrors the D-06-01 optional-3rd-param precedent, all existing per-entity call sites unchanged"
  - "iforest contamination test excluded from the strict raw-score-identity assertion (separate fit() calls are stochastic via unseeded random_state); added a same-instance-mutated-contamination test instead to isolate contamination's effect on threshold_ from fit-to-fit randomness"

patterns-established:
  - "Score-vs-threshold honesty test pattern: assert decision_function scores are pytest.approx-equal across two contamination values while asserting _model.threshold_ differs — encodes RESEARCH Pitfall 2 for future catalog/preset plans"

requirements-completed: [ALGO-01, ALGO-02]

# Metrics
duration: 6min
completed: 2026-07-02
status: complete
---

# Phase 08 Plan 01: Param-aware group detectors + request.params threading Summary

**peer_divergence.threshold and multivariate contamination/n_estimators are now real, request.params-driven knobs — presets built in later plans will genuinely change detection instead of being cosmetic.**

## Performance

- **Duration:** 6 min
- **Started:** 2026-07-02T19:03:24Z
- **Completed:** 2026-07-02T19:09:27Z
- **Tasks:** 2
- **Files modified:** 7

## Accomplishments
- `PeerDivergenceDetector.from_params({"threshold": "..."})` replaces the hardcoded `_THRESHOLD = 3.5` read inside `score_group` — a lower threshold genuinely flags more members on identical data
- `GroupMultivariateDetector(name, params)` honors `contamination` for all four PyOD variants (ecod/copod/pca/iforest) and `n_estimators` for iforest, while proving `contamination` never moves `decision_function()`'s continuous score — only `threshold_`/`is_anomaly`
- `registry._create_detector` + `fit_one` thread an optional `params` dict into both group-detector construction branches; `servicer.py`'s `ScoreGroupBatch` and `FitGroup` (peer + joint paths) forward `dict(request.params)` end-to-end
- Malformed param values (non-numeric strings) fall back to defaults via the existing `_cast_float`/new `_cast_int` try/except idiom — never abort/500 the RPC

## Task Commits

Each task was committed atomically:

1. **Task 1: Param-aware peer_divergence + multivariate detectors** - `d12628c` (feat)
2. **Task 2: Thread request.params through registry + servicer** - `2c337f9` (feat)

**Plan metadata:** (this commit)

## Files Created/Modified
- `detector/argus_detector/group/peer_divergence.py` - `_cast_float` helper, `PeerDivergenceDetector.__init__(threshold)` + `from_params`, `score_group(matrix, threshold)` parametrized
- `detector/argus_detector/group/multivariate_detector.py` - `_cast_float`/`_cast_int` helpers, `_DETECTOR_FACTORY` lambdas now take `params` and construct PyOD models with `contamination`/`n_estimators`, `__init__(detector_name, params=None)`
- `detector/argus_detector/registry.py` - `_create_detector(detector, params=None)` and `fit_one(..., params=None)` thread params into the `peer_divergence` and `ecod/copod/pca/iforest` branches
- `detector/argus_detector/servicer.py` - `ScoreGroupBatch` peer branch constructs via `from_params(dict(request.params))`; `FitGroup` passes `params=dict(request.params)` on both the peer-register and joint-fit `fit_one` calls
- `detector/tests/test_peer_divergence.py` - `TestPeerDivergenceFromParams`: lower threshold flags more members, empty params matches pre-change default (regression guard), non-numeric threshold falls back safely
- `detector/tests/test_group_multivariate.py` - `TestGroupMultivariateDetectorParams`: contamination changes `is_anomaly`/`threshold_` but not `decision_function` scores (ecod/copod/pca via cross-instance fit; iforest via same-instance mutated-contamination to remove RNG confound), `n_estimators` honored/ignored correctly, bad values fall back to defaults
- `detector/tests/test_servicer.py` - `TestScoreGroupBatchParams`: `request.params` threshold reaches `ScoreGroupBatch` and changes flagged-member count; malformed threshold doesn't abort; `FitGroup` contamination param reaches the persisted bundle's model; malformed `FitGroup` params don't abort

## Decisions Made
- Kept `_THRESHOLD = 3.5` as the peer_divergence default constant (still referenced by `from_params`'s default and by `PeerDivergenceDetector.__init__`'s default arg) rather than removing it — matches the plan's explicit instruction ("keep `_THRESHOLD = 3.5` as the default constant").
- Excluded `iforest` from the strict cross-instance score-identity assertion in `test_group_multivariate.py` and added a same-instance-mutated-contamination variant instead — `IForest.decision_function()` is inherently stochastic across separate `fit()` calls with an unseeded `random_state`, which the production factory deliberately leaves unseeded (not requested by the plan, not silently masked either — the honest test isolates contamination's effect on `threshold_` from fit-to-fit randomness by mutating `contamination` and calling PyOD's internal `_process_decision_scores()` on the same fitted model).

## Deviations from Plan

None - plan executed exactly as written. The iforest test-fixture adjustment above is a test-design choice necessitated by IForest's inherent randomness, not a deviation from any plan instruction (the plan's `<behavior>` bullet for multivariate contamination did not carve out iforest, but also did not mandate a specific test mechanism — the chosen approach preserves the exact assertion the plan asked for: contamination changes `is_anomaly`, not the continuous score).

## Issues Encountered
- Initial `test_contamination_changes_is_anomaly_not_score` fixture applied to `iforest` failed because two separately-`fit()`-ed `IForest` instances produce different `decision_function()` scores even with identical `contamination`, due to unseeded `random_state`. Resolved by parametrizing the cross-instance test to `ecod`/`copod`/`pca` only and adding a dedicated `test_iforest_contamination_changes_threshold_only` that mutates `contamination` on one already-fitted model and recomputes `threshold_` via PyOD's own `_process_decision_scores()`, proving the score is untouched while the threshold moves.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- Param contract locked and delivered exactly as specified in the plan's "Param contract" table: `threshold` (peer_divergence), `contamination` (ecod/copod/pca/iforest), `n_estimators` (iforest) — 08-02's `DetectorCatalog.cs` preset mapping can use these key names verbatim.
- Full detector suite green (193 passed) — no per-entity regressions from the optional-params threading.
- No blockers for 08-02 (detector catalog UI).

---
*Phase: 08-group-config-ui-algorithm-chooser*
*Completed: 2026-07-02*

## Self-Check: PASSED
