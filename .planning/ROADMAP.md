# Roadmap: Argus

## Milestones

- ✅ **v1.0 Foundations + Batch & Model Lifecycle** — Phases 1-2 (shipped 2026-06-10)
- ✅ **v2.0 Home Assistant Add-on** — Phases 1-4 (shipped 2026-06-30)
- ✅ **v3.0 Ingress Configuration UI** — Phases 1-4 (shipped 2026-07-02)
- ✅ **v4.0 Group & Multivariate Anomaly Detection + UX** — Phases 5-9 (shipped 2026-07-06)

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

**Live-verified on real HA OS (2026-07-02).** Formal UAT (8 items) deferred by operator — see STATE.md.

</details>

<details>
<summary>✅ v4.0 — Group & Multivariate Anomaly Detection + UX (Phases 5-9) — SHIPPED 2026-07-06</summary>

Argus extended from single-sensor to group anomaly detection (peer-divergence + joint-multivariate),
config UI rebuilt as a Preact+Vite SPA with a guided algorithm chooser, plus 2-member group support
and empirically-corrected guidance defaults. 18 plans, 42 tasks, 25/25 requirements complete.
Full detail in `.planning/milestones/v4.0-ROADMAP.md`.

- [x] **Phase 5: Group Detection Core (Proto + Python Detectors)** — 2D-matrix group proto contract + ScoreGroupBatch/FitGroup RPCs, peer-divergence (median/MAD) + joint-multivariate (RobustScaler + PyOD ECOD/COPOD/PCA/IForest) detectors, group model persistence (VERIFICATION: passed)
- [x] **Phase 6: Batch Group Pipeline** — GroupInfluxReader (aggregateWindow+pivot), count/mode-aware MQTT discovery/retraction, config-load unit/floor guards, BatchSchedulerWorker + MqttPublisherWorker wiring (VERIFICATION: passed)
- [x] **Phase 7: SPA Scaffolding** — Preact+Vite SPA built at Docker build-time, hash router, JSON API conversion, 1:1 v3.0 parity, htmx removed (VERIFICATION: human_needed — live-HA UI sign-off deferred)
- [x] **Phase 8: Group Config UI + Algorithm Chooser** — group CRUD UI, guided "what are you monitoring?" chooser + presets/Advanced, ranked joint attribution, friendly-name search + area browse, area-scoped suggestions (VERIFICATION: human_needed — live-HA UI sign-off deferred; 10 UAT scenarios skipped pending UI rebuild)
- [x] **Phase 9: 2-Member Groups + Algorithm Guidance Correction** — joint-mode floor lowered 3→2, PairwiseDeltaDetector for 2-member peer_divergence, guided "together" default ecod→copod, DetectorCatalog BestFor rewrite (VERIFICATION: passed, 11/11)

**Deferred at close:** Phase 07/08 live-HA UI verification + 10 Phase 08 UAT scenarios, pending the
planned UI rebuild (Phase 999.1). Backend detection paths verified (Phases 05/06/09 passed).

</details>

## Backlog

### Phase 999.1: Algorithm tester/simulator in group config UI (BACKLOG)

**Goal:** [Captured for future planning] During group creation/editing, let the operator simulate/preview how different group detectors (peer_divergence, ecod, copod, pca, iforest) would score the actual selected sensors' historical data, to validate the algorithm choice before saving — rather than relying solely on the guided chooser's static recommendation.
**Requirements:** TBD
**Plans:** not yet planned

Plans:

- [ ] TBD (promote with /gsd-review-backlog when ready)

Context: raised 2026-07-03 while live-verifying Phase 8's algorithm chooser. The guided "what are you monitoring?" flow recommends a detector by static rule, but the same investigation found the recommendation can be empirically wrong for some patterns (see Phase 9 — joint-mode 2-member floor + guided-default correction). A tester/simulator would let operators catch this kind of mismatch themselves against their own sensor history instead of discovering it live.

## Progress

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 1-2. Foundations + Batch/Model Lifecycle | v1.0 | 14/14 | Complete | 2026-06-10 |
| 1-4. Add-on (Skeleton, Code, Supervision, CI) | v2.0 | 12/12 | Complete | 2026-06-30 |
| 1-4. Ingress Configuration UI | v3.0 | 12/12 | Complete | 2026-07-02 |
| 5. Group Detection Core (Proto + Python Detectors) | v4.0 | 4/4 | Complete | 2026-07-02 |
| 6. Batch Group Pipeline | v4.0 | 4/4 | Complete | 2026-07-02 |
| 7. SPA Scaffolding | v4.0 | 3/3 | Complete | 2026-07-02 |
| 8. Group Config UI + Algorithm Chooser | v4.0 | 4/4 | Complete | 2026-07-02 |
| 9. 2-Member Groups + Algorithm Guidance Correction | v4.0 | 3/3 | Complete | 2026-07-03 |
