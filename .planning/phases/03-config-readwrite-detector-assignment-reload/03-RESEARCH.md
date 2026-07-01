# Phase 3: Config Read/Write + Detector Assignment + Reload — Research

**Researched:** 2026-07-01
**Domain:** .NET 8 live-config reload, htmx form encoding, MQTT discovery retraction
**Confidence:** HIGH — all findings grounded in codebase reads; no external docs needed

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- Detector management is an expandable section on each tracked entity row in the Phase-2 picker (not a separate page or modal).
- Per-detector dropdown (HST / MAD / STL) with "Add detector" control; multiple detectors per entity supported.
- UI labels timing honestly: HST = "streaming (live, ~2 s reload)"; MAD/STL = "batch (runs every N min)".
- Default to `hst` with sane defaults when no explicit assignment exists.
- Param editing uses per-type known fields with defaults, persisted into `DetectorConfig.Params` map — NO model change.
  - HST: `window`, `n_trees`, `high_threshold`, `low_threshold`, `min_consecutive`, `frozen_window`, `frozen_variance_threshold`.
  - MAD: `threshold`, `window`.
  - STL: `period`, `seasonal`, `threshold`.
- Validation is client hints only in Phase 3; full server+client validation is Phase 4.
- Reload mechanism: `ILiveEntitiesConfig` singleton with `volatile` reference swapped via `Interlocked.Exchange`, firing `ConfigChanged` event AFTER the swap. `HaListenerWorker` subscribes; on change cancels an inner CTS (NOT stoppingToken) and restarts `ScoreStreamPipeline.RunAsync` loop. MQTT + gRPC stay alive. Streaming gap target < 1s; SC2 "within 2s" for HST/streaming.
- `BatchSchedulerWorker` must change from ctor-captured `EntitiesConfig` to `ILiveEntitiesConfig.Get()` per batch cycle.
- Removed-entity MQTT retraction (SC4): diff old vs new, publish empty retained payloads to discovery topics of removed entities BEFORE restarting the loop; ≤30s.
- Save feedback: htmx banner "Saved — pipeline reloading…" → success; no full page reload.
- Config UI reads `/data/entities.yaml` via `EntitiesConfigLoader` to pre-fill per-entity assignments and params.

### Claude's Discretion
- Exact htmx wiring for expandable rows / add-detector control.
- Precise param-field set beyond the known keys listed above.
- Whether `ScoreStreamPipeline` takes `ILiveEntitiesConfig` by ctor or reads it in `BuildEntityStates` (must read the swapped config on restart).

### Deferred Ideas (OUT OF SCOPE)
- Full input validation (server + client) with error messages — Phase 4.
- CI multi-arch packaging + image-size gate + DOCS.md — Phase 4.
- Full `validate_session` Ingress auth middleware — Phase 4.
- Typed C# param accessors for MAD/STL — Phase 4.
- Dynamic timing caption update on dropdown change — Phase 4.
- Live update of disclosure toggle counter after add/remove — Phase 4.
- SSE / htmx polling for reload completion — Phase 4 (synchronous POST response in Phase 3).
- FileSystemWatcher debounce validation — Phase 4.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| UI-03 | UI assigns one or more detectors (HST, MAD, STL) with editable parameters to each tracked entity | EntityPickerPage extension via `<details>`/`<summary>` disclosure; indexed form encoding `detectors[i][j][name]`/`detectors[i][j][params][key]`; GET `/api/detectors/new-entry` fragment endpoint; POST `/api/sensors/save` extended parser |
| CFG-03 | Per-entity detector/parameter assignments persist in the structure `EntitiesConfigLoader` expects; multiple detectors per entity supported; sane defaults when unset | `EntitiesConfigLoader` already reads `Detectors: [{Name, Params}]` via YamlDotNet; `ConfigWriter.WriteAsync` is reusable; Phase 2 save handler must be extended to parse indexed detector fields and write them alongside entities |
| CFG-04 | Configuration changes apply to the running orchestrator within seconds via reload, without restarting the add-on | `ILiveEntitiesConfig` singleton (new); `Interlocked.Exchange` swap + `ConfigChanged` event; inner-CTS restart loop in `HaListenerWorker`; `ScoreStreamPipeline.BuildEntityStates()` reads live config at `RunAsync` entry; `BatchSchedulerWorker` migrates `_entities` field to per-cycle `Get()` calls |
</phase_requirements>

---

## Summary

Phase 3 is a cross-cutting plumbing change under an incremental UI layer. The UI work (extending the Phase-2 picker with detector disclosure rows) is mechanically straightforward — htmx fragment endpoint + indexed form encoding + server-side HTML builder extension. The hard part is the reload plumbing: introducing `ILiveEntitiesConfig` as a new cross-cutting singleton that every config-reading component must migrate to.

The codebase has three config consumers today: `ScoreStreamPipeline` (ctor-captures `EntitiesConfig` as `_entitiesConfig`), `BatchSchedulerWorker` (ctor-captures `EntitiesConfig` as `_entities`), and `MqttPublisherWorker` (ctor-captures `EntitiesConfig` as `_entities`). All three are registered in `Program.cs` against the raw `AddSingleton(entitiesConfig)` — that registration must be replaced by `ILiveEntitiesConfig`. `ScoreStreamPipeline` and `BatchSchedulerWorker` are both in the must-migrate list. `MqttPublisherWorker` only uses the config at startup for initial discovery publish; for Phase 3 it needs to be re-evaluated for the retraction path (it must re-publish discovery on reload), but it does NOT need per-cycle live reads.

`HaListenerWorker.ExecuteAsync` currently calls `_scoreStreamPipeline.RunAsync(...)` exactly once and exits. Converting it to an inner-CTS restart loop is a contained change in that single method. The `ConfigChanged` event fires on the `ILiveEntitiesConfig` singleton; the subscriber in `HaListenerWorker` must cancel the inner CTS and allow the loop to re-enter `RunAsync`. `ScoreStreamPipeline.BuildEntityStates()` — called at `RunAsync` entry — currently reads `_entitiesConfig.Entities` (the ctor-captured stale reference). It must instead read from `ILiveEntitiesConfig.Get()` so that restarts pick up the new config. The simplest correct implementation is to inject `ILiveEntitiesConfig` into the pipeline's ctor and call `Get()` inside `BuildEntityStates()`.

MQTT retraction is deterministic: `DiscoveryPublisher.PublishAllAsync` already shows the topic formula (`homeassistant/binary_sensor/{anomalyId}/config` and `homeassistant/sensor/{scoreId}/config`). An empty retained payload to those same topics removes the HA entity. The retraction diff runs in the reload handler (in `HaListenerWorker`) before the inner-CTS cancel, so HA gets the retraction while the broker connection is still alive.

**Primary recommendation:** Introduce `ILiveEntitiesConfig` as a new class in the `Config` namespace. Migrate the three consumers by ctor argument. Extend `HaListenerWorker.ExecuteAsync` to a restart loop. Extend `Program.cs` to register the new singleton and wire the save endpoint to call `Swap`. Extend `EntityPickerPage` + `Program.cs` for the UI/API changes.

