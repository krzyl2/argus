---
phase: 09-2-member-groups-algorithm-guidance-correction
fixed_at: 2026-07-03T10:30:00Z
review_path: .planning/phases/09-2-member-groups-algorithm-guidance-correction/09-REVIEW.md
iteration: 1
findings_in_scope: 4
fixed: 4
skipped: 0
status: all_fixed
---

# Phase 9: Code Review Fix Report

**Fixed at:** 2026-07-03T10:30:00Z
**Source review:** .planning/phases/09-2-member-groups-algorithm-guidance-correction/09-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 4 (critical_warning scope — IN-01 excluded)
- Fixed: 4
- Skipped: 0

## Fixed Issues

### CR-01: Registry key collision between classic peer_divergence and 2-member pairwise-delta models causes silent scoring failure after a group shrinks

**Files modified:** `detector/argus_detector/servicer.py`
**Commit:** 710982f
**Applied fix:** Added an `isinstance(model, PairwiseDeltaDetector)` guard in the `ScoreGroupBatch` 2-member branch, right after `get_model()`. If the registry returns a stale, wrong-typed `PeerDivergenceDetector` (left over from a classic N>=3 nightly fit before the group shrank to 2 members), the servicer now aborts with the same "call FitGroup first" `INVALID_ARGUMENT` message instead of letting the type-confusion `ValueError` from `PeerDivergenceDetector.score_batch` propagate into a generic `ok=False`. Applied the cheapest fix from the review (self-heals immediately since `FitGroup` always overwrites the key correctly on its next run) rather than the more invasive key-namespacing alternative, to keep the change surgical.

### CR-02: Peer-divergence group shape transitions (2 <-> 3+ members) leave orphaned MQTT discovery entities in Home Assistant

**Files modified:** `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs`
**Commit:** 544a6fd
**Applied fix:** In `OnConfigChanged`'s retraction diff, now compute `UsesPerMemberEntities` on both the old and new group. When the entity shape differs (crossing the 2/3+-member boundary), retract the *entire* old shape's entity set (all old per-member entities, or the single old group-level entity) rather than only diffing the member list. When the shape is unchanged, the original member-list diff behavior is preserved unchanged.

### WR-01: GRP-04 "no verdict" cycle for classic peer_divergence groups no longer logs anything

**Files modified:** `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs`
**Commit:** e15da41
**Applied fix:** Added an `else if (!string.IsNullOrEmpty(response.Error))` branch after the existing `PerMember.Count > 0` / `GroupVerdict != null` branches, restoring per-cycle log output for the GRP-04 below-floor case (`Ok=true`, `Error` set, both `PerMember` and `GroupVerdict` empty) that previously fell through both branches silently.

### WR-02: FitGroup's 2-member registration path bypasses the registry's documented per-entity locking discipline

**Files modified:** `detector/argus_detector/registry.py`, `detector/argus_detector/servicer.py`
**Commit:** f8aa8ae
**Applied fix:** Added a `DetectorRegistry.swap_model()` helper that takes the per-`(entity_id, detector)` `_entity_lock` (the same lock `fit_one()`/`get_model()` use) before writing, distinct from `register()` which only takes the coarse dict-resize `_lock`. Updated the 2-member `FitGroup` path in `servicer.py` to call `swap_model()` instead of `register()` after training outside the lock, restoring the documented train-outside-lock/atomic-swap contract (MDL-04) for this code path.

## Skipped Issues

None — all in-scope findings were fixed.

---

_Fixed: 2026-07-03T10:30:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
