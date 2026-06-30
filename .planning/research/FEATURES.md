# Feature Research

**Domain:** Home Assistant add-on Ingress configuration web UI (v3.0)
**Researched:** 2026-06-30
**Confidence:** MEDIUM — HA Ingress mechanics from official developer docs (HIGH); UX patterns from ecosystem survey of real add-ons (MEDIUM); static-file/path-base specifics from community issues (MEDIUM); .NET file-watch reload from official docs (HIGH)

---

## Scope

This file covers ONLY the new v3.0 Ingress Configuration UI. Existing v2.0 features (streaming
detection, MQTT discovery, batch scheduling, health entity, add-on packaging) are shipped and out
of scope. The three UI scenarios under research:

- **(a) Entity discovery + selection** — browse/filter/search live HA sensors, see current values,
  select which Argus tracks
- **(b) Per-entity detector assignment** — assign HST/MAD/STL with editable parameters
- **(c) Apply without restart** — save → reload → feedback/validation loop

Dependencies on v2.0 capabilities are noted throughout.

---

## How HA Ingress Works — Critical Background

Understanding the Ingress mechanism is prerequisite to all feature decisions below.

### Ingress plumbing

- Add-on `config.yaml` must declare `ingress: true` and `ingress_port: <port>` (default 8099).
- HA Supervisor reverse-proxies `https://ha-host/api/hassio_ingress/<token>/` to
  `http://172.30.32.1:<ingress_port>/` inside the add-on container.
- **Authentication is handled by HA.** The user is already authenticated before the request
  reaches the add-on — the add-on does NOT need to implement auth.
- **Only connections from `172.30.32.2` must be allowed** — the add-on should reject other IPs.
- HA injects the `X-Ingress-Path` header on every request, containing the base path prefix
  (e.g. `/api/hassio_ingress/abc123`). The app MUST use this for all absolute URL generation.
- Protocols supported: HTTP/1.x, streaming, WebSockets.
- `ingress_entry` is the URL HA opens when the user clicks "Open Web UI".

### Critical path-base pitfall

Ingress prepends a dynamic path prefix to all requests. Static assets served with hardcoded root
paths (`/app.js`, `/style.css`) will 404 through the proxy. The two valid approaches:

1. **Relative paths only** — all asset references use relative URLs (`./app.js`, `../style.css`);
   works without knowing the prefix at build time.
2. **`UsePathBase` middleware** — read `X-Ingress-Path` at startup or per-request and set
   `app.UsePathBase(ingressPath)` so ASP.NET Core strips the prefix before routing.
   `UsePathBase` must be registered BEFORE `UseRouting` in the middleware pipeline.

Option 1 (relative paths) is simpler and has no runtime dependency; Option 2 is required if the
app uses absolute redirects or generates absolute URLs server-side.

### What the orchestrator already has (v2.0 assets)

| Existing capability | How UI reuses it |
|--------------------|-----------------|
| `HaWebSocketClient.GetStatesAsync()` | Fetch all live entity states for the discovery browser |
| `SelectDiscoverableSensors()` | Filter to numeric sensors not yet configured |
| `EntitiesConfig` / `EntitiesConfigLoader` | Config model the UI reads and writes |
| `entities.yaml` under `/data/` | Config file the UI persists changes to |
| `ConnectionSettings.HaToken` (`SUPERVISOR_TOKEN`) | Auth for making HA API calls from the UI backend |
| `DetectorConfig` + `HstParams` | Parameter schema the per-entity form is built from |

The UI backend runs inside the same process as the orchestrator (ASP.NET minimal API added to the
existing `Host`). It does not need a separate process or new container.

---

## Feature Landscape

### Table Stakes (Users Expect These)

These are the non-negotiable features for a usable Ingress config UI. Missing any one of them
means the UI is broken or confusing.

