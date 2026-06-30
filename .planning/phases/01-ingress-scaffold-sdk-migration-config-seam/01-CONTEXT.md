# Phase 1: Ingress Scaffold + SDK Migration + Config Seam - Context

**Gathered:** 2026-06-30
**Status:** Ready for planning
**Mode:** Auto-generated (infrastructure phase — discuss skipped)

<domain>
## Phase Boundary

The orchestrator serves an Ingress web endpoint ("Open Web UI") through the existing
process. Scope: SDK migration Worker→Web, Kestrel bound on `0.0.0.0:8099`, `config.yaml`
declares `ingress` keys, a placeholder page loads through the HA Supervisor proxy, all
v2.0 BackgroundService functionality verified unaffected, empty-entities no longer crashes
the orchestrator, and the atomic config write path is in place from day one.

Out of scope: live sensor discovery UI (Phase 2), config read/write + detector assignment
+ reload (Phase 3), full validation + CI packaging + docs (Phase 4). Phase 1 ships only a
*placeholder* page — proof the Ingress plumbing works end-to-end.
</domain>

<decisions>
## Implementation Decisions

### Claude's Discretion
This is a pure infrastructure phase (scaffold + migration + config seam). All success
criteria are technical/verification-based; there is no user-facing UI design to decide.
The architecture decisions below were already locked during v3.0 research synthesis
(see STATE.md "v3.0 architecture decisions") and are binding constraints, not open choices.

### Locked architecture (binding — from v3.0 research)
- **SDK migration:** `Microsoft.NET.Sdk.Worker` → `Microsoft.NET.Sdk.Web`;
  `Host.CreateApplicationBuilder` → `WebApplication.CreateBuilder`. All existing
  `AddHostedService`/`AddSingleton` registrations remain identical under `WebApplication` —
  no service-registration changes.
- **Co-host in orchestrator process:** Kestrel + Minimal API inside the existing process;
  no second s6 service. UI reads the same singletons as the workers.
- **Kestrel bind:** `0.0.0.0:8099` (not loopback) — Supervisor connects from `172.30.32.2`.
  Verify with `ss -tlnp | grep 8099` inside the container.
- **UI tech:** server-rendered HTML + htmx 2.0.10 (~14 KB, BSD 0-Clause), committed to
  `wwwroot/`. No SPA, no Node.js build step, no CDN — air-gapped safe.
- **PathBase strategy (live-test item):** set `context.Request.PathBase` per-request from
  `X-Ingress-Path` AND emit `<base href="{ingressPath}/">` in the HTML head. Open the UI
  exclusively via "Open Web UI" in HA (never the direct port); confirm all assets + any
  `/api` calls return 200; record behavior and close the STACK-vs-PITFALLS conflict.
- **Docker base image:** `mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled` (replaces
  `runtime:8.0-jammy-chiseled`; ~10 MB larger; same distroless base).
- **config.yaml:** add `ingress: true`, `ingress_port: 8099`, `panel_icon`/`panel_title`.
  No `ports:` entry — Ingress-only; exposing the port would bypass HA auth.
- **Config source of truth:** `/data/entities.yaml` unchanged — read/write via YamlDotNet
  (already referenced). No new config format.

### Critical pre-conditions (must land this phase)
- `EntitiesConfigLoader.Validate()` must change from `throw` to `LogWarning` on empty
  entities — otherwise first-boot with empty `options.json` crashes the orchestrator before
  the UI can load (ARCHITECTURE.md Anti-Pattern 6).
- Atomic config write (temp-then-rename + `SemaphoreSlim(1)`) in place from the start — no
  partial reads possible during a concurrent FileSystemWatcher event.
</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets
- `orchestrator/Argus.Orchestrator/Program.cs` — top-level `Host.CreateApplicationBuilder`
  startup; all DI registrations live here (the migration target). Lines 11/145-146 are the
  Worker-host construction to convert to `WebApplication`.
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader` (+ `EntitiesConfig`) —
  YAML loader; `Validate()` is the empty-entities crash site to soften to a warning.
  `EntitiesConfig` already supports per-entity `detectors: [{name, params}]` (Phase 3 data
  model exists).
- `YamlDotNet 16.3.0` already referenced in the csproj — use for atomic read/write.
- `EntitiesConfigTests.cs` + `StartupSensorLogTests.cs` exist — extend for empty-entities
  warning and atomic-write coverage.

### Established Patterns
- Single authoritative `ConnectionSettings` singleton built from `ARGUS_*` env vars in
  Program.cs; DI consumers inject the instance directly.
- Hosted services registered via `AddHostedService<T>` (HaListenerWorker, MqttPublisherWorker,
  HealthPublisherWorker, BatchSchedulerWorker). These must keep running post-migration.
- Conditional registration guard: InfluxDB batch path only registered when `InfluxUrl` is
  set (Program.cs 117-143) — pattern to follow for any conditional UI wiring.
- `argus/config.yaml` add-on manifest (`homeassistant_api: true`, `map: [data]`,
  `watchdog: tcp://[HOST]:50051`) — add `ingress` keys here.
- `argus/Dockerfile` — base image swap happens here.

### Integration Points
- Program.cs DI container — Kestrel + Minimal API endpoints + `wwwroot` static files wire in
  alongside existing services.
- `argus/config.yaml` — ingress declaration.
- `argus/Dockerfile` — aspnet base image; copy `wwwroot/` assets.
- v2.0 health signal `binary_sensor.argus_addon_health` is the regression check after migration.
</code_context>

<specifics>
## Specific Ideas

No specific UI requirements — Phase 1 placeholder page only needs to prove the Ingress path
resolves (load htmx + a CSS/JS asset via the Ingress URL, render a minimal "Argus" heading).
Real UI begins Phase 2.
</specifics>

<deferred>
## Deferred Ideas

- Supervisor `validate_session` / `IngressAuthMiddleware` — research notes allow deferring full
  auth middleware to Phase 4 (Phase 1–2 MVP may accept from `172.30.32.2`). Probe live
  Supervisor before implementing.
- Entity selection UI, detector-assignment UI, config read/write/reload — Phases 2–3.
</deferred>
