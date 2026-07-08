---
phase: 11-new-standalone-screens-dashboard-algorithms-settings
plan: 02
subsystem: ui
tags: [preact, signals, hash-router, css, theme]

# Dependency graph
requires:
  - phase: 10-design-system-foundation
    provides: Shared Preact component library, argus.css token set, Sidebar/AppShell shell, `data-theme`/`localStorage('argus-theme')` mechanism
provides:
  - Hash routes #/dashboard, #/algorithms, #/settings wired into router.ts + main.tsx render switch
  - Sidebar.tsx nav items dashboard/algorithms/settings enabled (href + isActive), no longer disabled placeholders
  - state/theme.ts: shared `theme` signal + `setTheme()` single write path (Sidebar and future Settings Appearance control both read/write this)
  - Skeleton DashboardPage/AlgorithmsPage/SettingsPage components (titled headers only; bodies land in 11-03/04/05)
  - New argus.css composition classes: .argus-page-header(+__title/__subtitle), .argus-section-label, .argus-dashboard-kpi-row, .argus-dashboard-layout, .argus-catalog-param-row, .argus-settings-layout
affects: [11-03-dashboard-screen, 11-04-algorithms-screen, 11-05-settings-screen]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "state/theme.ts mirrors the state/groups.ts signals-module convention (exported signal + exported mutator function) for shared cross-component UI state"
    - "Theme bootstrap (localStorage / prefers-color-scheme -> data-theme) now lives inside state/theme.ts's module-level code instead of main.tsx, guaranteeing correct ES-module evaluation order"

key-files:
  created:
    - orchestrator/ui/src/state/theme.ts
    - orchestrator/ui/src/components/DashboardPage.tsx
    - orchestrator/ui/src/components/AlgorithmsPage.tsx
    - orchestrator/ui/src/components/SettingsPage.tsx
  modified:
    - orchestrator/ui/src/router.ts
    - orchestrator/ui/src/main.tsx
    - orchestrator/ui/src/components/Sidebar.tsx
    - orchestrator/ui/src/components/Sidebar.test.tsx
    - orchestrator/ui/public/css/argus.css

key-decisions:
  - "Created the 3 skeleton page components during Task 1 (not Task 3 as the plan's task boundaries suggested) because main.tsx's render switch (Task 1) needed them to import-resolve, and Task 2's own build-verify step would otherwise fail before Task 3 (which creates them) ever runs"
  - "Moved the pre-render theme bootstrap out of main.tsx into state/theme.ts's module-level code -- ES static imports always fully evaluate before an importing module's own top-level code runs, so main.tsx's textually-later import chain (AppShell -> Sidebar -> state/theme) would execute the theme signal's init read BEFORE main.tsx's own inline bootstrap ran, reading an unset attribute"
  - "Guarded window.matchMedia behind a typeof check in resolveInitialTheme -- jsdom (vitest's test environment) does not implement it, and Sidebar.test.tsx transitively imports state/theme.ts"

patterns-established:
  - "Shared cross-component UI state (beyond page-scoped state/*.ts) lives in its own small signals module with a single write-path function, following state/theme.ts as the second example after state/groups.ts"

requirements-completed: [DASH-01, ALGO-07, SET-01]

# Metrics
duration: ~15min
completed: 2026-07-08
status: complete
---

# Phase 11 Plan 02: Nav/Routing + Shared Theme + Screen Skeletons Summary

**Enabled Dashboard/Algorithms/Settings hash routes and sidebar nav, converted Sidebar's local theme useState into a shared `state/theme.ts` signal, and landed titled skeleton pages plus every new `.argus-*` CSS class the wave-2 screen plans need.**

## Performance

- **Duration:** ~15 min
- **Tasks:** 3 completed
- **Files modified:** 9 (4 created, 5 modified)

## Accomplishments
- All 3 previously-disabled Sidebar nav items (Dashboard, Algorithms, Settings) now navigate to their hash routes; default landing route (`#/sensors`) unchanged
- Theme state unified into one `@preact/signals` source (`state/theme.ts`) so the Sidebar toggle and the future Settings Appearance control (11-05) can never go out of sync
- Three titled skeleton pages render correctly through `main.tsx`'s route switch
- All CSS classes wave-2 plans (11-03/04/05) need already exist in `argus.css` — those plans should not need to touch it

## Task Commits

Each task was committed atomically:

1. **Task 1: Enable nav + routing for the 3 new screens (D-10)** - `194d42b` (feat)
2. **Task 2: Shared theme signal + Sidebar refactor (D-09)** - `73c94ca` (feat)
3. **Task 3: Skeleton page components + new CSS classes** - `ee5677e` (feat)

_Note: Task 1's commit also includes the 3 skeleton page component files — see Deviations._

