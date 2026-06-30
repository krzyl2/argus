# Architecture Research

**Domain:** Ingress Configuration UI — v3.0 integration into existing .NET 8 Generic Host add-on
**Researched:** 2026-06-30
**Confidence:** HIGH (all findings derived from reading actual source files; no speculative gaps)

---

## v2.0 Baseline (Shipped, 2026-06-30)

```
HA Supervisor
    │  SUPERVISOR_TOKEN
    ▼
[cont-init.d/10-config-gen.sh]     s6 oneshot — runs before any service
    │  reads /data/options.json
    │  writes /var/run/s6/container_environment/ARGUS_*
    │  writes /data/entities.yaml (via gen-entities.py — all entities get hst, params:{})
    ▼
s6 services.d  (started in parallel)
    ├── detector/run   (Python gRPC — local mode only; s6 down-file disables in remote mode)
    └── orchestrator/run   (.NET 8)
            Host.CreateApplicationBuilder(args)               ← SDK: Microsoft.NET.Sdk.Worker
            EntitiesConfigLoader.Load(entitiesPath)           ← one-shot at startup, result is singleton
            ┌──────────────────────────────────────────┐
            │  BackgroundServices (all run concurrently)│
            │   HaListenerWorker                        │
            │     NetDaemonHaEventSource                │
            │       HaWebSocketClient                   │
            │         ws://supervisor/core/websocket    │
            │         get_states() on every connect     │
            │         SelectDiscoverableSensors() (log) │
            │   ScoreStreamPipeline                     │
            │   MqttPublisherWorker                     │
            │   HealthPublisherWorker                   │
            │   BatchSchedulerWorker (if InfluxURL set) │
            └──────────────────────────────────────────┘
```

**Critical structural facts confirmed in source code:**

1. **SDK is `Microsoft.NET.Sdk.Worker`** (not Web). Adding Kestrel/ASP.NET Minimal API requires changing the SDK to `Microsoft.NET.Sdk.Web` and updating `Program.cs` to use `WebApplication.CreateBuilder`. All existing BackgroundService registrations are compatible with WebApplication — no behavior change to running services.

2. **`EntitiesConfig` is a DI singleton** resolved once at startup via `EntitiesConfigLoader.Load()` and registered with `builder.Services.AddSingleton(entitiesConfig)`. Three consumers hold direct constructor-injected references: `NetDaemonHaEventSource`, `ScoreStreamPipeline`, and (indirectly) `BatchSchedulerWorker`.

3. **`NetDaemonHaEventSource` pre-builds `_configuredEntities`** — a `HashSet<string>` — in its constructor from the singleton config. This set is **never updated** by any existing mechanism. On reload, this must change.

4. **`ScoreStreamPipeline.BuildEntityStates()`** reads `_entitiesConfig.Entities` at `RunAsync` call time (not at construction), but `HaListenerWorker` calls `RunAsync` once and then awaits it indefinitely. The entity state dictionary is built exactly once per process lifetime.

5. **`HaWebSocketClient.GetStatesAsync`** is a general snapshot of all HA states — not filtered by configured entities. It already contains all data needed for the sensor discovery API. The method is a local variable inside `NetDaemonHaEventSource.RunConnectionLoopAsync`; it is not exposed as a service.

6. **`SelectDiscoverableSensors`** is already a `public static` method — callable from anywhere given a state snapshot and a `HashSet<string>` of already-configured entities.

7. **The HA Supervisor token** (`SUPERVISOR_TOKEN`, available as `ARGUS_HA_TOKEN` env var) is the same credential used for everything: HA WebSocket auth, MQTT service discovery, and — for v3 — Ingress session validation. No separate token is needed.

8. **`include_patterns`/`exclude_patterns`** are in `config.yaml` options schema (and thus in `/data/options.json`), but `gen-entities.py` and `EntitiesConfigLoader` both ignore them entirely. They never affect entity selection. This is the "v2.0 patterns-ignored gap" that CFG-02 closes.

9. **`config.yaml` has no `ingress:` key** — it must be added for v3.

10. **`gen-entities.py` hardcodes `hst`** with `params: {}` for every entity. The `EntitiesConfig` model already supports multiple detectors per entity with arbitrary params — the generator just does not use this capability.

