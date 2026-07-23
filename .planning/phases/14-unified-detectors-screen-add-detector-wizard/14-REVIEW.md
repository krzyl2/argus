---
phase: 14-unified-detectors-screen-add-detector-wizard
reviewed: 2026-07-21T18:57:37Z
depth: standard
files_reviewed: 20
files_reviewed_list:
  - orchestrator/ui/src/components/AddDetectorWizard.tsx
  - orchestrator/ui/src/components/AddDetectorWizard.test.tsx
  - orchestrator/ui/src/components/DetectorList.tsx
  - orchestrator/ui/src/components/DetectorList.test.tsx
  - orchestrator/ui/src/components/DetectorListRow.tsx
  - orchestrator/ui/src/components/DetectorListRow.test.tsx
  - orchestrator/ui/src/components/DetectorsPage.tsx
  - orchestrator/ui/src/components/DetectorsPage.test.tsx
  - orchestrator/ui/src/components/MemberPicker.tsx
  - orchestrator/ui/src/components/MemberPicker.test.tsx
  - orchestrator/ui/src/components/SettingsPage.tsx
  - orchestrator/ui/src/components/SettingsPage.test.tsx
  - orchestrator/ui/src/components/Sidebar.tsx
  - orchestrator/ui/src/components/Sidebar.test.tsx
  - orchestrator/ui/src/components/SingleDetectorEditorForm.tsx
  - orchestrator/ui/src/components/SingleDetectorEditorForm.test.tsx
  - orchestrator/ui/src/main.tsx
  - orchestrator/ui/src/router.test.ts
  - orchestrator/ui/src/router.ts
  - orchestrator/ui/src/state/detectors.test.ts
  - orchestrator/ui/src/state/detectors.ts
findings:
  critical: 0
  warning: 6
  info: 4
  total: 10
status: issues
---

# Phase 14: Code Review Report

**Reviewed:** 2026-07-21T18:57:37Z
**Depth:** standard
**Files Reviewed:** 20
**Status:** issues

## Summary

Reviewed the Phase 14 IA-restructure diff (unified `/detectors` list, `AddDetectorWizard`,
`SingleDetectorEditorForm`, router/sidebar changes, and the relocated Pattern Filters section on
`SettingsPage`) against `14-CONTEXT.md`'s locked decisions (D-01..D-09) and the six pitfalls in
`14-RESEARCH.md`, then traced call chains into `state/sensors.ts`, `state/detectors.ts`, `router.ts`,
`MemberPicker.tsx`, and the backend `/api/sensors/save` handler to verify behavior, not just intent.

**The critical focus area (D-07, Pitfall 1) checks out.** All three save-capable surfaces —
`AddDetectorWizard`, `SingleDetectorEditorForm`, and the relocated `SettingsPage` pattern-filters
section — call `loadSensors('')` unconditionally on mount before any `setTracked`/`save()`, and each
has a genuine regression test (`WIZ-04`, and the `SettingsPage` analog) that asserts the *actual POST
body* preserves the full pre-existing tracked set, not merely that a save fired. Pitfall 6
(`AlgorithmChooser` cross-contamination) is also verifiably avoided — `SingleDetectorEditorForm`
imports only from `state/sensors`, and a dedicated test confirms a pre-set `draftDetector` survives a
mount untouched. `parseSensorEntityId` correctly `try/catch`-wraps `decodeURIComponent` and the
`/sensors`+`/groups` bare-route redirects are implemented and tested.

No data-loss-capable bug was found this phase. The findings below are real UX/quality/compliance
gaps: a reused group-only validation message that becomes actively misleading in the wizard's
1-sensor path, a save surface that skips the validation gate every other save surface enforces, a
sidebar dual-highlight bug, a router fallback that doesn't actually fall back per its own documented
contract, an untrack action with no visible feedback, and an accessibility regression in the
extracted single-sensor form (hardcoded `entityIdx=0` produces a meaningless "entity 0" ARIA label
for every sensor). None of these contradict D-01..D-09 — they are correctness/quality issues
orthogonal to the locked decisions.

