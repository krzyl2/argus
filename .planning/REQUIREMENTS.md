# Requirements: Argus — v4.0 Group & Multivariate Anomaly Detection + UX

**Defined:** 2026-07-02
**Core Value:** Anomalies appear in HA as live binary_sensor + score entities with no manual entity creation and no HA restart — v4.0 extends this from single sensors to **groups** of sensors (relational + joint-multivariate), with a user-friendly algorithm chooser.

> Group latency is explicitly NOT bound by the single-sensor "< 2 s" Core Value target — groups wait for member time-alignment and run batch-first.

## v4.0 Requirements

Requirements for this milestone. Each maps to exactly one roadmap phase.

### Group Detection (GRP)

- [x] **GRP-01**: Operator can define a named group of sensor members explicitly in config (no auto-discovery), keyed by a stable operator-assigned group_id
- [x] **GRP-02**: Group members' history is time-aligned onto a common grid before scoring (InfluxDB `aggregateWindow`+`pivot`, server-side in .NET), with a staleness cap on forward-filled gaps
- [x] **GRP-03**: Peer-divergence detection flags WHICH member diverges from the group consensus, emitting a per-member binary_sensor + score (mirrors the v1–v3 per-entity output contract), using a robust (median/MAD) statistic
- [x] **GRP-04**: Peer-divergence enforces a minimum-member-count floor and degrades safely (does not emit meaningless verdicts) for groups below it
- [x] **GRP-05**: Joint-multivariate detection flags a jointly-abnormal value vector across a group, emitting a single group-level binary_sensor + score, using a PyOD multivariate detector (PCA/ECOD/COPOD/IForest)
- [x] **GRP-06**: Joint-multivariate features are per-feature scaled/normalized before fitting so mixed units (e.g. hPa vs %RH) do not dominate the joint score; the scaler is persisted with the model
- [x] **GRP-07**: Group models follow the existing Fit/Save/Load lifecycle, keyed by group_id + detector + version, without colliding with per-entity model keys
- [x] **GRP-08**: Group anomaly entities are published and retracted via MQTT discovery on group creation/membership change without orphaning stale HA entities
- [ ] **GRP-09**: Joint-multivariate detection attributes which member/feature drove the anomaly (per-feature reconstruction error), surfaced as a ranked contribution, not a flat boolean

### Algorithm Library & Chooser (ALGO)

- [ ] **ALGO-01**: Operator selects detector sensitivity via a Low/Med/High preset for the new group detectors, mapping to underlying parameters without exposing raw values by default
- [ ] **ALGO-02**: An Advanced toggle reveals and lets the operator override the raw underlying parameters behind a preset
- [ ] **ALGO-03**: Each selectable algorithm shows a "best for…" description explaining its intended use case in the chooser
- [ ] **ALGO-04**: A guided "what are you monitoring?" chooser pre-selects a sensible algorithm AND visibly shows/explains its pick, always allowing one-click override

### Sensor Search & Browse (SRCH)

- [ ] **SRCH-01**: Operator can search the sensor list by friendly_name (not only entity_id)
- [ ] **SRCH-02**: The sensor list is categorized/browsable by HA area and/or domain
- [ ] **SRCH-03**: The group-config UI suggests area-scoped candidate groups ("these N sensors share an area — group them?") that the operator approves; it never auto-groups

### UI Rebuild (UI)

- [x] **UI-01**: The configuration UI is rebuilt as a light SPA (Preact + Vite), built at Docker build-time and shipped as static assets — no Node in the runtime image
- [x] **UI-02**: The SPA loads and functions correctly under HA Ingress's dynamic base path (verified via "Open Web UI", never direct port), using hash routing or runtime base-path templating
- [x] **UI-03**: All new/existing `/api/*` endpoints enforce Ingress auth under the SPA
- [x] **UI-04**: Existing v3.0 config capabilities (sensor discovery/selection, per-entity detector assignment, hot-reload) remain fully functional after the SPA migration

## Future Requirements

Deferred beyond v4.0. Tracked but not in this roadmap.

### Streaming Groups (STRM)

- **STRM-01**: Group detection runs on the live streaming path (windowed + last-value-carried-forward across independently-arriving state_changed events)
- **STRM-02**: Group detection meets a defined streaming latency target once the batch model is validated in production

### Sensitivity Generalization (ALGO future)

- **ALGO-F1**: The Low/Med/High sensitivity scale extends uniformly across the existing univariate detectors (MAD/STL/HST), not only the new group detectors — deferred until the per-detector-family mapping is proven on 2–3 detectors

## Out of Scope

Explicitly excluded. Documented to prevent scope creep.

| Feature | Reason |
|---------|--------|
| Fully automatic algorithm selection (no visible reasoning) | Opaque black box erodes trust for a single-operator no-support tool; contradicts operator's direct-control preference — guided-but-transparent chooser (ALGO-04) instead |
| Continuous sensitivity slider (0–100) | False precision with no ground truth to calibrate; discrete Low/Med/High presets + Advanced escape hatch instead |
| Automatic dynamic group discovery (ML infers membership) | Unverifiable; wrong groupings silently produce wrong attributions — explicit config (GRP-01) with area-scoped *suggestions* (SRCH-03) only |
| Cross-group "meta-anomaly" dashboard | Scope creep into BI/dashboarding — PROJECT.md excludes custom HA dashboards; output stays as auto-created HA entities |
| Notification/alerting tied to presets | PROJECT.md excludes acting on anomalies — Argus only exposes entities; reactions stay in HA/Node-RED |
| Streaming group detection this milestone | Batch-first must prove the model; time-alignment across async streams is a distinct hard problem (see STRM-01/02) |
| Populating the old `EntityConfig.Groups`/`Covariates` placeholders | Wrong (per-entity, inverted) shape; retired in favor of a group-centric top-level `EntitiesConfig.Groups` list |

## Traceability

Which phases cover which requirements. Populated during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| GRP-01 | Phase 6 | Complete |
| GRP-02 | Phase 6 | Complete |
| GRP-03 | Phase 5 | Complete |
| GRP-04 | Phase 5 | Complete |
| GRP-05 | Phase 5 | Complete |
| GRP-06 | Phase 5 | Complete |
| GRP-07 | Phase 5 | Complete |
| GRP-08 | Phase 6 | Complete |
| GRP-09 | Phase 8 | Pending |
| ALGO-01 | Phase 8 | Pending |
| ALGO-02 | Phase 8 | Pending |
| ALGO-03 | Phase 8 | Pending |
| ALGO-04 | Phase 8 | Pending |
| SRCH-01 | Phase 8 | Pending |
| SRCH-02 | Phase 8 | Pending |
| SRCH-03 | Phase 8 | Pending |
| UI-01 | Phase 7 | Complete |
| UI-02 | Phase 7 | Complete |
| UI-03 | Phase 7 | Complete |
| UI-04 | Phase 7 | Complete |

**Coverage:**

- v4.0 requirements: 20 total
- Mapped to phases: 20 (Phase 5: 5, Phase 6: 3, Phase 7: 4, Phase 8: 8)
- Unmapped: 0 ✓

---
*Requirements defined: 2026-07-02*
*Last updated: 2026-07-02 after v4.0 roadmap creation (Phases 5-8)*