| Feature | Why Expected | Complexity | v2.0 Dependency | Notes |
|---------|--------------|------------|-----------------|-------|
| Ingress endpoint accessible via "Open Web UI" | Every HA add-on with a web UI uses Ingress; missing = button does nothing | LOW | Adds `ingress: true` + `ingress_port` to `config.yaml`; orchestrator adds ASP.NET minimal API server on that port | Port 8080 recommended (avoids conflict with watchdog gRPC on 50051) |
| Live sensor list showing all HA numeric sensors | Without this the user has no idea what entity_ids exist; they are forced to use Developer Tools separately | MEDIUM | Reuses `HaWebSocketClient.GetStatesAsync()` + `SelectDiscoverableSensors()` already in v2.0 | Returns entity_id + current numeric value; no need to re-implement the HA call |
| Current value shown per sensor in the list | Users need context to identify sensors (e.g. "22.3" for a temp vs a raw counter) | LOW | Values already returned by `GetStatesAsync()` | Format to 2 decimal places; omit unit (not available from basic get_states) |
| Text search / filter on entity_id | A typical HA instance has 200-2000 entities; unfiltered list is unusable | LOW | Client-side JS only; no server round-trip needed | Substring match on entity_id string; update list on keyup |
| Distinction: "already tracking" vs "available to add" | User must see which sensors Argus monitors and which are new candidates | LOW | Read `entities.yaml` from `/data/` (already written by startup) | Two sections or visual differentiation (checkbox state, label, color) |
| Select/deselect sensors from the discovered list | Core action — add a sensor to tracking | LOW | Writes to `entities.yaml` via save action | Checkbox or toggle per row; bulk select not required for MVP |
| Per-entity detector type selector (HST/MAD/STL) | Requirements spec (UI-03) explicitly calls this out; it is the main config action beyond entity selection | MEDIUM | `DetectorConfig.Name` + typed param structs already exist in v2.0 | Dropdown per entity; each detector has a distinct parameter set |
| Per-detector parameter fields with defaults shown | Without visible defaults, users have no idea what "n_trees" means or what a safe value is | MEDIUM | `HstParams.From()` defaults already defined in `EntitiesConfig.cs` | Show current value pre-filled; display valid range next to each field |
| Input validation with visible error messages before save | Without this users silently save bad config (e.g. `high_threshold: 2.0` which is out of range [0,1]) | MEDIUM | Parameter ranges are already coded in `HstParams` defaults; need to expose as validation rules | Validate on form submit; highlight fields in error; block save if invalid |
| Save button persists changes to `/data/entities.yaml` | Core action; without persistence changes are lost on restart | LOW | `EntitiesConfigLoader` already reads this file; UI backend writes it | Atomic write (write to temp file, rename) to avoid partial writes |
| Apply without add-on restart | Requirements spec (CFG-04) requires changes apply within seconds; a restart takes 15-30+ seconds and clears model state | HIGH | File-watch on `entities.yaml` in the orchestrator; reconfigure streaming pipeline in-place | This is the most technically complex table-stakes feature — see dependency notes |
| Success/failure feedback after save | User needs to know whether the save worked and whether the orchestrator accepted the reload | LOW | The reload mechanism must return success/error status; UI polls or uses SSE | Toast notification or status banner; show error text on failure |
| Config state survives add-on restart | User expects configuration to persist; losing it on restart = trust broken | LOW | `/data/` volume already survives restarts (v2.0 maps `type: data`) | No new work needed beyond correct path |

### Differentiators (Argus-Specific Value)

These features are not expected by default but meaningfully improve the UX for this specific use case.