---

## v3.0 Target Architecture

```
HA Frontend
    │  Ingress proxy  (HA authenticates the user session)
    │  X-Ingress-Token header injected
    ▼
Kestrel  (binds on ingress_port in config.yaml)
    │
    ├── GET  /           → wwwroot/index.html  (static file middleware)
    ├── GET  /api/sensors → SensorsApiEndpoints
    ├── GET  /api/config  → ConfigApiEndpoints (read current entities.yaml)
    └── POST /api/config  → ConfigApiEndpoints (validate + write + reload)
    │
    ▼
┌──────────────────────────────────────────────────────────────────┐
│  Argus.Orchestrator  (single process — WebApplication)           │
│                                                                  │
│  ILiveEntitiesConfig  (new singleton — atomic-swappable ref)     │
│      ↑ written by ConfigApiEndpoints on POST /api/config         │
│      ↑ watched by FileSystemWatcher on /data/entities.yaml       │
│                                                                  │
│  IHaSensorRegistry   (new singleton — last-known get_states)     │
│      ↑ updated by NetDaemonHaEventSource after each GetStates()  │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  BackgroundServices (unchanged execution, new config ref) │   │
│  │   HaListenerWorker   ←  ILiveEntitiesConfig              │   │
│  │     NetDaemonHaEventSource  ←  ILiveEntitiesConfig       │   │
│  │       HaWebSocketClient (existing, no change)            │   │
│  │       → pushes snapshot to IHaSensorRegistry             │   │
│  │   ScoreStreamPipeline  ←  ILiveEntitiesConfig            │   │
│  │   MqttPublisherWorker  (no change)                       │   │
│  │   HealthPublisherWorker  (no change)                     │   │
│  │   BatchSchedulerWorker  (no change or ILiveEntitiesConfig)│  │
│  └──────────────────────────────────────────────────────────┘   │
│                                                                  │
│  /data/entities.yaml  ← single source of truth                   │
└──────────────────────────────────────────────────────────────────┘
```

---

## Architectural Decision Records

### ADR-1: Web Server Hosting — Co-host in Orchestrator Process

**Decision:** Extend the existing orchestrator process with ASP.NET Minimal API via `WebApplication.CreateBuilder`. Do not create a separate s6 service.

**Reasoning:**
- The UI's `/api/sensors` route needs the live `get_states` snapshot that is produced by the already-running `HaWebSocketClient` inside `NetDaemonHaEventSource`. A separate s6 process would need a second WS connection to the Supervisor proxy, duplicating code and creating a second authentication surface.
- The reload path (POST /api/config → signal running pipeline) is a within-process call on a shared singleton. Cross-process signaling (Unix socket, file lock, SIGUSER) is unnecessary complexity.
- `ingress_port` in `config.yaml` must be bound by exactly one listener. Co-hosting means Kestrel is that listener; there is no port conflict.
- SDK migration from `Microsoft.NET.Sdk.Worker` to `Microsoft.NET.Sdk.Web` is the only project-file change required. All existing `AddSingleton` / `AddHostedService` registrations are identical under `WebApplication`; this is a documented and supported migration path.
- The Docker base image changes from `mcr.microsoft.com/dotnet/runtime:8.0-jammy-chiseled` to `mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled` — same distroless base, adds Kestrel (~10 MB). Image size impact is negligible.

**Rejected alternative:** Separate s6 service (Python Flask or second .NET process). Reasons: doubled WS connection, cross-process reload IPC, second Bearer-token consumer, no benefit over co-hosting.

### ADR-2: Configuration Source of Truth — Keep `/data/entities.yaml`

**Decision:** `/data/entities.yaml` is and remains the single config source of truth. The UI writes this same YAML; the orchestrator reads it. No new config format is introduced.

