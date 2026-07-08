# Phase 12: Sensors Screen Rebuild - Research

**Researched:** 2026-07-08
**Domain:** Preact SPA frontend rebuild (component composition + interaction-model refactor), no backend changes
**Confidence:** HIGH

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

**Detector-type picker — Select → radio-card (SEN-02 / SC3)**
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

**Row interaction model — single-select-and-expand**
- **D-04:** Adopt the **DS reference interaction model** (`ui_kits/admin/Sensors.jsx`): clicking a
  sensor row **selects** it (highlight with `--color-accent-soft`); **only the selected AND
  tracked** sensor expands its detector editor inline. Replaces the current model where every
  tracked row renders its own independent `<details>` disclosure simultaneously.
- **D-05:** Rationale: matches the "single-sensor detector assignment" framing and is far more
  readable at 400+ HA entities than N parallel open disclosures. Selection state is local UI
  state (which entityId is selected), not persisted.

**Area/domain browse — enable grouped sections**
- **D-06:** Enable the **existing (currently unused) `groupByArea` prop** on `SensorList` for the
  rebuilt screen: collapsible per-area sections (alphabetical), with a domain/"Ungrouped"
  fallback section last — the SRCH-02 behavior already implemented in `SensorList.tsx`. SC1
  explicitly requires "area/domain browse".
  > ⚠ **DS-reference conflict flagged (Rule 7):** `ui_kits/admin/Sensors.jsx` shows a **flat**
  > filtered list with no area grouping. The roadmap SC1 requirement (area/domain browse) wins —
  > the kit's flat list is a mockup simplification, and the grouping code already exists. Use the
  > kit for row/Card/Badge visual fidelity only, not for the flat-vs-grouped structure.

**Component adoption depth — full adoption**
- **D-07:** **Full adoption of Phase 10 primitives**, refactoring markup as needed:
  - Wrap the sensor list in **`Card`** (per DS reference `Card padding="none"`).
  - Replace `argus-pill`/`argus-pill--tracked` with the shared **`Badge`** ("tracked").
  - Replace the raw `<input>` elements in `DetectorParamGrid` with the shared **`Input`**
    component (built-in `label` + `error` display), driving `error` from `detectorParams.ts`.
  - **Research correction:** the ported `Input.tsx` has no built-in `label`/`error` rendering
    today (that's the DS *spec*, not the current component) — see Pattern 4 below for the actual
    wrapping approach to use.
- **D-08:** **Preserve unchanged:** all of `detectorParams.ts` validation logic and messages
  (English, operator-facing parity spec — do not reword); the `entityIdx` computation and its
  correlation with the save-handler's alphabetical-sort ordering (`SensorList.tsx` trackedEntityIdx
  → save handler); the `save`/`hasValidationErrors` state layer in `state/sensors.ts`; the
  `SaveBar` + `SaveResultBanner` flow.
  - **Research correction:** see Summary — the "save-handler correlation" is not an actual wire
    contract in the current architecture; the invariant that matters in practice is DOM-id
    uniqueness of the `trackedEntityIdx` counter across renders. Preserving the counter mechanism
    satisfies D-08's intent.

### Claude's Discretion
- Exact grid/spacing/typography per screen follows the Phase 10 shared library + the
  `ui_kits/admin/Sensors.jsx` visual reference. Planner may refine layout details.
- Whether `DetectorParamGrid`'s 2-column layout is retained as-is or expressed via the DS
  reference's `gridTemplateColumns: '1fr 1fr'` is a styling detail — field set/order/defaults
  and validation must not change (D-08).

### Deferred Ideas (OUT OF SCOPE)
- **StatusDot per sensor** — SC1 lists "StatusDot patterns", but `SensorEntry` has no
  health/availability signal; only "tracked" status exists (→ `Badge`). A real StatusDot for
  sensors needs a live-availability/freshness signal — own phase if desired.
- **Backend single-sensor detector catalog endpoint** — the DS reference's `singleCatalog`
  (defaults/timing/presets served from the server) has no backend; Phase 11 already deferred this.
  Stays client-hardcoded here; own phase if a real catalog is wanted.
- **Sensitivity presets for single-sensor detectors** (Low/Med/High) — reference kit hints at it
  via catalog metadata; no data source today. Own phase.

None of the above block Phase 12.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| SEN-01 | Sensors screen rebuilt to Design System spec (list, filtering) | Patterns 2/3 (single-select-and-expand row model, `groupByArea` enablement) + "Card-wrapped list" code example + `.argus-page-header` precedent from `DashboardPage.tsx` |
| SEN-02 | Single-sensor detector assignment (hst/mad/stl) with inline validation — source `DetectorDefaults.cs` + `detectorParams.ts`; markup and component structure may be refactored, not just restyled | Pattern 1 (widen `AlgorithmCard` to plain-string props for the radio-card picker) + Pattern 4 (`Input`/`FieldValidationError` wrapping for inline validation) + Pitfalls 2-4 (aria-describedby, step attribute, type-safety around the widened `AlgorithmCard`) |

</phase_requirements>

## Summary

