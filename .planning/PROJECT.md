# Argus — Home Assistant Anomaly Detection

## What This Is

A self-hosted, extensible anomaly-detection system for Home Assistant sensor data. It watches environmental sensors (temperature, humidity, pressure — indoor and outdoor) and surfaces anomalies back into HA as auto-created `binary_sensor` (flag) and `sensor` (score) entities via MQTT discovery. Built by one developer for personal home automation use; no cloud, no multi-tenancy.

## Core Value

Anomalies on v1 environmental sensors appear in HA as live binary_sensor + score entities within 2 seconds of a state_changed event, with no manual entity creation and no HA restart required.

## Current Milestone: v2.0 Home Assistant Add-on

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

(None yet — ship to validate)

### Active

- [ ] End-to-end streaming path: HA WebSocket → gRPC ScoreStream → MQTT → HA entity, latency < 2 s
- [ ] Batch detection path: InfluxDB history → gRPC Fit/ScoreBatch → MQTT → HA entity
- [ ] MQTT discovery with stable unique_id; binary_sensor + score sensor grouped per source entity
- [ ] Per-entity model lifecycle: Fit, Save, Load; keyed by entity_id + detector + version
- [ ] Config-driven entities via entities.yaml; adding entity requires only config edit, no redeploy
- [ ] Detectors: RobustZScore/MAD, River Half-Space Trees (streaming), STL seasonal-residual
- [ ] Per-entity calibration with hysteresis (anti-flapping)
- [ ] Graceful degradation: if detector host unreachable, anomaly sensors go `unavailable` (not false `off`)
- [ ] Restart resilience: all components restart independently without losing model state or orphaning HA entities
- [ ] Two-host deployment: orchestrator on edge host, detector on GPU host
- [ ] mTLS on gRPC link between hosts

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
| .NET 8 orchestrator + Python detector (D2) | .NET handles I/O/scheduling; Python owns all ML via mature libs | — Pending |
| gRPC with mTLS for edge↔detector (D4) | Strongly typed, supports streaming + unary, excellent .NET↔Python interop | — Pending |
| MQTT discovery for HA egress (D6) | Idempotent, survives restarts, no HA restart needed | — Pending |
| PyOD + River + Darts as detection engines (D10) | No hand-rolled detectors; reuse permissive-licensed mature libraries | — Pending |
| Per-entity models on disk on GPU host (D7) | joblib/pickle for PyOD; native save for River/Darts | — Pending |
| Mono-repo layout (Section 5.5) | Single repo for proto, orchestrator, detector, deploy | — Pending |

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
*Last updated: 2026-06-29 — milestone v2.0 (Home Assistant Add-on) started*
