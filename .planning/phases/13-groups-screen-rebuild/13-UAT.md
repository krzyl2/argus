---
status: testing
phase: 13-groups-screen-rebuild
source: [13-VERIFICATION.md]
started: 2026-07-20T07:13:31Z
updated: 2026-07-20T07:13:31Z
---

## Current Test

number: 1
name: Theme parity — group editor, member picker & algorithm wizard on #/groups
expected: |
  Toggling the theme switch on #/groups (list), #/groups/new and #/groups/:id renders
  Card-wrapped list rows with two Badges, DS page-header + Back affordance,
  Input/Select/Checkbox/Badge member picker, GuidedFlowStep Card+Buttons, and
  AlgorithmCard 2px accent-border selection — all pixel-accurate to the Argus Design
  System spec (ui_kits/admin/Groups.jsx + HANDOFF_TO_CLAUDE_CODE.md) in both light and
  dark themes, with no unstyled/light-leaking regions.
awaiting: user response

## Tests

### 1. Theme parity — group editor, member picker & algorithm wizard on #/groups
expected: Card-wrapped list rows with two Badges, DS page-header + Back affordance, Input/Select/Checkbox/Badge member picker, GuidedFlowStep Card+Buttons, and AlgorithmCard 2px accent-border selection all render pixel-accurate to spec in both light and dark themes, with no unstyled/light-leaking regions.
result: [pending]

### 2. Theme parity — attribution panel (ranked bars + unsupported empty state)
expected: Open a group with a joint verdict — the AttributionBar ranked list shows the top row accent-filled and others neutral. Open a peer/no-attribution group — the unsupported empty state shows the two-line custom .argus-empty message naming the detector. Both match the DS spec visually in light and dark themes.
result: [pending]

### 3. SC2 literal-wording sign-off — SensitivityPresetPicker radio styling (inherited from Phase 10)
expected: Human sign-off on the pre-existing Phase 10 interpretation (10-CONTEXT.md D-03, 10-PATTERNS.md, 10-VERIFICATION.md truth #4) — the 2px accent-border rule is scoped to card-shaped selectors (AlgorithmCard) only; SensitivityPresetPicker's native `<input type=radio accent-color>` satisfies A11Y-02 via the browser's own checked/unchecked affordance, not a bordered radio-card. Phase 13 preserved SensitivityPresetPicker byte-identical (D-07) and did not introduce this ambiguity. Confirm this reading is accepted, OR decide SC2's literal wording requires SensitivityPresetPicker to become a bordered radio-card (would be new scope, likely a follow-up phase).
result: [pending]

## Summary

total: 3
passed: 0
issues: 0
pending: 3
skipped: 0
blocked: 0

## Gaps