**Reasoning:**
- `EntitiesConfig` / `EntityConfig` / `DetectorConfig` already model everything v3 needs: multiple detectors per entity, named string params. The schema is richer than `gen-entities.py` currently exploits.
- `EntitiesConfigLoader` already handles deserialization, validation (`entity_id` non-empty, at least one detector per entity), and structured logging. The UI's POST handler can call `EntitiesConfigLoader.Validate()` on the in-memory object before writing to disk — no new validation infrastructure needed.
- The first-boot path is unchanged: `10-config-gen.sh` runs `gen-entities.py`, which writes `/data/entities.yaml` from `options.json`. On subsequent UI saves, the UI writes the file directly. `gen-entities.py` only runs at container start (s6 cont-init.d), not on UI save.
- `include_patterns` / `exclude_patterns` (CFG-02) are a UI-level filter concern: they determine which sensors appear selectable in the picker UI, but the resulting *selected* entities are what ends up in `entities.yaml`. These patterns need not appear in `entities.yaml` itself.

**One schema concern to address:** `EntitiesConfigLoader.Validate()` throws if `entities` is empty. When the UI first opens (before the user has configured anything), `entities.yaml` may contain an empty list (from a default options.json with no entities). The orchestrator's current behavior is to throw at startup. For v3, the loader must tolerate an empty-entities file with a warning rather than a fatal throw; the streaming pipeline simply runs with no entities, which is a valid state.

**Rejected alternative:** Separate UI-owned JSON config with a converter layer. Reasons: schema duplication, converter synchronization risk, no benefit over the existing YAML.

### ADR-3: Reload Without Restart — `ILiveEntitiesConfig` Atomic Swap + Stream Loop Restart

**Decision:** Introduce `ILiveEntitiesConfig` as a reloadable wrapper. On save, swap the internal reference atomically. Signal `HaListenerWorker` to restart its `ScoreStreamPipeline.RunAsync` loop (which calls `BuildEntityStates()` on the new config).

**Reasoning and mechanism:**

The core challenge is that `EntitiesConfig` is currently a frozen singleton and `NetDaemonHaEventSource._configuredEntities` is a constructor-built `HashSet<string>`. Neither is reload-aware.

The solution uses two complementary changes:

**Change 1 — `ILiveEntitiesConfig`:**
```
ILiveEntitiesConfig
    volatile EntitiesConfig _current   // read by pipeline workers
    void Reload(EntitiesConfig next)   // Interlocked.Exchange swap
    event Action? ConfigChanged        // fired after swap
```

All three current consumers (`NetDaemonHaEventSource`, `ScoreStreamPipeline`, `BatchSchedulerWorker`) receive `ILiveEntitiesConfig` instead of `EntitiesConfig`. Reads call `.Get()` to access the current config. This is a safe pattern because `EntitiesConfig` is immutable after construction (its lists are built once by the deserializer).

**Change 2 — Stream pipeline restart on reload:**

`ScoreStreamPipeline.RunAsync` opens one bidi gRPC stream per entity and runs indefinitely. It cannot incrementally add/remove entity streams; `BuildEntityStates()` is called once. The simplest correct approach: `ILiveEntitiesConfig` fires `ConfigChanged`; `HaListenerWorker` catches this by cancelling an inner `CancellationTokenSource` (not the host-level `stoppingToken`), causing the `RunAsync` to exit cleanly; `HaListenerWorker` then loops and calls `_gateway.WaitForHealthyAsync` + `_scoreStreamPipeline.RunAsync` again. On the new call, `BuildEntityStates()` reads from the already-swapped `ILiveEntitiesConfig` — new entity set is live. The streaming gap is < 1 second.

`NetDaemonHaEventSource._configuredEntities` must change to be rebuilt on each `GetStatesAsync` call rather than in the constructor. Since `HaListenerWorker` restarts the inner loop anyway, `NetDaemonHaEventSource` will be called again; reading from `ILiveEntitiesConfig.Get()` at the start of `RunConnectionLoopAsync` rebuilds the set.

**File watch:** `FileSystemWatcher` on `/data/entities.yaml` with `NotifyFilter.LastWrite`, 500ms debounce. Handles external edits (user edits via HA file editor). POST /api/config also calls `ILiveEntitiesConfig.Reload()` directly after writing the file — no need to wait for the watcher.

**What the reload gap looks like to HA:** MQTT stays connected. No LWT fires. Existing entity scores/flags go briefly unavailable (per-entity availability goes offline) for < 1 second while the new stream is opened. New entities start publishing after warm-up (HST window). Removed entities stop publishing but their MQTT topics persist (no explicit delete); HA will eventually mark them unavailable.

