# Phase 14: Unified Detectors Screen + Add-Detector Wizard - Pattern Map

**Mapped:** 2026-07-21
**Files analyzed:** 10 (6 create, 4 modify) + 1 generalize
**Analogs found:** 10 / 10

All paths below are relative to `orchestrator/ui/src/` unless stated otherwise.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `components/DetectorsPage.tsx` | route-page (controller-ish) | request-response (loader effect + branch render) | `components/GroupsPage.tsx` | exact |
| `components/DetectorList.tsx` | component (list) | CRUD (render list) | `components/GroupList.tsx` (+ `SensorList.tsx` empty-state) | exact |
| `components/DetectorListRow.tsx` | component (row, 2 variants) | CRUD (render + navigate) | `components/GroupListRow.tsx` + `components/SensorListRow.tsx` | exact |
| `components/AddDetectorWizard.tsx` | component (form/wizard) | request-response (hand-off, no own save) | `components/AreaSuggestionBanner.tsx` (hand-off) + `components/MemberPicker.tsx` (picker UI) | exact (composite) |
| `components/SingleDetectorEditorForm.tsx` | component (form) | CRUD (edit + save) | inline block in `components/SensorsPage.tsx` (lines 42-66) | exact (extraction) |
| `state/detectors.ts` | store (computed signal) | transform (merge) | `state/groups.ts` (computed-signal / signal-export pattern) | role-match |
| `router.ts` (modify) | route parser | request-response | `router.ts` own `parseGroupId`/`normalizeHash` | exact (self-analog) |
| `main.tsx` (modify) | route switch | request-response | `main.tsx` own `App()` if/else chain | exact (self-analog) |
| `components/Sidebar.tsx` (modify) | component (nav) | request-response | `components/Sidebar.tsx` own `NAV_ITEMS`/`isActive` | exact (self-analog) |
| `components/MemberPicker.tsx` (modify — add prop) | component | CRUD (multi-select) | `components/MemberPicker.tsx` own `MIN_QUERY_LENGTH` constant | exact (self-analog) |
| `components/SensorsPage.tsx` (modify — extract/shim) | route-page | request-response | itself (source of extraction) | exact |
| `components/SettingsPage.tsx` (modify — host PatternFiltersPanel) | route-page | CRUD (new save path) | `components/SensorsPage.tsx`'s Pattern Filters block (lines 68-74) + `SaveBar`/`SaveResultBanner` usage | role-match |

## Pattern Assignments

### `components/DetectorsPage.tsx` (route-page)

**Analog:** `components/GroupsPage.tsx` (full file, 45 lines — read above)

**Imports pattern:**
```tsx
import { useEffect } from 'preact/hooks';
import { route } from '../router';
import { groups, loadGroups } from '../state/groups';
import { query as sensorQuery, sensors, loadSensors } from '../state/sensors';
import { detectorRows } from '../state/detectors'; // new
import { DetectorList } from './DetectorList';
```

**Loader-effect pattern (copy verbatim, adapt to two loaders):**
```tsx
useEffect(() => {
  loadGroups();
  loadSensors(sensorQuery.value); // D-07: must load full set ('' query semantics), not a partial one
}, []);
```
Note: `GroupsPage` already calls `loadSensors(sensorQuery.value)` on mount for `MemberPicker`'s benefit — `DetectorsPage` needs the same call for its own merge, so this is a direct copy, not new invention. Confirm `sensorQuery.value` is `''` at boot (it is, per `state/sensors.ts` `export const query = signal('')`).

**No editor-branch needed** here (unlike `GroupsPage`, which branches internally between list/editor) — `DetectorsPage` only renders the list; editors are separate routes (`GroupEditorForm` at `#/groups/:id`, `SingleDetectorEditorForm` at `#/detectors/sensor/:id`) per D-03/D-05. Drop the `isEditor` branch entirely; this file is pure list+header+CTA, structurally simpler than `GroupsPage`.