---

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Detector assignment UI (disclosure rows, param fields) | ASP.NET Minimal API (server-rendered HTML) | Browser (inline onclick remove, native `<details>`) | All rendering is server-side; the only client-side logic is the 3-line inline remove onclick and native HTML details |
| Add-detector htmx fragment (`GET /api/detectors/new-entry`) | ASP.NET Minimal API | — | Returns a new `.argus-detector-entry` HTML fragment with HST defaults |
| Save + parse indexed detector fields (`POST /api/sensors/save`) | ASP.NET Minimal API | — | Form parsing, GlobExpander resolve, detector list reconstruction, YAML write, ILiveEntitiesConfig.Swap |
| Config pre-fill (read current entities.yaml for UI) | ASP.NET Minimal API (via EntitiesConfigLoader) | — | Reads the live reference from ILiveEntitiesConfig.Get(); no disk re-read needed on each page load |
| ILiveEntitiesConfig singleton (volatile swap) | ASP.NET process (Config namespace) | — | Single writer (ConfigWriter + save endpoint), many readers (pipeline, batch, worker) |
| Reload restart loop | HaListenerWorker (BackgroundService) | — | Subscribes ConfigChanged, cancels inner CTS, re-runs ScoreStreamPipeline.RunAsync |
| HST streaming pipeline re-init with new config | ScoreStreamPipeline.BuildEntityStates() | — | Reads ILiveEntitiesConfig.Get() at RunAsync entry; MQTT + gRPC transport stay alive |
| MAD/STL batch cycle update | BatchSchedulerWorker | — | Reads ILiveEntitiesConfig.Get() at each RunBatchAsync / RunNightlyFitAsync entry |
| MQTT discovery retraction for removed entities | HaListenerWorker (reload handler) | MqttConnection / DiscoveryPublisher | Diff old vs new entity sets; publish empty retained payload before inner-CTS cancel |
| Discovery publish for newly-added entities | MqttPublisherWorker (post-reload) | DiscoveryPublisher | Needs to republish after reload; see MqttPublisherWorker analysis below |

---

## Standard Stack

No new dependencies introduced in Phase 3. All libraries are already in the project.

### Core (already in project — no install needed)
| Library | Version | Purpose | Note |
|---------|---------|---------|------|
| YamlDotNet | existing | Deserialize entities.yaml with `Detectors` list | `EntitiesConfigLoader.Load` uses `DeserializerBuilder` + `UnderscoredNamingConvention` + `IgnoreUnmatchedProperties` — already works with `DetectorConfig {Name, Params}` |
| MQTTnet | 5.1.0.1559 | Publish empty retained payload for retraction | `MqttConnection.PublishAsync(topic, payload:"", retain:true, ct)` — empty string payload retracts |
| htmx | 2.0.10 | Add-detector htmx GET fragment; save POST | Already committed to `wwwroot/js/htmx.min.js` |

[VERIFIED: codebase read — EntitiesConfig.cs, ConfigWriter.cs, MqttConnection.cs, EntityPickerPage.cs]

---

## Architecture Patterns

### System Architecture Diagram

```
User browser
    │
    │ GET /sensors → full page (pre-filled detector disclosure rows)
    │ GET /api/detectors/new-entry?entity_idx=i&det_idx=j → HTML fragment (htmx)
    │ POST /api/sensors/save (extended: entities[] + detectors[i][j][name/params])
    ▼
ASP.NET Minimal API (Program.cs)
    │
    ├─ EntityPickerPage.BuildFullPage() — reads ILiveEntitiesConfig.Get()
    │     └─ renders <details> disclosure rows with pre-filled detector entries
    │
    ├─ GET /api/detectors/new-entry — returns blank detector entry HTML (HST defaults)
    │
    └─ POST /api/sensors/save
          │
          ├─ parse form: entities[], patterns, detectors[i][j][name/params]
          ├─ GlobExpander.Resolve → resolved entity list
          ├─ reconstruct List<EntityConfig> with full Detectors lists
          ├─ YamlDotNet serialize → ConfigWriter.WriteAsync (atomic rename)
          ├─ ILiveEntitiesConfig.Swap(newConfig)
          │      └─ Interlocked.Exchange → ConfigChanged event fired
          └─ return HTML banner ("Saved — pipeline reloading…" then success)

ILiveEntitiesConfig (singleton)
    │
    │ ConfigChanged event
    ▼
HaListenerWorker.ExecuteAsync (restart loop)
    │
    ├─ diff old vs new entity sets
    ├─ MqttConnection.PublishAsync(discoveryTopic, "", retain:true) × removed entities
    ├─ cancel innerCts
    └─ loop: ScoreStreamPipeline.RunAsync(_haEventSource.ReadAllAsync(innerCt), innerCt)
                 └─ BuildEntityStates() reads ILiveEntitiesConfig.Get() at entry

BatchSchedulerWorker (parallel, independent)
    └─ RunBatchAsync / RunNightlyFitAsync: _liveConfig.Get().Entities per cycle

MqttPublisherWorker (stays alive across reloads)
    └─ needs to republish discovery for new entities on ConfigChanged
       (see MqttPublisherWorker migration analysis below)
```

### Recommended Project Structure

No new directories. Changes are within existing files + one new file:

```
orchestrator/Argus.Orchestrator/
├── Config/
│   ├── EntitiesConfig.cs           # unchanged
│   ├── EntitiesConfigLoader.cs     # unchanged
│   ├── ConfigWriter.cs             # unchanged
│   └── LiveEntitiesConfig.cs       # NEW — ILiveEntitiesConfig + LiveEntitiesConfig
├── Web/
│   └── EntityPickerPage.cs         # extended (BuildFullPage, BuildDetectorEntry)
├── Workers/
│   ├── HaListenerWorker.cs         # extended (inner-CTS restart loop, retraction)
│   └── MqttPublisherWorker.cs      # migrated (ILiveEntitiesConfig, ConfigChanged subscription)
├── Batch/
│   └── BatchSchedulerWorker.cs     # migrated (_entities → _liveConfig.Get())
├── Detection/
│   └── ScoreStreamPipeline.cs      # migrated (ILiveEntitiesConfig, BuildEntityStates reads Get())
└── Program.cs                      # register ILiveEntitiesConfig; wire save endpoint; add /api/detectors/new-entry
```

---

## Detailed Design: Each Research Question Answered

### Q1 — ILiveEntitiesConfig: Interface, Swap, Thread Safety

**Design (confirmed from patterns in codebase):**

```csharp
// Config/LiveEntitiesConfig.cs
public interface ILiveEntitiesConfig
{
    EntitiesConfig Get();
    void Swap(EntitiesConfig newConfig);
    event EventHandler? ConfigChanged;
}

public sealed class LiveEntitiesConfig : ILiveEntitiesConfig
{
    private volatile EntitiesConfig _current;

    public LiveEntitiesConfig(EntitiesConfig initial)
        => _current = initial;

    public EntitiesConfig Get() => _current;

    public void Swap(EntitiesConfig newConfig)
    {
        Interlocked.Exchange(ref _current, newConfig);
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ConfigChanged;
}
```

