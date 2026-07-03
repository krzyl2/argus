---
phase: 09-2-member-groups-algorithm-guidance-correction
reviewed: 2026-07-03T00:00:00Z
depth: standard
files_reviewed: 16
files_reviewed_list:
  - detector/argus_detector/group/pairwise_delta.py
  - detector/argus_detector/pyod_detector.py
  - detector/argus_detector/servicer.py
  - detector/tests/test_pairwise_delta.py
  - detector/tests/test_servicer.py
  - orchestrator/Argus.Orchestrator.Tests/DiscoveryPayloadTests.cs
  - orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs
  - orchestrator/Argus.Orchestrator.Tests/GroupBatchSchedulerTests.cs
  - orchestrator/Argus.Orchestrator.Tests/GroupsEndpointsTests.cs
  - orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs
  - orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs
  - orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs
  - orchestrator/Argus.Orchestrator/Web/DetectorCatalog.cs
  - orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs
  - orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs
  - orchestrator/ui/src/validation/groupParams.test.ts
  - orchestrator/ui/src/validation/groupParams.ts
findings:
  critical: 2
  warning: 2
  info: 1
  total: 5
status: issues_found
---

# Phase 9: Code Review Report

**Reviewed:** 2026-07-03
**Depth:** standard
**Files Reviewed:** 16
**Status:** issues_found

## Summary

Reviewed the 2-member `peer_divergence` pairwise-delta path (Python) and the five
.NET orchestrator call sites that had to become count-aware once the joint/peer
member floor dropped from 3 to 2. The individual mechanics of each change are
sound and well-tested in isolation (`PairwiseDeltaDetector`, the servicer
count-branch, `UsesPerMemberEntities`, the two documented executor deviations).

However, two cross-cutting BLOCKER bugs emerge specifically from the
*combination* of changes, neither of which is exercised by the existing test
suite:

1. **Registry key collision** between the classic N>=3 no-op `peer_divergence`
   registration and the new stateful 2-member `PairwiseDeltaDetector`
   registration — both share the identical `(group_slug, "peer_divergence")`
   registry key with no member-count discriminator. Because nightly fit is now
   unconditionally called for *all* groups (a phase-9 change), a group that is
   later shrunk from 3+ members to exactly 2 members will silently fail to
   score (type-confusion exception, swallowed into `ok=False`) until the next
   nightly `FitGroup` cycle overwrites the stale entry — up to a 24h dead
   window with no anomaly detection for that group.

2. **Orphaned HA/MQTT discovery entities** when a `peer_divergence` group's
   member count crosses the 2-member/3+-member boundary while keeping the same
   `group_id`. `MqttPublisherWorker`'s `ConfigChanged` handler only diffs
   `oldGroup.Members` vs `newGroup.Members` when the *old* group's shape used
   per-member entities; it never detects that the shape itself (per-member vs.
   single-group-verdict) changed, so the previous shape's retained discovery
   payloads are never retracted — leaving stale/duplicate entities in HA
   indefinitely.

Also flagged: a silent loss of the "below-floor / no-verdict" log line in
`BatchSchedulerWorker` (dispatch-on-response-shape regression), and a locking-
discipline inconsistency in the new `FitGroup` 2-member registration path.

## Critical Issues

### CR-01: Registry key collision between classic peer_divergence and 2-member pairwise-delta models causes silent scoring failure after a group shrinks

**File:** `detector/argus_detector/servicer.py:253-261` (ScoreGroupBatch 2-member branch), `detector/argus_detector/servicer.py:412-416` (classic FitGroup no-op registration), `detector/argus_detector/registry.py:152-158` (`fit_one` stateless-registration branch)

**Issue:**

Both the classic N>=3 `peer_divergence` registration and the new 2-member
`PairwiseDeltaDetector` registration are keyed identically as
`(group_slug, "peer_divergence")` in `DetectorRegistry._detectors`. There is no
member-count component in the key.

Before this phase, the classic path's `FitGroup` was **never called** for
`peer_divergence` groups (the orchestrator explicitly skipped them in nightly
fit), so this key never got populated for classic groups. Phase 9 removed that
skip (`BatchSchedulerWorker.cs` — `RunNightlyFitAsync` now calls
`RunGroupFitAsync` for every group unconditionally), so a classic N>=3
`peer_divergence` group now gets a no-op `PeerDivergenceDetector` instance
registered under `(group_slug, "peer_divergence")` on every nightly fit cycle
(`registry.fit_one` → `_create_detector("peer_divergence")` →
`PeerDivergenceDetector.from_params(...)`).

Reproduction sequence:
1. Group `g` starts with 3 members, mode `peer_divergence`. Nightly fit runs;
   registry now holds a `PeerDivergenceDetector` at `("group_g", "peer_divergence")`.
