---
phase: 05-group-detection-core-proto-python-detectors
fixed_at: 2026-07-02T12:45:00Z
review_path: .planning/phases/05-group-detection-core-proto-python-detectors/05-REVIEW.md
iteration: 1
findings_in_scope: 3
fixed: 3
skipped: 0
status: all_fixed
---

# Phase 05: Code Review Fix Report

**Fixed at:** 2026-07-02T12:45:00Z
**Source review:** .planning/phases/05-group-detection-core-proto-python-detectors/05-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 3 (WR-01, WR-02, WR-03 — Info findings IN-01/02/03 excluded per fix_scope)
- Fixed: 3
- Skipped: 0

## Fixed Issues

### WR-01: Pusta lista `series` omija walidację i ląduje jako niekontrolowany `ok=False` zamiast `INVALID_ARGUMENT`

**Files modified:** `detector/argus_detector/servicer.py`, `detector/tests/test_servicer.py`
**Commit:** `0320740`
**Applied fix:** Added `not request.series or` to the ragged-length guard condition in both `ScoreGroupBatch` and `FitGroup`, so an empty `series` list now aborts with `INVALID_ARGUMENT` ("empty series list") instead of falling through to the matrix-construction code and raising an uncontrolled `ValueError` caught by the generic `except Exception`. Added `test_empty_series_aborts_invalid_argument` (ScoreGroupBatch) and `test_fit_group_empty_series_aborts_invalid_argument` (FitGroup) to `test_servicer.py`.

### WR-02: Servicer sięga po prywatny atrybut `model._model.threshold_`, łamiąc enkapsulację `GroupMultivariateDetector`

**Files modified:** `detector/argus_detector/group/multivariate_detector.py`, `detector/argus_detector/servicer.py`, `detector/tests/test_group_multivariate.py`
**Commit:** `bc21c75`
**Applied fix:** Added a public `is_anomaly(score: float) -> bool` method on `GroupMultivariateDetector` that encapsulates the `score > self._model.threshold_` comparison. `servicer.py`'s `ScoreGroupBatch` now calls `model.is_anomaly(group_score)` instead of reaching into `model._model.threshold_` directly. The new method does not call `predict()`/`decision_function()`, preserving the documented no-corrupt-attribution guarantee for ECOD/COPOD's mutable `self.O` matrix. Added two tests to `test_group_multivariate.py`: one asserting `is_anomaly()` matches the private-attribute comparison (regression guard for the encapsulation itself), and one asserting the return type is `bool`.

### WR-03: `score_group()` zawiera martwą gałąź NaN/bool, nieosiągalną przez publiczne API — duplikacja floor-check

**Files modified:** `detector/argus_detector/group/peer_divergence.py`
**Commit:** `a09ba83`
**Applied fix:** Removed the `n_members < _MIN_MEMBERS` branch from the module-level `score_group()` function, which previously returned `nan.astype(bool)` (misleadingly `True` — the inverse of the intended "no verdict" meaning) and duplicated the floor-check already performed by the sole production caller, `PeerDivergenceDetector.score_batch()`, before `score_group()` is ever invoked. Confirmed via grep that no other code (production or test) calls `score_group()` directly with `n_members < _MIN_MEMBERS`, so this is a pure dead-code removal with no behavior change on the reachable path. Docstring updated to document that floor enforcement is now solely `score_batch()`'s responsibility.

## Skipped Issues

None — all in-scope findings were fixed.

## Verification

Ran the full detector test suite after all three fixes: `cd detector && python -m pytest -q` → **177 passed**, 8 pre-existing warnings (unrelated `RuntimeWarning` from `pyod/models/mad.py` divide, present before this fix session), zero regressions.

Note: the fixer's isolated worktree did not have the generated proto stubs (`argus_pb2.py`/`argus_pb2_grpc.py`/`argus_pb2.pyi` are gitignored build artifacts) checked out; they were regenerated locally via `python detector/scripts/gen_proto.py` before running tests. No proto source changes were made — `proto/argus.proto` was untouched.

Info findings IN-01 (scikit-learn undocumented in CLAUDE.md stack table) and IN-02 (missing `encoding="utf-8"` in model_store.py) and IN-03 (Fit/FitGroup TOCTOU on next_version) were explicitly excluded per `fix_scope: critical+warning` and left untouched.

---

_Fixed: 2026-07-02T12:45:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
