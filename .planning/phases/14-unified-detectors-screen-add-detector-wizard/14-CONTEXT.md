# Phase 14: Unified Detectors Screen + Add-Detector Wizard - Context

**Gathered:** 2026-07-21
**Status:** Ready for planning
**Source:** Operator decisions captured during `/gsd-plan-phase 14` (no separate discuss-phase run) +
`14-RESEARCH.md` (HIGH-confidence, source-verified). Read `14-RESEARCH.md` first — this file records
the *decisions*; the research records the *evidence* behind them.

<domain>
## Phase Boundary

Restructure the admin IA: replace the two separate **Sensors** and **Groups** nav destinations with
**one unified "Detectors" list screen** + **one shared "Add-detector" wizard route**. This is a
**client-only** restructuring/extraction phase built entirely on reuse of Phase 10–13 Design System
components and existing v4.0 editors — **no backend changes** (D-09).

**In scope (all under `orchestrator/ui/src/**`):**
- New `/detectors` list merging groups (`GET /api/groups`) + tracked single sensors (`GET /api/sensors`).
- New `/detectors/add` wizard (thin hand-off; 1-vs-≥2 sensor branch).
- New `/detectors/sensor/:entityId` single-sensor detector-edit view extracted from `SensorsPage`.
- Sidebar + router restructure; old-route redirects.
- Relocate Pattern Filters UI to Settings (D-08).

**Out of scope (→ Deferred):** any backend change; a real single-sensor detector *catalog* +
sensitivity presets + guided "what are you monitoring?" flow for hst/mad/stl (operator chose the
hand-off + simple-editor design — D-04; literal guided-flow reuse was deferred twice already in
Phases 11/12). See `<deferred>`.

</domain>

<decisions>
## Implementation Decisions

