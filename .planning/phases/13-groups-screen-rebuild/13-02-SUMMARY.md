---
phase: 13-groups-screen-rebuild
plan: 02
subsystem: ui
tags: [preact, design-system, wizard, groups, algorithm-chooser]

# Dependency graph
requires:
  - phase: 10-design-system-foundation
    provides: Card, Button, Input shared primitives + AlgorithmCard (Phase 12 string-prop-widened)
  - phase: 13-groups-screen-rebuild (13-01)
    provides: GroupList/GroupEditorForm/MemberPicker rebuilt to DS spec
provides:
  - GuidedFlowStep restyled to a DS Card + shared Buttons, copy verbatim (Copywriting Contract)
  - AdvancedParamsDisclosure param fields wired through the shared Input (external label pattern)
  - AlgorithmChooser "Algorithm" section-label heading
  - Regression test coverage locking D-03 (no mode-filter on the catalog / guided step) and D-07
    (SensitivityPresetPicker Med-default/isCustomized behavior)
affects: [13-03, groups-screen-verification]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Wizard step restyle: wrap in <Card padding=\"sm\">, answer/skip controls become shared <Button variant=\"secondary\"|\"ghost\">, copy left byte-identical"
    - "Param grid field restyle: external <label for={id}> + shared <Input id={id} type step onChange> replacing raw <input>, matching Phase 12's DetectorParamGrid convention"
    - "Raw-class section label (<p class=\"argus-section-label\">) — no wrapper component, matches Phase 11 convention"

key-files:
  created:
    - orchestrator/ui/src/components/GuidedFlowStep.test.tsx
    - orchestrator/ui/src/components/AdvancedParamsDisclosure.test.tsx
    - orchestrator/ui/src/components/SensitivityPresetPicker.test.tsx
  modified:
    - orchestrator/ui/src/components/GuidedFlowStep.tsx
    - orchestrator/ui/src/components/AdvancedParamsDisclosure.tsx
    - orchestrator/ui/src/components/AlgorithmChooser.tsx
    - orchestrator/ui/src/components/AlgorithmChooser.test.tsx

key-decisions:
  - "AdvancedParamsDisclosure's Input call site drops min/max (Input has no such props, Pitfall 3) — matches the established DetectorParamGrid convention exactly; field set/order/defaults unaffected"
  - "AlgorithmChooser section-label text is \"Algorithm\", matching the DS reference (Groups.jsx SectionLabel) verbatim since D-04 only locks the wizard-step copy, not this heading"
  - "state/groupEditor.ts, state/groups.ts, AlgorithmCard.tsx, Input.tsx, SensitivityPresetPicker.tsx untouched — plan's D-07/Pitfall-3 boundaries held exactly"

patterns-established:
  - "D-03 regression test pattern: render AlgorithmChooser once per draftMode value, skip to manual, assert getAllByRole('radio').length is identical across peer_divergence and joint"

requirements-completed: [GRP-13]

coverage:
  - id: D1
    description: "GuidedFlowStep renders as a DS Card with two answer Buttons + a ghost skip Button, copy byte-identical to the pre-existing verbatim text"
    requirement: "GRP-13"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/GuidedFlowStep.test.tsx#renders inside a Card with the verbatim question, two answer buttons, and a skip button"
        status: pass
      - kind: unit
        ref: "orchestrator/ui/src/components/GuidedFlowStep.test.tsx#clicking the first/second answer / skip"
        status: pass
    human_judgment: false
  - id: D2
    description: "AlgorithmChooser keeps the full unfiltered detector catalog and the guided step available for both peer_divergence and joint draftMode (D-03 regression guard)"
    requirement: "GRP-13"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/AlgorithmChooser.test.tsx#shows the full unfiltered catalog and keeps the guided step available in BOTH peer_divergence and joint modes (D-03 regression guard)"
        status: pass
    human_judgment: false
  - id: D3
    description: "AdvancedParamsDisclosure param fields render via the shared Input (external label), preserving field set/order/defaults and updateParam/draftParams wiring"
    requirement: "GRP-13"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/AdvancedParamsDisclosure.test.tsx#renders each schema field via the shared Input with the correct id and current value"
        status: pass
      - kind: unit
        ref: "orchestrator/ui/src/components/AdvancedParamsDisclosure.test.tsx#editing a field calls updateParam(field.key, value) and lands in draftParams without touching other keys"
        status: pass
    human_judgment: false
  - id: D4
    description: "SensitivityPresetPicker Med-default/isCustomized behavior is unchanged (regression guard, D-07)"
    requirement: "GRP-13"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/SensitivityPresetPicker.test.tsx#defaults to the Med preset and expands its params into the draft"
        status: pass
      - kind: unit
        ref: "orchestrator/ui/src/components/SensitivityPresetPicker.test.tsx#shows the \"Med, customized\" indicator once a param diverges from the active preset"
        status: pass
    human_judgment: false
  - id: D5
    description: "A selected AlgorithmCard shows the 2px accent-border selected state (class-driven), reused from Phase 10/12 without modification"
    requirement: "GRP-13"
    verification: []
    human_judgment: true
    rationale: "AlgorithmCard.tsx was not modified by this plan (Pitfall 3 — reuse as-is); the 2px accent-border visual is a CSS property best confirmed by a human/UAT screenshot pass rather than a unit assertion on class names alone."

