---
status: testing
phase: 14-unified-detectors-screen-add-detector-wizard
source: [14-VERIFICATION.md]
started: 2026-07-21T18:54:54Z
updated: 2026-07-22T11:42:00Z
---

## Current Test

[testing complete]

## Tests

### 1. Add-detector wizard end-to-end — 1-sensor exit
expected: Lands on /detectors/sensor/<entityId> with the SingleDetectorEditorForm showing that sensor's detector-assignment UI; the sensor appears as a tracked row back on /detectors after saving. (Also confirm the Untrack action lives here in the editor, not on the list row — D-08a.)
result: issue
reported: "dziwne błędy. zapisuję detektor, ale nie pojawia się na liście detektorów. raz się pojawił po zapisie ale po odświeżeniu zniknął."
severity: blocker
diagnosis: |
  Reproduced live (2.1.4) via browser. Saving a single-sensor detector (POST /api/sensors/save)
  returns 200 + "2 entities tracked" (runtime/in-memory), but on full refresh GET /api/sensors
  shows the new sensor isTracked=false AND GET /api/groups returns {"groups":[]} — the pre-existing
  group "Ciśnienie w oponach" was WIPED. Root cause #1 (code-proven, Program.cs:410-414): the
  /api/sensors/save handler rebuilds the ENTIRE entities.yaml root dict as { _patterns, entities }
  with NO "groups" key, then liveCfg.Swap → every sensor save destroys all existing groups on disk
  and in live config. This is the exact inverse of /api/groups/save [handler 8], which correctly
  reads current entities.yaml and replaces only the top-level groups: key. Second symptom (new
  sensor not persisting across refresh) is separate and still needs a debug agent — likely the
  post-Swap HaListenerWorker restart / gen-entities regeneration dropping the manually-selected id.

### 2. Add-detector wizard end-to-end — >=2-sensor exit
expected: From /detectors -> Add detector, select >=2 sensors -> "Create group". Lands on /groups/new with GroupEditorForm's member picker pre-filled with the selected entity ids; the operator can then proceed through the existing AlgorithmChooser -> GuidedFlowStep -> SensitivityPresetPicker flow unmodified.
result: pass

### 3. Visual / Design-System fidelity of the unified list, wizard, and relocated Settings section
expected: Row spacing/rhythm, badge tones, button labels, and section rhythm match the Argus Design System reference (ui_kits/admin/index.html / HANDOFF_TO_CLAUDE_CODE.md); the group-row and sensor-row variants read as ONE consistent list (not two stitched-together lists); the new Settings "Auto-track patterns" section does not disturb the existing three read-only sections; both light and dark themes render with no unstyled regions.
result: skipped
reason: "Requires human design-fidelity judgment against the reference kit. Partially observed via browser: dark-theme /detectors renders cleanly (sidebar, heading, blue Add-detector button, tracked badge, Edit link — no unstyled regions); earlier snapshot showed group-row + sensor-row rendering in one list. Full verdict deferred — the group-row variant needed for list-consistency was wiped by the test-1 data-loss bug (G-14-1), and Settings 'Auto-track patterns' section + light-theme + reference-fidelity were not exhaustively compared."

## Summary

total: 3
passed: 1
issues: 1
pending: 0
skipped: 1
blocked: 0

## Gaps

- gap_id: G-14-1
  truth: "After saving a single-sensor detector, the sensor persists as a tracked row on /detectors across refresh — AND existing groups are NOT destroyed by the save."
  status: failed
  reason: "Reproduced live (2.1.4): single-sensor save wipes the pre-existing group (GET /api/groups -> {groups:[]}) and the new sensor is isTracked=false after refresh, despite a 200 'Saved — 2 entities tracked' response."
  severity: blocker
  test: 1
  artifacts:
    - "orchestrator/Argus.Orchestrator/Program.cs:410-414 — /api/sensors/save builds entities.yaml root as { _patterns, entities } with NO groups key, then liveCfg.Swap()"
    - "orchestrator/Argus.Orchestrator/Program.cs:475-523 — /api/groups/save DOES preserve entities/_patterns by reading current file first (the correct pattern to mirror)"
    - "Program.cs:248-274 GET /api/sensors isTracked=e.IsTracked (registry, updated by Swap); Program.cs:458-473 GET /api/groups reads liveCfg.Get().Groups"
  missing:
    - "/api/sensors/save must read the CURRENT entities.yaml and preserve the top-level groups: key (mirror /api/groups/save's read-modify-write) instead of dropping it"
    - "Second symptom: new manually-selected sensor not durable across refresh — root cause not yet isolated (post-Swap restart / gen-entities regeneration suspected); needs a debug agent"
