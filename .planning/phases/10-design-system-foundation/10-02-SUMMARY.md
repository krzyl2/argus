---
phase: 10-design-system-foundation
plan: 02
subsystem: ui
tags: [preact, components, forms, a11y]

# Dependency graph
requires: [10-01]
provides:
  - "Button component: variant primary/secondary/ghost/destructive-ghost, size md/sm/xs, loading spinner, parent-owned label"
  - "Input/Select/Textarea wrappers over .argus-param-field__input / .argus-detector-select / .argus-filters__textarea"
  - "Checkbox wrapper over .argus-checkbox"
  - "SearchInput: debounced, ⌕-glyph wrapper over .argus-search / .argus-search__input, ready for SensorSearchInput to delegate to in Plan 10-06"
affects: [10-03, 10-04, 10-05, 11, 12, 13]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Shared form-control wrappers are pure BEM-class compositions — no inline styles, no owned business-state (arm/confirm, debounce excepted) beyond what's explicitly ported"
    - "SearchInput debounce/ref/unmount-cleanup logic ported verbatim from SensorSearchInput (useRef timer + useEffect cleanup + setTimeout in handleInput) so a later plan can delegate rather than reimplement"

key-files:
  created:
    - orchestrator/ui/src/components/Button.tsx
    - orchestrator/ui/src/components/Button.test.tsx
    - orchestrator/ui/src/components/Input.tsx
    - orchestrator/ui/src/components/Select.tsx
    - orchestrator/ui/src/components/Textarea.tsx
    - orchestrator/ui/src/components/Checkbox.tsx
    - orchestrator/ui/src/components/SearchInput.tsx
  modified: []

key-decisions:
  - "Button: single <button> element, class = `argus-btn argus-btn--{variant} argus-btn--{size}`, native `disabled` = `disabled || loading`; spinner rendered as a leading aria-hidden span when loading — matches Design System Button.jsx API shape without any of its inline-style palette logic"
  - "SearchInput ported the exact debounce/cleanup pattern from SensorSearchInput (useRef + useEffect cleanup + setTimeout) rather than re-deriving it, per plan instruction to keep Plan 10-06's delegation trivial"
  - "Textarea's `mono` prop is declared in TextareaProps per the plan's artifact spec but not consumed in the render — `.argus-filters__textarea` is already monospace from Plan 10-01, so there is nothing conditional to apply; kept the prop for API-shape parity with the Design System spec"

requirements-completed: [COMP-01, A11Y-01]

# Metrics
duration: 20min
completed: 2026-07-08
status: complete
---

# Phase 10 Plan 02: Form Control Components Summary

**Ported six form-control components (Button, Input, Select, Textarea, Checkbox, SearchInput) to Preact as thin wrappers over the `.argus-*` BEM classes added in Plan 10-01, with Button covered by a 5-case unit test and SearchInput's debounce logic ported verbatim from the existing SensorSearchInput.**

## Performance

- **Duration:** ~20 min
- **Completed:** 2026-07-08T09:20:46Z
- **Tasks:** 3
- **Files modified:** 7 (all new)

## Accomplishments