## Warnings

### WR-01: `MemberPicker`'s group-only "needs at least 2 members" validation renders — and is actively wrong — during the wizard's valid 1-sensor path

**File:** `orchestrator/ui/src/components/AddDetectorWizard.tsx:62-70`, root cause in
`orchestrator/ui/src/components/MemberPicker.tsx:43-44, 86-87` and
`orchestrator/ui/src/validation/groupParams.ts:16-21`

**Issue:** `MemberPicker` unconditionally computes and renders
`validateGroupMembers(selectedIds)` as a `FieldValidationError`:

```ts
const memberFloorError = validateGroupMembers(selectedIds); // "A group needs at least 2 members."
...
<FieldValidationError message={memberFloorError ?? undefined} />
```

This is correct when `MemberPicker` is only ever used for group creation (its original call site,
`GroupEditorForm`, where <2 members truly is invalid). `AddDetectorWizard` now reuses the same
component for a **dual-purpose** flow where selecting exactly 1 sensor is a fully valid, intended
outcome (`WIZ-02` — "Configure detector"). Because the wizard never reads or gates on
`memberFloorError`, the moment exactly 1 sensor is checked, the UI simultaneously shows:
- an **enabled** primary button labeled "Configure detector" (implying the selection is valid), and
- the error text "A group needs at least 2 members." rendered directly beneath the picker.

The same applies to `validateUnitConsistency(selectedEntries, mode)` — the wizard hardcodes
`mode="peer_divergence"` (`AddDetectorWizard.tsx:65`) purely to satisfy the prop, so a 2+ selection
with mixed units shows a peer-divergence-specific unit-mismatch error even though the operator hasn't
chosen a mode yet (mode is decided later, inside `GroupEditorForm`).

This message is visible on every normal use of the wizard's primary single-sensor path (0 or 1
selected is the default/most common state before the operator adds a second sensor), not an edge
case. No existing test asserts its absence for the 1-selected state.

**Fix:** Gate `MemberPicker`'s own inline member-floor/unit-mismatch messages behind a prop (e.g.
`showGroupValidation?: boolean`, default `true` to preserve `GroupEditorForm`'s behavior unchanged)
and have `AddDetectorWizard` pass `showGroupValidation={false}`, since the wizard has its own
"N sensor(s) selected" + button-label logic that already communicates validity for both paths:

```tsx
<MemberPicker
  ...
  showGroupValidation={false}
/>
```

### WR-02: `SettingsPage`'s pattern-filters `SaveBar` doesn't gate on `hasValidationErrors`, unlike every other `state/sensors.ts` save surface — a stale invalid detector param elsewhere silently blocks the pattern save with no explanation

**File:** `orchestrator/ui/src/components/SettingsPage.tsx:195`

**Issue:**

```tsx
<SaveBar saving={patternsSaving} disabled={patternsSaving} onSave={save} />
```

Compare to `SingleDetectorEditorForm.tsx:78`: `disabled={saving || hasValidationErrors.value}`.
`hasValidationErrors` is a **global** computed over every entry in `entityEdits` (`state/sensors.ts:159`),
not scoped to the current screen. If the operator has, in the same session, opened
`SingleDetectorEditorForm` for some sensor, typed an invalid param value, and navigated away
*without* saving (nothing resets `entityEdits` on navigation), that invalid entry persists in the
global signal. Later, editing only the Pattern Filters textarea on `/settings` and clicking Save is
not blocked client-side, but `POST /api/sensors/save`'s server-side `InputValidator.Validate` rejects
the **entire** request (`Program.cs:363-369`, `{ ok: false, kind: "validation" }`) — the pattern-filter
edit the operator actually intended to save is silently dropped, and `SettingsPage` has no detector
param fields to show *why*, leaving the operator with an unexplained generic validation failure banner.

**Fix:** Match every other save surface:

```tsx
<SaveBar saving={patternsSaving} disabled={patternsSaving || hasValidationErrors.value} onSave={save} />
```