| Feature | Value Proposition | Complexity | v2.0 Dependency | Notes |
|---------|-------------------|------------|-----------------|-------|
| include_patterns / exclude_patterns wired to real selection | The v2.0 schema has these fields but they are IGNORED; closing this gap is the explicit v3.0 goal (REQUIREMENTS CFG-02) | MEDIUM | `include_patterns` / `exclude_patterns` exist in `config.yaml` options schema; orchestrator must now actually apply them | Pattern UI: two text inputs (one pattern per line or comma-separated); preview which entities match before save |
| Live pattern preview ("these 7 sensors would be tracked") | Users cannot tell if their glob `sensor.*_temperature` matches 2 or 20 sensors without running it | MEDIUM | Reuses `SelectDiscoverableSensors()` server-side; exposed via a preview endpoint | Debounce input → GET /api/ui/preview?patterns=... → returns matched count + list |
| Multiple detectors per entity | v2.0 config model supports multiple `detectors:` per entity but the UI only needs to expose 1 per entity for MVP; surfacing the multi-detector model is a differentiator | MEDIUM | `EntityConfig.Detectors: List<DetectorConfig>` already supports multiple | "Add detector" button per entity row; UI renders each detector as a collapsible panel |
| Detector parameter documentation shown inline | HST/MAD/STL parameters are ML concepts; showing a one-line tooltip ("Higher = more sensitive; default: 0.7") prevents misuse | LOW | No dependency — purely UI content | Tooltip or `<details>` element per parameter field |
| Reload status visible in UI (applying / applied / error) | When "apply without restart" is in progress the user needs feedback that the orchestrator is reconfiguring | LOW | Reload mechanism must surface status; UI polls `GET /api/ui/status` | Status badge: "Idle / Reloading / Error" with timestamp of last successful reload |
| Sensor count summary (N tracked, M available) | Quick orientation; how many sensors is Argus watching? | LOW | Count from loaded `EntitiesConfig` | Single line at top of page: "Tracking 3 sensors. 47 numeric sensors available." |
| Unsaved-changes warning before navigating away | Prevents accidental loss of edits if the user clicks the HA sidebar while mid-edit | LOW | No dependency | Browser `beforeunload` event; warn only if form is dirty |

### Anti-Features (Scope Traps to Explicitly Avoid)

These are commonly requested or tempting features that should NOT be built for v3.0.

| Feature | Why Tempting | Why Problematic | What To Do Instead |
|---------|-------------|-----------------|-------------------|
| Full SPA framework (React, Vue, Svelte) | Modern, component-based, good DX | Adds a build pipeline to the .NET project; assets must be bundled and embedded in the image; significant complexity for a single-page config UI used infrequently | Use server-rendered HTML with vanilla JS or HTMX for dynamic parts. The config UI has ~3 screens; it does not need a component framework. |
| InfluxDB configuration in the Ingress UI | All config in one place | InfluxDB settings are in `options.json` (managed by Supervisor); the Ingress UI does not own them. Writing them from the UI bypasses the Supervisor's options model and creates two sources of truth. | Leave InfluxDB config in the HA add-on options tab (Supervisor-managed). Ingress UI owns only entity selection and detector assignment. |
| Add-on options tab removal | Single config UI is cleaner | The Supervisor options form handles fields the Ingress UI cannot (InfluxDB credentials, detector endpoint, batch interval). Removing the options tab breaks those fields. | Keep both. Document the split: options tab = infrastructure settings; Ingress UI = sensor + detector config. |
| Live sensor value streaming / dashboard | "Wouldn't it be cool to see live anomaly scores?" | That is a monitoring dashboard, not a configuration UI. It requires SSE or WebSocket per-sensor streams, a real-time chart library, and significant ongoing maintenance. It dilutes the v3.0 focus. | HA already displays the MQTT-published binary_sensor + score sensor entities on any dashboard. Link to those entities from the UI if a live view is desired. |
| User authentication / session management | Security concern | HA Ingress already authenticates the user before the request reaches the add-on. Adding a second auth layer confuses users and duplicates HA's work. | Trust the Ingress layer. Accept all connections from `172.30.32.2` (the Supervisor proxy IP) without additional auth. |
| Undo / revision history | "What if I make a mistake?" | Adds complexity (storing previous config versions under `/data/`) with low frequency of use. The add-on restart itself is a recovery path (old model state is still on disk). | Document that the previous `entities.yaml` can be restored via File Editor if needed. A single backup copy (`.entities.yaml.bak`) written before each save is sufficient. |
| Auto-discovery-only mode (no explicit entity list) | Zero config | Monitoring every numeric sensor produces model pollution and false positives from sensors that are naturally non-stationary (e.g. energy counters). Users need to choose what to watch. | Keep explicit selection as the primary mode. Use `include_patterns` to reduce typing, not to eliminate intention. |
| Per-entity calibration / threshold tuning UI | Full control | HST threshold tuning requires understanding the anomaly score distribution for each sensor, which requires historical data and statistics the UI cannot display cleanly. Exposing raw threshold sliders without context leads to misconfiguration. | Expose only the documented `high_threshold` / `low_threshold` / `min_consecutive` fields with their defaults shown. Hide deeper tuning (frozen window, variance threshold) behind an "Advanced" toggle. |
| Grafana-style iframe embed in HA dashboard | "Show sensor data inside a Lovelace card" | HA Ingress URLs are not embeddable in iframe/webpage Lovelace cards without an active Ingress session. This is a known HA limitation. Attempting it produces auth errors. | The anomaly entities themselves (binary_sensor + score sensor) are natively embeddable in Lovelace. No iframe needed. |
| Multi-user concurrent editing | Two users editing config simultaneously | Single-user, single-operator system (PROJECT.md constraint). File-based config has no locking. | No concurrent editing protection needed. Document that only one operator should edit at a time (trivially true in a home setup). |

