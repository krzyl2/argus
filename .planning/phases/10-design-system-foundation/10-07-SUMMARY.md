---
phase: 10-design-system-foundation
plan: 07
subsystem: ui
tags: [preact, button, badge, banner, groups, design-system]

# Dependency graph
requires:
  - phase: 10-02
    provides: "Button component (variant/size API, parent-owned arm/confirm label)"
  - phase: 10-03
    provides: "Badge component (tone-driven pill wrapper)"
  - phase: 10-04
    provides: "Consolidated Banner component (success/error/validation/reloading/info tones, action/onDismiss slots)"
provides:
  - "GroupListRow retrofitted to shared Button (destructive-ghost delete action) + Badge (mode/status pills), two-step arm/confirm delete preserved"
  - "SaveResultBanner, GroupSaveResultBanner, AreaSuggestionBanner consolidated onto shared Banner with copy/branching preserved"
affects: [13]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Retrofit pattern: raw .argus-* markup call sites swap to shared components while callers retain all owned state (arm/confirm timer, dismissedAreas) — matches the stateless-wrapper convention established by Button/Badge/Banner"
    - "Multi-affordance banners (AreaSuggestionBanner) route both action buttons through Banner's single `action` slot as a Fragment, rather than using onDismiss, to preserve exact button copy ('Review'/'Not now') instead of collapsing to Banner's fixed '✕ Dismiss' affordance"

key-files:
  created: []
  modified:
    - orchestrator/ui/src/components/GroupListRow.tsx
    - orchestrator/ui/src/components/SaveResultBanner.tsx
    - orchestrator/ui/src/components/GroupSaveResultBanner.tsx
    - orchestrator/ui/src/components/AreaSuggestionBanner.tsx

key-decisions:
  - "AreaSuggestionBanner's 'Review'/'Not now' buttons were placed in Banner's `action` slot as a Fragment rather than mapping 'Not now' to Banner's `onDismiss` prop — onDismiss renders a fixed '✕' with aria-label 'Dismiss', which would silently reword the 'Not now' copy the plan explicitly requires to stay verbatim"
  - "Banner's role is hardcoded to role=\"status\" for every tone (established in 10-04); the retrofit does not attempt to restore the previous role=\"alert\"/aria-live=\"assertive\" pair for validation/error banners since Banner.tsx is out of this plan's file scope (files_modified) and its author (10-04) explicitly deferred richer role semantics to a future need — SaveResultBanner.test.tsx does not assert role/aria-live so this is a silent, non-regressing behavior narrowing, not a test-covered guarantee"

patterns-established: []

requirements-completed: [COMP-01]

# Metrics
duration: ~12min
completed: 2026-07-08
status: complete
---

# Phase 10 Plan 07: Groups Call-Site Retrofit Summary

**Retrofitted GroupListRow (Button + Badge, two-step arm/confirm delete preserved) and the three Groups-screen banner components (SaveResultBanner, GroupSaveResultBanner, AreaSuggestionBanner) onto the shared Banner, with zero copy or behavior changes and the existing SaveResultBanner test suite passing unmodified.**

## Performance

- **Duration:** ~12 min
- **Tasks:** 2
- **Files modified:** 4

## Accomplishments
- `GroupListRow.tsx` now imports and renders `Button` (`variant="destructive-ghost" size="xs"`) for the delete action, with the row's own `armed`/`timerRef`/`CONFIRM_WINDOW_MS` state driving the `'Confirm delete'` / `'Delete group'` label swap unchanged — no `window.confirm()` introduced
- `GroupListRow.tsx` mode and status pills now render via `Badge` (`tone="neutral"` for mode, `tone="tracked"` for anomaly/active status)
- `SaveResultBanner.tsx` and `GroupSaveResultBanner.tsx` map their `ok`/`kind==='validation'`/error branches to `<Banner tone="success"|"validation"|"error">` respectively, with every message string byte-for-byte unchanged (including the HST warm-up note child in `SaveResultBanner`)
- `AreaSuggestionBanner.tsx` renders `<Banner tone="info">`, moving its "Review"/"Not now" action buttons into Banner's `action` slot while preserving the suggestion copy and the `pendingPrefillMembers`/`dismissedAreas` logic unchanged
- Zero raw `.argus-pill`, `.argus-banner`, or destructive `.argus-btn` markup remains in any of the four retrofitted files (grep-verified)
- `SaveResultBanner.test.tsx` (5 cases) passes unmodified; full suite run (`npx vitest run`) shows 92/92 tests passing across 13 files — no regressions
- `npx tsc -b` exits 0

