# Requirements: Argus — Admin UI Rebuild (v4.1)

**Defined:** 2026-07-08
**Core Value:** Anomalies on v1 environmental sensors appear in HA as live binary_sensor + score entities within 2 seconds of a state_changed event, with no manual entity creation and no HA restart required.
**Milestone goal:** Replace the functional-but-provisional v4.0 SPA with a pixel-perfect implementation of the Argus Design System across all 5 admin screens, in Preact using existing `argus.css` conventions, with full light/dark mode.

## v4.1 Requirements

### Theming

- [x] **THEME-01**: `argus.css` has a full set of dark-mode tokens (`data-theme="dark"`) matching `Argus Design System/tokens/colors.css` + `elevation.css` — currently 0 dark-mode rules exist in the shipped stylesheet
- [x] **THEME-02**: Theme toggle (light/dark) in the sidebar; selection persists (localStorage); works consistently across all 5 screens

### Shared Components

- [x] **COMP-01**: Design-system component set ported to Preact (Button, Input, Select, Checkbox, SearchInput, Textarea, Card, Badge, StatusDot, KpiTile, AttributionBar, Disclosure, Banner, EmptyState, AlgorithmCard, SensitivityPreset, Sidebar) per `Argus Design System/components/*` specs

### Dashboard (new screen)

