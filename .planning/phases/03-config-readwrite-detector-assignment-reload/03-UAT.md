---
status: testing
phase: 03-config-readwrite-detector-assignment-reload
source: [03-VERIFICATION.md]
started: 2026-07-01
updated: 2026-07-01
---

## Current Test

number: 1
name: Detector assignment pre-fill via Ingress
expected: |
  Open the Argus UI in live HA. Tracked entities show an expandable "Detectors" section
  pre-filled from /data/entities.yaml (detector type + params); HST shows streaming label,
  MAD/STL show batch labels; typing in search keeps the detector panels.
awaiting: user response

## Tests

### 1. Detector assignment pre-fill via Ingress
expected: Per-entity detector panels pre-fill from entities.yaml (type + params, defaults where absent); Add/Remove detector work; search does not drop the panels.
result: [pending]

### 2. Sub-2-second hot-reload without restart
expected: Change a detector/param and Save. The running pipeline reloads (log shows ConfigReloadTriggered→ConfigReloadComplete) within ~2 s and the new assignment takes effect WITHOUT an add-on restart. MQTT + gRPC stay connected. (MAD/STL changes apply on the next batch cycle.)
result: [pending]

### 3. Removed-entity MQTT retraction within 30 s
expected: Deselect a tracked entity and Save. Within ~30 s its `binary_sensor.argus_*` / `sensor.argus_*` disappear from the HA entity registry (empty retained discovery payloads published). Newly-added entities appear immediately as available (not "unavailable").
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
checks together after the v3.0 add-on image is rebuilt + deployed. Drive with `/gsd-verify-work 3`.
The reload mechanism, retraction, and live entity-filter are unit-proven (221/221 tests); these
items verify wall-clock latency + broker/HA propagation in the real environment.
