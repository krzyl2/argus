# Phase 9: 2-Member Groups + Algorithm Guidance Correction - Pattern Map

**Mapped:** 2026-07-03
**Files analyzed:** 14 (13 modified + 1 new)
**Analogs found:** 14 / 14 (13 are self-analogs — same file, existing surrounding code is the pattern; 1 new file has an external analog)

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|--------------------|------|-----------|-----------------|---------------|
| `orchestrator/ui/src/validation/groupParams.ts` | utility (validation) | request-response | itself (existing `validateGroupMembers`/`validateUnitConsistency`) | exact |
| `orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs` | middleware (server validation) | request-response | itself (existing `Validate()`) | exact |
| `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` | config loader | CRUD (config read) | itself (existing member-count guard) | exact |
| `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` (`PeerMinFreshMembers` / `BuildGroupMatrix`) | service (batch job) | batch | itself (existing staleness-policy branch) | exact |
| `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` (`RunGroupBatchAsync` publish branch) | service (batch job) | batch / pub-sub | itself (existing `isPeer` branch, to be replaced with response-shape branch) | exact |
| `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` (`RunNightlyFitAsync` skip) | service (batch job) | batch | itself (existing skip guard, to be deleted) | exact |
| `orchestrator/Argus.Orchestrator/Web/DetectorCatalog.cs` (`Guided()` + `BestFor` copy) | config (static descriptive data) | request-response | itself (existing catalog entries) | exact |
| `detector/argus_detector/servicer.py` (`ScoreGroupBatch`/`FitGroup` peer_divergence branches) | controller (gRPC servicer) | request-response | itself (existing joint-mode branch in the same methods, for the "no fitted model -> abort" and per-verdict-construction idioms) | exact / role-match |
| `detector/argus_detector/pyod_detector.py` (new `is_anomaly` method) | model/service (detector class) | transform | `detector/argus_detector/group/multivariate_detector.py`'s `is_anomaly()` (lines 153-162) | exact (method-level analog, cross-file) |
| `detector/argus_detector/model_store.py` | model (persistence) | file-I/O | read-only reuse — no changes; `save_pyod`/`load_pyod` (lines 78-108) | exact (no modification needed) |
| `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs` (`IsPeerDivergence` + 3 call sites) | service (MQTT discovery) | pub-sub | itself (existing `IsPeerDivergence`/`BuildGroupBinarySensorConfig`/`BuildGroupSensorConfig`/`PublishGroupAsync`) | exact |
| `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs` (retract branch, lines 95-111) | worker (hot-reload/retract) | event-driven / pub-sub | itself (existing `isPeer` retract branch) | exact |
| **NEW:** `detector/argus_detector/group/pairwise_delta.py` | service (detector class, group-adjacent) | transform / batch | `detector/argus_detector/group/multivariate_detector.py` (class shape: `__init__`/`fit`/`score_batch`/`is_fitted`/`is_anomaly`) + `detector/argus_detector/pyod_detector.py` (the actual MAD wrapping to delegate to, unmodified) | role-match (structural analog: multivariate_detector.py; delegate analog: pyod_detector.py) |

## Pattern Assignments

### `orchestrator/ui/src/validation/groupParams.ts` (utility, request-response)

**Analog:** itself — existing floor constant + validator function (lines 8-21)

