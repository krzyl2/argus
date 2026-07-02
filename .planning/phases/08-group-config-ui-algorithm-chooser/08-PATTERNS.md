# Phase 8: Group Config UI + Algorithm Chooser - Pattern Map

**Mapped:** 2026-07-02
**Files analyzed:** 28 (Python: 4, .NET: 10, SPA: 14)
**Analogs found:** 26 / 28 (2 have no exact analog — new UI-only components, classified role-match)

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `detector/argus_detector/group/peer_divergence.py` (add `from_params`) | service (model factory) | transform | `detector/argus_detector/pyod_detector.py` `from_params()` | exact |
| `detector/argus_detector/group/multivariate_detector.py` (add param-aware `__init__`) | service (model factory) | transform | `detector/argus_detector/pyod_detector.py` `from_params()` + `_DETECTOR_FACTORY` | exact |
| `detector/argus_detector/registry.py` `_create_detector` (thread `params`) | service | transform | same file, `mad`/`robust_zscore` branch (already takes no params — needs a params-aware branch mirroring hst) | role-match |
| `detector/argus_detector/servicer.py` `ScoreGroupBatch`/`FitGroup` (pass `request.params`) | controller (RPC handler) | request-response | same file — `PyODDetector`/`EntityDetector` per-entity `ScoreBatch` path already threads `params` (see `registry.fit_one`) | exact (same file, sibling method) |
| `detector/tests/test_peer_divergence.py` (add threshold-param tests) | test | transform | existing file itself | exact |
| `detector/tests/test_group_multivariate.py` (add contamination/n_estimators tests) | test | transform | existing file itself | exact |
| `detector/tests/test_servicer.py` (add params pass-through test) | test | request-response | existing file itself | exact |
| `orchestrator/Argus.Orchestrator/Web/GroupsEndpoints.cs` (or inline `Program.cs`) — `GET /api/groups`, `POST /api/groups/save` | controller/route | CRUD | `Program.cs` `/api/sensors` (GET) + `/api/sensors/save` (POST) | exact |
| `orchestrator/Argus.Orchestrator/Web/DetectorCatalog.cs` — `GET /api/detectors/catalog` | controller/route + config | request-response | `Program.cs` `/api/detectors/defaults` + `Web/DetectorDefaults.cs` | exact |
| `orchestrator/Argus.Orchestrator/Batch/GroupStatusCache.cs` | model/cache | event-driven | `Health/ArgusHealthSignals.cs` (volatile fields) + `Ha/HaSensorRegistry.cs` (volatile snapshot swap) | exact |
| `orchestrator/Argus.Orchestrator/Web/GroupsEndpoints.cs` — `GET /api/groups/{id}/status` | controller/route | request-response | `Program.cs` `/api/sensors` (GET, live-config read pattern) | role-match |
| `orchestrator/Argus.Orchestrator/Web/GroupSaveRequest.cs` | model (DTO) | CRUD | `Web/SaveRequest.cs` | exact |
| `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` (inject `IGroupStatusCache`, populate on joint-mode branch) | service (background worker) | event-driven | same file — existing `else` (joint) branch at lines ~233–254 | exact (same file, extend) |
| `orchestrator/Argus.Orchestrator/Ha/HaWebSocketClient.cs` (add `GetAreaRegistryAsync`/`GetEntityRegistryAsync`) | service (WS client) | request-response | same file — existing `GetStatesAsync()` | exact |
| `orchestrator/Argus.Orchestrator/Ha/HaSensorRegistry.cs` (`HaSensorEntry` gains `AreaName`/`Domain`) | model/cache | event-driven | same file — `UpdateSnapshot` | exact |
| `orchestrator/Argus.Orchestrator/*Tests/GroupsEndpointsTests.cs` | test | request-response | existing orchestrator endpoint test patterns (Phase 7 `/api/sensors/save` tests) | role-match |
| `orchestrator/ui/src/router.ts` (add `#/groups`, `#/groups/new`, `#/groups/:id`) | route | request-response | same file (existing hash-router pattern) | exact |
| `orchestrator/ui/src/api/client.ts` | utility | request-response | no change needed — reuse verbatim (`apiGet`/`apiPost`) | exact (unmodified reuse) |
| `orchestrator/ui/src/api/types.ts` (add `GroupConfig`/`DetectorCatalog`/`GroupStatus` DTOs) | model (types) | transform | same file — existing `SensorEntry`/`SaveRequest`/`DetectorEntry` shapes | exact |
| `orchestrator/ui/src/state/groups.ts` | store (signals) | CRUD | `state/sensors.ts` | exact |
| `orchestrator/ui/src/state/groupEditor.ts` | store (signals) | event-driven | `state/sensors.ts` (mutator-function pattern) | role-match |
| `orchestrator/ui/src/validation/groupParams.ts` | utility (validation) | transform | `validation/detectorParams.ts` | exact |
| `orchestrator/ui/src/components/GroupList.tsx` | component | CRUD | `components/SensorList.tsx` | exact |
| `orchestrator/ui/src/components/GroupListRow.tsx` | component | CRUD | `components/SensorListRow.tsx` | exact |
| `orchestrator/ui/src/components/GroupEditorForm.tsx` | component | CRUD | `components/SensorsPage.tsx` (top-level page orchestrating state + save) | role-match |
| `orchestrator/ui/src/components/MemberPicker.tsx` | component | CRUD | `components/SensorList.tsx` (multi-select mode) | role-match |
| `orchestrator/ui/src/components/AlgorithmChooser.tsx` | component | event-driven | `components/DetectorDisclosure.tsx` (disclosure + list-of-entries orchestration) | role-match (closest Phase 7 component per UI-SPEC) |
| `orchestrator/ui/src/components/GuidedFlowStep.tsx` | component | event-driven | no direct analog — new state-machine-driven UI | role-match (closest: `DetectorEntry.tsx` select+conditional-render pattern) |
| `orchestrator/ui/src/components/AlgorithmCard.tsx` | component | request-response | `components/DetectorEntry.tsx` (one selectable/editable unit in a list) | role-match |
| `orchestrator/ui/src/components/SensitivityPresetPicker.tsx` | component | event-driven | `components/DetectorEntry.tsx` (select + param sync) | role-match |
| `orchestrator/ui/src/components/AdvancedParamsDisclosure.tsx` | component | event-driven | `components/DetectorDisclosure.tsx` + `components/DetectorParamGrid.tsx` | exact |
| `orchestrator/ui/src/components/AttributionPanel.tsx` | component | streaming (poll) | no direct analog — new polling component | role-match (closest: `SensorSearchInput.tsx` for the debounce/cleanup-on-unmount timer discipline) |
| `orchestrator/ui/src/components/AttributionBar.tsx` | component | transform (render) | no direct analog — new CSS-bar visualization | role-match (closest: `SensorListRow.tsx` for row-rendering conventions) |
| `orchestrator/ui/src/components/AreaSuggestionBanner.tsx` | component | event-driven | `components/EmptyState.tsx` (conditional informational banner pattern) | role-match |
| `orchestrator/ui/src/components/SensorSearchInput.tsx` (extend predicate) | component | transform | same file (modify in place) | exact |
| `orchestrator/ui/src/components/SensorList.tsx` (add area-grouping mode) | component | transform | same file (modify in place) | exact |

