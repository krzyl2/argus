# Phase 11: New Standalone Screens (Dashboard, Algorithms, Settings) - Context

**Gathered:** 2026-07-08
**Status:** Ready for planning

<domain>
## Phase Boundary

Three new admin screens on the Phase 10 Design System foundation, reachable from the
existing Sidebar and correct in both themes:

1. **Dashboard** — KPI tiles + "recent anomalies" + "system health" sections.
2. **Algorithms** — read-only browse catalog of the 5 group detectors.
3. **Settings** — global-configuration screen.

Scope clarifications locked in this discussion:
- Dashboard is **frontend-only** (no new backend).
- Settings adds **one small read-only backend endpoint** (`GET /api/settings`) — deliberately
  chosen; see D-06. This is the only server-side work in the phase.
- Requirements: DASH-01, DASH-02, DASH-03, ALGO-07, ALGO-08, SET-01.

Out of scope (redirected to Deferred Ideas): editable Algorithms defaults, single-sensor
catalog browse, a real `/api/health` endpoint, and settings write/persistence.

> ⚠ **Roadmap inconsistency flagged (Rule 7/12):** `ROADMAP.md` marks Phase 11 as
> `[x] completed 2026-07-08`, but no plans/context/verification exist and STATE.md has
> `status: executing, current_phase: 11`. The `[x]` checkbox is premature/erroneous — Phase 11
> is the phase being planned now. Roadmap checkbox should be corrected to `[ ]` during/after
> execution.

</domain>

<decisions>
## Implementation Decisions

### Dashboard — real vs mocked data (frontend-only)
- **D-01:** KPI tiles use **real data where an existing API supplies it, no new backend**:
  monitored-sensor count from `GET /api/sensors`, group count from `GET /api/groups`,
  active-detector count derived (sum of per-sensor detectors + group count). The
  "Home Assistant: Connected" tile has **no endpoint** → rendered as an explicit **mock+TODO**
  (visibly marked, not silently faked).
- **D-02:** "Recent anomalies" section is **entirely mock+TODO** with an explicit banner/marker.
  Rationale: there is no anomaly-history/feed endpoint. `GET /api/groups/{id}/status` only
  returns the single latest cached verdict per group (no history, groups only, N+1) — decided
  NOT worth wiring; single-sensor anomalies go to HA via MQTT and are not stored server-side.
- **D-03:** "System health" section is **mock+TODO** (no `/api/health` endpoint; DetectionGateway
  does a startup check but exposes no HTTP surface).
- **Marking convention:** every mocked section/tile must be explicitly labelled TODO in-UI so no
  mock is mistaken for a live feed (DASH-02/DASH-03 acceptance requirement).

### Algorithms — read-only group catalog (Claude's discretion, per ROADMAP)
- **D-04:** Read-only **browse** of the **5 group detectors only** (peer_divergence, ecod, copod,
  pca, iforest), sourced from `GET /api/detectors/catalog` (backed by `Web/DetectorCatalog.cs`):
  name, "best for…" copy, Low/Med/High presets, param schema. **No editing, no SaveBar.**
- **D-05:** This screen is distinct from the in-flow `AlgorithmChooser` wizard step used by Groups
  (Phase 13) — display/browse only, not a selection surface.
  > ⚠ **ui_kit conflict flagged:** the reference `ui_kits/admin/Algorithms.jsx` is *editable*
  > (SaveBar, editable params) and includes an extra "Single sensors" (hst/mad/stl) catalog
  > section. Both are intentionally **rejected** for Phase 11 (ROADMAP says "read-only"; backend
  > has no single-sensor catalog endpoint). Use the kit for layout/visual reference only.

### Settings — read-only + one new read endpoint
- **D-06:** Add a **new `GET /api/settings`** endpoint that returns current non-sensitive config
  read from `ConnectionSettings`/`IConfiguration`. The screen is **read-only** (no POST/write).
  > ⚠ **Scope note (Rule 12):** this pushes Phase 11 beyond pure frontend — a deliberate,
  > user-chosen addition. Chosen over "values are mock+TODO" so displayed config is truthful.
