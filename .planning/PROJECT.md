# Argus — Home Assistant Anomaly Detection

## What This Is

A self-hosted, extensible anomaly-detection system for Home Assistant sensor data. It watches environmental sensors (temperature, humidity, pressure — indoor and outdoor) and surfaces anomalies back into HA as auto-created `binary_sensor` (flag) and `sensor` (score) entities via MQTT discovery. Built by one developer for personal home automation use; no cloud, no multi-tenancy.

## Core Value

Anomalies on v1 environmental sensors appear in HA as live binary_sensor + score entities within 2 seconds of a state_changed event, with no manual entity creation and no HA restart required.

## Current State

**Shipped:** v1.0 streaming + batch detection; v2.0 HA add-on (multi-arch GHCR image, Supervisor MQTT creds, health entity, HA WebSocket via Supervisor proxy — live-verified 2026-06-30); **v3.0 Ingress Configuration UI** (add-on 2.0.9 — sensor discovery + selection, per-entity detector/parameter assignment, hot-reload without restart, MQTT retraction — live bring-up 2026-07-02). Releases are built locally (buildx → GHCR), not CI.

## Next Milestone: v4.0 Group & Multivariate Anomaly Detection + UX

**Goal:** Analyze groups of sensors, not just single ones, and make algorithm selection user-friendly. This expands Argus from single-sensor environmental monitoring toward a general relational anomaly platform.

**Target features:**
- **Group detection, both modes:** peer-divergence (which member diverges from its group — e.g. one tire pressure rising unlike the others) AND joint multivariate (values jointly abnormal — e.g. room humidity → leak).
- **Batch-first** (InfluxDB resampling for time-alignment; InfluxDB confirmed available); streaming groups later.
- **More algorithms** + user-friendly chooser: readable parameter presets (Sensitivity Low/Med/High) with Advanced toggle; "best for…" descriptions per algorithm.
- **Search by friendly name** (today only entity_id); modern, readable UI (approach — htmx+CSS vs light SPA — decided in v4.0 planning).

Model already has `EntityConfig.Groups`/`Covariates` placeholders (parsed-and-ignored today, D-09); proto is univariate and needs a multi-series extension.

---

## Shipped: v2.0 Home Assistant Add-on (2026-06-30)

**Goal:** Argus installable via HA add-on store ("custom repository") — install and configure entirely through the UI, with no manual tokens, `.env` files, or config-file editing.

**Target features:**
- Add-on packaging: `repository.yaml` + add-on folder (`config.yaml` + options schema), HA base image with s6 running both processes in one container, multi-arch build (amd64 + aarch64).
- Local detector by default (loopback, no mTLS); optional external detector via configurable `detector_endpoint` URL (mTLS retained for the remote path). Single add-on, not two.
- UI-driven config: list of `entity_id` in the options form; InfluxDB settings (url/token/org/bucket + measurement/value_field); streaming + batch both in scope.
- Auto auth: HA via `SUPERVISOR_TOKEN` (`homeassistant_api`), MQTT via Supervisor service discovery; `entities.yaml` generated at startup from `options.json`.

**Milestone decisions (override locked v1 constraints — intentional):**
- **D4 (mTLS):** now conditional — bypassed on loopback (local detector), retained for the remote `detector_endpoint` path.
- **D2/D17 (two-host):** default is a single container; host↔detector split remains available via `detector_endpoint`.
- **Distribution:** the add-on requires HA OS / Supervised; the existing `docker compose` path stays for HA Container/Core and for a remote detector.

## Requirements

### Validated

- ✓ End-to-end streaming path: HA WebSocket → gRPC ScoreStream → MQTT → HA entity, latency < 2 s — v1.0
- ✓ Batch detection path: InfluxDB history → gRPC Fit/ScoreBatch → MQTT → HA entity — v1.0
- ✓ MQTT discovery with stable unique_id; binary_sensor + score sensor grouped per source entity — v1.0
- ✓ Per-entity model lifecycle: Fit, Save, Load; keyed by entity_id + detector + version — v1.0
- ✓ Config-driven entities; adding entity requires only config edit, no redeploy — v1.0 (v3.0: via UI, no YAML)
- ✓ Detectors: MAD, River Half-Space Trees (streaming), STL seasonal-residual — v1.0 (RobustZScore N/A in PyOD 3.6 → MAD)
- ✓ Per-entity calibration with hysteresis (anti-flapping) — v1.0
- ✓ Graceful degradation: detector unreachable → anomaly sensors `unavailable`, not false `off` — v1.0
- ✓ Restart resilience: components restart independently without losing model state or orphaning HA entities — v1.0
- ✓ Installable HA add-on (single container, Supervisor auth, multi-arch) — v2.0
- ✓ Ingress config UI: discover/select sensors, assign detectors+params, hot-reload without restart — v3.0

### Active (v4.0)

