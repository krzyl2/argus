# Phase 12: Sensors Screen Rebuild - Context

**Gathered:** 2026-07-08
**Status:** Ready for planning

<domain>
## Phase Boundary

Rebuild the existing, functional Sensors screen (`SensorsPage.tsx` and its supporting
components) to the Argus Design System spec. **Markup and component structure may be
refactored, not just restyled.** Single-sensor detector assignment (hst/mad/stl) and inline
validation must remain fully working end-to-end after the rebuild.

Requirements: SEN-01 (list + filtering to DS spec), SEN-02 (single-sensor detector assignment
with inline validation — source `DetectorDefaults.cs` + `detectorParams.ts`).

**In scope:** frontend rebuild only. No new backend. Preserve `detectorParams.ts` validation
logic (parity with `InputValidator.cs`/`DetectorDefaults.cs`) and the `entityIdx` correlation
with the save-handler's alphabetical-sort ordering.

**Out of scope (→ Deferred):** backend single-sensor detector catalog endpoint; any
health/availability signal for StatusDot; changes to Pattern Filters behavior beyond restyle.

</domain>

<decisions>
## Implementation Decisions

### Detector-type picker — Select → radio-card (SEN-02 / SC3)
- **D-01:** Replace the current `<Select>` (hst/mad/stl) in `DetectorEntry` with a **radio-card
  picker** that shows the Phase 10 shared component's **2px accent-border selection state**
  (never color alone). SC3 makes the radio-card mandatory; the discussion locked *which*
  component.
- **D-02:** Reuse the existing **`AlgorithmCard`** component (Phase 10) for the hst/mad/stl
  picker — 2px accent selection + "best for…"-style label with the timing caption
  (hst = streaming, mad/stl = batch). Do **not** introduce a new bespoke card component.
- **D-03:** **Per-detector model is preserved** — a sensor may still have multiple detectors.
  "Add detector" appends another block, each with its own radio-card type picker. The radio-card
  selects the *type* of that one detector block; it does not collapse multi-detector support.

### Row interaction model — single-select-and-expand
- **D-04:** Adopt the **DS reference interaction model** (`ui_kits/admin/Sensors.jsx`): clicking a
  sensor row **selects** it (highlight with `--color-accent-soft`); **only the selected AND
  tracked** sensor expands its detector editor inline. Replaces the current model where every
  tracked row renders its own independent `<details>` disclosure simultaneously.
- **D-05:** Rationale: matches the "single-sensor detector assignment" framing and is far more
  readable at 400+ HA entities than N parallel open disclosures. Selection state is local UI
  state (which entityId is selected), not persisted.

### Area/domain browse — enable grouped sections
- **D-06:** Enable the **existing (currently unused) `groupByArea` prop** on `SensorList` for the
  rebuilt screen: collapsible per-area sections (alphabetical), with a domain/"Ungrouped"
  fallback section last — the SRCH-02 behavior already implemented in `SensorList.tsx`. SC1
  explicitly requires "area/domain browse".
  > ⚠ **DS-reference conflict flagged (Rule 7):** `ui_kits/admin/Sensors.jsx` shows a **flat**
  > filtered list with no area grouping. The roadmap SC1 requirement (area/domain browse) wins —
  > the kit's flat list is a mockup simplification, and the grouping code already exists. Use the
  > kit for row/Card/Badge visual fidelity only, not for the flat-vs-grouped structure.

### Component adoption depth — full adoption
- **D-07:** **Full adoption of Phase 10 primitives**, refactoring markup as needed:
  - Wrap the sensor list in **`Card`** (per DS reference `Card padding="none"`).
  - Replace `argus-pill`/`argus-pill--tracked` with the shared **`Badge`** ("tracked").
  - Replace the raw `<input>` elements in `DetectorParamGrid` with the shared **`Input`**
    component (built-in `label` + `error` display), driving `error` from `detectorParams.ts`.
- **D-08:** **Preserve unchanged:** all of `detectorParams.ts` validation logic and messages
  (English, operator-facing parity spec — do not reword); the `entityIdx` computation and its
  correlation with the save-handler's alphabetical-sort ordering (`SensorList.tsx` trackedEntityIdx
  → save handler); the `save`/`hasValidationErrors` state layer in `state/sensors.ts`; the
  `SaveBar` + `SaveResultBanner` flow.

### Claude's Discretion
- Exact grid/spacing/typography per screen follows the Phase 10 shared library + the
  `ui_kits/admin/Sensors.jsx` visual reference. Planner may refine layout details.
- Whether `DetectorParamGrid`'s 2-column layout is retained as-is or expressed via the DS
  reference's `gridTemplateColumns: '1fr 1fr'` is a styling detail — field set/order/defaults
  and validation must not change (D-08).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Design references (layout/visual only — NOT behavior)
- `Argus Design System/HANDOFF_TO_CLAUDE_CODE.md` — milestone design reference package (voice,
  visual foundation, component API+look specs).
- `Argus Design System/ui_kits/admin/Sensors.jsx` — Sensors reference layout: single-select row
  model (D-04), Card-wrapped list, Badge "tracked", 2-col param grid. **Flat list + `singleCatalog`
  mock are NOT authoritative** (see D-06 conflict; catalog deferred).
