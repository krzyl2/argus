# Phase 13: Groups Screen Rebuild - Context

**Gathered:** 2026-07-17
**Status:** Ready for planning

<domain>
## Phase Boundary

Rebuild the existing, functional Groups screen (`GroupsPage.tsx`, `GroupList.tsx`,
`GroupListRow.tsx`, `GroupEditorForm.tsx`, `MemberPicker.tsx`, `AlgorithmChooser.tsx`,
`AttributionPanel.tsx`, `AttributionBar.tsx`, and supporting components) to the Argus
Design System spec. **Markup and component structure may be refactored, not just restyled.**
Group CRUD, the guided algorithm creation wizard, and attribution display must remain fully
working end-to-end after the rebuild.

Requirements: GRP-12 (group editor rebuilt to DS spec), GRP-13 (algorithm creation wizard
rebuilt to DS spec, radio-card 2px accent selection), GRP-14 (attribution panel / AttributionBar
rebuilt to DS spec).

**In scope:** frontend rebuild only, all work in `orchestrator/ui/src/**`. Direct sibling of
Phase 12 (Sensors rebuild) — the same DS-adoption principles apply.

**Out of scope (→ Deferred):** any backend change; live per-member value column in the member
picker (no live-value field on `SensorEntry`); algorithm tester/simulator (Backlog Phase 999.1).

</domain>

<decisions>
## Implementation Decisions

Phase 13 inherits the locked principles from Phases 10/11/12 (see Prior Decisions below). The
four decisions here resolve where the DS reference (`ui_kits/admin/Groups.jsx`) genuinely diverges
from the current implementation.

### Navigation / editing model (GRP-12)
- **D-01:** **Keep the hash-router model** (`#/groups`, `#/groups/new`, `#/groups/:id`) — URL is
  deep-linkable and already works. Adopt only the DS reference's *visual* `PageHeader` and the
  "← Back to groups" affordance. **Reject** the kit's in-place `editing`-state toggle (it drops
  URL addressability). Consistent with the "kit = layout, not behavior/structure" rule (Phase 11/12).
  `GroupsPage.tsx`'s internal route-branch between `GroupList` and `GroupEditorForm` stays.

### Group list row (GRP-12)
- **D-02:** **Card-wrapped, click-to-edit row** — adopt the kit's `Card padding="none"` clickable
  row + two `Badge`s (mode + detector). **But preserve GRP behavior the kit's mock omits:** keep the
  inline **two-step "Delete group"** confirm (`destructive-ghost`, 3s arm window, no `window.confirm`)
  and the **status indicator** ("no status yet" / active|anomaly `Badge`) as right-aligned row meta.
  > ⚠ **DS-reference conflict flagged (Rule 7):** `Groups.jsx` shows a pure click-to-edit row with no
  > delete and no status. The kit's row is a mockup simplification; the existing delete + status
  > behavior wins (analog of Phase 12 D-06). Use the kit for row/Card/Badge visual fidelity only.

### Algorithm chooser — mode filtering (GRP-13)
- **D-03:** **Preserve current chooser behavior** — the `AlgorithmChooser` shows the **full** detector
  catalog regardless of `draftMode`, and the guided "What are you monitoring?" step is **always
  available** (state machine in `state/groupEditor.ts`). Restyle only.
  > ⚠ **DS-reference conflict flagged (Rule 7):** `Groups.jsx` filters the catalog by mode
  > (peer → only `peer_divergence`; joint → the rest) and gates the guided step to `mode === 'joint'`.
  > That is a *behavioral* change to `groupEditor.ts` + its tests, out of scope for a visual rebuild.
  > The kit's mode-filter is a mock convenience; current behavior is authoritative.
- **D-04:** The wizard steps (guided question → `AlgorithmCard` grid → `SensitivityPresetPicker`
  → `AdvancedParamsDisclosure`) must render to DS spec, with **radio-card 2px accent-border selection**
  (never color alone). `AlgorithmCard` (Phase 10, string-prop-widened in Phase 12) is already
  A11Y-02 compliant — **reuse it, do not re-derive.** Restyle `GuidedFlowStep`'s buttons to the DS
  `Card` + `Button` layout; keep its copy verbatim (Copywriting Contract, 2 answers + skip link).

