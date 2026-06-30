---
status: testing
phase: 03-process-supervision-runtime-integration
source: [03-VERIFICATION.md]
started: 2026-06-30
updated: 2026-06-30
---

## Current Test

number: 1
name: s6 service startup ordering (PROC-01, PROC-02)
expected: |
  Both detector and orchestrator appear as running s6 longrun services. The
  orchestrator log shows "Detector is SERVING — starting orchestrator" before
  any HA event processing begins.
awaiting: user response

## Tests

### 1. s6 service startup ordering (PROC-01, PROC-02)
expected: On a live HA OS instance, starting the add-on container brings up both detector and orchestrator as s6 longrun services; the orchestrator only begins consuming HA events after the detector reports gRPC health SERVING.
result: [pending]

### 2. Exit-on-crash behavior (PROC-03)
expected: Killing the detector process inside the container makes the container exit non-zero immediately (finish script runs /run/s6/basedir/bin/halt); HA Supervisor marks the add-on crashed rather than silently restarting.
result: [pending]

### 3. Remote-mode down file (PROC-04)
expected: Setting detector_endpoint to a remote URL and restarting leaves the local detector process absent; only the orchestrator starts (s6 down file written by config-gen).
result: [pending]

### 4. Supervisor watchdog (PROC-05)
expected: Blocking gRPC port 50051 for up to ~2 min causes HA Supervisor to restart the add-on, honoring watchdog: "tcp://[HOST]:50051" in config.yaml.
result: [pending]

### 5. Live MQTT credential rotation (SUPV-03)
expected: Rotating Mosquitto credentials and waiting for the reconnect cycle (~60s) lets Argus reconnect with new credentials without restarting; no secrets in logs (host:port only); event 4008 emitted.
result: [pending]

### 6. Health entity appears in HA (HEALTH-01)
expected: "Argus — status" (device_class problem) appears in HA via MQTT discovery, state OFF when healthy; flips to ON within ~15s of stopping the detector.
result: [pending]

### 7. Startup sensor discovery log (UICFG-05)
expected: On startup, before detection, the add-on log shows one INFO line per unconfigured numeric HA sensor ("Unconfigured numeric sensor: <entity_id> = <value>") plus a total-count line, sourced from the live HA get_states snapshot.
result: [pending]

## Summary

total: 7
passed: 0
issues: 0
pending: 7
skipped: 0
blocked: 0

## Gaps