- [x] **DASH-01**: Dashboard screen with KPI tiles (KpiTile) per `ui_kits/admin/index.html` layout
- [x] **DASH-02**: "Recent anomalies" section (mocked data; marked TODO where the backend endpoint doesn't exist yet)
- [x] **DASH-03**: "System health" section (mocked data; marked TODO)

### Algorithms (new screen)

- [x] **ALGO-07**: Algorithms screen — group detector catalog browse (peer_divergence/ecod/copod/pca/iforest), source `Web/DetectorCatalog.cs`
- [x] **ALGO-08**: Presets + "best for…" copy per detector, sourced from `DetectorCatalog.cs`

### Sensors (rebuild)

- [x] **SEN-01**: Sensors screen rebuilt to Design System spec (list, filtering)
- [x] **SEN-02**: Single-sensor detector assignment (hst/mad/stl) with inline validation — source `DetectorDefaults.cs` + `detectorParams.ts`; markup and component structure may be refactored, not just restyled

### Groups (rebuild)

- [x] **GRP-12**: Group editor rebuilt to Design System spec
- [x] **GRP-13**: Algorithm creation wizard (guided flow) rebuilt to Design System spec
- [x] **GRP-14**: Attribution panel (AttributionBar) rebuilt to Design System spec

### Settings (new screen)

- [x] **SET-01**: Settings screen — global configuration (scope per `templates/admin-page` + existing repo settings)

### Accessibility & Interaction

- [x] **A11Y-01**: Focus always visible (2px accent outline, 2px offset) on all interactive elements, all screens
- [x] **A11Y-02**: Radio-card selection = 2px accent border, never color alone (Groups wizard + Sensors detector picker)

### Detectors IA restructure (Phase 14 — added 2026-07-21)

Minted during Phase 14 planning from `14-RESEARCH.md`'s Derived Requirements table. Client-only IA
restructure (no backend changes — D-09); depends on the Phase 10–13 Design System + rebuilt components.

- [x] **DET-01**: Detectors screen shows one unified, DS-consistent list merging groups (`GET /api/groups`) and tracked single sensors (`GET /api/sensors`, `isTracked` entries only)
- [x] **DET-02**: Editing a group row navigates to the existing, unchanged `/groups/:id` `GroupEditorForm`
- [x] **DET-03**: Editing a single-sensor row navigates to a new dedicated single-sensor detector-edit view/route, preserving hst/mad/stl assignment + inline validation (`detectorParams.ts`) + `DetectorDefaults.cs` defaults
- [x] **DET-04**: Sidebar "Sensors" and "Groups" nav items removed; "Detectors" + "Add detector" items added, with correct active-route highlighting
- [x] **DET-05**: `/detectors` is the new default route; bare `/sensors` and bare `/groups` redirect to `/detectors`; `/groups/new` and `/groups/:id` continue to work unchanged for direct deep links
- [x] **DET-06**: Pattern Filters (include/exclude auto-track) UI relocated to the Settings screen after the Sensors screen is removed, honoring the full-list-replace save guard (D-07)
- [x] **WIZ-01**: Add-detector wizard route with sensor multi-select search that reveals matching rows only once the query is >=3 characters
- [x] **WIZ-02**: Selecting exactly 1 sensor in the wizard tracks it and opens the single-sensor detector-edit view for it
- [x] **WIZ-03**: Selecting >=2 sensors in the wizard pre-fills them into a new group draft (`#/groups/new`) via the existing `pendingPrefillMembers` handoff, then continues through the existing, unchanged guided algorithm-chooser flow
- [x] **WIZ-04**: Any save triggered from the wizard, the single-sensor editor, or the relocated Settings pattern-filters loads the complete current tracked-sensor set first, so the full-list-replace `POST /api/sensors/save` never silently drops previously tracked sensors (Pitfall 1 / D-07)

### Streaming state persistence + warm-up backfill (Phase 15 — added 2026-08-03)

Backend requirements inside the UI-themed v4.1 milestone. Operator-reported critical defect: HST
warm-up restarts from zero on every service or machine restart, because streaming detector state is
RAM-only and the orchestrator keeps a second, independent warm-up counter.

- [x] **PERSIST-01**: Streaming detector state (River HST model + `MinMaxScaler` + `n_seen`) is checkpointed to disk on a recurring interval while the service runs — not only at shutdown — so an unexpected power loss or crash costs at most one interval of readings
- [x] **PERSIST-02**: Checkpoint writes are atomic (temp file + rename) and only occur for entities whose state actually changed since the last checkpoint; an entity with no new readings produces no disk writes
- [x] **PERSIST-03**: The detector flushes all pending checkpoints on SIGTERM, so a clean add-on restart loses zero readings
- [x] **PERSIST-04**: Checkpoints are restored into the registry at detector startup before the service reports healthy; a corrupt or River-version-incompatible checkpoint is discarded with a warning and never blocks startup for other entities
- [ ] **WARM-01**: The detector is the single source of truth for warm-up — `warmed_up` and `n_seen` travel on the `Verdict`, and the orchestrator no longer maintains its own reading counter
- [ ] **WARM-02**: Per-entity `hst` params (`window`, `n_trees`) reach the detector, so a configured non-default window governs both actual HST calibration and the warm-up progress shown in the UI
- [ ] **BACKFILL-01**: A cold entity (no checkpoint, `n_seen == 0`) is primed from InfluxDB history before live streaming, so an entity with sufficient history is warmed up on its first live reading
- [ ] **BACKFILL-02**: Backfill is idempotent — an orchestrator restart against an already-primed or checkpointed detector never re-feeds historical data
- [ ] **BACKFILL-03**: Backfill degrades safely — InfluxDB unconfigured, unreachable, or returning no rows produces a warning and normal live warm-up, never a startup failure
- [ ] **BACKFILL-04**: The same backfill pass primes the orchestrator's `FrozenSensorDetector` rolling window, so frozen-sensor detection is not blind for N readings after a restart

## Future Requirements

Deferred, not in this milestone's roadmap.

### UI

- **UI-10**: Icon set adoption (Lucide/Material Symbols) replacing Unicode glyph placeholders — only if separately decided

### Backlog

- **BACKLOG-01**: Algorithm tester/simulator in group config UI (Phase 999.1) — preview detector scores against real sensor history before saving

## Out of Scope

| Feature | Reason |
|---------|--------|
| Real backend endpoints for Dashboard (recent anomalies / system health) | Mocked for this milestone; runtime endpoints may not exist yet |
| Streaming groups UI | STRM-01/02 deferred from v4.0, backend not built |
| Icon set swap | Unicode placeholders stay until separately decided |
| Algorithm tester/simulator | Phase 999.1 backlog, separate future milestone |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| THEME-01 | Phase 10 | Complete |
| THEME-02 | Phase 10 | Complete |
| COMP-01 | Phase 10 | Complete |
| A11Y-01 | Phase 10 | Complete |
| A11Y-02 | Phase 10 | Complete |
| DASH-01 | Phase 11 | Complete |
| DASH-02 | Phase 11 | Complete |
| DASH-03 | Phase 11 | Complete |
| ALGO-07 | Phase 11 | Complete |
| ALGO-08 | Phase 11 | Complete |
| SET-01 | Phase 11 | Complete |
| SEN-01 | Phase 12 | Complete |
| SEN-02 | Phase 12 | Complete |
| GRP-12 | Phase 13 | Complete |
| GRP-13 | Phase 13 | Complete |
| GRP-14 | Phase 13 | Complete |
| DET-01 | Phase 14 | Planned |
| DET-02 | Phase 14 | Planned |
| DET-03 | Phase 14 | Planned |
| DET-04 | Phase 14 | Planned |
| DET-05 | Phase 14 | Planned |
| DET-06 | Phase 14 | Planned |
| WIZ-01 | Phase 14 | Planned |
| WIZ-02 | Phase 14 | Planned |
| WIZ-03 | Phase 14 | Planned |
| WIZ-04 | Phase 14 | Planned |
| PERSIST-01 | Phase 15 | Complete |
| PERSIST-02 | Phase 15 | Complete |
| PERSIST-03 | Phase 15 | Complete |
| PERSIST-04 | Phase 15 | Complete |
| WARM-01 | Phase 15 | Pending |
| WARM-02 | Phase 15 | Pending |
| BACKFILL-01 | Phase 15 | Pending |
| BACKFILL-02 | Phase 15 | Pending |
| BACKFILL-03 | Phase 15 | Pending |
| BACKFILL-04 | Phase 15 | Pending |

**Coverage:**

- v4.1 requirements: 36 total (16 original + 10 Phase 14 IA restructure + 10 Phase 15 persistence/backfill)
- Mapped to phases: 36 (Phase 10: 5, Phase 11: 6, Phase 12: 2, Phase 13: 3, Phase 14: 10, Phase 15: 10)
- Unmapped: 0 ✓

---
*Requirements defined: 2026-07-08*
*Last updated: 2026-08-03 — Phase 15 added 10 backend requirements (PERSIST-01..04, WARM-01..02, BACKFILL-01..04), 100% coverage*