2. Operator edits the group down to 2 members (same `group_id`) via
   `POST /api/groups/save` — this is explicitly a supported, tested config
   transition (`GroupsEndpointsTests.Validate_TwoMemberPeerDivergenceGroup_SameUnits_ReturnsNoErrors`).
3. Before the *next* nightly fit runs, `BatchSchedulerWorker`'s regular batch
   tick calls `ScoreGroupBatch` with `len(request.series) == 2`. The servicer
   routes to the 2-member branch:
   ```python
   if not self._registry.has_model(group_slug, detector):   # True — a model IS registered
       ...
   model = self._registry.get_model(group_slug, detector)   # returns the STALE PeerDivergenceDetector
   delta = PairwiseDeltaDetector.compute_delta(...)          # a flat 1-D list
   scores = model.score_batch(delta)                         # PeerDivergenceDetector.score_batch expects a 2-D matrix
   ```
   `PeerDivergenceDetector.score_batch` does
   `n_timestamps, n_members = x.shape` on a 1-D array, raising
   `ValueError: not enough values to unpack`. This propagates to the outer
   `except Exception as e:` and returns `GroupScoreResponse(ok=False, error=str(e))`.
4. The orchestrator logs `"ScoreGroupBatch returned ok=false"` and skips
   publishing for that cycle — repeating on every batch tick until the next
   nightly `FitGroup` overwrites the key with a correctly-typed
   `PairwiseDeltaDetector` (via `register()`, which unconditionally
   overwrites). Until then the group produces **zero anomaly detection**,
   not the intended "call FitGroup first" abort.

Neither `test_servicer.py` nor `test_pairwise_delta.py` exercises a registry
that already contains a classic-mode entry before the 2-member path runs, so
this gap is untested.

**Fix:** Guard the 2-member branch against a wrong-typed cached model (cheapest
fix — self-heals immediately since `FitGroup` always overwrites correctly),
or give the two variants disjoint registry keys.

```python
if detector == "peer_divergence" and len(request.series) == 2:
    from argus_detector.group.pairwise_delta import PairwiseDeltaDetector
    model = self._registry.get_model(group_slug, detector)
    if not isinstance(model, PairwiseDeltaDetector):
        context.abort(
            grpc.StatusCode.INVALID_ARGUMENT,
            f"no fitted 2-member model for group {request.group_id!r}/{detector}; call FitGroup first",
        )
        return None
    ...
```
(Apply the same `isinstance` guard, or an equivalent, anywhere else `get_model`
is trusted without a preceding `has_model` check for this key.) A more robust
long-term fix is to namespace the key by variant, e.g.
`(group_slug, "peer_divergence_pairwise")` for the 2-member case, so the two
detector lifecycles can never collide regardless of call ordering.

---

### CR-02: Peer-divergence group shape transitions (2 <-> 3+ members) leave orphaned MQTT discovery entities in Home Assistant

**File:** `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs:90-113`

**Issue:**

`OnConfigChanged`'s retraction diff only fires a per-member retract when the
*old* group already used per-member entities:

```csharp
if (newGroupsById.TryGetValue(oldGroup.GroupId, out var newGroup))
{
    var isPeer = DiscoveryPublisher.UsesPerMemberEntities(oldGroup);
    if (!isPeer) continue; // joint groups (and 2-member peer groups) have no per-member diff

    var removed = oldGroup.Members
        .Except(newGroup.Members, StringComparer.OrdinalIgnoreCase)
        .ToList();
    if (removed.Count > 0)
        await DiscoveryPublisher.RetractGroupAsync(_mqtt, oldGroup, removed, _stoppingToken);
}
```

This only diffs *member lists*, never *entity shape*. `UsesPerMemberEntities`
(`IsPeerDivergence(group) && group.Members.Count >= 3`) can change value for
the same `group_id` across a save, because the member-floor is now 2 for both
modes. Two concrete failure directions, both live workflows (see
`GroupsEndpointsTests`/`EntitiesConfigTests` 2-member fixtures):

- **3+ members -> 2 members:** `isPeer` (computed from `oldGroup`) is `true`.
  `removed` = only the member(s) actually dropped from the list. The
  *surviving* members' per-member entities (e.g.
  `argus_group_<slug>_<member>_flag`) are never retracted, even though the new
  shape publishes a single group-level entity (`argus_group_<slug>_flag`)
  instead. Result: N-1 orphaned per-member entities in HA, permanently
  retained, plus one new group-level entity.
- **2 members -> 3+ members:** `isPeer` (computed from `oldGroup`, which had 2
  members) is `false`, so the `if (!isPeer) continue;` guard skips retraction
  entirely — the old single group-level entity (`argus_group_<slug>_flag`) is
  never retracted, even though the new shape publishes per-member entities
  under different unique_ids. Result: 1 orphaned group-level entity plus N new
  per-member entities.

No test (`DiscoveryPayloadTests`, `GroupsEndpointsTests`) exercises the
`ConfigChanged` retraction path across a shape-changing edit — only the
`PublishGroupAsync` static builder is tested per-shape in isolation.