---

## Feature Dependencies

```
[Ingress endpoint (config.yaml + ASP.NET server)]
    └──required by──> ALL UI features

[Entity discovery browser]
    └──requires──> [HaWebSocketClient.GetStatesAsync() — already exists]
    └──requires──> [SelectDiscoverableSensors() — already exists]
    └──requires──> [SUPERVISOR_TOKEN available — already exists]
    └──enhances──> [Entity selection checkboxes]

[Entity selection checkboxes]
    └──requires──> [Entity discovery browser]
    └──requires──> [Read current entities.yaml — already exists via EntitiesConfigLoader]
    └──feeds──> [Save → write entities.yaml]

[Detector type selector + parameter fields]
    └──requires──> [Entity selection] (must know which entities are selected)
    └──requires──> [DetectorConfig + HstParams — already in v2.0]
    └──requires──> [Input validation rules]
    └──feeds──> [Save → write entities.yaml]

[Save → write entities.yaml]
    └──requires──> [Input validation passes]
    └──triggers──> [Reload without restart]

[Reload without restart (CFG-04)]
    └──requires──> [File watcher on entities.yaml — NEW, not in v2.0]
    └──requires──> [In-place pipeline reconfiguration — NEW, not in v2.0]
    └──requires──> [Save → write entities.yaml]
    └──feeds──> [Reload status indicator in UI]

[include_patterns / exclude_patterns wired]
    └──requires──> [Entity discovery browser] (pattern expansion needs live entity list)
    └──requires──> [Save → write entities.yaml]
    └──optional──> [Live pattern preview endpoint]

[Live pattern preview]
    └──requires──> [include_patterns / exclude_patterns wired]
    └──requires──> [HaWebSocketClient.GetStatesAsync()]

[Reload status indicator]
    └──requires──> [Reload without restart]
    └──requires──> [Status endpoint GET /api/ui/status]
```

### Key dependency notes

- **Reload without restart is the single most complex dependency.** It requires the orchestrator
  to watch `entities.yaml` for changes and reconfigure the live streaming pipeline without
  cancelling the host. The v2.0 pipeline reads `EntitiesConfig` once at startup and registers it
  as a singleton — this must change to a `IOptionsMonitor<EntitiesConfig>` or equivalent reactive
  pattern. This is NOT currently implemented and is the highest-risk item for v3.0.

- **Entity discovery browser has zero new infrastructure cost.** `GetStatesAsync()` and
  `SelectDiscoverableSensors()` already exist in v2.0; the UI just needs an HTTP endpoint that
  calls them and returns JSON.

- **Ingress path-base is a blocking issue for static assets.** All `<script>`, `<link>`, and
  `<img>` tags must use relative paths, or the app must read `X-Ingress-Path` at startup and
  call `app.UsePathBase()` before `app.UseRouting()`. This must be solved before the first
  working UI prototype.

---

## MVP Definition for v3.0

### Must ship (v3.0 launch)

