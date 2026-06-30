---
gsd_state_version: 1.0
milestone: v3.0
milestone_name: Ingress Configuration UI
status: planning
last_updated: "2026-06-30T00:00:00.000Z"
last_activity: 2026-06-30
progress:
  total_phases: 4
  completed_phases: 0
  total_plans: 0
  completed_plans: 0
  percent: 0
---

# Project State: Argus

## Current Status

- Milestone: **v3.0 Ingress Configuration UI** — roadmap created (4 phases), requirements mapped
- Previous: **v2.0 Home Assistant Add-on — SHIPPED & live-verified 2026-06-30**
- Last action: Roadmap created for v3.0 — 2026-06-30

## Project Reference

See: .planning/PROJECT.md

**Core value:** Anomalies appear in HA as live binary_sensor + score entities within 2 seconds.
**Current focus:** v3.0 Phase 1 — Ingress Scaffold + SDK Migration + Config Seam

## Phase Status (v3.0)

| Phase | Name | Status |
|-------|------|--------|
| 1 | Ingress Scaffold + SDK Migration + Config Seam | Not started |
| 2 | Live Sensor Discovery + Entity Selection UI | Not started |
| 3 | Config Read/Write + Detector Assignment + Reload | Not started |
| 4 | Validation, CI Packaging + Documentation | Not started |

```
Progress: [                    ] 0% (0/4 phases)
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

### Blockers

- None

## Performance Metrics

| Metric | Target | Current |
|--------|--------|---------|
| Plans completed | — | 0/TBD |
| Phases completed | 4 | 0/4 |
| Requirements mapped | 9/9 | 9/9 |

## Session Continuity

- Last session: 2026-06-30 — v3.0 roadmap created from research + requirements.
- Resume point: `/gsd-discuss-phase 1` to refine Phase 1 plans, or `/gsd-plan-phase 1` to begin planning directly.

## Operator Next Steps

1. Run `/gsd-discuss-phase 1` (recommended) to refine Phase 1 scope and identify the exact plan breakdown before implementation.
2. Alternatively, run `/gsd-plan-phase 1` to generate executable plans directly from the Phase 1 roadmap entry.
3. During Phase 1 planning/execution: live-test the X-Ingress-Path / UsePathBase behavior on the real HA OS instance to resolve the STACK vs PITFALLS conflict.

## Current Position

Phase: 1 — Ingress Scaffold + SDK Migration + Config Seam
Plan: —
Status: Not started
Last activity: 2026-06-30 — Roadmap created
