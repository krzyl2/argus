# Phase 13: Groups Screen Rebuild - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-07-17
**Phase:** 13-Groups Screen Rebuild
**Areas discussed:** Navigation model, Group list row, Algorithm chooser mode-filtering, Attribution panel behavior

---

## Navigation / editing model

| Option | Description | Selected |
|--------|-------------|----------|
| Keep hash-router | Keep addressable `#/groups/:id` pages; adopt only DS PageHeader + Back visual | ✓ |
| In-place toggle from kit | Single component with local `editing` state, no URL change | |

**User's choice:** Keep router (recommended).
**Notes:** Deep-linkable URL preserved; kit's in-place toggle rejected. Consistent with the
"kit = layout, not behavior/structure" rule from Phases 11/12. → CONTEXT D-01.

---

## Group list row

| Option | Description | Selected |
|--------|-------------|----------|
| Card click→edit, delete+status in row | Adopt clickable Card + mode/detector Badges, but preserve two-step delete + status as row meta | ✓ |
| DS row + actions only in editor | Pure click-to-edit row; move delete/status into the editor | |

**User's choice:** Card click→edit, delete+status kept in row (recommended).
**Notes:** Kit's row drops delete + status as a mock simplification; GRP behavior wins
(analog of Phase 12 D-06 conflict flag, Rule 7). → CONTEXT D-02.

---

## Algorithm chooser — mode filtering

| Option | Description | Selected |
|--------|-------------|----------|
| Preserve current behavior | Full catalog shown regardless of mode; guided always available; restyle only | ✓ |
| Adopt kit's mode-filter | Filter catalog by draftMode; guided only for joint | |

**User's choice:** Preserve current behavior (recommended).
**Notes:** Kit's mode-filter + joint-only guided gate is a behavioral change to
`state/groupEditor.ts` + tests — out of scope for a visual rebuild. Flagged Rule 7. → CONTEXT D-03.

---

## Attribution panel

| Option | Description | Selected |
|--------|-------------|----------|
| Preserve polling + 4 server states | Keep 60s poll + server-driven states; restyle bars + wrap in Card + EmptyState for unsupported | ✓ |
| Gate by detector type like kit | Hide panel for peer_divergence/pca client-side by detector type | |

**User's choice:** Preserve polling + server-driven 4 states (recommended).
**Notes:** Kit gates attribution client-side by detector type with static mock bars; real
contract is server-driven via `contributions.length`. Restyle only. Flagged Rule 7. → CONTEXT D-05.

---

## Claude's Discretion

- Exact grid/spacing/typography per section (name+mode two-col grid, editor `maxWidth: 720`,
  section labels) — follows Phase 10 library + `Groups.jsx` visual reference.
- Member picker meta column: unit-only (current) vs kit's `{value} {unit}` — no live-value field
  on `SensorEntry`, so kit value not sourceable; behavior unchanged.
- `AdvancedParamsDisclosure` grid layout (`1fr 1fr`) — styling only; fields/defaults/preset
  expansion unchanged.

## Deferred Ideas

- Algorithm tester/simulator (Backlog Phase 999.1).
- Live per-member value column in member picker (needs a live-value feed on `SensorEntry`).
- Adopting the kit's mode-filtered catalog + joint-only guided gate (own scoped change + tests).
- Kit's in-place URL-less editing model (rejected for deep-linking; revisit only if URLs dropped).