**Rejected alternative — host restart (`IHostApplicationLifetime.StopApplication()`):** s6 restarts the process. MQTT connection tears down, LWT fires, entities go `unavailable` (not just temporarily offline), model state is not lost (disk-persisted). Violates CFG-04 "without restarting the add-on" since user sees entities go unavailable for 5-10 seconds.

**Rejected alternative — full in-place pipeline reconfigure without restart:** Would require tracking stream handles per entity, cancelling removed ones, starting new ones, all while the fan-out task is running. Significantly more complex than a < 1-second loop restart with identical operational result.

### ADR-4: Live HA Sensor Discovery — `IHaSensorRegistry` Snapshot

**Decision:** `NetDaemonHaEventSource` pushes the `get_states` result to a singleton `IHaSensorRegistry` after each connect. `GET /api/sensors` reads from the registry — no new WS connection opened per request.

**Reasoning:**
- The HA WS connection is already owned by `NetDaemonHaEventSource`. Opening a second `HaWebSocketClient` per API request would hit the Supervisor proxy on every UI page load, wasting a connection and risking rate-limiting.
- `get_states` is called on every connect (first connect + every reconnect). The registry snapshot is at most `BackoffMaxSeconds` (60s) stale — acceptable for a configuration UI.
- `IHaSensorRegistry` is a `ConcurrentDictionary<string, double>` updated atomically after each `GetStatesAsync`. The API handler reads it without blocking or joining the event source.
- `SelectDiscoverableSensors` is already a static public method taking `(IEnumerable<(string, string?)>, HashSet<string>)`. The API handler calls it directly with the registry snapshot and the current `ILiveEntitiesConfig` entity set.

**Ingress authentication:** HA Ingress injects `X-Ingress-Token` on every proxied request. The middleware validates this by calling `http://supervisor/core/api/ingress/validate_session` (Supervisor internal API, authenticated with `SUPERVISOR_TOKEN`). This is the documented pattern for add-on Ingress auth. No custom user auth is required — HA handles user sessions.

---

## Component Inventory

### New Components

| Component | Path | Purpose |
|-----------|------|---------|
| `ILiveEntitiesConfig` | `Config/ILiveEntitiesConfig.cs` | Interface: `EntitiesConfig Get()`, `void Reload(EntitiesConfig)`, `event Action? ConfigChanged` |
| `LiveEntitiesConfig` | `Config/LiveEntitiesConfig.cs` | Singleton implementation; `volatile` reference; `FileSystemWatcher`; `Interlocked.Exchange` swap |
| `IHaSensorRegistry` | `Ha/IHaSensorRegistry.cs` | Interface: `IReadOnlyDictionary<string, double> GetSnapshot()`, `void Update(IReadOnlyList<HaStateDto>)` |
| `HaSensorRegistry` | `Ha/HaSensorRegistry.cs` | `ConcurrentDictionary<string, double>` updated by `NetDaemonHaEventSource` |
| `ConfigApiEndpoints` | `Api/ConfigApiEndpoints.cs` | `GET /api/config` (read current), `POST /api/config` (validate + write + reload) |
| `SensorsApiEndpoints` | `Api/SensorsApiEndpoints.cs` | `GET /api/sensors` (registry snapshot + SelectDiscoverableSensors) |
| `IngressAuthMiddleware` | `Api/IngressAuthMiddleware.cs` | Validates `X-Ingress-Token` via Supervisor `/ingress/validate_session`; applied to all `/api/*` routes |
| `wwwroot/` | `wwwroot/` | UI static assets: `index.html`, bundled JS/CSS; served by `app.UseStaticFiles()` |

### Modified Components

