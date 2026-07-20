---
phase: 13-groups-screen-rebuild
plan: 03
subsystem: ui
tags: [preact, design-system, attribution, groups]

requires:
  - phase: 13-groups-screen-rebuild (13-01)
    provides: Groups list/editor/MemberPicker rebuilt to DS spec
  - phase: 13-groups-screen-rebuild (13-02)
    provides: Algorithm wizard restyled to DS spec
provides:
  - AttributionPanel Card-wrapped 4-state render (loading / no-score / unsupported / ranked), unsupported state via custom .argus-empty markup
  - AttributionBar regression test confirming its already-DS-compliant accent-vs-neutral fill contract
  - Phase-wide regression gate: full build + full vitest suite green with D-07 files unmodified
affects: [groups-screen-verification]

tech-stack:
  added: []
  patterns:
    - "AttributionPanel's 4 early-return branches Card-wrapped; ranked branch gains a raw .argus-section-label heading (Phase 11 raw-class convention)"
    - "Unsupported state rendered via custom .argus-empty markup naming the detector, not the sensor-specific EmptyState (Pitfall 4)"

key-files:
  created:
    - orchestrator/ui/src/components/AttributionBar.test.tsx
  modified:
    - orchestrator/ui/src/components/AttributionPanel.tsx
    - orchestrator/ui/src/components/AttributionPanel.test.tsx

key-decisions:
  - "Updated the unsupported-branch copy from 'This algorithm does not provide per-feature attribution.' to a two-line Card+.argus-empty message ('Attribution not available.' / 'The {detector} detector does not provide per-feature attribution.') naming the detector, matching 13-RESEARCH.md Pattern 5's example structure — the existing test assertion for this branch was updated accordingly"
  - "AttributionBar.tsx received no production change — confirmed by reading argus.css (lines 899-909) that .argus-attribution-bar__fill--top already uses --color-accent, matching the D-05 spec verbatim from its Phase 8 build; only a new regression test file was added"
  - "Did not touch state/groups.ts, state/groupEditor.ts, validation/groupParams.ts, AlgorithmCard.tsx, or Input.tsx (D-07) — confirmed via git log that no phase-13 commit (13-01/13-02/13-03) touches any of these five files"

patterns-established:
  - "Card + raw .argus-section-label wrap around an unchanged poll/state hook (reusable for any future live-polling panel)"

requirements-completed: [GRP-14]

coverage:
  - id: D1
    description: "AttributionPanel's 4 states (loading/no-score/unsupported/ranked) all render Card-wrapped, poll useEffect and contributions.map order/topRank unchanged"
    requirement: "GRP-14"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/AttributionPanel.test.tsx"
        status: pass
    human_judgment: false
  - id: D2
    description: "Unsupported state renders custom .argus-empty markup naming the detector, never the sensor-specific EmptyState"
    requirement: "GRP-14"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/AttributionPanel.test.tsx#renders the honest no-attribution message for pca/iforest"
        status: pass
    human_judgment: false
  - id: D3
    description: "AttributionBar's accent-vs-neutral fill contract (topRank -> fill--top / --color-accent), width-percent calc, and label/value text are regression-covered"
    requirement: "GRP-14"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/AttributionBar.test.tsx"
        status: pass
    human_judgment: false
  - id: D4
    description: "Full frontend build and full vitest suite are green with no D-07 file touched across the whole phase"
    requirement: "GRP-14"
    verification:
      - kind: build
        ref: "npm run build"
        status: pass
      - kind: unit
        ref: "npm run test -- --run (158/158, 25 test files)"
        status: pass
    human_judgment: false
  - id: D5
    description: "Both themes render the rebuilt attribution panel/bars correctly, top row accent-filled (visual sign-off)"
    requirement: "GRP-14"
    verification: []
    human_judgment: true
    rationale: "Visual/theme parity requires human eyeballing in a live browser session against a group with a real joint verdict; not something a unit test can assert."

duration: 4min
completed: 2026-07-20
status: complete
---

# Phase 13 Plan 03: Attribution Panel Restyle Summary

**AttributionPanel's four states Card-wrapped with a DS section label, the unsupported state rebuilt with custom .argus-empty markup naming the detector, AttributionBar confirmed already DS-compliant with new regression coverage — all around the completely unchanged 60s poll / server-pre-sorted logic (D-05), with the phase-wide build + full vitest suite green.**

## Performance

- **Duration:** 4 min
- **Started:** 2026-07-20T07:05:46Z
- **Tasks:** 3
- **Files modified:** 2 (+ 1 new test file)