(`hasValidationErrors` is already exported from `state/sensors.ts`; just add it to the existing import
list at `SettingsPage.tsx:5-10`.)

### WR-03: Sidebar highlights both "Detectors" and "Add detector" simultaneously when on `/detectors/add`

**File:** `orchestrator/ui/src/components/Sidebar.tsx:24-31`

**Issue:**

```ts
if (item.id === 'detectors') return currentRoute === '/detectors' || currentRoute.startsWith('/detectors/');
if (item.id === 'add-detector') return currentRoute === '/detectors/add';
```

For `currentRoute === '/detectors/add'`, both conditions are true: the `add-detector` item matches
exactly, and the `detectors` item also matches via the `/detectors/` prefix check. Both nav buttons
render with `argus-sidebar__item--active` at once. `Sidebar.test.tsx` only asserts the highlight for
`/detectors/sensor/...` (a route with no dedicated nav item of its own) — it never exercises
`/detectors/add`, so this dual-highlight regression has no test coverage.

**Fix:** Exclude the dedicated `/detectors/add` sub-route from the generic `/detectors/*` match:

```ts
if (item.id === 'detectors') {
  return currentRoute === '/detectors' ||
    (currentRoute.startsWith('/detectors/') && currentRoute !== '/detectors/add');
}
```

### WR-04: `main.tsx`'s `/detectors/sensor/:entityId` route branch does not fall back to the Detectors list when the id fails to parse — contradicts the documented D-01 fallback contract

**File:** `orchestrator/ui/src/main.tsx:25-26` (root cause: branch chosen on `route.value.startsWith(...)`
alone, ignoring whether `routeSensorEntityId.value` actually parsed)

