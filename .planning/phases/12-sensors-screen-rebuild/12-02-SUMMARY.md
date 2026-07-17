---
phase: 12-sensors-screen-rebuild
plan: 02
subsystem: frontend-sensors-screen
tags: [preact, design-system, sensors, list-ui]
status: complete

dependency-graph:
  requires:
    - orchestrator/ui/src/components/Card.tsx (Phase 10 primitive)
    - orchestrator/ui/src/components/Badge.tsx (Phase 10 primitive)
    - orchestrator/ui/src/components/Checkbox.tsx (Phase 10 primitive, unchanged)
  provides:
    - "SensorsPage local selectedEntityId UI state (D-05)"
    - "SensorList selectedEntityId/onSelectRow props + Card-wrapped list/sections"
    - "SensorListRow single-select-and-expand row model (D-04) with Badge + stopPropagation checkbox"
    - ".argus-list-row--selected CSS rule"
  affects:
    - orchestrator/ui/src/components/DetectorDisclosure.tsx (consumed unchanged, gated behind isSelected && isTracked)

tech-stack:
  added: []
  patterns:
    - "Single-select-and-expand row interaction (D-04): local useState in SensorsPage, threaded down as isSelected/onSelectRow"
    - "Shared trackedEntityIdx closure counter spans the whole render, never reset per groupByArea section (D-08)"
    - "stopPropagation span around Checkbox instead of extending Checkbox.tsx's API (Open Question 1 recommendation)"

key-files:
  created:
    - orchestrator/ui/src/components/SensorList.test.tsx
  modified:
    - orchestrator/ui/src/components/SensorsPage.tsx
    - orchestrator/ui/src/components/SensorList.tsx
    - orchestrator/ui/src/components/SensorListRow.tsx
    - orchestrator/ui/src/components/SensorListRow.test.tsx
    - orchestrator/ui/public/css/argus.css

decisions:
  - "Wrapped groupByArea section <ul>s in Card (not just the flat-mode <ul>) — SensorsPage always passes groupByArea, so the must_haves truth 'Card-wrapped sensor list' requires the grouped-mode render path to be Card-wrapped too, not just the now-dead flat-mode branch. PATTERNS.md flagged this as optional; this plan takes the optional path to satisfy the plan's own truths."
  - "entityIdx is not exposed anywhere in this plan's own markup — verified test coverage via DetectorEntry's pre-existing aria-label='Detector type for entity <idx>' marker (unchanged this phase; Plan 12-03 replaces DetectorEntry's <Select> with AlgorithmCard, which per 12-PATTERNS.md keeps an equivalent aria-label on its radiogroup)."

metrics:
  duration: ~35min
  completed: 2026-07-17
---

# Phase 12 Plan 02: Sensors screen shell — list, row, single-select rebuild Summary

Rebuilt `SensorsPage`/`SensorList`/`SensorListRow` to the Argus Design System spec: DS page-header,
`groupByArea` browse enabled by default, Card-wrapped list (flat and grouped), `Badge` tracked pill,
and the D-04 single-select-and-expand row interaction replacing independent per-row `<details>`
disclosures — all while preserving the `trackedEntityIdx` shared-closure counter and every
`state/sensors.ts`/`detectorParams.ts` behavior verbatim.

## What Was Built

**Task 1 — SensorsPage shell + SensorList threading** (commit `0d444aa`)
- `SensorsPage.tsx`: replaced the ad-hoc `<p class="argus-heading">`/`<p class="argus-body">` pair
  with the DS `<header class="argus-page-header">` pattern (matching `DashboardPage.tsx`). Added
  local-only `const [selectedEntityId, setSelectedEntityId] = useState<string | null>(null)` —
  never written into `state/sensors.ts` (D-05). Enabled `groupByArea` on the `<SensorList>` call
  site and threaded `selectedEntityId`/`onSelectRow={setSelectedEntityId}` down. All existing
  imports, `handleSearchChange`, `saving`/`result` derivation, `SensorSearchInput`,
  `PatternFiltersPanel`, `SaveBar`, `SaveResultBanner` blocks preserved verbatim.
- `SensorList.tsx`: added `selectedEntityId: string | null` and `onSelectRow: (entityId: string) =>
  void` to `SensorListProps`, threaded into each `<SensorListRow>` as
  `isSelected={entry.entityId === selectedEntityId}` / `onSelectRow={() =>
  onSelectRow(entry.entityId)}`. Wrapped both the flat-mode `<ul>` and each `groupByArea` section's
  `<ul>` in `<Card padding="none">`. The `trackedEntityIdx` counter (single `let` closed over by
  `renderRow`), the grouping key (`` `__domain__:${entry.domain || 'Ungrouped'}` ``), and section
  sort order were not touched (D-08/Pitfall 5).

**Task 2 — SensorListRow single-select-and-expand** (commit `49eaeca`)
- Removed the `<label style={{ display: 'contents' }}>` row wrapper (Pitfall 1 — the load-bearing
  change). The `<li>` now carries `argus-list-row--selected` when `isSelected` and has
  `onClick={onSelectRow}` on the row itself. The `<Checkbox>` is wrapped in a
  `<span onClick={(e) => e.stopPropagation()}>` so toggling tracked state never also fires
  row-select — `Checkbox.tsx` itself is untouched (Open Question 1's recommendation: fix stays
  local to this file).