- [ ] `ingress: true` + `ingress_port` in `config.yaml` — enables "Open Web UI" button
- [ ] ASP.NET minimal API server on ingress port, added to existing orchestrator host
- [ ] `X-Ingress-Path` / relative-path handling so static assets resolve through the Supervisor proxy
- [ ] Entity discovery page: fetch all HA numeric sensors, show entity_id + current value, text search
- [ ] Entity selection: checkboxes; distinquish already-tracked from available; persist selection to `entities.yaml`
- [ ] Per-entity detector assignment: detector type dropdown (HST/MAD/STL); parameter fields with defaults; parameter validation with error messages
- [ ] Save action: atomic write to `/data/entities.yaml`; single `.bak` backup before overwrite
- [ ] Reload without restart: file watcher triggers in-place pipeline reconfiguration; status returned to UI
- [ ] Success/failure feedback: status banner or toast after save + reload attempt
- [ ] `include_patterns` / `exclude_patterns` wired: pattern fields in UI; expansion applied before writing `entities.yaml` (closes v2.0 gap)
- [ ] `ingress_entry` documented in DOCS.md; updated DOCS-02 requirement satisfied

### Add after v3.0 ships (v3.x)

- [ ] Live pattern preview — only if users find pattern matching confusing without it
- [ ] Multiple detectors per entity in UI — model already supports it; UI currently shows 1
- [ ] Advanced parameter toggle (frozen window, variance threshold) — hide by default, expose on demand
- [ ] Unsaved-changes warning — browser `beforeunload`; low effort, add when UI is stable

### Defer to v4+

- [ ] Monitoring dashboard / live anomaly scores — distinct milestone, not config UI
- [ ] Per-entity calibration UI with historical data — requires InfluxDB query integration in the UI
- [ ] Multi-detector comparison view — requires understanding score distributions

---

## Feature Prioritization Matrix

| Feature | User Value | Implementation Cost | Priority |
|---------|------------|---------------------|----------|
| Ingress endpoint live | HIGH | LOW | P1 |
| X-Ingress-Path / path-base handling | HIGH | LOW | P1 (blocker) |
| Entity discovery browser | HIGH | LOW | P1 |
| Text search/filter on entity list | HIGH | LOW | P1 |
| Entity selection + tracking status | HIGH | LOW | P1 |
| Detector type selector | HIGH | MEDIUM | P1 |
| Parameter fields with defaults | HIGH | MEDIUM | P1 |
| Input validation + error messages | HIGH | MEDIUM | P1 |
| Save → write entities.yaml | HIGH | LOW | P1 |
| Reload without restart (CFG-04) | HIGH | HIGH | P1 |
| Success/failure feedback | HIGH | LOW | P1 |
| include_patterns/exclude_patterns wired | MEDIUM | MEDIUM | P1 |
| Reload status indicator | MEDIUM | LOW | P2 |
| Sensor count summary | LOW | LOW | P2 |
| Unsaved-changes warning | LOW | LOW | P2 |
| Live pattern preview | MEDIUM | MEDIUM | P2 |
| Multiple detectors per entity in UI | LOW | MEDIUM | P3 |
| Advanced parameter toggle | LOW | LOW | P3 |
| Monitoring dashboard | MEDIUM | HIGH | P3 (separate milestone) |

**Priority key:** P1 = must have for v3.0 launch; P2 = add when core works; P3 = future

---

## Ingress Config.yaml Changes Required

The v2.0 `config.yaml` does not declare ingress. These additions are required:

```yaml
# Add these fields to argus/config.yaml
ingress: true
ingress_port: 8080        # any free port; avoid 50051 (gRPC watchdog)
ingress_entry: /          # path HA opens; "/" with UsePathBase is fine
panel_icon: mdi:tune      # sidebar icon shown in HA
panel_title: Argus Config # sidebar label
```

The watchdog currently points to `tcp://[HOST]:50051` (gRPC). The ingress port (8080) should also
be reachable for a `http://` watchdog entry if the health endpoint is added to the minimal API.

---

## Reload Without Restart — Technical Options