# Metrics
duration: 3min
completed: 2026-07-20
status: complete
---

# Phase 13 Plan 02: Algorithm Wizard Restyle Summary

**GuidedFlowStep restyled to DS Card + Button, AdvancedParamsDisclosure param fields moved to shared Input, AlgorithmChooser given an "Algorithm" section label — all around an untouched groupEditor.ts state machine and unfiltered detector catalog (D-03/D-07 regression-tested).**

## Performance

- **Duration:** 3 min (task commits 09:02:42Z - 09:04:21Z)
- **Tasks:** 3
- **Files modified:** 7 (4 production, 3 new test files, 1 test file extended)

## Accomplishments
- `GuidedFlowStep.tsx` now wraps its content in `<Card padding="sm">` and renders its two answers + skip as shared `<Button>` variants, with the Copywriting Contract's copy kept byte-identical
- `AdvancedParamsDisclosure.tsx`'s raw per-field `<input>` replaced by the shared `<Input>` (external `<label>` convention matching Phase 12's `DetectorParamGrid`), with `updateParam`/`draftParams` wiring untouched
- `AlgorithmChooser.tsx` gained a single `<p class="argus-section-label">Algorithm</p>` heading; its `useEffect` draft-sync logic and `AlgorithmCard` grid are byte-identical to before
- New/extended regression coverage locks in D-03 (catalog stays unfiltered + guided step always available across both `draftMode` values) and D-07 (`SensitivityPresetPicker` Med-default/`isCustomized` behavior, previously untested)
- Full test suite (151/151) and `npm run build` both green after all three tasks

## Task Commits

Each task was committed atomically:

1. **Task 1: Restyle GuidedFlowStep to a DS Card + Buttons (copy verbatim)** - `84b6358` (feat)
2. **Task 2: Swap AdvancedParamsDisclosure raw inputs for the shared Input (external label)** - `4d12943` (feat)
3. **Task 3: AlgorithmChooser section-label + D-03 no-mode-filter guard; SensitivityPresetPicker regression test** - `db540ff` (feat)

**Plan metadata:** (this commit, docs)

## Files Created/Modified
- `orchestrator/ui/src/components/GuidedFlowStep.tsx` - restyled to `Card` + shared `Button` controls, copy verbatim
- `orchestrator/ui/src/components/GuidedFlowStep.test.tsx` - new; verbatim copy + callback wiring assertions
- `orchestrator/ui/src/components/AdvancedParamsDisclosure.tsx` - param fields now render via shared `Input`
- `orchestrator/ui/src/components/AdvancedParamsDisclosure.test.tsx` - new; Input-based render + `updateParam` wiring
- `orchestrator/ui/src/components/AlgorithmChooser.tsx` - added `.argus-section-label` heading only
- `orchestrator/ui/src/components/AlgorithmChooser.test.tsx` - extended with the D-03 no-mode-filter regression guard + section-label assertion
- `orchestrator/ui/src/components/SensitivityPresetPicker.test.tsx` - new; regression guard for the unmodified component (Med default, isCustomized, preset re-selection)

## Decisions Made
- `AdvancedParamsDisclosure`'s new `Input` call site omits `min`/`max` (the shared `Input` component has no such props — matches `DetectorParamGrid`'s established convention exactly; field set/order/defaults are unaffected since those constraints are about which fields render, not which HTML attributes decorate them)
- `AlgorithmChooser`'s new section-label text is "Algorithm", copied verbatim from the DS reference's `SectionLabel` usage in `Groups.jsx` (confirmed via grep) — D-04 only locks the wizard-step copy, so this heading text was Claude's discretion, resolved by matching the visual reference
- No changes made to `state/groupEditor.ts`, `state/groups.ts`, `AlgorithmCard.tsx`, `Input.tsx`, or `SensitivityPresetPicker.tsx` — all five were read but confirmed already correct for this phase's needs (Pitfall 3 / D-07)

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- GRP-13 requirement satisfied: wizard steps (guided question -> AlgorithmCard grid -> SensitivityPresetPicker -> AdvancedParamsDisclosure) now render through Design System primitives, with the no-mode-filter (D-03) and unchanged-state-machine (D-07) guarantees regression-tested.
- Manual/UAT verification of the 2px accent-border selected state in both themes remains deferred to `/gsd-verify-work` per the plan's `<verification>` section (D5 above, `human_judgment: true`).
- Ready for 13-03 (attribution panel rebuild, GRP-14) — no shared state or component contract from this plan needs revisiting.

---
*Phase: 13-groups-screen-rebuild*
*Completed: 2026-07-20*

## Self-Check: PASSED