- `Button` exports `Button`/`ButtonProps` with variant (primary/secondary/ghost/destructive-ghost) + size (md/sm/xs) + loading + disabled, composing `argus-btn argus-btn--{variant} argus-btn--{size}`; loading renders `.argus-btn__spinner` and sets native `disabled`; parent owns the arm/confirm label swap (verified against `GroupListRow`'s "Delete group" → "Confirm delete" call site)
- `Button.test.tsx`: 5 passing vitest cases (variant+size class composition, destructive-ghost class, onClick fired once, loading spinner+disabled, exact children label passthrough)
- `Input`/`Select`/`Textarea` wrap `.argus-param-field__input` / `.argus-detector-select` / `.argus-filters__textarea` respectively, forwarding `value`/`onChange` with the same `(e.target as HTMLXxxElement).value` cast pattern used by their real call sites (`DetectorEntry`, `DetectorParamGrid`, `PatternFiltersPanel`)
- `Checkbox` wraps `.argus-checkbox`, mirroring `SensorListRow`'s raw checkbox `checked`/`onChange` cast pattern
- `SearchInput` wraps `.argus-search`/`.argus-search__input` with a leading `aria-hidden` `⌕` glyph span, porting `SensorSearchInput`'s debounce timer + unmount-cleanup `useEffect` verbatim (ready for `SensorSearchInput` to delegate to it in Plan 10-06)
- No component uses inline styles or suppresses the keyboard focus ring — A11Y-01 preserved through to every new wrapper

## Task Commits

Each task was committed atomically:

1. **Task 1: Button component + test (variants, sizes, loading)** - `f5e7c5e` (feat)
2. **Task 2: Input, Select, Textarea wrappers** - `093eaa5` (feat)
3. **Task 3: Checkbox + SearchInput wrappers** - `16181e6` (feat)

## Files Created/Modified

- `orchestrator/ui/src/components/Button.tsx` - `Button`/`ButtonProps`; composes `argus-btn` + variant/size classes, spinner + disabled on loading
- `orchestrator/ui/src/components/Button.test.tsx` - 5 vitest + `@testing-library/preact` cases covering the `<behavior>` spec
- `orchestrator/ui/src/components/Input.tsx` - `Input`/`InputProps`; wraps `.argus-param-field__input`
- `orchestrator/ui/src/components/Select.tsx` - `Select`/`SelectProps`/`SelectOption`; wraps `.argus-detector-select`, maps `options` to `<option>`
- `orchestrator/ui/src/components/Textarea.tsx` - `Textarea`/`TextareaProps`; wraps `.argus-filters__textarea`
- `orchestrator/ui/src/components/Checkbox.tsx` - `Checkbox`/`CheckboxProps`; wraps `.argus-checkbox`
- `orchestrator/ui/src/components/SearchInput.tsx` - `SearchInput`/`SearchInputProps`; debounced wrapper over `.argus-search`/`.argus-search__input` with `⌕` glyph

## Decisions Made

- Button API/markup follows the plan literally (single `<button>`, BEM class string, native `disabled`) rather than the Design System's inline-style `Button.jsx` reference, which was read only for the variant/size *shape*, not its styling approach (plan explicitly forbids inline styles)
- `SearchInput`'s debounce implementation is a verbatim port of `SensorSearchInput`'s logic (same `useRef`/`useEffect`/`setTimeout` shape) rather than a rewrite, to keep Plan 10-06's planned delegation a pure call-through
- `Textarea`'s `mono` prop is part of the exported prop interface (per the plan's artifact spec) but currently a no-op since `.argus-filters__textarea` is unconditionally monospace already

## Deviations from Plan

None — plan executed exactly as written. All acceptance criteria met without needing Rule 1-4 fixes.

## Issues Encountered

- `orchestrator/ui/node_modules` was not present in this fresh worktree (git worktrees do not carry gitignored directories) — ran `npm install` locally before running `tsc -b`/`vitest`. Dev-environment step only, nothing committed for it (`node_modules/` stays gitignored), matching the same note from Plan 10-01's summary.

## Next Phase Readiness

- All 6 form-control primitives Wave 3 (retrofit) and later screens need now exist and compile
- `SearchInput` is ready for `SensorSearchInput` to delegate to in Plan 10-06 without any behavior change
- Full `npx tsc -b` and `npx vitest run` (90/90 tests across 12 files) both pass with zero regressions
- No blockers for the rest of Wave 2 or Wave 3

## Self-Check: PASSED

- FOUND: orchestrator/ui/src/components/Button.tsx
- FOUND: orchestrator/ui/src/components/Button.test.tsx
- FOUND: orchestrator/ui/src/components/Input.tsx
- FOUND: orchestrator/ui/src/components/Select.tsx
- FOUND: orchestrator/ui/src/components/Textarea.tsx
- FOUND: orchestrator/ui/src/components/Checkbox.tsx
- FOUND: orchestrator/ui/src/components/SearchInput.tsx
- FOUND: f5e7c5e (Task 1)
- FOUND: 093eaa5 (Task 2)
- FOUND: 16181e6 (Task 3)

---
*Phase: 10-design-system-foundation*
*Completed: 2026-07-08*