| Component | File | Change |
|-----------|------|--------|
| Project SDK | `Argus.Orchestrator.csproj` | `Microsoft.NET.Sdk.Worker` → `Microsoft.NET.Sdk.Web` |
| Host builder | `Program.cs` | `Host.CreateApplicationBuilder` → `WebApplication.CreateBuilder`; register new services; map routes; `app.Run()` |
| DI: EntitiesConfig | `Program.cs` | Register `ILiveEntitiesConfig` / `LiveEntitiesConfig` as singleton; remove bare `EntitiesConfig` singleton |
| `NetDaemonHaEventSource` | `Ha/NetDaemonHaEventSource.cs` | Inject `ILiveEntitiesConfig` instead of `EntitiesConfig`; rebuild `_configuredEntities` from live config at each connect (not constructor); call `IHaSensorRegistry.Update()` after each `GetStatesAsync` |
| `ScoreStreamPipeline` | `Detection/ScoreStreamPipeline.cs` | Inject `ILiveEntitiesConfig` instead of `EntitiesConfig`; `BuildEntityStates()` calls `ILiveEntitiesConfig.Get()` |
| `HaListenerWorker` | `Workers/HaListenerWorker.cs` | Add inner `CancellationTokenSource` reset on `ILiveEntitiesConfig.ConfigChanged`; loop to restart `RunAsync` without stopping the host |
| `EntitiesConfigLoader` | `Config/EntitiesConfigLoader.cs` | Relax empty-entities validation from `throw` to `LogWarning`; return empty `EntitiesConfig` as valid (not fatal) |
| `BatchSchedulerWorker` | `Batch/BatchSchedulerWorker.cs` | Inject `ILiveEntitiesConfig`; read `.Get().Entities` at each batch cycle (currently reads the singleton once at construction — verify) |
| `argus/config.yaml` | `argus/config.yaml` | Add `ingress: true`, `ingress_port: 8099` (or chosen port), `panel_icon`, `panel_title` |
| `10-config-gen.sh` | `argus/rootfs/etc/cont-init.d/10-config-gen.sh` | Write `ASPNETCORE_URLS=http://0.0.0.0:{ingress_port}` to s6 environment |
| Docker base image | `argus/Dockerfile` | `mcr.microsoft.com/dotnet/runtime:8.0-jammy-chiseled` → `mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled` |

### Unchanged Components

`HaWebSocketClient`, `MqttConnection`, `StatePublisher`, `MqttPublisherWorker`, `HealthPublisherWorker`, `DiscoveryPublisher`, `DetectionGateway`, `DetectorChannelFactory`, `InfluxDbReader`, `ReconnectCooldown`, `ArgusHealthSignals`, `FrozenSensorDetector`, `HysteresisGate`, `SupervisorMqttCredentialSource`, `UniqueId`, `FriendlyName`, `EntitiesConfig` / `EntityConfig` / `DetectorConfig` / `HstParams` (data model — no schema change), `gen-entities.py` (still runs at boot for first-time setup), all Python detector code.

---

## Data Flows

### Config Save and Reload (POST /api/config)

```
Browser  →  POST /api/config  { entities:[...], detectors:{...} }
    ↓
IngressAuthMiddleware
    validates X-Ingress-Token via http://supervisor/core/api/ingress/validate_session
    ↓
ConfigApiEndpoints.PostConfig
    1. deserialize JSON payload → build EntitiesConfig in-memory
    2. call EntitiesConfigLoader.Validate(newConfig)  (reuse existing validator)
    3. serialize to YAML via YamlDotNet (same lib already in project)
    4. atomic write:
         File.WriteAllText(tmpPath, yaml)
         File.Move(tmpPath, "/data/entities.yaml", overwrite: true)
    5. ILiveEntitiesConfig.Reload(newConfig)
    ↓
LiveEntitiesConfig.Reload(newConfig)
    Interlocked.Exchange(ref _current, newConfig)
    ConfigChanged?.Invoke()
    ↓
HaListenerWorker  (subscribed to ConfigChanged)
    cancels inner CancellationTokenSource
    ↓
ScoreStreamPipeline.RunAsync  exits cleanly (cancelled)
    ↓
HaListenerWorker.loop
    WaitForHealthyAsync  (~0ms — detector already SERVING)
    ScoreStreamPipeline.RunAsync  (re-enters; calls BuildEntityStates())
    BuildEntityStates()  →  ILiveEntitiesConfig.Get().Entities  (new config)
    ↓
NetDaemonHaEventSource  (on next connect, rebuilds _configuredEntities from ILiveEntitiesConfig)
    new entity set active — no container restart
```

### Sensor Discovery (GET /api/sensors)