This is the highest-complexity table-stakes feature. Three approaches ranked by risk:

### Option R1 — FileSystemWatcher + IOptionsMonitor (recommended)

Use `IOptionsMonitor<EntitiesConfig>` with a custom JSON/YAML file provider. On file change:
1. Reload `entities.yaml` from disk.
2. Diff old vs new entity set.
3. Add new entities to `ScoreStreamPipeline` and `_configuredEntities` HashSet.
4. Remove dropped entities (send MQTT `unavailable`, unregister from pipeline).
5. Return success to UI via status endpoint.

Risk: `ScoreStreamPipeline` and `HaListenerWorker` currently read `EntitiesConfig` once at
construction time (singleton). The DI graph must be restructured to tolerate live entity set
changes. This is the main implementation risk.

Mitigation: scope the reload to only what changes — the `_configuredEntities` HashSet in
`NetDaemonHaEventSource` and the per-entity `EntityRuntimeState` map in `ScoreStreamPipeline`.
The gRPC channel and MQTT connection do not need to restart.

### Option R2 — Soft restart (process self-restart)

Write config, then signal the orchestrator to exit with code 0. The s6 supervisor restarts it.
Simpler to implement; respects the existing startup path entirely.

Downside: ~5-10 second gap during restart; in-flight gRPC streams drop; MQTT LWT fires briefly.
Not acceptable per CFG-04 ("within seconds").

### Option R3 — Config-gen bridge only (no in-process reload)

UI writes new `entities.yaml`; a separate config-gen script regenerates and restarts only the
entities-tracking state. Requires IPC between script and orchestrator (signal, named pipe, HTTP).
More complex than R1 with no benefit.

**Recommendation: R1.** The IOptionsMonitor pattern is well-supported in .NET 8 and documented.
The key constraint is that `reloadOnChange: true` must be set on the YAML file provider, and
`UsePathBase` must be registered before `UseRouting` if absolute redirects are needed.

---

## Sources

- [HA Add-on Presentation / Ingress docs](https://developers.home-assistant.io/docs/add-ons/presentation) — ingress: true, ingress_port, X-Ingress-Path header, IP restriction
- [HA community: Addon ingress thread](https://community.home-assistant.io/t/addon-ingress/936226) — real add-on developer pitfalls with path resolution
- [HA community: Trouble with static assets in custom addon with ingress](https://community.home-assistant.io/t/trouble-with-static-assets-in-custom-addon-with-ingress/712298) — relative paths vs X-Ingress-Path
- [HA community: How to use X-Ingress-Path in an add-on](https://community.home-assistant.io/t/how-to-use-x-ingress-path-in-an-add-on/276905) — base URL extraction pattern
- [Understanding PathBase in ASP.NET Core — Andrew Lock](https://andrewlock.net/understanding-pathbase-in-aspnetcore/) — UsePathBase placement before UseRouting
- [Using PathBase with .NET 6 WebApplicationBuilder — Andrew Lock](https://andrewlock.net/using-pathbase-with-dotnet-6-webapplicationbuilder/) — minimal API specific guidance
- [Real-Time Config Updates with IOptionsMonitor .NET](https://medium.com/codenx/real-time-configuration-updates-in-asp-net-core-with-live-loading-of-appsettings-json-d63eac388d28) — file watcher + IOptionsMonitor pattern
- [MinimalAPI IOptionsMonitor known issue — dotnet/aspnetcore#34056](https://github.com/dotnet/aspnetcore/issues/34056) — builder.Configuration vs DI IConfiguration mismatch warning
- [hms-homelab/hms-baby-tracker](https://github.com/hms-homelab/hms-baby-tracker) — real HA add-on with Ingress UI (FastAPI + SQLite pattern)
- [HTMX + Alpine.js for config UIs — InfoWorld](https://www.infoworld.com/article/3856520/htmx-and-alpine-js-how-to-combine-two-great-lean-front-ends.html) — lightweight alternative to SPA for config UIs

---
*Feature research for: Home Assistant add-on Ingress configuration web UI (v3.0)*
*Researched: 2026-06-30*
