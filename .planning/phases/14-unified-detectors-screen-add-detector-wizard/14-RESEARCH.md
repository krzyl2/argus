# Phase 14: Unified Detectors Screen + Add-Detector Wizard - Research

**Researched:** 2026-07-21
**Domain:** Preact SPA IA restructure (frontend-only), reusing v4.1 Design System components
**Confidence:** HIGH (all claims verified by reading the actual source files listed below; no external
library research was needed — this phase is 100% internal reuse/refactor)

## Summary

Phase 14 replaces two nav destinations (Sensors, Groups) with one **Detectors** list screen and one
shared **Add-detector wizard**. Every data source the phase needs already exists (`GET /api/groups`,
`GET /api/sensors`) and every editing surface it needs already exists (`GroupEditorForm` for groups;
`SensorsPage`'s inline detector-assignment block for single sensors). This is a client-only
restructuring/extraction phase — **no backend changes are required** if one scope point from the
ROADMAP text is descoped (see below).

The one substantive discrepancy: the ROADMAP text says the wizard's two exits "both continue through
the full guided flow (algorithm + sensitivity/params), reusing `GuidedFlowStep` / `AlgorithmChooser` /
`SensitivityPresetPicker`." Those three components are hard-wired to the **group** detector catalog
(`peer_divergence/ecod/copod/pca/iforest`, `GET /api/detectors/catalog`) — there is no equivalent
catalog, guided question, or Low/Med/High preset system for single-sensor detectors (`hst/mad/stl`).
This was explicitly investigated and deferred twice already: `12-CONTEXT.md` ("Backend single-sensor
detector catalog endpoint... Own phase if a real catalog is wanted" / "Sensitivity presets for
single-sensor detectors... Own phase") and `11-CONTEXT.md` ("deferred single-sensor catalog
endpoint"). Building a real single-sensor catalog+guided-flow is new backend+frontend scope, not
reuse — see **Open Question 1**.

**Primary recommendation:** Build the wizard as a thin **hand-off** component, not a monolithic
multi-step form. It owns only sensor search + 1-vs-≥2 branching (reusing `MemberPicker` with a new
`minQueryLength` prop set to 3). On ≥2 selections it sets `pendingPrefillMembers` and navigates to the
existing `#/groups/new` route, letting the untouched `GroupEditorForm` → `AlgorithmChooser` →
`GuidedFlowStep` → `SensitivityPresetPicker` chain run exactly as it does today (true reuse, zero
modification). On exactly 1 selection it tracks the sensor and navigates to a **new**
`/detectors/sensor/:entityId` route backed by a **new** `SingleDetectorEditorForm` extracted verbatim
from `SensorsPage`'s existing `DetectorDisclosure`/`DetectorEntry`/`DetectorParamGrid` stack (which
already reuses `AlgorithmCard` for hst/mad/stl selection — no guided question, no presets, matching
the deferred-scope reality). This satisfies the ROADMAP's user-visible intent ("continue through the
guided flow") via navigation hand-off rather than literal component embedding, while requiring zero
backend changes and touching a minimal set of files.

<phase_requirements>
## Phase Requirements

No REQ-IDs were pre-assigned (ROADMAP marks Phase 14 requirements as "TBD (derive during planning)").
This research surfaces the concrete capabilities the phase must deliver; the planner should mint
REQ-IDs from the candidate list below (`## Derived Requirements`) and add them to REQUIREMENTS.md.
</phase_requirements>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Unified list merge (groups + tracked sensors) | Browser/Client | API/Backend (existing `GET /api/groups`, `GET /api/sensors`) | Pure client-side merge of two already-existing JSON responses; no new query needed |
| Add-detector wizard step/branch logic | Browser/Client | — | New signal-based state machine, mirrors `state/groupEditor.ts` pattern |
| Sidebar nav restructure | Browser/Client | — | `Sidebar.tsx` + `router.ts` only |
| Group creation/edit persistence | API/Backend | Database/Storage (`entities.yaml`) | Unchanged — full-list-replace `POST /api/groups/save` |
| Single-sensor detector persistence | API/Backend | Database/Storage (`entities.yaml`) | Unchanged — full-list-replace `POST /api/sensors/save`; **client must load the full sensor set before saving** (see Pitfall 1) |
| Sensor search (wizard, ≥3 chars) | Browser/Client | API/Backend (`GET /api/sensors?q=`, already supports server search) | Recommend client-side filter over an already-loaded list (matches `MemberPicker`'s existing pattern) — no new server call required |
| Pattern Filters (include/exclude auto-track) | Browser/Client | API/Backend (bundled into `POST /api/sensors/save`) | Currently lives inside `SensorsPage`; needs a new home once Sensors nav item is removed (Open Question 2) |

## Package Legitimacy Audit

Not applicable — this phase installs no new npm/pip/cargo packages. All work reuses existing
dependencies already in `orchestrator/ui/package.json` (`preact`, `@preact/signals`) and existing
C# projects. No `package-legitimacy` check was run because there is nothing to check.

## Components/Files: Reuse vs. Create vs. Modify

### Reuse unchanged (zero modification)

| File | Why it needs no change |
|------|------------------------|
| `orchestrator/ui/src/components/GroupEditorForm.tsx` | Mounted unchanged at `/groups/new` / `/groups/:id`; wizard's ≥2 exit hands off here via `pendingPrefillMembers` |
| `orchestrator/ui/src/components/AlgorithmChooser.tsx`, `GuidedFlowStep.tsx`, `SensitivityPresetPicker.tsx`, `AdvancedParamsDisclosure.tsx` | Group-detector-only; reused via the `GroupEditorForm` hand-off, not embedded in the wizard directly |
| `orchestrator/ui/src/components/AttributionPanel.tsx` | Group-only; unaffected |
| `orchestrator/ui/src/state/groups.ts`, `state/groupEditor.ts` | Unchanged — `saveGroup`, `pendingPrefillMembers`, `draftMembers` etc. all reused as-is |
| `Web/DetectorCatalog.cs`, `Web/DetectorDefaults.cs`, `Web/GroupSaveRequest.cs`, `Web/SaveRequest.cs`, `Web/GroupInputValidator.cs` | No backend contract changes needed |
| `Program.cs` `GET /api/groups`, `GET /api/sensors`, `POST /api/groups/save`, `POST /api/sensors/save`, `GET /api/detectors/catalog`, `GET /api/detectors/defaults` | All existing endpoints are sufficient — see `## Backend Change Decision` |
| `orchestrator/ui/src/components/AlgorithmCard.tsx` | Already prop-agnostic (`name: string`) per Phase 12-01 — reused as-is by both the group grid and the single-sensor grid |
| `orchestrator/ui/src/components/Card.tsx`, `Badge.tsx`, `Checkbox.tsx`, `Button.tsx`, `SearchInput.tsx`/`SensorSearchInput.tsx`, `EmptyState.tsx` | Generic DS primitives, no change needed |
| `orchestrator/ui/src/validation/detectorParams.ts`, `validation/groupParams.ts` | Validation rules unchanged |
| `orchestrator/ui/src/components/sensorMatch.ts` | `matchesSensorQuery` reused verbatim by the wizard's client-side filter |

### Reuse with a small generalization

| File | Change needed | Why |
|------|---------------|-----|
| `orchestrator/ui/src/components/MemberPicker.tsx` | Add optional `minQueryLength?: number` prop (default `2`, preserving Groups' current behavior) | The wizard needs the identical multi-select-with-reveal-threshold UI, but at `>=3` chars per ROADMAP, not `>=2`. Everything else (`matchesSensorQuery`, checkbox rows, `Badge tone="member"`) is directly reusable. |
| `orchestrator/ui/src/components/Sidebar.tsx` | Replace the `sensors`/`groups` `NAV_ITEMS` entries with `detectors` (`#/detectors`) + `add-detector` (`#/detectors/add`); update `isActive()` | Structural nav change explicitly requested by ROADMAP |
| `orchestrator/ui/src/router.ts` | Change default route fallback (`normalizeHash`'s `'/sensors'` literal and the boot `effect`'s `'#/sensors'`) to `/detectors`; add a redirect for bare `/sensors` and bare `/groups` (see Router Decision) | New default route + old-route back-compat |
| `orchestrator/ui/src/main.tsx` | Add render branches for `/detectors`, `/detectors/add`, `/detectors/sensor/:entityId`; remove the implicit "everything else falls through to `SensorsPage`" else-branch, replacing it with the redirect target | Route table growth |

### Create new

| File | Purpose | Modeled on |
|------|---------|------------|
| `orchestrator/ui/src/components/DetectorsPage.tsx` | Top-level `/detectors` list screen: loads groups + sensors, merges into unified rows, renders `DetectorList` | `GroupsPage.tsx` (routing-orchestration role) |
| `orchestrator/ui/src/components/DetectorList.tsx` | Card-wrapped `<ul class="argus-list">` rendering one row per group or tracked sensor | `GroupList.tsx` / `SensorList.tsx` |
| `orchestrator/ui/src/components/DetectorListRow.tsx` (or two variants — see Row Model Decision) | One unified-look DS row per group/sensor | `GroupListRow.tsx` + `SensorListRow.tsx` |
| `orchestrator/ui/src/components/AddDetectorWizard.tsx` | `/detectors/add` route component: sensor multi-select (via `MemberPicker` with `minQueryLength=3`) + 1-vs-≥2 branch + hand-off navigation | New; thin — no algorithm-chooser UI of its own |
| `orchestrator/ui/src/components/SingleDetectorEditorForm.tsx` | `/detectors/sensor/:entityId` route component: single-sensor detector assignment, extracted from `SensorsPage`'s inline block | Extraction of `SensorsPage.tsx` lines ~50-66 + `DetectorDisclosure`/`DetectorEntry`/`DetectorParamGrid`/`AddDetectorButton` |
| `orchestrator/ui/src/state/detectors.ts` | Computed signal merging `groups` (from `state/groups.ts`) + tracked entries from `state/sensors.ts` into a unified, sorted row list | New; thin computed layer, no new fetch logic |

### Modify (surgical)

| File | Change | Why |
|------|--------|-----|
| `orchestrator/ui/src/components/SensorsPage.tsx` | Extract inline per-sensor detector-assignment JSX into `SingleDetectorEditorForm`; `SensorsPage` itself either becomes a thin redirect shim or is deleted once the bare `/sensors` route redirects (see Router Decision) | The full "browse all sensors + toggle tracked + assign detector + patterns + save" screen is being split: browsing/toggling moves conceptually into the wizard + Detectors list; per-sensor detector editing moves into the new dedicated view |
| `orchestrator/ui/src/state/sensors.ts` | No signal/logic changes required — `setTracked`, `addDetector`, `save()` etc. are reused as-is by both `SingleDetectorEditorForm` and `AddDetectorWizard` | Existing full-list-replace `save()` already does the right thing **provided the full sensor set is loaded first** (Pitfall 1) |
| Pattern Filters UI (`PatternFiltersPanel.tsx` + its two signals in `state/sensors.ts`) | Relocate the *rendering* of `PatternFiltersPanel` out of the removed browse-all screen to a surviving screen (recommend: `SettingsPage.tsx`) | See Open Question 2 — ROADMAP does not mention this feature; it will be silently lost if nobody re-homes it |

## Router Decision (reconciled)

Current state (`router.ts`): hand-rolled hash router, default route `/sensors`, `parseGroupId` regex
handles `/groups/:id` only.

**Recommended:**
1. Change `normalizeHash`'s fallback and the boot `effect`'s target from `/sensors` → `/detectors`.
2. Add a redirect: when the normalized path is exactly `/sensors` or exactly `/groups` (bare, no
   `/new` or `/:id` suffix), rewrite to `/detectors` inside `normalizeHash` (or a dedicated
   `redirectLegacyRoutes` helper called right after it) — this preserves old bookmarks/deep links
   without adding dead render branches in `main.tsx`.
3. **Do not touch** `/groups/new` or `/groups/:id` parsing (`parseGroupId`) — both keep working
   unchanged, since `GroupEditorForm` is reused via direct navigation from the wizard and from
   `DetectorListRow`'s "Edit" link on a group row.
4. Add two new route shapes: `/detectors/add` (no `:id` — same parser-free category as the three
   Phase 11 static routes) and `/detectors/sensor/:entityId` (needs a **new** parser analogous to
   `parseGroupId`, but note entity ids contain dots, e.g. `sensor.living_room_temp` — `encodeURIComponent`
   at link-creation time, `decodeURIComponent` at parse time, exactly like `GroupListRow`'s existing
   `encodeURIComponent(group.groupId)` pattern).
5. `main.tsx`'s route switch grows one `else if` per new route; the final `else` becomes
   `<DetectorsPage />` instead of `<SensorsPage />`.

## Unified Row Model Decision

`GroupListRow` and `SensorListRow` currently take structurally different props (`GroupConfig` vs.
`SensorEntry` + edit-state + 6 callback props) and render different meta content (mode/detector
badges + delete-with-confirm vs. tracked badge + inline detector disclosure). **Recommend two thin
row variants under one list**, not one collapsed component:

- `DetectorListRow` renders either a `GroupRow` sub-view or a `SensorRow` sub-view based on a
  discriminant (`kind: 'group' | 'sensor'`) on the merged row model from `state/detectors.ts`.
- Reuse `GroupListRow`'s existing delete-with-confirm pattern and `SensorListRow`'s tracked
  Badge/entity-id styling verbatim inside each variant — do not invent new markup, just relocate the
  existing JSX so both variants render inside one `<ul class="argus-list">` for visual consistency.
- Both variants' "Edit" affordance becomes an `<a href="#/groups/:id">` (unchanged) or
  `<a href="#/detectors/sensor/:entityId">` (new) — no inline expand-in-place editor on the unified
  list (that inline-expand UX from `SensorsPage`/`SensorListRow` is superseded by navigating to the
  new dedicated editor view, matching how `GroupListRow` already only ever *links* to its editor
  rather than expanding inline).

Row identity/keying: group ids (`GroupConfig.groupId`) and entity ids (`SensorEntry.entityId`, e.g.
`sensor.foo`) live in disjoint namespaces already (HA entity ids always contain a `.`; slugified group
ids from `GroupEditorForm`'s `slugify()` never do, since `.` is stripped by
`replace(/[^a-z0-9]+/g, '_')`) — no collision is possible today, but the merged list's `key=` prop
should still be namespaced (e.g. `` `group:${groupId}` `` / `` `sensor:${entityId}` ``) as defense in
depth against a future group-id scheme change.

## Wizard Two-Exit Persistence (reconciled)

**≥2 sensors selected → group path:**
1. Wizard sets `pendingPrefillMembers.value = selectedEntityIds` (existing signal, `state/groups.ts`).
2. Wizard navigates `location.hash = '#/groups/new'`.
3. `GroupsPage` → `GroupEditorForm` mounts, `resetDraft()` runs (existing `useEffect` in
   `GroupEditorForm`), consumes `pendingPrefillMembers` into `draftMembers`, exactly as
   `AreaSuggestionBanner`'s existing "Review" action already does today. **Zero new code on the
   receiving end.**
4. Operator picks mode/algorithm/sensitivity via the existing, untouched `AlgorithmChooser` flow and
   clicks Save → `POST /api/groups/save` (unchanged, full-list-replace of the `groups:` key only).

**1 sensor selected → single-sensor path:**
1. Wizard calls `setTracked(entityId, true)` (existing function, `state/sensors.ts`) — **but only
   after** the full sensor set has been loaded into `entityEdits` (see Pitfall 1).
2. Wizard navigates `location.hash = '#/detectors/sensor/' + encodeURIComponent(entityId)`.
3. `SingleDetectorEditorForm` mounts, reads the entity's current `entityEdits` entry (already tracked
   + defaulted to one `hst` detector by `setTracked`'s existing logic), renders the same
   `DetectorDisclosure`/`DetectorEntry`/`DetectorParamGrid` stack `SensorsPage` uses today.
4. Operator adjusts detector type/params, clicks Save → `POST /api/sensors/save` (unchanged,
   full-list-replace of `entities:` + `_patterns:`).

Both exits reuse the **existing full-list-replace save functions verbatim** — `saveGroup()` and
`save()` in `state/groups.ts`/`state/sensors.ts` need no code changes. The only new discipline is
*when* those signals get populated (item below).

## Backend Change Decision

**Zero backend changes required**, on the condition that Open Question 1 (single-sensor
guided-flow/sensitivity parity) is descoped to "reuse the existing hst/mad/stl `AlgorithmCard` grid +
`DetectorParamGrid`, no presets/guided question" — which matches the deferred-scope precedent set in
Phase 11 and Phase 12 (`11-CONTEXT.md`, `12-CONTEXT.md`). If the operator instead wants literal
Low/Med/High sensitivity presets for single-sensor detectors, that requires a **new**
`Web/SingleDetectorCatalog.cs` (mirroring `DetectorCatalog.cs`'s shape) + a new
`GET /api/detectors/single-catalog` endpoint + a generalized `SensitivityPresetPicker` (currently
hard-imports `draftParams`/`draftPresetLabel` from `state/groups.ts` — would need to accept those as
props instead) — this is explicitly **new scope**, not reuse, and should be raised as a separate
decision point in `/gsd-discuss-phase` or accepted as an explicit scope-cut, not silently built.

## Common Pitfalls

### Pitfall 1: Full-list-replace save silently drops untracked-in-this-session sensors
**What goes wrong:** `POST /api/sensors/save` (and the client's `save()` in `state/sensors.ts`) is a
**full-list-replace** of the `entities:` key — it writes exactly the sensors present (and marked
tracked) in the client's `entityEdits` signal at save time, nothing more. `GlobExpander.Resolve` on
the server unions the posted `selectedIds` with pattern-derived ids against the server's *own* full
registry snapshot, so the semantics are safe *only if* every currently-tracked sensor is present in
`entityEdits` before Save is clicked.
**Why it happens:** Today this is safe by accident: `SensorsPage`'s `useEffect` always calls
`loadSensors(query.value)` with an initial empty query, and `HaSensorRegistry.GetFiltered("")` returns
**all** sensors (`orchestrator/Argus.Orchestrator/Ha/HaSensorRegistry.cs` — early-return on empty `q`),
so `entityEdits` gets seeded for the entire tracked set on mount. If the new wizard or
`SingleDetectorEditorForm` route mounts *without* first loading the full sensor list (e.g. because the
wizard only ever fetches the ≥3-char search results), calling `save()` from that context would POST a
truncated `entities:` list and **silently untrack every other sensor in `entities.yaml`** on the next
save.
**How to avoid:** Either (a) call `loadSensors('')` unconditionally on `AddDetectorWizard` /
`SingleDetectorEditorForm` mount (background full load, independent of what the visible search UI
shows), or (b) never call `save()` from a partially-loaded state — gate the Save button on
`entityEdits` having been fully hydrated at least once. Recommend (a): mirrors the existing
`GroupsPage` pattern, which already calls `loadSensors(sensorQuery.value)` with `q=''` on mount purely
to give `MemberPicker` a full candidate list, for the exact same underlying reason.
**Warning signs:** A UAT scenario where tracking one new sensor via the wizard causes previously
tracked sensors to disappear from the Detectors list / stop scoring after the next save.

### Pitfall 2: Group vs. sensor id namespace assumption is implicit, not enforced
**What goes wrong:** The unified list's row key/identity logic assumes group ids and entity ids never
collide (see Unified Row Model Decision). This holds today because `slugify()` strips `.` but nothing
enforces it going forward.
**How to avoid:** Namespace the `key=` prop (`group:id` / `sensor:id`) even though a real collision is
currently impossible — cheap insurance, and documents the assumption for future maintainers.

### Pitfall 3: MIN_QUERY_LENGTH inconsistency across three search surfaces
**What goes wrong:** Three different sensor-search surfaces now exist with three different reveal
thresholds if not reconciled: `MemberPicker` (`MIN_QUERY_LENGTH = 2`, Groups' member picker),
`HaSensorRegistry.GetFiltered` (server-side, no minimum — returns everything for `q=''`), and the new
wizard (ROADMAP-specified `>=3`). Leaving `MemberPicker`'s constant untouched while adding a
*different* constant for the wizard, without a shared/parameterized source, invites future drift
(someone "fixes" one and not the other).
**How to avoid:** Generalize `MemberPicker` to accept `minQueryLength?: number = 2` and have the
wizard pass `minQueryLength={3}` explicitly — one component, one documented default, one deliberate
override. Do not fork a second copy of the picker.

### Pitfall 4: Removing the Sensors nav item silently orphans Pattern Filters
**What goes wrong:** `include`/`exclude` glob-pattern auto-tracking (`PatternFiltersPanel.tsx` +
`includePatterns`/`excludePatterns` signals in `state/sensors.ts`) is rendered only inside
`SensorsPage.tsx` today. If `SensorsPage` is removed/redirected without re-homing this UI, operators
lose the ability to view or edit include/exclude patterns through the UI entirely (the feature still
works server-side — `GlobExpander.Resolve` still runs on every save — but becomes invisible/uneditable
once the screen that rendered its textareas is gone).
**How to avoid:** Explicitly decide where `PatternFiltersPanel` renders post-restructure before
writing the plan (see Open Question 2) — do not let this be an accidental omission discovered during
UAT.

### Pitfall 5: Deep-link/bookmark breakage from the default-route change
**What goes wrong:** Any existing bookmark/shortcut to `#/sensors` or `#/groups` (bare) that isn't
redirected will render nothing sensible once `main.tsx`'s fallback branch is repointed to
`DetectorsPage`. (This is mitigated by the Router Decision's explicit redirect, but only if that
redirect is actually implemented — it is easy to add the new `/detectors` branch and forget the
backward-compat redirect for the two old bare routes.)
**How to avoid:** Add an explicit test asserting `#/sensors` and `#/groups` (bare, no id) both
normalize to `/detectors`, alongside the existing `parseGroupId` tests.

### Pitfall 6: AlgorithmChooser's single-sync-point assumption doesn't extend to a second draft
**What goes wrong:** `AlgorithmChooser.tsx`'s effect that mirrors `state/groupEditor.ts`'s
`selectedDetector` into `state/groups.ts`'s `draftDetector`/`draftParams` (Phase 08-04's documented
"single sync point") is specific to the **group** draft. If a future iteration tries to reuse
`AlgorithmChooser` directly inside `SingleDetectorEditorForm` (rather than the recommended
hand-off-to-`/groups/new` design), it will silently write into the *group* draft signals from a
single-sensor context — cross-contaminating any group draft the operator might return to. This
research's recommended design avoids the problem entirely by never mounting `AlgorithmChooser` inside
the single-sensor path.
**How to avoid:** Do not mount `AlgorithmChooser` from `SingleDetectorEditorForm`. If per Open
Question 1 the operator does want a literal single-sensor guided flow, it needs its own parallel
state module (not `state/groupEditor.ts`), not a shared mount of the existing component.

## Open Questions (RESOLVED)

> **All three resolved by operator decision during `/gsd-plan-phase 14` (recorded in `14-CONTEXT.md`).**
> Q1 → **D-05** (hand-off + simple hst/mad/stl editor; literal single-sensor guided flow deferred);
> Q2 → **D-08b** (relocate Pattern Filters to Settings); Q3 → **D-08a** (untrack only inside the editor).
> Each individual recommendation below was accepted as-is.

1. **[RESOLVED → D-05] Does "full guided flow" for the single-sensor path mean literal reuse of
   `GuidedFlowStep`/`SensitivityPresetPicker`, or reuse of the existing (simpler) hst/mad/stl
   `AlgorithmCard` grid + `DetectorParamGrid`?**
   - What we know: `GuidedFlowStep`/`SensitivityPresetPicker`/the "what are you monitoring?" question
     are bound to the 5-entry **group** catalog (`GET /api/detectors/catalog`) and have no
     hst/mad/stl equivalent. `11-CONTEXT.md` and `12-CONTEXT.md` both explicitly deferred a
     single-sensor catalog + sensitivity presets as "own phase if wanted."
   - What's unclear: whether the ROADMAP author intended literal component reuse (implying new
     backend catalog scope) or was describing the operator-visible *experience* ("a guided path")
     achievable via the hand-off design in this research.
   - Recommendation: Default to the hand-off design (zero new backend scope) as the plan's baseline;
     surface this explicitly in `/gsd-discuss-phase` or the plan's assumptions so the operator can
     override if they actually want single-sensor sensitivity presets built now.

2. **[RESOLVED → D-08b] Where does Pattern Filters (`PatternFiltersPanel`) live after the Sensors screen is removed?**
   - What we know: it's a global include/exclude auto-track config, orthogonal to per-sensor/per-group
     detector assignment, currently only rendered inside `SensorsPage`.
   - What's unclear: ROADMAP doesn't mention it at all — silence, not an explicit decision to drop it.
   - Recommendation: relocate its rendering to `SettingsPage.tsx` (already the "global configuration"
     screen, `GET /api/settings` already exists there) — the underlying signals/save path
     (`includePatterns`/`excludePatterns` in `state/sensors.ts`, bundled into `POST /api/sensors/save`)
     need no change, only where the `<PatternFiltersPanel>` JSX is mounted.

3. **[RESOLVED → D-08a] Does the unified Detectors list need a destructive "untrack sensor" action analogous to
   `GroupListRow`'s "Delete group" two-step confirm?**
   - What we know: today, untracking a sensor is done via the checkbox inside the full `SensorsPage`
     browse view (no confirm step). The unified list's sensor row (per Row Model Decision) no longer
     exposes an inline checkbox — it links out to `SingleDetectorEditorForm`.
   - What's unclear: whether "untrack" belongs on the list row (parity with Delete group) or only
     inside `SingleDetectorEditorForm` (e.g. removing all detectors + a checkbox to untrack).
   - Recommendation: Claude's discretion — put an "Untrack sensor" destructive-ghost action inside
     `SingleDetectorEditorForm` (mirrors "Remove detector" already there) rather than on the list row,
     since the list row's real estate is already tight with two row variants.

## Derived Requirements

Candidate REQ-IDs for the planner to mint into REQUIREMENTS.md (prefix suggestion: `DET-*` for the
list screen, `WIZ-*` for the wizard):

| Candidate ID | One-line description |
|---|---|
| DET-01 | Detectors screen shows one unified, DS-consistent list merging groups (`GET /api/groups`) and tracked single sensors (`GET /api/sensors`, `isTracked` entries only) |
| DET-02 | Editing a group row navigates to the existing, unchanged `/groups/:id` `GroupEditorForm` |
| DET-03 | Editing a single-sensor row navigates to a new dedicated single-sensor detector-edit view/route, preserving hst/mad/stl assignment + inline validation + `DetectorDefaults.cs` defaults |
| DET-04 | Sidebar nav item "Sensors" and "Groups" removed; "Detectors" + "Add detector" items added, with correct active-route highlighting |
| DET-05 | `/detectors` is the new default route (root with no hash); bare `/sensors` and bare `/groups` redirect to `/detectors`; `/groups/new` and `/groups/:id` continue to work unchanged for direct deep links |
| WIZ-01 | Add-detector wizard route with sensor multi-select search that reveals matching rows only once the query is >=3 characters |
| WIZ-02 | Selecting exactly 1 sensor in the wizard tracks it and opens the single-sensor detector-edit view for it |
| WIZ-03 | Selecting >=2 sensors in the wizard pre-fills them into a new group draft (`#/groups/new`) via the existing `pendingPrefillMembers` handoff, then continues through the existing, unchanged guided algorithm-chooser flow |
| WIZ-04 | Any save triggered from the wizard or the new single-sensor editor loads the complete current tracked-sensor set first, so the full-list-replace `POST /api/sensors/save` never silently drops previously tracked sensors (Pitfall 1) |
| DET-06 (Claude's discretion / needs operator confirm) | Pattern Filters (include/exclude auto-track) UI relocated to the Settings screen after the Sensors screen is removed |

## Security Domain

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Unchanged — Ingress path/loopback check (`IsAuthorizedRequest`) already gates every `/api/*` route; Phase 14 adds no new auth surface |
| V3 Session Management | no | No session concept in this app (single-operator local admin UI) |
| V4 Access Control | no | No new roles/permissions; existing `IsAuthorizedRequest` 403 guard already covers any new endpoints if Open Question 1 leads to one |
| V5 Input Validation | yes | New route param (`entityId` in `/detectors/sensor/:entityId`) must be `decodeURIComponent`'d defensively — mirrors the existing `parseGroupId` pattern (no path traversal risk since it's a hash-route param consumed client-side only, never used as a filesystem path) |
| V6 Cryptography | no | No new crypto surface |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Full-list-replace save racing/clobbering (Pitfall 1) | Tampering (of persisted config, via incomplete client state, not an attacker) | Load full sensor set before any save; existing monotonic-sequence guards (`loadSensorsSeq`) already prevent stale-response races |
| Malformed hash route params | Information Disclosure (minor — client-side render glitch at worst) | `decodeURIComponent` + graceful fallback to the list view on parse failure, same discipline as existing `parseGroupId` |

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new libraries; every claim verified by reading the actual source file
- Architecture: HIGH — router/state/component reuse paths traced end-to-end through real code
- Pitfalls: HIGH — Pitfall 1 (full-list-replace safety) verified against both the client `save()`
  logic and the server's `GlobExpander.Resolve`/`HaSensorRegistry.GetFiltered` implementation
- Single-sensor guided-flow scope gap: HIGH — corroborated by two independent prior-phase CONTEXT.md
  deferral notes (11, 12), not just inferred from this session's code reading

**Research date:** 2026-07-21
**Valid until:** No expiry driver (internal-only reuse research, not version-pinned) — re-verify only
if Phases 10-13 code changes again before Phase 14 executes.