Phase 14 inherits the locked principles from Phases 10–13 (kit = layout-only; full DS-primitive
adoption; preserve validation/behavior verbatim; reuse — don't re-derive). The decisions below
resolve the IA-restructure specifics and the three ROADMAP-vs-code gaps surfaced by research.

### Navigation / routing / sidebar
- **D-01:** **`/detectors` becomes the new default route.** In `router.ts`, repoint `normalizeHash`'s
  fallback and the boot effect from `/sensors` → `/detectors`. Add a redirect so bare `/sensors` and
  bare `/groups` rewrite to `/detectors` (preserve old bookmarks). **Do NOT touch** `parseGroupId` —
  `/groups/new` and `/groups/:id` keep working unchanged (reused via hand-off). Add two new route
  shapes: `/detectors/add` (parser-free, like the Phase 11 static routes) and
  `/detectors/sensor/:entityId` (new parser analogous to `parseGroupId`; entity ids contain dots →
  `encodeURIComponent` at link time, `decodeURIComponent` at parse time, defensive fallback to the
  list on parse failure). Add a test asserting bare `/sensors` and `/groups` normalize to `/detectors`.
- **D-02:** **Sidebar (`Sidebar.tsx`) nav restructure:** remove the `sensors` and `groups`
  `NAV_ITEMS` entries; add `detectors` (`#/detectors`) and `add-detector` (`#/detectors/add`); update
  `isActive()` so `/detectors/*` sub-routes highlight the Detectors item.

### Unified list screen
- **D-03:** **One unified DS list merging both sources.** New `state/detectors.ts` exposes a computed
  signal merging `groups` (`state/groups.ts`) + **tracked-only** single sensors (`state/sensors.ts`,
  `isTracked` entries) into one sorted row list with a `kind: 'group' | 'sensor'` discriminant.
  Namespace row `key=` as `group:${groupId}` / `sensor:${entityId}` (defense-in-depth; collision
  impossible today). New `DetectorsPage.tsx` (loads both, mounts list; models on `GroupsPage.tsx`) +
  `DetectorList.tsx` (Card-wrapped `<ul class="argus-list">`) + `DetectorListRow.tsx` (two thin
  variants under one list, relocating existing `GroupListRow`/`SensorListRow` JSX verbatim — do not
  invent new markup). Rows **only navigate** to editors (`<a href>`), no inline expand-in-place.

### Editors — reuse
- **D-04:** **Group editing reuses the existing, UNCHANGED `GroupEditorForm`** via
  `#/groups/:id` / `#/groups/new`. Zero changes to `GroupEditorForm` / `AlgorithmChooser` /
  `GuidedFlowStep` / `SensitivityPresetPicker` / `AdvancedParamsDisclosure` / `AttributionPanel` /
  `state/groups.ts` / `state/groupEditor.ts` / `validation/groupParams.ts`.
- **D-05:** **Single-sensor editing = new `/detectors/sensor/:entityId` route + new
  `SingleDetectorEditorForm.tsx`**, extracted from `SensorsPage`'s existing inline detector-assignment
  block (`DetectorDisclosure`/`DetectorEntry`/`DetectorParamGrid`/`AddDetectorButton`). Reuses
  `AlgorithmCard` for hst/mad/stl selection + `DetectorParamGrid` + `validation/detectorParams.ts`
  inline validation + `Web/DetectorDefaults.cs` server defaults. **Operator decision (ROADMAP-gap Q1):
  hand-off + simple editor — NO `GuidedFlowStep`/`SensitivityPresetPicker` on the single-sensor path**
  (those are group-catalog-bound; a real single-sensor catalog/presets is Deferred). Never mount
  `AlgorithmChooser` from this form (Pitfall 6 — cross-contaminates the group draft).

### Add-detector wizard (thin hand-off)
- **D-06:** **Wizard is a thin hand-off, not a monolithic form.** New `AddDetectorWizard.tsx` at
  `/detectors/add` owns only sensor multi-select + the 1-vs-≥2 branch. Generalize `MemberPicker.tsx`
  with an optional `minQueryLength?: number` prop (**default 2** preserves Groups' current behavior);
  the wizard passes `minQueryLength={3}` (ROADMAP ≥3-char reveal). Do NOT fork a second picker.
  - **≥2 sensors →** set `pendingPrefillMembers` (existing signal) + navigate `#/groups/new` — the
    untouched `GroupEditorForm` consumes it exactly as `AreaSuggestionBanner` does today (zero new
    receiving-end code), then the operator runs the existing guided algorithm flow.
  - **exactly 1 sensor →** `setTracked(entityId, true)` (existing) + navigate
    `#/detectors/sensor/${encodeURIComponent(entityId)}`.
- **D-07:** **Full-list-replace save safety (Pitfall 1 — CRITICAL).** `POST /api/sensors/save` and the
  client `save()` in `state/sensors.ts` are **full-list-replace** of the `entities:` key. The wizard
  AND `SingleDetectorEditorForm` MUST `loadSensors('')` (full set) on mount **before** any `save()`,
  so tracking one sensor never silently untracks every other sensor in `entities.yaml`. Add a
  regression test: save after wizard-tracking a new sensor preserves the pre-existing tracked set.

### Untrack action placement (operator decision Q3)
- **D-08a:** **Destructive "untrack sensor" lives ONLY inside `SingleDetectorEditorForm`** (mirrors the
  existing "Remove detector" affordance there), **never on the unified list row.** The list row only
  navigates. Group delete stays where it is (inside the group editor flow), not on the list row.

### Pattern Filters relocation (operator decision Q2)
- **D-08b:** **Relocate `PatternFiltersPanel` rendering to `SettingsPage.tsx`.** Its signals
  (`includePatterns`/`excludePatterns` in `state/sensors.ts`) and save path (bundled into
  `POST /api/sensors/save`) are unchanged — only the JSX mount moves off the removed browse screen.
  ⚠ **Planner must handle the coupling:** Settings today is largely read-only; giving it an editable
  pattern-filters section that persists via the sensors full-list-replace save means Settings must
  also honor D-07 (load the full sensor set before saving). Scope this deliberately (likely its own
  plan/wave), do not bolt it onto an unrelated screen without the D-07 guard.

### Zero backend
- **D-09:** **Zero backend changes.** Existing endpoints are sufficient: `GET /api/groups`,
  `GET /api/sensors[?q=]`, `POST /api/groups/save`, `POST /api/sensors/save`,
  `GET /api/detectors/catalog`, `GET /api/detectors/defaults`. If any plan proposes a backend change,
  STOP and re-confirm with the operator (it means the single-sensor-catalog deferral was violated).

### Claude's Discretion
- Exact row layout/spacing/typography per the Phase 10 shared library + `ui_kits/admin/index.html`
  visual reference. [informational]
- Whether `SensorsPage.tsx` becomes a thin redirect shim or is deleted outright once the bare
  `/sensors` route redirects (router redirect makes its render path dead code either way). [informational]
- Client-side filter over an already-loaded list vs. server `GET /api/sensors?q=` for the wizard's
  ≥3-char search — research recommends client-side (matches `MemberPicker`'s existing pattern). [informational]
- Unified-list sort order (groups-first vs. interleaved alphabetical). [informational]

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase research (read first)
- `.planning/phases/14-unified-detectors-screen-add-detector-wizard/14-RESEARCH.md` — full reuse/create/
  modify table, router/row/wizard reconciled decisions, 6 pitfalls, derived-requirements table.

### Files to CREATE (all under `orchestrator/ui/src/`)
- `components/DetectorsPage.tsx` (models on `GroupsPage.tsx`)
- `components/DetectorList.tsx` (models on `GroupList.tsx`/`SensorList.tsx`)
- `components/DetectorListRow.tsx` (two variants; relocates `GroupListRow.tsx` + `SensorListRow.tsx` JSX)
- `components/AddDetectorWizard.tsx` (new; thin)
- `components/SingleDetectorEditorForm.tsx` (extraction of `SensorsPage.tsx` inline block)
- `state/detectors.ts` (computed merge; no new fetch)

### Files to MODIFY (surgical)
- `components/Sidebar.tsx` (D-02) · `router.ts` (D-01) · `main.tsx` (route table)
- `components/MemberPicker.tsx` (add `minQueryLength?` prop, default 2 — D-06)
- `components/SensorsPage.tsx` (extract inline block → `SingleDetectorEditorForm`; then shim/delete)
- `components/SettingsPage.tsx` (host relocated `PatternFiltersPanel` — D-08b)

### Files to REUSE UNCHANGED
- `components/GroupEditorForm.tsx`, `AlgorithmChooser.tsx`, `GuidedFlowStep.tsx`,
  `SensitivityPresetPicker.tsx`, `AdvancedParamsDisclosure.tsx`, `AttributionPanel.tsx`,
  `AttributionBar.tsx` (D-04)
- `components/{Card,Badge,Checkbox,Button,SearchInput,SensorSearchInput,EmptyState,AlgorithmCard}.tsx`
- `components/sensorMatch.ts` (`matchesSensorQuery`), `components/DetectorParamGrid.tsx`,
  `components/DetectorEntry.tsx`, `components/DetectorDisclosure.tsx`, `components/AddDetectorButton.tsx`
- `state/groups.ts`, `state/groupEditor.ts`, `state/sensors.ts` (reuse signals/functions as-is)
- `validation/detectorParams.ts`, `validation/groupParams.ts`
- `api/types.ts` (`GroupConfig`, `SensorEntry`, catalog types)
- Backend: `Program.cs` endpoints, `Web/DetectorCatalog.cs`, `Web/DetectorDefaults.cs`,
  `Web/GlobExpander.cs`, `Ha/HaSensorRegistry.cs` (read-only; D-09)

### Design references (layout/visual only — NOT behavior)
- `Argus Design System/HANDOFF_TO_CLAUDE_CODE.md`, `ui_kits/admin/index.html`,
  `ui_kits/admin/shared.jsx` (`PageHeader`/`SectionLabel`/`SaveBar` wrappers).

### Prior context (siblings)
- `.planning/phases/13-groups-screen-rebuild/13-CONTEXT.md`, `12-.../12-CONTEXT.md`,
  `11-.../11-CONTEXT.md` — kit-is-layout-only, full-adoption, single-sensor-catalog deferrals.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable assets (verified in 14-RESEARCH.md)
- `pendingPrefillMembers` signal (`state/groups.ts`) — the exact hand-off channel the ≥2 wizard exit
  uses; `AreaSuggestionBanner` already drives it today (zero receiving-end code).
- `setTracked(entityId, true)` (`state/sensors.ts`) — seeds a default `hst` detector; the 1-sensor exit.
- `saveGroup()` / `save()` full-list-replace save functions — reused verbatim; only *when* signals get
  populated changes (D-07).
- `AlgorithmCard` is prop-agnostic (`name: string`, Phase 12-01) — reused by the single-sensor grid.

### Pitfalls (from research — carry into plans)
1. **Full-list-replace untrack (CRITICAL)** — `loadSensors('')` before any save (D-07).
2. Group/sensor id namespace — namespace the `key=` prop (D-03).
3. MIN_QUERY_LENGTH drift — one parameterized `MemberPicker`, not a fork (D-06).
4. Removing Sensors nav orphans Pattern Filters — relocate (D-08b).
5. Default-route change breaks `#/sensors`/`#/groups` bookmarks — add redirects + test (D-01).
6. `AlgorithmChooser` single-sync-point is group-only — never mount it in the single-sensor path (D-05).

### Integration points
- No backend changes (D-09). Unified list = client merge of `GET /api/groups` + `GET /api/sensors`.
  Wizard exits reuse existing group + sensor save paths.

</code_context>

<specifics>
## Specific Ideas

- Unified row: group variant = friendlyName/groupId title, `groupId · N members` subtitle, mode +
  detector Badges (reuse `GroupListRow`); sensor variant = friendlyName/entityId title, tracked Badge +
  assigned-detector Badge (reuse `SensorListRow` styling). Whole row links to its editor.
- Wizard: single `MemberPicker` (minQueryLength=3) → live count → primary Button label switches
  ("Configure detector" for 1, "Create group" for ≥2) → navigate on click.
- Settings: add a "Auto-track patterns" section rendering the relocated `PatternFiltersPanel`, with its
  own SaveBar honoring D-07 (full sensor set loaded first).

</specifics>

<deferred>
## Deferred Ideas

- **Literal single-sensor guided flow + sensitivity presets** — a real `Web/SingleDetectorCatalog.cs`
  + `GET /api/detectors/single-catalog` + generalized `SensitivityPresetPicker` for hst/mad/stl.
  Operator chose the hand-off + simple-editor design (D-05); deferred twice before (11/12-CONTEXT.md).
  Own phase if genuinely wanted.
- **Untrack action on the list row** — rejected (D-08a) in favor of untrack-in-editor.
- **Combined `GET /api/detectors` backend endpoint** — not needed; client merge suffices (D-09).

None of the above block Phase 14.

</deferred>

---

*Phase: 14 — Unified Detectors Screen + Add-Detector Wizard*
*Context captured: 2026-07-21 (operator decisions + 14-RESEARCH.md)*
