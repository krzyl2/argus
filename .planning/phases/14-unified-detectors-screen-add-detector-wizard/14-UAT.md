---
status: testing
phase: 14-unified-detectors-screen-add-detector-wizard
source: [14-VERIFICATION.md]
started: 2026-07-21T18:54:54Z
updated: 2026-07-21T18:54:54Z
---

## Current Test

number: 1
name: Add-detector wizard end-to-end — 1-sensor exit
expected: |
  From /detectors, open "Add detector", search (>=3 chars) and select exactly 1 sensor, click
  "Configure detector". The app navigates to /detectors/sensor/<entityId> and the
  SingleDetectorEditorForm shows that sensor's detector-assignment UI (hst/mad/stl AlgorithmCard +
  DetectorParamGrid). After saving, the sensor appears as a tracked row back on /detectors.
awaiting: user response

## Tests

### 1. Add-detector wizard end-to-end — 1-sensor exit
expected: Lands on /detectors/sensor/<entityId> with the SingleDetectorEditorForm showing that sensor's detector-assignment UI; the sensor appears as a tracked row back on /detectors after saving. (Also confirm the Untrack action lives here in the editor, not on the list row — D-08a.)
result: [pending]

### 2. Add-detector wizard end-to-end — >=2-sensor exit
expected: From /detectors -> Add detector, select >=2 sensors -> "Create group". Lands on /groups/new with GroupEditorForm's member picker pre-filled with the selected entity ids; the operator can then proceed through the existing AlgorithmChooser -> GuidedFlowStep -> SensitivityPresetPicker flow unmodified.
result: [pending]

### 3. Visual / Design-System fidelity of the unified list, wizard, and relocated Settings section
expected: Row spacing/rhythm, badge tones, button labels, and section rhythm match the Argus Design System reference (ui_kits/admin/index.html / HANDOFF_TO_CLAUDE_CODE.md); the group-row and sensor-row variants read as ONE consistent list (not two stitched-together lists); the new Settings "Auto-track patterns" section does not disturb the existing three read-only sections; both light and dark themes render with no unstyled regions.
result: [pending]

## Summary

total: 3
passed: 0
issues: 0
pending: 3
skipped: 0
blocked: 0

## Gaps