## Pattern Assignments

### `detector/argus_detector/group/peer_divergence.py` (service, transform)

**Analog:** `detector/argus_detector/pyod_detector.py`

**`from_params` pattern to replicate** (pyod_detector.py lines 26-72):
```python
_DEFAULT_THRESHOLD = 3.5
_DEFAULT_CONTAMINATION = 0.1

def _cast_float(params: dict[str, str], key: str, default: float) -> float:
    """Cast a string param to float, returning default if key absent or invalid."""
    raw = params.get(key)
    if raw is None:
        return default
    try:
        return float(raw)
    except (ValueError, TypeError):
        return default

class PyODDetector:
    def __init__(self, threshold: float = _DEFAULT_THRESHOLD, contamination: float = _DEFAULT_CONTAMINATION) -> None:
        self._model = MAD(threshold=threshold, contamination=contamination)
        self._fitted = False

    @classmethod
    def from_params(cls, params: dict[str, str]) -> "PyODDetector":
        threshold = _cast_float(params, "threshold", _DEFAULT_THRESHOLD)
        contamination = _cast_float(params, "contamination", _DEFAULT_CONTAMINATION)
        return cls(threshold=threshold, contamination=contamination)
```

**Apply to `peer_divergence.py`:** today `_THRESHOLD = 3.5` is a **module constant** consumed directly inside `score_group()` (line 74: `flags = np.abs(scores) > _THRESHOLD`) — not an instance attribute. Convert to an instance field set in `__init__`, add `from_params(params)` using the exact `_cast_float` helper (copy it, or share via a small util — codebase currently duplicates this pattern per-detector, follow that convention, do not introduce a shared module for a 2-line helper per Rule 2/Simplicity First). `score_batch`/`score_group` must read `self._threshold` instead of the module constant. Existing `_MIN_MEMBERS = 3` guard, `_MAD_CONST`/`_MEAN_AD_CONST` fallback logic (lines 25-56) are untouched — floor and MAD=0 fallback are NOT sensitivity-tunable per CONTEXT.md scope.

**Error/degrade pattern to preserve:** the existing `(None, None, error_string)` triple-return for the below-floor case (lines 108-113) must NOT be touched by the param change — sensitivity threshold and member-floor are orthogonal guards.

---

### `detector/argus_detector/group/multivariate_detector.py` (service, transform)

**Analog:** `detector/argus_detector/pyod_detector.py` (`from_params`) + own `_DETECTOR_FACTORY` dict (lines 37-42)

**Current factory (no params, to be extended)**:
```python
_DETECTOR_FACTORY = {
    "ecod": lambda: __import__("pyod.models.ecod", fromlist=["ECOD"]).ECOD(),
    "copod": lambda: __import__("pyod.models.copod", fromlist=["COPOD"]).COPOD(),
    "pca": lambda: __import__("pyod.models.pca", fromlist=["PCA"]).PCA(standardization=False),
    "iforest": lambda: __import__("pyod.models.iforest", fromlist=["IForest"]).IForest(),
}
```

