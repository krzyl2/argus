# Project Research Summary

**Project:** Argus v3.0 — Ingress Configuration UI
**Domain:** ASP.NET Core Minimal API web UI co-hosted in an existing .NET 8 Generic Host add-on, served through Home Assistant Supervisor Ingress
**Researched:** 2026-06-30
**Confidence:** HIGH (stack + architecture derive from reading actual source files and official docs; pitfalls confirmed via supervisor source + community evidence)

## Executive Summary

Argus v3.0 adds a Home Assistant Ingress web UI to an already-running v2.0 add-on. The right approach is to co-host ASP.NET Core Minimal API inside the existing Generic Host process by switching the project SDK from `Microsoft.NET.Sdk.Worker` to `Microsoft.NET.Sdk.Web` and replacing `Host.CreateApplicationBuilder` with `WebApplication.CreateBuilder`. All six existing `BackgroundService` instances remain unchanged. Kestrel binds on `0.0.0.0:8099` (the Ingress port), is not exposed via `ports:` in `config.yaml`, and serves server-rendered HTML with htmx 2.x for partial-update interactions — no SPA, no Node.js toolchain, no CDN dependency, air-gapped safe. The UI reads and writes the same `/data/entities.yaml` that `EntitiesConfigLoader` already consumes, so no new config format is introduced.

The highest-risk feature is reload-without-restart. The current pipeline reads `EntitiesConfig` once at startup as a frozen singleton; workers hold direct references that are never updated. To support live reload, a new `ILiveEntitiesConfig` singleton wraps the config with an `Interlocked.Exchange` swap and a `ConfigChanged` event. `HaListenerWorker` subscribes to that event, cancels an inner `CancellationTokenSource`, and restarts only the `ScoreStreamPipeline.RunAsync` loop — leaving MQTT, gRPC transport, and HA WebSocket connections alive. Removed entities must have their MQTT discovery topics retracted before the loop restarts. The streaming gap is under one second.

Three pre-conditions are critical before any UI feature work begins: (1) `gen-entities.py` currently runs at every container start and will silently erase UI-authored config — it must be guarded with a marker file or `_source: ui` field before the first UI save lands in Phase 2; (2) `EntitiesConfigLoader.Validate()` currently throws on an empty entities list, which would crash the orchestrator before the UI is reachable — this must be relaxed to a warning in Phase 1; (3) all config writes must be atomic (write to `.tmp`, then `File.Move(..., overwrite: true)`) and serialized via a `SemaphoreSlim(1)` from day one.

---

## Key Findings

### Recommended Stack

Switch the orchestrator project SDK to `Microsoft.NET.Sdk.Web`. This is a one-line `.csproj` change; `WebApplication` is a strict superset of `IHost` and runs all existing hosted services identically. No new NuGet packages are required — Kestrel, Static Files middleware, and Minimal API routing are all included in the Web SDK. Bundle htmx 2.0.10 (14 KB, BSD 0-Clause, air-gapped safe) into `wwwroot/` at commit time. Use the existing `YamlDotNet 16.3.0` for all config reads and writes. Change the Dockerfile base image from `mcr.microsoft.com/dotnet/runtime:8.0-jammy-chiseled` to `mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled` (~10 MB larger; same distroless base).

**Core technologies:**
- `Microsoft.NET.Sdk.Web` (.NET 8): SDK switch that enables Kestrel + Minimal API with zero additional packages
- ASP.NET Core Minimal API (.NET 8 built-in): 4-5 route handlers covering config read/write and sensor discovery
- Kestrel HTTP server (.NET 8 built-in): `http://0.0.0.0:8099` — never loopback; never `ports:` in `config.yaml`
- htmx 2.0.10 (committed to `wwwroot/`): partial HTML swaps for entity list and form interactions; no SPA needed
- `YamlDotNet 16.3.0` (already pinned): reads and writes `/data/entities.yaml` with `UnderscoredNamingConvention`

**What NOT to add:** React/Vue/Svelte (Node.js build complexity), Blazor (SignalR fragile behind HA Supervisor proxy), static-value `UsePathBase` middleware (prefix is dynamic), CDN references to htmx (air-gapped installs fail), separate HTTPS in Kestrel (HA Supervisor handles TLS externally).

### Expected Features