**Thread safety analysis:**
- `volatile EntitiesConfig _current` ensures readers always see the latest reference (no CPU cache stale read). The `volatile` keyword on a reference type provides the store/load barrier needed. [VERIFIED: mirrors `HaSensorRegistry._snapshot` (volatile IReadOnlyList) and `ArgusHealthSignals.HaConnected/DetectorConnected` (volatile bool) — both confirmed in codebase reads]
- `Interlocked.Exchange` is a full memory barrier — guarantees the reference is visible to all threads immediately after the swap. [ASSUMED — .NET memory model; consistent with documented behavior]
- Single writer: `ConfigWriter.WriteAsync` is guarded by `SemaphoreSlim(1,1)`. The save endpoint awaits `WriteAsync` before calling `Swap`, so two concurrent saves cannot produce interleaved partial swaps. [VERIFIED: ConfigWriter.cs lines 18-20]
- Many readers: `Get()` is a single volatile read — no lock needed, no torn read possible (object reference is word-sized on 64-bit). [ASSUMED — .NET reference type reads are atomic on 64-bit; consistent with codebase pattern]
- `ConfigChanged` event firing AFTER the swap means subscribers who call `Get()` always see the new config.

**Registration change in Program.cs:**

Current (lines 22-23):
```csharp
var entitiesConfig = EntitiesConfigLoader.Load(entitiesPath, entitiesLogger);
builder.Services.AddSingleton(entitiesConfig);
```

Must become:
```csharp
var entitiesConfig = EntitiesConfigLoader.Load(entitiesPath, entitiesLogger);
var liveConfig = new LiveEntitiesConfig(entitiesConfig);
builder.Services.AddSingleton<ILiveEntitiesConfig>(liveConfig);
// Keep raw AddSingleton(entitiesConfig) ONLY if any consumer cannot be migrated;
// target: remove it entirely after migration.
```

**Consumers to migrate (from Program.cs reads):**

| Consumer | Current injection | Migration |
|----------|------------------|-----------|
| `ScoreStreamPipeline` (line 119) | `EntitiesConfig` via ctor | `ILiveEntitiesConfig` via ctor |
| `BatchSchedulerWorker` factory (line 148) | `sp.GetRequiredService<EntitiesConfig>()` | `sp.GetRequiredService<ILiveEntitiesConfig>()` |
| `MqttPublisherWorker` (line 113) | `EntitiesConfig` via ctor | `ILiveEntitiesConfig` via ctor |
| `HaListenerWorker` (line 93) | no direct config injection | receives `ILiveEntitiesConfig` to subscribe to `ConfigChanged` |
| `GET /sensors` handler (line 224) | `EntitiesConfig config` parameter | `ILiveEntitiesConfig` resolved from DI |

[VERIFIED: Program.cs full read, constructor signatures in each file]

**`EntitiesConfig` raw singleton can be removed from DI** once all consumers are migrated — but keeping it temporarily as a fallback during migration is fine.

---

### Q2 — HaListenerWorker Reload Loop

**Current state (HaListenerWorker.cs lines 33-50):**

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    await _gateway.WaitForHealthyAsync(stoppingToken);
    if (stoppingToken.IsCancellationRequested) return;
    await _scoreStreamPipeline.RunAsync(
        _haEventSource.ReadAllAsync(stoppingToken), stoppingToken);  // one-shot
}
```

**Required inner-CTS restart loop:**

The loop must:
1. Subscribe to `ConfigChanged` once (before the loop).
2. On each iteration, create a fresh inner `CancellationTokenSource` linked to `stoppingToken`.
3. Pass `innerCts.Token` to `RunAsync` and `ReadAllAsync`.
4. On `ConfigChanged`: perform retraction diff, then cancel `innerCts`.
5. Catch `OperationCanceledException` from the inner token — check if it was the inner token (not `stoppingToken`) to distinguish reload from shutdown.
6. Re-loop only when inner token was cancelled (config changed); exit cleanly when `stoppingToken` fires.

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    await _gateway.WaitForHealthyAsync(stoppingToken);
    if (stoppingToken.IsCancellationRequested) return;

    EntitiesConfig? lastConfig = null;
    CancellationTokenSource? innerCts = null;

    // Subscribe to config changes — fires on the thread-pool, must be captured safely
    void OnConfigChanged(object? sender, EventArgs e)
    {
        // Retraction diff happens before the loop re-starts; capture current CTS
        var cts = innerCts;
        if (cts is not null && !cts.IsCancellationRequested)
            cts.Cancel();  // triggers OperationCanceledException in RunAsync
    }
    _liveConfig.ConfigChanged += OnConfigChanged;

    try
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var currentConfig = _liveConfig.Get();

            // MQTT retraction: diff removed entities before restart
            if (lastConfig is not null)
                await RetractRemovedEntitiesAsync(lastConfig, currentConfig, stoppingToken);

            lastConfig = currentConfig;

            innerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            try
            {
                await _scoreStreamPipeline.RunAsync(
                    _haEventSource.ReadAllAsync(innerCts.Token), innerCts.Token);
            }
            catch (OperationCanceledException) when (
                innerCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                // Inner cancel (config changed) — loop re-enters
                _logger.LogInformation("Pipeline restarting due to config change");
            }
            finally
            {
                innerCts.Dispose();
                innerCts = null;
            }
        }
    }
    finally
    {
        _liveConfig.ConfigChanged -= OnConfigChanged;
    }
}
```

