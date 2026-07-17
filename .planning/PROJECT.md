# Argus — Home Assistant Anomaly Detection

## What This Is

A self-hosted, extensible anomaly-detection system for Home Assistant sensor data. It watches environmental sensors (temperature, humidity, pressure — indoor and outdoor) and surfaces anomalies back into HA as auto-created `binary_sensor` (flag) and `sensor` (score) entities via MQTT discovery. Built by one developer for personal home automation use; no cloud, no multi-tenancy.

## Core Value

Anomalies on v1 environmental sensors appear in HA as live binary_sensor + score entities within 2 seconds of a state_changed event, with no manual entity creation and no HA restart required.

## Current State

**Shipped:** v1.0 streaming + batch detection; v2.0 HA add-on (multi-arch GHCR image, Supervisor MQTT creds, health entity, HA WebSocket via Supervisor proxy — live-verified 2026-06-30); v3.0 Ingress Configuration UI (add-on 2.0.9 — sensor discovery + selection, per-entity detector/parameter assignment, hot-reload without restart, MQTT retraction — live bring-up 2026-07-02); **v4.0 Group & Multivariate Anomaly Detection + UX** (group detection both modes, Preact+Vite SPA with guided algorithm chooser, 2-member group support — shipped 2026-07-06). Releases are built locally (buildx → GHCR), not CI.

**Open at v4.0 close:** live-HA UI verification for the SPA (Phases 07/08 `human_needed`) + 10 Phase 08 UAT scenarios deferred pending a planned UI rebuild (Phase 999.1). Backend detection paths verified (Phases 05/06/09 passed). See STATE.md Deferred Items.

**v4.1 progress:** Phases 10 & 11 shipped (Design System foundation + Dashboard/Algorithms). Phase 12 (Sensors screen) complete 2026-07-17 — DS page-header, Card-wrapped groupByArea list, single-select-and-expand row, AlgorithmCard radiogroup detector picker + shared-Input param grid; save/validation flow preserved (D-08). Next: Phase 13 (Groups screen).

## Current Milestone: v4.1 Admin UI Rebuild (Design System)

**Goal:** Replace the functional-but-provisional v4.0 SPA with a pixel-perfect implementation of the Argus Design System across all 5 admin screens, in Preact using existing `argus.css` conventions, with full light/dark mode.

**Target features:**
- Dashboard — KpiTile + recent anomalies + system health (data is mocked; runtime endpoints may not exist yet — mark TODO)
- Algorithms — group detector catalog (peer_divergence/ecod/copod/pca/iforest), presets, "best for…" copy — source `Web/DetectorCatalog.cs`
- Sensors — sensor list, single-sensor detector assignment (hst/mad/stl) with inline validation — source `Web/DetectorDefaults.cs`, `src/validation/detectorParams.ts`
- Groups — group editor + algorithm creation wizard + attribution panel (AttributionBar)
- Settings — global configuration
- Port design-system components to Preact: Button, Input, Select, Checkbox, SearchInput, Textarea, Card, Badge, StatusDot, KpiTile, AttributionBar, Disclosure, Banner, EmptyState, AlgorithmCard, SensitivityPreset, Sidebar

**Key context:**
- Typographic brand only, no logo ("A" tile + "Argus" wordmark)
- Chrome UI in English; HA entity friendly-names in Polish; entity IDs always monospace
- No emoji — status = StatusDot
- Icons: Unicode glyph placeholders for now (swap to Lucide/Material Symbols only if repo adopts an icon set)
- Radio-card selection = 2px accent border (a11y rule), never color alone
- Focus always visible (2px accent outline, 2px offset)
- `data-theme="dark"` first-class
- Motion minimal, respects `prefers-reduced-motion`
- Rebuild against the existing app — reproduce fidelity from `Argus Design System/ui_kits/admin/index.html` using Preact + argus.css patterns, not copy-pasted HTML
- Resolves the v4.0-deferred "UI redesign" item; unblocks the deferred Phase 07/08 live-HA UI verification

Source: `Argus Design System/HANDOFF_TO_CLAUDE_CODE.md` (design reference package, not production code).

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
- ✓ Group detection — peer-divergence: flag the member diverging from its group's collective behavior; attribute WHICH member — v4.0 (GRP-03/04); 2-member pairwise-delta path added v4.0 (GRP-11)
- ✓ Group detection — joint multivariate: flag jointly-abnormal value vectors across a group, with ranked per-feature attribution — v4.0 (GRP-05/06/09); 2-member joint groups supported (GRP-10)
- ✓ Batch groups via InfluxDB resampling (time-alignment on a common grid) — v4.0 (GRP-01/02/08)
- ✓ Expanded algorithm library with a user-friendly chooser (readable presets + "best for" descriptions, guided flow) — v4.0 (ALGO-01..06); "together" default corrected ecod→copod after empirical testing
- ✓ Sensor search by friendly name + categorized area/domain browse + area-scoped suggestions — v4.0 (SRCH-01/02/03)
- ✓ Config UI rebuilt as a light Preact+Vite SPA (built at Docker build-time, no Node in runtime) — v4.0 (UI-01..04); live-HA UI sign-off deferred pending planned redesign

### Active (v4.1)

- [ ] UI rebuild against Argus Design System — Dashboard, Algorithms, Sensors, Groups, Settings in Preact + argus.css, light/dark
- [ ] Complete deferred live-HA UI verification (Phase 07/08) as part of the rebuild

### Future

- [ ] Algorithm tester/simulator in group config UI (Phase 999.1 backlog) — preview detector scores against real sensor history before saving

### Deferred (not yet scheduled)

- [ ] Two-host deployment: orchestrator on edge host, detector on GPU host (Phase 3 GPU — never executed)
- [ ] mTLS on gRPC link between hosts (code path exists; two-host deployment never validated live)
- [ ] Streaming groups (window + last-value-carried-forward) — after batch groups prove the model (STRM-01/02)

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
| Light SPA for UI (v4.0) | Server-rendered htmx too limiting for algorithm chooser + friendly-name search UX | ⚠ Revisit — shipped functionally (Preact+Vite, built at Docker build-time, no runtime Node), but operator intends to redesign; live-HA UI sign-off deferred |
| Group detection both modes (v4.0) | Peer-divergence (median/MAD) for "which member diverges"; joint-multivariate (PyOD ECOD/COPOD/PCA/IForest + RobustScaler) for "jointly abnormal" | ✓ Good — proto carries a real 2D matrix; backend verified (Phases 05/06 passed) |
| Batch-first groups; streaming deferred (v4.0) | Time-alignment across async streams is a distinct hard problem; batch (InfluxDB aggregateWindow+pivot) proves the model first | ✓ Good — STRM-01/02 explicitly deferred |
| 2-member joint floor + pairwise-delta peer path (v4.0 Phase 9) | 3-member floor blocked legitimate 2-member joint groups; 2-member peer_divergence is degenerate → score member_a−member_b with the proven single-entity MAD detector | ✓ Good — 11/11 verification passed |
| Guided "together" default ecod→copod (v4.0 Phase 9) | ECOD/PCA produced ~90% false positives on correlated-pair relationship-breaks; COPOD/IForest correctly distinguished, COPOD preserves attribution | ✓ Good — empirically validated over 10 seeds |

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
*Last updated: 2026-07-17 — Phase 12 (Sensors screen) complete; v4.1 milestone in progress (3/5 screens: Dashboard, Algorithms, Sensors)*