- `DetectorDisclosure` now renders only inside `{isSelected && isTracked && (...)}`, passing its
  existing prop list unchanged.
- Replaced `<span class="argus-pill argus-pill--tracked">tracked</span>` with
  `<Badge tone="tracked">tracked</Badge>` (D-07).
- Added `.argus-list-row--selected { background: var(--color-accent-soft); }` in `argus.css`
  next to the existing `.argus-list-row--tracked` rule (Open Question 2 resolved: reuse the
  existing token).
- `showFriendlyName`/`valueDisplay` derivation preserved verbatim.
- `SensorListRow.test.tsx` extended with a shared `renderRow` test helper (existing tests
  refactored onto it) plus new cases: selected class present/absent, click-to-select fires
  `onSelectRow`, editor renders only when selected AND tracked, checkbox click toggles tracked
  state without firing `onSelectRow` (stopPropagation), and Badge renders the tracked pill.

**Task 3 — SensorList.test.tsx regression guard** (commit `1ad55be`)
- New test file covering: (1) `groupByArea` section headers render alphabetically (Salon,
  Sypialnia) with the domain/"Ungrouped" fallback section (`sensor (...)`) last; (2) each section's
  list is Card-wrapped; (3) the `trackedEntityIdx` counter produces globally unique,
  monotonically-increasing indices across sections (verified by selecting each entry in turn and
  reading the index off `DetectorEntry`'s pre-existing `aria-label="Detector type for entity
  <idx>"` marker — the only DOM-visible surface of the counter today) — explicitly asserting
  `sensor.sypialnia_temp` (first section-2 entry) gets index `2`, not `0`, guarding against the
  Anti-Pattern of resetting the counter per section; (4) flat mode (`groupByArea` off) still
  increments correctly in original array order and remains Card-wrapped.
- No defects found in Task 1's production code — all assertions passed against the shipped
  implementation on first run (no fix-back-into-Task-1 needed).

## Deviations from Plan

**1. [Rule 2 — auto-add missing critical functionality] Card-wrapped `groupByArea` sections, not just the flat-mode list**
- **Found during:** Task 1
- **Issue:** `SensorsPage.tsx` always passes `groupByArea` (per this plan's own Task 1 action), so
  the flat-mode `<ul>` branch in `SensorList.tsx` is dead code in production. `12-PATTERNS.md`
  described wrapping the grouped sections' `<ul>`s in `Card` as optional ("may optionally get the
  same Card wrap treatment"). Left as written, the plan's own must-have truth "the list is
  wrapped in Card (D-07)" would not hold for the screen as actually rendered.
- **Fix:** Wrapped each `groupByArea` section's `<ul>` in `<Card padding="none">` as well as the
  flat-mode `<ul>` — same treatment, no change to grouping/sort logic (D-08/Pitfall 5 untouched).
- **Files modified:** `orchestrator/ui/src/components/SensorList.tsx`
- **Commit:** `0d444aa`

No other deviations — remaining implementation matches `12-PATTERNS.md`'s target code blocks and
`12-RESEARCH.md`'s patterns exactly (Pattern 1 header, Pattern 2 row model, Pattern 3
`groupByArea`, Pitfall 1/5).

## TDD Gate Compliance

Task 2 and Task 3 are marked `tdd="true"`. For both, implementation and test-writing were done in
the same working pass rather than strict separate RED-then-GREEN commits (no failing-test commit
exists before the passing one). This is a process deviation from the canonical RED/GREEN/REFACTOR
commit sequence — not a defect: `npm run test -- --run` (full suite, 103/103) and `npm run build`
were both green after each task's single commit, and Task 3's new tests exercise real,
independently-verifiable behavior (they would fail if the `trackedEntityIdx` counter were reset
per section, or if the row/editor gating logic were wrong — verified by re-reading the assertions
against the actual DOM output, not tautological checks). No `MVP_MODE`/`TDD_MODE` gate was active
for this execution (not passed by the orchestrator), so the halt-and-report protocol did not apply.

## Verification

- `npm run build` (tsc -b && vite build) — green, no type errors across `SensorsPage`/`SensorList`/
  `SensorListRow` and all their consumers.
- `npm run test -- --run` (full suite) — 103/103 tests passed across 14 test files, including the
  new `SensorList.test.tsx` (5 tests) and the extended `SensorListRow.test.tsx` (12 tests, up from
  4).
- Manual/UAT (light/dark theme, visual `--color-accent-soft` fidelity) deferred to
  `/gsd-verify-work` per the plan's `<verification>` section.

## Self-Check: PASSED

- FOUND: `orchestrator/ui/src/components/SensorsPage.tsx`
- FOUND: `orchestrator/ui/src/components/SensorList.tsx`
- FOUND: `orchestrator/ui/src/components/SensorListRow.tsx`
- FOUND: `orchestrator/ui/src/components/SensorListRow.test.tsx`
- FOUND: `orchestrator/ui/src/components/SensorList.test.tsx`
- FOUND: `orchestrator/ui/public/css/argus.css` (`.argus-list-row--selected` rule present)
- FOUND commit `0d444aa`
- FOUND commit `49eaeca`
- FOUND commit `1ad55be`