```
Browser  →  GET /api/sensors
    ↓
IngressAuthMiddleware
    ↓
SensorsApiEndpoints.GetSensors
    snapshot  = IHaSensorRegistry.GetSnapshot()       // last get_states, all entities
    tracked   = ILiveEntitiesConfig.Get().Entities    // currently configured entity_ids
    discoverable = SelectDiscoverableSensors(
        snapshot.Select(kv => (kv.Key, kv.Value.ToString())),
        new HashSet<string>(tracked.Select(e => e.EntityId))
    )
    returns JSON: { all: [...], tracked: [...], discoverable: [...] }
    ↓
Browser renders entity picker; user selects entities; assigns detectors
```

### Registry Update (on each HA connect)

```
NetDaemonHaEventSource.RunConnectionLoopAsync
    client.ConnectAndAuthAsync(...)
    states = client.GetStatesAsync(ct)      ← existing call, unchanged
    IHaSensorRegistry.Update(states)        ← NEW: push snapshot to registry
    LogDiscoverableSensors(states)          ← existing (unchanged)
    ... rest of existing logic unchanged
```

### First-Boot (no change to existing path)

```
cont-init.d/10-config-gen.sh
    reads /data/options.json  (entities list from HA options UI)
    gen-entities.py → /data/entities.yaml  (hst defaults; single detector per entity)
    ↓
LiveEntitiesConfig initializes from /data/entities.yaml at startup
    (reads via EntitiesConfigLoader.Load — unchanged)
    ↓
Orchestrator starts, streams with whatever entities options.json declared
```

---

## Suggested Build Order (Phase Sequencing)

Dependency rule: each phase delivers a shippable, testable increment. No phase assumes work from a later phase.

### Phase 1 — Ingress Scaffold + SDK Migration (UI-01, CFG-01 partial)

**Goal:** "Open Web UI" button appears in HA; clicking it loads a placeholder page through Ingress. Prove SDK migration does not break existing BackgroundServices.

Steps:
1. Change `Argus.Orchestrator.csproj` SDK to `Microsoft.NET.Sdk.Web`.
2. Migrate `Program.cs`: `Host.CreateApplicationBuilder` → `WebApplication.CreateBuilder`; keep all existing registrations; add `app.UseStaticFiles()`; add `app.Run()`.
3. Add `ingress: true`, `ingress_port: 8099`, `panel_icon`, `panel_title` to `argus/config.yaml`.
4. Update `10-config-gen.sh` to write `ASPNETCORE_URLS`.
5. Update Dockerfile base image to `aspnet:8.0-jammy-chiseled`.
6. Add `wwwroot/index.html` (placeholder: "Argus Configuration UI — coming soon").
7. Add `IngressAuthMiddleware` skeleton (pass-through for now; log the token for debugging).
8. Integration smoke test: install add-on, click "Open Web UI", see placeholder page, verify all v2 functionality (streaming, MQTT, health) still works.

**Dependencies:** None — this is the foundation.

### Phase 2 — Live Sensor Discovery API (UI-02, CFG-02)

**Goal:** `/api/sensors` returns the live HA entity list; UI renders a filterable entity picker.

Steps:
1. Add `IHaSensorRegistry` + `HaSensorRegistry` (singleton).
2. Modify `NetDaemonHaEventSource` to call `IHaSensorRegistry.Update(states)` after each `GetStatesAsync`.
3. Implement `SensorsApiEndpoints` (`GET /api/sensors`).
4. Implement `include_patterns`/`exclude_patterns` filtering in `SelectDiscoverableSensors` (this closes the v2.0 gap — CFG-02; the patterns are available in the add-on options but currently ignored).
5. Build entity picker UI component (list, search/filter, multi-select).
6. Complete `IngressAuthMiddleware` (call Supervisor validate_session endpoint).

**Dependencies:** Phase 1 (Ingress endpoint must exist; Kestrel must be running).

### Phase 3 — Config Read/Write API + Detector Assignment (UI-03, CFG-01, CFG-03)

**Goal:** User can see the current tracked-entity config in the UI and save a new one; detector/parameter assignment works; reload happens without container restart.

