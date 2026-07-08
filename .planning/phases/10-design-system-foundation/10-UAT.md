---
status: testing
phase: 10-design-system-foundation
source: [10-VERIFICATION.md]
started: 2026-07-08T09:50:00Z
updated: 2026-07-08T09:50:00Z
---

## Current Test

number: 1
name: Theme swap completeness + persistence across the 2 live screens
expected: |
  No light-leaking/unstyled region on either Sensors or Groups after toggling to dark;
  theme persists across reload with no flash of the wrong theme before first paint.
awaiting: user response

## Tests

### 1. Theme swap completeness + persistence across the 2 live screens
expected: Load the Sensors screen, click the Sidebar theme toggle, then the Groups screen — every region (sidebar, main content, cards, banners, pills, inputs) swaps to dark values with no light-colored/unstyled region left behind. Reload the page and confirm the dark choice is restored before first paint (no flash of wrong theme).
result: [pending]

### 2. Pixel-accuracy of the 17 shared components vs. Design System spec (both themes)
expected: Each of Button, Input, Select, Checkbox, SearchInput, Textarea, Card, Badge, StatusDot, KpiTile, AttributionBar, Disclosure, Banner, EmptyState, AlgorithmCard, SensitivityPresetPicker, Sidebar visually matches its `Argus Design System/components/*` spec (spacing, color, radius, typography) in light and dark themes.
result: [pending]

### 3. Keyboard focus-ring visibility (A11Y-01)
expected: Tab through the Sensors screen (search input, checkboxes, detector select, param inputs, Save button) and the Groups screen (Delete/Edit buttons, AlgorithmCard/SensitivityPresetPicker in the group wizard) using only the keyboard — every focused element shows a visible 2px accent outline with 2px offset; never invisible on any control.
result: [pending]

### 4. AlgorithmCard selection distinguishable without color (A11Y-02)
expected: In the Groups wizard, select different AlgorithmCard options and confirm the selected card is distinguishable in grayscale/color-blindness simulation (2px vs 1px border thickening, not just an accent-color change).
result: [pending]

## Summary

total: 4
passed: 0
issues: 0
pending: 4
skipped: 0
blocked: 0

## Gaps