## Task Commits

Each task was committed atomically:

1. **Task 1: GroupListRow → shared Button + Badge (arm/confirm preserved)** - `0a4ee3b` (feat)
2. **Task 2: Consolidate the three banner components onto shared Banner** - `426c44d` (feat)

## Files Created/Modified
- `orchestrator/ui/src/components/GroupListRow.tsx` - Delete/Edit row now renders `Button`(destructive-ghost, xs) and `Badge`(neutral/tracked) instead of raw `.argus-btn`/`.argus-pill` markup; arm/confirm state untouched
- `orchestrator/ui/src/components/SaveResultBanner.tsx` - success/validation/error branches now render `<Banner tone=...>` instead of raw `.argus-banner` divs; copy and HST warm-up note unchanged
- `orchestrator/ui/src/components/GroupSaveResultBanner.tsx` - same Banner-tone mapping as SaveResultBanner, group-specific copy unchanged
- `orchestrator/ui/src/components/AreaSuggestionBanner.tsx` - renders `<Banner tone="info">` with Review/Not now buttons in the `action` slot; suggestion-finding logic and dismiss-for-session behavior unchanged

## Decisions Made
- AreaSuggestionBanner's two action buttons go into Banner's single `action` slot as a Fragment rather than splitting "Not now" into Banner's `onDismiss` — preserves the exact "Not now" button copy instead of collapsing it into Banner's fixed "✕" dismiss affordance (verbatim-copy requirement takes precedence over maximal slot usage)
- Did not modify `Banner.tsx` to restore per-tone `role="alert"`/`aria-live="assertive"` semantics that `SaveResultBanner`/`GroupSaveResultBanner` previously had for validation/error branches — `Banner.tsx` is outside this plan's `files_modified` scope, its fixed `role="status"` behavior was an explicit, documented decision in the 10-04 plan/summary, and `SaveResultBanner.test.tsx` does not assert role/aria-live (only banner tone class and text content), so no test regression resulted

## Deviations from Plan

None - plan executed exactly as written. Both tasks matched their acceptance criteria without needing Rule 1-4 fixes.

## Issues Encountered
- `orchestrator/ui/node_modules` was absent in this fresh worktree (same known gap noted in prior Phase 10 plan summaries — worktrees don't carry gitignored directories). Ran `npm install` locally before `npx tsc -b`/`npx vitest run`; nothing committed for it, `node_modules/` stays gitignored.

## Next Phase Readiness
- All Groups-screen call sites targeted by this plan now consume the shared Button/Badge/Banner library (D-04); Phase 13 (Groups Screen Rebuild) inherits these retrofitted components as its starting point rather than raw markup
- The `Banner` role="status"-for-all-tones gap (validation/error previously used role="alert"/aria-live="assertive") is a pre-existing 10-04 design decision, not introduced by this plan — flagged here for future awareness in case a later phase adds richer Banner semantics
- No blockers for remaining Phase 10 waves

## Self-Check: PASSED

- FOUND: orchestrator/ui/src/components/GroupListRow.tsx
- FOUND: orchestrator/ui/src/components/SaveResultBanner.tsx
- FOUND: orchestrator/ui/src/components/GroupSaveResultBanner.tsx
- FOUND: orchestrator/ui/src/components/AreaSuggestionBanner.tsx
- FOUND: 0a4ee3b (Task 1)
- FOUND: 426c44d (Task 2)

---
*Phase: 10-design-system-foundation*
*Completed: 2026-07-08*
