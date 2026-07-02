# Phase 8: Group Config UI + Algorithm Chooser - Research

**Researched:** 2026-07-02
**Domain:** Preact SPA feature UI (group authoring, guided algorithm chooser, sensor search/browse, attribution display) + supporting .NET Minimal API endpoints, wired to an existing Phase 5/6 group-detection backend and a Phase 7 SPA shell.
**Confidence:** MEDIUM — the SPA/backend wiring patterns are HIGH confidence (direct codebase read of Phase 7 precedent). The Python detector "sensitivity" plumbing is a **known gap** (see Critical Finding below) and the HA registry WS field shapes are MEDIUM/LOW confidence (not fully documented publicly).

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

**Group Authoring UI + Persistence**
- New SPA routes: `#/groups` (list) + `#/groups/new` and `#/groups/:id` (editor). `#/sensors` (Phase 7) stays.
- New backend endpoints: `GET /api/groups` (list groups from live config) + `POST /api/groups/save` (writes the top-level `groups:` list to entities.yaml via the existing ConfigWriter + LiveEntitiesConfig.Swap pipeline — same hot-reload path, no restart). Ingress auth (IsAuthorizedRequest) first, as with all /api/*.
- Members picker reuses the Phase 7 SensorList / SensorSearchInput components in multi-select mode.
- Client-side validation mirrors the Phase 6 backend group guards: min-member floor (3), unit consistency for peer-divergence groups (using HA-sourced unit metadata) — validated before save; backend remains the authority (degrade-not-crash on load).

**Algorithm Chooser (ALGO-01..04)**
- Backend `GET /api/detectors/catalog` is the single source of truth: per group detector it returns the Low/Med/High presets (→ concrete param values), the "best for…" description (ALGO-03), and the param schema (types/ranges for the Advanced form).
- Preset expansion: the chosen preset is stored as a label AND expanded to concrete params written into the group config (self-contained YAML). The Advanced toggle (ALGO-02) reveals the raw params and lets the operator override individual values behind the preset (ALGO-01).
- Guided flow (ALGO-04): a "what are you monitoring?" step (e.g. "a room/area's related sensors" → joint ECOD; "which one diverges from its peers" → peer-divergence) pre-selects a detector, VISIBLY shows/explains the pick, and always allows one-click override. Never an opaque auto-pick (Out-of-Scope: fully-automatic selection).
- Scope: presets/chooser apply ONLY to the new group detectors (peer_divergence, ECOD, COPOD, PCA, IForest). Univariate MAD/STL/HST are unchanged (ALGO-F1 uniform sensitivity is deferred to a future milestone).

**Sensor Search & Browse (SRCH-01..03)**
- Discovery enrichment: fetch HA `area_registry` + `entity_registry` (via the existing HaWebSocketClient WS path) so each discovered sensor carries `friendly_name`, `area_name`, `domain`, and `unit_of_measurement`; cached at config-load / discovery time.
- Search (SRCH-01): extend SensorSearchInput to match friendly_name AND entity_id (today entity_id only).
- Browse (SRCH-02): sensor list is grouped/collapsible by HA area (fallback to domain when no area).
- Suggestions (SRCH-03): "N sensors share area X — group them?" surfaced as an operator-approved proposal that pre-fills the group editor; NEVER auto-groups (Out-of-Scope: automatic dynamic group discovery).

**Attribution Display (GRP-09)**
- Data source: the orchestrator retains each group's last verdict + `FeatureContribution` list in memory (analogous to the existing health signals cache), exposed via `GET /api/groups/{id}/status`.
- Presentation: a ranked per-feature/per-member contribution (sorted list / bar) instead of a flat boolean. Attribution is only available for detectors that produce it (ECOD/COPOD — from Phase 5); PCA/IForest return null contributions → show a "no per-feature attribution for this detector" message, not a fake ranking.
- Refresh: the SPA polls `/api/groups/{id}/status` while a group's status view is open (roughly the batch interval cadence). No SSE.
- Scope: read-only display in the Argus UI. No custom HA dashboards (PROJECT.md exclusion); the HA entities themselves remain as shipped in Phase 6.

### Claude's Discretion
- None called out explicitly as "discretion" in CONTEXT.md — treat any HOW-level implementation detail not pinned above (exact param schema JSON shape, exact preset numeric values, exact polling interval, exact area-registry cache lifetime) as this phase's discretion, informed by the Findings below.

### Deferred Ideas (OUT OF SCOPE)
- Uniform Low/Med/High sensitivity across univariate MAD/STL/HST (ALGO-F1) — future milestone, after the per-detector-family mapping is proven on the group detectors.
- Streaming group detection + live streaming attribution (STRM-01/02) — out of scope this milestone.
- Cross-group "meta-anomaly" dashboard / any custom HA dashboards — explicit Out-of-Scope (PROJECT.md).
- Fully-automatic algorithm selection or a continuous sensitivity slider — explicit Out-of-Scope.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| GRP-09 | Joint-multivariate detection attributes which member/feature drove the anomaly, surfaced as a ranked contribution | See "Attribution Pipeline" (Architecture Patterns) + "In-Memory Last-Verdict Cache" — data already flows via `FeatureContribution` in `GroupScoreResponse`; only a cache + endpoint + SPA panel are new |
| ALGO-01 | Low/Med/High preset selects sensitivity without exposing raw params by default | See **Critical Finding: Sensitivity Params Are Not Wired in Python** — this is the hard part of the phase, not the UI |
| ALGO-02 | Advanced toggle reveals/overrides raw params behind a preset | See "Detector Catalog Design" — catalog's param schema drives `AdvancedParamsDisclosure`, reusing `DetectorParamGrid` pattern |
| ALGO-03 | Each algorithm shows a "best for…" description | See "Detector Catalog Design" — static catalog data, .NET-side, no Python round-trip |
| ALGO-04 | Guided "what are you monitoring?" chooser pre-selects + explains, always overridable | See "Guided Flow State Machine" (Architecture Patterns) — pure client-side signals state, catalog supplies the algorithm metadata |
| SRCH-01 | Search sensor list by friendly_name (not only entity_id) | See "HA Registry Enrichment" — friendly_name already flows from `get_states`; only the search predicate changes (already enriched in Phase 2/7) |
| SRCH-02 | Sensor list browsable by HA area and/or domain | See "HA Registry Enrichment" — requires NEW `area_registry`/`entity_registry` WS calls; `HaSensorEntry` needs new fields |
| SRCH-03 | Group-config UI suggests area-scoped candidate groups, operator-approved only | See "HA Registry Enrichment" + `AreaSuggestionBanner` computed client-side from the enriched sensor list |
</phase_requirements>

## Summary

Phase 8 is primarily a **wiring phase**: the SPA shell (Phase 7), the group config model (Phase 6 `EntitiesConfig.Groups`), and the group-scoring RPC contract (Phase 5 `GroupScoreResponse.contributions`) all already exist. The new work is (1) four new Minimal API endpoints following the exact `IsAuthorizedRequest` → typed-DTO → JSON pattern established in `Program.cs`, (2) ~10 new Preact components reusing `argus.css` and the Phase 7 component library almost verbatim, and (3) two new HA WebSocket calls (`config/area_registry/list`, `config/entity_registry/list`) to enrich `HaSensorEntry` with `area_name`/`domain`.

**The one genuine unknown, and the thing the planner must budget real tasks for, is this: the Python group detectors currently accept zero tunable sensitivity parameters.** `PeerDivergenceDetector` hardcodes `_THRESHOLD = 3.5` as a module constant with no `from_params()` method at all. `GroupMultivariateDetector.__init__(detector_name)` takes only the algorithm name — no `contamination`, no `n_estimators` — and none of `request.params` (already on the wire via the proto) is read anywhere in `servicer.ScoreGroupBatch`/`FitGroup` for group detectors. Meanwhile ECOD and COPOD are *architecturally parameter-free* in PyOD (their only knob, `contamination`, only shifts the binary `threshold_`/`is_anomaly` decision — it does **not** change the continuous `decision_function()` score that is what the group verdict's `score` field and MQTT `sensor` entity actually expose). This means a Low/Med/High preset for ECOD/COPOD **cannot make the reported anomaly *score* more or less sensitive** — only `contamination` (threshold) and, for peer_divergence, the z-score threshold, are real levers. The planner needs a Python-side plan/task to (a) add `from_params()`-style construction to `GroupMultivariateDetector` (contamination for pca/iforest/ecod/copod, n_estimators for iforest) and (b) make peer_divergence's z-score threshold a param instead of a hardcoded constant — otherwise ALGO-01 ships a UI that changes nothing detector-side for 2 of 5 algorithm choices.

**Primary recommendation:** Treat the sensitivity-preset backend as two layers with two different owners: the .NET `GET /api/detectors/catalog` (label → concrete-params mapping, purely descriptive, .NET-only) and a small Python change to actually consume `params["contamination"]` / `params["threshold"]` in the group detectors before Phase 8's UI can honestly claim "Low/Med/High changes behavior." Scope the Python change as its own task/plan (not a UI detail) and be explicit in the catalog's "best for…" copy about what sensitivity does and does not affect per detector.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Group list/editor UI, guided flow, attribution display | Browser / Client (Preact SPA) | — | Pure presentation + client validation; no server-rendering in this stack (Phase 7 locked SPA architecture) |
| `GET /api/groups`, `POST /api/groups/save` | API / Backend (.NET Minimal API) | Database/Storage (entities.yaml) | Reads/writes `EntitiesConfig.Groups` via existing `ConfigWriter` + `LiveEntitiesConfig` — same tier as Phase 7's `/api/sensors/save` |
| `GET /api/detectors/catalog` | API / Backend (.NET, static/computed) | — | Purely descriptive metadata; no Python round-trip needed — the catalog is authored in C#, not fetched live from the detector |
| `GET /api/groups/{id}/status` | API / Backend (.NET, in-memory cache) | — | Reads a new in-memory cache populated by `BatchSchedulerWorker`'s existing group-scoring loop; no new gRPC call from this endpoint |
| Sensitivity preset → actual detector behavior | API/Backend (.NET catalog: descriptive) **+ Detector (Python): enforcement** | — | The catalog only *describes* param values; the Python detector must actually *read* `params["contamination"]`/`params["threshold"]` for the preset to have any real effect — currently it does not (Critical Finding) |
| HA area/entity registry enrichment | API / Backend (.NET `HaSensorRegistry` + `HaWebSocketClient`) | — | New WS calls made from the existing connect/reconnect loop in `NetDaemonHaEventSource`, same tier and lifecycle as the existing `get_states` call |
| Client-side group validation (floor, unit match) | Browser / Client | — | Mirrors `EntitiesConfigLoader.ValidateGroups` for fast feedback; backend remains authoritative (same "client validation is UX-only" pattern as Phase 4 `InputValidator`/`validation/detectorParams.ts`) |

## Standard Stack

No new external packages are introduced by this phase. All work reuses the existing pinned stack.

### Core (already pinned — CLAUDE.md / project stack)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Preact + @preact/signals | (Phase 7 pinned) | SPA UI + reactive state | Already the locked stack; no new screens require a new dependency |
| Vite + TypeScript | (Phase 7 pinned) | Build | Unchanged |
| Vitest + @testing-library/preact | (Phase 7 pinned) | Component tests | Unchanged |
| .NET 8 Minimal API | framework-provided | New `/api/groups*`, `/api/detectors/catalog` endpoints | Same pattern as Phase 7's `/api/sensors*` |
| YamlDotNet | (already in project) | Serializes `groups:` on save | Same serializer instance/pattern as `/api/sensors/save` |
| PyOD 3.6.0 | pinned | ECOD/COPOD/PCA/IForest group detectors | Already in place from Phase 5 — Phase 8 only changes how params reach the constructor, not the library |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Client-side polling (`setInterval` + `GET /api/groups/{id}/status`) | Server-Sent Events / WebSocket push | CONTEXT.md explicitly locks polling ("No SSE") — do not introduce a push channel |
| .NET-side static catalog | A Python `/catalog` gRPC call returning presets | Adds a network round-trip and couples UI copy to detector process availability for no benefit — catalog content (labels, "best for" text, preset numbers) is UI/config metadata, not runtime detector state |

**Installation:** none — no new packages for this phase.

**Version verification:** Not applicable — no new package versions to verify. PyOD 3.6.0 is already installed and pinned; verified previously per `CLAUDE.md` Sources section (2026-06-04 release).

## Package Legitimacy Audit

Not applicable — this phase adds zero new external packages (npm or PyPI). No legitimacy check required.

## Architecture Patterns

### System Architecture Diagram

```
┌─────────────────────────── Browser (Preact SPA, hash-routed) ───────────────────────────┐
│                                                                                            │
│  #/groups ──┬─→ GroupList ──(GET api/groups)───────────────────┐                         │
│             └─→ AreaSuggestionBanner ──(computed from enriched sensor list, client-side)  │
│                                                                  │                         │
│  #/groups/new, #/groups/:id ─→ GroupEditorForm                  │                         │
│      ├─ MemberPicker (SensorList multi-select, enriched w/ area/domain)                   │
│      ├─ AlgorithmChooser                                        │                         │
│      │    ├─ GuidedFlowStep ──(client-only state machine)──┐    │                         │
│      │    └─ AlgorithmCard grid ──(GET api/detectors/catalog)   │                         │
│      │         └─ SensitivityPresetPicker + AdvancedParamsDisclosure                      │
│      ├─ AttributionPanel ──poll──(GET api/groups/{id}/status)───┼──┐                      │
│      └─ SaveBar ──(POST api/groups/save)────────────────────────┘  │                      │
└───────────────────────────────────────┬──────────────────────────────┼────────────────────┘
                                          │ relative fetch, Ingress auth │
┌─────────────────────────────────────── ▼ ──────────────────────────── ▼ ──────────────────┐
│                          .NET 8 Kestrel — Program.cs Minimal API                            │
│                                                                                              │
│  GET  /api/groups            → EntitiesConfig.Groups (LiveEntitiesConfig.Get())            │
│  POST /api/groups/save       → validate → ConfigWriter.WriteAsync → LiveEntitiesConfig.Swap│
│  GET  /api/detectors/catalog → static/computed preset table (new DetectorCatalog.cs)       │
│  GET  /api/groups/{id}/status→ read GroupStatusCache[id] (new in-memory cache)             │
│                                                                                              │
│  BatchSchedulerWorker.RunGroupBatchAsync() ──writes──→ GroupStatusCache[group_id]           │
│       (existing loop; already calls ScoreGroupBatchAsync + has response.Contributions)      │
│                                                                                              │
│  NetDaemonHaEventSource (on every HA connect) ──calls──→ HaWebSocketClient                  │
│       .GetStatesAsync()        (existing)                                                   │
│       .GetAreaRegistryAsync()  (NEW)                                                        │
│       .GetEntityRegistryAsync()(NEW)                                                        │
│       ──joins──→ HaSensorRegistry.UpdateSnapshot(states, areas, entityRegistry, tracked)    │
└───────────────────────────────┬──────────────────────────────────────┬────────────────────┘
                                 │ gRPC (existing)                      │ WS (Supervisor proxy)
                                 ▼                                      ▼
                    Python detector (ScoreGroupBatch)          Home Assistant Core
                    ── contributions already returned          (area_registry, entity_registry,
                       for ecod/copod (Phase 5) ──               get_states)
                    ── params["contamination"]/["threshold"]
                       NOT YET READ (Critical Finding —
                       new Python task needed for ALGO-01
                       to have real effect)
```

### Recommended Project Structure
```
orchestrator/ui/src/
├── components/
│   ├── GroupList.tsx / GroupListRow.tsx / AreaSuggestionBanner.tsx      # new
│   ├── GroupEditorForm.tsx / MemberPicker.tsx                            # new
│   ├── AlgorithmChooser.tsx / GuidedFlowStep.tsx / AlgorithmCard.tsx     # new
│   ├── SensitivityPresetPicker.tsx / AdvancedParamsDisclosure.tsx        # new
│   ├── AttributionPanel.tsx / AttributionBar.tsx                         # new
│   ├── SensorList.tsx        # extended: area-grouping mode
│   └── SensorSearchInput.tsx # extended: friendly_name predicate
├── state/
│   └── groups.ts              # new — signals mirroring state/sensors.ts pattern
├── api/
│   └── types.ts               # extended — GroupConfig, DetectorCatalog, GroupStatus DTOs
└── validation/
    └── groupParams.ts         # new — floor(3) + unit-consistency, mirrors detectorParams.ts

orchestrator/Argus.Orchestrator/
├── Web/
│   ├── GroupsEndpoints.cs (or inline in Program.cs, matching existing style)  # new
│   ├── DetectorCatalog.cs     # new — static preset table, sibling to DetectorDefaults.cs
│   └── GroupSaveRequest.cs    # new — DTO, sibling to SaveRequest.cs
├── Batch/
│   └── GroupStatusCache.cs    # new — volatile Dictionary<string, GroupStatusEntry>
└── Ha/
    ├── HaWebSocketClient.cs   # extended — GetAreaRegistryAsync/GetEntityRegistryAsync
    └── HaSensorRegistry.cs    # extended — HaSensorEntry gains AreaName/Domain fields

detector/argus_detector/group/
├── multivariate_detector.py   # extended — from_params() reading contamination/n_estimators
└── peer_divergence.py         # extended — threshold becomes a param, not a module constant
```

### Pattern 1: In-Memory Last-Verdict Cache (GRP-09 backing store)

**What:** A thread-safe, volatile-reference cache keyed by `group_id`, populated by `BatchSchedulerWorker` at the exact point it already has `response` from `ScoreGroupBatchAsync`, read by the new `GET /api/groups/{id}/status` endpoint.

**When to use:** Any "last known value, read by HTTP, written by a background worker" scenario — this project already has two precedents: `ArgusHealthSignals` (single volatile bools) and `HaSensorRegistry` (volatile immutable-list swap). This is the same pattern, generalized to a dictionary.

**Why a `Dictionary`, not per-field volatiles:** Unlike `ArgusHealthSignals` (2 fixed fields), the group cache is keyed by an open set of `group_id`s that changes as groups are added/removed — a plain `ConcurrentDictionary<string, GroupStatusEntry>` (single writer per key from `RunGroupBatchAsync`, many readers from Kestrel) is the natural fit; no locking needed beyond what `ConcurrentDictionary` already provides.

**Example (new file, e.g. `Batch/GroupStatusCache.cs`):**
```csharp
// Source: pattern mirrors ArgusHealthSignals.cs (volatile fields) and
// HaSensorRegistry.cs (volatile immutable-reference swap) already in this codebase.
namespace Argus.Orchestrator.Batch;

public sealed record FeatureContributionDto(string MemberId, double Contribution);

public sealed record GroupStatusEntry(
    string GroupId,
    double? Score,
    bool? IsAnomaly,
    string Detector,
    DateTimeOffset ScoredAtUtc,
    IReadOnlyList<FeatureContributionDto> Contributions); // empty for pca/iforest — never fabricated

public interface IGroupStatusCache
{
    GroupStatusEntry? Get(string groupId);
    void Set(GroupStatusEntry entry);
}

public sealed class GroupStatusCache : IGroupStatusCache
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GroupStatusEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public GroupStatusEntry? Get(string groupId) =>
        _entries.TryGetValue(groupId, out var e) ? e : null;

    public void Set(GroupStatusEntry entry) => _entries[entry.GroupId] = entry;
}
```

`BatchSchedulerWorker.RunGroupBatchAsync` already has everything needed at the exact point marked in the codebase (`orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` lines 211–254): after `response = await _detectorClient.ScoreGroupBatchAsync(request, ct)`, for the `isPeer` branch there is no single "last verdict" (per-member list) — GRP-09 attribution only applies to joint mode, so cache population only needs to occur in the `else` (joint) branch, where `response.Contributions` and `response.GroupVerdict` already exist. Inject `IGroupStatusCache` into `BatchSchedulerWorker`'s constructor (mirrors how `IStatePublisher`/`ILiveEntitiesConfig` are already injected) and call `.Set(...)` right where the existing `_logger.LogInformation(LogEvents.GroupScored, ...)` line is (line ~244–253), before/after the log call.

**Anti-pattern to avoid:** Do not add a new gRPC call from the `/api/groups/{id}/status` endpoint to re-score on demand — GRP-09/CONTEXT.md explicitly scope this to "last verdict" polling of a passively-updated cache, not a live query.

### Pattern 2: HA Registry Enrichment (SRCH-01/02/03)

**What:** Extend `HaWebSocketClient` with two new request/response methods (`GetAreaRegistryAsync`, `GetEntityRegistryAsync`), called once per HA connect (same lifecycle point as the existing `GetStatesAsync()` call in `NetDaemonHaEventSource.RunConnectionLoopAsync`), and join the three datasets (`get_states`, `entity_registry`, `area_registry`) into an enriched `HaSensorEntry`.

**When to use:** Any HA registry metadata not present on `get_states` (area name, domain is derivable client-side from `entity_id.Split('.')[0]` without a WS call — do not fetch domain from the registry, it is redundant).

**Command shapes** `[ASSUMED — MEDIUM confidence, not fully documented in official HA developer docs at research time; the `list_for_display` variant IS documented with abbreviated keys, but the plain (non-abbreviated) `list` commands used here follow the same shape as HA's own frontend/registry storage model and third-party WS client libraries (e.g. node-red-contrib-home-assistant-websocket) — recommend a live-HA smoke test in Wave 0 of this phase, same as the project's existing "live research gaps" precedent in STATE.md for Phase 1/2]`:

```jsonc
// Request (mirrors existing get_states id/type shape in HaWebSocketClient.SendAsync)
{ "id": 3, "type": "config/area_registry/list" }

// Response — result is a flat array, one object per area
{
  "id": 3, "type": "result", "success": true,
  "result": [
    { "area_id": "living_room", "name": "Living Room", "picture": null, "icon": null }
    // ...
  ]
}

// Request
{ "id": 4, "type": "config/entity_registry/list" }

// Response — result is a flat array, one object per registered entity
{
  "id": 4, "type": "result", "success": true,
  "result": [
    {
      "entity_id": "sensor.living_room_temperature",
      "area_id": null,                 // null when entity inherits area from its device
      "device_id": "abcd1234",         // look up device's area_id when entity's own area_id is null
      "platform": "mqtt",
      "disabled_by": null,
      "hidden_by": null
      // ... many more fields, all optional beyond entity_id
    }
  ]
}
```

**Critical gotcha (documented pattern, HIGH confidence — this is a well-known HA API trap):** an entity's effective area is **not always** `entity_registry_entry.area_id` — when that field is `null`, the entity inherits its area from its *device* (`device_registry_entry.area_id` for the entity's `device_id`). A naive join using only `entity_registry` will silently under-report areas for any entity that was auto-assigned an area via its device (the common case for most integrations) rather than manually overridden per-entity. **Recommendation:** either (a) also fetch `config/device_registry/list` and resolve `entity.area_id ?? device[entity.device_id].area_id`, or (b) explicitly scope SRCH-02/03 v1 to "entities with an explicit area_id only, fallback to domain for the rest" and document that device-inherited areas are a known gap — given CONTEXT.md already specifies "fallback to domain when no area," option (b) is likely sufficient for this phase's scope, but the planner should decide explicitly rather than have it fall out accidentally. Flag this as an **Open Question** for CONTEXT/discuss-phase confirmation if not already implicitly decided.

**When to fetch relative to `get_states`:** Fetch area/entity registry **once per connect**, right after `GetStatesAsync()` and before `UpdateSnapshot` is called (`NetDaemonHaEventSource.cs` line ~135-138) — registries change far less often than sensor values, so there is no need to re-fetch per state_changed event. On reconnect, re-fetch both (cheap, and areas/entities can change while disconnected).

**Extending `HaSensorEntry` (breaking change to an existing record — check all callers):**
```csharp
// orchestrator/Argus.Orchestrator/Ha/IHaSensorRegistry.cs — extend, do not replace
public record HaSensorEntry(
    string EntityId,
    double CurrentValue,
    string? UnitOfMeasurement,
    string? FriendlyName,
    bool IsTracked,
    string? AreaName,   // NEW — null when unresolved (no area_id on entity, and device fallback out of scope/unavailable)
    string Domain);      // NEW — derived from EntityId.Split('.')[0], never null (SRCH-02 fallback grouping key)
```
All 3 existing callers of the `HaSensorEntry` positional constructor (`HaSensorRegistry.UpdateSnapshot`, and any test fixtures in `HaSensorRegistryTests.cs`) must be updated — this is a record with positional params, so every call site breaks at compile time until updated (a compile-time safety net, not a runtime risk).

### Pattern 3: Detector Catalog Design (ALGO-01..03)

**What:** A single static (or config-driven-but-not-live-reloaded) C# table returning, per group detector name, the Low/Med/High preset → param-value mapping, a "best for…" description, and a param schema (name/type/min/max) for the Advanced form.

**Where the preset numbers come from — NOT from Python, NOT from `DetectorDefaults.cs`:** `DetectorDefaults.cs` (Phase 7) only covers the per-entity `hst`/`mad`/`stl` detectors — it has no entries for `peer_divergence`/`ecod`/`copod`/`pca`/`iforest`. There is currently **no existing default-value source for group detectors anywhere in the codebase** — Phase 5/6 never assign default `Params` to a `GroupConfig` (its `Params` dictionary defaults to `new()` = empty, and the Python side currently ignores it entirely per the Critical Finding). This means the catalog's preset values are a **new design decision for this phase**, not a lookup of an existing table. Recommend:

| Detector | Med (default) preset params | Low preset | High preset | Real effect on score? |
|----------|------------------------------|------------|-------------|------------------------|
| `peer_divergence` | `threshold=3.5` (current hardcoded Iglewicz-Hoaglin default) | `threshold=4.5` (less sensitive — fewer flags) | `threshold=2.5` (more sensitive) | Directly changes the flag boundary — **requires** peer_divergence.py to accept `threshold` as a param (Critical Finding fix #2) |
| `ecod` / `copod` | `contamination=0.1` (PyOD default) | `contamination=0.05` | `contamination=0.2` | Only shifts `is_anomaly`/`threshold_`, **not** the continuous score exposed via MQTT `sensor.*_score` — be explicit about this in "best for…" copy |
| `pca` | `contamination=0.1` | `contamination=0.05` | `contamination=0.2` | Same caveat as ecod/copod |
| `iforest` | `contamination=0.1`, `n_estimators=100` (PyOD default) | `contamination=0.05`, `n_estimators=100` | `contamination=0.2`, `n_estimators=150` | `n_estimators` affects score stability/quality (more trees = smoother scores), `contamination` affects only the threshold — same caveat |

`[ASSUMED]` — these exact numeric preset values (2.5/3.5/4.5 for peer_divergence threshold; 0.05/0.1/0.2 for contamination) are a reasonable, PyOD-default-centered starting point but are **not derived from any tuning/backtesting in this project** — flag for user confirmation during planning/discuss, consistent with how Phase 5/6 treated the `_THRESHOLD=3.5` constant as "locked" pending real-world validation.

**Param schema shape** (drives `AdvancedParamsDisclosure`, same shape convention as `DetectorParamGrid`'s `FieldSpec`):
```csharp
// orchestrator/Argus.Orchestrator/Web/DetectorCatalog.cs (new, sibling to DetectorDefaults.cs)
public record ParamFieldSchema(string Key, string Type, double? Min, double? Max, string? Step);
public record DetectorPreset(string Label, Dictionary<string, string> Params); // "Low"|"Med"|"High" -> params
public record DetectorCatalogEntry(
    string Name,                       // "peer_divergence" | "ecod" | "copod" | "pca" | "iforest"
    string BestFor,                    // ALGO-03 copy
    List<DetectorPreset> Presets,      // exactly 3: Low, Med, High
    List<ParamFieldSchema> ParamSchema);
```

**Guided-flow → detector mapping (ALGO-04), also catalog-adjacent data, not detector-runtime data:**
```csharp
// "A room/area's related sensors, together" -> ecod (joint mode default)
// "Which one sensor diverges from its peers" -> peer_divergence
// This mapping is UI copy/config, belongs beside DetectorCatalog, not fetched from Python.
```

### Pattern 4: Guided Flow State Machine (ALGO-04)

**What:** A small client-only state machine (no new state management library — `@preact/signals`, same as `state/sensors.ts`) with states `guided-question → guided-pick-shown → manual` (or the reverse transition on override), matching the UI-SPEC's `AlgorithmChooser` states table exactly.

**Example (mirrors `state/sensors.ts`'s existing signal + pure-function-mutator style):**
```typescript
// orchestrator/ui/src/state/groupEditor.ts (new)
import { signal } from '@preact/signals';

export type ChooserMode = 'guided-question' | 'guided-pick-shown' | 'manual';
export const chooserMode = signal<ChooserMode>('guided-question');
export const selectedDetector = signal<string | null>(null);
export const guidedRecommended = signal<string | null>(null); // non-null only while showing "Suggested based on your answer"

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
This satisfies UI-SPEC's non-negotiable rule ("one click on any other card overrides the guided pick with zero friction") — `pickAlgorithmManually` is a single synchronous state update, no confirmation step, matching the `removeDetector`-style zero-friction mutators already in `state/sensors.ts`.

### Anti-Patterns to Avoid
- **Fetching the catalog from Python at request time:** `GET /api/detectors/catalog` must not make a gRPC call — it is static UI/config metadata that must render even when the detector process is down (same principle as `DetectorDefaults.cs` never calling into Python).
- **Re-sorting `contributions` client-side by a different key:** UI-SPEC is explicit — the proto's `FeatureContribution` list is already ranked server-side (Python returns it in member-index order matching `request.series`, and `servicer.py` does not sort it — **verify/add sorting server-side in the .NET cache-population step or the Python servicer, not in the SPA**, since today neither Python nor .NET actually sorts `contributions` by `contribution` value; `AttributionPanel`'s contract assumes it's pre-sorted, which is not yet true).
- **Treating `GroupConfig.Params` as already round-tripping detector behavior:** it round-trips through YAML and the RPC wire today, but (per Critical Finding) two of the group detector implementations ignore it entirely — do not assume "params are already used" from the existence of the `Params` dictionary alone.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Hash routing for `#/groups*` | A new router or regex engine | Extend `router.ts`'s existing hand-rolled matching (same as Phase 7 Pattern 2) | Consistent with the explicit "no router library" constraint already documented in `router.ts`'s own comment |
| Ranked bar visualization | A charting library (Chart.js, Recharts, etc.) | Plain CSS `div` width percentage (UI-SPEC explicit: "not a charting dependency") | UI-SPEC Explicit Non-Goals — no icon/chart library this phase |
| Config write/hot-reload | A new file-watcher or write path | `ConfigWriter.WriteAsync` + `ILiveEntitiesConfig.Swap` (byte-for-byte the same pipeline `/api/sensors/save` uses) | Already atomic (temp+rename), already fires `ConfigChanged`, already validated on load — reimplementing risks losing the atomicity/validate-before-swap guarantees documented in `ConfigWriter.cs`/`EntitiesConfigLoader.cs` |
| Group validation | A new validation framework | Client-side TS functions mirroring `EntitiesConfigLoader.ValidateGroups` (floor=3, unit-consistency via `ResolvedUnits`) exactly, same "client is UX-only, server is authority" split as `validation/detectorParams.ts`/`InputValidator.cs` | Two independent implementations of the same rule (floor, unit match) already coexist safely in this codebase (TS mirrors C#) — proven pattern, don't invent a shared-schema codegen step for this |

**Key insight:** Nearly everything in this phase has a same-shape precedent already merged in Phases 5–7. The risk in this phase is not "we don't know how to build a Preact form" — it's assuming the sensitivity preset already does something on the Python side when it currently does not, and assuming HA area resolution is a single flat lookup when device-inherited areas are a documented HA gotcha.

## Common Pitfalls

### Pitfall 1: Shipping ALGO-01 Low/Med/High as UI-only, with no detector-side effect
**What goes wrong:** The catalog endpoint returns preset → params mappings, the SPA lets the operator pick Low/Med/High, the params get written to `entities.yaml` and sent over the RPC wire — but `GroupMultivariateDetector.__init__` and `PeerDivergenceDetector` never read them, so every preset produces an identical detector and identical scores.
**Why it happens:** The proto (`GroupScoreRequest.params`) and the config model (`GroupConfig.Params`) already exist and look "wired," so it's easy to assume the last mile (Python constructor reading the dict) is also done — it is not (verified directly against `multivariate_detector.py` and `peer_divergence.py` source, and confirmed no `from_params` exists for either, unlike the per-entity `PyODDetector.from_params` precedent).
**How to avoid:** Add an explicit plan/task for `detector/argus_detector/group/multivariate_detector.py` and `peer_divergence.py` to read `contamination`/`n_estimators`/`threshold` from a `params: dict[str, str]` argument (mirroring `PyODDetector.from_params` exactly), and thread `request.params` into their construction in `servicer.py`'s `ScoreGroupBatch`/`FitGroup`. Write a test asserting that two different preset param sets actually produce different `threshold_`/score behavior on the same fixture data.
**Warning signs:** If the plan for this phase has zero Python file changes, this pitfall has not been addressed.

### Pitfall 2: Believing "contamination" makes ECOD/COPOD's exposed score more/less sensitive
**What goes wrong:** The catalog's "best for…" / preset copy implies "High sensitivity = catches more anomalies," but for ECOD/COPOD/PCA/IForest, `contamination` only affects the internal `threshold_` used by `predict()` — the group's `Verdict.score` (what MQTT publishes as the `sensor.*_score` entity, and what `AttributionPanel`/HA dashboards actually display) comes from `decision_function()`, which `contamination` does not touch at all (confirmed: `GroupMultivariateDetector.score_batch` calls `decision_function()`; `is_anomaly()` separately compares against `self._model.threshold_`, which contamination-at-fit-time does set).
**Why it happens:** "Sensitivity" is conflated between "the anomaly score" and "the is_anomaly flag" — they are two different signals in this system's design.
**How to avoid:** In the catalog's "best for…"/preset copy, be precise: for ecod/copod/pca/iforest, phrase the preset effect as "changes how often `is_anomaly` fires for a given score distribution" not "changes the score." Only peer_divergence's `threshold` (once wired per Pitfall 1) directly reshapes the score itself (the modified z-score is threshold-independent, but is_anomaly = `abs(score) > threshold` — same caveat actually applies there too, since peer_divergence's score is likewise threshold-independent; only its binary flag is threshold-dependent).
**Warning signs:** Copy or a UAT scenario that says "High sensitivity produces higher anomaly scores" — this is factually wrong for all 5 detectors; the score value itself is the same regardless of preset, only the anomaly/non-anomaly boundary moves.

### Pitfall 3: HA area resolution silently drops device-inherited areas
**What goes wrong:** SRCH-02/03 group most sensors under "Ungrouped"/domain-fallback because the entity_registry's own `area_id` is null for the majority of real-world entities (most integrations assign area via the *device*, not per-entity) — the browse/suggestion feature looks broken/useless in a live HA instance even though the code is "correct" per a naive entity-only join.
**Why it happens:** This is a well-documented Home Assistant API gotcha, not an Argus-specific bug — `entity_registry_entry.area_id` is frequently null by design.
**How to avoid:** Either fetch `config/device_registry/list` too and resolve `entity.area_id ?? device[entity.device_id]?.area_id`, or explicitly scope-down and document "entities must have an explicit area override to appear grouped; domain fallback covers the rest" as an accepted phase-8 limitation — CONTEXT.md's existing "fallback to domain when no area" wording is compatible with either choice, but the planner must pick one and the human verification step (Phase 8's UAT) should check against a *real* HA instance with device-assigned (not per-entity) areas, which is the common case.
**Warning signs:** A live-HA smoke test where "most sensors show up under a domain fallback instead of their obviously-correct room" — that is this pitfall manifesting, not a save/serialization bug.

### Pitfall 4: `FeatureContribution` list not actually sorted before it reaches the SPA
**What goes wrong:** `AttributionPanel`'s contract (UI-SPEC) says "already ranked server-side... the SPA does not re-sort" — but neither `servicer.py`'s `ScoreGroupBatch` nor any .NET code currently sorts `contributions` by value; it is emitted in `request.series` member order (whatever order the group's members happen to be listed in `GroupConfig.Members`/`BuildGroupScoreRequest`'s dictionary iteration), not by contribution magnitude.
**Why it happens:** Attribution was added in Phase 5 purely as "ranked list" language in the proto/docstring, but the actual sort step was never implemented — Phase 5/6 only logged `response.Contributions[0]` as "top contributor" (`BatchSchedulerWorker.cs` line 243), which happens to work today only because `[0]` in an *unsorted* list is not reliably the true top contributor.
**How to avoid:** Add an explicit sort-by-`contribution`-descending step — either in `GroupStatusCache.Set` (recommended: .NET-side, one `OrderByDescending` call, keeps Python untouched) or in `servicer.py` before returning. Do this as an explicit task; do not assume it for free from "the proto says ranked."
**Warning signs:** `AttributionBar`'s "top-rank gets accent color" renders the wrong bar as accent when member order in `GroupConfig.Members` doesn't happen to match true contribution magnitude — likely invisible in a 3-member test fixture, visible in a 5+ member real group.

## Code Examples

### Reading a config-yaml `Params` dictionary in Python, from_params precedent
```python
# Source: detector/argus_detector/pyod_detector.py (existing, verified in this codebase — the
# pattern Phase 8's Python task should replicate for GroupMultivariateDetector/PeerDivergenceDetector)
@classmethod
def from_params(cls, params: dict[str, str]) -> "PyODDetector":
    threshold = _cast_float(params, "threshold", _DEFAULT_THRESHOLD)
    contamination = _cast_float(params, "contamination", _DEFAULT_CONTAMINATION)
    return cls(threshold=threshold, contamination=contamination)
```

### Existing JSON endpoint pattern to replicate for the 4 new endpoints
```csharp
// Source: orchestrator/Argus.Orchestrator/Program.cs (existing /api/sensors, verbatim pattern)
app.MapGet("/api/groups", (HttpRequest req, ILiveEntitiesConfig liveCfg) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);
    var groups = liveCfg.Get().Groups; // CFG-04: read live, not a captured stale reference
    return Results.Json(new { groups });
});
```

### Existing volatile-cache precedent to replicate for `GroupStatusCache`
```csharp
// Source: orchestrator/Argus.Orchestrator/Ha/HaSensorRegistry.cs (existing pattern)
public sealed class HaSensorRegistry : IHaSensorRegistry
{
    private volatile IReadOnlyList<HaSensorEntry> _snapshot = Array.Empty<HaSensorEntry>();
    // single writer (NetDaemonHaEventSource), many readers (Kestrel) — no lock needed
}
```

## State of the Art

| Old Approach (Phase 7 and earlier) | Phase 8 Approach | When Changed | Impact |
|--------------------------------------|-------------------|---------------|--------|
| Per-entity detectors only (hst/mad/stl), one screen (#/sensors) | Adds group-scoped detectors (peer_divergence/ecod/copod/pca/iforest) on a second screen family (#/groups*) | Phase 8 | New routes, new endpoints, new SPA state module — additive, `#/sensors` untouched |
| `DetectorDefaults.cs` (per-entity hst/mad/stl defaults) is the only "detector metadata" endpoint | New `DetectorCatalog.cs` (group detectors: presets + best-for + schema) — a parallel, NOT a replacement | Phase 8 | Two catalog concepts coexist; do not conflate or merge them — different detector families, different param shapes |
| `GroupConfig.Params` exists in the model/YAML/wire but is inert for group detectors | Phase 8 (recommended) makes it live for at least `contamination`/`n_estimators`/peer `threshold` | Phase 8 (new work, not yet done) | Without this, ALGO-01 is cosmetic only — see Critical Finding |

**Deprecated/outdated:** none — this is the first phase to touch group detector sensitivity; there is no prior approach being replaced.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `config/area_registry/list` and `config/entity_registry/list` return flat arrays with `area_id`/`name` and `entity_id`/`area_id`/`device_id` fields respectively (non-abbreviated, unlike the documented `list_for_display` variant) | HA Registry Enrichment (Pattern 2) | If field names differ, the enrichment join silently returns nulls for area_name — degrade-safely (falls back to domain per CONTEXT.md), but SRCH-02/03 would appear broken until corrected. Recommend a live-HA smoke test early in Wave 0 |
| A2 | Preset numeric values (peer_divergence threshold 2.5/3.5/4.5; contamination 0.05/0.1/0.2; iforest n_estimators 100/150) are a reasonable starting point | Detector Catalog Design (Pattern 3) | Not tuned/backtested — operator may find "High" still too quiet or "Low" too noisy; low risk since Advanced override always available (ALGO-02), but the labels' honesty depends on these being roughly right |
| A3 | Entities without an explicit `area_id` (device-inherited area) are acceptable to fall back to domain grouping for this phase's v1 scope, rather than also resolving via `device_registry` | Common Pitfalls #3 | If most real sensors are device-area-assigned (likely, per HA's own UX design), SRCH-02/03 may look mostly-empty-of-areas in the live verification step unless device_registry resolution is added |
| A4 | `FeatureContribution` needs an explicit sort-by-contribution-descending step added in this phase (not already present) | Common Pitfalls #4 | If wrong (i.e., it already sorts somewhere unseen), the extra sort is harmless (idempotent) — low risk either way, but omitting it when needed breaks the "top bar = accent" UI contract |

**None of these are blockers to starting planning** — they are scoping/task-list inputs. A1 and A3 are best resolved by explicit planner decision (documented in the plan, confirmed against a live HA instance per the phase's existing "Human Verification (carry forward)" note in CONTEXT.md) rather than blind trust in this research.

## Open Questions

1. **Does this phase's scope include the Python `from_params()` change for group detectors, or is it deferred?**
   - What we know: CONTEXT.md and the UI-SPEC both describe ALGO-01/02 purely as a .NET/SPA feature (catalog endpoint + preset UI); neither mentions touching `detector/`.
   - What's unclear: Whether the phase's authors intended "preset selection" to be UI-only theater (params get written to config but never consumed) as an accepted v1 limitation, or assumed (incorrectly, per this research) that the plumbing already existed.
   - Recommendation: Surface this explicitly in planning/discuss — either descope the phase to be honest that presets don't yet affect detection (documented limitation, still useful as the correct plumbing/UX skeleton for a later fix) or add 1-2 focused Python tasks (small: `from_params` on 2 files + wiring in `servicer.py`, well under this phase's existing complexity budget) so ALGO-01 is truthful on day one. Given the user's "instruction-adherence" profile and the explicit ALGO-01..04 requirement language ("mapping to underlying parameters"), recommend including the Python fix rather than shipping cosmetic-only presets.

2. **Should area resolution include `device_registry` for SRCH-02/03, or is entity-only + domain-fallback acceptable v1 scope?**
   - What we know: CONTEXT.md says "fallback to domain when no area" — compatible with either interpretation.
   - What's unclear: Whether "no area" means "entity_registry.area_id is null" (narrow) or "no *effective* area including device inheritance" (correct, per real HA behavior).
   - Recommendation: Default to entity-only + domain-fallback for v1 (smaller task, matches literal CONTEXT.md wording) but flag device-registry resolution as a fast-follow if live verification shows most sensors falling through to domain grouping.

3. **Where does the `contributions` sort-by-value step belong — Python `servicer.py` or .NET `GroupStatusCache`?**
   - What we know: Either location produces the same observable result for the SPA.
   - What's unclear: Which is more consistent with the project's existing "orchestrator does no ML-adjacent logic, Python owns all ML-adjacent output shaping" split (see PROJECT.md D2: "All ML in Python").
   - Recommendation: Sort in .NET (`GroupStatusCache.Set`, one `OrderByDescending(c => c.Contribution)` call) — sorting a list by a numeric field is not "ML logic," it's response shaping for a UI-specific cache the Python side has no knowledge of, and keeps the Python attribution contract (`Pitfall 1` docstring: "ranked; empty for non-attributable detectors") technically true at the wire level without requiring a Python change for this specific fix.

## Environment Availability

Not applicable — this phase has no new external tool/service dependencies. HA WebSocket connectivity, InfluxDB, and the Python detector gRPC service are all pre-existing dependencies already covered by Phase 1-7's environment setup; no new probe is needed.

## Security Domain

`workflow.nyquist_validation` is `false` in `.planning/config.json`; per the project's own config, this does not itself disable the Security Domain requirement (that's a separate `security_enforcement` flag, which is absent = enabled). Addressing it:

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | No (new) | Ingress-proxied; auth is `IsAuthorizedRequest` (Supervisor-IP / loopback check), unchanged from Phase 2/7 — no new auth surface introduced by Phase 8 |
| V3 Session Management | No | No sessions; stateless request auth per-call, same as existing endpoints |
| V4 Access Control | Yes | Every new endpoint (`/api/groups`, `/api/groups/save`, `/api/detectors/catalog`, `/api/groups/{id}/status`) MUST call `IsAuthorizedRequest(req.HttpContext)` first, exactly like all 3 existing `/api/*` endpoints — this is a checklist item for plan-checker/code-review, not new design |
| V5 Input Validation | Yes | `POST /api/groups/save` body must be validated server-side (min-member floor, unit consistency, known detector name) BEFORE any write — mirror `InputValidator.Validate` gate pattern used by `/api/sensors/save` (validate-before-write, never trust client-side validation alone) |
| V6 Cryptography | No | No new crypto surface (mTLS to detector is pre-existing/unchanged) |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Malformed/oversized `POST /api/groups/save` body (e.g. thousands of fake members) | Denial of Service | Reuse the existing `ReadFromJsonAsync<T>` + try/catch JSON-exception pattern from `/api/sensors/save`; consider a sane upper bound on `Members.Count` server-side (not just the floor of 3) since nothing currently caps group size |
| `{id}` path parameter in `GET /api/groups/{id}/status` used to probe for group existence / enumerate group_ids | Information Disclosure (low severity — single-operator, no multi-tenancy per PROJECT.md D9) | Return a consistent 200-with-null-status (not 404) for unknown group_id, avoiding a trivial existence oracle — low priority given this is a self-hosted single-operator tool, but cheap to do right |
| Group `Params` dictionary accepting arbitrary string keys/values from the Advanced form, forwarded verbatim into the Python `params` map and eventually `float()`-cast | Tampering (client sends non-numeric garbage) | Already mitigated by the existing `_cast_float` catch/default pattern in `pyod_detector.py` (any bad value silently falls back to the default) — replicate this exact catch-and-default behavior in any new `from_params` code for group detectors, do not let a bad param 500 the detector RPC |

## Sources

### Primary (HIGH confidence — direct codebase reads, this session)
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs`, `EntitiesConfigLoader.cs`, `ConfigWriter.cs`, `LiveEntitiesConfig.cs` — group config model, validation, write/swap pipeline
- `orchestrator/Argus.Orchestrator/Program.cs` — `IsAuthorizedRequest`, existing `/api/sensors*`/`/api/detectors/defaults` endpoint patterns
- `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` — exact point where group verdict + `Contributions` are already available, ready for cache population
- `orchestrator/Argus.Orchestrator/Ha/HaWebSocketClient.cs`, `NetDaemonHaEventSource.cs`, `IHaSensorRegistry.cs`, `HaSensorRegistry.cs` — existing WS client shape, `get_states` call site, sensor registry model
- `orchestrator/Argus.Orchestrator/Health/ArgusHealthSignals.cs` — volatile-cache precedent
- `proto/argus.proto` — `GroupScoreResponse.contributions` / `FeatureContribution` already on the wire
- `detector/argus_detector/group/multivariate_detector.py`, `peer_divergence.py`, `servicer.py` — **confirmed group detectors take zero tunable params from `request.params`; peer threshold is a hardcoded module constant** (Critical Finding, verified by direct source read, not inference)
- `detector/argus_detector/pyod_detector.py` — existing `from_params()` precedent for the per-entity MAD detector
- `orchestrator/ui/src/` (router.ts, api/client.ts, api/types.ts, state/sensors.ts, validation/detectorParams.ts, components/*) — Phase 7 SPA patterns to replicate
- `.planning/phases/08-group-config-ui-algorithm-chooser/08-CONTEXT.md`, `08-UI-SPEC.md` — locked decisions and design contract for this phase
- `.planning/REQUIREMENTS.md`, `.planning/STATE.md` — requirement text and accumulated project decisions

### Secondary (MEDIUM confidence — WebSearch, cross-checked against multiple community/official sources)
- [PyOD PCA/COPOD/ECOD/IForest contamination parameter](https://pyod.readthedocs.io/en/latest/pyod.models.html) — contamination default 0.1, range (0, 0.5), affects `threshold_`/`predict()` only, not `decision_function()`
- [PyOD IForest n_estimators](https://github.com/yzhao062/pyod/blob/master/pyod/models/iforest.py) — default 100
- [Home Assistant WebSocket API developer docs](https://developers.home-assistant.io/docs/api/websocket/) — confirms `config/entity_registry/list_for_display` shape (abbreviated keys); does not document the plain `list` variant field-by-field

### Tertiary (LOW confidence — WebSearch only, flagged for live verification)
- Exact field names/shape of `config/area_registry/list` and `config/entity_registry/list` (non-`list_for_display`) responses — not found in official docs during this session; recommend a live-HA smoke test in Wave 0 of implementation (consistent with this project's existing precedent of deferring live-HA-only verification, per STATE.md "Live research gaps" section for Phases 1/2)
- Device-registry area-inheritance behavior (`entity.area_id ?? device.area_id`) — well-known community pattern (multiple forum/GitHub threads referenced it) but not from an official HA source in this session

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — zero new packages, entirely reuses verified Phase 5-7 stack
- Architecture (SPA components, .NET endpoints, cache pattern): HIGH — direct precedent read from Phase 7 code for every new piece
- Architecture (HA registry field shapes): LOW — not confirmed against live HA or official exhaustive docs this session
- Pitfalls (Python param plumbing gap): HIGH — verified by direct source read of `multivariate_detector.py`/`peer_divergence.py`/`servicer.py`, not inferred
- Pitfalls (contribution sort, contamination-vs-score semantics): HIGH — verified against PyOD docs + direct source read

**Research date:** 2026-07-02
**Valid until:** 30 days (stable — no fast-moving dependencies; the Python param-plumbing finding does not expire, it is a static fact about the current codebase state until fixed)