**Current pattern to change:**
```typescript
const MIN_MEMBERS = 3;

const MSG_BELOW_FLOOR = 'A group needs at least 3 members.';

export function validateGroupMembers(members: string[]): string | null {
  if (members.length < MIN_MEMBERS) {
    return MSG_BELOW_FLOOR;
  }
  return null;
}
```
**Change:** `MIN_MEMBERS = 2`; update `MSG_BELOW_FLOOR` copy to match the new floor (mirror whatever final wording `GroupInputValidator.cs` uses — keep them byte-identical per this file's own header comment "mirrors GroupInputValidator.cs"). `validateUnitConsistency` (lines 28-40) is untouched — no change needed there.

---

### `orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs` (middleware, request-response)

**Analog:** itself — `MinMembers` constant (line 21) + floor check (lines 92-96)

**Current pattern to change:**
```csharp
private const int MinMembers = 3;
...
if (group.Members.Count < MinMembers)
{
    errors.Add($"Group '{group.GroupId}' needs at least {MinMembers} members.");
    continue;
}
```
**Change:** `MinMembers = 2`. Keep the exact error-message interpolation shape (`$"Group '{group.GroupId}' needs at least {MinMembers} members."`) so the message stays generated from the constant, not hand-typed — this is the pattern already in place and requires zero string-literal changes, only the constant value.

**Consistency note:** `IsModeDetectorConsistent` (lines 34-41) and the unit-consistency check (lines 129-143) are structurally unrelated to member count and need no change — do not touch them.

---

### `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` (config loader, CRUD)

**Analog:** itself — floor check (lines 111-117)

**Current pattern to change:**
```csharp
if (group.Members is null || group.Members.Count < 3)
{
    logger.LogWarning(LogEvents.GroupRejected,
        "Group '{GroupId}' has {MemberCount} member(s), below the minimum of 3 — skipped",
        group.GroupId, group.Members?.Count ?? 0);
    continue;
}
```
**Change:** `< 3` -> `< 2`; update the log message's literal `"minimum of 3"` text to `"minimum of 2"` (this file, unlike `GroupInputValidator.cs`, hard-codes the number in the message string rather than interpolating a constant — match existing style, do not introduce a new constant here since none exists today).

---

### `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` — three separate edits

**1. `PeerMinFreshMembers` / `BuildGroupMatrix` (Pitfall 1)**

**Analog:** itself, lines 47 and 299-333 (`BuildGroupMatrix`)

**Current pattern:**
```csharp
private const int PeerMinFreshMembers = 3;
...
if (!isPeer && staleMembers.Count > 0)
{
    skipWholeGroup = true;
    return new Dictionary<string, List<double>>();
}

var activeMembers = group.Members.Where(m => !staleMembers.Contains(m)).ToList();

if (isPeer && activeMembers.Count < PeerMinFreshMembers)
{
    skipWholeGroup = true;
    return new Dictionary<string, List<double>>();
}
```
**Change:** Gate the drop-stale-then-require-floor policy on `group.Members.Count >= 3` instead of unconditionally on `isPeer`. For exactly-2-member peer_divergence groups, route into the SAME branch joint mode already uses (`!isPeer && staleMembers.Count > 0` — skip-whole-group-on-any-staleness), because the copy-paste-ready joint-mode pattern immediately above is already the correct policy for a 2-member pairwise-delta group (both members must be present). Concretely: change the first `if` condition from `!isPeer` to `(!isPeer || group.Members.Count < 3)`, and keep the second `if` (`isPeer && activeMembers.Count < PeerMinFreshMembers`) unchanged — it becomes unreachable for 2-member peer groups once the first branch catches them, exactly mirroring how `PeerDivergenceDetector`'s own N>=3 floor becomes unreachable at the Python layer (same "leave the old contract untouched, make it unreachable for the new case" pattern documented in RESEARCH.md).

**2. `RunGroupBatchAsync` publish branch (Pitfall 2)**

**Analog:** itself, lines 239-285 — the existing `if (isPeer) { foreach response.PerMember ... } else { var v = response.GroupVerdict; ... }` structure

**Current pattern:**
```csharp
if (isPeer)
{
    foreach (var v in response.PerMember)
    {
        await _statePublisher.PublishGroupScoreAsync(group.GroupId, v.EntityId, v.Score ?? 0.0, ct);
        await _statePublisher.PublishGroupFlagAsync(group.GroupId, v.EntityId, v.IsAnomaly, ct);
    }
    ...
}
else
{
    var v = response.GroupVerdict;
    await _statePublisher.PublishGroupScoreAsync(group.GroupId, null, v.Score ?? 0.0, ct);
    ...
}
```
**Change:** Replace `if (isPeer)` with `if (response.PerMember.Count > 0)` and the `else` with `else if (response.GroupVerdict != null)`. The body of each branch is copied verbatim — this is a condition-swap only, no new logic, keeping every existing `_statePublisher` call, log message, and the `Contributions`-sorting block (lines 257-268) exactly as-is (the 2-member peer_divergence case naturally falls into the existing group-level `else` branch, which already handles `null`/empty `Contributions` correctly for joint detectors with no attribution — same code path, new caller).

**3. `RunNightlyFitAsync` skip (Pitfall 5)**

**Analog:** itself, lines 500-503

**Current pattern:**
```csharp
foreach (var group in _liveConfig.Get().Groups)
{
    if (string.Equals(group.Mode, "peer_divergence", StringComparison.OrdinalIgnoreCase))
        continue;

    try
    {
        await RunGroupFitAsync(group, ct);
    }
    ...
```
**Change:** Delete the `if (string.Equals(...)) continue;` guard entirely (pure subtraction, per RESEARCH.md). `RunGroupFitAsync` is called unconditionally for every group, exactly as it already is for joint-mode groups today — no new C# code, only removal of the special-case skip. Python's `FitGroup` then decides internally what "fit" means for `peer_divergence` based on member count.

---

### `orchestrator/Argus.Orchestrator/Web/DetectorCatalog.cs` (config, request-response)

**Analog:** itself — `Guided()` (lines 136-140) and each `DetectorCatalogEntry`'s `BestFor` string (lines 45-129)

**Current pattern (Guided):**
```csharp
public static List<GuidedAnswer> Guided() =>
[
    new GuidedAnswer("together", "ecod"),
    new GuidedAnswer("diverges", "peer_divergence"),
];
```
**Change:** `new GuidedAnswer("together", "ecod")` -> `new GuidedAnswer("together", "copod")`. Structure (list-of-records) is unchanged — one string literal swap.

**Current pattern (BestFor, peer_divergence entry, lines 46-49):**
```csharp
BestFor: "Best for a group of similar sensors (e.g. tire pressures, per-room temperatures) " +
         "where you want to know WHICH member is diverging from the others. Sensitivity " +
         "directly changes how far a member must drift from its peers before it is flagged.",
```
**Change guidance:** Rewrite all 5 `BestFor` strings using this file's existing multi-line string-concatenation style (`"..." + "..." + "..."`, each line ending with `+` and a trailing space before the closing quote). Per ROADMAP scope item 4 and RESEARCH's Open Design Question #5, the `peer_divergence` entry's "WHICH member is diverging" phrasing is now misleading for the 2-member pairwise case (no attribution is possible there) — flag this explicitly for operator sign-off in the plan; do not silently invent final copy since "operator will personally edit/redact before ship" per ROADMAP. Preserve the existing per-entry structure (`Name`, `BestFor`, `Presets`, `ParamSchema`) — only the `BestFor` string values change, no schema/preset changes.

---

### `detector/argus_detector/servicer.py` (controller, request-response) — `ScoreGroupBatch` and `FitGroup`

**Analog:** itself — the existing joint-mode branch in both methods (lines 275-321 for `ScoreGroupBatch`, lines 365-370 for `FitGroup`), which already demonstrates the "check registry.has_model -> abort if missing -> get_model -> score_batch -> is_anomaly -> build Verdict" idiom this new branch must copy.

**Existing peer_divergence branch to extend (`ScoreGroupBatch`, lines 247-273):**
```python
if detector == "peer_divergence":
    from argus_detector.group.peer_divergence import PeerDivergenceDetector
    model = PeerDivergenceDetector.from_params(dict(request.params))
    scores, flags, error = model.score_batch(matrix)
    if error:
        return argus_pb2.GroupScoreResponse(ok=True, error=error)
    member_ids = [s.member_id for s in request.series]
    last_scores = scores[-1]
    last_flags = flags[-1]
    per_member = [
        argus_pb2.Verdict(
            entity_id=member_ids[i],
            score=wrappers_pb2.DoubleValue(value=last_scores[i]),
            is_anomaly=bool(last_flags[i]),
            detector=detector,
            timestamp=ts,
        )
        for i in range(len(member_ids))
    ]
    return argus_pb2.GroupScoreResponse(per_member=per_member, ok=True)
```

**Existing joint-mode branch to copy the shape of (lines 275-321):**
```python
if not self._registry.has_model(group_slug, detector):
    context.abort(
        grpc.StatusCode.INVALID_ARGUMENT,
        f"no fitted model for group {request.group_id!r}/{detector}; call FitGroup first",
    )
    return None

model = self._registry.get_model(group_slug, detector)
scores, contributions = model.score_batch(matrix)
group_score = scores[-1]
is_anomaly = model.is_anomaly(group_score)
group_verdict = argus_pb2.Verdict(
    entity_id=group_slug,
    score=wrappers_pb2.DoubleValue(value=group_score),
    is_anomaly=is_anomaly,
    detector=detector,
    timestamp=ts,
)
...
return argus_pb2.GroupScoreResponse(
    group_verdict=group_verdict,
    contributions=feature_contributions,  # empty list when contributions is None
    ok=True,
)
```

**New sub-branch to add** (before constructing `PeerDivergenceDetector`, inside the existing `if detector == "peer_divergence":` block): branch on `len(request.series) == 2`. When true, delegate to a new registry-backed `PairwiseDeltaDetector` (from the new `pairwise_delta.py`) using the exact joint-mode idiom above (`has_model` check -> abort with the SAME message format `f"no fitted model for group {request.group_id!r}/{detector}; call FitGroup first"` -> `get_model` -> `score_batch` -> `is_anomaly` -> build ONE `group_verdict` Verdict, empty `per_member`, empty `contributions`). Persist via `registry.register(group_slug, "peer_divergence", fitted)` / `model_store.save_pyod(group_slug, "peer_divergence", version, fitted, entity_id=group_slug)` (see model_store.py excerpt below) instead of `save_group_bundle` (no scaler needed — single derived feature).

**Existing FitGroup peer_divergence branch to extend (lines 359-363):**
```python
if detector == "peer_divergence":
    self._registry.fit_one(group_slug, detector, matrix, params=dict(request.params))
    return argus_pb2.FitGroupResponse(ok=True)
```
**New sub-branch:** `len(request.series) == 2` -> compute `delta = np.array(request.series[0].values) - np.array(request.series[1].values)`, fit a `PairwiseDeltaDetector` on it, persist via `model_store.save_pyod` (mirroring the joint-mode `save_group_bundle` call at line ~370, but using `save_pyod` since there is no scaler/bundle). `len(request.series) >= 3` keeps the existing no-op `fit_one`/no-persist call unchanged.

---

### `detector/argus_detector/pyod_detector.py` (model/service, transform) — add `is_anomaly`

**Analog:** `detector/argus_detector/group/multivariate_detector.py` lines 153-162

```python
def is_anomaly(self, score: float) -> bool:
    """True if score exceeds the underlying PyOD detector's fitted threshold_.

    WR-02: public accessor so callers (servicer.py) do not need to reach
    into the private `_model` attribute to apply the threshold decision.
    Deliberately does NOT call predict() — see score_batch() docstring:
    re-invoking decision_function() would corrupt the just-extracted
    ECOD/COPOD self.O attribution (RESEARCH.md Pitfall 1).
    """
    return bool(score > self._model.threshold_)
```
**New code for `pyod_detector.py`:** Add the same method verbatim (MAD has a `threshold_` attribute post-fit like every PyOD model; the ECOD/COPOD self.O caveat in the docstring does not apply here — simplify the docstring to drop that irrelevant caveat, but keep the WR-02 "public accessor, never reach into `_model`" rationale sentence since it's the actual reason this method must exist rather than being inlined in servicer.py).

---

### `detector/argus_detector/model_store.py` — read-only reuse, no modification

**Analog:** itself, `save_pyod`/`load_pyod` (lines 78-108) and `group_slug()` (lines 47-54)

```python
def group_slug(group_id: str) -> str:
    return f"group_{group_id}"

def save_pyod(
    self,
    entity_slug: str,
    detector: str,
    version: int,
    model: object,
    entity_id: str | None = None,
) -> None:
    d = self._model_dir(entity_slug, detector, version)
    d.mkdir(parents=True, exist_ok=True)
    joblib.dump(model, d / "model.joblib")
    self._write_version_json(d, entity_slug, detector, version)
    self._write_entity_id(d, entity_id if entity_id is not None else entity_slug)
    self._update_latest(entity_slug, detector, version)
    self._prune(entity_slug, detector)
```
**Usage for Phase 9:** Call `model_store.save_pyod(group_slug(group_id), "peer_divergence", version, fitted_pairwise_detector, entity_id=group_slug(group_id))` — same call shape as any per-entity MAD model. Key `(group_slug, "peer_divergence")` is confirmed never written today (classic peer_divergence's FitGroup branch never persists), so there is zero collision risk. Do not add a new save/load method — this file needs no changes.

---

### `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs` (Pitfall 3)

**Analog:** itself — `IsPeerDivergence` (lines 226-227) and its 3 call sites (238-241, 279-282, 331-333)

**Current pattern:**
```csharp
private static bool IsPeerDivergence(GroupConfig group)
    => string.Equals(group.Mode, "peer_divergence", StringComparison.OrdinalIgnoreCase);
```
used at:
```csharp
var isPeer = IsPeerDivergence(group);
var name = isPeer ? $"{group.FriendlyName} {memberId} anomalia" : $"{group.FriendlyName} anomalia";
...
var memberIds = IsPeerDivergence(group) ? group.Members.Cast<string?>() : [null];
```
**Change:** Add a new count-aware helper next to `IsPeerDivergence`, matching its exact style (private static bool, one-line expression body):
```csharp
private static bool UsesPerMemberEntities(GroupConfig group)
    => IsPeerDivergence(group) && group.Members.Count >= 3;
```
Replace `IsPeerDivergence(group)` with `UsesPerMemberEntities(group)` at all 3 call sites listed above (`BuildGroupBinarySensorConfig`, `BuildGroupSensorConfig`, `PublishGroupAsync`'s `memberIds` line) — no other logic in these methods changes; this is a drop-in rename/condition-swap using the identical ternary/`? :` shapes already present.

---

### `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs` (Pitfall 4)

**Analog:** itself, lines 95-111 (`isPeer` local variable computed twice inline, once per old-group path)

**Current pattern:**
```csharp
var isPeer = string.Equals(oldGroup.Mode, "peer_divergence", StringComparison.OrdinalIgnoreCase);
if (!isPeer) continue; // joint groups have no per-member diff
...
var isPeer = string.Equals(oldGroup.Mode, "peer_divergence", StringComparison.OrdinalIgnoreCase);
IEnumerable<string?> removedAll = isPeer
    ? oldGroup.Members.Cast<string?>()
    : [null];
```
**Change:** Replace both inline `isPeer` computations with a call to the same `UsesPerMemberEntities`-style helper added to `DiscoveryPublisher.cs` — either expose it as `internal static` on `DiscoveryPublisher` and call `DiscoveryPublisher.UsesPerMemberEntities(oldGroup)` from this file (preferred — single source of truth, avoids a 3rd private copy of the same logic), or duplicate the identical one-line helper locally if cross-class visibility is undesired per existing conventions. Either way, the count check (`oldGroup.Members.Count >= 3`) must be added alongside the existing Mode-string check at both call sites.

---

## Shared Patterns

### Mode-string-only branch -> count-aware / response-shape-aware branch
**Source:** RESEARCH.md Pitfalls 1-4; canonical fix pattern demonstrated by `DiscoveryPublisher.IsPeerDivergence` -> `UsesPerMemberEntities`
**Apply to:** `BatchSchedulerWorker.cs` (`BuildGroupMatrix`, `RunGroupBatchAsync`), `DiscoveryPublisher.cs` (3 call sites), `MqttPublisherWorker.cs` (2 call sites)
```csharp
// OLD: var isPeer = string.Equals(group.Mode, "peer_divergence", ...);
// NEW: var usesPerMember = IsPeerDivergence(group) && group.Members.Count >= 3;
//      -- or, for the batch-publish branch, react to RPC response SHAPE instead:
// if (response.PerMember.Count > 0) { ... } else if (response.GroupVerdict != null) { ... }
```
Every C#-side fix in this phase is a variant of this one pattern — do not invent a different idiom per file.

### PyOD wrapper class shape (fit / score_batch / is_fitted / is_anomaly)
**Source:** `detector/argus_detector/pyod_detector.py` (per-entity) and `detector/argus_detector/group/multivariate_detector.py` (group joint) — both share this shape
**Apply to:** new `detector/argus_detector/group/pairwise_delta.py`
```python
class PairwiseDeltaDetector:
    def __init__(self, threshold: float = ..., contamination: float = ...) -> None:
        self._detector = PyODDetector(threshold=threshold, contamination=contamination)  # delegate, don't reinvent

    @classmethod
    def from_params(cls, params: dict[str, str]) -> "PairwiseDeltaDetector": ...

    @staticmethod
    def compute_delta(series_a: list[float], series_b: list[float]) -> list[float]:
        return (np.array(series_a, dtype=float) - np.array(series_b, dtype=float)).tolist()

    def fit(self, delta: list[float]) -> None:
        self._detector.fit(delta)

    def score_batch(self, delta: list[float]) -> list[float]:
        return self._detector.score_batch(delta)

    @property
    def is_fitted(self) -> bool:
        return self._detector.is_fitted

    def is_anomaly(self, score: float) -> bool:
        return self._detector.is_anomaly(score)  # after adding is_anomaly to PyODDetector, see below
```
This wraps `PyODDetector` unmodified (per ROADMAP's explicit "reusing proven univariate anomaly detection... not inventing new group math") rather than subclassing or duplicating MAD logic — matches the `Don't Hand-Roll` table in RESEARCH.md exactly.

### `_cast_float` param-casting idiom
**Source:** identical private module-level function duplicated verbatim in `pyod_detector.py` (lines 26-34), `multivariate_detector.py` (lines 45-53), and `peer_divergence.py` (lines 37-45)
**Apply to:** new `pairwise_delta.py`'s `from_params()` classmethod
```python
def _cast_float(params: dict[str, str], key: str, default: float) -> float:
    raw = params.get(key)
    if raw is None:
        return default
    try:
        return float(raw)
    except (ValueError, TypeError):
        return default
```
Copy this exact function into `pairwise_delta.py` (the codebase's established convention is per-module duplication of this small helper, not a shared import — follow that, don't refactor it into a shared utility as part of this phase).

### `is_anomaly` public-accessor convention (WR-02)
**Source:** `multivariate_detector.py` lines 153-162 (see full excerpt above)
**Apply to:** `pyod_detector.py` (new method) and `pairwise_delta.py` (delegates to it)
Never let `servicer.py` reach into `_model.threshold_`/`_detector._model` directly — always go through a public `is_anomaly(score)` method, per the established WR-02 convention already enforced for the joint-mode path.

## No Analog Found

None — every file in this phase's 14-file list has at least one direct or structural analog identified above; the new `pairwise_delta.py` file's design is fully covered by combining the `multivariate_detector.py` class-shape analog with the `pyod_detector.py` delegation target.

## Metadata

**Analog search scope:** `orchestrator/ui/src/validation/`, `orchestrator/Argus.Orchestrator/{Web,Config,Batch,Mqtt,Workers}/`, `detector/argus_detector/`, `detector/argus_detector/group/`, `detector/tests/`
**Files scanned:** 14 target files + 2 additional analog-only reads (`multivariate_detector.py` as structural analog for the new file, `peer_divergence.py` for the floor-contract/test-preservation context)
**Pattern extraction date:** 2026-07-03
