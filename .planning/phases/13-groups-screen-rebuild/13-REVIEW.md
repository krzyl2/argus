---
phase: 13-groups-screen-rebuild
reviewed: 2026-07-20T00:00:00Z
depth: standard
files_reviewed: 9
files_reviewed_list:
  - orchestrator/ui/src/components/GroupsPage.tsx
  - orchestrator/ui/src/components/GroupList.tsx
  - orchestrator/ui/src/components/GroupListRow.tsx
  - orchestrator/ui/src/components/GroupEditorForm.tsx
  - orchestrator/ui/src/components/MemberPicker.tsx
  - orchestrator/ui/src/components/GuidedFlowStep.tsx
  - orchestrator/ui/src/components/AdvancedParamsDisclosure.tsx
  - orchestrator/ui/src/components/AlgorithmChooser.tsx
  - orchestrator/ui/src/components/AttributionPanel.tsx
findings:
  critical: 1
  warning: 6
  info: 3
  total: 10
status: issues
---

# Phase 13: Code Review Report

**Reviewed:** 2026-07-20T00:00:00Z
**Depth:** standard
**Files Reviewed:** 9
**Status:** issues

## Summary

Reviewed the 9 rebuilt Groups-screen components against the Argus Design System adoption
(D-01..D-08 in `13-CONTEXT.md`) and cross-checked their callers/callees (`state/groups.ts`,
`state/groupEditor.ts`, `state/sensors.ts`, `validation/groupParams.ts`, `router.ts`, and the
shared `Card`/`Badge`/`Input`/`Select`/`Checkbox`/`FieldValidationError` primitives) to verify
behavior parity claims hold in practice, not just in comments.

The DS restyle itself (Card/Badge/Select/Checkbox/Input adoption, two-step delete, guided-flow
copy, attribution states) is faithful to the locked decisions. However, tracing call chains
turned up one data-loss-capable bug in group creation (client-side id collision silently
overwrites an unrelated existing group), plus a cross-screen state leak where a Sensors-page
search filter silently narrows which sensors are selectable in the group member picker. Neither
is caught by the 158/158 passing suite because both require inter-component/inter-screen state
that isolated unit tests don't exercise.

None of the findings below revisit or contradict D-01..D-08 — they are correctness issues
orthogonal to the locked visual/behavioral decisions.

## Critical Issues

### CR-01: Creating a group whose slugified name collides with an existing group's id silently overwrites that group (data loss)

**File:** `orchestrator/ui/src/components/GroupEditorForm.tsx:30-36, 89-94`
(root cause completes in `orchestrator/ui/src/state/groups.ts:107-146`, `saveGroup()`)

**Issue:** In create mode (`groupId === null`), every keystroke in the Name field re-derives
`draftGroupId` via `slugify(next)`:

```tsx
onChange={(next) => {
  draftFriendlyName.value = next;
  if (!groupId) {
    draftGroupId.value = slugify(next);
  }
}}
```

`saveGroup()` in `state/groups.ts` then upserts by id:

```ts
const existingIdx = groups.value.findIndex((g) => g.groupId === draft.groupId);
const nextGroups =
  existingIdx >= 0
    ? groups.value.map((g, i) => (i === existingIdx ? draft : g))  // <-- overwrite in place
    : [...groups.value, draft];
```

If the new group's display name slugifies to the same id as an **already-existing, unrelated**
group (e.g. two groups both named "Living Room", or any other slug collision), `existingIdx >= 0`
is true and the brand-new draft **replaces** that other group's entry in `nextGroups` before the
request is even sent. The list POSTed to `/api/groups/save` never contains a duplicate id, so the
backend's own duplicate-id rejection (`GroupInputValidator.cs`, "Duplicate group ID ... must be
unique") never fires — the client has already silently merged the two groups into one before the
backend ever sees them. The operator gets a normal success banner with no indication that a
different, previously-configured group (members, mode, detector, params) was just clobbered.

This is reachable via ordinary use: create "Test", save; later create another group named "Test"
(or anything else that slugifies the same way) — the second save destroys the first group's
configuration with no confirmation or warning.

**Fix:** Before allowing save in create mode, check `draftGroupId.value` against the currently
loaded `groups.value` list and surface a "Name already in use" validation error (blocking save)
when it collides with an existing group's id — analogous to `nameError`/`noAlgorithmError`:

```tsx
const groupIdCollision =
  !groupId && groups.value.some((g) => g.groupId === draftGroupId.value)
    ? 'A group with this name already exists.'
    : null;
// ... include groupIdCollision in hasErrors and render its FieldValidationError
```

## Warnings

### WR-01: GroupsPage reuses the Sensors screen's leftover search filter for its initial sensor load, silently restricting the member picker's catalog

**File:** `orchestrator/ui/src/components/GroupsPage.tsx:13-18`

**Issue:**

```tsx
useEffect(() => {
  loadGroups();
  // Member picker needs the full sensor list — reuse the existing sensors
  // signal/loader rather than introducing a second sensor-fetch path.
  loadSensors(sensorQuery.value);
}, []);
```