- **D-07:** **Secret-redaction constraint** — `GET /api/settings` must expose only non-sensitive
  values (e.g. detector gRPC endpoint, InfluxDB URL/bucket, batch interval, nightly fit hour,
  log level). It must **NOT** return tokens/passwords/HA long-lived tokens. Endpoint must follow
  the existing `IsAuthorizedRequest(...)` guard used by every other `/api/*` route in `Program.cs`.
- **D-08:** Show **all 3 sections per the reference kit**: *Connections* (gRPC/InfluxDB) and
  *Batch & detection* (interval, nightly fit hour, log level) rendered **read-only** from the new
  endpoint; *Appearance* is the one **functional** section.
- **D-09:** Theme control is **Light/Dark only**, bound to the **same `localStorage` key
  (`argus-theme`) and `data-theme` mechanism** established in Phase 10 (Sidebar toggle). **No
  'System' option** and **no new theme logic** — the Settings Appearance control and the Sidebar
  toggle are two surfaces over one shared state.

### Navigation / routing (Claude's discretion, per ROADMAP)
- **D-10:** Default landing route **stays `#/sensors`** (unchanged). Enable the 3 currently-disabled
  Sidebar placeholders (`dashboard`, `algorithms`, `settings` in `Sidebar.tsx` `NAV_ITEMS`, D-02
  from Phase 10) and add hash-routes `#/dashboard`, `#/algorithms`, `#/settings` to the hand-rolled
  router (`router.ts`) — **no router library** (locked in Phase 8/9 RESEARCH). Update `isActive`
  for the 3 new routes.

### Claude's Discretion
- **Algorithms** (D-04/D-05) and **Nav/routing** (D-10) were not selected for discussion by the
  user; resolved per ROADMAP scope as noted above. Planner may refine layout details.
- Exact component composition per screen (KpiTile grid, Card layouts, SectionLabel/PageHeader
  helpers) follows the Phase 10 shared library + reference ui_kits.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Design references (layout/visual only — NOT behavior)
- `Argus Design System/ui_kits/admin/index.html` — DASH-01 layout anchor (KPI row + sections).
- `Argus Design System/ui_kits/admin/Dashboard.jsx` — Dashboard reference layout (KPI/anomalies/health/quick-actions). Note: `window.ARGUS_DATA` here is mock data.
- `Argus Design System/ui_kits/admin/Algorithms.jsx` — Algorithms reference layout. **Editable/SaveBar + "Single sensors" section are rejected** (see D-05).
- `Argus Design System/ui_kits/admin/Settings.jsx` — Settings reference layout (Connections/Batch&detection/Appearance).
- `Argus Design System/ui_kits/admin/data.js` — shape reference for mock KPI/anomaly/health data.
- `Argus Design System/templates/admin-page/AdminPage.dc.html` — SET-01 page-scope template.

### Backend data sources
- `orchestrator/Argus.Orchestrator/Web/DetectorCatalog.cs` — source of truth for the 5 group detectors (ALGO-07/08), served by `GET /api/detectors/catalog` (`Program.cs:581`).
- `orchestrator/Argus.Orchestrator/Config/ConnectionSettings.cs` — config surface the new `GET /api/settings` reads from (Batch interval etc.); mind secret redaction (D-07).
- `orchestrator/Argus.Orchestrator/Program.cs` — endpoint registrations + `IsAuthorizedRequest` auth pattern; `/api/sensors` (:248), `/api/groups` (:458), `/api/detectors/catalog` (:581), `/api/groups/{id}/status` (:590). New `GET /api/settings` registers here.

