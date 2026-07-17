---
phase: 12-sensors-screen-rebuild
plan: 01
subsystem: orchestrator-ui-shared-components
tags: [preact, algorithm-card, input, tdd, radio-card, accessibility]
dependency-graph:
  requires: []
  provides:
    - "AlgorithmCard: generic string props (name/bestFor/selected/recommended/onSelect)"
    - "Input: id/step/ariaDescribedby passthrough props"
  affects:
    - orchestrator/ui/src/components/AlgorithmChooser.tsx
    - "Plan 12-03 (Sensors detector editor — composes both widened primitives)"
tech-stack:
  added: []
  patterns:
    - "Shared selection primitive widened to plain-string props; detector-name union narrowed only at the call site (never inside the shared component)"
    - "Additive-optional-props passthrough pattern for extending a shared input wrapper without touching existing consumers"
key-files:
  created:
    - orchestrator/ui/src/components/AlgorithmCard.test.tsx
  modified:
    - orchestrator/ui/src/components/AlgorithmCard.tsx
    - orchestrator/ui/src/components/AlgorithmChooser.tsx
    - orchestrator/ui/src/components/Input.tsx
decisions:
  - "guidedRecommended renamed to recommended on AlgorithmCard (12-RESEARCH.md Assumption A2) — both existing and future (Sensors) callers read equally well with the generic name"
  - "GroupDetectorName cast (`name as GroupDetectorName`) lives only in AlgorithmChooser.tsx's onSelect callback, never inside AlgorithmCard.tsx (Pitfall 3)"
  - "AlgorithmChooser.test.tsx required no changes — it exercises AlgorithmChooser's public behavior, not AlgorithmCard's internal prop shape, so it passed unchanged against the widened component"
metrics:
  duration: "12m"
  completed: 2026-07-17
status: complete
---

# Phase 12 Plan 01: Widen Shared Selection Primitives (AlgorithmCard, Input) Summary

Widened `AlgorithmCard` from a `DetectorCatalogEntry`/`GroupDetectorName`-coupled group-detector
card to a generic plain-string radio-card primitive, and added `id`/`step`/`ariaDescribedby`
passthrough to the shared `Input` — unblocking Plan 12-03's hst/mad/stl detector-type picker and
inline-validated param grid without introducing any new component.

## What Was Built

**Task 1 (TDD): `AlgorithmCard` widened to generic string props**
- `AlgorithmCardProps` changed from `{ entry: DetectorCatalogEntry; selected; guidedRecommended; onSelect: (d: GroupDetectorName) => void }` to `{ name: string; bestFor: string; selected: boolean; recommended: boolean; onSelect: (name: string) => void }`.
- `DetectorCatalogEntry`/`GroupDetectorName` imports removed from `AlgorithmCard.tsx` entirely — the shared component is now detector-catalog-agnostic.
- Markup, class names (`argus-algorithm-card`, `argus-algorithm-card--selected`, `argus-algorithm-card__guided-label`, `__name`, `__best-for`), and the `role="radio"` + `aria-checked={selected}` semantics are unchanged (SC3 regression preserved: selection is border-class-driven, never color alone).
- `AlgorithmChooser.tsx` (the only existing caller) updated: `name={entry.name} bestFor={entry.bestFor} recommended={guidedRecommended.value === entry.name}`, with the narrowing cast `onSelect={(name) => pickAlgorithmManually(name as GroupDetectorName)}` living at the call site, not inside the shared component (Pitfall 3).
- New `AlgorithmCard.test.tsx` (6 tests): plain-string rendering, selected class + `aria-checked`, unselected state, `onSelect(name)` firing, recommended-label present/absent.
- RED confirmed first: all 6 new tests failed against the pre-widening component (`Cannot read properties of undefined (reading 'name')`), then GREEN after the widening — TDD gate sequence honored.

**Task 2: `Input` gains `id`/`step`/`ariaDescribedby` passthrough**
- Three additive optional props on `InputProps`, forwarded 1:1 to the native `<input>` as `id`, `step`, `aria-describedby`.
- No existing prop removed; `SettingsPage.tsx`'s 6 call sites required no changes (Assumption A3, confirmed via `npm run build`).
- Enables Plan 12-03's `DetectorParamGrid` to wire `aria-describedby` (screen-reader link to `FieldValidationError`) and `step` (numeric-spinner increments for threshold fields) through the shared component instead of a raw `<input>`.

## Verification

- `npm run test -- --run src/components/AlgorithmCard.test.tsx src/components/AlgorithmChooser.test.tsx` — 13/13 pass (6 new + 7 existing, unchanged).
- Full suite: `npm run test -- --run` — 98/98 pass across all 14 test files (no regression elsewhere).
- `npm run build` (`tsc -b && vite build`) — green, confirming no type regression across all `Input`/`AlgorithmCard` consumers.

## TDD Gate Compliance

- RED: `ea6bba4` — `test(12-01): add failing test for widened AlgorithmCard props` (confirmed failing before implementation).
- GREEN: `209e2bc` — `feat(12-01): widen AlgorithmCard to generic string props` (confirmed passing after implementation).
- REFACTOR: none needed — implementation was already minimal/clean after GREEN.

## Deviations from Plan

None — plan executed exactly as written. `AlgorithmChooser.test.tsx` needed no edits (plan flagged this as "only as needed"; turned out to be zero changes since it tests public behavior, not the old prop shape).

## Known Stubs

None.

## Threat Flags

None — this plan touches only presentation primitives (no new network endpoint, auth path, file access, or schema change). Matches the plan's own threat register: T-12-01 (accept, pure presentation, server re-validates) and T-12-02 (mitigate via Preact's default JSX auto-escaping, unchanged, no `dangerouslySetInnerHTML` introduced).

## Self-Check: PASSED

- FOUND: orchestrator/ui/src/components/AlgorithmCard.tsx
- FOUND: orchestrator/ui/src/components/AlgorithmChooser.tsx
- FOUND: orchestrator/ui/src/components/AlgorithmCard.test.tsx
- FOUND: orchestrator/ui/src/components/Input.tsx
- FOUND commit ea6bba4 (test)
- FOUND commit 209e2bc (feat)
- FOUND commit a170726 (feat)
