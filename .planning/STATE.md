---
gsd_state_version: 1.0
milestone: v3.0
milestone_name: Ingress Configuration UI
status: executing
stopped_at: Completed 03-01-PLAN.md
last_updated: "2026-07-01T07:46:44.675Z"
last_activity: 2026-07-01
progress:
  total_phases: 4
  completed_phases: 2
  total_plans: 8
  completed_plans: 6
  percent: 50
---

# Project State: Argus

## Current Status

- Milestone: **v3.0 Ingress Configuration UI** — roadmap created (4 phases), requirements mapped
- Previous: **v2.0 Home Assistant Add-on — SHIPPED & live-verified 2026-06-30**
- Last action: Plan 01-02 complete — SDK migration (Worker → Web), Kestrel 0.0.0.0:8099, X-Ingress-Path middleware, placeholder page + wwwroot assets, config.yaml ingress keys — 2026-06-30 (live-HA verification deferred)

## Project Reference

See: .planning/PROJECT.md

**Core value:** Anomalies appear in HA as live binary_sensor + score entities within 2 seconds.
**Current focus:** Phase 03 — Config Read/Write + Detector Assignment + Reload

## Phase Status (v3.0)

| Phase | Name | Status |
|-------|------|--------|
| 1 | Ingress Scaffold + SDK Migration + Config Seam | Code complete — live-HA verify pending |
| 2 | Live Sensor Discovery + Entity Selection UI | Not started |
| 3 | Config Read/Write + Detector Assignment + Reload | Not started |
| 4 | Validation, CI Packaging + Documentation | Not started |

```
Progress: [████████░░] 75%
```

v1.0 + v2.0 archived under `.planning/milestones/` and `.planning/archive/`.

## Accumulated Context

### v2.0 outcomes (relevant to v3)

- Add-on is a single-container image `ghcr.io/krzyl2/argus` (amd64+arm64), built locally via buildx + QEMU and pushed to GHCR (CI workflow also present in `.github/workflows/build.yml`).
- HA connection: orchestrator uses a raw WebSocket client (`HaWebSocketClient`) to the Supervisor proxy `ws://supervisor/core/websocket` with `Authorization: Bearer SUPERVISOR_TOKEN` on the upgrade — NetDaemon.Client could not (its WS factory is internal); direct `homeassistant:8123` is refused for add-ons.
- Config today: `config-gen` (`10-config-gen.sh`) turns add-on options → env + `/data/entities.yaml`; `gen-entities.py` builds entities **only** from the explicit `entities` list and hardcodes the `hst` detector. `include_patterns`/`exclude_patterns` are currently **ignored** (v3 closes this).
- `EntitiesConfigLoader` already supports per-entity `detectors: [{name, params}]` — the data model for v3's detector-assignment UI exists; the UI + config-gen wiring is what's missing.
- `SelectDiscoverableSensors` + the startup `get_states` discovery already enumerate live numeric sensors — reuse for the v3 selection UI.
- Detector binds `0.0.0.0` in local mode (watchdog reachability); InfluxDB batch path is skipped when `influx_url` is empty (streaming-only).
- Add-on image base: `ghcr.io/home-assistant/base-debian:bookworm`, Python 3.11 (no apt python3.12 on Debian), .NET 8 runtime via `dotnet-install.sh`.

### v3.0 architecture decisions (from research)

- **SDK migration:** `Microsoft.NET.Sdk.Worker` → `Microsoft.NET.Sdk.Web`; `Host.CreateApplicationBuilder` → `WebApplication.CreateBuilder`. All existing `AddHostedService`/`AddSingleton` registrations are identical under `WebApplication` — no service registration changes.
- **Co-host in orchestrator process:** Kestrel + Minimal API inside the existing process; no second s6 service. UI reads same singletons as workers (EntitiesConfig, health signals).
- **UI technology:** Server-rendered HTML + htmx 2.0.10 (14 KB, BSD 0-Clause, committed to `wwwroot/`). No SPA, no Node.js build step, no CDN. Air-gapped safe.
- **Config source of truth:** `/data/entities.yaml` unchanged — UI reads and writes it via `YamlDotNet` (already in project). No new config format.
- **Reload mechanism:** `ILiveEntitiesConfig` singleton with `Interlocked.Exchange` swap + `ConfigChanged` event. `HaListenerWorker` cancels inner CTS (not host-level stoppingToken) and restarts `ScoreStreamPipeline.RunAsync` loop only. MQTT + gRPC transport stays alive. Streaming gap < 1 second.
- **Kestrel bind:** `0.0.0.0:8099` (not loopback). Supervisor connects from `172.30.32.2`.
- **Docker base image:** `mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled` (replaces `runtime:8.0-jammy-chiseled`; ~10 MB larger; same distroless base).
- **No `ports:` entry in config.yaml:** Ingress-only; exposing the port bypasses HA auth.

### Critical pre-conditions (must not be deferred)

- **Phase 1:** `EntitiesConfigLoader.Validate()` must change from `throw` to `LogWarning` on empty entities — otherwise orchestrator crashes on first-boot with no entities configured, blocking the UI from loading.
- **Phase 1:** Atomic config write (temp-then-rename + SemaphoreSlim(1)) must be in place from the start.
- **Phase 2 (start):** `gen-entities.py` guard (`_source: ui` marker or `.ui_config_present` lock file) must land BEFORE the first UI save endpoint is wired — otherwise an add-on restart after a UI save silently erases user config.
- **Phase 3 (before planning):** Source-read `BatchSchedulerWorker` to determine whether it captures `EntitiesConfig.Entities` at construction or per-cycle. This decides whether it is in the "must change" list for Phase 3.