**Must have (table stakes for v3.0):**
- Ingress endpoint via "Open Web UI" — `ingress: true` + `ingress_port: 8099` in `config.yaml`; Kestrel on `0.0.0.0:8099`
- Live sensor discovery browser — entity_id + current value, text search, powered by `IHaSensorRegistry` (no new WS connection)
- Tracked vs available distinction — show which sensors Argus currently monitors
- Entity selection — checkboxes persist to `/data/entities.yaml` via atomic write
- Per-entity detector assignment — HST/MAD/STL dropdown, typed parameter fields, visible defaults
- Input validation with clear error messages before save
- Atomic config write — temp file + rename; `SemaphoreSlim(1)` serialization
- Reload without add-on restart — `ILiveEntitiesConfig.ConfigChanged` fires; `HaListenerWorker` inner-CTS cancel; pipeline loop restarts; MQTT discovery retraction for removed entities
- `include_patterns`/`exclude_patterns` wired — closes the v2.0 patterns-ignored gap (CFG-02)
- Success/failure feedback after save

**Should have (v3.x differentiators):**
- Live pattern preview endpoint — debounced `GET /api/ui/preview?patterns=...` returning matched entity count
- Reload status indicator — `GET /api/ui/status` returning Idle/Reloading/Error with last-success timestamp
- Sensor count summary ("Tracking N, M available")
- Unsaved-changes warning via `beforeunload`
- Multiple detectors per entity exposed in UI (model already supports it)

**Defer to v4+:**
- Monitoring dashboard / live anomaly scores — distinct milestone; HA entities already surfaced via MQTT
- Per-entity calibration UI with historical data — requires InfluxDB query in the UI
- Grafana-style iframe embed — blocked by HA Ingress session mechanics

**Anti-features to explicitly avoid:**
- SPA framework — config form does not need component lifecycle management
- InfluxDB config in the Ingress UI — owned by Supervisor options form; two sources of truth
- User auth/session management — HA Ingress already authenticates; a second layer breaks the flow
- Separate port exposed via `ports:` — bypasses HA auth entirely

### Architecture Approach

The v3.0 orchestrator is a single `WebApplication` process. Two new singletons are added alongside the existing BackgroundServices: `IHaSensorRegistry` (receives the `get_states` snapshot after each HA connect — no second WS connection) and `ILiveEntitiesConfig` (atomic-swappable config reference with `ConfigChanged` event). Four existing components change their injected type from `EntitiesConfig` to `ILiveEntitiesConfig`. The `FileSystemWatcher` on `/data/entities.yaml` (300ms debounce, `Renamed` event) handles external edits. `POST /api/config` calls `ILiveEntitiesConfig.Reload()` directly after the atomic file write. `/data/entities.yaml` remains the single source of truth; `gen-entities.py` continues to run at first boot but is guarded from overwriting UI-authored config.

**Major components:**

1. `ILiveEntitiesConfig` / `LiveEntitiesConfig` (`Config/`) — `volatile` ref + `Interlocked.Exchange` swap + `ConfigChanged` event; `FileSystemWatcher` with 300ms debounce; replaces the bare `EntitiesConfig` singleton in DI
2. `IHaSensorRegistry` / `HaSensorRegistry` (`Ha/`) — `ConcurrentDictionary` snapshot pushed by `NetDaemonHaEventSource` after each `GetStatesAsync`; read by `GET /api/sensors` without opening a new WS connection
3. `ConfigApiEndpoints` (`Api/`) — `GET /api/config` (read current), `POST /api/config` (validate then atomic write then `ILiveEntitiesConfig.Reload()`)
4. `SensorsApiEndpoints` (`Api/`) — `GET /api/sensors` reads registry and calls `SelectDiscoverableSensors`
5. `IngressAuthMiddleware` (`Api/`) — validates `X-Ingress-Token` via Supervisor `/ingress/validate_session`; applied to all `/api/*` routes
6. `wwwroot/` — `index.html` + `htmx.min.js` + minimal CSS; served by `app.UseStaticFiles()`

**Modified (not new) components:** `NetDaemonHaEventSource` (push to registry; read config from `ILiveEntitiesConfig` per connect), `ScoreStreamPipeline` (inject `ILiveEntitiesConfig`), `HaListenerWorker` (subscribe `ConfigChanged`; inner CTS cancel + loop restart), `EntitiesConfigLoader` (relax empty-entities from throw to LogWarning), `BatchSchedulerWorker` (verify and fix config read pattern if captured at construction).

**Unchanged:** all MQTT, gRPC, InfluxDB, health, and Python detector code.