**Pattern to apply:** change each factory lambda to accept a `params: dict[str, str]` argument and read `contamination` (all 4 detectors) / `n_estimators` (iforest only) via the same `_cast_float`/int-cast-with-default idiom as `pyod_detector.py`. `GroupMultivariateDetector.__init__(self, detector_name: str, params: dict[str, str] | None = None)` — keep `standardization=False` hardcoded for PCA (RESEARCH Pitfall 2, do NOT make configurable — it is a correctness constant, not a sensitivity knob). Preserve `_ATTRIBUTABLE = {"ecod", "copod"}` and the `self._model.O[-len(matrix):]` synchronous-read discipline (lines 96-103) untouched — param wiring must not touch the attribution extraction code path.

**Critical constraint (RESEARCH Pitfall 2, factual — must inform the .NET catalog's copy, not just the Python code):** `contamination` only moves `self._model.threshold_` (used by `is_anomaly()`), never `decision_function()`'s continuous score. Do not write test assertions or catalog copy implying it changes the score.

---

### `detector/argus_detector/registry.py` `_create_detector` (service, transform)

**Analog:** same file — the `hst`/`stl`/`mad` branches don't take params today either (params are applied later via `fit_one`/`EntityDetector.from_params`); group branches (lines 256-263) construct with zero args:
```python
if detector == "peer_divergence":
    from argus_detector.group.peer_divergence import PeerDivergenceDetector
    return PeerDivergenceDetector()
if detector in ("ecod", "copod", "pca", "iforest"):
    from argus_detector.group.multivariate_detector import GroupMultivariateDetector
    return GroupMultivariateDetector(detector)
```
Check how `_create_detector` is invoked from `fit_one`/`registry.py` line ~147/158 to see if `params` is already threaded to `_create_detector` for the `mad`→`PyODDetector.from_params` path — if so, mirror exactly the same call convention for the two group branches (pass `params` through, call `.from_params(params)` / constructor with `params=params` instead of the current zero-arg form).

---

### `detector/argus_detector/servicer.py` `ScoreGroupBatch`/`FitGroup` (controller, request-response)

**Analog:** same file, the group-detector construction sites already visible:
```python
# ScoreGroupBatch, line 249-250 (peer_divergence, stateless — constructed fresh per call)
from argus_detector.group.peer_divergence import PeerDivergenceDetector
model = PeerDivergenceDetector()
```
```python
# FitGroup, line 359 / 364-365 (joint-multivariate, via registry.fit_one)
self._registry.fit_one(group_slug, detector, matrix)
model = self._registry.get_model(group_slug, detector)
```
**Apply:** thread `request.params` (a `dict`/`MessageMapContainer`, cast to plain `dict[str, str]`) into both call sites — `PeerDivergenceDetector.from_params(dict(request.params))` for the stateless path, and pass `params=dict(request.params)` through `registry.fit_one(...)` → `_create_detector` for the joint-mode fit path. Mirror the per-entity precedent already in this file's other RPC methods where `params` from the request travels to `EntityDetector.from_params`/`PyODDetector.from_params` (verify exact per-entity call site in `ScoreBatch`/`FitOne` before writing — not shown in the excerpts read this session, but the naming/threading convention is consistent across this file).

**Error handling pattern to preserve exactly (both methods, verbatim structure):**
```python
if not request.group_id:
    context.abort(grpc.StatusCode.INVALID_ARGUMENT, "empty group_id")
    return None  # WR-06: after abort, gRPC ignores the return value
...
except Exception as e:
    logger.exception("unexpected error in ScoreGroupBatch for %s", request.group_id)
    return argus_pb2.GroupScoreResponse(ok=False, error=str(e))
```
Bad/non-numeric param values must silently fall back to defaults (via `_cast_float`'s try/except) — never let a malformed `params` value 500 the RPC (matches Security Domain "Tampering" mitigation in RESEARCH.md).

---

### `detector/tests/test_peer_divergence.py`, `test_group_multivariate.py`, `test_servicer.py` (test, transform/request-response)

**Analog:** the existing test files themselves — read their current fixture/assertion style before adding cases (not read this session; follow existing `pytest` conventions in-file). New assertions needed:
- `test_peer_divergence.py`: two different `threshold` params on identical fixture data produce different `flags` arrays (a lower threshold flags more members) — directly tests Pitfall 1 from RESEARCH (currently zero Python file changes = pitfall not addressed).
- `test_group_multivariate.py`: two different `contamination` values produce different `is_anomaly()` results for a borderline score, but IDENTICAL `decision_function()`/score output — this test structurally encodes Pitfall 2's factual constraint (contamination affects threshold only, never score) so future changes can't silently break that guarantee (Rule 9: tests verify intent, not just behavior).
- `test_servicer.py`: a `ScoreGroupBatch`/`FitGroup` call with `request.params={"contamination": "0.3"}` reaches the constructed detector (assert via a mock/spy on `_create_detector` or by checking resulting `is_anomaly` differs from the no-params call).

---

### `orchestrator/Argus.Orchestrator/Web/GroupsEndpoints.cs` — `GET /api/groups`, `POST /api/groups/save` (controller, CRUD)

**Analog:** `Program.cs` lines 242-266 (`GET /api/sensors`) and lines 287-429 (`POST /api/sensors/save`)

**GET pattern** (Program.cs lines 242-266, copy structure exactly):
```csharp
app.MapGet("/api/sensors", (HttpRequest req, IHaSensorRegistry registry, ILiveEntitiesConfig liveCfg) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);
    var q = req.Query["q"].FirstOrDefault() ?? "";
    var entries = registry.GetFiltered(q);
    var payload = entries.Select(e => new { entityId = e.EntityId, /* ... */ });
    return Results.Json(new { entries = payload });
});
```
For `GET /api/groups`: `if (!IsAuthorizedRequest(...)) return 403;` then `var groups = liveCfg.Get().Groups;` (CFG-04 rule: read live config each request, never a captured stale reference — see Program.cs comment at line 240-241) then `Results.Json(new { groups })`.

**POST save pattern** (Program.cs lines 287-429) — full skeleton to replicate:
```csharp
app.MapPost("/api/sensors/save", async (HttpRequest req, /* deps */, CancellationToken ct) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);
    try
    {
        SaveRequest? body;
        try { body = await req.ReadFromJsonAsync<SaveRequest>(ct); }
        catch (System.Text.Json.JsonException)
        {
            return Results.Json(new { ok = false, kind = "error", reason = "invalid request body" },
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (body is null) { /* same 400 */ }

        // ... build/validate model from body ...
        var validationErrors = InputValidator.Validate(/* ... */);
        if (validationErrors.Count > 0)
        {
            logger.LogWarning(LogEvents.UiValidationBlocked, "...");
            return Results.Json(new { ok = false, kind = "validation", errorCount = validationErrors.Count });
        }

        // Serialize via YamlDotNet SerializerBuilder + UnderscoredNamingConvention (never string-format YAML)
        var serializer = new SerializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build();
        var fullYaml = serializer.Serialize(root);
        await writer.WriteAsync(entitiesPath, fullYaml, ct);
        File.WriteAllText(lockPath, string.Empty); // lock file only AFTER successful write

        var newConfig = EntitiesConfigLoader.Load(entitiesPath, logger);
        liveCfg.Swap(newConfig); // fires ConfigChanged → hot-reload, no restart
        return Results.Json(new { ok = true, kind = "success" /* or matching SaveResponse shape */ });
    }
    catch (Exception ex) { /* generic error path, log + Results.Json(new { ok=false, kind="error", reason=... }) */ }
});
```
For `POST /api/groups/save`: same shape — deserialize `GroupSaveRequest`, run server-side validation mirroring `EntitiesConfigLoader.ValidateGroups` (floor=3, unit-consistency), serialize the top-level `groups:` YAML key via the same `SerializerBuilder`, call the same `ConfigWriter.WriteAsync` + `ILiveEntitiesConfig.Swap` — this is explicitly locked in CONTEXT.md as "same hot-reload path, no restart."

---

### `orchestrator/Argus.Orchestrator/Web/DetectorCatalog.cs` — `GET /api/detectors/catalog` (controller + config, request-response)

**Analog:** `Program.cs` lines 272-282 (`GET /api/detectors/defaults`) + `Web/DetectorDefaults.cs` (full file, static lookup table)

**Endpoint pattern** (Program.cs lines 272-282):
```csharp
app.MapGet("/api/detectors/defaults", (HttpRequest req) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);
    var name = (req.Query["name"].FirstOrDefault() ?? "").ToLowerInvariant();
    var defaults = DetectorDefaults.Get(name);
    if (defaults is null) return Results.StatusCode(400);
    return Results.Json(new { name, @params = defaults });
});
```
**Static table pattern** (`DetectorDefaults.cs`, full file — 49 lines, copy the class shape, not the values):
```csharp
public static class DetectorDefaults
{
    public static Dictionary<string, string>? Get(string? name)
    {
        return (name ?? "").ToLowerInvariant() switch
        {
            "hst" => new Dictionary<string, string> { ["window"] = "250", /* ... */ },
            "mad" => new Dictionary<string, string> { ["threshold"] = "3.5", ["window"] = "20" },
            "stl" => new Dictionary<string, string> { ["period"] = "24", /* ... */ },
            _ => null,
        };
    }
}
```
**Apply:** `DetectorCatalog.cs` is a NEW, PARALLEL static table (not a replacement — RESEARCH "State of the Art" table is explicit: `DetectorDefaults.cs` has zero entries for peer_divergence/ecod/copod/pca/iforest) returning `DetectorCatalogEntry` records (name, bestFor, 3 presets, param schema) — see RESEARCH.md "Pattern 3: Detector Catalog Design" for the exact record shapes (`ParamFieldSchema`, `DetectorPreset`, `DetectorCatalogEntry`) and the recommended preset numeric table (flagged `[ASSUMED]`, confirm with user before locking). Must NOT call gRPC/Python (Anti-Pattern in RESEARCH.md) — purely static/computed C#, same principle as `DetectorDefaults.cs`.

---

### `orchestrator/Argus.Orchestrator/Batch/GroupStatusCache.cs` (model/cache, event-driven)

**Analog 1:** `Health/ArgusHealthSignals.cs` (full file, 22 lines) — volatile-field pattern:
```csharp
public sealed class ArgusHealthSignals
{
    public volatile bool HaConnected;
    public volatile bool DetectorConnected;
}
```
**Analog 2:** `Ha/HaSensorRegistry.cs` (full file, 53 lines) — volatile immutable-reference swap, single-writer/many-reader:
```csharp
public sealed class HaSensorRegistry : IHaSensorRegistry
{
    private volatile IReadOnlyList<HaSensorEntry> _snapshot = Array.Empty<HaSensorEntry>();
    public IReadOnlyList<HaSensorEntry> GetAll() => _snapshot;
    public void UpdateSnapshot(IReadOnlyList<HaStateDto> states, HashSet<string> trackedEntityIds)
    {
        var entries = states.Where(/*...*/).Select(s => new HaSensorEntry(/*...*/)).ToList();
        _snapshot = entries; // atomic reference swap, no lock needed
    }
}
```
**Apply:** since group cache is keyed by an open set of `group_id`s (unlike the 2 fixed fields in `ArgusHealthSignals`), use `ConcurrentDictionary<string, GroupStatusEntry>` per RESEARCH.md Pattern 1's exact code (already fully drafted there) — single writer (`BatchSchedulerWorker`), many readers (Kestrel), no additional locking. Register as scoped/singleton DI the same way `IHaSensorRegistry`/`ArgusHealthSignals` are registered (check `Program.cs`'s `builder.Services.AddSingleton<...>()` calls for the exact registration line to mirror).

---

### `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` (service, event-driven — extend in place)

**Exact injection point** (lines 233-254, joint-mode `else` branch — copy structure, add cache write):
```csharp
else
{
    var v = response.GroupVerdict;
    await _statePublisher.PublishGroupScoreAsync(group.GroupId, null, v.Score ?? 0.0, ct);
    await _statePublisher.PublishGroupFlagAsync(group.GroupId, null, v.IsAnomaly, ct);

    // Contributions are carried through the RPC response for future HA surfacing (GRP-09,
    // scheduled for Phase 8) — logged here at info level only, no MQTT publish this phase.
    if (response.Contributions.Count > 0)
    {
        var top = response.Contributions[0];
        _logger.LogInformation(LogEvents.GroupScored, "... topContributor={Member}", /*...*/ top.MemberId);
    }
    else { /* log without topContributor */ }
}
```
**Apply:** inject `IGroupStatusCache` via constructor (same DI pattern as `_statePublisher`/`_detectorClient`/`_logger` already injected). Immediately after `if (response.Contributions.Count > 0) { var top = ... }`, add the sort-by-descending step flagged in RESEARCH.md Pitfall 4 (currently `response.Contributions[0]` is NOT guaranteed to be the true top contributor — it is member-index order, not value-sorted) — `var sorted = response.Contributions.OrderByDescending(c => c.Contribution).ToList();` then `_groupStatusCache.Set(new GroupStatusEntry(group.GroupId, v.Score, v.IsAnomaly, group.Mode /*detector name*/, DateTimeOffset.UtcNow, sorted.Select(c => new FeatureContributionDto(c.MemberId, c.Contribution)).ToList()));` — call this in BOTH branches conceptually, but per RESEARCH.md GRP-09 only applies to the joint (`else`) branch; the `isPeer` branch has no single "last verdict" to cache (per-member list, out of GRP-09 scope).

---

### `orchestrator/Argus.Orchestrator/Ha/HaWebSocketClient.cs` / `HaSensorRegistry.cs` (service + model, request-response / event-driven)

**Analog:** existing `GetStatesAsync()` call site in `HaWebSocketClient.cs` (not read this session — locate via Grep for `GetStatesAsync` before writing; same request/id/type WS message shape as documented in RESEARCH.md Pattern 2's JSON examples) and `HaSensorRegistry.UpdateSnapshot` (full file read, above) — the exact record-with-positional-params breaking-change pattern:
```csharp
public record HaSensorEntry(
    string EntityId, double CurrentValue, string? UnitOfMeasurement,
    string? FriendlyName, bool IsTracked);
```
**Apply:** add `GetAreaRegistryAsync()`/`GetEntityRegistryAsync()` mirroring `GetStatesAsync()`'s existing request/response WS plumbing exactly (same `SendAsync` id/type envelope). Extend `HaSensorEntry` with `AreaName` (nullable) and `Domain` (non-null, derived client-side via `EntityId.Split('.')[0]` — do NOT fetch domain from the registry, RESEARCH.md is explicit this is redundant). Every positional-constructor call site breaks at compile time (a safety net, not a runtime risk) — update `HaSensorRegistry.UpdateSnapshot` and any test fixtures. Fetch registries once per HA connect, right after `GetStatesAsync()`, before `UpdateSnapshot` is called (same lifecycle point, re-fetch on reconnect too).

**Known gap to scope explicitly (RESEARCH.md Pitfall 3):** entity-registry-only `area_id` is null for most real-world entities (device-inherited areas). CONTEXT.md's Open-Question resolution locks v1 scope to entity-only + domain fallback — do NOT add `device_registry` resolution this phase; document as a fast-follow.

---

### SPA: `router.ts` (route, request-response — extend in place)

**Analog:** same file, full content (23 lines) — hand-rolled hash matching, explicitly no router library:
```typescript
export const route = signal(normalizeHash(location.hash));
function normalizeHash(hash: string): string {
  const path = hash.replace(/^#/, '');
  return path || '/sensors';
}
window.addEventListener('hashchange', () => { route.value = normalizeHash(location.hash); });
```
**Apply:** extend `normalizeHash`/route-matching to recognize `/groups`, `/groups/new`, `/groups/:id` (parse the `:id` segment manually — no route-param library, consistent with "hand-rolled" comment at line 3). Do not add `preact-router`/`preact-iso`.

---

### SPA: `api/client.ts` (utility, request-response — reuse verbatim, zero modification)

**Full file already generic** — `apiGet<T>(path)`/`apiPost<T>(path, body)` both enforce no-leading-slash and JSON-body error handling. New group calls (`apiGet<GroupListResponse>('api/groups')`, `apiPost<GroupSaveResponse>('api/groups/save', body)`, `apiGet<DetectorCatalog>('api/detectors/catalog')`, `apiGet<GroupStatus>(\`api/groups/${id}/status\`)`) use this file exactly as-is — no changes needed to `client.ts` itself.

---

### SPA: `state/groups.ts` (store, CRUD)

**Analog:** `state/sensors.ts` (full file, 179 lines) — signal + pure-function-mutator style:
```typescript
export const sensors = signal<SensorEntry[]>([]);
export const loading = signal(false);
export const saveState = signal<SaveState>('idle');

let loadSensorsSeq = 0;
export async function loadSensors(q: string): Promise<void> {
  const seq = ++loadSensorsSeq;
  loading.value = true;
  try {
    const res = await apiGet<{ entries: SensorEntry[] }>(`api/sensors?q=${encodeURIComponent(q)}`);
    if (seq !== loadSensorsSeq) return; // stale-response guard
    sensors.value = res.entries;
  } finally { if (seq === loadSensorsSeq) loading.value = false; }
}

export async function save(): Promise<void> {
  saveState.value = 'saving';
  const body: SaveRequest = { /* ... */ };
  try {
    const result = await apiPost<SaveResponse>('api/sensors/save', body);
    saveState.value = { result };
  } catch (err) {
    saveState.value = { result: { ok: false, kind: 'error', reason: err instanceof Error ? err.message : 'unexpected error' } };
  }
}
```
**Apply:** `state/groups.ts` mirrors this exactly — `groups = signal<GroupConfig[]>([])`, `loadGroups()` (same monotonic-sequence stale-response guard pattern), `saveGroup()` (same try/catch → `saveState` discriminated-union pattern). The `validationErrors`/`hasValidationErrors` `computed()` pattern (lines 144-159) mirrors directly for group validation (floor/unit-mismatch) using `validation/groupParams.ts`.

---

### SPA: `state/groupEditor.ts` (store, event-driven — guided-flow state machine)

**Analog:** RESEARCH.md's own fully-drafted example (already following `state/sensors.ts`'s signal + pure-mutator convention) — copy directly:
```typescript
import { signal } from '@preact/signals';

export type ChooserMode = 'guided-question' | 'guided-pick-shown' | 'manual';
export const chooserMode = signal<ChooserMode>('guided-question');
export const selectedDetector = signal<string | null>(null);
export const guidedRecommended = signal<string | null>(null);

export function answerGuidedQuestion(answer: 'together' | 'diverges'): void {
  const detector = answer === 'together' ? 'ecod' : 'peer_divergence';
  guidedRecommended.value = detector;
  selectedDetector.value = detector;
  chooserMode.value = 'guided-pick-shown';
}
export function skipToManual(): void {
  guidedRecommended.value = null;
  chooserMode.value = 'manual';
}
export function pickAlgorithmManually(detector: string): void {
  guidedRecommended.value = null; // overriding clears the "guided" label per UI-SPEC
  selectedDetector.value = detector;
  chooserMode.value = 'manual';
}
```
Zero-friction override (`pickAlgorithmManually` is one synchronous update, no confirmation) matches `state/sensors.ts`'s `removeDetector` mutator style (lines 106-115) exactly.

---

### SPA: `validation/groupParams.ts` (utility, transform)

**Analog:** `validation/detectorParams.ts` (first 40 lines read) — field-level validator + message-constant convention:
```typescript
const MSG_REQUIRED = 'Must provide a value.';
const INT_MIN: Record<string, number> = { window: 1, /* ... */ };
function isBlankOrNonNumeric(raw: string): boolean {
  return raw.trim() === '' || Number.isNaN(parseFloat(raw));
}
export function validateField(key: string, raw: string): string | null { /* ... */ }
```
**Apply:** `validateGroupMembers(members: string[]): string | null` returns the exact CONTEXT.md/UI-SPEC copy `"A group needs at least 3 members."` when `members.length < 3` (mirrors `EntitiesConfigLoader.ValidateGroups` floor=3 — same "client is UX-only, server is authority" split noted in RESEARCH.md "Don't Hand-Roll"). `validateUnitConsistency(members: SensorEntry[]): string | null` returns `"Peer-divergence groups need members with the same unit. Found: {units}."` when `mode === 'peer_divergence'` and units differ — messages must match UI-SPEC's Copywriting Contract table verbatim (do not reword, per the established convention in `detectorParams.ts`'s own header comment: "Messages are the parity spec — do not reword").