`sensorQuery` (`query` from `state/sensors.ts`) is the **same global signal** bound to the
Sensors screen's search box (`SensorsPage.tsx` sets `query.value = next` on every keystroke and
never resets it on navigating away). `loadSensors(q)` calls `GET /api/sensors?q=...`, which is a
server-side filter — the `sensors` signal ends up holding only whatever subset last matched that
query.

If the operator typed a filter on `#/sensors` (e.g. "temp") and then navigates to `#/groups/new`
without clearing it, `GroupsPage`'s mount fetches only sensors matching "temp" into the shared
`sensors` signal, and `MemberPicker` (which layers its own client-side `matchesSensorQuery` filter
on top of this already-filtered prop) can never show or select any sensor outside that leftover
filter — with no indication to the operator that the catalog is incomplete. The stated intent
("member picker needs the *full* sensor list") is not what this code does.

**Fix:** Load the full catalog unconditionally for the Groups screen, independent of the Sensors
screen's query state:

```tsx
useEffect(() => {
  loadGroups();
  loadSensors(''); // full list — do not inherit SensorsPage's leftover filter
}, []);
```

### WR-02: `AttributionPanel` doesn't reset local state when `groupId` changes, so a stale group's attribution can flash under the new group's heading

**File:** `orchestrator/ui/src/components/AttributionPanel.tsx:20-49`

**Issue:** `status`/`loaded` are plain `useState`, and the poll effect's dependency is
`[groupId]`. `GroupEditorForm` renders `<AttributionPanel groupId={groupId} />` at a fixed JSX
position without a `key`, so navigating directly between two group-edit URls that both keep
`isEditor` true (e.g. browser back/forward between `#/groups/A` and `#/groups/B`, or a manual
hash edit) reuses the same component instance instead of remounting it. The effect re-runs and
fires an immediate `poll()` for the new `groupId`, but until that request resolves, the component
continues rendering group A's `status`/`contributions` underneath what is now group B's editor —
a real, if transient, mismatched-data render.

**Fix:** Reset local state synchronously when `groupId` changes, e.g.:

```tsx
useEffect(() => {
  setStatus(null);
  setLoaded(false);
  let cancelled = false;
  // ...poll() as before...
}, [groupId]);
```

or mount `AttributionPanel` with `key={groupId}` in `GroupEditorForm` to force a clean remount.

### WR-03: "anomaly" and "active" group status render with the identical Badge tone — no color distinction for the app's core signal

**File:** `orchestrator/ui/src/components/GroupListRow.tsx:55-59`

**Issue:**

```tsx
{status ? (
  <Badge tone="tracked">{status.isAnomaly ? 'anomaly' : 'active'}</Badge>
) : (
  <span class="argus-label">no status yet</span>
)}
```

`tone="tracked"` is hardcoded regardless of `status.isAnomaly`. `.argus-pill--tracked` resolves to
`background-color: var(--color-status-ok)` (green) with white text — so a group currently flagged
as an anomaly renders in the same "healthy" green pill as a normal, active group; only the text
("anomaly" vs "active") differs. For an anomaly-detection product, the list view is exactly where
an operator scans for anomalies at a glance — losing the color signal here undermines that.
`Badge` already exposes `warn`/`error` tones (`.argus-pill--warn` / `.argus-pill--error`) styled
for this purpose.

**Fix:**

```tsx
<Badge tone={status.isAnomaly ? 'error' : 'ok'}>{status.isAnomaly ? 'anomaly' : 'active'}</Badge>
```

### WR-04: Row is not "click-to-edit" as D-02 specifies — only the small "Edit" text link navigates

**File:** `orchestrator/ui/src/components/GroupListRow.tsx:44-50, 60-62`

**Issue:** D-02 (`13-CONTEXT.md`) calls for adopting "the kit's `Card padding='none'` **clickable
row**" for the group list. The implemented row is a plain `<li>` with no click handler on the row
itself or on the `.argus-row-content` block (entity id + mode/detector badges) — the only way to
navigate to the editor is the small `Edit` text link in the row-meta area. `GroupListRow.test.tsx`
has no test exercising row-level navigation, confirming it isn't implemented. This is a functional
gap against a locked decision, not just a visual one.

**Fix:** Make the row (or at minimum the `.argus-row-content` block) navigate on click, keeping
`Edit`/`Delete group` as explicit affordances for keyboard users:

