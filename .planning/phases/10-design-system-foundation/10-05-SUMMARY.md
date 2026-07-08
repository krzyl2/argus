---
phase: 10-design-system-foundation
plan: 05
subsystem: ui
tags: [preact, navigation, theme-toggle, sidebar, appshell]

# Dependency graph
requires: [10-01]
provides:
  - "Sidebar component: navy ground, 5 nav items (Dashboard/Algorithms/Sensors/Groups/Settings), active-state derived from route.value, 3 disabled placeholders (D-02)"
  - "AppShell rewritten to .argus-shell + Sidebar + .argus-main, header/footer removed (D-01)"
  - "Theme toggle in Sidebar footer: flips [data-theme] on <html> + writes localStorage['argus-theme'] (THEME-02 write half; restore half already in Plan 10-01 main.tsx bootstrap)"
affects: [11, 12, 13]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Sidebar nav item list is a static const array (id/label/icon/href?/disabled?); active state computed by matching route.value, not stored in component state"
    - "Theme toggle state initialized by reading document.documentElement's current data-theme attribute (set synchronously pre-paint by Plan 10-01's main.tsx bootstrap) rather than re-deriving from localStorage/matchMedia a second time"

key-files:
  created:
    - orchestrator/ui/src/components/Sidebar.tsx
    - orchestrator/ui/src/components/Sidebar.test.tsx
  modified:
    - orchestrator/ui/src/components/AppShell.tsx
    - orchestrator/ui/vitest.config.ts

key-decisions:
  - "Algorithms and Settings both use the ⚙ glyph (per plan's explicit icon assignment) — not a mistake, both are gear-shaped in the absence of a real icon set"
  - "vitest.config.ts execArgv: ['--no-experimental-webstorage'] added (Rule 3) — Node 22+'s built-in Web Storage API shadows jsdom's window.localStorage in this Node 26/vitest 4/jsdom 29 combo, making localStorage undefined inside tests; disabling Node's built-in lets jsdom's per-test implementation work"

patterns-established:
  - "Pattern: disabled nav items get both the .argus-sidebar__item--disabled class AND the native disabled attribute (belt-and-suspenders — CSS pointer-events:none plus real keyboard/AT semantics)"

requirements-completed: [THEME-02, COMP-01]

# Metrics
duration: ~20min
completed: 2026-07-08
status: complete
---

# Phase 10 Plan 05: Sidebar + AppShell Retrofit Summary

**Built the navy Sidebar component (5 nav items, 3 disabled placeholders, active-route highlighting) and replaced AppShell's old top-header/footer layout with the Sidebar-based `.argus-shell` shell; added the Sidebar-footer theme toggle that flips and persists `data-theme`/`localStorage['argus-theme']`, with a unit test covering both the nav-item rendering and the toggle's write behavior.**

## Performance

- **Duration:** ~20 min
- **Completed:** 2026-07-08
- **Tasks:** 3
- **Files modified:** 4 (2 created, 2 modified)

## Accomplishments

- `Sidebar.tsx` renders exactly 5 nav items in order (Dashboard, Algorithms, Sensors, Groups, Settings); Dashboard/Algorithms/Settings are disabled (`.argus-sidebar__item--disabled` + native `disabled`), Sensors/Groups are enabled and route via `location.hash = '#/sensors'` / `'#/groups'` (D-02)
- Active nav item derived reactively from the `route` signal (Sensors active on `/sensors`; Groups active on `/groups` or any `/groups/*` sub-route) — no local state duplicated for this
- `AppShell.tsx` rewritten: `<div class="argus-shell">` containing `<Sidebar />` + `<main class="argus-main">{children}</main>`; old `argus-header`/`argus-footer`/inline `maxWidth` all removed (D-01); no placeholder routes added for Dashboard/Algorithms/Settings (D-05)
- Theme toggle lives in `.argus-sidebar__footer`: reads current `data-theme` on mount, and on click sets `document.documentElement`'s `data-theme` attribute, writes `localStorage['argus-theme']`, and flips its own glyph (`☾` when light → click goes dark; `☀` when dark → click goes light) with a descriptive `aria-label`
- `Sidebar.test.tsx` (vitest + `@testing-library/preact`) asserts 5 items render with 3 disabled, and that clicking the toggle twice correctly round-trips `data-theme`/`localStorage['argus-theme']` between `'dark'` and `'light'`

## Task Commits

Each task was committed atomically:

1. **Task 1: Sidebar component (nav items D-02 + active state)** - `0df6797` (feat)
2. **Task 2: AppShell rewrite to Sidebar-based shell (D-01)** - `dc8c3f4` (feat)
3. **Task 3: Theme toggle in Sidebar footer + test (THEME-02)** - `f699612` (feat)

_Note: no TDD tasks in this plan (`tdd` not set on any task) — verified via `tsc -b`, grep-based acceptance criteria, and a vitest unit test for Task 3._

## Files Created/Modified