**Issue:** `router.ts`'s own doc comment for `parseSensorEntityId` states the design intent: "Returns
null for a non-matching path or a malformed percent-encoding (**defensive fallback per T-14-01-01**)",
and `14-CONTEXT.md`'s D-01 explicitly specifies "...defensive fallback to the list on parse failure."
`parseSensorEntityId` itself correctly returns `null` on malformed input (verified by
`router.test.ts`'s `%E0%A4%A` case) — but `main.tsx` never checks that value before choosing the
render branch:

```tsx
} else if (route.value.startsWith('/detectors/sensor/')) {
  page = <SingleDetectorEditorForm entityId={routeSensorEntityId.value ?? ''} />;
}
```

A malformed or truncated hash (e.g. `#/detectors/sensor/` with a trailing slash and nothing after, or
a bad `%`-escape) still satisfies `startsWith('/detectors/sensor/')`, so this branch is taken
regardless, with `entityId=''` silently substituted. The result is not a crash, but not "fallback to
the list" either — it renders a `SingleDetectorEditorForm` with a blank title, no matching sensor, and
an empty detector-editor block. Clicking that empty form's "Add detector" affordance would call
`addDetector('')`, inserting a phantom `entityId: ''` entry (`isTracked: true` by
`addDetector`'s default) into the global `entityEdits` signal — harmless in practice only because the
backend's own `Where(s => !string.IsNullOrEmpty(s))` filter (`Program.cs:324`) strips empty ids before
persisting, but this is incidental backend defense, not the client behavior the decision specifies.

**Fix:** Check the parsed value, not just the path prefix, and fall back to the list:

```tsx
} else if (route.value.startsWith('/detectors/sensor/') && routeSensorEntityId.value) {
  page = <SingleDetectorEditorForm entityId={routeSensorEntityId.value} />;
} else if (route.value.startsWith('/detectors/sensor/')) {
  page = <DetectorsPage />; // parse failure — defensive fallback per D-01/T-14-01-01
}
```

### WR-05: "Untrack sensor" in `SingleDetectorEditorForm` gives no immediate visual feedback — the detector editor stays fully open and editable after untracking

**File:** `orchestrator/ui/src/components/SingleDetectorEditorForm.tsx:60-76`

**Issue:** The pre-existing inline block this form was extracted from only ever rendered the detector
editor when `isSelected && isTracked` (`SensorListRow.tsx:67` in the pre-Phase-14 tree) — unchecking
the tracked checkbox immediately collapsed the detector UI. The new form has no such gating:

```tsx
const edit = entityEdits.value[entityId];
const detectors = edit?.detectors ?? [];
...
<DetectorDisclosure entityId={entityId} entityIdx={0} detectors={detectors} .../>
...
<Button variant="destructive-ghost" size="xs" onClick={() => setTracked(entityId, false)}>
  Untrack sensor
</Button>
```

Clicking "Untrack sensor" calls `setTracked(entityId, false)`, which flips `isTracked` in
`entityEdits` but leaves `detectors` untouched (`state/sensors.ts:86-94`) — and `DetectorDisclosure`
is rendered unconditionally regardless of `edit?.isTracked`. The operator sees no change at all: the
same detector rows, params, and "Add detector" button remain visible and editable exactly as before
the click, with no banner/label indicating the sensor is now marked for removal. The only way to
confirm the click had any effect is to Save and then check whether the sensor disappeared from the
`/detectors` list on a later visit.

**Fix:** Gate the detector editor on tracked state and/or show explicit feedback, e.g.:

```tsx
{edit?.isTracked ? (
  <DetectorDisclosure entityId={entityId} entityIdx={0} detectors={detectors} .../>
) : (
  <p class="argus-label">This sensor will be untracked on next save.</p>
)}
```

### WR-06: `SingleDetectorEditorForm` hardcodes `entityIdx={0}`, producing a meaningless "Detector type for entity 0" ARIA label for every sensor (accessibility regression)

**File:** `orchestrator/ui/src/components/SingleDetectorEditorForm.tsx:62`, consumed at
`orchestrator/ui/src/components/DetectorEntry.tsx:38` and
`orchestrator/ui/src/components/DetectorParamGrid.tsx:60`

**Issue:** `entityIdx` feeds directly into a screen-reader-facing string:

```tsx
// DetectorEntry.tsx
aria-label={`Detector type for entity ${entityIdx}`}
```

In the original `SensorsPage`/`SensorList` inline block, `entityIdx` was the entity's real position
in the rendered (sorted/filtered) sensor list, so the label at least varied per entity. In the new
extracted form, every sensor's detector-type `<select>` — regardless of which entity is actually open
— announces itself to assistive technology as "Detector type for entity 0". This isn't a functional
bug (DOM ids like `param-0-${detIdx}-${key}` don't collide, since only one entity is ever rendered on
this page) but it is a real, silent a11y content regression: the label no longer identifies *which*
entity's detector is being configured.

**Fix:** Thread the actual `entityId` (or a stable hash of it) through instead of a fixed index, or
have `DetectorEntry`/`DetectorParamGrid` accept an optional `entityLabel?: string` for the ARIA string
while keeping `entityIdx` for DOM-id uniqueness:

```tsx
// DetectorEntry.tsx
aria-label={`Detector type for ${entityLabel ?? `entity ${entityIdx}`}`}
```

```tsx
// SingleDetectorEditorForm.tsx
<DetectorDisclosure entityId={entityId} entityIdx={0} entityLabel={entityId} detectors={detectors} .../>
```

## Info

### IN-01: `main.tsx`'s bare-`/groups` branch is now fully unreachable dead code as a direct consequence of the D-01 redirect

**File:** `orchestrator/ui/src/main.tsx:15, 29`

**Issue:** `const isGroupsRoute = route.value === '/groups' || route.value.startsWith('/groups/');`
predates this phase, but `router.ts`'s new `normalizeHash` (D-01) now redirects bare `/groups` to
`/detectors` before `route.value` is ever set — `route.value === '/groups'` can no longer be true.
This compounds the already-flagged `13-REVIEW.md` `IN-01` finding (`GroupsPage.tsx`'s own
`'/groups/new'.startsWith('/groups/')` redundancy), which was left unfixed; Phase 14 adds a second,
now-larger layer of the same class of dead branch on top of it.

**Fix:** Simplify to `const isGroupsRoute = route.value.startsWith('/groups/');` (the first disjunct
is subsumed and now literally unreachable).

### IN-02: `SensorsPage.tsx`, `SensorListRow.tsx`, `SensorList.tsx` are orphaned — no longer imported anywhere, left in the tree

**File:** `orchestrator/ui/src/components/SensorsPage.tsx`,
`orchestrator/ui/src/components/SensorListRow.tsx`, `orchestrator/ui/src/components/SensorList.tsx`

**Issue:** `main.tsx` no longer imports `SensorsPage`, and nothing else in `src/` renders it — its
render path is fully unreachable now that the default route and the bare-`/sensors` redirect both
point to `/detectors`. `14-CONTEXT.md` explicitly leaves this to "Claude's discretion" ("thin redirect
shim or deleted outright... either way [dead code]"), so this is not a violation of any locked
decision, but the files (plus their still-passing, now-untested-in-practice test files) remain in the
tree with no reference, which is a maintenance/lint-noise cost going forward.

**Fix:** Delete `SensorsPage.tsx`, `SensorsPage.test.tsx` (if present), `SensorListRow.tsx`,
`SensorListRow.test.tsx`, and `SensorList.tsx`/`SensorList.test.tsx` in a follow-up cleanup pass, or
explicitly note them as intentionally-retained reference code.

### IN-03: `DetectorListRow`'s non-null type assertions bypass the type system instead of a discriminated union

**File:** `orchestrator/ui/src/components/DetectorListRow.tsx:13-17`

**Issue:**

```tsx
export function DetectorListRow({ row }: DetectorListRowProps) {
  return row.kind === 'group' ? (
    <GroupRow group={row.group as NonNullable<DetectorRow['group']>} />
  ) : (
    <SensorRow entry={row.entry as NonNullable<DetectorRow['entry']>} />
  );
}
```

`DetectorRow` (`state/detectors.ts:11-16`) models `group`/`entry` as independently-optional fields
correlated only by convention with the `kind` discriminant, not by the type system. The `as
NonNullable<...>` casts assert that correlation holds without the compiler verifying it — if a future
change to `detectorRows` ever produces a `kind: 'group'` row with `group` unset, this would compile
cleanly and throw a runtime `TypeError` reading `group.friendlyName` inside `GroupRow`, rather than
being caught at build time.

**Fix:** Model `DetectorRow` as a real discriminated union so narrowing is exhaustive and cast-free:

```ts
export type DetectorRow =
  | { key: string; kind: 'group'; group: GroupConfig }
  | { key: string; kind: 'sensor'; entry: SensorEntry };
```

### IN-04: Unified list's sensor row omits the "assigned-detector" badge described in `14-CONTEXT.md`'s row layout — cosmetic spec-text/implementation mismatch, not a functional defect

**File:** `orchestrator/ui/src/components/DetectorListRow.tsx:43-60`

**Issue:** `14-CONTEXT.md`'s `<specifics>` section describes the sensor row as "friendlyName/entityId
title, tracked Badge **+ assigned-detector Badge**." The implemented `SensorRow` only renders a
`tracked` Badge, matching the *actual* pre-existing `SensorListRow.tsx` JSX verbatim (which never had
a detector-name badge either — confirmed against the pre-Phase-14 file). D-03 explicitly directs
relocating existing JSX "verbatim... do not invent new markup," so the implementation is arguably more
faithful to that directive than to the `<specifics>` prose, which appears to have over-described what
`SensorListRow` actually rendered. Flagging only so the discrepancy is a recorded, deliberate choice
rather than a silent gap noticed later during UAT.

**Fix:** No code change required if the verbatim-reuse reading is accepted; otherwise, add a small
`Badge` showing `entry` detector type(s) sourced from `entityEdits.value[entry.entityId]?.detectors`.

---

_Reviewed: 2026-07-21T18:57:37Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
