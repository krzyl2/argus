# Roadmap: Argus

## Milestones

- ✅ **v1.0 Foundations + Batch & Model Lifecycle** — Phases 1-2 (shipped 2026-06-10)
- ✅ **v2.0 Home Assistant Add-on** — Phases 1-4 (shipped 2026-06-30)
- ✅ **v3.0 Ingress Configuration UI** — Phases 1-4 (shipped 2026-07-02)
- ✅ **v4.0 Group & Multivariate Anomaly Detection + UX** — Phases 5-9 (shipped 2026-07-06)
- 🚧 **v4.1 Admin UI Rebuild (Design System)** — Phases 10-13 (in progress)

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
planned UI rebuild (Phase 999.1 backlog item promoted into this v4.1 milestone). Backend detection
paths verified (Phases 05/06/09 passed).

</details>

### 🚧 v4.1 Admin UI Rebuild (Design System) (In Progress)

**Milestone Goal:** Replace the functional-but-provisional v4.0 SPA with a pixel-perfect implementation
of the Argus Design System across all 5 admin screens (Dashboard, Algorithms, Sensors, Groups,
Settings), in Preact using existing `argus.css` conventions, with full light/dark mode. Resolves the
v4.0-deferred "UI redesign" item and unblocks the deferred Phase 07/08 live-HA UI verification.

**Reference:** `Argus Design System/HANDOFF_TO_CLAUDE_CODE.md` (design reference package, not
production code) — `ui_kits/admin/index.html` (composition reference), `components/*` (component
API+look specs), `tokens/*.css` (colors/typography/spacing/elevation — light on `:root`, dark on
`[data-theme="dark"]`), `readme.md` (voice/content + visual foundation spec).

- [ ] **Phase 10: Design System Foundation** - Dark-mode tokens, ported shared component library (Button/Input/Select/.../Sidebar), and cross-cutting focus-visibility + radio-card a11y rules that every later screen depends on
- [ ] **Phase 11: New Standalone Screens (Dashboard, Algorithms, Settings)** - Three new admin screens (mocked KPIs/recent-anomalies/health, read-only detector catalog browse, global config) built on the Phase 10 foundation
- [ ] **Phase 12: Sensors Screen Rebuild** - Sensor list + single-sensor detector assignment (hst/mad/stl) with inline validation, rebuilt to Design System spec
- [ ] **Phase 13: Groups Screen Rebuild** - Group editor + algorithm creation wizard + attribution panel rebuilt to Design System spec

## Phase Details

### Phase 10: Design System Foundation

**Goal**: The design system's visual and interaction foundation — dark-mode tokens, the shared Preact
component library, and the two cross-cutting accessibility rules — exists so every later screen can be
built pixel-accurate in both themes without re-deriving these primitives per-screen.
**Depends on**: Phase 9 (v4.0 — existing Preact+Vite SPA + argus.css to extend, not replace)
**Requirements**: THEME-01, THEME-02, COMP-01, A11Y-01, A11Y-02
**Success Criteria** (what must be TRUE):

  1. Toggling the theme switch in the sidebar instantly swaps every token-driven color across the whole
     app between the light and dark token sets, with no unstyled or light-leaking regions, and the
     choice persists in localStorage and is restored on reload — consistent across all 5 admin screens

  2. Every design-system component (Button, Input, Select, Checkbox, SearchInput, Textarea, Card,
     Badge, StatusDot, KpiTile, AttributionBar, Disclosure, Banner, EmptyState, AlgorithmCard,
     SensitivityPreset, Sidebar) exists as a Preact component matching its `Argus Design System/components/*`
     spec, in both themes

  3. Tabbing through any interactive element (button, input, radio-card, nav link) shows a visible 2px
     accent outline with 2px offset — focus is never invisible

  4. Selecting an AlgorithmCard or SensitivityPreset radio-card shows a 2px accent border on the
     selected option, and the selected vs. unselected state is distinguishable without relying on color
     alone

**Plans**: 1/7 plans executed

Plans:
**Wave 1**

- [x] 10-01-PLAN.md — CSS token foundation + [data-theme="dark"] block + all component BEM classes + A11Y-01 focus fix + main.tsx theme bootstrap (THEME-01, THEME-02, A11Y-01)

**Wave 2** *(blocked on Wave 1 completion)*

