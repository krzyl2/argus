---
status: testing
phase: 02-live-sensor-discovery-entity-selection-ui
source: [02-VERIFICATION.md]
started: 2026-07-01
updated: 2026-07-01
---

## Current Test

number: 1
name: Live sensor discovery + htmx search
expected: |
  Opening the picker in HA shows the live numeric sensors from get_states with current
  values + units; the tracked ones show the "tracked" pill; typing in the search box
  filters the list (htmx fragment swap) on entity_id.
awaiting: user response

## Tests

### 1. Live sensor discovery + htmx search
expected: Registry populates from the real get_states snapshot; the picker lists live numeric sensors with values/units; the search box filters the rendered rows; tracked rows show the pill + checked checkbox.
result: [pending]

### 2. Save + patterns + lock file
expected: Selecting entities (and/or entering include/exclude globs) and clicking Save writes /data/entities.yaml (resolved entities + `_patterns:` block) and creates /data/.ui_config_present. Success banner shows the tracked count. Empty selection saves `entities: []` without error.
result: [pending]

### 3. Restart preserves UI-authored config
expected: After a UI save, restarting the add-on does NOT overwrite entities.yaml (10-config-gen.sh skips gen-entities.py because .ui_config_present exists); the orchestrator's tracked entity set reflects the UI selection on the next cycle.
result: [pending]

## Summary

total: 3
passed: 0
issues: 0
pending: 3
skipped: 0
blocked: 0

## Gaps

(none — all items pending live-HA test after add-on rebuild + deploy)

## Note

Live-HA items deferred per the milestone-wide decision (see 01-UAT.md): run all phases' live-HA
checks together after the v3.0 add-on image is rebuilt and deployed. Drive with `/gsd-verify-work 2`.
Depends on Phase 1's Ingress plumbing (01-UAT.md) working live.