- [ ] Group detection — peer-divergence: flag the member diverging from its group's collective behavior; attribute WHICH member
- [ ] Group detection — joint multivariate: flag jointly-abnormal value vectors across a group
- [ ] Batch groups via InfluxDB resampling (time-alignment on a common grid)
- [ ] Expanded algorithm library with a user-friendly chooser (readable presets + "best for" descriptions)
- [ ] Sensor search by friendly name (today only entity_id) + categorized long list
- [ ] Modern, readable UI (approach decided in v4.0 planning)

### Deferred (not yet scheduled)

- [ ] Two-host deployment: orchestrator on edge host, detector on GPU host (Phase 3 GPU — never executed)
- [ ] mTLS on gRPC link between hosts (code path exists; two-host deployment never validated live)
- [ ] Streaming groups (window + last-value-carried-forward) — after batch groups prove the model

### Out of Scope

- Image/camera anomaly detection (Anomalib) — only if camera data added later
- Acting on anomalies (notifications, automations) — Argus only exposes entities; operator wires reactions in HA/Node-RED
- Custom HA dashboards — auto-created entities are sufficient
- ML.NET detection — all ML is Python (D2)
- Cloud services — self-hosted only (D9)
- Multi-user / remote-access concerns — single operator

## Context

- **Hosts:** Edge host (HA + Orchestrator), GPU host (Python detector + CUDA). Communicate over LAN.
- **Data sources:** HA WebSocket API (`state_changed`) for streaming; InfluxDB for history/backfill. Do NOT read recorder DB directly.
- **Data sink:** MQTT broker (reuses existing Zigbee2MQTT broker) with homeassistant/ discovery prefix.
- **Detection libraries:** PyOD (BSD-2), River (BSD-3), Darts (Apache-2.0) — all permissive. No GPL.
- **v1 entities:** outdoor temp/humidity/pressure; per-room temp/humidity (all rooms).
- **Repo layout:** mono-repo with `proto/`, `orchestrator/` (.NET 8), `detector/` (Python), `deploy/`.
- **Open questions before Phase 1:** exact HA entity_ids (Q1), InfluxDB location + retention (Q2).

## Constraints

- **Architecture:** .NET 8 orchestrator + Python gRPC detector — locked (D2). All ML in Python.
- **Transport:** gRPC over LAN with mTLS (D4). MQTT is documented fallback only.
- **Languages:** Code/identifiers in English; HA entity friendly-names in Polish (D8).
- **Licenses:** BSD/Apache/MIT only. No GPL, no ADTK unless isolated (MPL-2.0).
- **Hosting:** Self-hosted, no cloud (D9).
- **GPU:** Phase 3 only; Phase 1–2 are CPU-only and must work without GPU.

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| .NET 8 orchestrator + Python detector (D2) | .NET handles I/O/scheduling; Python owns all ML via mature libs | ✓ Good — clean seam held through v1-v3 |
| gRPC with mTLS for edge↔detector (D4) | Strongly typed, streaming + unary, .NET↔Python interop | ✓ Good; v2.0 made mTLS conditional (loopback insecure / remote mTLS) |
| MQTT discovery for HA egress (D6) | Idempotent, survives restarts, no HA restart needed | ✓ Good — retraction added in v3.0 |
| PyOD + River + Darts as detection engines (D10) | Reuse permissive-licensed mature libraries | ✓ Good (Darts unused so far; RobustZScore N/A → MAD) |
| Per-entity models on disk (D7) | joblib/pickle for PyOD; pickle for River HST | ✓ Good — entity_id.txt sidecar added for slug round-trip |
| Mono-repo layout | Single repo for proto, orchestrator, detector, deploy | ✓ Good |
| Local buildx→GHCR release (not CI) | Operator builds+pushes locally; version==image tag | ✓ Good — v3.0 releases shipped this way |
| Orchestrator on aspnet base (v3.0) | Web SDK app needs Microsoft.AspNetCore.App, not plain runtime | ✓ Good — fixed 2.0.7 (both add-on + standalone Dockerfiles) |
| Empty include patterns select nothing, not all (v3.0) | Checkbox-driven selection; empty=all flooded HA with ~400 entities | ✓ Good — fixed 2.0.9, GlobExpander semantics changed |

## Evolution

This document evolves at phase transitions and milestone boundaries.

**After each phase transition** (via `/gsd-transition`):
1. Requirements invalidated? → Move to Out of Scope with reason
2. Requirements validated? → Move to Validated with phase reference
3. New requirements emerged? → Add to Active
4. Decisions to log? → Add to Key Decisions
5. "What This Is" still accurate? → Update if drifted

**After each milestone** (via `/gsd-complete-milestone`):
1. Full review of all sections
2. Core Value check — still the right priority?
3. Audit Out of Scope — reasons still valid?
4. Update Context with current state

---
*Last updated: 2026-07-02 — after v3.0 (Ingress Configuration UI) milestone; v4.0 (Group & Multivariate Detection + UX) planned*
