# Phase 10: Design System Foundation - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-07-08
**Phase:** 10-design-system-foundation
**Areas discussed:** Sidebar/AppShell rollout scope, Component library scope, Success Criteria #1 verifiability at 2/5 screens

---

## Sidebar/AppShell rollout scope

| Option | Description | Selected |
|--------|-------------|----------|
| Swap teraz | AppShell.tsx renders the new Sidebar immediately instead of `<header>`; only way Success Criteria #1 is verifiable | ✓ |
| Tylko komponent, bez podpięcia | Sidebar built in isolation, AppShell unchanged until per-screen rebuild phases | |
| Ty decydujesz | Leave to Claude | |

**User's choice:** Swap teraz (recommended option)
**Notes:** None.

| Option | Description | Selected |
|--------|-------------|----------|
| Wszystkich 5, 3 wyłączone/placeholder | Sidebar shows final nav for all 5 screens now; Dashboard/Algorithms/Settings disabled until Phase 11 | ✓ |
| Tylko 2 istniejące | Sidebar shows only Sensors/Groups; other items added with Phase 11 routing | |
| Ty decydujesz | Leave to Claude | |

**User's choice:** Wszystkich 5, 3 wyłączone/placeholder (recommended option)
**Notes:** None.

---

## Component library scope

| Option | Description | Selected |
|--------|-------------|----------|
| Dopasować do spec w miejscu | Modify existing AlgorithmCard.tsx/SensitivityPresetPicker.tsx in place to match spec exactly | ✓ |
| Zbudować nowe obok, podmienić w Phase 13 | Build new versions separately, leave old ones untouched until Phase 13 | |
| Ty decydujesz | Leave to Claude | |

**User's choice:** Dopasować do spec w miejscu (recommended option)
**Notes:** None.

| Option | Description | Selected |
|--------|-------------|----------|
| Tylko biblioteka, bez retrofitu | New component modules created; existing pages (SaveBar, DetectorEntry, etc.) keep current raw-class markup until their own rebuild phase | |
| Retrofit wszystkiego teraz | All existing call sites switched to new shared components in Phase 10 itself | ✓ |
| Ty decydujesz | Leave to Claude | |

**User's choice:** Retrofit wszystkiego teraz — **against Claude's recommendation** (which was library-only, no retrofit). User made an explicit, deliberate call for the larger-footprint option.
**Notes:** Expands Phase 10 scope to touch files Phase 12/13 will also touch (SaveBar, DetectorEntry, SensorSearchInput, EmptyState, etc.).

---

## Success Criteria #1 verifiability at 2/5 screens

| Option | Description | Selected |
|--------|-------------|----------|
| Tak — 2 żywe + 3 placeholder wystarczy | Verify toggle on Sensors/Groups (retrofitted) + sidebar placeholders; Dashboard/Algorithms/Settings inherit consistency automatically when Phase 11 adds routes | ✓ |
| Nie — zbudować min. szkielet wszystkich 5 tras teraz | Add empty placeholder routes for all 5 screens now so the criterion is verifiable against 5 live routes | |
| Ty decydujesz | Leave to Claude | |

**User's choice:** Tak — 2 żywe + 3 placeholder wystarczy (recommended option)
**Notes:** None.

---

## Claude's Discretion

- **Dark-mode activation mechanism** — not selected by the user as a discussion area, and the user declined to explore additional gray areas afterward. Claude identified and resolved a real conflict during codebase scouting: `argus.css` today gates all dark values behind `@media (prefers-color-scheme: dark)` (OS-only, no manual toggle) — contradicting STATE.md's "0 dark-mode rules" note (stale). Resolution locked in CONTEXT.md: replace the media-query block with `[data-theme="dark"]` attribute-driven tokens (verbatim port from the Design System package), with a one-time `matchMedia` read as the *initial* default only when no `localStorage` choice exists yet. See CONTEXT.md's "Claude's Discretion" section for full rationale.
- New shared component file location/structure inside `orchestrator/ui/src/components/` — left to planner.
- Whether new components get matching `.test.tsx` files — left to planner per existing repo conventions.

## Deferred Ideas

None — discussion stayed within phase scope. Icon-set adoption is already out of scope for this milestone per REQUIREMENTS.md (UI-10).
