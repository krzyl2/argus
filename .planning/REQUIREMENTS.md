# Requirements: Argus — Admin UI Rebuild (v4.1)

**Defined:** 2026-07-08
**Core Value:** Anomalies on v1 environmental sensors appear in HA as live binary_sensor + score entities within 2 seconds of a state_changed event, with no manual entity creation and no HA restart required.
**Milestone goal:** Replace the functional-but-provisional v4.0 SPA with a pixel-perfect implementation of the Argus Design System across all 5 admin screens, in Preact using existing `argus.css` conventions, with full light/dark mode.

## v4.1 Requirements

### Theming

- [ ] **THEME-01**: `argus.css` has a full set of dark-mode tokens (`data-theme="dark"`) matching `Argus Design System/tokens/colors.css` + `elevation.css` — currently 0 dark-mode rules exist in the shipped stylesheet
- [ ] **THEME-02**: Theme toggle (light/dark) in the sidebar; selection persists (localStorage); works consistently across all 5 screens

### Shared Components

- [ ] **COMP-01**: Design-system component set ported to Preact (Button, Input, Select, Checkbox, SearchInput, Textarea, Card, Badge, StatusDot, KpiTile, AttributionBar, Disclosure, Banner, EmptyState, AlgorithmCard, SensitivityPreset, Sidebar) per `Argus Design System/components/*` specs

### Dashboard (new screen)

- [ ] **DASH-01**: Dashboard screen with KPI tiles (KpiTile) per `ui_kits/admin/index.html` layout
- [ ] **DASH-02**: "Recent anomalies" section (mocked data; marked TODO where the backend endpoint doesn't exist yet)
- [ ] **DASH-03**: "System health" section (mocked data; marked TODO)

### Algorithms (new screen)

- [ ] **ALGO-07**: Algorithms screen — group detector catalog browse (peer_divergence/ecod/copod/pca/iforest), source `Web/DetectorCatalog.cs`
- [ ] **ALGO-08**: Presets + "best for…" copy per detector, sourced from `DetectorCatalog.cs`

### Sensors (rebuild)

- [ ] **SEN-01**: Sensors screen rebuilt to Design System spec (list, filtering)
- [ ] **SEN-02**: Single-sensor detector assignment (hst/mad/stl) with inline validation — source `DetectorDefaults.cs` + `detectorParams.ts`; markup and component structure may be refactored, not just restyled

### Groups (rebuild)

- [ ] **GRP-12**: Group editor rebuilt to Design System spec
- [ ] **GRP-13**: Algorithm creation wizard (guided flow) rebuilt to Design System spec
- [ ] **GRP-14**: Attribution panel (AttributionBar) rebuilt to Design System spec

### Settings (new screen)

- [ ] **SET-01**: Settings screen — global configuration (scope per `templates/admin-page` + existing repo settings)

### Accessibility & Interaction

- [ ] **A11Y-01**: Focus always visible (2px accent outline, 2px offset) on all interactive elements, all screens
- [ ] **A11Y-02**: Radio-card selection = 2px accent border, never color alone (Groups wizard + Sensors detector picker)

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

Filled during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| THEME-01 | TBD | Pending |
| THEME-02 | TBD | Pending |
| COMP-01 | TBD | Pending |
| DASH-01 | TBD | Pending |
| DASH-02 | TBD | Pending |
| DASH-03 | TBD | Pending |
| ALGO-07 | TBD | Pending |
| ALGO-08 | TBD | Pending |
| SEN-01 | TBD | Pending |
| SEN-02 | TBD | Pending |
| GRP-12 | TBD | Pending |
| GRP-13 | TBD | Pending |
| GRP-14 | TBD | Pending |
| SET-01 | TBD | Pending |
| A11Y-01 | TBD | Pending |
| A11Y-02 | TBD | Pending |

**Coverage:**
- v4.1 requirements: 16 total
- Mapped to phases: 0 (pending roadmap)
- Unmapped: 16 ⚠️

---
*Requirements defined: 2026-07-08*
*Last updated: 2026-07-08 after initial definition*