### Critical Pitfalls

1. **X-Ingress-Path breaks all absolute asset and API URLs** — HA Supervisor routes through a dynamic prefix; hardcoded leading-slash paths 404 through the proxy. Mitigation: relative paths only in HTML/JS; inject `<base href="{ingressPath}/">` from the header; set per-request `PathBase` before `UseRouting`. Always test via HA Ingress, never direct port. (Phase 1 blocker)

2. **Kestrel bound to loopback causes 502 Bad Gateway** — HA Supervisor connects from `172.30.32.2`; `localhost:5000` is unreachable. Mitigation: `ASPNETCORE_URLS=http://0.0.0.0:8099` or explicit `ConfigureKestrel`; suppress default URL list; never add `ports:`. (Phase 1 blocker)

3. **`gen-entities.py` overwrites UI-authored config on restart** — runs unconditionally at every container start. Mitigation: guard with `_source: ui` marker field or `.ui_config_present` lock file. Must be in place before Phase 2 first save. (Phase 2 pre-condition)

4. **`EntitiesConfigLoader.Validate()` throws on empty entities list** — orchestrator crashes before UI is reachable. Mitigation: change from `throw` to `LogWarning`; treat empty-entities as "monitoring paused". Must land in Phase 1. (Phase 1 pre-condition)

5. **Reload races — pipeline restart resets state / orphans MQTT entities** — `ScoreStreamPipeline.RunAsync` rebuilds all entity state, resetting warm-up counters; removed entities linger as "unavailable" in HA. Mitigation: diff old vs new entity sets; retract MQTT discovery topics for removed entities before restarting the loop; document ~4-minute warm-up. (Phase 4)

6. **Non-atomic config write corrupts entities.yaml** — `File.WriteAllText` to the live path can be read mid-write. Mitigation: write to `.tmp`, then `File.Move(tmp, target, overwrite: true)`; serialize with `SemaphoreSlim(1)`; watch `Renamed` event with 300ms debounce. (Phase 1 for atomic write; Phase 4 for watcher debounce)

---

## Conflict to Resolve in Phase 1 (Live Test Required)

**X-Ingress-Path: Does the Supervisor strip the prefix before forwarding?**

- **STACK.md** (citing supervisor `ingress.py` source): The Supervisor strips the ingress prefix before forwarding. The container sees requests at `/`, not at `/api/hassio_ingress/<token>/api/config`. Therefore `UsePathBase` is NOT needed — only `<base href="{ingressPath}/">` in the HTML head.

- **FEATURES.md and PITFALLS.md** (citing Andrew Lock PathBase articles and community threads): `UsePathBase` reading `X-Ingress-Path` before `UseRouting` IS required to ensure ASP.NET redirect helpers, `LinkGenerator`, and static-file middleware prepend the correct external prefix on server-generated absolute URLs.

**Resolution approach:** Both positions can be simultaneously correct at different layers. The safe implementation is: (a) set `context.Request.PathBase` from `X-Ingress-Path` in a per-request middleware before `UseRouting`, AND (b) emit `<base href="{ingressPath}/">` in the root HTML handler. This satisfies both positions with no overhead.

**Phase 1 acceptance criterion:** Open the UI exclusively via HA Supervisor Ingress ("Open Web UI" button) — never via direct port. Confirm all static assets, `GET /api/sensors`, and `POST /api/config` return HTTP 200. Confirm `Location:` headers on any redirect contain the full ingress prefix. Record the verified behavior and close this conflict.

---

## Implications for Roadmap

The REQUIREMENTS.md traceability table maps directly to four phases. Research confirms this ordering is correct and dependency-driven.

### Phase 1: Ingress Scaffold + SDK Migration (UI-01, CFG-01 partial)

**Rationale:** All other work depends on Kestrel running inside the orchestrator process. SDK migration, host builder change, and Ingress wiring are the foundation. The three Phase 1 pre-conditions must land before any feature work.

**Delivers:** "Open Web UI" loads a placeholder page through Ingress; all v2.0 BackgroundService functionality verified unaffected; atomic config write path in place; `EntitiesConfigLoader` no longer crashes on empty entities.

**Requirements addressed:** UI-01, CFG-01 (infrastructure)

