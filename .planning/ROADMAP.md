# Roadmap: Argus

## Milestones

- ✅ **v1.0 Foundations + Batch & Model Lifecycle** — Phases 1-2 (shipped 2026-06-10)
- ✅ **v2.0 Home Assistant Add-on** — Phases 1-4 (shipped 2026-06-30)
- 📋 **v3.0 Ingress Configuration UI** — Phases 1-4 (planned)

## Phases

<details>
<summary>✅ v1.0 — Foundations + Batch & Model Lifecycle (Phases 1-2) — SHIPPED 2026-06-10</summary>

All 14 plans complete, 34 requirements covered. Code review clean.
Artifacts archived under `.planning/archive/v1.0/`.

- [x] **Phase 1: Foundations + Streaming** — mono-repo, mTLS gRPC, HA WebSocket ingestion, River HST streaming detector, MQTT discovery, ScoreStreamPipeline with hysteresis
- [x] **Phase 2: Batch Path + Model Lifecycle** — InfluxDB reader, PyOD MAD + STL, ModelStore, BatchSchedulerWorker, per-entity model persistence

</details>

<details>
<summary>✅ v2.0 — Home Assistant Add-on (Phases 1-4) — SHIPPED 2026-06-30</summary>

Argus installable via the HA add-on store and configurable through the HA UI — no manual
tokens, `.env` files, or hand-edited config required. Full detail archived in
`.planning/milestones/v2.0-ROADMAP.md`.

- [x] **Phase 1: Add-on Skeleton + Config-Gen** — repository.yaml + Supervisor-valid schema + EN/PL translations + config-gen seam (options.json → env + entities.yaml) + torch-free Dockerfile — completed 2026-06-30
- [x] **Phase 2: v1 Code Changes** — conditional gRPC security (http→insecure / https→mTLS) + configurable detector bind/model_root — completed 2026-06-30
- [x] **Phase 3: Process Supervision + Runtime Integration** — s6 longrun services, detector readiness gate, live Supervisor MQTT credentials, composite health entity — completed 2026-06-30
- [x] **Phase 4: Multi-Arch CI + Integration + Documentation** — multi-arch GHCR image, image-facts gates, DOCS.md — completed 2026-06-30

**Live-verified on a real HA OS install (2026-06-30):** multi-arch image (`ghcr.io/krzyl2/argus`,
amd64+arm64) pulls and runs; both s6 services start with the readiness gate; the orchestrator
authenticates to HA via the Supervisor proxy (`ws://supervisor/core/websocket` + Bearer header on
a raw WebSocket client); MQTT connects with live Supervisor credentials; `binary_sensor.argus_addon_health`
reads OFF (healthy); startup discovery logged 412 unconfigured numeric sensors.

</details>

### 📋 v3.0 Ingress Configuration UI (Planned)

**Milestone Goal:** Replace hand-edited YAML config with a Home Assistant **Ingress** web UI served
by the add-on ("Open Web UI"): discover HA sensors and pick which Argus tracks, and assign which
detector algorithm(s) + parameters apply to each sensor — no manual `entities` list, and changes
apply without an add-on restart.

- [ ] Phase 1: Ingress scaffold + config persistence seam (TBD plans)
- [ ] Phase 2: Entity discovery + selection UI (TBD plans)
- [ ] Phase 3: Per-entity detector/parameter assignment UI (TBD plans)
- [ ] Phase 4: Validation, reload-without-restart, docs & CI (TBD plans)

## Phase Details

### Phase 1: Ingress scaffold + config persistence seam
**Goal**: The add-on exposes an Ingress web endpoint ("Open Web UI") served by the orchestrator
(ASP.NET minimal API behind `ingress: true` / `ingress_port` in config.yaml; auth handled by HA
Ingress), backed by a single configuration source of truth under `/data` that both the UI and the
orchestrator's `EntitiesConfigLoader` read.
**Depends on**: v2.0
**Requirements**: UI-01, CFG-01
**Success Criteria**:
  1. The add-on page shows "Open Web UI"; opening it serves the Argus UI through HA Ingress with no separate login.
  2. The UI reads current config and writes changes to a `/data` config file; the orchestrator loads from the same file.
  3. No new publicly exposed port — Ingress-only.

### Phase 2: Entity discovery + selection UI
**Goal**: The UI lists candidate HA sensors (reusing `get_states` + `SelectDiscoverableSensors`,
filterable) and lets the user select which entities Argus tracks — replacing the manual `entities`
list and wiring `include_patterns`/`exclude_patterns` into actual selection (closing the v2.0 gap).
**Depends on**: Phase 1
**Requirements**: UI-02, CFG-02
**Success Criteria**:
  1. The UI shows live numeric sensors with current values; the user checks/unchecks which to track and saves.
  2. include/exclude patterns are honored as selection filters.
  3. Saving updates the tracked-entity set consumed by the orchestrator.

### Phase 3: Per-entity detector/parameter assignment UI
**Goal**: For each tracked entity, the UI assigns one or more detectors (HST streaming, PyOD MAD,
STL, …) with parameters, persisted into the per-entity `detectors:` structure the orchestrator
already supports (currently hardcoded to `hst` in config-gen).
**Depends on**: Phase 2
**Requirements**: UI-03, CFG-03
**Success Criteria**:
  1. The UI offers available detector types with editable parameters per entity.
  2. Assignments persist in the structure `EntitiesConfigLoader` expects (multiple detectors per entity allowed).
  3. Sane defaults when a detector/param is unset.

### Phase 4: Validation, reload-without-restart, docs & CI
**Goal**: Config changes apply without an add-on restart (orchestrator reloads entity config on
change), UI inputs are validated, and the add-on ships UI docs + CI packaging of UI assets.
**Depends on**: Phase 3
**Requirements**: UI-04, CFG-04, DOCS-02
**Success Criteria**:
  1. Saving in the UI applies to the running orchestrator within seconds, no add-on restart.
  2. Invalid input (bad entity_id, out-of-range params) is rejected with a clear message.
  3. DOCS.md documents the UI; the multi-arch image bundles the UI assets and stays under 2 GB.

## Progress

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 1-2. Foundations + Batch/Model Lifecycle | v1.0 | 14/14 | Complete | 2026-06-10 |
| 1. Add-on Skeleton + Config-Gen | v2.0 | 3/3 | Complete | 2026-06-30 |
| 2. v1 Code Changes | v2.0 | 2/2 | Complete | 2026-06-30 |
| 3. Process Supervision + Runtime Integration | v2.0 | 3/3 | Complete | 2026-06-30 |
| 4. Multi-Arch CI + Integration + Documentation | v2.0 | 2/2 | Complete | 2026-06-30 |
| 1. Ingress scaffold + config seam | v3.0 | 0/TBD | Not started | - |
| 2. Entity discovery + selection UI | v3.0 | 0/TBD | Not started | - |
| 3. Per-entity detector assignment UI | v3.0 | 0/TBD | Not started | - |
| 4. Validation + reload + docs/CI | v3.0 | 0/TBD | Not started | - |