---

### SPA: `components/GroupList.tsx` (component, CRUD)

**Analog:** `components/SensorList.tsx` (full file, 64 lines):
```typescript
export function SensorList({ entries, query, edits, /* handlers */ }: SensorListProps) {
  if (entries.length === 0) {
    return <EmptyState query={query} />;
  }
  return (
    <ul class="argus-list">
      {entries.map((entry) => <SensorListRow key={entry.entityId} entry={entry} /* ... */ />)}
    </ul>
  );
}
```
**Apply:** `GroupList` renders `<ul class="argus-list">` of `GroupListRow`, empty-list branch renders `EmptyState`-style copy ("No groups configured." per UI-SPEC) instead of `SensorList`'s query-based `EmptyState`.

---

### SPA: `components/GroupListRow.tsx` (component, CRUD)

**Analog:** `components/SensorListRow.tsx` — not read this session (file exists, same directory); follow the row-rendering conventions visible in `SensorList.tsx`'s usage of it (props: `entry`, `entityIdx`, `isTracked`, handler callbacks) — `GroupListRow` needs `group` (name, mode badge, member count, status pill sourced from `GroupStatus`, edit link to `#/groups/:id`).

---

### SPA: `components/AlgorithmChooser.tsx`, `AlgorithmCard.tsx`, `SensitivityPresetPicker.tsx` (components, event-driven — role-match, no exact analog)