**Pitfalls to prevent in this phase:** Kestrel loopback bind (Pitfall 2), host builder DI migration (Pitfall 3), non-atomic config write (Pitfall 4a), image bloat from JS build (Pitfall 6), Kestrel + s6 shutdown ordering (Pitfall 7), no `ports:` entry (Pitfall 9), `UseStaticFiles` + PathBase ordering (Pitfall 10), X-Ingress-Path conflict resolution via live test.

**Research flag:** Live HA OS test required to resolve the X-Ingress-Path / `UsePathBase` conflict.

### Phase 2: Live Sensor Discovery API + Entity Selection (UI-02, CFG-02)

**Rationale:** Entity discovery depends on the Kestrel endpoint. The `gen-entities.py` guard must land at the start of Phase 2 — before the first UI save.

**Delivers:** `/api/sensors` returns live HA entity list; filterable entity picker UI; entity selection persists to `entities.yaml`; `include_patterns`/`exclude_patterns` wired (closes v2.0 gap); `gen-entities.py` conditional guard in place.

**Requirements addressed:** UI-02, CFG-02

**Architecture components introduced:** `IHaSensorRegistry` / `HaSensorRegistry`, `SensorsApiEndpoints`, `IngressAuthMiddleware` (complete), entity picker UI

**Pitfalls addressed:** `gen-entities.py` overwrite (Pitfall 8), new WS per API request (Anti-Pattern 5)

**Research flag:** Supervisor `validate_session` API shape is sparsely documented — probe live Supervisor before implementing `IngressAuthMiddleware`. Fallback: skip in Phase 2 MVP; add in Phase 4.

### Phase 3: Config Read/Write + Detector Assignment (UI-03, CFG-01, CFG-03)

**Rationale:** Detector assignment requires the entity list from Phase 2 and the full config read/write infrastructure. `ILiveEntitiesConfig` is the most invasive change; it must be completed atomically across all four BackgroundService consumers.

**Delivers:** Full config round-trip (read current config, assign detectors + parameters, save, pipeline reloads without container restart); `ILiveEntitiesConfig` with `ConfigChanged` event; `HaListenerWorker` inner-CTS restart loop; MQTT discovery retraction for removed entities.

**Requirements addressed:** UI-03, CFG-01, CFG-03

**Architecture components introduced:** `ILiveEntitiesConfig` / `LiveEntitiesConfig`, `ConfigApiEndpoints`, detector/parameter assignment UI, `FileSystemWatcher` with 300ms debounce

**Pitfalls addressed:** In-place list mutation (Anti-Pattern 4), host restart on save (Anti-Pattern 2), schema drift (Pitfall 4c)

**Research flag:** No deeper research needed — `ILiveEntitiesConfig` is a standard .NET pattern. Verify `BatchSchedulerWorker` config read pattern in source before planning.

### Phase 4: Validation, Polish, CI Packaging (UI-04, CFG-04, DOCS-02)

**Rationale:** Full validation and polish are meaningful only once the core workflow (Phase 3) is working end-to-end. CI packaging and documentation close the milestone.

**Delivers:** Full server-side parameter range validation; client-side validation with inline error messages; error states (WS not connected, save failure, reload timeout); CI image-size gate (fail if >2 GB); `FileSystemWatcher` debounce validated; DOCS.md updated; end-to-end test with zero manual YAML.

**Requirements addressed:** UI-04, CFG-04, DOCS-02

**Pitfalls addressed:** `FileSystemWatcher` double-fire (Pitfall 11), image bloat gate (Pitfall 6), reload race end-to-end test (Pitfall 5)

**Research flag:** Standard patterns only. Document the ~4-minute HST warm-up period in the UI and DOCS.md.

### Phase Ordering Rationale

- Phase 1 is the foundation: Kestrel + empty-entities crash fix + atomic write must all land together.
- Phase 2 before Phase 3: the entity picker is the input source for the detector assignment form. The `gen-entities.py` guard cannot slip to Phase 3.
- Phase 3 before Phase 4: validation and polish are only meaningful once the full save/reload cycle works.
- `ILiveEntitiesConfig` (Phase 3) is isolated from Phase 1 and Phase 2 so those can be validated independently.

### Research Flags

Phases needing live verification during planning:
- **Phase 1:** X-Ingress-Path / `UsePathBase` conflict — live HA OS test required
- **Phase 2:** Supervisor `validate_session` API shape — probe live Supervisor before implementing `IngressAuthMiddleware`

Phases with standard patterns (skip research-phase):
- **Phase 3:** `ILiveEntitiesConfig` + `Interlocked.Exchange` is well-documented .NET pattern
- **Phase 4:** Server-side validation, CI multi-stage Dockerfile, documentation — all standard