This phase rebuilds `SensorsPage.tsx` and its component tree against the Argus Design System,
composing the Phase 10 shared primitives (`Card`, `Badge`, `AlgorithmCard`, `Input`, `Textarea`,
`SearchInput`, `Checkbox`, `Button`, `Disclosure`) while preserving three behavioral invariants
verbatim: `detectorParams.ts` validation, the `entityIdx` DOM-id numbering scheme, and the
`state/sensors.ts` save/validation state layer. All source files were read in full; the codebase
already contains everything needed — no new dependencies, no new CSS tokens, no backend changes.

Two things in CONTEXT.md's decisions do not match the code exactly and need correction before
planning tasks are written: (1) the shared `Input`/`Textarea` components have **no built-in
`label`/`error` props** — D-07's "built-in label + error display" describes the Design System's
*spec* (`Input.d.ts`), not the ported `Input.tsx`, which only takes `value/onChange/type/
placeholder/ariaLabel/disabled/invalid`; every existing consumer (`SettingsPage.tsx`) wraps it
manually with an external `<span class="argus-param-field__label">`. (2) The existing
`AlgorithmCard.tsx` is typed against `DetectorCatalogEntry`/`GroupDetectorName` (group detectors:
peer_divergence/ecod/copod/pca/iforest) — it cannot accept `hst`/`mad`/`stl` without a type
change. The Design System's own spec (`AlgorithmCard.d.ts`) defines the props as **plain strings**
(`name: string`, `bestFor: string`, `onSelect: (name: string) => void`) — the ported component was
over-typed for its one Phase-11 caller. Widening it back to the spec's generic shape is the
correct fix, not a hack, and it is the only way to satisfy D-02 ("do not introduce a new bespoke
card component").

Also load-bearing: the `entityIdx` "correlation with the save-handler's alphabetical-sort
ordering" described in CONTEXT.md is **not actually a client-server wire contract** in the current
(Phase 7+) architecture — confirmed by reading `Program.cs`'s `/api/sensors/save` handler and
`InputValidator.cs`. The server keys everything by `entityId` string (dictionary lookups), and
independently recomputes its own alphabetical index server-side; the client never sends a numeric
index. `entityIdx` is purely a **client-side DOM-id-uniqueness mechanism** (feeds
`aria-describedby`/`id` on param inputs and the `aria-label` on the type picker) fed by a running
counter closed over across the whole render (`SensorList`'s `let trackedEntityIdx`). The real
risk during refactor is producing **duplicate DOM ids** (e.g. by resetting the counter per
`groupByArea` section, or switching to `Array.prototype.map` index instead of the shared
counter) — not breaking a save-time correlation that doesn't exist. Preserve the single shared
counter; that is sufficient.

**Primary recommendation:** Keep `state/sensors.ts`, `detectorParams.ts`, and the `trackedEntityIdx`
counter mechanism completely untouched. Refactor markup only in
`SensorsPage/SensorList/SensorListRow/DetectorDisclosure/DetectorEntry/DetectorParamGrid`, add one
new piece of local-only selection state (`selectedEntityId`, lives in `SensorsPage.tsx`, not in
`state/sensors.ts`), and widen `AlgorithmCard`'s props to plain strings so it can serve both the
group-detector grid (Phase 11) and the new hst/mad/stl picker (Phase 12) without a second card
component.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Sensor list rendering + search/filter | Browser / Client (Preact) | API/Backend (data + `q=` filter) | `GET /api/sensors?q=` already does server-side filtering; SPA renders results, computes `groupByArea` sections client-side |
| Row selection / expand state | Browser / Client | — | D-05: explicitly local UI state, never persisted or sent to server |
| Detector type picker (hst/mad/stl) | Browser / Client | — | Pure UI selection; committed value flows into existing `entityEdits` signal (unchanged) |
| Field validation (inline) | Browser / Client (`detectorParams.ts`) | API/Backend (`InputValidator.cs`, defense-in-depth) | Client validation gates the Save button; server re-validates on every POST regardless — dual validation is intentional, not redundant |
| Detector defaults | Browser / Client (`DETECTOR_DEFAULTS` const) | API/Backend (`DetectorDefaults.cs`, unused by SPA today) | Client constructs new detector entries locally to avoid a round-trip (WR-02); the two tables must stay in sync manually — this phase does not touch either |
| Save / persistence | API/Backend (`POST /api/sensors/save`) | Database/Storage (`entities.yaml` via `ConfigWriter`) | Out of scope — no backend changes this phase |

## Standard Stack

No new packages. This phase is 100% composition of existing in-repo components using the
existing toolchain.

### Core (already installed — versions from `orchestrator/ui/package.json`)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| preact | 10.29.3 | Component runtime | Locked project-wide (v4.0) |
| @preact/signals | 2.9.2 | Reactive state (`state/sensors.ts`) | Locked project-wide |
| vitest | 4.1.9 | Test runner | Locked project-wide |
| @testing-library/preact | 3.2.4 | Component test rendering | Locked project-wide |

### Alternatives Considered
None — this is a rebuild constrained to the existing stack; introducing any new library would
violate the "frontend rebuild only, no new dependencies" framing implicit in CONTEXT.md's scope.

**Installation:** none required.

## Package Legitimacy Audit

Not applicable — no new packages are introduced by this phase.

## Architecture Patterns

### System Architecture Diagram

```
GET /api/sensors?q=<query>                 POST /api/sensors/save
        |                                          ^
        v                                          |
+-------------------- SensorsPage.tsx -------------------------+
| loads: sensors[], entityEdits{}, saveState, hasValidationErrors|
| owns (NEW, local-only): selectedEntityId                       |
+------------------------------+---------------------------------+
                |                                |
                v                                v
        SensorSearchInput                   SensorList
        (SearchInput wrapper,               (flat OR groupByArea sections;
         unchanged)                          shared trackedEntityIdx counter
                                              spans BOTH modes)
                                                   |
                                                   v
                                          SensorListRow  (per entry)
                                          - row click -> onSelectRow(entityId)
                                          - checkbox click -> onToggleTracked (stopPropagation)
                                          - highlight: selected === entry.entityId
                                          - expand panel only if: selected AND isTracked
                                                   |
                                                   v
                                          DetectorDisclosure (per selected+tracked row)
                                                   |
                                                   v
                                          DetectorEntry (per detector in that sensor)
                                          - AlgorithmCard x3 (hst/mad/stl) replaces <Select>
                                          - onTypeChange -> updateDetectorName (state/sensors.ts, unchanged)
                                                   |
                                                   v
                                          DetectorParamGrid
                                          - Input (wrapped w/ external label+FieldValidationError)
                                          - errors from validateDetectorParams() (unchanged)
                                                   |
                                                   v
                                          entityEdits signal -> hasValidationErrors computed
                                                   |
                                                   v
                                          SaveBar (disabled=hasValidationErrors) -> save()
                                                   |
                                                   v
                                          SaveResultBanner (unchanged)
```

Data/behavior that must NOT be touched by this diagram's refactor: `state/sensors.ts` (all
exports), `validation/detectorParams.ts` (all exports), `api/types.ts` shapes, `SaveBar`,
`SaveResultBanner`.

### Recommended Project Structure
No new files/folders needed beyond the existing flat `orchestrator/ui/src/components/` layout.
Every file this phase touches already exists; no renames.

### Pattern 1: Widen `AlgorithmCard` to primitive props (required for D-01/D-02)
**What:** Change `AlgorithmCardProps` from `{ entry: DetectorCatalogEntry; guidedRecommended; ... }`
to `{ name: string; bestFor: string; selected: boolean; recommended: boolean; onSelect: (name: string) => void }`
— exactly matching the Design System's own spec (`Argus Design System/components/selection/AlgorithmCard.d.ts`).
**When to use:** Any radio-card selection among named options with a "best for" caption — group
detectors (Phase 11, existing) AND single-sensor hst/mad/stl (Phase 12, this phase).
**Why safe:** `AlgorithmChooser.tsx` (the only existing caller) passes `entry={entry}` where
`entry: DetectorCatalogEntry` already has both a `.name` and `.bestFor` field — updating the call
site to `name={entry.name} bestFor={entry.bestFor}` is a one-line change with no behavior change.
**Example:**
```tsx
// Source: Argus Design System/components/selection/AlgorithmCard.d.ts (spec) —
// current orchestrator/ui/src/components/AlgorithmCard.tsx over-narrows this to GroupDetectorName.
interface AlgorithmCardProps {
  name: string;
  bestFor: string;
  selected: boolean;
  recommended: boolean;      // renamed from guidedRecommended, or keep either name — planner's call
  onSelect: (name: string) => void;
}

// New Sensors usage (DetectorEntry.tsx), replacing the <Select>:
const DETECTOR_TYPES: { name: 'hst' | 'mad' | 'stl'; bestFor: string }[] = [
  { name: 'hst', bestFor: 'Streaming — reacts within ~2 s of a state change.' },
  { name: 'mad', bestFor: 'Batch — robust median-based outlier detection.' },
  { name: 'stl', bestFor: 'Batch — seasonal/trend decomposition for periodic data.' },
];

<div class="argus-algorithm-chooser__grid" role="radiogroup" aria-label={`Detector type for entity ${entityIdx}`}>
  {DETECTOR_TYPES.map((t) => (
    <AlgorithmCard
      key={t.name}
      name={t.name}
      bestFor={t.bestFor}
      selected={detector.name === t.name}
      recommended={false}
      onSelect={(name) => onTypeChange(name as 'hst' | 'mad' | 'stl')}
    />
  ))}
</div>
```
The `bestFor` copy above is a reasonable client-hardcoded caption (matches D-06's "timing captions
stay client-hardcoded" and reuses the existing `timingCaption` wording already in `DetectorEntry.tsx`
— reuse that exact string, do not invent new copy) — flagged `[ASSUMED]` since it is not sourced
from `DetectorCatalog.cs` (that catalog is for group detectors only, per Phase 11).

### Pattern 2: Single-select-and-expand row (D-04)
**What:** Row click sets `selectedEntityId` (local state in `SensorsPage.tsx`); only the row
where `entry.entityId === selectedEntityId && isTracked` renders the detector panel.
**When to use:** Exactly this screen, per D-04/D-05.
**Example:**
```tsx
// Source: Argus Design System/ui_kits/admin/Sensors.jsx (layout reference only — not behavior)
// SensorsPage.tsx
import { useState } from 'preact/hooks';
// ...
const [selectedEntityId, setSelectedEntityId] = useState<string | null>(null);
// pass selectedEntityId + setSelectedEntityId down through SensorList -> SensorListRow

// SensorListRow.tsx
<li
  class={`argus-list-row${isTracked ? ' argus-list-row--tracked' : ''}${
    isSelected ? ' argus-list-row--selected' : ''
  }`}
  onClick={onSelectRow}
>
  {/* checkbox must stopPropagation so clicking it doesn't also fire row select */}
  <Checkbox
    checked={isTracked}
    ariaLabel={entry.entityId}
    onChange={onToggleTracked}
    // Checkbox.tsx has no onClick prop today — wrap in a span with onClick stopPropagation,
    // or extend Checkbox.tsx with an optional onClick passthrough. Verify during planning.
  />
  {/* ...row content... */}
  {isSelected && isTracked && (
    <DetectorDisclosure ... />
  )}
</li>
```
**Landmine:** `Checkbox.tsx` has no `onClick`/stopPropagation escape hatch. Today's row has no
click handler at all (clicking anywhere activates the `<label>`'s wrapped input via native HTML
`<label>` semantics — see `style={{ display: 'contents' }}` on the wrapping `<label>` in the
current `SensorListRow.tsx`). Once the row gets an `onClick` for selection, a raw click on the
checkbox will bubble and ALSO trigger row-select (harmless — selection is idempotent/toggle-free,
not an on/off toggle — so double-firing is not actually a correctness bug), but a click on the
checkbox that lands on the `<label>` will fire the native checkbox toggle via label semantics
AND the row's onClick via bubbling. Simplest safe fix: keep the checkbox OUTSIDE the label-wraps-
row pattern, give it an explicit `onClick={(e) => e.stopPropagation()}` wrapper `<span>`, and give
the rest of the row (not the checkbox) the `onClick={onSelectRow}` handler. Do not rely on
`<label style="display:contents">` once row-level onClick exists — the DS reference
(`Sensors.jsx`) does exactly this: `<Checkbox ... onChange={(e) => { e.stopPropagation(); onToggle(); }} />` sitting inside a row `<div onClick={onSelect}>` with no `<label>` wrapper.

### Pattern 3: `groupByArea` enablement (D-06)
**What:** Pass `groupByArea` (hardcoded `true`, or read from a constant) to the existing
`<SensorList groupByArea>` call in `SensorsPage.tsx`. No changes needed inside `SensorList.tsx`
itself — the grouping logic (SRCH-02) is already correct and already shares the single
`trackedEntityIdx` counter across all sections (verified by reading the code: `renderRow` is one
closure used by both the flat-`.map` and the grouped-`sectionEntries.map` code paths).
**When to use:** Exactly this screen, per D-06.
**Verification point for planner:** After D-04's single-select refactor, re-verify `renderRow`
still returns one `<SensorListRow>` per entry with correctly incrementing `entityIdx` in both
flat and grouped modes — the counter mechanism is orthogonal to the row's internal markup, so this
should require no additional code, only a passing test.

### Pattern 4: `Input`/`Textarea` label+error wrapping (correcting D-07's premise)
**What:** `Input.tsx`/`Textarea.tsx` do NOT render a label or error message internally. The
existing, established repo pattern (used today in `SettingsPage.tsx`) is: wrap externally with
`<div class="argus-param-field">`, a `<span class="argus-param-field__label">`, the `<Input>`
itself (using its `invalid` boolean prop, not a string `error` prop), and — for
`DetectorParamGrid` specifically — the existing `<FieldValidationError message={error} />`
component for the message text (this already exists, is already wired to
`validateDetectorParams()`, and needs no changes).
**Example:**
```tsx
// Source: orchestrator/ui/src/components/SettingsPage.tsx (existing repo convention)
// DetectorParamGrid.tsx refactor — replace raw <input> with <Input>, keep label/error external:
<div class="argus-param-field${error ? ' argus-param-field--error' : ''}">
  <label class="argus-param-field__label" for={inputId}>{field.label}</label>
  <Input
    value={detector.params[field.key] ?? ''}
    onChange={(v) => onParamChange(field.key, v)}
    type="number"
    invalid={!!error}
    ariaLabel={field.label}
  />
  <FieldValidationError message={error} />
</div>
```
**Decision needed at plan time (flag as open, not blocking):** `Input.tsx` currently has no
`step` prop (used today for `high_threshold`/`low_threshold`/`frozen_variance_threshold`/
`threshold` fields' `step="0.01"`/`"0.0001"`/`"0.1"`) and no `id`/`aria-describedby` passthrough
(needed to keep `FieldValidationError`'s `aria-describedby` linkage working). `Input.tsx` will
need `step`, `id`, and `aria-describedby` added as optional passthrough props — a small,
additive change to the shared component, not a new component. This does not conflict with any
other consumer (`SettingsPage.tsx`'s 2 numeric inputs can pass `step` too, harmlessly optional).

### Anti-Patterns to Avoid
- **Reintroducing a bespoke card component for the type picker:** D-02 explicitly forbids this;
  widen `AlgorithmCard` instead (Pattern 1).
- **Resetting `trackedEntityIdx` per `groupByArea` section:** would produce duplicate DOM ids
  across sections (e.g. two "Salon" and "Sypialnia" sections each starting at 0) — breaks
  `aria-describedby` uniqueness. Keep the counter as a single closure spanning the whole render.
- **Treating `entityIdx` as a value that must be sent to or matched against the server:** it is
  not part of the wire contract (see Summary) — do not add any code that tries to keep it "in
  sync" with a server-computed index; there is nothing to sync.
- **Storing `selectedEntityId` in `state/sensors.ts`:** D-05 requires this to be local, ephemeral
  UI state — putting it in the persisted signal module would make it survive across route
  navigations/reloads unnecessarily and mixes concerns with the save-relevant state.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Radio-card selection with 2px accent border, never color alone | New card component | Widened `AlgorithmCard` (Pattern 1) | D-02 mandate; a11y rule (A11Y-02) already solved once in Phase 10 |
| Debounced search | New debounce logic | `SensorSearchInput` (already wraps `SearchInput`, unchanged) | Already correct, already tested (`SensorSearchInput.test.tsx`) |
| Field-level validation messages | New validation logic in components | `validateDetectorParams()` / `hasAnyError()` from `detectorParams.ts` (unchanged) | D-08 explicit preservation; parity spec with `InputValidator.cs` |
| Detector defaults | New default tables | `DETECTOR_DEFAULTS` in `state/sensors.ts` (unchanged) | Must stay byte-identical to `DetectorDefaults.cs` per existing WR-02 comment |

**Key insight:** Every "hard part" of this screen (validation, defaults, save orchestration,
debounced search, area grouping) is already solved and tested in the current codebase. The phase
is a pure presentation/interaction-model refactor around unchanged business logic.

## Runtime State Inventory

Not applicable — this is not a rename/refactor/migration-of-identifiers phase. No stored data,
service config, OS-registered state, secrets, or build artifacts carry any string being renamed.

## Common Pitfalls

### Pitfall 1: Checkbox click bubbling into new row-level onClick
**What goes wrong:** Adding `onClick` to the `<li>`/row wrapper for D-04's row-select causes a
checkbox click to also select the row (usually harmless) or, if the old `<label
style="display:contents">` wrapper is kept, causes double-toggling/inconsistent focus behavior.
**Why it happens:** Native `<label>` + wrapped `<input>` semantics plus a new ancestor `onClick`
compound unpredictably.
**How to avoid:** Follow Pattern 2 exactly — drop the `<label style="display:contents">` wrapper,
put `stopPropagation()` on the checkbox's change handler or on an intermediate span, put
`onClick={onSelectRow}` on the row content area (not on the checkbox's own DOM node).
**Warning signs:** Clicking the checkbox visibly also highlights/selects the row in a way that
looks like a second, unwanted state change (not fatal, but a UAT-visible defect worth avoiding).

### Pitfall 2: Losing the `aria-describedby` link when wrapping raw `<input>` with `<Input>`
**What goes wrong:** `FieldValidationError` renders `<span id={`${inputId}-err`}>`; the current
raw `<input>` wires `aria-describedby={`${inputId}-err`}`. If `Input.tsx` isn't given an
`aria-describedby` passthrough prop, this a11y link silently breaks (screen readers stop
announcing the error on focus).
**Why it happens:** `Input.tsx`'s current prop surface has no `id`/`aria-describedby`.
**How to avoid:** Add optional `id` and `ariaDescribedby` (or generic `...rest` passthrough of
native `input` attributes) to `Input.tsx` as part of this phase's component work.
**Warning signs:** axe/manual a11y check shows the error text is not associated with its input.

### Pitfall 3: Assuming `AlgorithmCard`'s `entry` prop can just accept a fake `DetectorCatalogEntry`
**What goes wrong:** A tempting shortcut is to build a fake `DetectorCatalogEntry`-shaped object
(`{ name: 'hst' as any, bestFor: '...', presets: [], paramSchema: [] }`) to avoid touching
`AlgorithmCard.tsx`. This works today by accident (TS structural typing + unused fields) but
casts `'hst'` to `GroupDetectorName` via `as any`, silently breaking type safety, and leaves the
group/single-sensor domains coupled through a shared type that shouldn't exist.
**Why it happens:** Looks like less work than widening the shared component.
**How to avoid:** Do Pattern 1 properly — widen `AlgorithmCard` to plain strings. It is a smaller,
cleaner diff than the workaround and matches the DS's own spec.
**Warning signs:** Any `as any`/`as GroupDetectorName` cast on an `'hst'|'mad'|'stl'` value.

### Pitfall 4: Detector param `step` attribute regression
**What goes wrong:** `DetectorParamGrid`'s current raw `<input>` sets `step={field.step}` for
`high_threshold` (`0.01`), `low_threshold` (`0.01`), `frozen_variance_threshold` (`0.0001`), and
detector `threshold` fields (`0.1`). If `<Input>` doesn't forward a `step` prop, the number
spinner's increment reverts to the browser default of `1`, degrading the input UX (not a
validation-correctness bug — `detectorParams.ts` still validates on blur/change regardless — but
a silent UX regression against current behavior).
**How to avoid:** Add `step` as an optional prop to `Input.tsx` (see Pattern 4 open decision).
**Warning signs:** Manually testing the HST detector's `high_threshold` field with the native
number-input spinner increments by 1.0 instead of 0.01.

### Pitfall 5: `groupByArea` fallback bucket key collision with a real area literally named "Ungrouped"
**What goes wrong:** `SensorList.tsx`'s existing grouping key is
`` `__domain__:${entry.domain || 'Ungrouped'}` `` — this is pre-existing code, not something this
phase writes, so it is not a new risk, but the planner should NOT "fix" or touch this key format
while refactoring markup nearby, since D-08 requires the `entityIdx` counter and section logic to
be otherwise unchanged. Flagging so it isn't accidentally "cleaned up" as unrelated scope creep.

## Code Examples

### Enabling groupByArea (no logic change, just the call site)
```tsx
// Source: orchestrator/ui/src/components/SensorsPage.tsx (existing file, one-line change)
<SensorList
  entries={sensors.value}
  query={query.value}
  edits={entityEdits.value}
  groupByArea
  // ...existing handler props unchanged...
/>
```

### Page header pattern already established (Phase 11 precedent — reuse, don't invent)
```tsx
// Source: orchestrator/ui/src/components/DashboardPage.tsx (existing, already-shipped pattern)
<header class="argus-page-header">
  <h1 class="argus-page-header__title">Sensors</h1>
  <p class="argus-page-header__subtitle">
    Select the sensors Argus monitors and assign detectors to each.
  </p>
</header>
```
Use this instead of the current `<p class="argus-heading">`/`<p class="argus-body">` pair in
`SensorsPage.tsx` — `.argus-page-header` styles already exist in `argus.css` (lines ~1240-1260)
and this is the exact pattern Phase 11 established for the other rebuilt/new screens.

### Card-wrapped list per D-07
```tsx
// Source: orchestrator/ui/src/components/DashboardPage.tsx composes Card padding="none"
// around .argus-list-row items — same pattern this phase applies to SensorList's <ul>.
<Card padding="none">
  <ul class="argus-list">{entries.map(renderRow)}</ul>
</Card>
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|---------------|--------|
| `<Select>` (hst/mad/stl) | `AlgorithmCard` radio-card grid | This phase (D-01/D-02) | Matches A11Y-02 (2px border, never color alone) |
| Independent `<details>` per tracked row | Single-select-and-expand (D-04) | This phase | Scales to 400+ HA entities without N open panels |
| Raw `.argus-pill--tracked` span | `<Badge tone="tracked">` | This phase (D-07) | Same visual, shared component |
| Flat `<ul>` only | `groupByArea` sections enabled | This phase (D-06) | SRCH-02 code already existed, unused until now |

**Deprecated/outdated:** none — no third-party API or library version changes involved.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `bestFor` caption text for hst/mad/stl in the new `AlgorithmCard` picker reuses the existing `DetectorEntry.tsx` `timingCaption` wording ("streaming (live, ~2 s reload)" / "batch (runs every N min)") rather than new copy | Pattern 1 | Low — cosmetic only; easy to adjust in review, no functional impact |
| A2 | `recommended`/`guidedRecommended` prop can be safely renamed during the `AlgorithmCard` widening, or kept as-is | Pattern 1 | Low — purely a naming choice, either satisfies both callers |
| A3 | Adding `step`/`id`/`ariaDescribedby` as new optional props to `Input.tsx` will not require touching any other existing `Input` consumer (`SettingsPage.tsx`) | Pattern 4, Pitfall 2/4 | Low — additive/optional props are backward compatible by construction; verified by reading all 6 `SettingsPage.tsx` call sites |

**If this table is empty:** N/A — see above; none of these are compliance/retention/security/perf
claims, all are low-risk implementation-detail choices the planner can make directly.

## Open Questions

1. **Should `Checkbox.tsx` gain an `onClick` passthrough, or should the stopPropagation wrapper live in `SensorListRow.tsx` only?**
   - What we know: `Checkbox.tsx` today has `checked/onChange/ariaLabel/disabled` only, no click handler.
   - What's unclear: whether other future consumers of row-level click-to-select will hit the same bubbling issue (e.g. Groups' `MemberPicker.tsx` renders its own checkbox rows already, per STATE.md's Phase 08-03 decision — it may already face this).
   - Recommendation: keep the fix local to `SensorListRow.tsx` (wrap the checkbox in a
     `<span onClick={(e) => e.stopPropagation()}>` or extend `onChange`'s event object) rather than
     changing the shared `Checkbox.tsx` API — smaller blast radius, and `MemberPicker.tsx` is out of
     this phase's scope.

2. **Exact final `.argus-list-row--selected` CSS is not yet in `argus.css`.**
   - What we know: `--color-accent-soft` token already exists and is used elsewhere (e.g.
     `.argus-nav-item--active` pattern around line 936/1198 in `argus.css`) for the identical
     "soft accent background = selected/active" visual.
   - What's unclear: exact declaration block needed for `.argus-list-row--selected` (likely just
     `background: var(--color-accent-soft);` matching the DS reference's inline style).
   - Recommendation: add a small new CSS rule (`.argus-list-row--selected { background:
     var(--color-accent-soft); }`) in `argus.css` alongside the existing `.argus-list-row--tracked`
     comment block — trivial, low-risk, planner should just do it as part of the row refactor task.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Node.js | `npm run build` / `npm run test` | ✓ | v26.3.0 | — |
| npm | package scripts | ✓ | 11.16.0 | — |
| vitest | test runner | ✓ (devDependency, already installed) | 4.1.9 | — |

**Missing dependencies with no fallback:** none.
**Missing dependencies with fallback:** none — this phase needs nothing beyond what's already
installed in `orchestrator/ui/`.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | vitest 4.1.9 + @testing-library/preact 3.2.4 |
| Config file | `orchestrator/ui/vitest.config.ts` |
| Quick run command | `npm run test -- --run <path-to-file>` (from `orchestrator/ui/`) |
| Full suite command | `npm run test -- --run` (from `orchestrator/ui/`) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| SEN-01 | List/filter renders in Card, groupByArea sections appear alphabetically with domain/Ungrouped fallback last | unit (render) | `npm run test -- --run src/components/SensorList.test.tsx` | ❌ Wave 0 (new file — no `SensorList.test.tsx` exists today) |
| SEN-01 | Row click selects (highlight class), does not toggle tracked state | unit (render + fireEvent) | `npm run test -- --run src/components/SensorListRow.test.tsx` | ✅ exists, needs new test cases added for selection behavior |
| SEN-02 | AlgorithmCard radio-card shows 2px accent border on selection, not color alone (class-based assertion) | unit (render) | `npm run test -- --run src/components/AlgorithmCard.test.tsx` | ❌ Wave 0 (new file — `AlgorithmCard.tsx` has no test today) |
| SEN-02 | Detector type selection via AlgorithmCard still calls `updateDetectorName` correctly | unit (render + fireEvent) | `npm run test -- --run src/components/DetectorEntry.test.tsx` | ❌ Wave 0 (new file — `DetectorEntry.tsx` has no test today) |
| SEN-02 | Inline validation errors still render per `detectorParams.ts` rules after `<Input>` swap | unit (render) | `npm run test -- --run src/components/DetectorParamGrid.test.tsx` | ❌ Wave 0 (new file) |
| SEN-02 | `detectorParams.ts` validation logic itself — regression guard, unchanged | unit | `npm run test -- --run src/validation/detectorParams.test.ts` | ✅ exists, no changes needed (D-08) |
| SEN-02 | `entityIdx`/counter uniqueness across groupByArea sections | unit | `npm run test -- --run src/components/SensorList.test.tsx` | ❌ Wave 0 (fold into the new SensorList.test.tsx) |
| SEN-01/02 | `state/sensors.ts` save/validation state layer — regression guard, unchanged | unit | `npm run test -- --run src/state/sensors.test.ts` | ✅ exists, no changes needed (D-08) |

### Sampling Rate
- **Per task commit:** `npm run test -- --run <touched-test-file(s)>`
- **Per wave merge:** `npm run test -- --run` (full suite) + `npm run build` (tsc -b && vite build — catches type errors from the `AlgorithmCard`/`Input` prop-shape changes across all call sites, including `AlgorithmChooser.tsx`)
- **Phase gate:** Full suite green + `npm run build` green before `/gsd-verify-work`

### Wave 0 Gaps
- [ ] `src/components/SensorList.test.tsx` — new file; covers groupByArea rendering + section
      ordering + shared `trackedEntityIdx` uniqueness across sections (SEN-01)
- [ ] `src/components/AlgorithmCard.test.tsx` — new file; covers the widened generic props,
      2px-border selection class assertion (A11Y-02 regression guard), and that both the group
      grid (`AlgorithmChooser.tsx`) and the new single-sensor grid still render correctly after
      the prop-shape change
- [ ] `src/components/DetectorEntry.test.tsx` — new file; covers hst/mad/stl selection replacing
      the old `<Select>`-based test coverage that never existed for this file
- [ ] `src/components/DetectorParamGrid.test.tsx` — new file; covers `<Input>` swap preserves
      `aria-describedby`/`aria-invalid`/`step` and error message rendering
- [ ] Update `src/components/SensorListRow.test.tsx` — existing file; add cases for the new
      `selected`/`onSelectRow` props and the "checkbox click doesn't fire row-select in a way
      that breaks toggle" interaction
- [ ] Update `src/components/AlgorithmChooser.test.tsx` — existing file; verify it still passes
      after `AlgorithmCard`'s prop-shape widening (call-site change: `entry={entry}` →
      `name={entry.name} bestFor={entry.bestFor}`)

## Security Domain

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Unchanged — `IsAuthorizedRequest` gate on `/api/sensors*` untouched by this phase |
| V3 Session Management | no | No session changes |
| V4 Access Control | no | No access-control changes |
| V5 Input Validation | yes | Client-side `detectorParams.ts` (preserved verbatim, D-08) is a UX convenience only; the authoritative boundary remains server-side `InputValidator.cs` (untouched, out of scope) — this phase must not weaken or bypass the client validation gating `hasValidationErrors`/`SaveBar`'s disabled state |
| V6 Cryptography | no | No crypto surface in this phase |

### Known Threat Patterns for this stack
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Client-only validation trusted as sole gate | Tampering | Not a new risk introduced by this phase — server-side `InputValidator.cs` already re-validates every `/api/sensors/save` POST independently (confirmed by reading `Program.cs`); this phase changes only the client's validation UI plumbing (`<Input invalid>` instead of raw `<input aria-invalid>`), not the trust boundary |
| Reflected/stored XSS via friendly_name or entity_id in rendered rows | Tampering/Info Disclosure | Preact's JSX auto-escapes text content by default — unchanged by this refactor; do not introduce `dangerouslySetInnerHTML` anywhere in the rebuilt components |

## Sources

### Primary (HIGH confidence — read directly from this repo)
- `orchestrator/ui/src/components/SensorsPage.tsx`, `SensorList.tsx`, `SensorListRow.tsx`,
  `DetectorDisclosure.tsx`, `DetectorEntry.tsx`, `DetectorParamGrid.tsx`, `AddDetectorButton.tsx`,
  `PatternFiltersPanel.tsx`, `SaveBar.tsx`, `SaveResultBanner.tsx`, `SensorSearchInput.tsx`,
  `FieldValidationError.tsx`, `Select.tsx` — current implementation, structure, props
- `orchestrator/ui/src/state/sensors.ts`, `orchestrator/ui/src/api/types.ts`,
  `orchestrator/ui/src/validation/detectorParams.ts` — state/data/validation contracts
- `orchestrator/ui/src/components/{AlgorithmCard,Card,Badge,Input,Checkbox,SearchInput,
  EmptyState,Button,Disclosure,Textarea}.tsx` — Phase 10 shared component actual signatures
- `orchestrator/Argus.Orchestrator/Web/DetectorDefaults.cs`,
  `orchestrator/Argus.Orchestrator/Config/InputValidator.cs`,
  `orchestrator/Argus.Orchestrator/Program.cs` (lines 245-390) — server-side save/validation flow,
  used to confirm `entityIdx` has no wire-level correlation
- `orchestrator/Argus.Orchestrator/Ha/HaSensorRegistry.cs` (line 59) — confirms `GET /api/sensors`
  returns alphabetically pre-sorted entries
- `Argus Design System/ui_kits/admin/Sensors.jsx` — layout/interaction reference (D-04's
  single-select model, Card-wrapped list, checkbox stopPropagation pattern)
- `Argus Design System/components/selection/AlgorithmCard.d.ts`, `.jsx` — authoritative DS spec
  showing the generic (non-group-detector-specific) prop shape
- `Argus Design System/components/forms/Input.d.ts` — authoritative DS spec showing `label`/
  `error`/`unit`/`mono` props that the ported `Input.tsx` does not yet implement
- `Argus Design System/HANDOFF_TO_CLAUDE_CODE.md` — screen/component scope, fidelity, hard rules
- `orchestrator/ui/src/components/DashboardPage.tsx`, `SettingsPage.tsx` — established Phase 11
  precedents for `.argus-page-header`, `Card`-wrapped `.argus-list-row`, and external
  label-wrapping around `<Input>`
- `orchestrator/ui/vitest.config.ts`, `orchestrator/ui/package.json` — test/build tooling
- `.planning/phases/12-sensors-screen-rebuild/12-CONTEXT.md`, `.planning/REQUIREMENTS.md`,
  `.planning/STATE.md` — locked decisions, requirements, project history
- `orchestrator/ui/public/css/argus.css` (grepped for `--color-accent-soft`,
  `.argus-algorithm-card`, `.argus-card`, `.argus-page-header`, `.argus-list-row`) — confirms all
  needed tokens/classes exist except a new `.argus-list-row--selected` modifier

### Secondary (MEDIUM confidence)
None used — all findings were verified directly against the checked-out repo; no external
web/docs lookups were needed for this phase (no new libraries, no framework version questions).

### Tertiary (LOW confidence)
None.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new dependencies, all versions read directly from `package.json`
- Architecture: HIGH — every touched file and every shared component was read in full; the two
  CONTEXT.md/code discrepancies (Input label/error, AlgorithmCard typing) were independently
  confirmed by reading both the DS spec files and the actual ported component files
- Pitfalls: HIGH — derived from direct code reading (checkbox/label semantics, aria-describedby
  wiring, entityIdx wire-analysis via `Program.cs`), not speculation

**Research date:** 2026-07-08
**Valid until:** No external dependency — valid until the next change to `AlgorithmCard.tsx`,
`Input.tsx`, or `state/sensors.ts` (internal repo drift only, not time-based staleness)