```tsx
<li class="argus-list-row" onClick={() => { location.hash = `#/groups/${encodeURIComponent(group.groupId)}`; }}>
```

(Guard the delete button's click handler with `event.stopPropagation()` so it doesn't also trigger
navigation.)

### WR-05: `aria-describedby` on the group name field points to a DOM id that is never rendered

**File:** `orchestrator/ui/src/components/GroupEditorForm.tsx:96` (root cause in
`orchestrator/ui/src/components/FieldValidationError.tsx:6-13`)

**Issue:**

```tsx
ariaDescribedby={nameError ? 'group-name-err' : undefined}
...
<FieldValidationError message={nameError ?? undefined} />
```

`FieldValidationError` renders `<span class="argus-param-field__error-msg" role="alert" ...>` with
**no `id` attribute at all** — it doesn't accept one in its props (`{ message?: string }`). So
`aria-describedby="group-name-err"` references a DOM id that never exists anywhere on the page.
Screen readers cannot associate the rendered error text with the Name input via this attribute —
the accessibility linkage is silently broken. (The same broken pattern pre-exists in
`DetectorParamGrid.tsx`, which has the same gap; that file is out of this phase's scope, but the
fix belongs in the shared `FieldValidationError` component either way.)

**Fix:** Add an optional `id` prop to `FieldValidationError` and thread it through from both call
sites:

```tsx
// FieldValidationError.tsx
interface FieldValidationErrorProps { message?: string; id?: string; }
export function FieldValidationError({ message, id }: FieldValidationErrorProps) {
  if (!message) return null;
  return <span id={id} class="argus-param-field__error-msg" role="alert" aria-live="assertive">{message}</span>;
}

// GroupEditorForm.tsx
<FieldValidationError id="group-name-err" message={nameError ?? undefined} />
```

### WR-06: `slugify()` can yield an empty groupId that passes the Name field's own validation

**File:** `orchestrator/ui/src/components/GroupEditorForm.tsx:30-36, 60, 91-93`

**Issue:** `nameError` only checks `draftFriendlyName.value.trim() === ''`. A name consisting
entirely of non-alphanumeric characters (e.g. `"!!!"`, `"---"`, or any non-Latin/emoji-only input)
is non-empty by that check but slugifies to `''` (`replace(/[^a-z0-9]+/g, '_')` then
`replace(/^_+|_+$/g, '')` strips everything, including the resulting all-underscore string). The
Save button is therefore not disabled, and `saveGroup()` will attempt to persist a group with
`groupId: ''`, relying entirely on the backend to reject it — no client-side feedback tells the
operator why Save silently fails or what to fix.

**Fix:** Validate the derived id, not just the raw name:

```tsx
const nameError =
  draftFriendlyName.value.trim() === ''
    ? 'Must provide a value.'
    : !groupId && draftGroupId.value === ''
      ? 'Name must contain at least one letter or number.'
      : null;
```

## Info

### IN-01: Redundant/dead conditions in `GroupsPage`'s route branching

**File:** `orchestrator/ui/src/components/GroupsPage.tsx:20-21`

**Issue:**

```tsx
const isEditor = route.value === '/groups/new' || route.value.startsWith('/groups/');
const groupId = route.value === '/groups/new' ? null : routeGroupId.value;
```

`'/groups/new'.startsWith('/groups/')` is already `true`, so the first disjunct in `isEditor` is
unreachable dead weight. Likewise, `router.ts`'s `parseGroupId` already returns `null` for
`/groups/new`, so `routeGroupId.value` is already `null` on that route and the ternary in `groupId`
is redundant.

**Fix:** Simplify to `const isEditor = route.value.startsWith('/groups/');` and
`const groupId = routeGroupId.value;`.

### IN-02: Three reviewed files pass a `Card` `padding` value that is currently a documented no-op

**File:** `orchestrator/ui/src/components/GroupList.tsx:26`,
`orchestrator/ui/src/components/MemberPicker.tsx:54`,
`orchestrator/ui/src/components/AttributionPanel.tsx:53,61,71,83`

**Issue:** `Card.tsx` explicitly discards its `padding` prop (`void padding`) because no
`'none'`/`'sm'` modifier class exists yet in `argus.css` — all paddings currently render as `'md'`.
This is a pre-existing Phase 10 gap, not introduced here, but all three reviewed files pass
`padding="none"`/`"sm"` expecting the DS-spec spacing (e.g. `MemberPicker`'s scroll-region card,
`AttributionPanel`'s compact panel) that doesn't yet visually exist.

**Fix:** Track/land the missing `.argus-card--none`/`.argus-card--sm` modifier classes so these
three call sites get the spacing they were written for.

### IN-03: Misleading comment about "single source of truth" for member validation

**File:** `orchestrator/ui/src/components/MemberPicker.tsx:41-42, 93-104`

**Issue:** The exported `useMemberPickerValidation` hook's doc comment says it exists "without
duplicating the picker's own validation call," but `MemberPicker` itself independently computes
the identical `validateGroupMembers`/`validateUnitConsistency` pair at lines 41-42 (for its own
inline `FieldValidationError`s), while `GroupEditorForm` separately calls the exported hook for
`hasErrors`/`SaveBar`. The same validation logic runs twice per render from two different call
sites — not a correctness bug (both computations use the same inputs and will always agree), just
a misleading comment given the actual duplication it claims to avoid.

**Fix:** Either have `MemberPicker` accept the already-computed errors as props from
`GroupEditorForm`, or reword the comment to stop claiming de-duplication that isn't there.

---

_Reviewed: 2026-07-20T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