- [ ] 10-02-PLAN.md — Form components: Button (+test), Input, Select, Textarea, Checkbox, SearchInput (COMP-01, A11Y-01)
- [ ] 10-03-PLAN.md — Display components: Card, Badge, StatusDot, KpiTile, Disclosure, AttributionBar (COMP-01)
- [ ] 10-04-PLAN.md — Feedback + Selection: Banner, EmptyState, AlgorithmCard, SensitivityPreset (COMP-01, A11Y-02)
- [ ] 10-05-PLAN.md — Navigation: Sidebar + AppShell shell + theme toggle (THEME-02, COMP-01)

**Wave 3** *(blocked on Wave 2 completion)*

- [ ] 10-06-PLAN.md — Retrofit forms consumers: SaveBar/AddDetectorButton/DetectorEntry/SensorListRow/SensorSearchInput (COMP-01, A11Y-01)
- [ ] 10-07-PLAN.md — Retrofit display/feedback consumers: GroupListRow + 3 banner components (COMP-01)

**UI hint**: yes

### Phase 11: New Standalone Screens (Dashboard, Algorithms, Settings)

**Goal**: Operators have three new admin screens — an at-a-glance Dashboard, a browsable Algorithms
catalog, and a Settings screen — reachable from the sidebar and matching the Design System in both
themes, built on the Phase 10 foundation.
**Depends on**: Phase 10
**Requirements**: DASH-01, DASH-02, DASH-03, ALGO-07, ALGO-08, SET-01
**Success Criteria** (what must be TRUE):

  1. Navigating to Dashboard shows KPI tiles (KpiTile) per the `ui_kits/admin/index.html` layout, with a
     "recent anomalies" section and a "system health" section — where a backend endpoint doesn't exist
     yet, the section shows mocked data explicitly marked TODO rather than silently faking a real feed

  2. Navigating to Algorithms shows a read-only catalog of all 5 group detectors (peer_divergence,
     ecod, copod, pca, iforest) with presets and "best for…" copy sourced from `Web/DetectorCatalog.cs`
     — distinct from the in-flow `AlgorithmChooser` wizard step used by Groups

  3. Navigating to Settings shows a global-configuration screen scoped per `templates/admin-page` and
     the app's existing configurable settings

  4. All three screens are reachable from the sidebar navigation and render correctly in both light and
     dark mode with no unstyled regions

**Plans**: TBD
**UI hint**: yes

### Phase 12: Sensors Screen Rebuild

**Goal**: The existing, functional Sensors screen (`SensorsPage.tsx` and its supporting components) is
rebuilt — markup and component structure may be refactored, not just restyled — to the Design System
spec, with single-sensor detector assignment and inline validation fully preserved.
**Depends on**: Phase 10
**Requirements**: SEN-01, SEN-02
**Success Criteria** (what must be TRUE):

  1. The Sensors screen's list and filtering UI (search, area/domain browse) matches the Design System
     spec (Card/Badge/StatusDot/SearchInput patterns) in both themes

  2. Assigning a detector (hst/mad/stl) to a sensor still works end-to-end after the rebuild, with
     inline validation errors shown per the existing `detectorParams.ts` rules and `DetectorDefaults.cs`
     server defaults

  3. Selecting a detector via the sensor's radio-card picker shows the Phase 10 shared component's 2px
     accent-border selection state, never color alone

**Plans**: TBD
**UI hint**: yes

### Phase 13: Groups Screen Rebuild

**Goal**: The existing, functional Groups screen (`GroupsPage.tsx`, `GroupEditorForm.tsx`,
`MemberPicker.tsx`, `AlgorithmChooser.tsx`, `AttributionBar.tsx`) is rebuilt — markup and component
structure may be refactored, not just restyled — to the Design System spec, with group CRUD, the
guided algorithm wizard, and attribution display fully preserved.
**Depends on**: Phase 10
**Requirements**: GRP-12, GRP-13, GRP-14
**Success Criteria** (what must be TRUE):

  1. The group editor (list, member picker, save/delete) matches the Design System spec in both themes
  2. The guided algorithm creation wizard's steps (chooser, sensitivity presets, Advanced override)
     match the Design System spec, with radio-card detector/preset selection showing the Phase 10
     shared component's 2px accent-border state

  3. The attribution panel (AttributionBar) renders ranked per-member/per-feature contribution bars
     matching the Design System spec, in both themes

**Plans**: TBD
**UI hint**: yes

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
| 10. Design System Foundation | v4.1 | 1/7 | In Progress|  |
| 11. New Standalone Screens (Dashboard, Algorithms, Settings) | v4.1 | 0/TBD | Not started | - |
| 12. Sensors Screen Rebuild | v4.1 | 0/TBD | Not started | - |
| 13. Groups Screen Rebuild | v4.1 | 0/TBD | Not started | - |
