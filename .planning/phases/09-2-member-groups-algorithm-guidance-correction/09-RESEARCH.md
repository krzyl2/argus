# Phase 9: 2-Member Groups + Algorithm Guidance Correction - Research

**Researched:** 2026-07-03
**Domain:** Anomaly-detection group config validation (.NET), gRPC group-detector dispatch (Python), guided-chooser UX copy
**Confidence:** HIGH (all findings verified by direct code reads; no external libraries introduced)

## Summary

Phase 9 has two independent halves. Half A (floor lowering + guided-default correction + copy)
is mechanical: three files enforce a uniform member-count floor of 3 with no mode branching, one
guided-answer mapping needs a one-line change, and one catalog needs copy edits. All four
locations were located and confirmed unchanged since the roadmap was written.

Half B (the pairwise-delta capability for 2-member `peer_divergence` groups) is the real design
work. Tracing the full call path — proto -> servicer.py -> registry.py -> peer_divergence.py /
pyod_detector.py -> model_store.py, then back up through BatchSchedulerWorker.cs ->
DiscoveryPublisher.cs / MqttPublisherWorker.cs — surfaces that this is **not** purely an
internal-to-Python change, as the roadmap context speculated. Five separate places in the C#
orchestrator implicitly assume "peer_divergence" always means "N>=3 members, one Verdict per
member" and will silently do the wrong thing (skip forever, publish nothing, or create
meaningless entities) once a 2-member peer_divergence group exists. These are documented in full
under Common Pitfalls and Architecture Patterns below, each with an exact file/line and a
concrete fix.

There is also a direct contradiction inside the ROADMAP.md phase-9 scope text itself: scope item 1
says "peer_divergence floor stays at 3", but scope item 2 requires 2-member peer_divergence groups
to be *accepted* (not rejected) so they can route to the new pairwise-delta path. Section "Open
Design Question — Resolution" below reconciles this: the **config-validation floor** for both
modes must become 2; "stays at 3" correctly describes only `PeerDivergenceDetector`'s own
internal, algorithm-level floor for its classic median/MAD sub-path (N>=3), which becomes
unreachable for N==2 once the new branch exists.

**Primary recommendation:** Keep the "peer_divergence" detector string unchanged for both the
classic (N>=3) and new pairwise (N==2) cases — branch on `len(request.series)` inside
`servicer.py`'s `ScoreGroupBatch`/`FitGroup`, *before* constructing `PeerDivergenceDetector`. Add
a new, small `PairwiseDeltaDetector` (wrapping the existing `PyODDetector` unchanged) in a new
`detector/argus_detector/group/pairwise_delta.py` file. Persist it via the existing
`ModelStore.save_pyod`/`load_pyod` under key `(group_slug, "peer_divergence")` — a path that is
currently *never written* for classic peer_divergence, so there is no collision. No proto changes
are needed. On the C# side, fix the five count-blind branches identified below.

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| GRP-10 (proposed) | Joint-mode groups accept exactly 2 members (floor lowered 3->2) | Confirmed 3 enforcement points + exact line numbers; PyOD 3.6.0 fits/scores 2-feature matrices without issue (empirically verified by operator per ROADMAP context) |
| GRP-11 (proposed) | 2-member `peer_divergence` groups score via a pairwise-delta path reusing the existing single-entity PyOD MAD detector | Full call-path trace done; concrete dispatch/persistence/publish design below |
| GRP-12 (proposed) | Client and server validation floors are consistent with the new mode-dependent membership rules (2 for joint; 2 for peer_divergence, routed internally by member count) | Resolves ROADMAP's internal floor-wording contradiction (see below) |
| ALGO-05 (proposed) | Guided chooser's "together" answer recommends COPOD instead of ECOD | One-line change confirmed at exact location |
| ALGO-06 (proposed) | `DetectorCatalog.cs` `BestFor` copy for all 5 entries accurately reflects correlation-handling and attribution capability, including a 2-member peer_divergence caveat | Existing copy read in full; specific misleading phrase identified for peer_divergence |

*Requirement IDs are proposed — REQUIREMENTS.md v4.0 traceability table has no Phase 9 row yet;
GRP-01..09/ALGO-01..04/SRCH-01..03/UI-01..04 are all "Complete". Confirm final IDs during
`/gsd-plan-phase`.*

## User Constraints (from ROADMAP.md — no CONTEXT.md exists for this phase)

No `/gsd-discuss-phase` was run for Phase 9. Per the task brief, ROADMAP.md's "Phase 9" section
(written 2026-07-03 after live verification of Phase 8) is the locked-decision context. Treat the
following as locked, not open:

### Locked Decisions
1. Lower the **joint-mode** member floor from 3 to 2 in three places: `groupParams.ts`,
   `GroupInputValidator.cs`, `EntitiesConfigLoader.cs`.