### Live research gaps (resolve during phase)

- **Phase 1:** X-Ingress-Path / UsePathBase conflict — live HA OS test required. Safe implementation: set PathBase per-request AND emit `<base href="{ingressPath}/">`. Verify via "Open Web UI" (never direct port).
- **Phase 2:** Supervisor `validate_session` API shape — probe live Supervisor before implementing `IngressAuthMiddleware`. Fallback: accept from 172.30.32.2 in Phase 2 MVP, complete auth middleware in Phase 4.

### Locked decisions (historical, still in force)

- .NET 8 orchestrator + Python gRPC detector (D2); gRPC mTLS for remote, insecure loopback for local (D4)
- MQTT discovery for HA entity creation (D6); PyOD MAD + STL + River HST detection engines
- Mono-repo: proto/, orchestrator/, detector/, deploy/, argus/ (add-on)
- Licenses: BSD/Apache/MIT only (no GPL, no ADTK/MPL-2.0)

### Plan 01-01 decisions (config seam)

- Null YAML deserialization returns `new EntitiesConfig()` instead of throwing — maintains no-crash guarantee for all first-boot scenarios
- `ConfigWriter` not registered in DI by Plan 01 — Plan 02 owns `Program.cs` to avoid parallel-wave file conflict
- `ConfigWriter.WriteAsync` writes verbatim strings; YAML serialization deferred to Phase 2+ callers (keeps writer focused and testable)

### Plan 01-02 decisions (SDK migration + Kestrel + Ingress scaffold)

- Kestrel bound via `ConfigureKestrel(IPAddress.Any, 8099)` — not `UseUrls` or `ASPNETCORE_URLS`
- Dual PathBase + `<base href>` defense handles both Supervisor-strips and Supervisor-does-not-strip behaviors (STACK-vs-PITFALLS conflict deferred to live-HA verification)
- `ArgusHealthSignals.DetectorConnected` volatile bool added — cached by HealthPublisherWorker every ~15 s for zero-latency UI reads (no gRPC call on page load)
- `ingressPath` HTML-encoded via `WebUtility.HtmlEncode` before `<base href>` interpolation (T-01-08)
- argus/Dockerfile unchanged — add-on uses base-debian:bookworm + dotnet-install.sh; Web SDK publish carries ASP.NET DLLs

### Decisions

- [Phase 02-03]: Root GET / redirects to /sensors; placeholder replaced by entity picker
- [Phase 02-03]: Single YamlDotNet root-dict serialization (_patterns + entities) — never string-format YAML (T-02-08)
- [Phase 02-03]: Empty checkbox selection writes entities: [] (valid, Pitfall 5)
- [Phase 02-03]: Interim auth: X-Ingress-Path OR RemoteIpAddress=172.30.32.2/loopback (T-02-09); Phase 4 completes validate_session
- [Phase ?]: RetractAsync delegate overload for testability — mirrors PublishAllAsync pattern, avoids IMqttConnection interface

### Blockers

- None

## Performance Metrics

| Metric | Target | Current |
|--------|--------|---------|
| Plans completed | — | 0/TBD |
| Phases completed | 4 | 0/4 |
| Requirements mapped | 9/9 | 9/9 |
| Phase 01 P01-01 | 2 | 2 tasks | 5 files |
| Phase 01 P01-02 | 231 | - tasks | - files |
| Phase 02 P02-01 | 4m | 3 tasks | 7 files |
| Phase 02 P02-02 | 8m | 2 tasks | 3 files |
| Phase 02 P02-03 | 5m | 2 tasks | 6 files |
| Phase 03 P03-01 | 10m | 2 tasks | 5 files |

## Session Continuity

**Last session:** 2026-07-01T07:46:44.668Z
**Stopped at:** Completed 03-01-PLAN.md
**Resume file:** None

- Last session: 2026-06-30 — Plan 01-02 complete: SDK migration (Worker → Web), Kestrel 0.0.0.0:8099, X-Ingress-Path PathBase middleware, placeholder page (PlaceholderPage.cs), wwwroot assets (htmx 2.0.10, argus.css), config.yaml ingress keys. Live-HA verification deferred to operator.
- Resume point: Live-HA verification per 01-02-SUMMARY.md "Pending Live-HA Verification" section, then Phase 2 planning.

## Operator Next Steps

1. PENDING: Live-HA verification of Plan 01-02 — rebuild and deploy add-on, confirm "Open Web UI", assets HTTP 200, ss -tlnp shows 0.0.0.0:8099, binary_sensor.argus_addon_health healthy. Record X-Ingress-Path strip behavior to close STACK-vs-PITFALLS conflict (see 01-02-SUMMARY.md).
2. Before Phase 2 save endpoint: land `gen-entities.py` guard (`_source: ui` marker) to prevent add-on restart erasing UI config.
3. Phase 2 planning: live sensor discovery + entity selection UI.

## Current Position

Phase: 03 (Config Read/Write + Detector Assignment + Reload) — EXECUTING
Plan: 2 of 3
Status: Ready to execute
Last activity: 2026-07-01
