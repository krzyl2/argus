# Phase 13: Groups Screen Rebuild - Pattern Map

**Mapped:** 2026-07-17
**Files analyzed:** 20 (11 components + 2 state + 1 validation + 9 new test files)
**Analogs found:** 20 / 20 (all either Phase-12 sibling files or Phase-10 shared primitives)

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `components/GroupsPage.tsx` | component (page/router-branch) | request-response | `components/SensorsPage.tsx` | exact (Phase 12 sibling) |
| `components/GroupList.tsx` | component (list) | CRUD (read) | `components/SensorList.tsx` | exact |
| `components/GroupListRow.tsx` | component (row) | CRUD (delete) + request-response | `components/SensorListRow.tsx` | role-match (row+Card+Badge pattern; delete/status logic is GroupListRow's own, unchanged) |
| `components/GroupEditorForm.tsx` | component (form) | CRUD (create/update) | `components/SettingsPage.tsx` (field wiring) + `components/GroupEditorForm.tsx` itself (structure unchanged) | role-match |
| `components/MemberPicker.tsx` | component (multi-select) | CRUD (read+select) | `components/SensorListRow.tsx` (Checkbox/Badge usage) + `components/SensorList.tsx` (Card-wrap+SearchInput) | role-match |
| `components/AlgorithmChooser.tsx` | component (wizard step) | CRUD (select) | `components/AlgorithmCard.tsx` caller pattern (already correct, Phase 12) | exact (no change to core logic) |
| `components/GuidedFlowStep.tsx` | component (wizard step) | request-response (local state) | `components/GuidedFlowStep.tsx` (restyle in place) + `components/Card.tsx`/`Button.tsx` | role-match |
| `components/SensitivityPresetPicker.tsx` | component (radio group) | CRUD (select), unchanged logic | itself (Phase 10 verified in place) | exact — no change |
| `components/AdvancedParamsDisclosure.tsx` | component (form fields) | CRUD (update) | `components/DetectorParamGrid.tsx` (Phase 12, `Input` external-label wiring) | exact |
| `components/AttributionPanel.tsx` | component (polling display) | streaming (60s poll) + request-response | itself (poll logic unchanged); wrap pattern from `components/SensorList.tsx` Card usage | exact — restyle only |
| `components/AttributionBar.tsx` | component (bar chart row) | transform (render-only) | itself (already DS-compliant, Phase 8) | exact — CSS-only if anything |
| `components/AreaSuggestionBanner.tsx` | component (banner) | request-response | `components/Banner.tsx` (Phase 10 Wave 3 tone-driven) | exact |
| `components/GroupSaveResultBanner.tsx` | component (banner) | request-response | `components/Banner.tsx` | exact |
| `components/SaveBar.tsx` | component (action bar) | CRUD (save) | itself, unchanged (D-07) | exact — no change |
| `components/FieldValidationError.tsx` | component (inline error) | transform (render-only) | itself, unchanged | exact — no change |
| `state/groups.ts` | store/state | CRUD | itself, unchanged (D-07) | exact — no change |
| `state/groupEditor.ts` | store/state (state machine) | event-driven | itself, unchanged (D-07) | exact — no change |
| `validation/groupParams.ts` | utility (validation) | transform | itself, unchanged (D-07) | exact — no change |
| `components/GroupList.test.tsx` (new) | test | — | `components/SensorList.test.tsx` | exact (Card-wrap + row-count assertions template) |
| `components/GroupListRow.test.tsx` (new) | test | — | Phase-12 row test pattern (fake-timer arm/confirm) — no direct file exists; use `AlgorithmChooser.test.tsx` for render-assertion style + write new fake-timer harness | role-match |
| `components/GroupEditorForm.test.tsx` (new) | test | — | `AttributionPanel.test.tsx` (render + fireEvent style) | role-match |
| `components/MemberPicker.test.tsx` (new) | test | — | `components/SensorList.test.tsx` (Checkbox/Badge row assertions) | role-match |
| `components/GuidedFlowStep.test.tsx` (new) | test | — | `AlgorithmChooser.test.tsx` (copy/verbatim button assertions) | role-match |
| `components/SensitivityPresetPicker.test.tsx` (new) | test | — | `AlgorithmCard.test.tsx` (radio-card selection assertions) | role-match |
| `components/AdvancedParamsDisclosure.test.tsx` (new) | test | — | `AttributionPanel.test.tsx` (field-wiring/render style) | role-match |
| `components/AttributionBar.test.tsx` (new) | test | — | `AttributionPanel.test.tsx` (existing, sibling component) | role-match |
| `components/AlgorithmChooser.test.tsx` (extend) | test | — | itself | exact — extend, don't rewrite |
| `components/AttributionPanel.test.tsx` (extend) | test | — | itself | exact — extend, don't rewrite |

## Pattern Assignments

### `components/GroupList.tsx` (component, CRUD-read)

**Analog:** `components/SensorList.tsx` (Card-wrapped list, Phase 12 D-0x)

**Core Card-wrap pattern** (`SensorList.tsx:73-78`, reused verbatim in RESEARCH.md Pattern 1):
```tsx
<Card padding="none">
  <ul class="argus-list">{entries.map(renderRow)}</ul>
</Card>
```
Apply identically to `GroupList.tsx`, mapping `groups` instead of `entries`, rendering `<GroupListRow key={group.groupId} group={group} status={...} />`.

**Empty-state branch:** `SensorList.tsx`'s equivalent zero-length guard — replicate with existing `.argus-empty` markup or list-specific copy ("No groups yet — create one to get started" style), consistent with Phase 12's empty handling. Do not force-fit the sensor-specific `EmptyState.tsx` component here either (it takes `{query: string}`, sensor-search-specific copy — same caution as Pitfall 4 for AttributionPanel).

---

### `components/GroupListRow.tsx` (component, CRUD-delete + request-response)

**Analog:** `components/SensorListRow.tsx` for Card/Badge/Checkbox conventions; **own current file** for delete/status logic (preserve verbatim).

**Current file, in full** (`GroupListRow.tsx:1-68`) — this IS the analog for the two-step delete state machine; only the render's Badge set and outer wrapping change:
```tsx
const CONFIRM_WINDOW_MS = 3000;
const [armed, setArmed] = useState(false);
const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

function handleDeleteClick() {
  if (armed) {
    if (timerRef.current) clearTimeout(timerRef.current);
    setArmed(false);
    deleteGroup(group.groupId);
    return;
  }
  setArmed(true);
  timerRef.current = setTimeout(() => setArmed(false), CONFIRM_WINDOW_MS);
}
```
Keep this exactly. **Add** a second `Badge tone="accent"` for `group.detector` alongside the existing mode `Badge tone="neutral"` (`GroupListRow.tsx:48`), matching the DS reference's two-badge row (D-02). Keep the existing status branch (`GroupListRow.tsx:54-58`) and the `Button variant="destructive-ghost" size="xs"` delete control (`GroupListRow.tsx:62-64`) verbatim.

**stopPropagation pattern** (only if whole-row click-to-edit is added — Claude's discretion per RESEARCH.md Pattern 1) — copy from `SensorListRow.tsx:49-53`:
```tsx
<li class="argus-list-row" onClick={onSelectRow}>
  <span onClick={(e) => e.stopPropagation()}>
    <Checkbox ... />
  </span>
```
Same idea: wrap the Delete `Button` and the "Edit" `<a>` in a `stopPropagation` handler if the `<li>` itself gains an `onClick` navigate. If the existing `<a href="#/groups/{id}">Edit</a>` link stays as the click target (simplest, zero risk), this pattern is unnecessary.

---

### `components/GroupEditorForm.tsx` (component, CRUD create/update)

**Analog:** `components/SettingsPage.tsx` external-label convention (also used by Phase 12's `DetectorParamGrid.tsx`).

**Name field pattern** — external `<label>` + `Input` + `FieldValidationError` (NOT component-internal label/error, confirmed by reading `Input.tsx` — it renders no label/error itself):
```tsx
<div class={`argus-param-field${nameError ? ' argus-param-field--error' : ''}`}>
  <label class="argus-param-field__label" for="group-name">Name</label>
  <Input
    id="group-name"
    value={draftFriendlyName.value}
    onChange={(next) => {
      draftFriendlyName.value = next;
      if (!groupId) draftGroupId.value = slugify(next);
    }}
    invalid={!!nameError}
    ariaDescribedby={nameError ? 'group-name-err' : undefined}
  />
  <FieldValidationError message={nameError ?? undefined} />
</div>
```
`Input.tsx` signature confirmed (`Input.tsx:1-12`): `{value, onChange, type?, placeholder?, ariaLabel?, disabled?, invalid?, id?, step?, ariaDescribedby?}` — already has all passthrough props needed, no further widening.

**Mode field pattern** — raw `<select>` → `Select`:
```tsx
<Select
  value={draftMode.value}
  onChange={(v) => { draftMode.value = v as typeof draftMode.value; }}
  ariaLabel="Mode"
  options={[
    { value: 'peer_divergence', label: 'Peer-divergence — which sensor is diverging' },
    { value: 'joint', label: 'Joint (multivariate) — unusual combination' },
  ]}
/>
```
`Select.tsx` signature confirmed (`Select.tsx:6-12`): `{value, onChange, options: SelectOption[], ariaLabel?, disabled?}`.

**Page header / "Back to groups"** — copy `SensorsPage.tsx:44-49`'s `.argus-page-header` structure:
```tsx
<header class="argus-page-header">
  <h1 class="argus-page-header__title">...</h1>
  <p class="argus-page-header__subtitle">...</p>
</header>
```
Back affordance: plain `<a href="#/groups">` or `Button variant="ghost"` with `onClick={() => (location.hash = '#/groups')}` — visual only, D-01 forbids changing the router.

---

### `components/MemberPicker.tsx` (component, CRUD read+select)

**Analog:** `components/SensorListRow.tsx` for Checkbox/Badge usage; `components/SensorList.tsx` for Card-wrap.

**IMPORTANT pre-existing state:** working tree already has an uncommitted `MIN_QUERY_LENGTH = 2` diff on this file (confirmed via `git diff` in RESEARCH.md) — D-07 locks this in as target behavior. Fold it into this phase's first commit; do not revert while refactoring markup.

**Row pattern** (adapted from `SensorListRow.tsx:44-63`, but keep the `<label style="display:contents">` wrapper — no competing row-level `onClick` exists here, unlike `SensorListRow`):
```tsx
<li key={entry.entityId} class={`argus-list-row${checked ? ' argus-list-row--tracked' : ''}`}>
  <label style={{ display: 'contents' }}>
    <Checkbox checked={checked} ariaLabel={entry.entityId} onChange={(next) => onToggleMember(entry.entityId, next)} />
    <div class="argus-row-content">
      <span class="argus-row-entity-id">{entry.entityId}</span>
      {showFriendlyName && <span class="argus-row-friendly-name">{entry.friendlyName}</span>}
    </div>
    <div class="argus-row-meta">
      {entry.unitOfMeasurement && <span class="argus-row-value">{entry.unitOfMeasurement}</span>}
      {checked && <Badge tone="member">member</Badge>}
    </div>
  </label>
</li>
```
`Checkbox.tsx` signature confirmed (`Checkbox.tsx:1-6`): `{checked, onChange: (checked: boolean) => void, ariaLabel?, disabled?}`. `Badge.tsx` signature confirmed (`Badge.tsx:3-6`): `{tone?: 'tracked'|'member'|'neutral'|'ok'|'warn'|'error'|'accent', children}`.

**Search input:** replace raw `<input>` with `SearchInput` (`SearchInput.tsx:3-9`): `{value, onChange, placeholder?, ariaLabel?, debounceMs?}` — debounced (200ms default), leading ⌕ glyph, already handles unmount cleanup. Do NOT re-derive debounce logic.

**Card-wrap:** wrap results `<ul>` in `<Card padding="none">` per `SensorList.tsx:73-78`.

**Anti-pattern (Pitfall 1):** do not add a `stopPropagation` wrapper around the `Checkbox` here — there is no row-level `onClick` competing with it in this component (unlike `SensorListRow`). Keep the native `<label>` toggle-anywhere-on-row behavior.

---

### `components/GuidedFlowStep.tsx` (component, wizard step)

**Analog:** self (restyle in place) + `Card.tsx`/`Button.tsx`.

```tsx
<Card padding="sm">
  <p class="argus-body">What are you monitoring?</p>
  <div class="argus-guided-flow-step__answers">
    <Button variant="secondary" onClick={() => answerGuidedQuestion('together')}>
      A room/area&apos;s related sensors, together
    </Button>
    <Button variant="secondary" onClick={() => answerGuidedQuestion('diverges')}>
      Which one sensor diverges from its peers
    </Button>
  </div>
  <Button variant="ghost" size="sm" onClick={skipToManual}>
    Skip — choose manually
  </Button>
</Card>
```
`Button.tsx` signature confirmed (`Button.tsx:3-12`): `{variant?: 'primary'|'secondary'|'ghost'|'destructive-ghost', size?: 'md'|'sm'|'xs', disabled?, loading?, type?, onClick?, ariaLabel?, children}`. Keep copy verbatim (Copywriting Contract, D-04) — do not reword either answer or the skip link.

**Do NOT touch:** `AlgorithmChooser.tsx`'s `useEffect` sync (lines 41-61 per RESEARCH.md), its `AlgorithmCard` grid block (already correct, Phase 12), or add any `.filter()` on `cat.detectors` keyed off `draftMode` (D-03/Pitfall 5).

---

### `components/AdvancedParamsDisclosure.tsx` (component, CRUD update)

**Analog:** `components/DetectorParamGrid.tsx` (Phase 12) — apply the exact same external-`Input`-wrapping treatment per field, driven by `field.type`/`min`/`max`/`step` from `DetectorCatalogEntry.paramSchema`. Do not touch `updateParam`/`draftParams` wiring.

---

### `components/AttributionPanel.tsx` (component, streaming/poll)

**Analog:** self — poll logic (lines 19-48 per RESEARCH.md) unchanged, only render branches (lines 50-80) wrapped.

```tsx
if (!loaded) {
  return <Card padding="sm"><p class="argus-label">Loading attribution…</p></Card>;
}
if (!status) {
  return <Card padding="sm"><p class="argus-body">No anomaly score yet — attribution will appear after the next batch run.</p></Card>;
}
if (status.contributions.length === 0) {
  return (
    <Card padding="sm">
      <div class="argus-empty">
        <p class="argus-body">Attribution not available.</p>
        <p class="argus-label">The {status.detector} detector does not report per-member attribution.</p>
      </div>
    </Card>
  );
}
// ranked list: wrap existing .map(...) in <Card padding="sm">...</Card>
```
**SectionLabel:** no shared component exists — use the raw-class convention from `DashboardPage.tsx`:
```tsx
<p class="argus-section-label">Member attribution · last result, refreshes ~60s</p>
```

**Anti-pattern (Pitfall 4):** do NOT pass a fake `query` string into `EmptyState.tsx` (its actual prop shape is `{query: string}` with hardcoded sensor-search copy) — render custom `.argus-empty` markup instead, as shown above.

---

### `components/AttributionBar.tsx` (component, transform/render-only)

**Analog:** self — already DS-compliant (Phase 8), confirmed in `AttributionBar.tsx:1-27`:
```tsx
interface AttributionBarProps {
  memberId: string;
  contribution: number;
  topContribution: number;
  topRank: boolean;
}
export function AttributionBar({ memberId, contribution, topContribution, topRank }: AttributionBarProps) {
  const widthPct = topContribution > 0 ? Math.min(100, (contribution / topContribution) * 100) : 0;
  return (
    <div class="argus-attribution-bar">
      <span class="argus-label argus-attribution-bar__label">{memberId}</span>
      <div class="argus-attribution-bar__track">
        <div class={`argus-attribution-bar__fill${topRank ? ' argus-attribution-bar__fill--top' : ''}`} style={{ width: `${widthPct}%` }} />
      </div>
      <span class="argus-label argus-attribution-bar__value">{contribution.toFixed(3)}</span>
    </div>
  );
}
```
No prop/logic changes needed — `--color-accent` fill on `topRank` confirmed present in `argus.css` lines 899-909. Do not rename `memberId`/`topRank` to the kit's `label`/`top`.

---

### `components/AreaSuggestionBanner.tsx`, `components/GroupSaveResultBanner.tsx`

**Analog:** `components/Banner.tsx` (Phase 10 Wave 3, confirmed signature):
```tsx
export interface BannerProps {
  tone?: 'success' | 'error' | 'validation' | 'reloading' | 'info';
  children: ComponentChildren;
  action?: ComponentChildren;
  onDismiss?: () => void;
}
```
Both banners were already retrofitted onto `Banner` in Phase 10 Wave 3 per RESEARCH.md — verify their call sites still match this signature; do not re-derive a new banner component.

---

## Shared Patterns

### Card-wrapped lists
**Source:** `components/SensorList.tsx:73-78`
**Apply to:** `GroupList.tsx`, `MemberPicker.tsx` results list
```tsx
<Card padding="none">
  <ul class="argus-list">{entries.map(renderRow)}</ul>
</Card>
```
`Card.tsx` signature confirmed (`Card.tsx:3-7`): `{padding?: 'none'|'sm'|'md', interactive?: boolean, children}` — note `padding` is currently a no-op in CSS (all render as `.argus-card` default), per the component's own comment.

### Page header
**Source:** `components/SensorsPage.tsx:44-49`
**Apply to:** `GroupsPage.tsx` list view, `GroupEditorForm.tsx` create/edit heading
```tsx
<header class="argus-page-header">
  <h1 class="argus-page-header__title">Groups</h1>
  <p class="argus-page-header__subtitle">...</p>
</header>
```

### External label + Input + FieldValidationError (not component-internal)
**Source:** `components/SettingsPage.tsx` convention, also `DetectorParamGrid.tsx` (Phase 12)
**Apply to:** `GroupEditorForm.tsx` name field, `AdvancedParamsDisclosure.tsx` param fields
```tsx
<label class="argus-param-field__label" for={id}>...</label>
<Input id={id} invalid={!!error} ariaDescribedby={error ? `${id}-err` : undefined} ... />
<FieldValidationError message={error} />
```
**Known pre-existing gap (not to fix unless explicitly scoped):** `FieldValidationError.tsx` renders no matching `id`, so `ariaDescribedby={`${id}-err`}` points at a non-existent DOM node — already true in shipped Phase 12 code. Follow convention as-is; optionally add an `id` prop to `FieldValidationError.tsx` as in-scope-but-optional polish (flagged, not required).

### Section label (no wrapper component)
**Source:** `DashboardPage.tsx` raw-class usage (no `SectionLabel.tsx` exists)
**Apply to:** `AttributionPanel.tsx`, wizard section headings
```tsx
<p class="argus-section-label">...</p>
```

### Two-step destructive delete (preserve verbatim, do not re-derive)
**Source:** `GroupListRow.tsx:23-42` (own file, already correct)
**Apply to:** no change — this is the pattern other future rows should copy, not the reverse.

## No Analog Found

None — every production file has either a direct Phase-12 sibling analog or is itself the unchanged source of truth (state/validation modules). New test files use existing Groups-family tests (`AlgorithmChooser.test.tsx`, `AttributionPanel.test.tsx`) and Phase 12's `SensorList.test.tsx` as structural templates (fixture-factory + `render()` + querySelector assertions, fake timers for arm/confirm flows).

## Metadata

**Analog search scope:** `orchestrator/ui/src/components/`, `orchestrator/ui/src/state/`, `orchestrator/ui/src/validation/`, `.planning/phases/12-sensors-screen-rebuild/`
**Files scanned:** GroupsPage.tsx, GroupList.tsx, GroupListRow.tsx, GroupEditorForm.tsx, MemberPicker.tsx, AlgorithmChooser.tsx, GuidedFlowStep.tsx, SensitivityPresetPicker.tsx, AdvancedParamsDisclosure.tsx, AttributionPanel.tsx, AttributionBar.tsx, AreaSuggestionBanner.tsx, GroupSaveResultBanner.tsx, SaveBar.tsx, FieldValidationError.tsx, Card.tsx, Badge.tsx, Checkbox.tsx, SearchInput.tsx, Select.tsx, Input.tsx, Banner.tsx, Button.tsx, SensorListRow.tsx, SensorList.tsx, SensorList.test.tsx
**Pattern extraction date:** 2026-07-17