### Attribution panel (GRP-14)
- **D-05:** **Preserve the live-polling, server-driven behavior** of `AttributionPanel` — 60s poll of
  `GET /api/groups/{id}/status`, the 4 states (loading / no-score-yet / unsupported when
  `contributions.length === 0` / ranked list), and the server pre-sort (never client re-sort).
  **Restyle only:** wrap in `Card` + a `SectionLabel` ("Member attribution · last result, refreshes
  ~60s"), and render the unsupported state via the shared **`EmptyState`** component. `AttributionBar`
  restyled to DS spec — top-ranked uses `--color-accent` fill, others neutral (accent = "the one answer").
  > ⚠ **DS-reference conflict flagged (Rule 7):** `Groups.jsx` gates attribution by detector *type*
  > (peer_divergence/pca → EmptyState) client-side with static mock bars. The real contract is
  > server-driven via `contributions.length`; keep that. Kit's bars are a visual reference only.

### Component adoption depth — full adoption
- **D-06:** **Full adoption of Phase 10 primitives**, refactoring markup as needed:
  - Wrap group list and member picker in **`Card`** (member picker: `padding="none"`, scroll region).
  - Mode selector: raw `<select>` → shared **`Select`** with descriptive option labels.
  - Member picker: raw `<ul>`/`<input>` → shared **`Card` + `Checkbox` + `Badge` ("member")
    + `SearchInput`**; `argus-pill--tracked` → `Badge`.
  - Name field: raw `<input>` → shared **`Input`** (built-in `label` + `error`), error driven by
    the existing name-required rule.
  - `AreaSuggestionBanner`, `GroupSaveResultBanner` → shared **`Banner`** tones (already retrofitted
    in Phase 10 Wave 3 — verify, don't re-derive).
- **D-07:** **Preserve unchanged (behavior parity):** all of `validation/groupParams.ts`
  (`validateGroupMembers` member-floor, `validateUnitConsistency` unit-mismatch, English messages —
  do not reword); the `state/groups.ts` draft layer (`draftFriendlyName`/`draftMembers`/`draftMode`/
  `draftDetector`/`draftParams`/`draftPresetLabel`, `saveGroup`, `deleteGroup`, slugify-on-create);
  the `state/groupEditor.ts` chooser state machine (`chooserMode`, `selectedDetector`,
  `guidedRecommended`, catalog load-once); `SensitivityPresetPicker` (Phase 10 verified DS-compliant
  in place — Med default, `isCustomized`, accent-color radios); `MIN_QUERY_LENGTH = 2` gate in
  `MemberPicker`; the 60s `AttributionPanel` poll; `SaveBar` disabled/saving flow.

### Claude's Discretion
- Exact grid/spacing/typography per section follows the Phase 10 shared library + the
  `ui_kits/admin/Groups.jsx` visual reference (name+mode two-col grid, `maxWidth: 720` editor column,
  section labels). Planner may refine layout details.
- Whether the member picker's meta column shows unit-of-measurement only (current) or is dropped is a
  styling detail — no live per-member value exists on `SensorEntry`, so the kit's `{value} {unit}` is
  not fully sourceable; field/behavior must not change.
- Whether `AdvancedParamsDisclosure` uses the DS reference's `1fr 1fr` grid is styling — field
  set/order/defaults and preset expansion must not change (D-07).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Design references (layout/visual only — NOT behavior)
- `Argus Design System/HANDOFF_TO_CLAUDE_CODE.md` — milestone design reference package (voice,
  visual foundation, component API+look specs).
- `Argus Design System/ui_kits/admin/Groups.jsx` — Groups reference layout: PageHeader + Back,
  Card-wrapped clickable list rows, mode `Select`, `Card`-wrapped member picker, `AlgorithmCard`
  grid, `SensitivityPreset`, `Disclosure` advanced params, `AttributionBar`. **In-place editing
  toggle, mode-filtered catalog, detector-type attribution gate, and `ARGUS_DATA` mocks are NOT
  authoritative** (see D-01/D-03/D-05 conflict flags).
- `Argus Design System/ui_kits/admin/index.html` — composition reference (row/Card patterns).
- `Argus Design System/ui_kits/admin/shared.jsx` — `PageHeader`, `SectionLabel`, `SaveBar` reference
  wrappers used by `Groups.jsx`.

### Frontend files being rebuilt / integrated (all under `orchestrator/ui/src/`)
- `components/GroupsPage.tsx` — screen entry point + route branch (keep router per D-01).
- `components/GroupList.tsx` + `components/GroupListRow.tsx` — list + row (→ Card/Badge, D-02).
- `components/GroupEditorForm.tsx` — create/edit form orchestration.
- `components/MemberPicker.tsx` — multi-select picker (→ Card/Checkbox/Badge/SearchInput, D-06).
  **Note: currently has uncommitted local edits** (MIN_QUERY_LENGTH=2 gate) — reconcile before rebuild.
- `components/AlgorithmChooser.tsx` + `components/GuidedFlowStep.tsx` — wizard (D-03/D-04).
- `components/SensitivityPresetPicker.tsx` + `components/AdvancedParamsDisclosure.tsx` — preset +
  advanced params (preserve behavior, D-07).
- `components/AttributionPanel.tsx` + `components/AttributionBar.tsx` — attribution (D-05).
- `components/AreaSuggestionBanner.tsx`, `components/GroupSaveResultBanner.tsx`,
  `components/SaveBar.tsx`, `components/FieldValidationError.tsx` — supporting.
- `state/groups.ts` + `state/groupEditor.ts` — draft + chooser state. **Preserve behavior (D-07).**
- `validation/groupParams.ts` — member-floor + unit-consistency. **Preserve verbatim (D-07).**
- `api/types.ts` — `GroupConfig`, `GroupMode`, `GroupDetectorName`, `GroupStatus`,
  `DetectorCatalog`/`DetectorCatalogEntry`, `SensorEntry`.

### Shared components to compose (Phase 10)
- `components/{Card,Badge,Banner,Select,SearchInput,Checkbox,Input,Disclosure,EmptyState,Button,AlgorithmCard}.tsx`
- `AlgorithmCard` already A11Y-02 compliant + string-prop-widened (Phase 12 D-02).

### Prior context
- `.planning/phases/12-sensors-screen-rebuild/12-CONTEXT.md` — sibling screen rebuild; established
  full-adoption + kit-is-layout-only + preserve-validation-verbatim, and the DS-reference conflict-flag
  pattern (D-06 there). Direct template for Phase 13.
- `.planning/phases/10-design-system-foundation/10-CONTEXT.md` — component library + theme +
  focus/radio-card a11y rules; `SensitivityPresetPicker` verified DS-compliant in place.
- `.planning/phases/11-new-standalone-screens-dashboard-algorithms-settings/11-CONTEXT.md` —
  established "kit is layout-only, not behavior".

### Backend catalog source (for wizard, read-only)
- `orchestrator/Argus.Orchestrator/Web/DetectorCatalog.cs` — group detector catalog + guided
  answer→detector map + presets served via `GET /api/detectors/catalog` (do not change; wizard consumes it).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`AlgorithmCard`** (Phase 10, widened in Phase 12) — the group detector radio-card, already
  2px-accent A11Y-02 compliant. Reuse as-is for GRP-13.
- **`SensitivityPresetPicker`** — already the DS-compliant "SensitivityPreset" (Phase 10 verified
  in place); no separate `SensitivityPreset.tsx` exists. Reuse.
- **Phase 10 primitives** (Card/Badge/Banner/Select/SearchInput/Checkbox/Input/Disclosure/EmptyState/
  Button) — compose.
- **`state/groupEditor.ts` chooser state machine** — `chooserMode` ('guided-question' →
  'guided-pick-shown' → 'manual'), catalog load-once, guided answer→detector map. Reuse verbatim.
- **`validation/groupParams.ts`** — member-floor + unit-consistency, drives SaveBar disabled state.

### Established Patterns
- **Two-step inline delete** in `GroupListRow` (3s arm window, no `window.confirm`) — preserve (D-02).
- **60s poll, server-pre-sorted, 4-state** `AttributionPanel`; errors leave last-known state (soft
  display) — preserve (D-05).
- **`MIN_QUERY_LENGTH = 2`** gate before rendering the sensor list in `MemberPicker` (400+ entities) —
  preserve (D-07).
- **Kit-is-layout-only** (Phases 11/12): reference `.jsx` mocks (`ARGUS_DATA`) define visuals, not
  behavior — do NOT let them drive navigation, catalog filtering, or attribution gating.
- **Router branch** in `GroupsPage`: `#/groups/new` + `#/groups/:id` → `GroupEditorForm`; else list.

### Integration Points
- No backend changes. Wizard consumes `GET /api/detectors/catalog`; attribution consumes
  `GET /api/groups/{id}/status`; CRUD via existing `saveGroup`/`deleteGroup` in `state/groups.ts`.
- Member picker reuses the shared `sensors` signal/loader (loaded in `GroupsPage` effect).

</code_context>

<specifics>
## Specific Ideas

- Group list row: `friendlyName` (or `groupId` fallback) as title, `groupId · N members` mono
  subtitle, mode `Badge` + detector `Badge`, whole row clickable to edit; delete + status as
  right-aligned meta (D-02).
- Guided wizard copy is a Copywriting Contract — keep the 2 answers + "Skip — choose manually"
  verbatim (D-04).
- Attribution: top-ranked bar uses `--color-accent`, others neutral; unsupported state via shared
  `EmptyState`; SectionLabel notes "refreshes ~60s" (D-05).
- Member "member" pill → shared `Badge` (tone member/tracked).

</specifics>

<deferred>
## Deferred Ideas

- **Algorithm tester/simulator in group config** — Backlog Phase 999.1 (simulate detector scoring
  against selected sensors' history before saving). Not this phase.
- **Live per-member value column** in the member picker — kit shows `{value} {unit}`, but
  `SensorEntry` has no live-value field. Own phase if a live-value feed is added.
- **Adopting the kit's mode-filtered catalog + joint-only guided gate** — a behavioral change to
  `groupEditor.ts`; if wanted, its own scoped change with test updates (D-03 flags it).
- **Kit's in-place (URL-less) editing model** — rejected for deep-linking (D-01); revisit only if
  URL addressability is ever dropped project-wide.

None of the above block Phase 13.

</deferred>

---

*Phase: 13-Groups Screen Rebuild*
*Context gathered: 2026-07-17*