### Frontend integration points
- `orchestrator/ui/src/api/types.ts` — `DetectorCatalog`/`DetectorCatalogEntry` types (reuse for Algorithms); add a settings response type.
- `orchestrator/ui/src/api/client.ts` — `apiGet` helper for the new fetches.
- `orchestrator/ui/src/components/Sidebar.tsx` — `NAV_ITEMS` placeholders to enable + `isActive` update (D-10).
- `orchestrator/ui/src/router.ts` — hand-rolled hash router to extend with 3 new routes (D-10).
- `orchestrator/ui/src/components/AppShell.tsx` — shell wrapping `<main>` where new pages render.

### Prior context
- `.planning/phases/10-design-system-foundation/10-CONTEXT.md` — Phase 10 decisions (D-01..D-07): Sidebar/AppShell, shared component library, `[data-theme="dark"]` + `localStorage('argus-theme')` theme mechanism that D-09 reuses.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **Phase 10 shared component library** (Preact): KpiTile, Card, Badge, StatusDot, Button,
  EmptyState, Disclosure, SensitivityPreset, Input, Select, Banner — all three screens compose these.
- **`apiGet` client** (`api/client.ts`) + typed responses (`api/types.ts`) — reuse for
  `/api/sensors`, `/api/groups`, `/api/detectors/catalog`, and the new `/api/settings`.
- **`DetectorCatalog` TS type** already exists and is already consumed by `AlgorithmChooser` —
  Algorithms screen reuses the same fetch/shape (read-only rendering).
- **Theme mechanism** from Phase 10 (`data-theme` attr + `localStorage 'argus-theme'`, bootstrap
  in `main.tsx`, toggle in `Sidebar.tsx`) — Settings Appearance binds to this, no new logic.

### Established Patterns
- **Hand-rolled hash router** (`router.ts`) — signals-based, no router library (locked). New
  screens add routes here + a switch in the top-level render.
- **All `/api/*` endpoints check `IsAuthorizedRequest(req.HttpContext)` first** — new
  `/api/settings` must follow this exactly.
- **`Sidebar.tsx` already lists all 5 nav items** (Phase 10 D-02); 3 are `disabled: true`
  placeholders — enabling is flipping flags + adding `href` + `isActive` cases, not reshaping.

### Integration Points
- New `GET /api/settings` in `Program.cs` reading `ConnectionSettings`/`IConfiguration` (redacted).
- New Preact page components rendered by the router switch inside `AppShell`'s `<main>`.
- Dashboard KPI derivation reuses existing sensors/groups fetches (may already be in state layer).

</code_context>

<specifics>
## Specific Ideas

- Every mocked Dashboard region must carry a visible TODO marker (explicit "mocked — no backend
  endpoint yet"), never a silent fake — this is a hard acceptance requirement, not polish.
- Algorithms screen must read "best for…" copy verbatim from the catalog API, never hardcode
  detector descriptions client-side (matches the Phase 9 anti-pattern: catalog copy is server-owned).
- Reference ui_kits are for **layout/visual** guidance only; their embedded `ARGUS_DATA` mocks and
  editable behaviors do NOT define Phase 11 behavior.

</specifics>

<deferred>
## Deferred Ideas

- **Editable Algorithms defaults with persistence** (reference ui_kit shows SaveBar + editable
  params) — needs a write path + storage model; own phase.
- **Single-sensor (hst/mad/stl) catalog browse** — no backend catalog endpoint exists for these
  (they live in `DetectorDefaults.cs`); own phase if desired.
- **`GET /api/health` endpoint** (detector gRPC reachability / HA / MQTT connection) — would let
  Dashboard's "system health" + HA KPI tile be real instead of mock+TODO; own phase.
- **Settings write/persistence (`POST /api/settings`)** — conflicts with the Supervisor-injected
  env-var config model; needs addon-options plumbing; own phase.

</deferred>

---

*Phase: 11-New Standalone Screens (Dashboard, Algorithms, Settings)*
*Context gathered: 2026-07-08*
