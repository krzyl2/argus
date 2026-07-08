---
phase: 11-new-standalone-screens-dashboard-algorithms-settings
plan: 04
subsystem: ui
tags: [preact, signals, detector-catalog, read-only]

# Dependency graph
requires:
  - phase: 11-02
    provides: AlgorithmsPage skeleton (page header), .argus-page-header/.argus-catalog-param-row CSS classes
provides:
  - state/algorithms.ts (catalog signal + loadError + loadCatalog()) — read-only detector catalog fetch, independent from state/groupEditor.ts's wizard-scoped catalog
  - AlgorithmsPage body — 5 read-only detector cards (name, verbatim best-for, presets badges, param-schema disclosure)
affects: []

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "state/algorithms.ts mirrors state/groups.ts's signal + async-loader convention for a screen-scoped read-only data module, kept deliberately separate from state/groupEditor.ts's catalog signal (which carries the wizard's `guided` field and full DetectorCatalog shape) — two different consumers of the same endpoint, two independent signals"
    - "One-off visual details (mono font-family, lead-size name, presets flex row, grid layout) applied via inline style objects rather than new argus.css classes, since the plan's file scope for this plan was limited to the two .tsx/.ts files listed in frontmatter"

key-files:
  created:
    - orchestrator/ui/src/state/algorithms.ts
  modified:
    - orchestrator/ui/src/components/AlgorithmsPage.tsx

key-decisions:
  - "Grid layout (repeat(auto-fill, minmax(280px, 1fr))) implemented as an inline style on AlgorithmsPage's grid container rather than reusing .argus-algorithm-chooser__grid (which uses a 200px minmax) or adding a new CSS class — keeps the change within the plan's declared file scope (AlgorithmsPage.tsx only, no argus.css edit) while matching the UI-SPEC's exact breakpoint value"
  - "Mono font-family applied via inline `style={{ fontFamily: 'var(--font-mono)' }}` for the detector name and param-schema key, since no existing argus.css utility class applies --font-mono outside one hardcoded textarea rule"

requirements-completed: [ALGO-07, ALGO-08]

# Metrics
duration: ~15min
completed: 2026-07-08
status: complete
---

# Phase 11 Plan 04: Algorithms Screen (Read-Only Detector Catalog) Summary

**Fleshed out the Algorithms screen with a read-only, 5-card browse of the group detector catalog (peer_divergence/ecod/copod/pca/iforest) sourced entirely from `GET /api/detectors/catalog` — name, verbatim "best for" copy, Low/Med/High preset badges, and an expandable parameter-schema disclosure, with zero editing surface.**

## Performance

- **Duration:** ~15 min
- **Tasks:** 2 completed
- **Files modified:** 2 (1 created, 1 modified)

## Accomplishments
- `state/algorithms.ts` fetches the catalog via `apiGet<DetectorCatalog>('api/detectors/catalog')` and exposes `catalog` (`DetectorCatalogEntry[]`) + `loadError` signals, preserving server order verbatim
- `AlgorithmsPage` renders exactly 5 cards in catalog order, each showing the detector name (mono, lead, semibold), the API's `bestFor` copy rendered verbatim (no hardcoded/paraphrased client-side text), a Presets row of `Badge` pills (`"{label}: key=value, ..."` in `paramSchema` key order), and a `Disclosure` with one `.argus-catalog-param-row` per param (`"{type} · {min}–{max} · step {step}"`)
- No `SaveBar`, no editable controls, no "Single sensors" section — matches D-04/D-05's read-only browse constraint, distinct from the in-flow `AlgorithmChooser` wizard

## Task Commits

Each task was committed atomically:

1. **Task 1: Catalog fetch state (ALGO-07)** - `a8d9fae` (feat)
2. **Task 2: Read-only detector cards (ALGO-08, D-04/D-05)** - `2a08837` (feat)

## Files Created/Modified
- `orchestrator/ui/src/state/algorithms.ts` - new: `catalog` signal (`DetectorCatalogEntry[]`) + `loadError` signal + `loadCatalog()` fetching `api/detectors/catalog`
- `orchestrator/ui/src/components/AlgorithmsPage.tsx` - skeleton page header (from 11-02) extended with the 5-card read-only catalog grid, `AlgorithmCatalogCard` sub-component, and `formatPresetBadge`/`formatParamRange` formatting helpers

## Decisions Made
- Kept `state/algorithms.ts` fully independent from `state/groupEditor.ts`'s existing `catalog` signal — the wizard's signal wraps the full `DetectorCatalog` (including the `guided` answer-map, irrelevant here) and is scoped to `AlgorithmChooser`'s state machine; a second, simpler read-only signal avoids coupling this browse-only screen to the wizard's lifecycle
- Grid layout and mono-font styling applied via inline `style` objects on `AlgorithmsPage.tsx` rather than new `argus.css` classes, since the plan's `files_modified` frontmatter scoped this plan to `AlgorithmsPage.tsx` + `state/algorithms.ts` only (no CSS file listed) — 11-02 already landed every CSS class this plan explicitly needed (`.argus-catalog-param-row`), and the two visual details not covered by an existing class (name typography, grid breakpoint) are one-off enough not to warrant a new class

## Deviations from Plan

None - plan executed exactly as written. `npm --prefix orchestrator/ui run build` and the full vitest suite (92/92) both pass after each task's commit.

## Issues Encountered

`orchestrator/ui/node_modules` was not present in this worktree (not tracked in git, and worktrees don't share `node_modules` with the parent checkout) — ran `npm install` before the first build-verify step. Not a plan deviation; standard worktree setup, not committed (node_modules is gitignored).

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- Algorithms screen (`#/algorithms`) is now complete for this milestone; no further work scheduled against it in Phase 11
- 11-03 (Dashboard) and 11-05 (Settings) are independent wave-2 plans against the same 11-02 skeleton — no shared-file conflicts with this plan (this plan touched only `AlgorithmsPage.tsx` and `state/algorithms.ts`)
- No blockers

---
*Phase: 11-new-standalone-screens-dashboard-algorithms-settings*
*Completed: 2026-07-08*
