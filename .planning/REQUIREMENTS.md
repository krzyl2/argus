# Requirements: Argus — v3.0 Ingress Configuration UI

> v1.0 requirements archived in `.planning/milestones/v1.0-REQUIREMENTS.md`.
> v2.0 requirements archived in `.planning/milestones/v2.0-REQUIREMENTS.md`.
>
> **Status:** Roadmap created 2026-06-30. 9/9 v3.0 requirements mapped to phases.

## Milestone Goal

Replace hand-edited YAML configuration with a Home Assistant **Ingress** web UI served by the
add-on: discover HA sensors and select which Argus tracks, assign detector algorithm(s) +
parameters per sensor, and apply changes without an add-on restart.

## Requirements

### UI — Ingress web interface
- [x] **UI-01** — The add-on exposes an Ingress endpoint ("Open Web UI") served by the orchestrator (ASP.NET minimal API behind `ingress: true` / `ingress_port`), authenticated by HA Ingress, with no separately exposed port.
- [x] **UI-02** — The UI lists live HA numeric sensors (reusing `get_states` + `SelectDiscoverableSensors`), filterable, and lets the user select which entities Argus tracks.
- [ ] **UI-03** — The UI assigns one or more detectors (HST, MAD, STL, …) with editable parameters to each tracked entity.
- [ ] **UI-04** — UI inputs are validated (entity_id format, parameter ranges) with clear error messages before save.

### CFG — Configuration model & lifecycle
- [x] **CFG-01** — A single configuration source of truth under `/data` is read by both the UI and the orchestrator's `EntitiesConfigLoader`.
- [ ] **CFG-02** — Entity selection (incl. `include_patterns`/`exclude_patterns` honored as filters) persists to the config and is consumed by the orchestrator — replacing the manual `entities` list and closing the v2.0 patterns-ignored gap.
- [ ] **CFG-03** — Per-entity detector/parameter assignments persist in the structure `EntitiesConfigLoader` expects (multiple detectors per entity supported; sane defaults when unset).
- [ ] **CFG-04** — Configuration changes apply to the running orchestrator within seconds via reload, without restarting the add-on.

### DOCS
- [ ] **DOCS-02** — DOCS.md documents the Ingress UI (open, select entities, assign detectors); the multi-arch image bundles UI assets and stays under 2 GB.

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| UI-01 | v3 Phase 1 | Not started |
| CFG-01 | v3 Phase 1 | Not started |
| UI-02 | v3 Phase 2 | Not started |
| CFG-02 | v3 Phase 2 | Not started |
| UI-03 | v3 Phase 3 | Not started |
| CFG-03 | v3 Phase 3 | Not started |
| CFG-04 | v3 Phase 3 | Not started |
| UI-04 | v3 Phase 4 | Not started |
| DOCS-02 | v3 Phase 4 | Not started |

## Open Questions (resolve during discuss)

- Q1: UI tech — server-rendered minimal pages vs a small SPA bundled in the image? (image-size budget, build complexity) — **Research recommendation: server-rendered HTML + htmx 2.0.10; no SPA, no Node.js build step.**
- Q2: Config file format — extend the existing `entities.yaml`, or a richer JSON the UI owns and config-gen/loader read? — **Research recommendation: keep `/data/entities.yaml` as the single source of truth; UI writes the same YAML via YamlDotNet.**
- Q3: Reload mechanism — file-watch + in-place reconfigure of the streaming pipeline, vs orchestrator self-restart on config change? — **Research recommendation: ILiveEntitiesConfig atomic swap + HaListenerWorker inner-CTS restart loop (not host restart). See Phase 3.**
- Q4: How detector parameters are surfaced/validated per detector type (schema-driven form?). — **Research recommendation: typed parameter fields with defaults shown; validation rules derived from existing HstParams/DetectorConfig constraints.**