**Header + CTA shape to copy:**
```tsx
<header class="argus-page-header">
  <h1 class="argus-page-header__title">Detectors</h1>
  <p class="argus-page-header__subtitle">...</p>
</header>
<p>
  <a class="argus-btn argus-btn--primary" href="#/detectors/add">
    Add detector
  </a>
</p>
<DetectorList rows={detectorRows.value} />
```
`AreaSuggestionBanner` stays specific to Groups — do not port it into `DetectorsPage` (out of scope; D-03 doesn't mention it and it writes into `pendingPrefillMembers`/`#/groups/new`, unrelated to the unified list).

---

### `components/DetectorList.tsx` (component)

**Analog:** `components/GroupList.tsx` (full file, 34 lines) — primary structural analog (Card-wrapped `<ul class="argus-list">`, custom empty branch). `SensorList.tsx`'s `EmptyState` usage is the discretionary alternative for the empty branch (research says GroupList's custom `.argus-empty` copy is the closer match since it's the two-source list, not a query-based browse list).

**Core pattern (copy structure, adapt props to discriminated union):**
```tsx
import { DetectorListRow } from './DetectorListRow';
import { Card } from './Card';
import type { DetectorRow } from '../state/detectors';

interface DetectorListProps {
  rows: DetectorRow[];
}

export function DetectorList({ rows }: DetectorListProps) {
  if (rows.length === 0) {
    return (
      <div class="argus-empty">
        <p class="argus-body">No detectors configured.</p>
        <p class="argus-label">...</p>
      </div>
    );
  }
  return (
    <Card padding="none">
      <ul class="argus-list">
        {rows.map((row) => (
          <DetectorListRow key={row.key} row={row} />
        ))}
      </ul>
    </Card>
  );
}
```
Key: `row.key` must already be namespaced (`group:${groupId}` / `sensor:${entityId}`) per D-03/Pitfall 2 — build that in `state/detectors.ts`, not here.

---

### `components/DetectorListRow.tsx` (component, 2 variants)

**Analogs:** `components/GroupListRow.tsx` (full file, 69 lines) for the group variant; `components/SensorListRow.tsx` (full file, 77 lines) for the sensor variant.

**Group variant — copy verbatim from `GroupListRow.tsx` lines 44-68** (delete-with-confirm state at lines 17-42 stays too — group delete is unchanged per D-08a "Group delete stays where it is"):
```tsx
<li class="argus-list-row">
  <div class="argus-row-content">
    <span class="argus-row-entity-id">{group.friendlyName || group.groupId}</span>
    <Badge tone="neutral">{modeLabel}</Badge>
    <Badge tone="accent">{group.detector}</Badge>
  </div>
  <div class="argus-row-meta">
    <span class="argus-label">{group.members.length} {memberWord}</span>
    {status ? (...) : (<span class="argus-label">no status yet</span>)}
    <a class="argus-label" href={`#/groups/${encodeURIComponent(group.groupId)}`}>Edit</a>
    <Button variant="destructive-ghost" size="xs" onClick={handleDeleteClick}>
      {armed ? 'Confirm delete' : 'Delete group'}
    </Button>
  </div>
</li>
```

**Sensor variant — relocate `SensorListRow.tsx`'s look, but strip inline-expand + checkbox per D-03 ("Rows only navigate... no inline expand-in-place") and D-08a (no untrack action on the row):**
```tsx
<li class="argus-list-row">
  <div class="argus-row-content">
    <span class="argus-row-entity-id">{entry.entityId}</span>
    {showFriendlyName && <span class="argus-row-friendly-name">{entry.friendlyName}</span>}
  </div>
  <div class="argus-row-meta">
    <Badge tone="tracked">tracked</Badge>
    {/* assigned-detector badge — new, e.g. <Badge tone="accent">{detectorNames.join(', ')}</Badge> */}
    <a class="argus-label" href={`#/detectors/sensor/${encodeURIComponent(entry.entityId)}`}>Edit</a>
  </div>
</li>
```
Drop `SensorListRow`'s `Checkbox`/`onSelectRow`/`isSelected`/`DetectorDisclosure` entirely — that inline-expand UX is explicitly superseded (D-03). The `encodeURIComponent(entityId)` link-building matches `GroupListRow`'s `encodeURIComponent(group.groupId)` pattern exactly (line 60) — same idiom, new route.

**Discriminant dispatch shape:**
```tsx
export function DetectorListRow({ row }: { row: DetectorRow }) {
  return row.kind === 'group' ? <GroupRow group={row.group} /> : <SensorRow entry={row.entry} />;
}
```

---

### `components/AddDetectorWizard.tsx` (new; thin hand-off)

**Analogs:** `components/AreaSuggestionBanner.tsx` (full file, 77 lines) for the hand-off idiom; `components/MemberPicker.tsx` (full file, 104 lines) for the picker UI it mounts.

**Hand-off pattern — copy verbatim from `AreaSuggestionBanner.tsx` lines 47-51** (the exact channel D-06 specifies):
```tsx
function handleContinue() {
  if (selectedIds.length >= 2) {
    pendingPrefillMembers.value = selectedIds; // state/groups.ts signal
    location.hash = '#/groups/new';
  } else if (selectedIds.length === 1) {
    setTracked(selectedIds[0], true); // state/sensors.ts — AFTER full loadSensors('') per D-07
    location.hash = `#/detectors/sensor/${encodeURIComponent(selectedIds[0])}`;
  }
}
```

**Mount MemberPicker with the new prop (D-06):**
```tsx
<MemberPicker
  sensors={sensors.value}
  selectedIds={selectedIds}
  mode="peer_divergence"   // unused by the wizard's own logic; MemberPicker requires it for unit-mismatch validation — pass a neutral value or make it optional-tolerant; confirm during planning
  query={query}
  onQueryChange={setQuery}
  onToggleMember={toggleMember}
  minQueryLength={3}
/>
```
Caveat: `MemberPicker` also runs `validateGroupMembers`/`validateUnitConsistency` unconditionally (lines 41-42, 84-85) and renders `FieldValidationError` for both — those group-specific validations will render inside the wizard even for the 1-sensor path. Confirm with planner whether that's acceptable (likely yes — the ≥2 path needs it, the 1-sensor path with a single member never trips `validateGroupMembers`'s floor check incorrectly since floor is presumably ≥2, so the error only shows correctly urging 2+ before enabling group creation).

**D-07 guard — mount effect must load the full sensor set before any track/save:**
```tsx
useEffect(() => {
  loadSensors(''); // full set — never rely on the ≥3-char search results for this
}, []);
```

**Primary button label switch (from `<specifics>`):**
```tsx
<Button disabled={selectedIds.length === 0} onClick={handleContinue}>
  {selectedIds.length >= 2 ? 'Create group' : 'Configure detector'}
</Button>
```

---

### `components/SingleDetectorEditorForm.tsx` (extraction)

**Analog:** inline block in `components/SensorsPage.tsx` — read the full file (81 lines); the extractable block is the detector-assignment concerns woven through lines 1-81 (not a single contiguous block — it's the imports at 3-17, the `loadSensors` effect at 27-29, and the `SensorList`/detector-callback wiring at 54-66). Confirm scope: `SingleDetectorEditorForm` targets ONE entity, so it does NOT reuse `SensorList`/`SensorListRow` directly — it reuses the lower-level `DetectorDisclosure`/`DetectorEntry`/`DetectorParamGrid`/`AddDetectorButton` stack that `SensorListRow` mounts internally (`components/SensorListRow.tsx` lines 64-73):

```tsx
{isTracked && (
  <DetectorDisclosure
    entityId={entry.entityId}
    entityIdx={entityIdx}
    detectors={detectors}
    onTypeChange={onDetectorTypeChange}
    onParamChange={onDetectorParamChange}
    onRemove={onDetectorRemove}
    onAdd={onDetectorAdd}
  />
)}
```
Port this pattern into the new form for a single, known `entityId` (no `isSelected`/`onSelectRow` needed — the whole route IS the selection).

**State wiring — copy verbatim from `SensorsPage.tsx` imports (lines 2-17) and callback bindings (lines 60-65):**
```tsx
import {
  query, sensors, entityEdits, saveState, hasValidationErrors,
  loadSensors, setTracked, addDetector, removeDetector,
  updateDetectorName, updateDetectorParam, save,
} from '../state/sensors';
```
`onToggleTracked={setTracked}` becomes the "Untrack sensor" destructive action per D-08a (mirrors "Remove detector"); wire it as an explicit button, not a checkbox, since there's no list-row context here:
```tsx
<Button variant="destructive-ghost" size="xs" onClick={() => setTracked(entityId, false)}>
  Untrack sensor
</Button>
```

**D-07 guard — mount effect (copy `SensorsPage.tsx` line 27-29 verbatim):**
```tsx
useEffect(() => {
  loadSensors(''); // full set, not `query.value` — this route never shows the search box
}, []);
```

**Save wiring — copy verbatim from `SensorsPage.tsx` lines 39, 76-78:**
```tsx
const saving = saveState.value === 'saving';
const result = typeof saveState.value === 'object' ? saveState.value.result : null;
...
<SaveBar saving={saving} disabled={saving || hasValidationErrors.value} onSave={save} />
{result && <SaveResultBanner result={result} />}
```

**Do NOT mount `AlgorithmChooser`** (Pitfall 6) — this form only ever touches `state/sensors.ts` signals, never `state/groupEditor.ts`/`state/groups.ts` draft signals.

---

### `state/detectors.ts` (new; computed merge)

**Analog:** `state/groups.ts`'s signal-export + computed pattern (no direct computed-signal example exists in `groups.ts` itself — `state/sensors.ts`'s `validationErrors`/`hasValidationErrors` computed pair, lines 144-159, is the actual computed-signal analog to copy):
```ts
import { computed } from '@preact/signals';
import { groups } from './groups';
import { sensors, entityEdits } from './sensors';

export interface DetectorRow {
  key: string;           // `group:${groupId}` | `sensor:${entityId}`
  kind: 'group' | 'sensor';
  group?: GroupConfig;
  entry?: SensorEntry;
}

export const detectorRows = computed<DetectorRow[]>(() => {
  const groupRows: DetectorRow[] = groups.value.map((g) => ({
    key: `group:${g.groupId}`, kind: 'group', group: g,
  }));
  const sensorRows: DetectorRow[] = sensors.value
    .filter((s) => entityEdits.value[s.entityId]?.isTracked ?? s.isTracked)
    .map((s) => ({ key: `sensor:${s.entityId}`, kind: 'sensor', entry: s }));
  return [...groupRows, ...sensorRows]; // sort order = Claude's discretion per CONTEXT
});
```
No new fetch logic — pure derivation over `groups`/`sensors`/`entityEdits`, matching the "thin computed layer" directive in RESEARCH.

---

### `router.ts` (modify)

**Self-analog — copy the exact idiom of the existing `parseGroupId` (lines 26-31) for the new entity-id parser:**
```ts
function parseSensorEntityId(path: string): string | null {
  const match = path.match(/^\/detectors\/sensor\/([^/]+)$/);
  if (!match) return null;
  try {
    return decodeURIComponent(match[1]);
  } catch {
    return null; // defensive fallback to list on parse failure (D-01)
  }
}
```
**`normalizeHash` change (D-01) — copy the existing fallback-literal idiom (line 18) and add legacy redirect:**
```ts
function normalizeHash(hash: string): string {
  const path = hash.replace(/^#/, '') || '/detectors';
  if (path === '/sensors' || path === '/groups') return '/detectors';
  return path;
}
```
**Boot effect (line 41-45) — change target only:**
```ts
effect(() => {
  if (!location.hash) location.hash = '#/detectors';
});
```
**Do not touch `parseGroupId`** (D-01 explicit instruction) — it stays byte-identical.
**Test to add** (no existing `router.test.ts` file was found in the codebase — this will be a new test file, modeled on the assertion style used in `state/groups.test.ts`/`Sidebar.test.tsx` for signal-based behavior): assert `normalizeHash('#/sensors')` and `normalizeHash('#/groups')` both yield `/detectors`.

---

### `main.tsx` (modify)

**Self-analog — copy the existing if/else chain shape (lines 13-25), add branches, and change the final fallback:**
```tsx
} else if (route.value === '/detectors/add') {
  page = <AddDetectorWizard />;
} else if (route.value.startsWith('/detectors/sensor/')) {
  page = <SingleDetectorEditorForm entityId={routeSensorEntityId.value} />;
} else if (route.value === '/detectors') {
  page = <DetectorsPage />;
} else if (isGroupsRoute) {
  page = <GroupsPage />;
} else {
  page = <DetectorsPage />; // fallback replaces SensorsPage
}
```
Remove `import { SensorsPage } from './components/SensorsPage';` only if `SensorsPage` is fully deleted (Claude's discretion per CONTEXT — otherwise leave a dead import removal for a later cleanup pass).

---

### `components/Sidebar.tsx` (modify)

**Self-analog — copy the existing `NAV_ITEMS` array shape (lines 14-20) and `isActive` shape (lines 22-29):**
```tsx
const NAV_ITEMS: NavItem[] = [
  { id: 'dashboard', label: 'Dashboard', icon: '▦', href: '#/dashboard' },
  { id: 'algorithms', label: 'Algorithms', icon: '⚙', href: '#/algorithms' },
  { id: 'detectors', label: 'Detectors', icon: '◎', href: '#/detectors' },
  { id: 'add-detector', label: 'Add detector', icon: '+', href: '#/detectors/add' },
  { id: 'settings', label: 'Settings', icon: '⚙', href: '#/settings' },
];

function isActive(item: NavItem, currentRoute: string): boolean {
  if (item.id === 'dashboard') return currentRoute === '/dashboard';
  if (item.id === 'algorithms') return currentRoute === '/algorithms';
  if (item.id === 'detectors') return currentRoute === '/detectors' || currentRoute.startsWith('/detectors/');
  if (item.id === 'add-detector') return currentRoute === '/detectors/add';
  if (item.id === 'settings') return currentRoute === '/settings';
  return false;
}
```
Note the existing `groups` isActive rule (`currentRoute === '/groups' || currentRoute.startsWith('/groups/')`) stays out of `NAV_ITEMS` entirely per D-02 (Groups nav item removed; `/groups/:id` is still reachable, just not from the sidebar).

Existing test `components/Sidebar.test.tsx` will need updated item ids/hrefs — read it before editing to match its assertion style (not read in this pass; flag for the planner/executor).

---

### `components/MemberPicker.tsx` (modify — generalize)

**Self-analog — copy the existing `MIN_QUERY_LENGTH` constant idiom (line 26) as the default for a new prop:**
```tsx
interface MemberPickerProps {
  sensors: SensorEntry[];
  selectedIds: string[];
  mode: GroupMode;
  query: string;
  onQueryChange: (q: string) => void;
  onToggleMember: (entityId: string, checked: boolean) => void;
  minQueryLength?: number; // new — default preserves Groups' current behavior (D-06)
}

export function MemberPicker({ ..., minQueryLength = 2 }: MemberPickerProps) {
  const queryTooShort = query.trim().length < minQueryLength;
  ...
  <p class="argus-label">Type at least {minQueryLength} characters to search sensors.</p>
```
Existing test `components/MemberPicker.test.tsx` exists — check it asserts the literal `2` or `MIN_QUERY_LENGTH` text; update if it hardcodes the old constant name/behavior when the wizard's `minQueryLength={3}` test is added.

---

### `components/SettingsPage.tsx` (modify — relocate PatternFiltersPanel)

**Analog:** `components/SensorsPage.tsx` lines 68-74 (Pattern Filters JSX block) + its `SaveBar`/`SaveResultBanner` wiring (lines 76-78) — copy verbatim into `SettingsPage.tsx`, but per D-08b it MUST also guard with D-07 (`loadSensors('')` before any save, since this reuses the same full-list-replace `save()` in `state/sensors.ts`):
```tsx
useEffect(() => {
  loadSensors(''); // D-07 guard — Settings' pattern-filter save shares the sensors full-list-replace save()
}, []);

<PatternFiltersPanel
  include={includePatterns.value}
  exclude={excludePatterns.value}
  onIncludeChange={(v) => (includePatterns.value = v)}
  onExcludeChange={(v) => (excludePatterns.value = v)}
/>
<SaveBar saving={saveState.value === 'saving'} disabled={saveState.value === 'saving'} onSave={save} />
```
`SettingsPage.tsx` was not read in this pass (not in the required-reading list and out of the primary component set called out in the task) — before implementing, read its current structure to confirm it's additive (new section) rather than a full-page replacement, and confirm it doesn't already have an unrelated save button that would conflict with `state/sensors.ts`'s `save()`.

---

## Shared Patterns

### `encodeURIComponent`/`decodeURIComponent` link-and-parse idiom
**Source:** `components/GroupListRow.tsx` line 60 (`encodeURIComponent(group.groupId)`); `router.ts`'s `parseGroupId` (lines 26-31) for the parse-side idiom (adapted above for decode + defensive fallback since entity ids need decoding, group ids currently don't).
**Apply to:** `DetectorListRow`'s sensor-variant edit link, `AddDetectorWizard`'s 1-sensor navigation, `router.ts`'s new `parseSensorEntityId`.

### Full-list-replace save guard (D-07 / Pitfall 1)
**Source:** `components/GroupsPage.tsx` line 17 (`loadSensors(sensorQuery.value)` on mount, pre-existing precedent for "load full set before anything else needs it") + `state/sensors.ts` `save()` (lines 161-178, unmodified).
**Apply to:** `AddDetectorWizard.tsx`, `SingleDetectorEditorForm.tsx`, `SettingsPage.tsx` (all three now call `save()`/`setTracked` and MUST call `loadSensors('')` on mount first).

### Computed-signal derivation
**Source:** `state/sensors.ts` lines 144-159 (`validationErrors`/`hasValidationErrors`).
**Apply to:** `state/detectors.ts`'s `detectorRows`.

### Card-wrapped `<ul class="argus-list">` list shell
**Source:** `components/GroupList.tsx` lines 25-33.
**Apply to:** `DetectorList.tsx`.

### Hand-off signal + hash navigate (no receiving-end code needed)
**Source:** `components/AreaSuggestionBanner.tsx` lines 47-51.
**Apply to:** `AddDetectorWizard.tsx`'s ≥2-sensor exit.

## No Analog Found

None — every file in the CONTEXT "Files to CREATE"/"Files to MODIFY" lists has a direct or composite analog already read above. `SettingsPage.tsx`'s current internals were not read (out of the phase's named analog list) — flag this as a pre-implementation read for whoever picks up that specific file, not a missing-analog gap.

## Metadata

**Analog search scope:** `orchestrator/ui/src/components/`, `orchestrator/ui/src/state/`, `orchestrator/ui/src/router.ts`, `orchestrator/ui/src/main.tsx` — all real source, no guessing.
**Files scanned:** 14 (GroupsPage, GroupList, GroupListRow, SensorList, SensorListRow, MemberPicker, AreaSuggestionBanner, SensorsPage, state/groups.ts, state/sensors.ts, router.ts, main.tsx, Sidebar.tsx, DetectorDisclosure.tsx)
**Pattern extraction date:** 2026-07-21