- `orchestrator/ui/src/components/Sidebar.tsx` - New: nav item list, active-state derivation from `route`, click-to-navigate, theme toggle in footer slot
- `orchestrator/ui/src/components/Sidebar.test.tsx` - New: 5-items/3-disabled render assertion + theme-toggle data-theme/localStorage round-trip test
- `orchestrator/ui/src/components/AppShell.tsx` - Modified: replaced header/footer layout with `.argus-shell` + `Sidebar` + `.argus-main`
- `orchestrator/ui/vitest.config.ts` - Modified: added `execArgv: ['--no-experimental-webstorage']` (Rule 3 fix, see Deviations)

## Decisions Made

- Algorithms and Settings both render the `⚙` glyph — this is the plan's explicit, literal icon assignment (not a copy-paste error); no icon-set exists to give them visually distinct gear-family icons yet
- Theme toggle's initial state is read once from `document.documentElement.getAttribute('data-theme')` (already set synchronously by Plan 10-01's pre-paint `main.tsx` bootstrap) rather than re-reading `localStorage`/`matchMedia` a second time in the component — single source of truth for "what theme is currently active"

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking Issue] Node 22+ built-in Web Storage API shadows jsdom's localStorage in tests**
- **Found during:** Task 3 (writing `Sidebar.test.tsx`)
- **Issue:** Task 3's acceptance criteria requires `Sidebar.test.tsx` to assert `localStorage['argus-theme']` is written by the toggle. Running `npx vitest run src/components/Sidebar.test.tsx` in this worktree's environment (Node v26.3.0) failed both the toggle test and produced an uncaught exception from `Sidebar.tsx`'s own `handleThemeToggle` — `localStorage` (and `window.localStorage`) were `undefined` inside the jsdom test environment. Root cause: Node 22+ ships a built-in, non-configurable global Web Storage API that requires `--localstorage-file` to function; it shadows jsdom's own `window.localStorage` implementation in this Node 26 / vitest 4.1.9 / jsdom 29.1.1 combination, so neither implementation actually works without an explicit flag.
- **Fix:** Added `execArgv: ['--no-experimental-webstorage']` to `orchestrator/ui/vitest.config.ts`'s top-level `test` config (Vitest 4's flattened `poolOptions` — the old `test.poolOptions.forks.execArgv` shape is deprecated in Vitest 4 and logs a warning; the top-level `execArgv` key is the current API). This disables Node's built-in Web Storage global for vitest's worker processes only, so jsdom's per-jsdom-instance `window.localStorage` (backed by an in-memory store, correctly isolated per test file) takes over.
- **Files modified:** `orchestrator/ui/vitest.config.ts`
- **Verification:** `npx vitest run src/components/Sidebar.test.tsx` passes (2/2); full suite `npx vitest run` still passes 92/92 across all 13 test files (no existing test relied on the old, broken localStorage behavior since none of them touched `localStorage` before this plan).
- **Committed in:** `f699612` (Task 3 commit)

---

**Total deviations:** 1 auto-fixed (1 blocking issue, test-infrastructure only — no production code behavior changed)
**Impact on plan:** Necessary for Task 3's `verify` command (`npx vitest run src/components/Sidebar.test.tsx`) to pass at all in this Node version; `Sidebar.tsx`'s production `localStorage` calls are unaffected (they always worked correctly in the real browser — this was purely a test-environment gap).

## Issues Encountered

- `orchestrator/ui/node_modules` was not present in this worktree (git worktrees do not carry gitignored directories from the main checkout, same as noted in 10-01-SUMMARY.md) — ran `npm install` locally before running `tsc -b`/`vitest`. Dev-environment step only, nothing committed for it (`node_modules/` remains gitignored).

## Next Phase Readiness

- Success Criteria #1 ("theme toggle in the sidebar, consistent across all 5 screens") is now structurally verifiable: the Sidebar with all 5 nav items and the theme toggle exists in the running app for both live screens (Sensors, Groups) via the shared `AppShell`
- `Sidebar`/`AppShell` are stable integration points for Phase 11 (Dashboard/Algorithms/Settings routes will simply enable the corresponding nav items and remove their `disabled: true` flag — no Sidebar restructuring needed)
- No blockers for Wave 2 sibling plans or Phase 11-13

## Known Stubs

None — Dashboard/Algorithms/Settings nav items are intentionally disabled placeholders per D-02/D-05 (not stubs masking incomplete work; their routes are explicitly out of scope for this phase and documented as such in the plan).

## Threat Flags

None — this plan's only new surface (Sidebar nav clicks, theme toggle) is fully covered by the plan's own `<threat_model>` (T-10-05-01, T-10-05-02, T-10-05-SC), all accepted with no new mitigations required.

## Self-Check: PASSED

- FOUND: orchestrator/ui/src/components/Sidebar.tsx
- FOUND: orchestrator/ui/src/components/Sidebar.test.tsx
- FOUND: orchestrator/ui/src/components/AppShell.tsx
- FOUND: orchestrator/ui/vitest.config.ts
- FOUND: 0df6797 (Task 1)
- FOUND: dc8c3f4 (Task 2)
- FOUND: f699612 (Task 3)

---
*Phase: 10-design-system-foundation*
*Completed: 2026-07-08*
