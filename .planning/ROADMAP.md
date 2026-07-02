# Roadmap: Argus

## Milestones

- ✅ **v1.0 Foundations + Batch & Model Lifecycle** — Phases 1-2 (shipped 2026-06-10)
- ✅ **v2.0 Home Assistant Add-on** — Phases 1-4 (shipped 2026-06-30)
- ✅ **v3.0 Ingress Configuration UI** — Phases 1-4 (shipped 2026-07-02)
- 🚧 **v4.0 Group & Multivariate Anomaly Detection + UX** — Phases 5-8 (in progress)

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

### 🚧 v4.0 Group & Multivariate Anomaly Detection + UX (In Progress)

**Milestone Goal:** Analyze groups of sensors, not just single ones — both peer-divergence (which
member diverges from its group, e.g. one tire pressure rising unlike the others) and joint
multivariate (values jointly abnormal, e.g. room humidity → leak). Support more algorithms with a
user-friendly chooser (readable parameter presets + "best for" descriptions), search sensors by
friendly name, and a modern readable UI (light SPA — Preact + Vite).

**Locked scope decisions (2026-07-02):**

- Both group modes (peer-divergence + joint multivariate) ship this milestone.
- Batch-first (InfluxDB resampling for time-alignment, server-side Flux); streaming groups deferred (STRM-01/02).
- UI rebuilt as a light SPA (Preact + Vite, built at Docker build-time) — overrides v3.0's htmx/no-Node-build decision.
- GRP-09 (per-feature attribution for joint-multivariate) ships this milestone but is sequenced late, after base joint-multivariate detection and the UI shell both exist — attribution is meaningless without somewhere to display the ranked contribution.

- [x] **Phase 5: Group Detection Core (Proto + Python Detectors)** - Peer-divergence and joint-multivariate scoring work correctly in isolation, verified without any .NET or UI involvement (completed 2026-07-02)
- [x] **Phase 6: Batch Group Pipeline** - Operators define groups in config and see real, time-aligned group anomalies published to MQTT/HA without orphaning entities (completed 2026-07-02)
- [x] **Phase 7: SPA Scaffolding** - The configuration UI is rebuilt as a Preact+Vite SPA that loads and functions correctly under real HA Ingress, with all v3.0 capabilities intact (completed 2026-07-02)
- [x] **Phase 8: Group Config UI + Algorithm Chooser** - Operators author groups, choose algorithms via presets/guided chooser, and see ranked per-feature attribution for joint-multivariate anomalies (completed 2026-07-02)

## Phase Details

### Phase 5: Group Detection Core (Proto + Python Detectors)

**Goal**: Peer-divergence and joint-multivariate detection produce correct, independently-verifiable scores at the Python/proto layer before any orchestrator or UI code depends on them.
**Depends on**: Phase 4 (v3.0 — existing detector/proto foundation)
**Requirements**: GRP-03, GRP-04, GRP-05, GRP-06, GRP-07
**Success Criteria** (what must be TRUE):

  1. Given a group's pre-aligned value matrix, peer-divergence detection correctly identifies which member diverges from the group's robust (median/MAD) consensus, and does not emit a verdict for groups below the minimum-member-count floor
  2. Given a group's pre-aligned value matrix with mixed units (e.g. hPa + %RH), joint-multivariate detection (PyOD PCA/ECOD/COPOD/IForest) flags jointly-abnormal vectors without one feature's scale dominating the score, because features are scaled/normalized before fitting and the scaler is persisted with the model
  3. The proto contract carries a real 2D matrix (not a loop of univariate calls) for group scoring, so genuine joint anomalies that no single feature would trigger are still caught
  4. Group models Fit/Save/Load using group_id + detector + version as the key, and this never collides with an existing per-entity model key

**Plans**: 4/4 plans complete

  - [x] 05-01-PLAN.md — Proto contract: Series/GroupScore/FitGroup messages + RPCs, Python regen (Wave 1)
  - [x] 05-02-PLAN.md — Peer-divergence detector: robust modified z-score, floor, MAD=0 guard (Wave 1)
  - [x] 05-03-PLAN.md — Joint-multivariate detector (RobustScaler+PyOD) + group model persistence (Wave 1)
  - [x] 05-04-PLAN.md — Servicer + registry wiring: FitGroup/ScoreGroupBatch handlers + validation (Wave 3)

### Phase 6: Batch Group Pipeline