- `Argus Design System/ui_kits/admin/index.html` — composition reference (row/Card patterns).

### Backend + validation sources of truth (SEN-02)
- `orchestrator/Argus.Orchestrator/Web/DetectorDefaults.cs` — server-side per-detector default
  params (hst/mad/stl); source of truth for defaults sent per-entity.
- `orchestrator/ui/src/validation/detectorParams.ts` — client-side field + cross-field validation
  (parity with `InputValidator.cs`). **Preserve verbatim (D-08).**

### Frontend files being rebuilt / integrated
- `orchestrator/ui/src/components/SensorsPage.tsx` — screen entry point.
- `orchestrator/ui/src/components/SensorList.tsx` — flat vs `groupByArea` sections (enable per D-06).
- `orchestrator/ui/src/components/SensorListRow.tsx` — row markup (→ Card/Badge, select model D-04/D-07).
- `orchestrator/ui/src/components/DetectorDisclosure.tsx` — detector list per sensor.
- `orchestrator/ui/src/components/DetectorEntry.tsx` — type picker (Select → AlgorithmCard, D-01/D-02).
- `orchestrator/ui/src/components/DetectorParamGrid.tsx` — raw `<input>` → shared `Input` (D-07).
- `orchestrator/ui/src/components/AddDetectorButton.tsx`, `PatternFiltersPanel.tsx`,
  `SaveBar.tsx`, `SaveResultBanner.tsx`, `SensorSearchInput.tsx` — supporting components.
- `orchestrator/ui/src/state/sensors.ts` — state layer (`save`, `hasValidationErrors`,
  `entityEdits`, detector add/remove/update). **Preserve behavior (D-08).**
- `orchestrator/ui/src/api/types.ts` — `SensorEntry`, `DetectorEntry` types.

### Shared components to compose (Phase 10)
- `orchestrator/ui/src/components/{AlgorithmCard,Card,Badge,Input,Checkbox,SearchInput,EmptyState,Button,Disclosure}.tsx`

### Prior context
- `.planning/phases/10-design-system-foundation/10-CONTEXT.md` — component library + theme +
  focus/radio-card a11y rules.
- `.planning/phases/11-new-standalone-screens-dashboard-algorithms-settings/11-CONTEXT.md` —
  established the "kit is layout-only, not behavior" pattern; deferred single-sensor catalog endpoint.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`SensorList.groupByArea`** already implemented (SRCH-02) but unused on `#/sensors` — D-06
  enables it (flag flip + section styling, not new logic).
- **`AlgorithmCard`** (Phase 10) — reuse as the hst/mad/stl radio-card picker (D-02).
- **`detectorParams.ts`** — full validation (field + cross-field high/low), drives
  `hasValidationErrors` → SaveBar disabled state. Reuse as-is.
- **Phase 10 primitives** (Card/Badge/Input/Checkbox/SearchInput/EmptyState/Button) — compose.

### Established Patterns
- **`entityIdx` correlation**: `SensorList` computes a tracked-entity index (`trackedEntityIdx++`)
  that must stay aligned with the save handler's alphabetical sort. Refactoring row/section markup
  must not break this ordering (D-08).
- **Native `<details>`/`<summary>`** currently drives detector disclosure with no JS open-state.
  D-04's single-select model replaces this with explicit selected-entityId UI state.
- **Kit-is-layout-only** (from Phase 11): reference `.jsx` files define visuals, not behavior —
  their embedded mocks (`singleCatalog`, `ARGUS_DATA`) do NOT define Phase 12 behavior.

### Integration Points
- No backend changes. All work in `orchestrator/ui/src/**`.
- Detector defaults continue to flow from `DetectorDefaults.cs` per-entity; timing captions stay
  client-hardcoded (hst=streaming, mad/stl=batch).

</code_context>

<specifics>
## Specific Ideas

- Detector-type radio-card must show 2px accent border on selection, distinguishable without color
  alone (A11Y-02 rule from Phase 10) — reuse `AlgorithmCard`, don't re-derive.
- Validation messages in `detectorParams.ts` are a parity spec with the server — do not reword.
- Single-select row highlight uses `--color-accent-soft` per the DS reference.

</specifics>

<deferred>
## Deferred Ideas

- **StatusDot per sensor** — SC1 lists "StatusDot patterns", but `SensorEntry` has no
  health/availability signal; only "tracked" status exists (→ `Badge`). A real StatusDot for
  sensors needs a live-availability/freshness signal — own phase if desired.
- **Backend single-sensor detector catalog endpoint** — the DS reference's `singleCatalog`
  (defaults/timing/presets served from the server) has no backend; Phase 11 already deferred this.
  Stays client-hardcoded here; own phase if a real catalog is wanted.
- **Sensitivity presets for single-sensor detectors** (Low/Med/High) — reference kit hints at it
  via catalog metadata; no data source today. Own phase.

None of the above block Phase 12.

</deferred>

---

*Phase: 12-Sensors Screen Rebuild*
*Context gathered: 2026-07-08*
