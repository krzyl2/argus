---
status: testing
phase: 04-validation-ci-packaging-documentation
source: [04-VERIFICATION.md]
started: 2026-07-01
updated: 2026-07-01
---

## Current Test

number: 1
name: Visual validation error states in live browser
expected: |
  In a live HA Ingress session, entering an invalid detector parameter turns the field red,
  shows the inline error message, and greys out (disables) the Save button until the input is valid.
awaiting: user response

## Tests

### 1. Visual validation error states in live browser
expected: Invalid param fields turn red, inline error messages appear, Save button greys out; correcting the input clears the error and re-enables Save.
result: [pending]

### 2. HST warm-up disclosure after save
expected: Saving a config containing at least one HST detector shows the ~4-minute warm-up note in the success banner. Non-HST-only saves do not show it.
result: [pending]

### 3. Single reload log line on live Linux host
expected: On real HA-OS/Linux, a single UI save produces exactly one "External edit detected — reloaded" log line ~300ms later (inotify IN_MOVED_TO → Renamed debounce); no double-fire, add-on does not restart.
result: [pending]

### 4. GitHub Actions CI run passing on tag push
expected: On a tag push, the "Assert wwwroot assets present in publish output" step is green, the new "Run unit tests" step passes, and the multi-arch image stays under 2 GB.
result: [pending]

## Summary

total: 4
passed: 0
issues: 0
pending: 4
skipped: 0
blocked: 0

## Gaps