## Files Created/Modified
- `orchestrator/ui/src/router.ts` - documents the 3 new static routes (no parser change needed)
- `orchestrator/ui/src/main.tsx` - render switch maps `/dashboard`, `/algorithms`, `/settings` to their page components; removed the now-redundant inline theme bootstrap
- `orchestrator/ui/src/components/Sidebar.tsx` - NAV_ITEMS enabled with real hrefs, `isActive` extended, theme read/write moved to the shared signal
- `orchestrator/ui/src/components/Sidebar.test.tsx` - updated nav-item disabled-count assertion (0, was 3)
- `orchestrator/ui/src/state/theme.ts` - new: `theme` signal + `setTheme()`, owns theme bootstrap
- `orchestrator/ui/src/components/DashboardPage.tsx` - skeleton: page header only
- `orchestrator/ui/src/components/AlgorithmsPage.tsx` - skeleton: page header only
- `orchestrator/ui/src/components/SettingsPage.tsx` - skeleton: page header only
- `orchestrator/ui/public/css/argus.css` - `.argus-page-header`(+`__title`/`__subtitle`), `.argus-section-label`, `.argus-dashboard-kpi-row`, `.argus-dashboard-layout`, `.argus-catalog-param-row`, `.argus-settings-layout`

## Decisions Made
- Skeleton page components created one task earlier than the plan's file-boundary assignment (Task 1 instead of Task 3) — required for the import graph to resolve and to keep Task 2's `npm run build` verify step green; Task 3 then finished them with the CSS classes they use
- Theme bootstrap centralized in `state/theme.ts` rather than kept in `main.tsx` — the only way to guarantee the attribute is set before the shared signal reads it, given ES module import-evaluation ordering

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Created skeleton page components during Task 1, not Task 3**
- **Found during:** Task 1 (main.tsx render switch)
- **Issue:** Task 1's action explicitly says to import `DashboardPage`/`AlgorithmsPage`/`SettingsPage` "created in Task 3", but Task 2's own acceptance criteria requires `npm run build` to pass — which would fail with unresolved imports until Task 3 (which the plan places after Task 2) actually creates those files
- **Fix:** Created the 3 skeleton components (exact UI-SPEC title/subtitle copy, `.argus-page-header` markup) as part of Task 1 so main.tsx's imports resolve and every subsequent task's build stays green; Task 3 added only the CSS classes those components already reference
- **Files modified:** orchestrator/ui/src/components/DashboardPage.tsx, AlgorithmsPage.tsx, SettingsPage.tsx (created), orchestrator/ui/src/main.tsx
- **Verification:** `npm run build` passes after every task's commit; `npm test -- --run` (92/92) passes
- **Committed in:** 194d42b (Task 1 commit)

**2. [Rule 1 - Bug] Theme bootstrap moved from main.tsx into state/theme.ts**
- **Found during:** Task 2 (shared theme signal)
- **Issue:** The plan's Task 2 action says to leave main.tsx's inline bootstrap "intact" and only read the already-applied `data-theme` attribute in the new signal's initializer. But ES module static imports always fully evaluate (including all transitive imports) before the importing module's own top-level code runs — main.tsx's inline bootstrap sits textually after its `import { AppShell }` statement, so the `AppShell -> Sidebar -> state/theme` import chain (which evaluates the theme signal's initializer) always runs BEFORE main.tsx's own bootstrap code, regardless of source-line order. The signal would have permanently initialized to `'light'` regardless of the user's stored preference.
- **Fix:** Moved the bootstrap resolution (localStorage → prefers-color-scheme → data-theme attribute) into `state/theme.ts`'s module-level code, which runs as a side effect of the same import chain, guaranteeing the attribute is set before the signal reads it. Removed the now-dead bootstrap block from main.tsx.
- **Files modified:** orchestrator/ui/src/state/theme.ts, orchestrator/ui/src/main.tsx
- **Verification:** `npm run build` passes; manual trace of import evaluation order confirmed correct sequencing
- **Committed in:** 73c94ca (Task 2 commit)

**3. [Rule 3 - Blocking] Guarded window.matchMedia for the jsdom test environment**
- **Found during:** Task 2 (shared theme signal)
- **Issue:** `Sidebar.test.tsx` transitively imports `state/theme.ts`, whose bootstrap calls `window.matchMedia(...)`. jsdom (vitest's `environment: 'jsdom'`) does not implement `matchMedia`, throwing `TypeError: window.matchMedia is not a function` and failing the whole test file.
- **Fix:** Added a `typeof window.matchMedia === 'function'` guard before calling it; falls back to `'light'` in environments without it (test-only path — real browsers always implement it).
- **Files modified:** orchestrator/ui/src/state/theme.ts
- **Verification:** `npm test -- --run` — 13 files / 92 tests passed
- **Committed in:** 73c94ca (Task 2 commit)

---

**Total deviations:** 3 auto-fixed (2 blocking, 1 bug)
**Impact on plan:** All 3 were necessary for correctness (theme would silently ignore saved preference) or to keep the build/test suite green at every task boundary, exactly as the plan's own per-task verify steps require. No scope creep — no new files/behavior beyond what the plan already specified.

## Issues Encountered
None beyond the deviations above.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- 11-03 (Dashboard), 11-04 (Algorithms), 11-05 (Settings) can now build their screen bodies purely in their own `.tsx` files — routing, shared theme state, and every composition CSS class they need already exist
- No blockers

---
*Phase: 11-new-standalone-screens-dashboard-algorithms-settings*
*Completed: 2026-07-08*

## Self-Check: PASSED

All created files verified present on disk; all 4 task/summary commit hashes (194d42b, 73c94ca, ee5677e, 93652c5) verified present in git log.