Steps:
1. Introduce `ILiveEntitiesConfig` + `LiveEntitiesConfig` (file-watch + atomic swap).
2. Modify `Program.cs`: register `ILiveEntitiesConfig`; change existing `EntitiesConfig` singleton to read from `ILiveEntitiesConfig.Get()` where needed.
3. Modify `NetDaemonHaEventSource`, `ScoreStreamPipeline` to inject `ILiveEntitiesConfig`.
4. Modify `HaListenerWorker` to listen to `ConfigChanged` and restart inner `RunAsync` loop.
5. Relax `EntitiesConfigLoader.Validate()` — empty-entities must not throw.
6. Implement `ConfigApiEndpoints` (`GET /api/config`, `POST /api/config`).
7. Implement detector/parameter assignment UI (selector for hst/mad/stl; typed param fields; client-side validation).
8. End-to-end test: save config in UI → streaming pipeline restarts with new entity set within 1-2 seconds; no container restart.

**Dependencies:** Phase 2 (entity picker provides the entity selection input to the config UI).

### Phase 4 — Validation, Polish, CI Packaging (UI-04, CFG-04, DOCS-02)

**Goal:** Production-quality validation and error handling; CI bundles UI assets into the image; documentation.

Steps:
1. Full server-side validation in `POST /api/config`: entity_id format check, detector name whitelist (hst/mad/stl), param range checks, max entity count.
2. Client-side validation mirroring server-side: inline error messages, save-disabled until valid.
3. Error states in UI: HA WS not connected (registry empty), save failure (server error), reload timeout.
4. CI step: build UI assets → copy to `wwwroot/` → multi-arch Docker build; verify image size under 2 GB.
5. DOCS.md section: how to open the UI, how to select entities, how to assign detectors, what "apply" means.
6. End-to-end test: configure entirely via UI with zero manual YAML editing; verify all v2 functionality (streaming, batch, health) unaffected.

**Dependencies:** Phase 3 (all functionality must work before polish and docs are meaningful).

---

## Anti-Patterns to Avoid

### Anti-Pattern 1: Separate s6 Web Service

**What people do:** Add a new `services.d/webui/run` entry (e.g. Python Flask or second .NET binary) to serve the UI separately from the orchestrator.

**Why it's wrong:** Requires duplicating the HA WS connection (second Bearer-authenticated socket to the Supervisor proxy), cross-process IPC for the reload signal, a second `SUPERVISOR_TOKEN` consumer, and a second process managed by s6. Doubles the failure surface with no benefit. The within-process co-hosting approach is simpler and more robust.

**Do this instead:** `WebApplication.CreateBuilder` in the existing orchestrator; all routes co-hosted in the same Kestrel instance.

### Anti-Pattern 2: Host Restart on Config Save

**What people do:** After writing `entities.yaml`, call `IHostApplicationLifetime.StopApplication()` and let s6 restart the process.

**Why it's wrong:** Process restart tears down the MQTT connection (LWT fires, entities go `unavailable`), gRPC streams close, the health entity goes offline. Users see a 5-10 second gap in HA. This violates CFG-04.

**Do this instead:** `ILiveEntitiesConfig.ConfigChanged` event → cancel inner `CancellationTokenSource` in `HaListenerWorker` → restart only the stream loop. MQTT and WS connections stay alive.

### Anti-Pattern 3: Exposing a Non-Ingress Port

**What people do:** Bind Kestrel on `:5000` and add `ports: [5000:5000]` to the add-on config.

**Why it's wrong:** Bypasses HA authentication. The UI is reachable on the LAN without HA login. The add-on security model requires Ingress for any user-facing UI.

**Do this instead:** `ingress: true` + `ingress_port` in `config.yaml`; Kestrel binds only on the Ingress port; `IngressAuthMiddleware` validates the session token on every request.

### Anti-Pattern 4: Mutating `EntitiesConfig.Entities` In-Place

**What people do:** Keep `EntitiesConfig` as the DI singleton, call `.Entities.Clear()` and `.Entities.AddRange(newEntities)` on reload.

**Why it's wrong:** `List<EntityConfig>` is not thread-safe. `HaListenerWorker`, `ScoreStreamPipeline`, and `BatchSchedulerWorker` all read `.Entities` concurrently. Mutating the list while it is being iterated causes `InvalidOperationException` or silent corruption.

**Do this instead:** `ILiveEntitiesConfig.Reload()` uses `Interlocked.Exchange` to atomically swap the entire `EntitiesConfig` reference. Readers always see a consistent, complete config.