**Goal**: Operators can define a group in config and see it flow end-to-end — time-aligned InfluxDB history, scored via Phase 5's detectors, published/retracted as MQTT-discovered HA entities — with unit and membership guards preventing broken groups from silently producing nonsense.
**Depends on**: Phase 5
**Requirements**: GRP-01, GRP-02, GRP-08
**Success Criteria** (what must be TRUE):

  1. Operator can define a named group of sensor members in config, keyed by a stable operator-assigned group_id, with no auto-discovery involved
  2. Group members' history is resampled onto a common time grid server-side (InfluxDB aggregateWindow+pivot) before scoring, with a staleness cap so stale forward-filled gaps don't get scored as real data
  3. Group anomaly entities (per-member for peer-divergence, group-level for joint-multivariate) are published via MQTT discovery on group creation and correctly retracted on membership change, without orphaning stale HA entities
  4. A group with incompatible units across members, or with membership below the minimum-N floor, is rejected or degrades safely at config-load time rather than producing a silently-wrong score

**Plans**: 4/4 plans complete

- [x] 06-01-PLAN.md
- [x] 06-02-PLAN.md
- [x] 06-03-PLAN.md
- [x] 06-04-PLAN.md

### Phase 7: SPA Scaffolding

**Goal**: The v3.0 Ingress configuration UI is rebuilt on a Preact+Vite SPA foundation that is verified against real HA Supervisor Ingress before any new feature UI is built on top of it, with zero loss of existing v3.0 capability.
**Depends on**: Phase 4 (v3.0 — existing Ingress UI to replace)
**Requirements**: UI-01, UI-02, UI-03, UI-04
**Success Criteria** (what must be TRUE):

  1. The SPA is built at Docker build-time (Preact + Vite) and shipped as static assets only — no Node.js present in the runtime image
  2. Opening the add-on's Web UI via HA's "Open Web UI" (never a direct port) loads and fully functions under Supervisor's dynamic Ingress base path
  3. Every `/api/*` endpoint the SPA calls enforces the same Ingress auth guarantees the v3.0 server-rendered UI had
  4. All v3.0 capabilities — sensor discovery/selection, per-entity detector assignment, hot-reload without restart — work identically through the new SPA

**Plans**: 3/3 plans complete

  - [x] 07-01-PLAN.md — SPA scaffold: Vite/Preact project, hash router, relative-fetch client, 13 parity components + validation + Vitest (Wave 1)
  - [x] 07-02-PLAN.md — .NET JSON API conversion + SaveRequest DTO + UseStaticFiles/MapFallbackToFile + server-render removal (Wave 1)
  - [x] 07-03-PLAN.md — Multi-stage Dockerfile (Node build + in-image dotnet publish) + CI/build-push update + htmx removal (Wave 2)

**UI hint**: yes

### Phase 8: Group Config UI + Algorithm Chooser

**Goal**: Operators can author groups, pick detection algorithms through a transparent, guided chooser instead of raw parameters, find sensors by friendly name/area, and see which member/feature drove a joint-multivariate anomaly instead of a flat boolean.
**Depends on**: Phase 6, Phase 7
**Requirements**: GRP-09, ALGO-01, ALGO-02, ALGO-03, ALGO-04, SRCH-01, SRCH-02, SRCH-03
**Success Criteria** (what must be TRUE):

  1. Operator selects a Low/Med/High sensitivity preset for a group detector without seeing raw parameters, and an Advanced toggle reveals/overrides the underlying values behind that preset
  2. Each selectable algorithm in the chooser shows a "best for…" description, and a guided "what are you monitoring?" flow pre-selects and visibly explains an algorithm choice while always allowing one-click override
  3. Operator can find sensors by searching friendly_name (not just entity_id) and by browsing a list categorized by HA area/domain
  4. The group-config UI suggests area-scoped candidate groups for operator approval (never auto-groups), and joint-multivariate anomalies display a ranked per-feature/per-member contribution rather than a flat boolean

**Plans**: 4/4 plans complete

  - [x] 08-01-PLAN.md — Python param-wiring: peer_divergence from_params + multivariate contamination/n_estimators + registry/servicer request.params threading (Wave 1)
  - [x] 08-02-PLAN.md — .NET backend: 4 endpoints (groups/save/catalog/status), DetectorCatalog, GroupStatusCache + joint-branch populate + sort fix, HA area/entity registry enrichment (Wave 1)
  - [x] 08-03-PLAN.md — SPA foundation: group routes/state/types/validation, group list + editor + member picker, friendly_name search + area/domain browse (Wave 2)
  - [x] 08-04-PLAN.md — SPA chooser + attribution: guided flow + presets + Advanced override, ranked attribution panel, area suggestions, live-HA human-verify (Wave 3)

**UI hint**: yes

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
| 5. Group Detection Core (Proto + Python Detectors) | v4.0 | 4/4 | Complete    | 2026-07-02 |
| 6. Batch Group Pipeline | v4.0 | 4/4 | Complete    | 2026-07-02 |
| 7. SPA Scaffolding | v4.0 | 3/3 | Complete    | 2026-07-02 |
| 8. Group Config UI + Algorithm Chooser | v4.0 | 4/4 | Complete   | 2026-07-02 |