---

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | SDK migration verified vs official MS docs; htmx version confirmed; HA Ingress config keys verified vs developer docs + supervisor source; package versions read from actual `.csproj` |
| Features | MEDIUM | Table-stakes confirmed by HA developer docs and community add-on survey; reload mechanism derived from .NET hosted-service docs; no .NET add-on with live config reload found as direct precedent |
| Architecture | HIGH | All findings derived from reading actual source files; no speculative gaps |
| Pitfalls | HIGH / MEDIUM | Ingress pitfalls: HIGH (supervisor source + community threads); reload race: MEDIUM (pattern-derived; no direct HA .NET add-on precedent) |

**Overall confidence:** HIGH for Phases 1 and 2; MEDIUM for Phase 3 (reload implementation complexity); HIGH for Phase 4.

### Gaps to Address

- **X-Ingress-Path / `UsePathBase` conflict**: Requires live HA OS test in Phase 1. Safe implementation: set `PathBase` per-request AND emit `<base href>`.

- **Supervisor `validate_session` API shape**: Not documented. Probe live Supervisor in Phase 2. Fallback: defer `IngressAuthMiddleware` completion to Phase 4.

- **`BatchSchedulerWorker` config read pattern**: Quick source-read before Phase 3 planning — captured at construction or per batch cycle?

- **Empty-entities `POST /api/config` response**: Decide in Phase 3 planning. Research consensus: treat zero entities as valid "monitoring paused" state; document in UI.

---

## Sources

### Primary (HIGH confidence)

- [ASP.NET Core hosted services (.NET 8)](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-8.0) — `AddHostedService` + `WebApplication.CreateBuilder` compatibility
- [HA Developer Docs — Presenting your app (Ingress)](https://developers.home-assistant.io/docs/apps/presentation/) — `ingress`, `ingress_port`, `X-Ingress-Path`, 172.30.32.2 source IP
- [home-assistant/supervisor ingress.py source](https://github.com/home-assistant/supervisor/blob/main/supervisor/api/ingress.py) — URL prefix stripping behavior
- [ASP.NET Core proxy/load balancer docs](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-8.0) — `UsePathBase` placement requirements
- [ASP.NET Core static files docs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/static-files?view=aspnetcore-8.0) — `UseStaticFiles()` + `wwwroot/` defaults
- Actual source files: `Program.cs`, `NetDaemonHaEventSource.cs`, `ScoreStreamPipeline.cs`, `HaListenerWorker.cs`, `EntitiesConfigLoader.cs`, `Argus.Orchestrator.csproj`, `argus/config.yaml`, `gen-entities.py`

### Secondary (MEDIUM confidence)

- [HA Community — X-Ingress-Path usage](https://community.home-assistant.io/t/how-to-use-x-ingress-path-in-an-add-on/276905) — practical base href pattern
- [HA Community — absolute path handling with HA Ingress](https://community.home-assistant.io/t/how-to-handle-absolute-paths-with-ha-ingress/370572) — relative paths working
- [Understanding PathBase in ASP.NET Core — Andrew Lock](https://andrewlock.net/understanding-pathbase-in-aspnetcore/) — `UsePathBase` placement before `UseRouting`
- [Using PathBase with .NET 6 WebApplicationBuilder — Andrew Lock](https://andrewlock.net/using-pathbase-with-dotnet-6-webapplicationbuilder/) — minimal API specific
- [htmx.org npm](https://www.npmjs.com/package/htmx.org) — 2.0.10; BSD 0-Clause confirmed
- [HA Community — 502 Bad Gateway Ingress](https://community.home-assistant.io/t/502-bad-gateway-ingress-error/265775) — loopback bind cause
- [FileSystemWatcher debounce — cocowalla gist](https://gist.github.com/cocowalla/5d181b82b9a986c6761585000901d1b8) — 300ms debounce pattern

### Tertiary (LOW confidence — needs live validation)

- [Addon Ingress community discussion](https://community.home-assistant.io/t/addon-ingress/936226) — real add-on developer pitfalls; anecdotal
- [HA Supervisor Ingress proxy mechanics — deepwiki](https://deepwiki.com/home-assistant/supervisor/6.3-proxy-and-ingress) — third-party summary; defer to supervisor source

---
*Research completed: 2026-06-30*
*Ready for roadmap: yes*
