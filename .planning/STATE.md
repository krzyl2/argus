---
gsd_state_version: 1.0
milestone: v3.0
milestone_name: Ingress Configuration UI
current_phase: 0
status: Planning
last_updated: "2026-06-30T16:30:00.000Z"
last_activity: 2026-06-30
last_activity_desc: v2.0 shipped & live-verified; v3.0 (Ingress UI) roadmap drafted
progress:
  total_phases: 4
  completed_phases: 0
  total_plans: 0
  completed_plans: 0
  percent: 0
current_phase_name: Ingress scaffold + config persistence seam
---

# Project State: Argus

## Current Status

- Milestone: **v3.0 Ingress Configuration UI** — roadmap drafted (4 phases), requirements outlined
- Previous: **v2.0 Home Assistant Add-on — SHIPPED & live-verified 2026-06-30**
- Last action: Closed/archived v2.0; drafted v3.0 roadmap + requirements — 2026-06-30

## Project Reference

See: .planning/PROJECT.md

**Core value:** Anomalies appear in HA as live binary_sensor + score entities within 2 seconds.
**Current focus:** v3.0 Phase 1 — Ingress scaffold + config persistence seam (refine via /gsd-discuss-phase)

## Phase Status (v3.0)

| Phase | Name | Status |
|-------|------|--------|
| 1 | Ingress scaffold + config persistence seam | Not started |
| 2 | Entity discovery + selection UI | Not started |
| 3 | Per-entity detector/parameter assignment UI | Not started |
| 4 | Validation + reload-without-restart + docs/CI | Not started |

v1.0 + v2.0 archived under `.planning/milestones/` and `.planning/archive/`.

## Accumulated Context

### v2.0 outcomes (relevant to v3)

- Add-on is a single-container image `ghcr.io/krzyl2/argus` (amd64+arm64), built locally via buildx + QEMU and pushed to GHCR (CI workflow also present in `.github/workflows/build.yml`).
- HA connection: orchestrator uses a raw WebSocket client (`HaWebSocketClient`) to the Supervisor proxy `ws://supervisor/core/websocket` with `Authorization: Bearer SUPERVISOR_TOKEN` on the upgrade — NetDaemon.Client could not (its WS factory is internal); direct `homeassistant:8123` is refused for add-ons.
- Config today: `config-gen` (`10-config-gen.sh`) turns add-on options → env + `/data/entities.yaml`; `gen-entities.py` builds entities **only** from the explicit `entities` list and hardcodes the `hst` detector. `include_patterns`/`exclude_patterns` are currently **ignored** (v3 closes this).
- `EntitiesConfigLoader` already supports per-entity `detectors: [{name, params}]` — the data model for v3's detector-assignment UI exists; the UI + config-gen wiring is what's missing.
- `SelectDiscoverableSensors` + the startup `get_states` discovery (UICFG-05) already enumerate live numeric sensors — reuse for the v3 selection UI.
- Detector binds `0.0.0.0` in local mode (watchdog reachability); InfluxDB batch path is skipped when `influx_url` is empty (streaming-only).
- Add-on image base: `ghcr.io/home-assistant/base-debian:bookworm`, Python 3.11 (no apt python3.12 on Debian), .NET 8 runtime via `dotnet-install.sh`.

### Locked decisions (historical, still in force)

- .NET 8 orchestrator + Python gRPC detector (D2); gRPC mTLS for remote, insecure loopback for local (D4)
- MQTT discovery for HA entity creation (D6); PyOD MAD + STL + River HST detection engines
- Mono-repo: proto/, orchestrator/, detector/, deploy/, argus/ (add-on)

### Blockers

- None

## Session Continuity

- Last session: 2026-06-30 — debugged the add-on to a working live HA install, shipped v2.0, drafted v3.0.
- Resume point: refine v3.0 requirements/scope, then `/gsd-discuss-phase 1` (or `/gsd-new-milestone` to formalize v3.0).

## Operator Next Steps

- Optionally test the v2.0 anomaly E2E further (change a tracked sensor sharply → binary_sensor flips ON).
- Start v3.0: `/gsd-new-milestone` (formal questioning→research→requirements→roadmap) OR `/gsd-discuss-phase 1` to begin the Ingress UI directly from the drafted roadmap.
