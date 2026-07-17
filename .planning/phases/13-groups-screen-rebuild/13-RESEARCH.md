# Phase 13: Groups Screen Rebuild - Research

**Researched:** 2026-07-17
**Domain:** Preact SPA frontend rebuild (component composition + markup refactor), no backend changes
**Confidence:** HIGH

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

**Navigation / editing model (GRP-12)**
- **D-01:** Keep the hash-router model (`#/groups`, `#/groups/new`, `#/groups/:id`) — URL is
  deep-linkable and already works. Adopt only the DS reference's *visual* `PageHeader` and the
  "← Back to groups" affordance. **Reject** the kit's in-place `editing`-state toggle (it drops
  URL addressability). `GroupsPage.tsx`'s internal route-branch between `GroupList` and
  `GroupEditorForm` stays.

**Group list row (GRP-12)**
- **D-02:** Card-wrapped, click-to-edit row — adopt the kit's `Card padding="none"` clickable
  row + two `Badge`s (mode + detector). **But preserve GRP behavior the kit's mock omits:** keep
  the inline two-step "Delete group" confirm (`destructive-ghost`, 3s arm window, no
  `window.confirm`) and the status indicator ("no status yet" / active|anomaly `Badge`) as
  right-aligned row meta.
  > ⚠ DS-reference conflict flagged (Rule 7): `Groups.jsx` shows a pure click-to-edit row with no
  > delete and no status. The kit's row is a mockup simplification; the existing delete + status
  > behavior wins. Use the kit for row/Card/Badge visual fidelity only.

**Algorithm chooser — mode filtering (GRP-13)**
- **D-03:** Preserve current chooser behavior — `AlgorithmChooser` shows the full detector
  catalog regardless of `draftMode`, and the guided "What are you monitoring?" step is always
  available (state machine in `state/groupEditor.ts`). Restyle only.
  > ⚠ DS-reference conflict flagged: `Groups.jsx` filters the catalog by mode and gates the
  > guided step to `mode === 'joint'`. That is a behavioral change to `groupEditor.ts` + tests,
  > out of scope. Current behavior is authoritative.
- **D-04:** The wizard steps (guided question → `AlgorithmCard` grid → `SensitivityPresetPicker`
  → `AdvancedParamsDisclosure`) must render to DS spec, with radio-card 2px accent-border
  selection (never color alone). `AlgorithmCard` (Phase 10, string-prop-widened in Phase 12) is
  already A11Y-02 compliant — reuse it, do not re-derive. Restyle `GuidedFlowStep`'s buttons to
  the DS `Card` + `Button` layout; keep its copy verbatim (Copywriting Contract, 2 answers +
  skip link).

