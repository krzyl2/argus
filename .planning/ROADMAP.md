# Roadmap: Argus

## Milestones

- ✅ **v1.0 Foundations + Batch & Model Lifecycle** — Phases 1-2 (shipped 2026-06-10)
- ✅ **v2.0 Home Assistant Add-on** — Phases 1-4 (shipped 2026-06-30)
- ✅ **v3.0 Ingress Configuration UI** — Phases 1-4 (shipped 2026-07-02)
- 📋 **v4.0 Group & Multivariate Anomaly Detection + UX** — phases TBD (planned)

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

Argus installable via the HA add-on store and configurable through the HA UI. Full detail in
`.planning/milestones/v2.0-ROADMAP.md`.

- [x] **Phase 1: Add-on Skeleton + Config-Gen** — repository.yaml + Supervisor schema + config-gen seam + torch-free Dockerfile
- [x] **Phase 2: v1 Code Changes** — conditional gRPC security (http→insecure / https→mTLS) + configurable detector bind/model_root
- [x] **Phase 3: Process Supervision + Runtime Integration** — s6 longrun services, detector readiness gate, live Supervisor MQTT credentials, composite health entity
- [x] **Phase 4: Multi-Arch CI + Integration + Documentation** — multi-arch GHCR image, image-facts gates, DOCS.md

**Live-verified on real HA OS (2026-06-30).**

</details>

<details>
<summary>✅ v3.0 — Ingress Configuration UI (Phases 1-4) — SHIPPED 2026-07-02</summary>

Replace hand-edited YAML with an HA Ingress web UI: discover sensors, pick which Argus tracks,
assign detectors + parameters per sensor, applied without add-on restart. Full detail in
`.planning/milestones/v3.0-ROADMAP.md`.

- [x] **Phase 1: Ingress Scaffold + SDK Migration + Config Seam** — SDK Worker→Web, Kestrel 0.0.0.0:8099, config.yaml ingress keys, empty-entities crash fix, atomic write seam
- [x] **Phase 2: Live Sensor Discovery + Entity Selection UI** — IHaSensorRegistry, /api/sensors, filterable entity picker, include/exclude pattern wiring, gen-entities.py guard
- [x] **Phase 3: Config Read/Write + Detector Assignment + Reload** — ILiveEntitiesConfig atomic swap, detector/parameter UI, HaListenerWorker inner-CTS restart loop, MQTT retraction (CFG-04 hot-reload)
- [x] **Phase 4: Validation, CI Packaging + Documentation** — server+client validation, CI image-size gate, FileSystemWatcher debounce, DOCS.md

**Live-verified on real HA OS (2026-07-02):** add-on 2.0.9 starts (orchestrator + detector + Ingress UI);
HA WebSocket connects; UI serves; entity save + hot-reload work. Formal UAT (8 items) deferred by operator
decision at close — see STATE.md Deferred Items. Three real-world fixes found during live bring-up:
aspnet runtime (2.0.7), ScoreStreamPipeline DI (2.0.8), GlobExpander empty-pattern semantics (2.0.9).

</details>

### 📋 v4.0 Group & Multivariate Anomaly Detection + UX (Planned)

**Milestone Goal:** Analyze groups of sensors, not just single ones — both peer-divergence (which
member diverges from its group, e.g. one tire pressure rising unlike the others) and joint
multivariate (values jointly abnormal, e.g. room humidity → leak). Support more algorithms with a
user-friendly chooser (readable parameter presets + "best for" descriptions), search sensors by
friendly name, and a modern readable UI.

**Locked scope decisions (2026-07-02):**
- Both group modes (peer-divergence + joint multivariate) in parallel.
- Batch-first (InfluxDB resampling for time-alignment); streaming groups later. InfluxDB confirmed available.
- UI approach (htmx+CSS redesign vs light SPA) decided during v4.0 planning.

Phases to be defined via `/gsd-new-milestone`. Model already has `EntityConfig.Groups`/`Covariates`
placeholders (parsed-and-ignored today, D-09); proto is univariate and needs a multi-series extension.

## Progress

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 1-2. Foundations + Batch/Model Lifecycle | v1.0 | 14/14 | Complete | 2026-06-10 |
| 1. Add-on Skeleton + Config-Gen | v2.0 | 2/2 | Complete | 2026-06-30 |
| 2. v1 Code Changes | v2.0 | 3/3 | Complete | 2026-06-30 |
| 3. Process Supervision + Runtime Integration | v2.0 | 3/3 | Complete | 2026-06-30 |
| 4. Multi-Arch CI + Integration + Documentation | v2.0 | 4/4 | Complete | 2026-06-30 |
| 1. Ingress Scaffold + SDK Migration + Config Seam | v3.0 | 2/2 | Complete | 2026-06-30 |
| 2. Live Sensor Discovery + Entity Selection UI | v3.0 | 3/3 | Complete | 2026-07-01 |
| 3. Config Read/Write + Detector Assignment + Reload | v3.0 | 3/3 | Complete | 2026-07-01 |
| 4. Validation, CI Packaging + Documentation | v3.0 | 4/4 | Complete | 2026-07-01 |
| v4.0 Group & Multivariate Detection + UX | v4.0 | TBD | Planned | - |
