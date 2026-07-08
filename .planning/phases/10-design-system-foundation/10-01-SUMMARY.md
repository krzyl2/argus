---
phase: 10-design-system-foundation
plan: 01
subsystem: ui
tags: [css, design-tokens, dark-mode, a11y, preact]

# Dependency graph
requires: []
provides:
  - Full light+dark CSS token set on :root / [data-theme="dark"] (colors, spacing/radius/border/control, 8-size typography, elevation)
  - New BEM classes for Button variants/sizes, Sidebar/Shell layout, Card, KpiTile, Badge tones, StatusDot warn/idle, Banner info tone
  - A11Y-01 keyboard focus-visible ring no longer suppressed by any component :focus rule
  - Pre-paint theme bootstrap in main.tsx (localStorage argus-theme + matchMedia one-shot seed)
affects: [10-02, 10-03, 10-04, 10-05, 11, 12, 13]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Dark mode via [data-theme=\"dark\"] attribute selector (not @media prefers-color-scheme) — manual toggle + localStorage restore, OS preference only seeds first-load default"
    - "Focus suppression pattern: outline:none scoped to :focus:not(:focus-visible) so mouse clicks hide the outline but keyboard Tab always shows the global :focus-visible ring"
    - "BEM class extension: new component classes appended in a dedicated Phase 10 section rather than interleaved into per-phase historical sections"

key-files:
  created: []
  modified:
    - orchestrator/ui/public/css/argus.css
    - orchestrator/ui/src/main.tsx

key-decisions:
  - "Dark elevation overrides (--shadow-popover/--shadow-dialog) folded into the same [data-theme=\"dark\"] block rather than a separate rule — same selector, no functional difference, simpler file"
  - "Kept border-color changes on :focus (both input methods) while scoping only outline:none to :focus:not(:focus-visible) — preserves the existing visual focus cue for mouse users and adds back the keyboard ring, rather than removing the border-color affordance"
  - "Added flex:1 + min-width:0 to .argus-main alongside the max-width reconciliation, needed for .argus-main to correctly fill remaining width inside the new .argus-shell flex row (Rule 2 — missing critical functionality for the shell layout to work, not scope creep)"

patterns-established:
  - "Pattern 1: [data-theme=\"dark\"] attribute selector is now the single source of truth for dark values; no @media (prefers-color-scheme: dark) exists anywhere in argus.css"
  - "Pattern 2: focus-visible-safe suppression — outline:none must always be scoped :focus:not(:focus-visible), never a bare :focus or unconditional rule"

requirements-completed: [THEME-01, THEME-02, A11Y-01]

# Metrics
duration: 25min
completed: 2026-07-08
status: complete
---

# Phase 10 Plan 01: CSS Token Foundation + A11Y-01 Focus Fix + Theme Bootstrap Summary

**Ported the full Argus Design System token set (colors/spacing/typography/elevation) into production `argus.css`, replaced the OS-only `@media (prefers-color-scheme: dark)` block with a manual `[data-theme="dark"]` attribute block, added every new BEM class Wave 2 components need, fixed a global keyboard-focus-ring suppression bug across 5 component `:focus` rules, and added a pre-paint theme bootstrap to `main.tsx`.**

## Performance

- **Duration:** ~25 min
- **Completed:** 2026-07-08T09:07:13Z
- **Tasks:** 3
- **Files modified:** 2

## Accomplishments
- Full 4-token-family port (color/spacing-radius-border-control/8-size typography/elevation) onto `:root`, with `[data-theme="dark"]` replacing the old OS-only media block — zero parallel source of truth for dark values
- 16+ new BEM classes added (Button `--secondary`/`--ghost`/`--sm`/`--xs`/`__spinner`, full `.argus-sidebar*` family + `.argus-shell`, `.argus-card`/`--interactive`, `.argus-kpi-tile*`, Badge tones `--member`/`--neutral`/`--ok`/`--warn`/`--error`/`--accent`, StatusDot `.status-warn`/`.status-idle`, Banner `--info` + `__dismiss`) — Wave 2 component plans can now build `.tsx` files touching zero CSS
- A11Y-01 fixed: all 5 existing component `:focus { outline: none }` rules (search input, filters textarea, detector select, param field input, and the error-state param field) rescoped to `:focus:not(:focus-visible)` — keyboard Tab now always shows the global 2px accent ring
- Theme restored before first paint via `localStorage.getItem('argus-theme')`, falling back to a one-shot `matchMedia('(prefers-color-scheme: dark)')` seed — no flash of wrong theme on reload

## Task Commits

Each task was committed atomically:

1. **Task 1: Port full token set and replace OS-dark media block with [data-theme="dark"]** - `ddfd791` (feat)
2. **Task 2: Add new component BEM classes and fix A11Y-01 keyboard focus suppression** - `c037b07` (feat)
3. **Task 3: Add pre-paint theme bootstrap to main.tsx** - `dfecce1` (feat)

_Note: no TDD tasks in this plan — pure CSS/bootstrap plan, verified via grep-based acceptance criteria + `tsc -b`._

## Files Created/Modified
- `orchestrator/ui/public/css/argus.css` - Extended `:root` with brand/status-warn/soft-tint/layout/8-size-typography/elevation tokens; replaced `@media (prefers-color-scheme: dark)` with `[data-theme="dark"]`; added Button/Sidebar/Shell/Card/KpiTile/Badge/StatusDot/Banner BEM classes; fixed 5 focus-suppression bugs; reconciled `.argus-main` max-width to `var(--content-max)`
- `orchestrator/ui/src/main.tsx` - Added synchronous pre-render theme bootstrap reading `localStorage` / `matchMedia`

## Decisions Made
- Dark-mode elevation overrides folded into the same `[data-theme="dark"]` selector block rather than a second separate block (identical effective CSS, simpler file — see key-decisions above)
- Preserved `border-color` on `:focus` for both input methods while scoping only `outline: none` to `:focus:not(:focus-visible)` — mouse users keep the existing border cue, keyboard users additionally get the global focus-visible outline back
- Added `flex: 1; min-width: 0;` to `.argus-main` alongside the plan's required `max-width` reconciliation (Rule 2 — required for `.argus-main` to behave correctly as the second flex child of the new `.argus-shell` row; omitting it would leave `.argus-main` unable to fill/shrink correctly once the Sidebar retrofit lands in a later plan)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] Added `flex: 1; min-width: 0;` to `.argus-main`**
- **Found during:** Task 2 (component BEM class authoring)
- **Issue:** Plan's task 2 item 8 only asked to reconcile `.argus-main`'s `max-width` from `720px` to `var(--content-max)`. But Task 2 also introduces `.argus-shell` as a flex row containing Sidebar + `.argus-main` — without `flex: 1; min-width: 0;`, `.argus-main` would not correctly fill/shrink as the second flex child once a later plan wires the Sidebar retrofit into `AppShell.tsx`, leaving an unusable layout foundation.
- **Fix:** Added `flex: 1; min-width: 0;` alongside the `max-width` change in the same rule.
- **Files modified:** `orchestrator/ui/public/css/argus.css`
- **Verification:** No acceptance criteria regressed (`grep '720px'` returns empty; `tsc -b` exits 0); visually inert until `.argus-shell`/Sidebar are wired by a later plan, so no behavior change to the current `AppShell.tsx` (which does not yet use `.argus-shell`).
- **Committed in:** `c037b07` (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 missing critical)
**Impact on plan:** Necessary for the CSS foundation to actually support the `.argus-shell` layout Wave 2/D-01 depends on. No scope creep — no new class names beyond what the plan already specified for `.argus-shell`/`.argus-main`.

## Issues Encountered
- `orchestrator/ui/node_modules` was not present in this worktree (git worktrees do not carry untracked/ignored directories from the main checkout) — ran `npm install` locally in the worktree before running `npx tsc -b` for verification. This is a local dev-environment step, not a code change; nothing was committed for it (`node_modules/` remains gitignored).
- `Argus Design System/tokens/*.css` and `Argus Design System/components/*.jsx` reference files (untracked in the main repo per `git status`) are not visible inside this git worktree either, for the same reason. Read them directly from the main repo path (`C:\Workspace\Repos\Tools\Anomaly\Argus Design System\...`) instead — the Read tool can access any path on the filesystem regardless of worktree boundaries. No worktree files were written outside the assigned worktree; this was read-only reference lookup.

## Next Phase Readiness
- Wave 2 component plans (10-02 onward) can now build/retrofit `.tsx` files exclusively — every BEM class they need already exists in `argus.css`
- THEME-01 (dark tokens) and the THEME-02 restore-on-reload half are complete; the remaining THEME-02 "explicit toggle" half is Sidebar's responsibility (later plan)
- A11Y-01 is now correctly enforced globally; no component in this plan's scope suppresses the keyboard focus ring
- No blockers for Wave 2

## Self-Check: PASSED

- FOUND: orchestrator/ui/public/css/argus.css
- FOUND: orchestrator/ui/src/main.tsx
- FOUND: .planning/phases/10-design-system-foundation/10-01-SUMMARY.md
- FOUND: ddfd791 (Task 1)
- FOUND: c037b07 (Task 2)
- FOUND: dfecce1 (Task 3)

---
*Phase: 10-design-system-foundation*
*Completed: 2026-07-08*