**Attribution panel (GRP-14)**
- **D-05:** Preserve the live-polling, server-driven behavior of `AttributionPanel` — 60s poll of
  `GET /api/groups/{id}/status`, the 4 states (loading / no-score-yet / unsupported when
  `contributions.length === 0` / ranked list), and the server pre-sort (never client re-sort).
  Restyle only: wrap in `Card` + a `SectionLabel` ("Member attribution · last result, refreshes
  ~60s"), render the unsupported state via the shared `EmptyState` component. `AttributionBar`
  restyled to DS spec — top-ranked uses `--color-accent` fill, others neutral.
  > ⚠ DS-reference conflict flagged: `Groups.jsx` gates attribution by detector *type*
  > (peer_divergence/pca → EmptyState) client-side with static mock bars. The real contract is
  > server-driven via `contributions.length`; keep that. Kit's bars are a visual reference only.

**Component adoption depth — full adoption**
- **D-06:** Full adoption of Phase 10 primitives, refactoring markup as needed:
  - Wrap group list and member picker in `Card` (member picker: `padding="none"`, scroll region).
  - Mode selector: raw `<select>` → shared `Select` with descriptive option labels.
  - Member picker: raw `<ul>`/`<input>` → shared `Card` + `Checkbox` + `Badge` ("member") +
    `SearchInput`; `argus-pill--tracked` → `Badge`.
  - Name field: raw `<input>` → shared `Input` (built-in `label` + `error` — **research
    correction below**), error driven by the existing name-required rule.
  - `AreaSuggestionBanner`, `GroupSaveResultBanner` → shared `Banner` tones (already retrofitted
    in Phase 10 Wave 3 — verify, don't re-derive).
- **D-07:** Preserve unchanged (behavior parity): all of `validation/groupParams.ts`
  (`validateGroupMembers` member-floor, `validateUnitConsistency` unit-mismatch, English
  messages — do not reword); the `state/groups.ts` draft layer (`draftFriendlyName`/
  `draftMembers`/`draftMode`/`draftDetector`/`draftParams`/`draftPresetLabel`, `saveGroup`,
  `deleteGroup`, slugify-on-create); the `state/groupEditor.ts` chooser state machine
  (`chooserMode`, `selectedDetector`, `guidedRecommended`, catalog load-once);
  `SensitivityPresetPicker` (Phase 10 verified DS-compliant in place — Med default,
  `isCustomized`, accent-color radios); `MIN_QUERY_LENGTH = 2` gate in `MemberPicker`; the 60s
  `AttributionPanel` poll; `SaveBar` disabled/saving flow.

### Claude's Discretion
- Exact grid/spacing/typography per section follows the Phase 10 shared library + the
  `ui_kits/admin/Groups.jsx` visual reference (name+mode two-col grid, `maxWidth: 720` editor
  column, section labels). Planner may refine layout details.
- Whether the member picker's meta column shows unit-of-measurement only (current) or is
  dropped is a styling detail — no live per-member value exists on `SensorEntry`, so the kit's
  `{value} {unit}` is not fully sourceable; field/behavior must not change.
- Whether `AdvancedParamsDisclosure` uses the DS reference's `1fr 1fr` grid is styling — field
  set/order/defaults and preset expansion must not change (D-07).

### Deferred Ideas (OUT OF SCOPE)
- **Algorithm tester/simulator in group config** — Backlog Phase 999.1. Not this phase.
- **Live per-member value column** in the member picker — `SensorEntry` has no live-value field.
  Own phase if a live-value feed is added.
- **Adopting the kit's mode-filtered catalog + joint-only guided gate** — a behavioral change to
  `groupEditor.ts`; if wanted, its own scoped change with test updates (D-03 flags it).
- **Kit's in-place (URL-less) editing model** — rejected for deep-linking (D-01); revisit only if
  URL addressability is ever dropped project-wide.

None of the above block Phase 13.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| GRP-12 | Group editor rebuilt to Design System spec | Patterns 1 (list row Card/Badge), 2 (name/mode field DS layout), 3 (member picker full adoption); MemberPicker reconciliation (Summary); preserve `state/groups.ts`/`groupParams.ts` verbatim |
| GRP-13 | Algorithm creation wizard (guided flow) rebuilt to Design System spec, radio-card 2px accent selection | Pattern 4 (wizard restyle around unchanged `groupEditor.ts` state machine); reuse of already-widened `AlgorithmCard`/`SensitivityPresetPicker`; Pitfall 1 (mode-filter temptation) |
| GRP-14 | Attribution panel (AttributionBar) rebuilt to Design System spec | Pattern 5 (Card/SectionLabel/EmptyState wrap around unchanged poll); AttributionBar restyle (accent vs neutral fill, unchanged prop contract) |

</phase_requirements>

## Summary

This phase rebuilds `GroupsPage.tsx` and its full component tree — list, editor form, member
picker, algorithm wizard, and attribution panel — against the Argus Design System, exactly
mirroring Phase 12's approach: full adoption of Phase 10 primitives with markup/structure
refactor permitted, while three logic layers stay completely untouched: `state/groups.ts`,
`state/groupEditor.ts`, and `validation/groupParams.ts`. All source files (11 components, 2 state
modules, 1 validation module, `api/types.ts`) were read in full; the codebase already has
everything this phase needs — the two shared primitives Phase 12 taught to accept the shapes
this phase needs (`AlgorithmCard` widened to plain strings, `Input` with `id`/`step`/
`ariaDescribedby` passthrough) are **already in their final, Phase-12-shipped form** and need no
further changes. No new dependencies, no new CSS tokens beyond what Phase 10/11/12 already
added, no backend changes.

**MemberPicker reconciliation (the one concrete pre-existing-state issue this phase must
resolve):** the working tree has an uncommitted diff on `MemberPicker.tsx` (confirmed via
`git diff`) that adds `MIN_QUERY_LENGTH = 2` and a "type at least 2 characters" gate before any
sensor rows render. CONTEXT.md's D-07 explicitly locks this gate in as required behavior ("the
`MIN_QUERY_LENGTH = 2` gate in `MemberPicker`"), so this is not a conflict to resolve by
choosing a side — it is confirmation that the uncommitted local edit **is** the correct,
already-decided target behavior. The planner's first task in the `MemberPicker.tsx` plan must
fold this diff into the phase's first commit (the working tree's current state, not
`HEAD`'s prior state, is the correct starting point for the rebuild) — do not revert it while
refactoring markup, and do not treat it as a merge-driven behavior change requiring discussion.
The Phase 12 verification report (`12-VERIFICATION.md`, "Gaps Summary" note) already flagged this
file's uncommitted state as "a Phase 13 concern" — this confirms the file was intentionally left
staged for this phase rather than lost/orphaned work.

**AlgorithmCard / Input are Phase-12-final, not Phase-10-original** (the two components CONTEXT.md
references as reusable "as-is"): `AlgorithmCard.tsx` today (post-Phase-12) already has the
generic `{name, bestFor, selected, recommended, onSelect: (name: string) => void}` shape D-04
describes — no widening work remains, only markup/restyle of its callers
(`AlgorithmChooser.tsx`'s existing call site is already correct and untouched by this phase).
`Input.tsx` already has `id`/`step`/`ariaDescribedby` optional passthroughs — the DS
"built-in label + error" framing in D-06 does **not** match the actual component (same
correction Phase 12 made): `Input.tsx` renders no label/error internally. The established repo
convention (used in `SettingsPage.tsx` and Phase 12's `DetectorParamGrid.tsx`) is an external
`<label class="argus-param-field__label">` + `<Input invalid>` + a separate
`<FieldValidationError message={error} />` — apply this exact pattern to the group-name field
and to `AdvancedParamsDisclosure`'s param grid, not a component-internal label/error prop.

**Test coverage gap:** zero test files exist today for any Groups-family component
(`GroupsPage`, `GroupList`, `GroupListRow`, `GroupEditorForm`, `MemberPicker`, `AttributionBar`,
`GuidedFlowStep`, `SensitivityPresetPicker`, `AdvancedParamsDisclosure`, `AreaSuggestionBanner`,
`GroupSaveResultBanner`) — confirmed by listing `orchestrator/ui/src/components/*.test.tsx`.
Only `AlgorithmChooser.test.tsx`, `AlgorithmCard.test.tsx`, and `AttributionPanel.test.tsx` exist,
and none of the three needs modification for this phase (their props/behavior are unchanged).
This is the single largest Wave 0 gap and should be sized accordingly — Phase 12 added 6 new
test files for a comparably-scoped rebuild; Phase 13 likely needs a similar or larger count given
more components are in scope.

**Primary recommendation:** Refactor markup only, file by file, following the exact
`SettingsPage.tsx`/Phase-12 conventions already established for `.argus-page-header`, `Card`,
`Badge`, external `Input` label/error wrapping, and `Banner` tones. Fold the uncommitted
`MemberPicker.tsx` diff into the phase's own history as the starting point. Do not touch
`state/groups.ts`, `state/groupEditor.ts`, `validation/groupParams.ts`, `AlgorithmCard.tsx`, or
`Input.tsx` — all five are already exactly right for this phase's needs.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Group list rendering (Card/Badge row) | Browser / Client (Preact) | API/Backend (`GET /api/groups`) | Server returns the full list; SPA renders/wraps in Card, computes delete-arm/status display client-side |
| Two-step delete confirm | Browser / Client | API/Backend (`POST /api/groups/save`, full-list-replace) | Arm/confirm timer is pure local UI state; the actual delete is a save-endpoint call, unchanged |
| Group name/mode fields | Browser / Client | API/Backend (`GroupInputValidator.cs`, defense-in-depth) | Client validation (`nameError`, `groupParams.ts`) gates Save; server re-validates every POST independently |
| Member picker (search + multi-select) | Browser / Client | API/Backend (`sensors` signal already loaded by `GroupsPage`) | Filtering/gating (`MIN_QUERY_LENGTH`) is pure client computation over an already-fetched sensor list |
| Algorithm wizard (guided + manual + preset + advanced) | Browser / Client (`state/groupEditor.ts`) | API/Backend (`GET /api/detectors/catalog`) | Catalog is server-sourced; all wizard state (chooser mode, selection, preset label) is client-only until Save |
| Attribution panel (60s poll, 4 states) | Browser / Client | API/Backend (`GET /api/groups/{id}/status`) | Server computes and pre-sorts contributions; client only renders + polls, never re-sorts |
| Save / delete persistence | API/Backend (`POST /api/groups/save`) | Database/Storage (`entities.yaml` via `ConfigWriter`) | Out of scope — no backend changes this phase |

## Standard Stack

No new packages. This phase is 100% composition of existing in-repo components using the
existing toolchain (identical stack to Phase 12).

### Core (already installed — versions from `orchestrator/ui/package.json`)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| preact | 10.29.3 | Component runtime | Locked project-wide (v4.0) |
| @preact/signals | 2.9.2 | Reactive state (`state/groups.ts`, `state/groupEditor.ts`) | Locked project-wide |
| vitest | 4.1.9 | Test runner | Locked project-wide |
| @testing-library/preact | 3.2.4 | Component test rendering | Locked project-wide |

### Alternatives Considered
None — this is a rebuild constrained to the existing stack; introducing any new library would
violate the "frontend rebuild only" framing in CONTEXT.md's scope.

**Installation:** none required.

## Package Legitimacy Audit

Not applicable — no new packages are introduced by this phase.

## Architecture Patterns

### System Architecture Diagram

```
GET /api/groups          GET /api/detectors/catalog       GET /api/groups/{id}/status (60s poll)
        |                          |                                  |
        v                          v                                  v
+---------------------------- GroupsPage.tsx --------------------------------+
| route-branch: '/groups' -> GroupList | '/groups/new','/groups/:id' -> GroupEditorForm |
| loads: groups[] (loadGroups), sensors[] (loadSensors, shared with Sensors screen)      |
+-------------------+----------------------------------+---------------------------------+
                     |                                  |
                     v                                  v
              GroupList.tsx                     GroupEditorForm.tsx
              (Card-wrapped rows,                (name Input, mode Select,
               EmptyState if []                    MemberPicker, AlgorithmChooser,
               groups)                             AttributionPanel [existing only],
                     |                              SaveBar, GroupSaveResultBanner)
                     v                                  |
              GroupListRow.tsx                          |
              - click row -> navigate #/groups/:id      |
              - two-step delete (armed/confirm,         |
                3s window, destructive-ghost Button)     |
              - Badge(mode) + Badge(detector) +          |
                status Badge / "no status yet"           |
                                                          |
        +-------------------------------------------------+-------------------+
        |                              |                                      |
        v                              v                                      v
  MemberPicker.tsx               AlgorithmChooser.tsx                 AttributionPanel.tsx
  - MIN_QUERY_LENGTH=2 gate      - chooserMode signal drives:          - 60s poll, 4 states
    (uncommitted diff, D-07        GuidedFlowStep OR AlgorithmCard      (loading / no-score /
    locks this in)                 grid                                 unsupported / ranked)
  - Card+Checkbox+Badge+          - selectedEntry -> Sensitivity        - never re-sorts
    SearchInput rows                PresetPicker + AdvancedParams        contributions
  - draftMembers signal            Disclosure                          - AttributionBar per
    (state/groups.ts)             - draftDetector/draftParams sync        contribution row
                                     (state/groups.ts, unchanged)
```

Data/behavior that must NOT be touched by this diagram's refactor: `state/groups.ts` (all
exports), `state/groupEditor.ts` (all exports), `validation/groupParams.ts` (all exports),
`api/types.ts` shapes, `AlgorithmCard.tsx`, `Input.tsx`, `SensitivityPresetPicker.tsx`'s internal
logic, `AttributionPanel.tsx`'s poll/state logic, `SaveBar.tsx`.

### Recommended Project Structure
No new files/folders needed beyond the existing flat `orchestrator/ui/src/components/` layout.
Every production file this phase touches already exists; no renames. New files are test files
only (see Wave 0 Gaps).

### Pattern 1: Group list row → Card + two Badges + preserved delete/status (D-02)
**What:** `GroupList.tsx` wraps its `<ul>` in `Card padding="none"` (same pattern as Phase 12's
`SensorList.tsx`). `GroupListRow.tsx` keeps its existing two-step delete state machine
(`armed`/`timerRef`/`CONFIRM_WINDOW_MS`) and status Badge/`"no status yet"` fallback verbatim —
only the row's outer wrapping and Badge set changes (add a detector-name `Badge tone="accent"`
alongside the existing mode `Badge`, per the DS reference's two-badge row).
**When to use:** Exactly this list, per D-02.
**Example:**
```tsx
// Source: orchestrator/ui/src/components/GroupList.tsx (current) + Card pattern from
// Phase 12's SensorList.tsx (orchestrator/ui/src/components/SensorList.tsx:73-78)
import { Card } from './Card';

export function GroupList({ groups }: GroupListProps) {
  if (groups.length === 0) {
    return <EmptyStateForGroups />; // existing .argus-empty markup, or shared EmptyState if its copy fits
  }
  return (
    <Card padding="none">
      <ul class="argus-list">
        {groups.map((group) => <GroupListRow key={group.groupId} group={group} />)}
      </ul>
    </Card>
  );
}
```
```tsx
// GroupListRow.tsx — add detector Badge, keep delete/status logic unchanged (lines 33-42, 50-58
// of the current file are untouched):
<div class="argus-row-content">
  <span class="argus-row-entity-id">{group.friendlyName || group.groupId}</span>
  <Badge tone="neutral">{modeLabel}</Badge>
  <Badge tone="accent">{group.detector}</Badge>  {/* NEW — matches Groups.jsx's 2-badge row */}
</div>
```
**Note on click-to-navigate:** the kit's row is a `<div onClick={() => onEdit(g)}>`; the current
codebase already has a working `<a href="#/groups/{id}">Edit</a>` link plus a separate delete
button in `.argus-row-meta`. D-02 does not require making the *whole row* clickable — only that
the row is Card-wrapped with the two Badges; whether to also make the row body (not the delete
button) clickable via an `onClick` navigating `location.hash` is a Claude's-discretion styling
choice, but if done, follow Phase 12's `SensorListRow` `stopPropagation` pattern (Pitfall 1 below)
so the Delete button's click does not also trigger row navigation.

### Pattern 2: Name/mode two-column editor header (D-06 Input/Select adoption)
**What:** Replace `GroupEditorForm.tsx`'s raw `<input id="group-name">` with the shared `Input`
(external label + `FieldValidationError`, exact `SettingsPage.tsx`/Phase-12-`DetectorParamGrid`
convention — **not** a component-internal label/error prop, correcting D-06's premise same as
Phase 12's Pattern 4 did). Replace the raw `<select id="group-mode">` with the shared `Select`
component (already generic, takes `{value, options, onChange}` — no changes needed to
`Select.tsx` itself).
**Example:**
```tsx
// Source: orchestrator/ui/src/components/Select.tsx (existing, unmodified) +
// orchestrator/ui/src/components/SettingsPage.tsx external-label convention
import { Input } from './Input';
import { Select } from './Select';

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

<div class="argus-param-field">
  <label class="argus-param-field__label" for="group-mode">Mode</label>
  <Select
    value={draftMode.value}
    onChange={(v) => { draftMode.value = v as typeof draftMode.value; }}
    ariaLabel="Mode"
    options={[
      { value: 'peer_divergence', label: 'Peer-divergence — which sensor is diverging' },
      { value: 'joint', label: 'Joint (multivariate) — unusual combination' },
    ]}
  />
</div>
```
**Landmine:** `FieldValidationError`'s `id={`${inputId}-err`}` convention must match whatever
`ariaDescribedby` value `Input` is given — see Pitfall 2 below (same class of bug Phase 12 flagged
for `DetectorParamGrid`, now applying to the group-name field for the first time).

### Pattern 3: Member picker full adoption — Card + Checkbox + Badge + SearchInput (D-06)
**What:** Rewrite `MemberPicker.tsx`'s raw `<label style="display:contents">` + raw
`<input type="checkbox">` + raw `.argus-pill--tracked` rows to use the shared `Checkbox` and
`Badge` components, and wrap the results `<ul>` in `Card padding="none"` with a scrollable
region (matches the DS reference's `maxHeight: 220, overflow: 'auto'` — a styling-only addition,
no new CSS token needed if `.argus-member-picker` gets a `max-height`/`overflow-y` rule).
**Critical constraint — reconcile the uncommitted diff first:** the on-disk `MemberPicker.tsx`
already contains the `MIN_QUERY_LENGTH = 2` gate (confirmed via `git diff` — this is an
*uncommitted* local change, not yet in any commit). This gate must be preserved verbatim (D-07)
while the markup below it is rewritten — do not lose the `queryTooShort` branch when swapping
`<input type="checkbox">` for `<Checkbox>`.
**Example:**
```tsx
// Source: orchestrator/ui/src/components/MemberPicker.tsx (current, uncommitted MIN_QUERY_LENGTH
// gate included) + Checkbox/Badge/Card from Phase 10, + Phase 12's stopPropagation-free row
// pattern (member rows have no competing row-level onClick, so the <label> wrapper can stay —
// unlike SensorListRow, there is no row-select feature here to conflict with the checkbox).
import { Card } from './Card';
import { Checkbox } from './Checkbox';
import { Badge } from './Badge';

const MIN_QUERY_LENGTH = 2; // KEEP — already present in the uncommitted working-tree diff

// ...inside the component, filtered.map(...):
<li key={entry.entityId} class={`argus-list-row${checked ? ' argus-list-row--tracked' : ''}`}>
  <label style={{ display: 'contents' }}>
    <Checkbox
      checked={checked}
      ariaLabel={entry.entityId}
      onChange={(next) => onToggleMember(entry.entityId, next)}
    />
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
```tsx
// Wrap the results list (replaces the bare <ul class="argus-list">):
<Card padding="none">
  <ul class="argus-list">{filtered.map(renderRow)}</ul>
</Card>
```
**Why `<label style="display:contents">` can stay here (unlike Phase 12's SensorListRow):**
`MemberPicker` rows have no row-level click-to-select feature competing with the checkbox — the
whole row toggling the checkbox via native `<label>` semantics is exactly the desired behavior
(click anywhere on the row toggles membership). Do not "fix" this to match Phase 12's
`stopPropagation` pattern; that pattern solved a different problem (row-select vs. checkbox-
toggle conflict) that does not exist in this component.

### Pattern 4: Wizard restyle around the unchanged `groupEditor.ts` state machine (D-03/D-04)
**What:** `AlgorithmChooser.tsx`'s existing radiogroup-of-`AlgorithmCard` block (already using
the Phase-12-widened plain-string props) needs no prop-shape changes — it is already exactly the
shape D-04 asks for. The restyle work here is: (1) `GuidedFlowStep.tsx`'s two plain
`<button class="argus-btn">` answer buttons → DS `Card padding="sm"` wrapper +
`Button variant="secondary"` for each answer (keep copy verbatim, Copywriting Contract), and (2)
wrapping the whole chooser section in a `SectionLabel`-style heading, consistent with the
Attribution panel's `SectionLabel` treatment (Pattern 5).
**Example:**
```tsx
// Source: orchestrator/ui/src/components/GuidedFlowStep.tsx (current) +
// Argus Design System/ui_kits/admin/Groups.jsx lines 100-107 (layout reference only)
import { Card } from './Card';
import { Button } from './Button';

export function GuidedFlowStep() {
  return (
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
  );
}
```
**Do NOT touch:** `AlgorithmChooser.tsx`'s `useEffect` sync logic (lines 41-61), its
`AlgorithmCard` grid block (lines 75-88, already correct), `SensitivityPresetPicker.tsx`
(Phase-10-verified in place, `isCustomized`/Med-default/accent-radio logic untouched),
`AdvancedParamsDisclosure.tsx`'s `updateParam`/`draftParams` wiring (only its raw `<input>` per
field needs the same external-`Input`-wrapping treatment as Pattern 2, driven by `field.type`/
`min`/`max`/`step` from `DetectorCatalogEntry.paramSchema` — this mirrors Phase 12's
`DetectorParamGrid` swap exactly).

### Pattern 5: Attribution panel — Card + SectionLabel + EmptyState wrap (D-05)
**What:** `AttributionPanel.tsx`'s poll/state logic (lines 19-48) is completely unchanged. Only
its render branches (lines 50-80) get wrapped: the loading/no-score states get simple `Card`
wraps or stay as plain text (Claude's discretion); the `contributions.length === 0` "unsupported"
branch switches from a raw `<p class="argus-body argus-attribution-panel__unsupported">` to the
shared `EmptyState`-style presentation (note: the existing `EmptyState` component takes a
`{query: string}` prop shaped for the sensor-search use case — it is **not** a generic
title/description component; either add an overload/variant or render the unsupported message
inside a `Card` with matching `.argus-empty` markup rather than force-fitting the sensor-specific
`EmptyState`. Flagged as an open decision below, not a blocker).
**Example:**
```tsx
// Source: orchestrator/ui/src/components/AttributionPanel.tsx (current, poll logic unchanged)
import { Card } from './Card';

// existing early returns become Card-wrapped:
if (!loaded) {
  return <Card padding="sm"><p class="argus-label">Loading attribution…</p></Card>;
}
if (!status) {
  return (
    <Card padding="sm">
      <p class="argus-body">No anomaly score yet — attribution will appear after the next batch run.</p>
    </Card>
  );
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
// ranked list branch: wrap the existing .map(...) in <Card padding="sm">...</Card>
```
**AttributionBar.tsx restyle:** the current implementation (`memberId`, `contribution`,
`topContribution`, `topRank` props) already implements exactly the D-05 accent-vs-neutral fill
contract (`argus-attribution-bar__fill--top` uses `--color-accent`, confirmed present in
`argus.css` lines 899-909) — **no prop or logic changes needed**, this file is already DS-
compliant from Phase 8-04's original build. Restyle work here, if any, is CSS-only (verify token
usage against `HANDOFF_TO_CLAUDE_CODE.md`'s visual spec) — do not rename `memberId`/`topRank`
props to the kit's `label`/`top` names; that would require touching `AttributionPanel.tsx`'s call
site for no visual benefit.
**SectionLabel:** no shared `SectionLabel` Preact component exists yet — `.argus-section-label`
is a CSS class (already added in Phase 11, confirmed in `argus.css` lines 1268-1275) applied via
a raw `<p class="argus-section-label">` or `<div class="argus-section-label">` element directly
in `DashboardPage.tsx`/`SettingsPage.tsx` (no wrapper component). Follow the same raw-class
convention here: `<p class="argus-section-label">Member attribution · last result, refreshes
~60s</p>` — do not invent a new `SectionLabel.tsx` component for this phase; that would be scope
creep beyond what Phase 11 established.

### Anti-Patterns to Avoid
- **Adding an `onClick` navigate to the whole `GroupListRow` `<li>` without stopPropagation on
  the Delete button:** would cause a Delete click to also navigate to the edit screen. If the
  planner chooses whole-row click-to-edit (Claude's discretion, see Pattern 1), copy Phase 12's
  `stopPropagation` wrapper pattern exactly.
- **Force-fitting the sensor-specific `EmptyState` component onto the attribution-unsupported
  state:** `EmptyState.tsx`'s prop is `{query: string}` and its copy is sensor-search-specific
  ("No sensors match…", "Argus has not yet received a sensor snapshot…") — passing an unrelated
  `query` string to fake the unsupported-attribution message would produce nonsensical copy.
  Render the unsupported state's own markup instead (Pattern 5).
- **Reverting the uncommitted `MemberPicker.tsx` MIN_QUERY_LENGTH diff** while refactoring markup
  — this is locked-in target behavior (D-07), not leftover work to discard.
- **Filtering the algorithm catalog by `draftMode` or gating the guided step to `mode==='joint'`**
  — explicitly rejected by D-03; the kit's `Groups.jsx` does this, but it is a behavioral change
  out of scope for this phase.
- **Renaming `AttributionBar`'s `memberId`/`topRank` props** to match the kit's `label`/`top` —
  no functional benefit, unnecessary call-site churn in `AttributionPanel.tsx`.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Radio-card 2px-accent detector selection | New card component | Already-widened `AlgorithmCard` (Phase 12) | D-04 mandate; reusing avoids re-deriving A11Y-02 |
| Sensitivity preset radio group | New preset picker | `SensitivityPresetPicker` (Phase 10, verified DS-compliant) | D-07 explicit preservation |
| Two-step destructive confirm | New confirm-dialog/modal | `GroupListRow`'s existing armed/timer pattern | Already matches the Copywriting Contract (no `window.confirm`) |
| Field-level validation messages | New validation logic in components | `validateGroupMembers`/`validateUnitConsistency` from `groupParams.ts` (unchanged) | D-07 explicit preservation; parity spec with `GroupInputValidator.cs` |
| Debounced sensor search | New debounce logic | `SensorSearchInput` (wraps shared `SearchInput`, already tested) — reuse for member picker's query input if not already doing so | Already correct, already tested |

**Key insight:** Exactly like Phase 12, every "hard part" of this screen (member-floor/unit
validation, the chooser state machine, the attribution poll, the two-step delete) is already
solved and (mostly) tested in the current codebase. The phase is a pure presentation/markup
refactor around unchanged business logic — the only genuinely new work is test coverage for
components that currently have none.

## Runtime State Inventory

Not applicable — this is not a rename/refactor/migration-of-identifiers phase. No stored data,
service config, OS-registered state, secrets, or build artifacts carry any string being renamed.

## Common Pitfalls

### Pitfall 1: Reintroducing Phase 12's checkbox/label bug in a component that doesn't have it
**What goes wrong:** A planner familiar with Phase 12's `SensorListRow` fix might reflexively
apply the `stopPropagation`-wrapped-`Checkbox` pattern to `MemberPicker.tsx`'s rows, even though
`MemberPicker` has no row-level `onClick`/select feature to conflict with.
**Why it happens:** Pattern-matching Phase 12 too literally instead of checking whether the same
root cause (a new ancestor `onClick` competing with native `<label>` semantics) is actually
present.
**How to avoid:** Only apply the stopPropagation pattern where a row-level `onClick` competes
with a checkbox. `MemberPicker`'s rows have no such competing click handler — keep the `<label
style="display:contents">` wrapper as-is (Pattern 3).
**Warning signs:** Adding an unused `onClick={(e) => e.stopPropagation()}` wrapper with no
sibling `onClick` on the row.

### Pitfall 2: `aria-describedby` already points at a DOM id that doesn't exist (pre-existing, not new)
**What goes wrong:** It would be reasonable to assume `Input`'s `ariaDescribedby` prop links to a
real `id` rendered by `FieldValidationError`. It does not, and this is **already true today** in
the as-shipped, verified Phase 12 code — confirmed by reading both files together this session:
`DetectorParamGrid.tsx` passes `ariaDescribedby={`${inputId}-err`}` (line 79) into `Input`, but
`FieldValidationError.tsx`'s `<span>` (line 9 of that file) has **no `id` attribute at all** —
the `-err`-suffixed id is never rendered anywhere in the DOM. This is a pre-existing, already-
shipped a11y gap, not something Phase 13 would introduce.
**Why it happens:** The convention was established assuming `FieldValidationError` would render
a matching `id`, but that prop was never added to the component.
**How to avoid:** Two valid choices for this phase: (a) follow the existing (imperfect)
convention consistently for new fields (name field, `AdvancedParamsDisclosure` params) — matches
current codebase behavior, zero new risk, but doesn't fix the gap; or (b) fix it properly by
adding an optional `id` prop to `FieldValidationError.tsx` (small additive change, same class as
Phase 12's `Input.tsx` extension) and updating both `DetectorParamGrid.tsx` and this phase's new
usages to pass it. Option (b) is a genuine, in-scope a11y improvement given the project's A11Y-01/
02 emphasis, but is **not required** to satisfy GRP-12/13/14 — flag for planner as an optional
task, not a blocking dependency.
**Warning signs:** None visible without an axe/screen-reader audit — this is a silent gap, which
is exactly why Phase 12 didn't catch it either.

### Pitfall 3: Assuming `AlgorithmCard`/`Input` still need the Phase-12 widening work
**What goes wrong:** Re-reading Phase 12's RESEARCH.md/PATTERNS.md (a reasonable step, since this
phase mirrors it) could lead the planner to re-plan the `AlgorithmCard` prop-widening or
`Input.tsx` passthrough-prop work as if it hadn't happened yet.
**Why it happens:** Phase 12's RESEARCH.md describes those changes as *upcoming*; by the time
Phase 13 is planned, they are already shipped and verified (`12-VERIFICATION.md` confirms both).
**How to avoid:** This research confirms (by reading the actual current file contents this
session) that `AlgorithmCard.tsx` and `Input.tsx` are already in their final Phase-12 form. Do
not add tasks to "widen" or "extend" them again — only compose/restyle their callers.
**Warning signs:** A plan task titled anything like "widen AlgorithmCard props" or "add step/id
to Input" — both are already done.

### Pitfall 4: Force-fitting `EmptyState` onto the attribution-unsupported state
**What goes wrong:** D-05 says "render the unsupported state via the shared `EmptyState`
component" — but `EmptyState.tsx`'s actual prop is `{query: string}` with hardcoded sensor-search
copy branches. Passing some string as `query` to reuse this component verbatim would render
wrong/confusing text ("No sensors match ...", "Argus has not yet received a sensor snapshot...").
**Why it happens:** D-06's decision doc assumed `EmptyState` is more generic than it actually is
(same category of CONTEXT.md/code mismatch Phase 12 found twice already — this is now a
recurring pattern across phases 12/13, worth flagging explicitly to future planners).
**How to avoid:** Either (a) render the unsupported-attribution copy in raw `.argus-empty` markup
directly (matches the visual language `EmptyState` uses, no prop mismatch), or (b) if visual
parity with `EmptyState`'s exact spacing/typography matters, extract a small shared inner
component both can use — but do not pass a fake `query` string into the existing component.
**Warning signs:** `<EmptyState query="attribution not supported" />` or similar prop misuse.

### Pitfall 5: Treating the DS reference's mode-filtered catalog as a hint to also filter
**What goes wrong:** `Groups.jsx`'s `GroupEditor` filters `catalog` by `mode` (peer → only
`peer_divergence`; joint → the rest) and gates the guided step to `mode === 'joint'`. A planner
skimming the kit file for "what fields does the grid need" could accidentally copy this
filtering logic into `AlgorithmChooser.tsx`.
**Why it happens:** The kit file is the primary visual reference, and its filtering logic reads
as intentional design rather than a mockup simplification.
**How to avoid:** D-03 explicitly locks current unfiltered behavior in. Read `AlgorithmChooser.tsx`
top-to-bottom (already done this session — `cat.detectors.map(...)`, no mode-based `.filter()`
anywhere) as the source of truth for what to preserve.
**Warning signs:** Any new `.filter((d) => ...)` call on `cat.detectors` keyed off `draftMode`.

## Code Examples

### Card-wrapped list (established Phase 12 pattern — reuse verbatim)
```tsx
// Source: orchestrator/ui/src/components/SensorList.tsx:73-78 (as-shipped, Phase 12)
<Card padding="none">
  <ul class="argus-list">{entries.map(renderRow)}</ul>
</Card>
```

### Page header (established Phase 11/12 pattern — reuse for GroupsPage/GroupEditorForm)
```tsx
// Source: orchestrator/ui/src/components/SensorsPage.tsx:44-49 (as-shipped, Phase 12)
<header class="argus-page-header">
  <h1 class="argus-page-header__title">Groups</h1>
  <p class="argus-page-header__subtitle">
    Detect anomalies across related sensors — divergence within a group, or jointly-abnormal
    combinations.
  </p>
</header>
```
Apply to `GroupsPage.tsx`'s list view (replacing the current raw `.argus-heading`/`.argus-body`
pair at lines 29-35) and to `GroupEditorForm.tsx`'s "Edit group"/"Create group" heading (replacing
the raw `<p class="argus-heading">` at line 72) with the DS reference's "← Back to groups"
affordance (D-01: visual only, does not change the router — a plain `<a href="#/groups">` or
`Button variant="ghost"` with `onClick={() => (location.hash = '#/groups')}`).

### Section label (established Phase 11 pattern — no wrapper component, raw class)
```tsx
// Source: orchestrator/ui/src/components/DashboardPage.tsx (grep confirms this raw-class usage
// pattern, no SectionLabel.tsx component exists in the repo)
<p class="argus-section-label">Member attribution · last result, refreshes ~60s</p>
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|---------------|--------|
| Raw `<select>` (mode) | Shared `Select` component | This phase (D-06) | Same visual, shared primitive |
| Raw `<input type="checkbox">` + `<label style="display:contents">` (member picker) | Shared `Checkbox` (label wrapper kept, no stopPropagation needed) | This phase (D-06) | Same interaction, shared primitive |
| `argus-pill--tracked`/`argus-pill--member` raw spans | `<Badge tone="tracked"/"member"/"accent">` | This phase (D-06) | Same visual, shared component |
| Raw `<input>` (name field, advanced params) | Shared `Input` (external label/error, matches Phase 12 convention) | This phase (D-06) | Same visual, consistent a11y wiring |
| Two flat `<button class="argus-btn">` guided-answer buttons | `Card` + `Button variant="secondary"` wizard step | This phase (D-04) | DS-consistent wizard card styling |
| `AlgorithmCard` widened to plain-string props | (unchanged — already shipped Phase 12) | Phase 12 | Reused as-is, no further change needed |

**Deprecated/outdated:** none — no third-party API or library version changes involved.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The DS reference's detector `Badge` on the list row uses `tone="accent"` (matching `Groups.jsx`'s `<Badge tone="accent">{g.detector}</Badge>`) rather than `tone="neutral"` for a second badge | Pattern 1 | Low — purely a Badge tone choice, trivially adjustable in review; `argus-pill--accent` CSS class confirmed to exist |
| A2 | Fixing `FieldValidationError.tsx`'s pre-existing missing-`id` gap (confirmed present in already-shipped `DetectorParamGrid.tsx` too — not new to this phase) is optional polish, not required to satisfy GRP-12/13/14 | Pattern 2, Pitfall 2 | Low — the gap already exists in verified Phase 12 code; leaving it as-is for Phase 13's new fields is consistent with current behavior, not a regression |
| A3 | The attribution-unsupported state should use custom `.argus-empty` markup rather than a new shared generic-EmptyState variant | Pattern 5, Pitfall 4 | Low — either approach is visually similar; choosing custom markup avoids touching the shared `EmptyState.tsx` component's contract for one caller |
| A4 | Whole-row click-to-edit on `GroupListRow` (vs. keeping the existing `<a href>` "Edit" link plus separate Delete button) is left to planner/Claude discretion, not mandated by D-02 | Pattern 1 | Low — D-02 requires Card+2-Badge visual fidelity, not click-target changes; either choice satisfies the decision text |

**If this table is empty:** N/A — see above; A2 is the one item worth flagging as a genuine
open a11y-wiring decision for the planner; the rest are low-risk styling choices.

## Open Questions

1. **Should the member picker gain a `max-height`/scroll-region CSS rule to match the DS
   reference's `Card` `maxHeight: 220, overflow: 'auto'`?**
   - What we know: `.argus-member-picker` has no such rule today; `Card.tsx` itself has no
     `maxHeight`/scroll prop (its `padding` prop is also a no-op per the component's own comment —
     confirmed by reading `Card.tsx` this session).
   - What's unclear: exact pixel value / whether this is worth a new CSS rule vs. leaving the
     picker's height unconstrained (current behavior — the picker already gates on
     `MIN_QUERY_LENGTH` so result sets are usually small).
   - Recommendation: low-priority styling detail, Claude's discretion per CONTEXT.md; if added,
     a small new `.argus-member-picker` (or a `Card`-scoped modifier) CSS rule, analogous to
     Phase 12's `.argus-list-row--selected` addition.

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
| GRP-12 | Group list renders in Card, two Badges (mode + detector), status/delete meta preserved | unit (render) | `npm run test -- --run src/components/GroupList.test.tsx` | ❌ Wave 0 (no test exists for GroupList today) |
| GRP-12 | Two-step delete confirm still arms/fires/reverts correctly after row restyle | unit (render + fireEvent + fake timers) | `npm run test -- --run src/components/GroupListRow.test.tsx` | ❌ Wave 0 |
| GRP-12 | Name field renders via shared `Input`, error via `FieldValidationError`; mode via shared `Select` | unit (render) | `npm run test -- --run src/components/GroupEditorForm.test.tsx` | ❌ Wave 0 |
| GRP-12 | Member picker: MIN_QUERY_LENGTH=2 gate, Card/Checkbox/Badge/SearchInput rows, toggle wiring | unit (render + fireEvent) | `npm run test -- --run src/components/MemberPicker.test.tsx` | ❌ Wave 0 |
| GRP-12 | `validateGroupMembers`/`validateUnitConsistency` — regression guard, unchanged | unit | `npm run test -- --run src/validation/groupParams.test.ts` | ✅ exists, no changes needed (D-07) |
| GRP-12 | `state/groups.ts` save/delete/draft layer — regression guard, unchanged | unit | `npm run test -- --run src/state/groups.test.ts` | ✅ exists, no changes needed (D-07) |
| GRP-13 | `GuidedFlowStep` restyled to Card+Button, copy verbatim (2 answers + skip) | unit (render) | `npm run test -- --run src/components/GuidedFlowStep.test.tsx` | ❌ Wave 0 |
| GRP-13 | `AlgorithmChooser` still renders full unfiltered catalog + AlgorithmCard grid after restyle (D-03 regression guard) | unit (render) | `npm run test -- --run src/components/AlgorithmChooser.test.tsx` | ✅ exists, verify still passes / extend for restyle |
| GRP-13 | `SensitivityPresetPicker` Med-default/isCustomized logic — regression guard, unchanged | unit | `npm run test -- --run src/components/SensitivityPresetPicker.test.tsx` | ❌ Wave 0 (no test exists today, despite D-07's "verified DS-compliant" note — that was a Phase 10 code-reading claim, not a test file) |
| GRP-13 | `AdvancedParamsDisclosure` fields render via shared `Input`, `draftParams` wiring unchanged | unit (render) | `npm run test -- --run src/components/AdvancedParamsDisclosure.test.tsx` | ❌ Wave 0 |
| GRP-13 | `state/groupEditor.ts` chooser state machine — regression guard, unchanged | unit | `npm run test -- --run src/state/groupEditor.test.ts` | ✅ exists, no changes needed (D-07) |
| GRP-14 | `AttributionPanel` 4-state rendering (loading/no-score/unsupported/ranked) preserved after Card/EmptyState wrap | unit (render + fake timers) | `npm run test -- --run src/components/AttributionPanel.test.tsx` | ✅ exists, verify still passes / extend for restyle wrapper |
| GRP-14 | `AttributionBar` accent-vs-neutral fill on top-ranked vs. others — regression guard | unit (render) | `npm run test -- --run src/components/AttributionBar.test.tsx` | ❌ Wave 0 |

### Sampling Rate
- **Per task commit:** `npm run test -- --run <touched-test-file(s)>`
- **Per wave merge:** `npm run test -- --run` (full suite) + `npm run build` (tsc -b && vite build)
- **Phase gate:** Full suite green + `npm run build` green before `/gsd-verify-work`

### Wave 0 Gaps
- [ ] `src/components/GroupList.test.tsx` — new file; Card wrap, two-Badge row, empty-state branch
- [ ] `src/components/GroupListRow.test.tsx` — new file; two-step delete arm/confirm/timeout,
      status Badge/"no status yet" fallback, detector Badge presence
- [ ] `src/components/GroupEditorForm.test.tsx` — new file; name/mode field restyle, error wiring,
      member/algorithm/attribution slot composition unchanged
- [ ] `src/components/MemberPicker.test.tsx` — new file; `MIN_QUERY_LENGTH` gate (both branches),
      Checkbox/Badge/SearchInput rendering, toggle callback wiring
- [ ] `src/components/GuidedFlowStep.test.tsx` — new file; verbatim copy assertion (2 answers +
      skip link), Card/Button restyle doesn't change callback wiring
- [ ] `src/components/SensitivityPresetPicker.test.tsx` — new file (genuinely missing despite
      Phase 10's "verified in place" note — that was a code-review claim, not test coverage);
      Med default, `isCustomized` indicator, accent-radio class assertion
- [ ] `src/components/AdvancedParamsDisclosure.test.tsx` — new file; field render via `Input`,
      `updateParam`/`draftParams` wiring
- [ ] `src/components/AttributionBar.test.tsx` — new file; accent fill on `topRank=true`, neutral
      otherwise, width-percent calculation
- [ ] Extend `src/components/AlgorithmChooser.test.tsx` — existing file; add assertions that the
      catalog remains unfiltered across both `draftMode` values (D-03 regression guard)
- [ ] Extend `src/components/AttributionPanel.test.tsx` — existing file; verify the 4 states still
      render correctly through the new Card/EmptyState-style wrapping
- [x] `src/state/groups.test.ts`, `src/state/groupEditor.test.ts`,
      `src/validation/groupParams.test.ts` — all three confirmed to already exist (`ls` run this
      session); no changes needed, run as regression guards only (D-07)

## Security Domain

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Unchanged — `IsAuthorizedRequest` gate on `/api/groups*` untouched by this phase |
| V3 Session Management | no | No session changes |
| V4 Access Control | no | No access-control changes |
| V5 Input Validation | yes | Client-side `groupParams.ts` (preserved verbatim, D-07) is a UX convenience only; the authoritative boundary remains server-side `GroupInputValidator.cs` (untouched, out of scope) — this phase must not weaken or bypass the client validation gating `hasErrors`/`SaveBar`'s disabled state |
| V6 Cryptography | no | No crypto surface in this phase |

### Known Threat Patterns for this stack
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Client-only validation trusted as sole gate | Tampering | Not a new risk — server-side `GroupInputValidator.cs` re-validates every `/api/groups/save` POST independently; this phase changes only client validation-UI plumbing (`<Input invalid>` instead of raw `<input>`), not the trust boundary |
| Reflected/stored XSS via group `friendlyName`/member `entityId` in rendered rows | Tampering/Info Disclosure | Preact's JSX auto-escapes text content by default — unchanged by this refactor; do not introduce `dangerouslySetInnerHTML` anywhere in the rebuilt components |

## Sources

### Primary (HIGH confidence — read directly from this repo)
- `orchestrator/ui/src/components/GroupsPage.tsx`, `GroupList.tsx`, `GroupListRow.tsx`,
  `GroupEditorForm.tsx`, `MemberPicker.tsx`, `AlgorithmChooser.tsx`, `GuidedFlowStep.tsx`,
  `SensitivityPresetPicker.tsx`, `AdvancedParamsDisclosure.tsx`, `AttributionPanel.tsx`,
  `AttributionBar.tsx`, `AreaSuggestionBanner.tsx`, `GroupSaveResultBanner.tsx`, `SaveBar.tsx`,
  `FieldValidationError.tsx` — current implementation, structure, props (all read in full)
- `orchestrator/ui/src/components/{Card,Badge,Select,SearchInput,Checkbox,Input,Disclosure,
  EmptyState,Button,AlgorithmCard,Banner}.tsx` — Phase 10/12 shared component actual, current
  signatures (all read in full this session — confirms `AlgorithmCard`/`Input` are already in
  Phase-12-final form, no further widening needed)
- `orchestrator/ui/src/state/groups.ts`, `orchestrator/ui/src/state/groupEditor.ts`,
  `orchestrator/ui/src/validation/groupParams.ts`, `orchestrator/ui/src/api/types.ts` — state/
  validation/data contracts, read in full
- `git diff -- orchestrator/ui/src/components/MemberPicker.tsx` — confirms the exact uncommitted
  `MIN_QUERY_LENGTH = 2` diff content (used to settle the MemberPicker reconciliation question)
- `orchestrator/ui/src/components/SensorList.tsx`, `SensorListRow.tsx`, `SensorsPage.tsx`,
  `DashboardPage.tsx` — Phase 12/11 as-shipped precedents for Card-wrapped lists, page headers,
  section labels, `stopPropagation` checkbox pattern
- `Argus Design System/ui_kits/admin/Groups.jsx`, `shared.jsx` — layout/interaction reference
  (PageHeader/Back affordance, Card row/Badge visual fidelity, member-picker scroll-region,
  wizard Card layout, attribution SectionLabel copy) — **not authoritative for behavior** per
  D-01/D-03/D-05 conflict flags, confirmed by direct comparison against current component logic
- `orchestrator/ui/public/css/argus.css` (grepped for `.argus-page-header`, `.argus-section-
  label`, `.argus-list-row--selected`, `.argus-attribution-*`, `.argus-sensitivity-preset-*`,
  `.argus-guided-flow-step*`, `.argus-algorithm-chooser*`, `.argus-pill--member`, `.argus-pill--
  accent`, `.argus-checkbox`) — confirms all needed tokens/classes already exist; no new CSS
  classes required beyond possibly a member-picker scroll-region rule (Open Question 2)
- `orchestrator/ui/src/components/*.test.tsx` directory listing — confirms zero existing test
  files for any Groups-family component except `AlgorithmChooser.test.tsx`, `AlgorithmCard.test.tsx`,
  `AttributionPanel.test.tsx` (none of the three need modification for prop/behavior changes)
- `.planning/phases/12-sensors-screen-rebuild/12-RESEARCH.md`, `12-CONTEXT.md`, `12-PATTERNS.md`,
  `12-VERIFICATION.md` — direct template for this phase's structure/depth; `12-VERIFICATION.md`'s
  "Gaps Summary" note independently confirms the uncommitted `MemberPicker.tsx` diff is a known,
  intentionally-deferred Phase 13 concern (not orphaned work)
- `.planning/phases/13-groups-screen-rebuild/13-CONTEXT.md`, `.planning/REQUIREMENTS.md`,
  `.planning/STATE.md` — locked decisions, requirements, project history
- `orchestrator/ui/vitest.config.ts`, `orchestrator/ui/package.json` — test/build tooling
  versions confirmed unchanged since Phase 12

### Secondary (MEDIUM confidence)
None used — all findings were verified directly against the checked-out repo; no external
web/docs lookups were needed for this phase (no new libraries, no framework version questions).

### Tertiary (LOW confidence)
None.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new dependencies, all versions read directly from `package.json`
  (identical to Phase 12's confirmed versions)
- Architecture: HIGH — every touched file and every shared component was read in full this
  session; the MemberPicker uncommitted-diff question was settled directly via `git diff`, not
  inferred; the `AlgorithmCard`/`Input` "already Phase-12-final" finding was confirmed by reading
  current file contents, not assumed from CONTEXT.md's description
- Pitfalls: HIGH — derived from direct code reading (FieldValidationError's actual lack of an
  `id` prop, EmptyState's actual sensor-specific prop shape, AlgorithmChooser's actual absence of
  mode-filtering) rather than speculation; one genuine open item remains (Open Question 1 /
  Assumption A2 — whether `FieldValidationError` needs a new `id` prop) and is flagged as such
  rather than resolved by assumption

**Research date:** 2026-07-17
**Valid until:** No external dependency — valid until the next change to `AlgorithmCard.tsx`,
`Input.tsx`, `FieldValidationError.tsx`, `EmptyState.tsx`, or the three preserved state/validation
modules (internal repo drift only, not time-based staleness)