## Accomplishments
- `AttributionPanel.tsx`'s four early-return branches (loading, no-score, unsupported, ranked) are now `Card`-wrapped; the ranked branch gained a raw `.argus-section-label` reading "Member attribution · last result, refreshes ~60s".
- The unsupported branch (`contributions.length === 0`) now renders custom `.argus-empty` markup naming `status.detector`, replacing the old single-line paragraph — never the sensor-specific `EmptyState` component (grep-confirmed: no `EmptyState` reference in the file).
- The poll `useEffect` (`POLL_INTERVAL_MS`, `apiGet`, `setStatus`/`setLoaded`, interval cleanup) and the `contributions.map` order/`topRank={idx === 0}` derivation are byte-identical — confirmed by diff review, no client-side `.sort(` was added (D-05).
- `AttributionBar.tsx` was read and confirmed already implementing the D-05 accent-vs-neutral fill contract (`argus-attribution-bar__fill--top` uses `--color-accent`, verified directly in `argus.css` lines 899-909) — no production change made, only a new regression test file.
- Full frontend build (`tsc -b && vite build`) and the full vitest suite (158/158 tests, 25 files) are green, with `state/groups.ts`, `state/groupEditor.ts`, `validation/groupParams.ts`, `AlgorithmCard.tsx`, and `Input.tsx` confirmed untouched across the entire phase (13-01, 13-02, 13-03).

## Task Commits

Each task was committed atomically:

1. **Task 1: Wrap AttributionPanel's 4 states in Card + SectionLabel; custom empty for unsupported** - `d647ea0` (feat)
2. **Task 2: Verify/restyle AttributionBar and add its regression test** - `7550f74` (test)
3. **Task 3: Phase regression gate — full build + full vitest suite green** - no code change; verification only (build + full suite both green, no commit required)

## Files Created/Modified
- `orchestrator/ui/src/components/AttributionPanel.tsx` - all 4 render branches Card-wrapped; ranked branch gains section label; unsupported branch rebuilt with custom `.argus-empty` markup naming the detector
- `orchestrator/ui/src/components/AttributionPanel.test.tsx` - extended: Card-wrap assertions for all 4 states, section-label text assertion, no-EmptyState-copy assertion for the unsupported branch, new loading-state test
- `orchestrator/ui/src/components/AttributionBar.test.tsx` - new: accent fill--top toggle on `topRank`, width-percent calc (incl. `topContribution=0` and >100% clamp), label/value text formatting

## Decisions Made
- Updated the unsupported-branch copy to a two-line message naming the detector ("Attribution not available." / "The {detector} detector does not provide per-feature attribution.") rather than keeping the prior single-line text verbatim — this follows 13-RESEARCH.md Pattern 5's documented example structure and satisfies the plan's `<behavior>` requirement to "name the detector"; the pre-existing test assertion for this branch's copy was updated to match.
- Made no production change to `AttributionBar.tsx` — confirmed via direct CSS read that the accent-vs-neutral fill contract already matches the DS spec from its Phase 8 build; only added the missing regression test.
- Left `state/groups.ts`, `state/groupEditor.ts`, `validation/groupParams.ts`, `AlgorithmCard.tsx`, and `Input.tsx` completely untouched — verified via `git log` that no commit across the whole phase (13-01 through 13-03) modifies any of the five (D-07).

## Deviations from Plan

None - plan executed exactly as written. The unsupported-branch copy change (see Decisions above) is within the plan's own `<action>` instructions, not an unplanned deviation.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- GRP-14 requirement satisfied: the attribution panel and its ranked bars render through Design System primitives (Card + section label + custom empty state), with the 60s poll / 4-state / server-pre-sort behavior regression-tested (D-05).
- This was the phase's final wave — Phase 13 (Groups Screen Rebuild) is now functionally complete: GRP-12 (13-01), GRP-13 (13-02), and GRP-14 (13-03) are all implemented and unit-tested, with a green full build + full vitest suite (158/158) proving no cross-plan regression.
- Manual/UAT visual sign-off (both themes, top-row accent fill, unsupported empty state, ~60s refresh cadence) remains deferred to `/gsd-verify-work` per this plan's `<verification>` section (coverage D5 above).

---
*Phase: 13-groups-screen-rebuild*
*Completed: 2026-07-20*

## Self-Check: PASSED

All 3 created/modified files confirmed present on disk; both task commits (d647ea0, 7550f74) confirmed present in git history. Task 3 required no file changes (verification-only task) — full build and full vitest suite (158/158, 25 files) confirmed green.
