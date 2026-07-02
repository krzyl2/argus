---
phase: 05-group-detection-core-proto-python-detectors
plan: 02
subsystem: detector
tags: [peer-divergence, robust-statistics, numpy, group-detection]
dependency-graph:
  requires: []
  provides:
    - "PeerDivergenceDetector (detector/argus_detector/group/peer_divergence.py)"
    - "modified_zscore, score_group module functions"
  affects:
    - "Plan 05-03 (proto contract will carry group matrices to this detector)"
    - "Plan 05-04 (registry/servicer wiring will dispatch 'peer_divergence' to this class)"
tech-stack:
  added: []
  patterns:
    - "Stateless detector, tuple-return contract (scores, flags, error) — mirrors StlDetector's (scores, error) shape"
    - "MAD=0 -> meanAD fallback -> all-zeros guard chain (Iglewicz-Hoaglin robust statistics)"
key-files:
  created:
    - detector/argus_detector/group/__init__.py
    - detector/argus_detector/group/peer_divergence.py
    - detector/tests/test_peer_divergence.py
  modified: []
decisions:
  - "0.7979 meanAD-fallback constant documented as Iglewicz-Hoaglin statistics convention in code comment (RESEARCH A2/Open Question 1 resolved as Claude's discretion, per plan instruction)"
  - "Below-floor no-verdict (scores=None, error set) kept representationally distinct from MAD=0 all-normal (concrete 0.0 scores, no error) per RESEARCH Pitfall 4"
metrics:
  duration: "6m"
  completed: 2026-07-02
status: complete
---

# Phase 5 Plan 2: Peer-Divergence Detector Summary

Stateless numpy peer-divergence scorer using the modified z-score `0.6745*(x-median)/MAD` across group members per timestamp, with a `<3`-member no-verdict floor and a MAD=0 meanAD fallback.

## What Was Built

- **`detector/argus_detector/group/__init__.py`** — new empty package marker for the `group` subpackage.
- **`detector/argus_detector/group/peer_divergence.py`** — `modified_zscore(row)` and `score_group(matrix)` module functions copied verbatim from `05-RESEARCH.md` Code Examples (numerically pre-verified), wrapped in a `PeerDivergenceDetector` class matching `StlDetector`'s stateless, no-`fit()` shape. `score_batch(matrix)` returns `(scores, flags, error)`:
  - `n_members < 3` → `(None, None, "insufficient members: got N, need >= 3")` — explicit no-verdict (GRP-04).
  - Valid matrix → `(scores, flags, None)`, both shape `(n_timestamps, n_members)`, `flags = |z| > 3.5`.
  - `MAD == 0` but `meanAD > 0` → falls back to `0.7979 * (x-median)/meanAD` (Iglewicz-Hoaglin meanAD constant), documented in a code comment as a statistics convention.
  - `MAD == 0` and `meanAD == 0` (all-identical) → concrete `0.0` scores, never `NaN`.
- **`detector/tests/test_peer_divergence.py`** — `TestPeerDivergenceScoring`, `TestPeerDivergenceFloor`, `TestPeerDivergenceEdgeCases` (9 tests total), mirroring `test_pyod_detector.py`'s class-grouped style. Uses the hand-verified `[10,10,10,50]` fixture from RESEARCH for the MAD=0/meanAD case (expected outlier z ≈ 3.1916, confirmed by test assertion `abs=1e-3`). Explicitly asserts no `RuntimeWarning` is raised on the MAD=0 divide path (`warnings.simplefilter("error", RuntimeWarning)`).

## Verification

- `python -m pytest detector/tests/test_peer_divergence.py -x -q` → 9 passed.
- Full detector suite (`python -m pytest detector/tests/ -q`) → 129 passed, 8 pre-existing warnings (all from unrelated files: `test_pyod_detector.py`, `test_registry.py`, `test_servicer.py` — PyOD's own `MAD.decision_function` internals, out of scope for this plan).
- Manual smoke check confirmed floor case (`score_batch([[10.0,10.0]])` → error set, scores None) and outlier-flagging on a 4-member matrix with one clear divergent value.

## Deviations from Plan

None — plan executed exactly as written. Code copied verbatim from `05-RESEARCH.md` Code Examples as instructed; no constants re-derived.

## Decisions Made

- **0.7979 meanAD constant**: documented in-code as the Iglewicz-Hoaglin meanAD-fallback convention (matches RESEARCH Open Question 1's recommendation to treat this as Claude's discretion, not requiring separate user sign-off).
- **Representational distinction**: below-floor no-verdict (`scores=None`, error string) is never conflated with the MAD=0 all-normal case (concrete `0.0` scores, `error=None`) — enforced by two separate test classes (`TestPeerDivergenceFloor` vs `TestPeerDivergenceEdgeCases`).

## Known Stubs

None — this plan's scope is fully self-contained (stateless module + tests), no wiring into servicer/registry/proto occurs until Plan 05-04/05-03.

## Threat Flags

None — no new network endpoints, auth paths, or trust-boundary changes. `score_batch` operates purely on already-in-process Python data structures; input-shape validation (ragged series, empty groups) is explicitly deferred to the servicer boundary per the plan's threat model (T-05-03/T-05-04 both marked `mitigate` and both addressed by the guards implemented here — MAD=0 divide-by-zero guard and below-floor no-verdict).

## Self-Check: PASSED

- `detector/argus_detector/group/__init__.py` — FOUND
- `detector/argus_detector/group/peer_divergence.py` — FOUND
- `detector/tests/test_peer_divergence.py` — FOUND
- Commit `d0c669d` (feat: PeerDivergenceDetector) — FOUND
- Commit `fc52ca7` (test: peer-divergence tests) — FOUND