**Fix:** Compare `UsesPerMemberEntities` on both the old and new group and
retract the *entire old shape* whenever it differs, not just the leaf member
diff:

```csharp
if (newGroupsById.TryGetValue(oldGroup.GroupId, out var newGroup))
{
    var oldIsPeer = DiscoveryPublisher.UsesPerMemberEntities(oldGroup);
    var newIsPeer = DiscoveryPublisher.UsesPerMemberEntities(newGroup);

    if (oldIsPeer != newIsPeer)
    {
        // Entity shape changed (e.g. a peer group crossed the 2/3-member
        // boundary) — retract the OLD entity set entirely; the new shape's
        // entities are (re)published fresh below.
        IEnumerable<string?> oldEntities = oldIsPeer
            ? oldGroup.Members.Cast<string?>()
            : [null];
        await DiscoveryPublisher.RetractGroupAsync(_mqtt, oldGroup, oldEntities, _stoppingToken);
    }
    else if (oldIsPeer)
    {
        var removed = oldGroup.Members
            .Except(newGroup.Members, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (removed.Count > 0)
            await DiscoveryPublisher.RetractGroupAsync(_mqtt, oldGroup, removed, _stoppingToken);
    }
    // else: joint groups (and same-shape peer groups) — no per-member diff needed.
}
```

## Warnings

### WR-01: GRP-04 "no verdict" cycle for classic peer_divergence groups no longer logs anything

**File:** `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs:239-250`

**Issue:** The publish-routing dispatch changed from `if (isPeer)` to
`if (response.PerMember.Count > 0)`. Previously, a below-floor classic peer
group (`PerMember` empty, `error` set, `Ok=true`) still hit the `if (isPeer)`
branch and logged `"Scored group {GroupId} ({Mode}): 0 member verdicts"` every
cycle. Now, with `response.PerMember.Count > 0` false and
`response.GroupVerdict != null` also false (GRP-04 responses carry neither),
**neither branch executes** — the cycle produces no log output at all. This
contradicts the project's "fail loud" convention (nothing should be silently
skipped) and removes the only per-cycle observability signal an operator had
for "why isn't my group scoring" (compounding with CR-01 above, where this is
exactly the failure mode that would benefit most from a clear log line).

**Fix:**
```csharp
else if (response.GroupVerdict != null)
{
    ...
}
else if (!string.IsNullOrEmpty(response.Error))
{
    _logger.LogInformation(LogEvents.GroupScored,
        "Group {GroupId} ({Mode}) produced no verdict this cycle: {Error}",
        group.GroupId, group.Mode, response.Error);
}
```

### WR-02: FitGroup's 2-member registration path bypasses the registry's documented per-entity locking discipline

**File:** `detector/argus_detector/servicer.py:399-410`, `detector/argus_detector/registry.py:238-251`

**Issue:** `DetectorRegistry` documents (registry.py:5-11) that all model
writes go through the per-`(entity_id, detector)` lock (`_entity_lock`) via
`fit_one()`'s train-outside-lock/atomic-swap idiom, specifically so
`get_model()` readers never observe a torn state relative to concurrent
fits (MDL-04). The new 2-member `FitGroup` path instead calls
`self._registry.register(group_slug, detector, model)` directly, which only
takes the coarse `self._lock` (guards dict-resize only) — not the per-key
`_entity_lock` that `get_model()`/`fit_one()` use. CPython's GIL makes the
individual dict `__setitem__`/`__getitem__` calls atomic, so this will not
corrupt memory, but it silently breaks the class's own stated concurrency
contract for this one code path (a concurrent `ScoreGroupBatch` reading via
`get_model()` is not actually synchronized against this writer the way the
docstring promises).

**Fix:** Route the 2-member registration through the same locked swap pattern
`fit_one()` uses (or add a small `swap_model(key, model)` helper on
`DetectorRegistry` that takes `_entity_lock` before writing), rather than
calling the LoadModel-oriented `register()` from a live RPC path.

## Info

### IN-01: BuildGroupMatrix docstring no longer accurately describes 2-member peer group behavior

**File:** `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs:288-298`

**Issue:** The XML doc comment states "PEER: stale members are dropped from
the active set; if fewer than the minimum floor of fresh members remain, the
group is skipped" — this is no longer true for a 2-member peer group, which
(per the newly-added `group.Members.Count >= 3` gate, correctly) behaves like
JOINT: *any* staleness skips the whole group, since a pairwise delta cannot
tolerate a dropped column. The inline comment near the actual check explains
this correctly, but the method-level summary was not updated to match.

**Fix:** Add a sentence to the summary: "For exactly 2 peer members, any
staleness skips the whole group (same as JOINT) since a pairwise delta needs
both columns present."

---

_Reviewed: 2026-07-03_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