2. `peer_divergence`'s classic median/MAD algorithm keeps its floor of 3 for the N-member case
   (mathematically degenerate at N=2) — this is an algorithm-level statement about
   `PeerDivergenceDetector`, not (per this research's recommended resolution) a statement about
   what the config-validation layer should reject.
3. Change `DetectorCatalog.Guided()`'s "together" mapping from `ecod` to `copod`.
4. Update all 5 `BestFor` entries in `DetectorCatalog.cs` — draft copy already exists from a prior
   session; operator will personally edit/redact before ship. Treat as placeholder, not final.
5. New capability: 2-member `peer_divergence` groups compute `member_a - member_b` and score it
   with the existing single-entity PyOD MAD detector (`pyod_detector.py`) — empirically verified
   working (normal delta scores low, injected drift scores high) in a prior session.

### Claude's Discretion
- Exact dispatch mechanism for routing 2-member `peer_divergence` requests to the pairwise path
  (explicitly called out as unresolved in ROADMAP — this research answers it, see below).
- Whether the pairwise-delta group publishes per-member or group-level MQTT/HA entities
  (explicitly called out as unresolved — this research recommends group-level, see below).
- Final wording of `BestFor` copy (operator will edit anyway).

### Deferred Ideas (OUT OF SCOPE)
- Phase 999.1 (algorithm tester/simulator in group config UI) — explicitly filed as backlog, do
  not fold into Phase 9.
- STRM-01/02 (streaming group detection) — out of scope for all of v4.0.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Member-count floor validation (client UX + server authority) | API/Backend (.NET) | Browser/Client (SPA, UX-only mirror) | `GroupInputValidator.cs` is the authoritative boundary per its own doc comment; `groupParams.ts` is fast-feedback only |
| Pairwise-delta computation, fit, scoring | API/Backend — Python detector process | — | All numeric/ML logic is Python per PROJECT.md D2; matrix already arrives via existing proto, no orchestrator math needed |
| Model persistence (fit/save/load) | Database/Storage (local disk, `ModelStore`) | — | Existing per-group joblib file scheme, reused verbatim |
| gRPC dispatch (detector string routing) | API/Backend — Python (`servicer.py`) | — | Existing convention: "dispatch purely on detector string field, no separate mode enum" (Phase 05 decision, STATE.md) |
| Batch scheduling / nightly fit orchestration | API/Backend (.NET, `BatchSchedulerWorker`) | — | Owns when Fit/Score RPCs fire; must stop assuming "peer_divergence == never fit" |
| MQTT discovery entity shape (per-member vs group-level) | API/Backend (.NET, `DiscoveryPublisher`) | — | Entity shape must match the RPC response shape (per_member vs group_verdict), which is decided in Python |
| Guided chooser recommendation + BestFor copy | API/Backend (.NET, `DetectorCatalog`, static data only) | Browser/Client (SPA renders it) | Static descriptive data, never calls gRPC (existing anti-pattern rule) |

## Standard Stack

No new libraries. This phase reuses:

| Library | Version (confirmed installed) | Purpose | Why Standard |
|---------|---------|---------|--------------|
| PyOD | 3.6.0 (pinned; 3.6.1 exists upstream but project is pinned — do not bump without a separate decision) | `pyod.models.mad.MAD` reused unmodified for the pairwise-delta signal | Already production-proven since v1.0 (`pyod_detector.py`); roadmap explicitly says "reusing proven univariate anomaly detection" not inventing new math |
| joblib | 1.5.3 (existing) | Persist the fitted pairwise MAD model | Same serialization path as every other PyOD model in `ModelStore` |
| numpy | (transitive, existing) | Delta computation (`member_a - member_b`) | Already a dependency via PyOD/River |

`[VERIFIED: PyPI]` `pyod` 3.6.0 installed matches project pin (checked via `pip index versions
pyod`; latest upstream is 3.6.1, project intentionally stays on 3.6.0 per CLAUDE.md tech-stack doc
— no version bump needed or recommended for this phase).

## Package Legitimacy Audit

**Not applicable.** This phase installs no new packages in any ecosystem — it reuses
`pyod.models.mad.MAD` (already a direct dependency, already audited in prior phases) and adds no
new third-party code.

## Architecture Patterns

### Current Group-Scoring Data Flow (as of Phase 8, before Phase 9 changes)

```
entities.yaml (group_id, mode, detector, members[], params{})
        |
        v
[.NET] BatchSchedulerWorker.RunGroupBatchAsync (every batch tick)
        |-- IGroupInfluxDataSource.QueryGroupAsync -> time-aligned matrix (InfluxDB)
        |-- BuildGroupMatrix(isPeer) -- staleness policy branches HERE on Mode string
        |-- BuildGroupScoreRequest -> gRPC GroupScoreRequest{group_id, detector, params, series[]}
        v
[Python] DetectorServicer.ScoreGroupBatch  -- dispatches on request.detector string
        |
        |-- detector == "peer_divergence"
        |       -> PeerDivergenceDetector.from_params(params)   [constructed FRESH every call,
        |          .score_batch(matrix) -> (scores, flags, error)   NO registry/model_store use]
        |       -> per_member: [Verdict, Verdict, ...]     (one per group member)
        |
        `-- detector in (ecod|copod|pca|iforest)
                -> registry.has_model(group_slug, detector) or ABORT "call FitGroup first"
                -> GroupMultivariateDetector.score_batch(matrix) -> (scores, contributions|None)
                -> group_verdict: ONE Verdict                (whole-group score)
        v
[.NET] BatchSchedulerWorker.RunGroupBatchAsync
        |-- if (isPeer)  foreach response.PerMember -> PublishGroupScoreAsync(groupId, memberId, ...)
        `-- else         response.GroupVerdict      -> PublishGroupScoreAsync(groupId, null, ...)
        v
[.NET] DiscoveryPublisher / MqttPublisherWorker
        |-- IsPeerDivergence(group) -- Mode-string check, NO count awareness
        |       true  -> one binary_sensor+sensor PAIR PER MEMBER
        |       false -> one binary_sensor+sensor PAIR for the whole group
        v
   HA entities via MQTT discovery
```

**Nightly fit**: `BatchSchedulerWorker.RunNightlyFitAsync` calls `FitGroupAsync` for every group
EXCEPT it unconditionally `continue`s (skips) every group whose `Mode == "peer_divergence"`,
because classic peer_divergence has no fit step at all.

### Recommended Data Flow for 2-Member `peer_divergence` (Phase 9 addition)

```
entities.yaml (group_id, mode="peer_divergence", detector="peer_divergence", members=[a,b])
        |
        v
[.NET] BatchSchedulerWorker.RunNightlyFitAsync
        -- CHANGE: remove the "skip all peer_divergence" continue; call RunGroupFitAsync
           for peer_divergence groups too (Python decides what to do with it)
        v
[Python] DetectorServicer.FitGroup  -- detector == "peer_divergence"
        |
        |-- len(request.series) == 2   [NEW BRANCH]
        |       -> delta = series[0].values - series[1].values   (elementwise, numpy)
        |       -> PairwiseDeltaDetector(); .fit(delta)  (wraps PyODDetector.fit() unchanged)
        |       -> registry.register(group_slug, "peer_divergence", fitted)
        |       -> model_store.save_pyod(group_slug, "peer_divergence", version, fitted,
        |                                 entity_id=group_slug)
        |
        `-- len(request.series) >= 3   [UNCHANGED — existing no-op passthrough]
        v
[Python] DetectorServicer.ScoreGroupBatch  -- detector == "peer_divergence"
        |
        |-- len(request.series) == 2   [NEW BRANCH]
        |       -> require registry.has_model(group_slug, "peer_divergence")
        |          else ABORT INVALID_ARGUMENT "no fitted model ... call FitGroup first"
        |          (mirrors the existing joint-mode abort message exactly)
        |       -> delta = series[0].values - series[1].values
        |       -> scores = model.score_batch(delta)   (PyODDetector.score_batch, unchanged)
        |       -> is_anomaly = model.is_anomaly(scores[-1])  [NEW: small method added to
        |          PyODDetector mirroring GroupMultivariateDetector.is_anomaly(), see Pitfall 6]
        |       -> group_verdict: ONE Verdict (entity_id = group_slug, detector="peer_divergence")
        |       -> per_member: EMPTY   -- this is the key shape signal the .NET side must react to
        |       -> contributions: EMPTY -- delta cannot attribute to either member (same
        |          degeneracy reason classic peer_divergence has at N=2 — never fabricate this)
        |
        `-- len(request.series) >= 3   [UNCHANGED — classic PeerDivergenceDetector path]
        v
[.NET] BatchSchedulerWorker.RunGroupBatchAsync
        -- CHANGE: branch on response shape, not on isPeer/Mode string:
           if (response.PerMember.Count > 0)      -> per-member publish (unchanged code path)
           else if (response.GroupVerdict != null) -> group-level publish (unchanged code path,
                                                       already exists for joint mode — just reached
                                                       from a new caller)
        -- CHANGE: BuildGroupMatrix's isPeer staleness policy (drop-stale-then-require->=3-fresh)
           must be gated on group.Members.Count >= 3; for exactly 2 members, use the
           "any stale member skips the whole group" policy (already correct for joint, and the
           only sane policy when a pairwise delta needs BOTH members present)
        v
[.NET] DiscoveryPublisher / MqttPublisherWorker
        -- CHANGE: IsPeerDivergence(group)'s three call sites need count-awareness:
           2-member peer_divergence groups get ONE group-level binary_sensor+sensor pair
           (memberId=null), matching the single derived score — NOT two per-member entities
        v
   HA entities: 1 flag + 1 score entity for the pair (e.g. "Cisnienie kol przednich anomalia")
```

### Pattern: Servicer-level count branching, not detector-class branching

**What:** Route on `len(request.series)` inside `servicer.py`'s `ScoreGroupBatch`/`FitGroup`
methods, before any `PeerDivergenceDetector` object is constructed.

**When to use:** Whenever a "detector name" needs genuinely different math depending on input
shape, but changing the existing class's public contract would break a locked, currently-passing
test.

**Why (concrete evidence, not just style preference):**
`detector/tests/test_peer_divergence.py::TestPeerDivergenceFloor::test_below_floor_returns_no_verdict`
(lines 61-69) asserts:
```python
det = PeerDivergenceDetector()
scores, flags, error = det.score_batch([[10.0, 10.0]])   # n_members == 2
assert scores is None and flags is None
assert "insufficient members" in error
```
This test is a direct, current, passing assertion that `PeerDivergenceDetector.score_batch()`
errors on exactly 2 members. If the pairwise-delta logic is implemented *inside*
`PeerDivergenceDetector.score_batch()` (e.g. by editing `_MIN_MEMBERS` or adding an n==2 special
case there), this test either breaks (contract violated) or has to be rewritten to assert the
opposite of what it currently, correctly, asserts for the *classic* algorithm. Since the roadmap
explicitly frames the pairwise path as "a new detector-adjacent code path rather than a mode enum
value," the correct location for the new logic is a **new class outside `PeerDivergenceDetector`**,
selected by the servicer based on member count. `PeerDivergenceDetector`'s floor-of-3 contract
stays completely untouched and this test keeps passing unmodified.

### Anti-Patterns to Avoid
- **Branching on `group.Mode == "peer_divergence"` alone anywhere in the .NET orchestrator to
  decide per-member vs group-level behavior.** Five places in the current codebase do exactly
  this (`RunGroupBatchAsync`'s publish branch, `BuildGroupMatrix`'s staleness policy,
  `DiscoveryPublisher.IsPeerDivergence` x3 call sites, `MqttPublisherWorker`'s retract branch).
  After Phase 9 ships, `Mode == "peer_divergence"` no longer implies "N>=3, per-member." Every one
  of these must also check member count or (preferably) react to the RPC response *shape*
  instead of guessing from config.
- **A new `peer_divergence_pairwise` detector string** (rejected option). Would require touching
  `GroupInputValidator.IsModeDetectorConsistent`, `DetectorCatalog`, and every `isPeer`/
  `IsPeerDivergence` string check across two languages for a distinction the servicer can already
  make cheaply from `len(request.series)`. No UI plumbing exists today for the operator to pick a
  second "kind" of peer_divergence detector, so this option would also need new UI work not
  currently scoped.
- **Fabricating per-feature `contributions` for the pairwise-delta case.** The whole reason
  classic peer_divergence has a floor of 3 is that a 2-point set can't identify which point
  diverges (both are equidistant from the median). The pairwise delta doesn't fix this — it only
  tells you the *pair's relationship* broke, not which of the two members is responsible. Leave
  `contributions` empty for this case, exactly as joint-mode PCA/IForest already leave it empty
  when attribution isn't mathematically available.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Scoring a derived univariate signal (member_a - member_b) for anomalies | A new statistical test or threshold rule | `PyODDetector` (existing `pyod_detector.py`, wraps `pyod.models.mad.MAD`) unmodified | Production-proven since v1.0; roadmap explicitly verified empirically that it already works correctly on injected drift; reusing it needs zero new ML code, only a delta-computation + wiring |
| Persisting the fitted pairwise model | A new file format or a new `ModelStore` method | `ModelStore.save_pyod`/`load_pyod` (existing) | Identical shape to any other single-series PyOD model — no scaler, no bundle dict needed since there is only one derived feature, not multiple mixed-unit ones |
| Deciding is_anomaly for the pairwise verdict | Re-deriving a threshold formula in servicer.py | A new `PyODDetector.is_anomaly(score)` public method mirroring `GroupMultivariateDetector.is_anomaly()` | Keeps the "public accessor, never reach into `_model` from servicer.py" convention (WR-02) already established for the joint path |

**Key insight:** every piece of net-new logic this phase needs (delta computation, fit, score,
persist, threshold-decide) already has a proven, tested analog somewhere in this codebase. The
design work is entirely about *wiring* (which layer decides what, based on what signal), not
about inventing new algorithms.

## Common Pitfalls

### Pitfall 1: `BatchSchedulerWorker.PeerMinFreshMembers = 3` is a 4th, unlisted floor-enforcement site
**What goes wrong:** ROADMAP.md lists exactly 3 files for the floor change (`groupParams.ts`,
`GroupInputValidator.cs`, `EntitiesConfigLoader.cs`). There is a 4th, functionally identical
floor hiding in `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs:47`:
`private const int PeerMinFreshMembers = 3;`, consumed inside `BuildGroupMatrix` (lines 315-327):
for `isPeer` groups, stale members are dropped individually and the group is skipped entirely
unless `>= 3` fresh members remain.
**Why it happens:** This floor governs *runtime freshness* (how many members must have recent
data this batch tick), which is conceptually separate from the *config-time* membership floor the
roadmap was describing, but has the identical numeric effect for a 2-member group: it can never be
satisfied (only 2 members exist, ever), so the group is skipped every single tick the moment
either member's data is even briefly stale.
**How to avoid:** Gate the "drop-stale-members-then-require-floor" policy on
`group.Members.Count >= 3`. For exactly-2-member peer_divergence groups, use the same "any stale
member skips the whole group" policy joint mode already uses (`isPeer=false` behavior) — this is
also the *only* semantically correct policy, since a pairwise delta needs both members present.
**Warning signs:** A 2-member peer_divergence group configured correctly but silently never
publishing any scores; `LogEvents.GroupSkippedStale` logged every tick.

### Pitfall 2: Publish-routing branch keys off Mode string, not response shape
**What goes wrong:** `BatchSchedulerWorker.RunGroupBatchAsync` (lines 239-250) does
`if (isPeer) { foreach response.PerMember ... }`. For a 2-member peer_divergence group, the
Python side (per this research's recommended design) returns `GroupVerdict` populated and
`PerMember` empty. The `isPeer` branch would iterate an empty list and publish *nothing at all*,
silently — no error, no log warning distinguishing this from "the servicer had nothing to say."
**Why it happens:** `isPeer` is derived purely from `group.Mode`, which after Phase 9 no longer
implies which proto field the servicer populated.
**How to avoid:** Branch on `response.PerMember.Count > 0` vs `response.GroupVerdict != null`
instead of `isPeer`. This also future-proofs against any other detector shape changes without
touching the .NET side again.
**Warning signs:** A 2-member peer_divergence group's MQTT topics never receive a retained score
message; HA shows the entities as "unavailable" forever after first discovery-publish.

### Pitfall 3: `IsPeerDivergence(group)` drives per-member vs group-level discovery-entity shape with zero count awareness
**What goes wrong:** `DiscoveryPublisher.cs` (lines 226-352) uses `IsPeerDivergence(group)` (a
pure Mode-string check) in `BuildGroupBinarySensorConfig`, `BuildGroupSensorConfig`, and
`PublishGroupAsync` to decide "one entity pair per member" vs "one entity pair for the whole
group." A 2-member pairwise group would get **two** per-member entities created, even though the
underlying score is a single derived value with no per-member attribution — either duplicating
the same score onto both entities (misleading: implies each member has its own independent score)
or leaving one arbitrarily blank.
**Why it happens:** Same root cause as Pitfall 2 — Mode string no longer determines entity/response
shape by itself once N==2 peer_divergence exists.
**How to avoid:** Add a count-aware helper, e.g. `UsesPerMemberEntities(group) =>
IsPeerDivergence(group) && group.Members.Count >= 3`, and use it everywhere `IsPeerDivergence`
currently gates entity-shape decisions. This is the explicit "MQTT/HA-facing entity semantics"
open question from the task brief — **recommendation: group-level (one pair), matching the
`group_verdict` response shape.**
**Warning signs:** Two HA entities appear for what the operator configured as one relationship
check; one of them never updates or mirrors the other exactly.

### Pitfall 4: `MqttPublisherWorker`'s membership-change retract logic has the same Mode-only blind spot
**What goes wrong:** `MqttPublisherWorker.cs` lines 95-111 use `isPeer` (Mode-string) to decide
whether a membership change retracts per-member entities (peer_divergence) or a single null-entry
group-level entity (joint). A 2-member pairwise group swapping one member for another would try to
retract per-member entities that were never individually published (per Pitfall 3's fix), a no-op
against the wrong MQTT topic, while the actual group-level entity is never retracted/refreshed.
**How to avoid:** Same `UsesPerMemberEntities` count-aware helper as Pitfall 3, applied here too.
**Warning signs:** Stale HA entity persists after a 2-member group's membership is edited.

### Pitfall 5: Nightly fit unconditionally skips ALL peer_divergence groups
**What goes wrong:** `BatchSchedulerWorker.RunNightlyFitAsync` (lines 500-503):
```csharp
if (string.Equals(group.Mode, "peer_divergence", StringComparison.OrdinalIgnoreCase))
    continue;
```
This comment explicitly says "peer_divergence is stateless ... never Fit it" — true for the
classic (N>=3) algorithm, but the whole point of the new pairwise path is that it **is** stateful
(it wraps `PyODDetector`, which requires `fit()` before `score_batch()`). If this skip is left in
place, `FitGroup` is never called for 2-member peer_divergence groups, `registry.has_model(...)`
is always false, and `ScoreGroupBatch` aborts every batch tick with "no fitted model — call
FitGroup first."
**Why it happens:** This is the ROADMAP's own claim under question — the additional_context asks
"does the orchestrator need to know the pairwise-delta path exists at all" and speculates it might
be "purely internal to the Python detector layer." **This research finds that claim is false for
this one call site**: the C# skip must be removed (or made count-aware) or the new capability
never fits.
**How to avoid:** Remove the unconditional skip; call `RunGroupFitAsync` for every group
regardless of Mode, and let Python's `FitGroup` internally no-op for N>=3 (current, unchanged
behavior) and actually fit for N==2 (new behavior). This is the *only* orchestrator change in this
list that is a pure subtraction (delete a guard) rather than added logic.
**Warning signs:** `ScoreGroupBatch returned ok=false ... no fitted model` in logs for a 2-member
peer_divergence group, forever, because Fit is never even attempted.

### Pitfall 6: `PyODDetector` has no `is_anomaly()` helper (unlike `GroupMultivariateDetector`)
**What goes wrong:** `GroupMultivariateDetector.is_anomaly(score)` is a public accessor added
specifically so servicer.py never reaches into `_model.threshold_` directly (WR-02 convention,
documented Pitfall-1-adjacent comment in `multivariate_detector.py`). `PyODDetector` (the class
being reused for the pairwise path) has no equivalent method — only `fit()`, `score_batch()`, and
`is_fitted`.
**Why it happens:** `PyODDetector` was designed for the streaming/batch per-entity path, where
`is_anomaly` decisions are made by the orchestrator's hysteresis gate downstream, not by the
detector itself (`ScoreBatch`'s servicer code explicitly sets `is_anomaly=False` and comments
"orchestrator's hysteresis gate decides"). Group verdicts, by contrast, need `is_anomaly` set
directly by the servicer (Phase 5 design decision, documented in `ScoreGroupBatch`'s docstring).
**How to avoid:** Add a small `is_anomaly(self, score: float) -> bool: return bool(score >
self._model.threshold_)` method to `PyODDetector`, mirroring `GroupMultivariateDetector`'s
existing pattern exactly. This is additive and cannot break any existing caller (`ScoreBatch`'s
per-entity path never calls it).
**Warning signs:** Servicer code reaching into `model._model.threshold_` directly for the pairwise
path (breaks the established WR-02 encapsulation convention) or, if skipped, no accurate
`is_anomaly` in the published verdict.

### Pitfall 7: The ROADMAP's own floor wording is self-contradictory
**What goes wrong:** ROADMAP.md Phase 9 scope item 1 says "peer_divergence floor stays at 3."
Scope item 2 requires accepting exactly-2-member peer_divergence groups (to route them to the
pairwise path). Taken literally, item 1 would make `GroupInputValidator`/`EntitiesConfigLoader`/
`groupParams.ts` **reject** the very groups item 2 needs to accept, at the config-save/load layer
— before the request ever reaches the servicer's count-based branch.
**How to avoid (recommended resolution — flagged `[ASSUMED]`, needs explicit confirmation):** The
config-validation-layer floor becomes **2 for both modes** (`GroupInputValidator.MinMembers`,
`EntitiesConfigLoader`'s `< 3` check, `groupParams.ts`'s `MIN_MEMBERS`). "Stays at 3" correctly
describes only `PeerDivergenceDetector`'s own internal `_MIN_MEMBERS` constant — the floor for its
classic median/MAD sub-algorithm — which remains completely untouched and is simply never reached
for N==2 once the servicer routes N==2 to the new pairwise class instead. This reconciles both
scope items without contradicting either. See Assumptions Log A1.
**Warning signs:** If the planner implements item 1 literally (leaves peer_divergence's
config-validation floor at 3), a 2-member peer_divergence group can never be saved through the UI
at all, silently defeating item 2 regardless of how well the Python side is built.

## Open Design Question — Resolution

*(This section directly answers the "ONE OPEN DESIGN QUESTION" from the task brief.)*

1. **Dispatch mechanism:** Keep detector string `"peer_divergence"` unchanged for both cases.
   Branch on `len(request.series) == 2` inside `servicer.py`'s `ScoreGroupBatch` and `FitGroup`,
   *before* constructing `PeerDivergenceDetector`. Rejected alternative: a new
   `peer_divergence_pairwise` detector string — bigger blast radius (touches
   `GroupInputValidator.JointDetectors`-adjacent consistency logic, `DetectorCatalog`, every
   `isPeer` string check in two languages) for no benefit the servicer can't already provide from
   `len(request.series)` alone. See Pattern section above for the exact reasoning and the test
   that would break if the branch were placed inside `PeerDivergenceDetector` itself instead.

2. **`ModelStore` key scheme:** No new scheme needed. Reuse `ModelStore.save_pyod`/`load_pyod`
   directly (not `save_group_bundle`, which exists for the scaler+detector bundle multivariate
   needs — the pairwise case has only one derived feature, no scaling/mixed-units problem to
   solve). Key: `(group_slug, "peer_divergence")` where `group_slug = model_store.group_slug
   (group_id)`. This exact key is currently **never written to disk** — classic peer_divergence's
   `FitGroup` branch is a no-op, and (per Pitfall 5) is never even invoked in production today
   because `RunNightlyFitAsync` skips all peer_divergence groups unconditionally. No collision
   risk, today or after this phase, as long as classic peer_divergence (N>=3) continues to never
   call `save_pyod`.

3. **Proto contract:** No changes needed. `FitGroupRequest`/`GroupScoreRequest` already carry the
   full raw per-member `Series` list (2 members = 2 `Series`, each with the raw `repeated double
   values`), which is exactly what's needed to compute `values[0] - values[1]` server-side in
   Python. `GroupScoreResponse`'s existing `group_verdict` field (already populated by the joint
   path) is reused for the pairwise verdict; `per_member` stays empty for this case;
   `contributions` stays empty (no attribution is mathematically available — see Anti-Patterns).

4. **Does the C# orchestrator need to know this path exists?** **Partially yes** — contrary to
   the task brief's framing that this might be "purely internal to the Python detector layer."
   Confirmed via full call-path trace: five C#-side locations currently assume
   `Mode == "peer_divergence"` implies "N>=3, per-member" and will misbehave once N==2
   peer_divergence groups exist. See Pitfalls 1-5 above for the exact locations and fixes. None
   of these fixes require the orchestrator to understand *why* member count matters (i.e., no
   pairwise-delta math leaks into C#) — they only require replacing Mode-string checks with
   count-aware or response-shape-aware checks, which is a much smaller and more defensible
   change than teaching the orchestrator about the new algorithm.

5. **MQTT/HA entity semantics:** **Recommend group-level** (one binary_sensor + one sensor for
   the pair, `memberId=null`), matching the `group_verdict` response shape and matching the fact
   that the pairwise delta cannot attribute the anomaly to either individual member (same
   degeneracy the classic N=2 case has). This requires the count-aware
   `UsesPerMemberEntities(group)` fix described in Pitfalls 3 and 4. The friendly-name copy for
   this single entity (e.g. distinguishing it from a "true" 2-sensor joint-mode group with the
   same members) is a planner/operator decision, not a research one — flag it explicitly in the
   PLAN.md for operator sign-off, since `DetectorCatalog`'s peer_divergence `BestFor` text
   currently says "want to know WHICH member is diverging," which is actively misleading for the
   2-member case (see ALGO-06 in Phase Requirements).

## Read-First Material (exact locations, confirmed still current)

| File | Location | Current value | Change needed |
|------|----------|---------------|----------------|
| `orchestrator/ui/src/validation/groupParams.ts` | line 8 `const MIN_MEMBERS = 3;`, line 10 `MSG_BELOW_FLOOR`, `validateGroupMembers()` lines 16-21 | uniform floor 3, no mode param | Recommend: `MIN_MEMBERS = 2` for both modes (see Pitfall 7); function signature currently doesn't even take `mode` — may need to for future messaging nuance, but a single floor of 2 needs no branching |
| `orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs` | line 21 `private const int MinMembers = 3;`, check at lines 92-96 | uniform floor 3 | Recommend: `MinMembers = 2` |
| `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` | line 111 `if (group.Members is null || group.Members.Count < 3)` | uniform floor 3 | Recommend: `< 2` |
| `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` | line 47 `PeerMinFreshMembers = 3` (4th, unlisted floor — see Pitfall 1) | used in `BuildGroupMatrix`, lines ~315-327 | Gate on `group.Members.Count >= 3` |
| `orchestrator/Argus.Orchestrator/Web/DetectorCatalog.cs` | `Guided()` lines 136-140, `"together" -> "ecod"` at line 138 | `ecod` | Change to `copod` |
| `orchestrator/Argus.Orchestrator/Web/DetectorCatalog.cs` | `BestFor` copy, lines 45-129 (all 5 entries) | draft copy from a prior session | Rewrite per scope item 4; flag peer_divergence's "WHICH member" phrase as misleading for N=2 (Pitfall 7 area) |
| `detector/argus_detector/group/peer_divergence.py` | `_MIN_MEMBERS = 3` (line 28), floor check lines 138-144 | unchanged — algorithm-level floor for classic sub-path | **No change** — leave exactly as-is (see resolution above) |
| `detector/argus_detector/servicer.py` | `ScoreGroupBatch` peer_divergence branch, lines 247-273; `FitGroup` peer_divergence branch, lines 359-363 | single unconditional branch on `detector == "peer_divergence"` | Add `len(request.series) == 2` sub-branch in both methods |
| `detector/argus_detector/pyod_detector.py` | whole file (107 lines) | no `is_anomaly()` method | Add small `is_anomaly(score)` method (Pitfall 6) |
| `detector/argus_detector/model_store.py` | `group_slug()` (line 47), `save_pyod`/`load_pyod` (lines 78-109, 192-209) | existing, reusable as-is | No change — reuse directly |
| `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` | `RunGroupBatchAsync` publish branch, lines 239-250 | keys off `isPeer` (Mode string) | Branch on `response.PerMember.Count > 0` vs `response.GroupVerdict != null` (Pitfall 2) |
| `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` | `RunNightlyFitAsync`, lines 500-503 | unconditionally skips all peer_divergence groups | Remove the skip (Pitfall 5) |
| `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs` | `IsPeerDivergence()` (lines 226-227) + 3 call sites (238-241, 279-282, 331-333) | Mode-string only | Add count-aware `UsesPerMemberEntities()` helper (Pitfall 3) |
| `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs` | lines 95-111 | Mode-string only retract branching | Same count-aware helper (Pitfall 4) |
| `detector/tests/test_peer_divergence.py` | `TestPeerDivergenceFloor` class, lines 60-88 | asserts N=2 -> error | **Must keep passing unmodified** — confirms the branch belongs in servicer.py, not in this class |

## MemberPicker.tsx — uncommitted local change (verified, unrelated to Phase 9)

`git diff` on `orchestrator/ui/src/components/MemberPicker.tsx` shows an **unstaged, uncommitted**
change: a new `MIN_QUERY_LENGTH = 2` constant that hides the sensor list entirely until the
operator types at least 2 characters in the search box (previously the full sensor list — "400+
on a typical HA install" per its own comment — rendered unfiltered with an empty query). This is a
search-UX performance/usability fix for the member picker, **not** related to the 2-member group
member-count floor in any way (it gates the *search query string length*, not the number of
selected members). Recommendation: flag this to the planner as pre-existing unstaged work outside
Phase 9's scope — either commit it separately before Phase 9 starts, or leave it uncommitted and
ignore it; it does not need to be touched, extended, or reverted by this phase's plan.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Config-validation floor for `peer_divergence` mode should become 2 (not stay at 3 as ROADMAP scope item 1/5 literally states), because item 2 requires 2-member peer_divergence groups to be accepted | Pitfall 7 / Open Design Question #5 | If wrong, the whole pairwise-delta feature is unreachable through the UI/config-save path — operator cannot create the very groups item 2 is meant to support. Low probability of being wrong given item 2's plain-language requirement, but this reverses the literal text of a ROADMAP bullet, so flag for explicit operator/planner confirmation rather than silently overriding it. |
| A2 | Group-level (not per-member) MQTT/HA entity for 2-member `peer_divergence` groups | Pitfall 3 / Open Design Question #5 | If the operator actually wants two per-member entities (e.g. to preserve dashboard consistency with other peer_divergence groups), the recommended design would need the opposite branch in `DiscoveryPublisher`. Low blast radius to change later — isolated to the `UsesPerMemberEntities` helper — but should be confirmed at discuss/plan time, not assumed silently through to execution. |
| A3 | `PyODDetector` reused completely unmodified for the delta series (only a new `is_anomaly()` accessor added) is sufficient — no new params (e.g. a distinct `contamination` default) are needed for the pairwise case specifically | Don't Hand-Roll / Pitfall 6 | Low risk — `PyODDetector.from_params()` already accepts `threshold`/`contamination` overrides identically to any per-entity MAD detector; if the pairwise case needs different defaults later, that's a param-tuning change, not a structural one. |

## Open Questions

1. **Should the config-validation floor message differ by mode even though the numeric floor is
   the same (2 for both)?**
   - What we know: `groupParams.ts`'s current message is a flat "A group needs at least 3
     members." with no mode-specific text.
   - What's unclear: whether the UI should say something like "Peer-divergence groups need 2
     members (paired comparison) or 3+ members (which-one-diverges)" to set correct operator
     expectations about *why* 2-member peer_divergence behaves differently once created.
   - Recommendation: leave to the planner/UI-SPEC step — this is copywriting, not a structural
     question research can resolve. The functional floor-of-2 change is unambiguous regardless.

2. **Retention/versioning implications for the new `(group_slug, "peer_divergence")` model file
   if an operator later grows a 2-member group to 3+ members (or shrinks a 3+ member group to 2)
   in place, reusing the same `group_id`?**
   - What we know: `ModelStore._prune` keeps 3 versions per `(slug, detector)` key regardless of
     what fit produced them; the registry's in-memory `_detectors` dict would hold a stale
     `PairwiseDeltaDetector`-wrapped object if a group transitions from 2->3+ members between
     nightly fits (classic N>=3 peer_divergence never touches the registry at score time — it
     constructs fresh per call — so a stale pairwise entry left in the registry would simply be
     ignored, not read, once the group has 3+ members; the reverse direction, 3+ -> 2, means the
     registry has no entry yet, `has_model` returns false, and `ScoreGroupBatch` correctly aborts
     with "call FitGroup first" until the next nightly fit runs).
   - What's unclear: whether a bounded window of "no score published" (until the next nightly fit,
     up to ~24h) after a membership-count transition is acceptable, or whether the plan should add
     an explicit re-fit trigger on group save when membership count crosses the 2/3 boundary.
   - Recommendation: treat as an acceptable, bounded degrade (consistent with how joint-mode
     groups already behave — they also require a fit before their first score, and re-fits are
     nightly-only) unless the operator explicitly wants faster convergence after a group edit;
     flag as a possible follow-up task, not a blocker, since it is a pre-existing system property
     (not something Phase 9 introduces) once viewed as "any group edit needs a fit before scoring
     resumes."

## Security Domain

`security_enforcement` is not set to `false` in `.planning/config.json` (absent -> enabled), so
this section is included per protocol, but Phase 9 introduces no new attack surface:

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V5 Input Validation | yes (unchanged) | Existing numeric param validators (`GroupInputValidator`'s `double.TryParse` + Min/Max bounds, mirrored client-side) already cover any new detector params; no new user-facing input fields are introduced by this phase — only floor numbers and internal dispatch logic change |
| V2/V3/V4/V6 | no | No new auth, session, access-control, or cryptography surface — this phase is entirely internal detection-logic and config-validation-number changes |

No new threat patterns apply; the pairwise-delta computation runs entirely server-side inside the
already-trusted detector process on already-validated (unit-consistent, floor-checked) member
data.

## Sources

### Primary (HIGH confidence — direct code reads this session)
- `proto/argus.proto` — full RPC/message contract confirmed unchanged-needed
- `detector/argus_detector/servicer.py`, `registry.py`, `group/peer_divergence.py`,
  `group/multivariate_detector.py`, `pyod_detector.py`, `model_store.py` — full call-path trace
- `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs`,
  `Config/EntitiesConfigLoader.cs`, `Web/GroupInputValidator.cs`, `Web/DetectorCatalog.cs`,
  `Mqtt/DiscoveryPublisher.cs`, `Workers/MqttPublisherWorker.cs`,
  `Config/EntitiesConfig.cs` — full call-path trace
- `orchestrator/ui/src/validation/groupParams.ts`,
  `orchestrator/ui/src/components/MemberPicker.tsx` (+ its uncommitted `git diff`)
- `detector/tests/test_peer_divergence.py` — confirms the floor-of-3 contract that must not break
- `.planning/ROADMAP.md`, `.planning/REQUIREMENTS.md`, `.planning/STATE.md` — locked decisions,
  requirement ID scheme, Phase 05/06/08 decision log

### Secondary (MEDIUM confidence)
- `pip index versions pyod` — confirmed 3.6.0 installed matches project pin, 3.6.1 exists upstream
  (no action needed)

### Tertiary (LOW confidence)
- None — no web search was needed; this phase is entirely internal-codebase reasoning.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new libraries, existing pin confirmed via registry check
- Architecture (dispatch/persistence/proto design): HIGH — every claim traced to an exact file/line;
  the servicer-branch-not-class-branch recommendation is directly evidenced by a currently-passing
  test that would otherwise break
- Pitfalls (the 5 C#-side count-blind branches): HIGH — each is a direct code read, not inference;
  cross-checked against the actual response/entity shapes produced by the joint-mode path that
  already exists
- Floor-wording contradiction (Assumption A1): MEDIUM — the reconciliation is the only logically
  consistent reading of the two roadmap scope items, but it does reverse literal roadmap text, so
  flagged for explicit confirmation rather than presented as settled fact

**Research date:** 2026-07-03
**Valid until:** No external dependency drift risk (no new packages); codebase-internal findings
remain valid until the referenced files change — recommend re-verifying line numbers only if
Phase 9 planning is delayed past other concurrent work touching these same files.