**Key correctness properties:**
- `OperationCanceledException` when `stoppingToken.IsCancellationRequested` is NOT caught by the `when` guard — it propagates out of `ExecuteAsync`, causing the host to log it as expected shutdown. [ASSUMED — standard BackgroundService pattern; consistent with existing worker pattern in BatchSchedulerWorker.cs line 115]
- The `OnConfigChanged` handler runs on a threadpool thread (event is synchronous but called from the save endpoint's request thread). It only touches `innerCts` which is captured by reference as a field — safe because the `volatile`-like write is to a local captured var; `CancellationTokenSource.Cancel()` is thread-safe. [ASSUMED — CancellationTokenSource.Cancel() documented as thread-safe]
- `lastConfig` starts null — first loop iteration skips retraction diff. [VERIFIED: design matches CONTEXT.md "diff old vs new entity sets"]
- `_haEventSource.ReadAllAsync(innerCts.Token)` — verified that `IHaEventSource.ReadAllAsync` takes a `CancellationToken` parameter. [VERIFIED: Ha/IHaEventSource.cs implied by HaListenerWorker.cs line 49]

**Constructor additions needed:**

```csharp
// Add to HaListenerWorker ctor signature:
private readonly ILiveEntitiesConfig _liveConfig;
private readonly MqttConnection _mqtt;  // for retraction publishes
```

[VERIFIED: current ctor does not inject these; Program.cs must add them to the AddHostedService registration]

---

### Q3 — Streaming Gap During Swap

**MQTT and gRPC stay alive during swap — confirmed:**
- `MqttConnection` is a long-lived singleton registered in DI (Program.cs line 106). It is NOT torn down during the pipeline restart. The `MqttPublisherWorker` manages the MQTT connection lifecycle independently.
- `GrpcChannel` is a singleton (Program.cs line 64). `DetectionGateway` wraps it (line 71). Neither is affected by the pipeline restart.
- The `HaListenerWorker.ExecuteAsync` restart loop only cancels the inner CTS — the HA WebSocket event source (`IHaEventSource`) continues running in `NetDaemonHaEventSource` which is its own singleton.
- [VERIFIED: Program.cs registrations read in full — each of these is `AddSingleton`]

**Realistic reload latency analysis:**

The streaming gap is the window between inner-CTS cancel and when the new `RunAsync` starts receiving readings:
1. `ConfigChanged` fires → `innerCts.Cancel()` → `OperationCanceledException` propagates through `RunAsync`.
2. `RunAsync` cancellation propagates through all per-entity streams (`Task.WhenAll` + fan-out task).
3. Inner CTS disposed, `RetractRemovedEntitiesAsync` runs (MQTT publish, ~10-50ms per entity over LAN).
4. New `innerCts` created, new `RunAsync` starts, `BuildEntityStates()` called.
5. New per-entity gRPC streams opened.

The key delay is step 2-5. Steps 2-3 involve network I/O (retraction publishes), but the HA event source is still producing readings during this window — they are simply dropped by the fan-out (no matching channel after inner-CTS cancel). This is acceptable; the HC target of < 1s for the pipeline restart itself (no retraction) is achievable on LAN. SC2's "within 2s" includes the retraction publish time.

**Potential gap: `HaEventSource` readings lost during restart** — for Phase 3, acceptable. No buffering is required per CONTEXT.md.

---

### Q4 — BatchSchedulerWorker Migration

**Current ctor-captured field (BatchSchedulerWorker.cs line 37):**
```csharp
private readonly EntitiesConfig _entities;
```

**Set in both constructors (lines 56, 69):**
```csharp
_entities = entities ?? throw new ArgumentNullException(nameof(entities));
```

**Used in two internal methods:**
- `RunBatchAsync` (line 127): `foreach (var entity in _entities.Entities)`
- `RunNightlyFitAsync` (line 215): `foreach (var entity in _entities.Entities)`

**Migration:**
Replace `private readonly EntitiesConfig _entities` with `private readonly ILiveEntitiesConfig _liveConfig` and change both call sites to `_liveConfig.Get().Entities`. The two test constructors (line 44, 63) must be updated to accept `ILiveEntitiesConfig` instead of `EntitiesConfig`. Existing `BatchSchedulerWorkerTests` construct with `EntitiesConfig` directly — they need a compatibility shim or direct `LiveEntitiesConfig` wrapper.

**Program.cs factory (line 143-150):**
```csharp
builder.Services.AddHostedService<BatchSchedulerWorker>(sp => new BatchSchedulerWorker(
    sp.GetRequiredService<ConnectionSettings>(),
    sp.GetRequiredService<IInfluxDataSource>(),
    sp.GetRequiredService<IBatchDetectorClient>(),
    sp.GetRequiredService<IStatePublisher>(),
    sp.GetRequiredService<EntitiesConfig>(),   // ← change to ILiveEntitiesConfig
    sp.GetRequiredService<DetectionGateway>(),
    sp.GetRequiredService<ILogger<BatchSchedulerWorker>>()));
```

No other capture exists. [VERIFIED: BatchSchedulerWorker.cs full read — `_entities` only appears in RunBatchAsync and RunNightlyFitAsync]

---

### Q5 — MQTT Retraction for Removed Entities

**Topic formula (from DiscoveryPublisher.cs lines 134-144):**
```
homeassistant/binary_sensor/{anomalyId}/config   retain=true  payload="" (empty)
homeassistant/sensor/{scoreId}/config            retain=true  payload="" (empty)
```

Where:
- `anomalyId = "argus_{slug}_{detector}_anomaly"` — `UniqueId.AnomalyId(entityId, detector)`
- `scoreId = "argus_{slug}_{detector}_score"` — `UniqueId.ScoreId(entityId, detector)`
- `slug = entityId.Replace(".", "_")`
- `detector = entity.Detectors[0].Name` (first detector — same as `GetDetectorName()`)

[VERIFIED: DiscoveryPublisher.cs lines 43-44, UniqueId.cs full read]

**Empty payload retraction via MqttConnection:**

`MqttConnection.PublishAsync` (line 79) accepts `string payload`. Passing `string.Empty` with `retain: true` publishes an empty retained message — the MQTT retained payload deletion mechanism. HA removes the discovery entity when it receives an empty retained payload on the config topic.

[ASSUMED — MQTT spec; consistent with HomeAssistant documentation behavior. The MqttConnection itself supports this — the only required check is that MQTTnet 5 doesn't reject empty string payloads, which it does not per standard behavior]

**Retraction helper implementation:**

```csharp
private async Task RetractRemovedEntitiesAsync(
    EntitiesConfig oldConfig,
    EntitiesConfig newConfig,
    CancellationToken ct)
{
    var newIds = newConfig.Entities
        .Select(e => e.EntityId)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    foreach (var entity in oldConfig.Entities)
    {
        if (newIds.Contains(entity.EntityId)) continue;

        var detector = entity.Detectors.Count > 0 ? entity.Detectors[0].Name : "hst";
        var anomalyTopic = $"homeassistant/binary_sensor/{UniqueId.AnomalyId(entity.EntityId, detector)}/config";
        var scoreTopic   = $"homeassistant/sensor/{UniqueId.ScoreId(entity.EntityId, detector)}/config";

        await _mqtt.PublishAsync(anomalyTopic, string.Empty, retain: true, ct);
        await _mqtt.PublishAsync(scoreTopic,   string.Empty, retain: true, ct);
    }
}
```

**Where retraction runs:** In `HaListenerWorker.ExecuteAsync`, at the top of each loop iteration after the first (when `lastConfig != null`), BEFORE creating the new inner CTS. This ensures the MQTT publish happens while the broker connection is alive and before new `RunAsync` starts. [VERIFIED: CONTEXT.md: "BEFORE restarting the loop"]

**Multi-detector retraction:** The current `GetDetectorName()` uses `entity.Detectors[0].Name`. Phase 3 adds multi-detector support but the discovery entity is still keyed to the first detector name (matching `BuildBinarySensorConfig` / `BuildSensorConfig` behavior). If a future phase changes detector-per-discovery-topic semantics, retraction must be updated. For Phase 3, first-detector is correct.

---

### Q6 — Detector Params Round-Trip

**YAML serialization (Phase 2 save handler in Program.cs line 286-290):**
```csharp
var entities = resolvedIds
    .Select(id => new EntityConfig
    {
        EntityId = id,
        FriendlyName = entry?.FriendlyName ?? "",
        Detectors = [new DetectorConfig { Name = "hst", Params = [] }],
    })
    ...
```

Phase 2 hard-codes `hst` with empty params. Phase 3 must replace this with the submitted detector list from the form.

**YamlDotNet serialization of `DetectorConfig`:**
`DetectorConfig.Params` is `Dictionary<string, string>`. The `UnderscoredNamingConvention` serializer converts `Params` → `params` in YAML. The deserializer (`DeserializerBuilder` + `IgnoreUnmatchedProperties`) reads `params:` back into `Dictionary<string, string>`. This round-trip is already used by `BatchSchedulerWorker.BuildScoreBatchRequest` (line 193: `foreach (var (key, value) in detectorCfg.Params) request.Params[key] = value`) — proven to work. [VERIFIED: EntitiesConfig.cs, BatchSchedulerWorker.cs lines 193-195, EntitiesConfigLoader.cs — full reads]

**HstParams defaults:** `HstParams.From(dict)` uses `GetInt`/`GetDouble` with fallback defaults. Keys absent from the YAML (i.e., params sent as empty from the UI) fall back to defaults in the C# layer. For the Python detector, sending an empty `Params` map is valid — Python interprets absent keys as defaults too. [VERIFIED: EntitiesConfig.cs HstParams.From lines 49-61]

**MAD/STL params:** Already dispatched generically. `BatchSchedulerWorker.BuildScoreBatchRequest` iterates all `detectorCfg.Params` entries regardless of detector type (lines 193-195). No C# type-specific handling needed. [VERIFIED: BatchSchedulerWorker.cs lines 193-195]

---

### Q7 — Config Read for UI (SC1/CFG-03 Pre-fill)

**Current `GET /sensors` handler (Program.cs lines 224-235):**
```csharp
app.MapGet("/sensors", (HttpRequest req, IHaSensorRegistry registry,
    EntitiesConfig config, ArgusHealthSignals health) =>
{
    ...
    var html = EntityPickerPage.BuildFullPage(
        ip, registry, config, health, q,
        lastIncludePatterns, lastExcludePatterns);
    ...
});
```

Phase 3 changes: inject `ILiveEntitiesConfig` instead of `EntitiesConfig`, call `.Get()` to pass the current `EntitiesConfig` to `BuildFullPage`. The `lastIncludePatterns`/`lastExcludePatterns` in-memory holder persists across reloads (no change needed).

**EntitiesConfigLoader reads Detectors + Params:** `DeserializerBuilder` + `IgnoreUnmatchedProperties` reads `detectors: [{name: hst, params: {window: "200"}}]` into `EntityConfig.Detectors` as `List<DetectorConfig>` with `Name` and `Params` populated. The `_patterns:` block is ignored by `IgnoreUnmatchedProperties`. [VERIFIED: EntitiesConfigLoader.cs full read — uses `IgnoreUnmatchedProperties`]

**Pre-fill logic in HTML builder:** For each tracked entity in `EntitiesConfig.Entities`, render its `Detectors` list. For each `DetectorConfig`, use `d.Params.TryGetValue(key, defaultValue)` pattern to pre-fill inputs. This is the same pattern as `HstParams.From()`.

---

### Q8 — MqttPublisherWorker Migration

**Current problem:** `MqttPublisherWorker` captures `EntitiesConfig _entities` at ctor and calls `DiscoveryPublisher.PublishAllAsync(_mqtt, _entities.Entities, ct)` at startup. After a config reload that adds new entities, discovery for those new entities is never published — HA won't show them.

**Required behavior:** On config reload, `MqttPublisherWorker` must republish discovery for the new entity set (new entities get discovery; removed entities are retracted by `HaListenerWorker`).

**Migration options:**
1. Subscribe `MqttPublisherWorker` to `ConfigChanged` and republish `DiscoveryPublisher.PublishAllAsync` for the full new entity set. Idempotent — republishing existing entities does nothing harmful (retained, same payload). [ASSUMED — MQTT retain idempotency is standard behavior; `DiscoveryPublisher` docstring confirms "republish is safe"]
2. Alternatively, do both retraction and re-publish in `HaListenerWorker` and keep `MqttPublisherWorker` unchanged. This puts more responsibility in one place but requires injecting `DiscoveryPublisher`-style logic into the listener worker.

**Recommendation (for planner):** Option 1. `MqttPublisherWorker` already holds `MqttConnection` and has the right scope. Migrate it to `ILiveEntitiesConfig`, subscribe `ConfigChanged`, call `PublishAllAsync` with the new entity set. This is a small addition (5-10 lines) and keeps MQTT concerns in the MQTT worker.

[VERIFIED: MqttPublisherWorker.cs full read — lines 47, 53-55 show the current discovery + availability publish pattern]

---

### Q9 — Form Parsing for Indexed Detector Fields

**POST body shape (from 03-UI-SPEC.md):**
```
detectors[0][0][name]=hst
detectors[0][0][params][window]=250
detectors[0][0][params][n_trees]=25
...
detectors[0][1][name]=mad
detectors[0][1][params][threshold]=3.5
detectors[1][0][name]=stl
...
```

**ASP.NET Minimal API `IFormCollection` parsing:**

`IFormCollection` does NOT natively parse multi-level indexed brackets into a nested structure. The keys arrive as literal strings: `"detectors[0][0][name]"`, `"detectors[0][0][params][window]"`, etc. The save handler must parse these manually.

**Parsing algorithm:**
```csharp
// Regex to extract indices and leaf key:
// detectors[{entityIdx}][{detIdx}][name]          → entity/detector index + name
// detectors[{entityIdx}][{detIdx}][params][{key}] → entity/detector index + param key

// Group by entityIdx → detIdx → build DetectorConfig
var detectorMap = new Dictionary<(int,int), DetectorConfig>();
foreach (var (formKey, values) in form)
{
    var match = Regex.Match(formKey, @"^detectors\[(\d+)\]\[(\d+)\]\[(.+?)\](?:\[(.+)\])?$");
    if (!match.Success) continue;

    int ei = int.Parse(match.Groups[1].Value);
    int di = int.Parse(match.Groups[2].Value);
    string field = match.Groups[3].Value;   // "name" or "params"
    string? paramKey = match.Groups[4].Success ? match.Groups[4].Value : null;

    if (!detectorMap.TryGetValue((ei, di), out var dc))
        detectorMap[(ei, di)] = dc = new DetectorConfig();

    if (field == "name")
        dc.Name = values.FirstOrDefault() ?? "hst";
    else if (field == "params" && paramKey is not null)
        dc.Params[paramKey] = values.FirstOrDefault() ?? "";
}
```

Then correlate `entityIdx` with the ordered `selectedIds` list (same order as checkboxes in the form) to build `List<EntityConfig>`.

**Important:** `selectedIds` from `form["entities"]` gives the entity IDs in submission order. The entity index `{entity_idx}` in the detector fields maps positionally to this list. This correlation must be done carefully — the order of `form["entities"]` values must match the order in the HTML form (checkbox order = discovery order in the picker page).

**Alternatively (simpler):** The form's `entities[]` values arrive in DOM order. The HTML is server-rendered in a deterministic order (alphabetical by `EntityId` per Phase 2 `OrderBy(e => e.EntityId)`) — so `selectedIds[i]` matches `detectors[i][...]`. This works as long as untracked checkboxes (unchecked entities) don't emit `detectors[i][...]` fields — and they don't, because only tracked rows render disclosure sections.

[VERIFIED: EntityPickerPage.cs Phase 2 `BuildListRows` logic reviewed; 03-UI-SPEC.md form encoding section]

**Empty detector list handling:** If an entity's checkbox is submitted but no `detectors[i][j]` fields are submitted (user removed all detectors), the save handler must insert a default HST entry. [VERIFIED: CONTEXT.md "empty Params map means use defaults"; 03-UI-SPEC.md "save will use HST defaults"]

---

## Common Pitfalls

### Pitfall 1: Event Re-entrancy — Rapid Saves Trigger Multiple ConfigChanged
**What goes wrong:** User clicks Save twice quickly → two `Swap()` calls → two `ConfigChanged` events → `OnConfigChanged` cancels `innerCts` twice. Second cancellation is a no-op on an already-cancelled CTS, but if the first cancel has not yet restarted the loop, the second cancel fires before the new `innerCts` is assigned — `OnConfigChanged` sees `null` and does nothing.
**Why it happens:** The event handler captures `innerCts` by reference; between cancel and reassignment there is a brief null window.
**How to avoid:** Use `Volatile.Read` / `Interlocked.CompareExchange` on the `innerCts` reference, or simply use a `volatile` field. The null check `if (cts is not null)` already handles the race safely — the worst case is one extra Save is silently no-op'd for its reload trigger. The second Save DID write the config, so the first reload will pick it up via `ILiveEntitiesConfig.Get()`. Net effect: at most one reload is missed if saves are nanoseconds apart (not realistic for a single-user tool).
**Warning signs:** Banner shows success twice but pipeline only restarts once; harmless for this use case.

### Pitfall 2: ScoreStreamPipeline Still Reads Stale `_entitiesConfig` After Swap
**What goes wrong:** If `ScoreStreamPipeline` continues to inject `EntitiesConfig` by ctor (not `ILiveEntitiesConfig`), `BuildEntityStates()` always uses the initial config — reload has no effect on the streaming pipeline.
**Why it happens:** There are TWO `ScoreStreamPipeline` constructors (production + test). Both must be updated to accept `ILiveEntitiesConfig`. `BuildEntityStates()` must call `_liveConfig.Get().Entities`, not `_entitiesConfig.Entities`.
**How to avoid:** After migration, the test constructor also takes `ILiveEntitiesConfig` — tests wrap a static `EntitiesConfig` in a `LiveEntitiesConfig` for injection.
**Warning signs:** Reload succeeds (banner shows success, log shows restart) but the set of tracked entities hasn't changed.

### Pitfall 3: Inner CTS Disposal Race with ConfigChanged Handler
**What goes wrong:** `OnConfigChanged` fires after `finally { innerCts.Dispose(); innerCts = null; }` — accesses a disposed `CancellationTokenSource`.
**Why it happens:** The event handler references the captured `innerCts` variable; Dispose happens before reassignment.
**How to avoid:** Use a pattern that never calls `Cancel()` on a disposed CTS: set `innerCts = null` BEFORE `Dispose()`, check null in the handler.
**Warning signs:** `ObjectDisposedException` in the log from `CancellationTokenSource.Cancel`.

Revised safe pattern:
```csharp
finally
{
    var toDispose = innerCts;
    innerCts = null;  // null FIRST so handler sees null
    toDispose?.Dispose();
}
```

### Pitfall 4: MqttPublisherWorker Not Republishing Discovery for New Entities
**What goes wrong:** User adds a new entity, saves, pipeline restarts — but HA never shows the new `binary_sensor` / `sensor` because `MqttPublisherWorker` only published discovery at startup.
**Why it happens:** `MqttPublisherWorker` captures `EntitiesConfig` at startup and never re-runs `PublishAllAsync`.
**How to avoid:** Subscribe `MqttPublisherWorker` to `ConfigChanged`; on event, call `DiscoveryPublisher.PublishAllAsync(_mqtt, _liveConfig.Get().Entities, ct)`.
**Warning signs:** Pipeline restart succeeds (log shows it), MQTT state topics receive updates, but new HA entities don't appear.

### Pitfall 5: Indexed Form Field Entity-Detector Correlation
**What goes wrong:** `detectors[0][0][name]` is assumed to correspond to the first element of `form["entities"]` — but if checkboxes for untracked entities are also submitted, the indices diverge.
**Why it happens:** Untracked entities don't have disclosure sections, so no `detectors[i][...]` fields. But if entity index is based on position in the full `selectedIds` list (which only contains CHECKED entities), correlation is correct. If it's based on position in the full entity list (all rendered), it breaks.
**How to avoid:** The `entity_idx` must be the index of the entity within the tracked (checked) entities only. The server-rendered form must assign `entity_idx` as the position within the tracked set, not the overall list. [VERIFIED: 03-UI-SPEC.md: "`{entity_idx}` is the 0-based index of entities as ordered in the submitted checkbox list"]
**Warning signs:** Detector params are assigned to the wrong entity after save.

### Pitfall 6: Empty-Payload MQTT Retraction vs Payload-not-set
**What goes wrong:** Publishing `payload=""` (empty string) with retain=true is not the same as publishing a zero-byte payload in some MQTT broker implementations. MQTTnet 5's `WithPayload(Encoding.UTF8.GetBytes(""))` sends a zero-byte payload, which IS the standard delete-retained mechanism.
**How to avoid:** Use `string.Empty` with `MqttConnection.PublishAsync(topic, string.Empty, retain:true, ct)`. MQTTnet encodes this as a zero-byte payload. [ASSUMED — MQTT v3.1.1 spec §3.3.1-7: "A RETAIN flag set to 1 and a zero-length payload ... MUST remove any retained message" — this is standard; MQTTnet v5 implements this correctly]
**Warning signs:** Retracted entities reappear in HA after broker restart (retained message was not actually deleted).

### Pitfall 7: `EntitiesConfigLoader.Validate()` Rejects Empty Detectors on Reload
**What goes wrong:** If a user saves an entity with all detectors removed (empty detector list), `EntitiesConfigLoader.Validate()` throws: `"Entity '{EntityId}' has no detectors configured"` (line 59-61).
**Why it happens:** The save handler calls `EntitiesConfigLoader.Load()` for the `ILiveEntitiesConfig.Swap()` path — but if it does, the validation runs before the swap.
**How to avoid:** Option A: In the save handler, re-use `EntitiesConfigLoader.Load(path, logger)` after the write to get the new config for `Swap()` — the validator will catch this and throw, which becomes a Save error banner. Option B: The save handler defensively inserts a default HST entry for entities with empty detector lists before writing (per CONTEXT.md: "server will insert a default HST entry"). Option B is correct for Phase 3: always ensure at least one detector per entity before writing YAML. [VERIFIED: EntitiesConfigLoader.cs lines 59-61, CONTEXT.md "empty Params map means use defaults"]

### Pitfall 8: FileSystemWatcher — None Exists; No Interplay
**What goes wrong (hypothetical):** A file-system watcher fires on entities.yaml rename and triggers a reload concurrently with the save-endpoint-triggered reload.
**Actual state:** No `FileSystemWatcher` exists anywhere in the codebase. [VERIFIED: Grep of entire orchestrator codebase found only a comment in `ConfigWriter.cs` docs — no implementation]. The CONTEXT.md mentions "concurrent save and file-watcher event" as a Phase 4 concern (`CFG-05` / FileSystemWatcher debounce validation). For Phase 3, there is no file watcher — the ONLY reload trigger is `ILiveEntitiesConfig.Swap()` called from the save endpoint. No interplay pitfall exists in Phase 3.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Atomic config file write | Custom file write | `ConfigWriter.WriteAsync` (already exists) | SemaphoreSlim + temp-rename pattern; handles crash between write and rename |
| YAML serialization | String interpolation | `YamlDotNet SerializerBuilder` (already in use) | Escaping, multi-line, special chars — T-02-08 |
| Volatile reference swap | Custom locking | `volatile` + `Interlocked.Exchange` | .NET idiom; same as `HaSensorRegistry._snapshot` already in project |
| MQTT empty-payload retraction | Custom HA API call | `MqttConnection.PublishAsync(topic, "", retain:true)` | Standard MQTT retained-message deletion |
| Form indexed-field parsing | Custom XML-like parser | Regex on `IFormCollection` keys | Simple, already typed |

**Key insight:** The project's existing patterns (`ArgusHealthSignals` volatile fields, `HaSensorRegistry` volatile reference swap, `ConfigWriter` atomic write) provide exact precedents for every new pattern needed in Phase 3. No new patterns must be invented.

---

## Code Examples

### ILiveEntitiesConfig (new class)
```csharp
// Source: mirrors HaSensorRegistry volatile reference pattern [VERIFIED: HaSensorRegistry.cs]
public interface ILiveEntitiesConfig
{
    EntitiesConfig Get();
    void Swap(EntitiesConfig newConfig);
    event EventHandler? ConfigChanged;
}

public sealed class LiveEntitiesConfig : ILiveEntitiesConfig
{
    private volatile EntitiesConfig _current;

    public LiveEntitiesConfig(EntitiesConfig initial) => _current = initial;

    public EntitiesConfig Get() => _current;

    public void Swap(EntitiesConfig newConfig)
    {
        Interlocked.Exchange(ref _current, newConfig);
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ConfigChanged;
}
```

### MQTT retraction (empty retained payload)
```csharp
// Source: MqttConnection.PublishAsync signature [VERIFIED: MqttConnection.cs line 79]
// Topic formula [VERIFIED: DiscoveryPublisher.cs lines 134-144, UniqueId.cs]
await _mqtt.PublishAsync(
    $"homeassistant/binary_sensor/{UniqueId.AnomalyId(entityId, detector)}/config",
    string.Empty, retain: true, ct);
await _mqtt.PublishAsync(
    $"homeassistant/sensor/{UniqueId.ScoreId(entityId, detector)}/config",
    string.Empty, retain: true, ct);
```

### Indexed detector form parsing
```csharp
// Source: derived from ASP.NET IFormCollection flat-key behavior [ASSUMED]
// Pattern: detectors[entityIdx][detIdx][name|params][paramKey?]
var re = new Regex(@"^detectors\[(\d+)\]\[(\d+)\]\[(.+?)\](?:\[(.+?)\])?$",
    RegexOptions.Compiled);
var detectors = new Dictionary<(int, int), DetectorConfig>();

foreach (var key in form.Keys)
{
    var m = re.Match(key);
    if (!m.Success) continue;
    var k = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    if (!detectors.TryGetValue(k, out var dc)) detectors[k] = dc = new();
    if (m.Groups[3].Value == "name")
        dc.Name = form[key].FirstOrDefault() ?? "hst";
    else if (m.Groups[3].Value == "params" && m.Groups[4].Success)
        dc.Params[m.Groups[4].Value] = form[key].FirstOrDefault() ?? "";
}
// Build per-entity detector lists: group by entityIdx, then by detIdx
```

### HaListenerWorker inner-CTS loop (skeleton)
```csharp
// Source: designed for this phase; inner-CTS pattern [ASSUMED — standard .NET pattern]
CancellationTokenSource? innerCts = null;
void OnChanged(object? _, EventArgs __) { innerCts?.Cancel(); }
_liveConfig.ConfigChanged += OnChanged;
try
{
    while (!stoppingToken.IsCancellationRequested)
    {
        var toDispose = innerCts;
        innerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        toDispose?.Dispose();
        try
        {
            await _scoreStreamPipeline.RunAsync(
                _haEventSource.ReadAllAsync(innerCts.Token), innerCts.Token);
        }
        catch (OperationCanceledException)
            when (innerCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        { /* reload — re-loop */ }
    }
}
finally { _liveConfig.ConfigChanged -= OnChanged; innerCts?.Dispose(); }
```

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `AddSingleton(entitiesConfig)` (raw object) | `AddSingleton<ILiveEntitiesConfig>(liveConfig)` | Phase 3 | All consumers can read the live reference without restart |
| `EntitiesConfig` ctor-captured in workers | Per-cycle `_liveConfig.Get()` | Phase 3 | Hot reload for batch workers without restart |
| `HaListenerWorker` one-shot `RunAsync` | Inner-CTS restart loop | Phase 3 | Pipeline restarts on config change, not on host restart |
| `MqttPublisherWorker` startup-only discovery publish | Startup + ConfigChanged re-publish | Phase 3 | New entities get HA discovery after reload |

---

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `volatile` reference field provides sufficient memory barrier for single-writer/many-reader pattern on .NET 8 | Q1 ILiveEntitiesConfig | Low risk — same pattern used in HaSensorRegistry with no observed issues; worst case add `Interlocked.Exchange` for all reads too |
| A2 | `CancellationTokenSource.Cancel()` is thread-safe | Q2 HaListenerWorker loop | Low risk — documented in .NET; standard pattern |
| A3 | `OperationCanceledException` when `stoppingToken.IsCancellationRequested` is NOT caught by the `when` guard and propagates correctly as BackgroundService shutdown | Q2 | Low risk — standard BackgroundService termination contract |
| A4 | MQTTnet 5 sends a zero-byte payload for `string.Empty` input to `PublishAsync`, which triggers MQTT retained-message deletion | Q5 MQTT retraction | Medium risk — if MQTTnet encodes empty string differently, retraction fails silently; verify with a broker trace if SC4 doesn't work |
| A5 | ASP.NET `IFormCollection` delivers indexed bracket keys as flat strings (e.g. `"detectors[0][0][name]"`) | Q9 form parsing | Low risk — standard ASP.NET behavior; can verify with a request trace |
| A6 | `DiscoveryPublisher.PublishAllAsync` is idempotent (re-publishing existing entities has no harmful effect) | Pitfall 4 / MqttPublisherWorker | Low risk — retained=true with same payload is no-op at broker level |

---

## Open Questions

1. **MqttPublisherWorker ConfigChanged subscription: CancellationToken for the republish call**
   - What we know: `MqttPublisherWorker.ExecuteAsync` runs until `stoppingToken`. After the initial publish it blocks on `Task.Delay(Timeout.Infinite, stoppingToken)`. A `ConfigChanged` handler needs a CT for the republish.
   - What's unclear: Should the handler use `stoppingToken` (correct lifetime) or a short-lived CT? Can the handler be a proper async subscriber?
   - Recommendation: Store `stoppingToken` as a field; use it in the `ConfigChanged` handler with `Task.Run(() => DiscoveryPublisher.PublishAllAsync(..., _stoppingToken))`. Fire-and-forget with logged errors is acceptable for a single-user tool.

2. **Per-entity availability republish after reload**
   - What we know: On startup, `MqttPublisherWorker` publishes `online` availability for each entity. On reload, new entities have no availability message yet — HA considers them unavailable.
   - What's unclear: Does `ScoreStreamPipeline.RunAsync` publish `online` availability when it first starts reading for an entity?
   - Recommendation: Add an availability `online` publish in the `HaListenerWorker` reload path for each entity in the new config, or in `MqttPublisherWorker`'s `ConfigChanged` handler alongside re-discovery. Planner should decide; it is a small addition.

3. **Test constructor impact for ScoreStreamPipeline and BatchSchedulerWorker**
   - What we know: Both classes have test constructors that take `EntitiesConfig` directly. 161 existing `[Fact]` tests use these.
   - What's unclear: Whether to add `ILiveEntitiesConfig` overloads alongside the existing `EntitiesConfig` test constructors, or replace them.
   - Recommendation: Replace — wrap `EntitiesConfig` in `new LiveEntitiesConfig(cfg)` in test setup. This keeps the interface clean and tests the live-config path. Update `BatchSchedulerWorkerTests` and `ScoreStreamPipelineTests` accordingly.

---

## Environment Availability

Step 2.6: SKIPPED — Phase 3 is purely code changes within the existing orchestrator process. No new external services, CLIs, or runtimes are required. All dependencies (MQTTnet, YamlDotNet, MQTTnet) are already in the project.

---

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (existing — `Argus.Orchestrator.Tests`) |
| Config file | none — project reference in test `.csproj` |
| Quick run command | `dotnet test orchestrator/Argus.Orchestrator.Tests` |
| Full suite command | `dotnet test orchestrator/Argus.Orchestrator.Tests` |

### Phase Requirements → Test Map

| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| UI-03 | Detector disclosure rows render in HTML output | unit | `dotnet test --filter EntityPickerPageTests` | ✅ (extend existing) |
| UI-03 | `GET /api/detectors/new-entry` returns valid HTML fragment with HST defaults | unit | `dotnet test --filter DetectorEntryEndpointTests` | ❌ Wave 0 |
| CFG-03 | Indexed detector form fields parsed into EntityConfig.Detectors list | unit | `dotnet test --filter SaveEndpointDetectorParsingTests` | ❌ Wave 0 |
| CFG-03 | Empty detector list defaults to hst | unit | `dotnet test --filter SaveEndpointDetectorParsingTests` | ❌ Wave 0 |
| CFG-03 | YAML round-trip: DetectorConfig {Name, Params} survives serialize/deserialize | unit | `dotnet test --filter EntitiesConfigTests` | ✅ (extend existing) |
| CFG-04 | ILiveEntitiesConfig.Swap fires ConfigChanged after exchange | unit | `dotnet test --filter LiveEntitiesConfigTests` | ❌ Wave 0 |
| CFG-04 | HaListenerWorker restarts RunAsync on ConfigChanged (inner CTS cancel) | unit | `dotnet test --filter HaListenerWorkerReloadTests` | ❌ Wave 0 |
| CFG-04 | BatchSchedulerWorker.RunBatchAsync reads updated entity list after Swap | unit | `dotnet test --filter BatchSchedulerWorkerTests` | ✅ (extend existing) |
| CFG-04 | MQTT retraction publishes empty retained payload for removed entities | unit | `dotnet test --filter MqttRetractionTests` | ❌ Wave 0 |

### Sampling Rate
- **Per task commit:** `dotnet test orchestrator/Argus.Orchestrator.Tests`
- **Per wave merge:** `dotnet test orchestrator/Argus.Orchestrator.Tests`
- **Phase gate:** Full suite green before `/gsd-verify-work`

### Wave 0 Gaps
- [ ] `DetectorEntryEndpointTests.cs` — covers GET /api/detectors/new-entry fragment
- [ ] `SaveEndpointDetectorParsingTests.cs` — covers indexed detector form field parsing + empty-list default
- [ ] `LiveEntitiesConfigTests.cs` — covers ILiveEntitiesConfig Swap + ConfigChanged thread-safety
- [ ] `HaListenerWorkerReloadTests.cs` — covers inner-CTS restart loop (mocked ScoreStreamPipeline)
- [ ] `MqttRetractionTests.cs` — covers diff + empty retained publish

---

## Security Domain

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Phase 4 (validate_session deferred) |
| V3 Session Management | no | Ingress proxy handles sessions |
| V4 Access Control | yes (already) | `IsAuthorizedRequest` (RemoteIpAddress) — unchanged from Phase 2 |
| V5 Input Validation | yes | `WebUtility.HtmlEncode` on all user strings (T-02-07 inheritance); Phase 4 full validation |
| V6 Cryptography | no | gRPC mTLS unchanged |

### Known Threat Patterns for Phase 3

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Detector param injection via form fields | Tampering | `WebUtility.HtmlEncode` on param values before HTML render; values written to YAML via YamlDotNet serializer (no string format) |
| Config overwrite race (concurrent saves) | Tampering | `ConfigWriter.SemaphoreSlim(1,1)` serializes writes (unchanged) |
| Detector name injection (arbitrary string as detector name) | Tampering | Phase 4 validation; Phase 3 accepts any string — Python rejects unknown detector names gracefully |
| MQTT topic injection via entity_id | Tampering | `UniqueId.Slug()` replaces `.` with `_`; entity_id comes from the server-controlled entity registry, not from the form directly |

---

## Sources

### Primary (HIGH confidence)
- `orchestrator/Argus.Orchestrator/Program.cs` — DI registrations, consumer list, save endpoint Phase 2
- `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs` — ctor signature, `_entitiesConfig` capture, `BuildEntityStates()` implementation
- `orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs` — one-shot `RunAsync` call confirmed
- `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` — `_entities` ctor-captured, both usage sites confirmed
- `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs` — topic formula, `GetDetectorName()`, `PublishAllAsync` signature
- `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs` — `PublishAsync(topic, payload, retain, ct)` signature
- `orchestrator/Argus.Orchestrator/Mqtt/UniqueId.cs` — slug/anomalyId/scoreId formulas
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs` — `DetectorConfig`, `HstParams` defaults
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` — Validate() throws on empty detectors; IgnoreUnmatchedProperties
- `orchestrator/Argus.Orchestrator/Config/ConfigWriter.cs` — SemaphoreSlim(1,1), temp-rename pattern
- `orchestrator/Argus.Orchestrator/Ha/HaSensorRegistry.cs` — volatile IReadOnlyList reference swap precedent
- `orchestrator/Argus.Orchestrator/Health/ArgusHealthSignals.cs` — volatile bool precedent
- `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs` — startup-only discovery publish confirmed
- `.planning/phases/03-config-readwrite-detector-assignment-reload/03-CONTEXT.md` — all locked decisions
- `.planning/phases/03-config-readwrite-detector-assignment-reload/03-UI-SPEC.md` — form encoding, component classes, htmx contract

### Secondary (MEDIUM confidence)
- `.planning/phases/02-live-sensor-discovery-entity-selection-ui/02-03-SUMMARY.md` — Phase 2 delivered endpoints, test count baseline (160 tests → now 161 [Fact]s by grep)
- `.planning/STATE.md` — architectural decisions locked for v3.0

### Tertiary (LOW confidence — not needed, all answers found in codebase)
- None

---

## Metadata

**Confidence breakdown:**
- ILiveEntitiesConfig design: HIGH — exact precedent in HaSensorRegistry
- HaListenerWorker restart loop: HIGH — pattern derived from existing BackgroundService code
- MQTT retraction topics: HIGH — exact formula in DiscoveryPublisher + UniqueId verified
- Form parsing for indexed fields: MEDIUM — ASP.NET flat-key behavior is standard but not tested yet
- Empty payload retraction: MEDIUM — MQTT spec behavior assumed correct for MQTTnet 5

**Research date:** 2026-07-01
**Valid until:** 2026-08-01 (stable stack; no fast-moving dependencies)
