---
phase: 05-group-detection-core-proto-python-detectors
plan: 01
subsystem: api
tags: [protobuf, grpc, python, codegen]

# Dependency graph
requires: []
provides:
  - "proto/argus.proto extended with Series, FeatureContribution, GroupScoreRequest, GroupScoreResponse, FitGroupRequest, FitGroupResponse messages"
  - "DetectorService.ScoreGroupBatch and DetectorService.FitGroup RPCs (additive, existing 5 RPCs unchanged)"
  - "Regenerated Python stubs (argus_pb2.py, argus_pb2_grpc.py, argus_pb2.pyi) exposing all new group symbols"
  - "test_proto_codegen.py assertions proving group messages and RPC stubs regenerate correctly"
affects: [05-02, 05-03, 05-04]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Group RPC dispatch on detector string alone (no separate mode enum) — mirrors existing ScoreBatchRequest.detector convention"

key-files:
  created: []
  modified:
    - proto/argus.proto
    - detector/tests/test_proto_codegen.py
    - detector/argus_detector/proto/argus_pb2.py (gitignored, regenerated)
    - detector/argus_detector/proto/argus_pb2_grpc.py (gitignored, regenerated)
    - detector/argus_detector/proto/argus_pb2.pyi (gitignored, regenerated)

key-decisions:
  - "Mode dispatch (peer-divergence vs joint-multivariate) is inferred server-side from the detector string field, no separate enum added to GroupScoreRequest"
  - "Reused existing Verdict message for both per_member and group_verdict fields instead of a parallel score message"

patterns-established:
  - "Series message: member_id + repeated double values on a shared, pre-aligned timestamp axis — the 2D matrix carrier for all group RPCs"

requirements-completed: [GRP-03, GRP-05, GRP-07]

# Metrics
duration: 6min
completed: 2026-07-02
status: complete
---

# Phase 5 Plan 1: Proto Contract + Python Codegen Summary

**Extended argus.proto with a real 2D-matrix group contract (Series/GroupScoreRequest/GroupScoreResponse/FitGroupRequest/FitGroupResponse) and two new DetectorService RPCs (ScoreGroupBatch, FitGroup), regenerated Python stubs, and proved the wire contract with codegen tests.**

## Performance

- **Duration:** 6 min
- **Started:** 2026-07-02T12:01:00Z
- **Completed:** 2026-07-02T12:07:01Z
- **Tasks:** 2 completed
- **Files modified:** 2 tracked (proto/argus.proto, detector/tests/test_proto_codegen.py) + 3 gitignored regenerated stub files

## Accomplishments
- Proto contract now carries a genuine 2D matrix (`repeated Series { member_id, repeated double values }`) instead of a univariate-loop workaround
- Two new RPCs (`ScoreGroupBatch`, `FitGroup`) added to `DetectorService` without touching any existing message field number or RPC
- Python stubs regenerated via `gen_proto.py` and verified importable with all six new message types plus both new RPC stub methods

## Task Commits

Each task was committed atomically:

1. **Task 1: Extend proto contract with group messages and RPCs** - `18f8845` (feat)
2. **Task 2: Regenerate Python stubs and extend codegen test** - `b7d3fae` (test)

**Plan metadata:** pending (docs: complete plan)

## Files Created/Modified
- `proto/argus.proto` - Added Series, FeatureContribution, GroupScoreRequest, GroupScoreResponse, FitGroupRequest, FitGroupResponse messages and ScoreGroupBatch/FitGroup RPCs
- `detector/tests/test_proto_codegen.py` - Added assertions for the six new group messages, a Series round-trip test, and a DetectorServiceStub group-RPC-callable test
- `detector/argus_detector/proto/argus_pb2.py`, `argus_pb2_grpc.py`, `argus_pb2.pyi` - Regenerated (gitignored per `.gitignore:19`, produced fresh by `gen_proto.py` on every run/test session)

## Decisions Made
- Dispatch peer-divergence vs. joint-multivariate mode server-side purely on the `detector` string field (e.g. `"peer_divergence"` vs `"ecod"`/`"copod"`/`"pca"`/`"iforest"`), matching Open Question 2's recommendation in 05-RESEARCH.md and the existing `ScoreBatchRequest.detector` pattern — no redundant mode enum added to the wire contract.
- Reused the existing `Verdict` message for both `per_member` (peer-divergence) and `group_verdict` (joint-multivariate) fields in `GroupScoreResponse`, per the plan's explicit instruction not to define a parallel score message.

## Deviations from Plan

None - plan executed exactly as written. The locked message shape from 05-PATTERNS.md (lines 63-104) was copied verbatim; no field numbers or RPCs beyond the plan's spec were touched.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Proto contract and Python stubs are ready for Wave 2 (peer-divergence and joint-multivariate detector implementations) and Wave 3 (servicer handlers) to import `Series`, `GroupScoreRequest`, `GroupScoreResponse`, `FitGroupRequest`, `FitGroupResponse`, and the `ScoreGroupBatch`/`FitGroup` RPC stubs without `ImportError`.
- No blockers. The five original RPCs and all existing message field numbers remain unchanged, verified by the existing codegen test suite still passing (12/12).

---
*Phase: 05-group-detection-core-proto-python-detectors*
*Completed: 2026-07-02*

## Self-Check: PASSED
