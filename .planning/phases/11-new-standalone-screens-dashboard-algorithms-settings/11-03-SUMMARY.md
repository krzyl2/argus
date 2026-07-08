---
phase: 11-new-standalone-screens-dashboard-algorithms-settings
plan: 03
subsystem: ui
tags: [preact, signals, dashboard, mock-data]

# Dependency graph
requires:
  - phase: 11-new-standalone-screens-dashboard-algorithms-settings
    plan: 02
    provides: DashboardPage skeleton, .argus-dashboard-kpi-row/.argus-dashboard-layout/.argus-section-label CSS classes, hash route wiring
provides:
  - Full DashboardPage body — 4-tile KPI row (2 real counts, 1 derived truthful tile, 1 explicitly-marked mock tile) + mocked Recent anomalies + mocked System health sections
  - state/dashboard.ts — trackedCount/groupCount/loadError signals + loadDashboard() fetching GET /api/sensors + GET /api/groups
affects: []

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "state/dashboard.ts mirrors the state/groups.ts signals-module convention (exported signals + exported async loader, Promise.all for parallel fetches, null-on-failure never a fake zero)"

key-files:
  created:
    - orchestrator/ui/src/state/dashboard.ts
  modified:
    - orchestrator/ui/src/components/DashboardPage.tsx

key-decisions:
  - "Reused the existing .argus-list-row/.argus-row-content/.argus-row-meta classes (from the Sensors screen) for the two mock-section rows instead of inventing new row CSS — the plan's spec (StatusDot + identifier + secondary line + trailing badge, divider on all but last row) is exactly what .argus-list-row already provides (border-bottom + :last-child:none)"
  - "Applied `font-family: var(--font-mono)` inline on the anomaly identifier span since no .argus-mono utility class exists yet in argus.css (the --font-mono token is defined but unused elsewhere in the codebase) — out of scope to add a new utility class for one call site; inline is the minimal, surgical fix"

requirements-completed: [DASH-01, DASH-02, DASH-03]

# Metrics
duration: ~10min
completed: 2026-07-08
status: complete
---

# Phase 11 Plan 03: Dashboard Screen (KPI Row + Mock Sections) Summary

**Fleshed out the Dashboard screen's KPI row (2 real counts via GET /api/sensors + GET /api/groups, 1 truthful derived tile, 1 explicitly-mocked HA tile) plus two clearly-marked mock sections (Recent anomalies, System health) using the exact UI-SPEC datasets — no silent fakes anywhere.**

## Performance

- **Duration:** ~10 min
- **Tasks:** 2 completed
- **Files modified:** 2 (1 created, 1 modified)

## Accomplishments
- `state/dashboard.ts` fetches `/api/sensors` + `/api/groups` in parallel, derives `trackedCount` (filter `isTracked`) and `groupCount` (`groups.length`); any fetch failure leaves both counts `null` (rendered as "—") and sets `loadError` for the error Banner — never a stale/zero value shown as real
- `DashboardPage` renders exactly 4 `KpiTile`s in the UI-SPEC order: Monitored sensors (accent, real), Groups (real), Active group detectors (real, `groups.length` per Resolved Flagged Conflict #1 — no new backend field added), Home Assistant (mock, `hint="mocked — no endpoint yet"`, no `status` prop)
- Recent anomalies and System health sections render below the KPI row inside `.argus-dashboard-layout`, each behind a `Banner tone="info"` containing the word "Mocked", using the exact 5-row datasets from `11-UI-SPEC.md`
- Severity→status mapping (high→error, med→warn, low→idle) drives both the anomaly row's `StatusDot` and its `Badge` tone (error/warn/neutral)
- `npm run build` and `npm test` (92/92) both pass

## Task Commits

Each task was committed atomically:

1. **Task 1: KPI row with real + mock tiles (D-01)** - `4eab1ea` (feat)
2. **Task 2: Recent anomalies + System health mock sections (D-02/D-03)** - `0e9fc11` (feat)

## Files Created/Modified
- `orchestrator/ui/src/state/dashboard.ts` - new: `trackedCount`/`groupCount`/`loadError` signals + `loadDashboard()`
- `orchestrator/ui/src/components/DashboardPage.tsx` - full body: KPI row, error banner, mocked Recent anomalies + System health sections with local mock datasets

## Decisions Made
- Reused `.argus-list-row` family (Sensors screen's existing row CSS) for both mock sections' rows instead of adding new dashboard-specific row classes — the divider/hover/last-child behavior already matches the spec exactly, and it keeps the codebase's row-rendering convention consistent (Rule 11)
- Inline `font-family: var(--font-mono)` on the anomaly identifier only, since no shared mono utility class exists yet anywhere in `argus.css` — adding one for a single call site would be speculative (Rule 2, minimum code)

## Deviations from Plan

None — plan executed exactly as written. The `node_modules` directory was absent in this fresh worktree; ran `npm install` before the first build/test verification (standard environment setup, not a plan deviation).

## Issues Encountered
None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- Dashboard screen (DASH-01/02/03) complete — 4 KPI tiles + 2 mocked sections, matching `11-UI-SPEC.md` exactly
- No blockers for 11-04 (Algorithms) or 11-05 (Settings) — both are independent wave-2 plans against the same 11-02 skeleton

---
*Phase: 11-new-standalone-screens-dashboard-algorithms-settings*
*Completed: 2026-07-08*

## Self-Check: PASSED

All created/modified files verified present on disk (`orchestrator/ui/src/state/dashboard.ts`,
`orchestrator/ui/src/components/DashboardPage.tsx`); both task commit hashes (4eab1ea, 0e9fc11)
verified present in git log.