### Anti-Pattern 5: Opening a New WS Connection Per API Request

**What people do:** `GET /api/sensors` creates a new `HaWebSocketClient`, calls `GetStatesAsync`, returns results, disposes the client.

**Why it's wrong:** Each request opens and closes a WebSocket to the Supervisor proxy. Under any concurrent UI load this exhausts connections or hits rate limits. The Supervisor proxy is not designed for per-request WS lifecycles.

**Do this instead:** `IHaSensorRegistry` caches the last `get_states` snapshot populated by the existing long-lived `HaWebSocketClient` connection in `NetDaemonHaEventSource`. The API handler reads the cache; no new WS connection is opened.

### Anti-Pattern 6: Leaving `EntitiesConfigLoader.Validate()` Throwing on Empty

**What people do:** Leave the existing `throw new InvalidOperationException("entities.yaml contains no entities")` in place.

**Why it's wrong:** When a user first opens the add-on with an empty `options.json` (no entities configured) and then opens the UI to configure entities for the first time, the orchestrator will have crashed at startup because `gen-entities.py` writes `entities: []`. The UI is served by the orchestrator — which has crashed — so the user cannot reach the UI to fix the problem. Catch-22.

**Do this instead:** Change the empty-entities check from a fatal throw to a `LogWarning`. The orchestrator starts with zero entities (no streaming, which is correct) and the UI is reachable to let the user add entities.

---

## Integration Points

### External (Unchanged from v2)

| Integration | Mechanism | Change for v3 |
|-------------|-----------|---------------|
| HA WebSocket | `HaWebSocketClient` via `ws://supervisor/core/websocket` | `NetDaemonHaEventSource` additionally calls `IHaSensorRegistry.Update()` after `GetStatesAsync` |
| MQTT broker | `MqttConnection` / `MqttPublisherWorker` | None |
| Python gRPC detector | `DetectionGateway` | None |
| InfluxDB | `InfluxDbReader` / `BatchSchedulerWorker` | None |

### New Integration Points (v3)

| Integration | Mechanism | Notes |
|-------------|-----------|-------|
| HA Ingress | `config.yaml: ingress: true` + Kestrel on `ingress_port` | HA proxies user sessions; no port exposed to LAN |
| Ingress session validation | `IngressAuthMiddleware` → `http://supervisor/core/api/ingress/validate_session` | Uses `SUPERVISOR_TOKEN`; same token the orchestrator already holds |
| UI config save | `POST /api/config` → YAML write → `ILiveEntitiesConfig.Reload()` | Atomic file write + in-process reference swap |
| UI sensor list | `GET /api/sensors` → `IHaSensorRegistry.GetSnapshot()` | No new WS connection; reads from registry populated by existing event source |
| Config reload signal | `ILiveEntitiesConfig.ConfigChanged` event → `HaListenerWorker` inner CTS cancel | Within-process; no IPC |

---

## Open Questions

- **`ingress_port` value:** Any unused port (8099 suggested; verify it does not conflict with the gRPC watchdog port 50051 or any HA reserved ports).
- **Ingress validate_session API shape:** Confirm the exact Supervisor API endpoint and request/response format for `validate_session` before implementing `IngressAuthMiddleware`. This is a Supervisor internal API; documentation is sparse.
- **`BatchSchedulerWorker` config read pattern:** Verify whether `BatchSchedulerWorker` holds a reference to `EntitiesConfig.Entities` captured at construction, or reads it at each batch cycle. If captured at construction, it must be changed to read from `ILiveEntitiesConfig.Get()` at batch time to pick up newly added/removed entities.
- **Empty-entities UI state:** Decide what `/api/config POST` with zero entities should return — validation error or accept (saves empty tracking list, which is a valid "monitoring paused" state).
- **UI tech choice (Q1 in REQUIREMENTS.md):** Server-rendered pages (no build step, easy to embed in wwwroot) vs a bundled SPA (React/Preact — richer UX, adds CI complexity). This choice affects Phase 4 CI packaging and image size. Resolve before Phase 1 coding begins.

---

*Architecture research for: Argus v3.0 Ingress Configuration UI*
*Researched: 2026-06-30*