**Closest analog per UI-SPEC:** `components/DetectorDisclosure.tsx` (full file, 48 lines) for `AlgorithmChooser`'s list-of-selectable-entries orchestration; `components/DetectorEntry.tsx` (full file, 57 lines) for `AlgorithmCard`/`SensitivityPresetPicker`'s "one editable unit with a `<select>` + param sync" shape:
```typescript
// DetectorEntry.tsx pattern to replicate for AlgorithmCard/SensitivityPresetPicker
export function DetectorEntry({ entityIdx, detIdx, detector, onTypeChange, onParamChange, onRemove }: DetectorEntryProps) {
  return (
    <div class="argus-detector-entry">
      <div class="argus-detector-header">
        <select class="argus-detector-select" value={detector.name} onChange={(e) => onTypeChange(...)}>
          <option value="hst">HST</option>
          {/* ... */}
        </select>
      </div>
      <DetectorParamGrid entityIdx={entityIdx} detIdx={detIdx} detector={detector} onParamChange={onParamChange} />
    </div>
  );
}
```
**Apply:** `AlgorithmCard` is a `<button>`/`<div role="radio">`-based selectable unit (not a `<select>`, per UI-SPEC's card-grid contract) showing name + "best for…" + selected-state border (`--color-accent`) — structurally the same "one item, callback-driven selection, no local state" shape as `DetectorEntry`'s select handler. `SensitivityPresetPicker` is literally a native `<input type="radio">` group (UI-SPEC line 172: "native `<input type="radio">`, `accent-color: var(--color-accent)`, same pattern as the existing tracked-checkbox") — find the tracked-checkbox in `SensorListRow.tsx` (not read this session, but explicitly cited by UI-SPEC as the pattern to copy) before implementing.

---

### SPA: `components/AdvancedParamsDisclosure.tsx` (component, event-driven — exact analog)

**Analog:** `components/DetectorDisclosure.tsx` (full file, above) + `components/DetectorParamGrid.tsx` (not read this session, but explicitly named by UI-SPEC line 161-163: "reuses the exact `.argus-param-grid`/`.argus-param-field`/`.argus-param-field__input` CSS classes... from Phase 7's `DetectorParamGrid` — it is a param grid, just for group-detector params instead of per-entity ones"). Native `<details>`/`<summary>` — no new toggle mechanism:
```typescript
<details class="argus-detectors-details">
  <summary class="argus-disclosure-toggle">{summaryText}</summary>
  <div class="argus-detectors-panel">{/* param fields */}</div>
</details>
```

---

### SPA: `components/SensorSearchInput.tsx` (component, transform — extend in place)

**Current file (full, 39 lines) — extend the placeholder and match predicate (predicate itself lives in the consuming filter function, not shown here):**
```typescript
<input class="argus-search__input" type="search" defaultValue={value}
  placeholder="Filter by entity ID…" aria-label="Filter entities" onInput={handleInput} />
```
**Apply:** change placeholder to `"Filter by name or entity ID…"` (UI-SPEC Copywriting Contract — applies everywhere, including unchanged `#/sensors`). The debounce (200ms) + `useEffect` cleanup-on-unmount (lines 16-18) pattern is the cited analog for `AttributionPanel`'s poll-interval cleanup — reuse the same `useRef` + `clearTimeout`-on-unmount discipline, substituting `setInterval`/`clearInterval`.

---

### SPA: `components/SensorList.tsx` (component, transform — extend in place)

**Current file (full, 64 lines, above)** — add an area-grouping render mode: wrap groups of `SensorListRow` in `<details class="argus-disclosure-toggle-section">` per HA area (alphabetical, "Ungrouped"/domain fallback last), reusing the exact `.argus-disclosure-toggle` class already used by `DetectorDisclosure.tsx`'s `<summary>` — no new disclosure CSS.

---

## Shared Patterns

### Ingress Authorization (applies to ALL 4 new .NET endpoints)
**Source:** `Program.cs` lines 223-237 (`IsAuthorizedRequest`)
```csharp
bool IsAuthorizedRequest(HttpContext ctx)
{
    if (devTrustAllRequests) return true;
    var remote = ctx.Connection.RemoteIpAddress;
    if (remote is null) return false;
    if (System.Net.IPAddress.IsLoopback(remote)) return true;
    if (remote.Equals(System.Net.IPAddress.Parse("172.30.32.2"))) return true;
    return false;
}
```
**Apply to:** `GET /api/groups`, `POST /api/groups/save`, `GET /api/detectors/catalog`, `GET /api/groups/{id}/status` — call `if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);` as the FIRST line of every handler, no exceptions.

### Config Write + Hot-Reload Pipeline
**Source:** `Program.cs` lines 386-429 (`ConfigWriter.WriteAsync` → lock file → `EntitiesConfigLoader.Load` → `ILiveEntitiesConfig.Swap`)
**Apply to:** `POST /api/groups/save` — byte-for-byte the same pipeline, writing the top-level `groups:` YAML key instead of `entities:`/`_patterns:`. Never reimplement the atomic temp-then-rename write or the validate-before-swap ordering.

### Relative-Fetch API Client
**Source:** `orchestrator/ui/src/api/client.ts` (full file — `apiGet`/`apiPost`, no-leading-slash enforcement, `ok`/`kind`-discriminant JSON body parsing)
**Apply to:** every new SPA API call — no direct `fetch()` calls from any new component/state module.

### Signals State + Pure Mutators
**Source:** `orchestrator/ui/src/state/sensors.ts` (full file — signal declarations + `function mutate(...): void { edits = {...}; edits[key] = ...; signal.value = edits; }` style, plus the monotonic-sequence-number stale-response guard in `loadSensors`)
**Apply to:** `state/groups.ts`, `state/groupEditor.ts` — no new state library, no class-based stores.

### Volatile Cache / Single-Writer-Many-Reader
**Source:** `Health/ArgusHealthSignals.cs` (volatile fields) + `Ha/HaSensorRegistry.cs` (volatile immutable-reference swap)
**Apply to:** `Batch/GroupStatusCache.cs` — `ConcurrentDictionary`, one writer (`BatchSchedulerWorker`), many readers (Kestrel `/api/groups/{id}/status`), no explicit lock.

### Error Response Shape (`{ ok, kind, reason/errorCount }`)
**Source:** `Program.cs` `/api/sensors/save` (lines 303-304, 309-310, 360, and the final success/exception branches)
**Apply to:** `POST /api/groups/save` responses — reuse the `{ ok: false, kind: "error"|"validation", reason/errorCount }` discriminant shape exactly, consumed by the reused `SaveResultBanner` component client-side.

## No Analog Found

| File | Role | Data Flow | Reason |
|---|---|---|---|
| `orchestrator/ui/src/components/GuidedFlowStep.tsx` | component | event-driven | First client-only "question → pre-selected pick → overridable" state-machine UI in the codebase; closest analog is `DetectorEntry.tsx`'s select+conditional-render shape (role-match), but no prior guided/wizard flow exists. Use RESEARCH.md's fully-drafted `state/groupEditor.ts` state machine as the authoritative behavior spec instead of a code analog. |
| `orchestrator/ui/src/components/AttributionPanel.tsx` / `AttributionBar.tsx` | component | streaming (poll) / transform (render) | First polling component and first CSS-bar-chart rendering in the codebase. Closest partial analogs: `SensorSearchInput.tsx`'s debounce-timer cleanup-on-unmount discipline (for the poll interval) and `SensorListRow.tsx`'s row-rendering conventions (for the bar row). UI-SPEC's Attribution Display Contract (states, sort behavior, color rules) is the authoritative spec here, not a codebase precedent. |

## Metadata

**Analog search scope:** `detector/argus_detector/` (pyod_detector.py, registry.py, servicer.py, group/*.py), `detector/tests/`, `orchestrator/Argus.Orchestrator/` (Program.cs, Web/, Health/, Ha/, Batch/, Config/), `orchestrator/ui/src/` (router.ts, api/, state/, validation/, components/)
**Files scanned:** ~30 read/grepped directly this session; codebase-wide Glob/Grep used to confirm absence of prior art for the 2 "No Analog Found" components
**Pattern extraction date:** 2026-07-02
