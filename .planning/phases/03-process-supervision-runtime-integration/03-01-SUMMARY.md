---
phase: 03-process-supervision-runtime-integration
plan: "01"
subsystem: s6-overlay-services
status: complete
tags: [s6-overlay, home-assistant-addon, grpc-health, process-supervision]
dependency_graph:
  requires: []
  provides: [PROC-01, PROC-02, PROC-03, PROC-04, PROC-05]
  affects: [argus/rootfs/etc/services.d, argus/rootfs/usr/local/bin, argus/config.yaml]
tech_stack:
  added: []
  patterns:
    - s6-overlay v3 longrun (run + finish) per service
    - /run/s6/basedir/bin/halt for container exit on crash
    - gRPC grpc_health.v1 health poller (synchronous, importable predicate)
key_files:
  created:
    - argus/rootfs/etc/services.d/detector/run
    - argus/rootfs/etc/services.d/detector/finish
    - argus/rootfs/etc/services.d/orchestrator/run
    - argus/rootfs/etc/services.d/orchestrator/finish
    - argus/rootfs/usr/local/bin/wait-detector.py
    - detector/tests/test_wait_detector.py
  modified:
    - argus/rootfs/etc/cont-init.d/10-config-gen.sh
    - argus/config.yaml
decisions:
  - "Synchronous grpc (not grpc.aio) in wait-detector.py — async adds no value for a blocking poller"
  - "Module-level check_serving predicate for testability without mock injection"
  - "orchestrator/run reads /run/argus/mode written by config-gen rather than re-reading options.json"
  - "s6-overlay v3 halt: /run/s6/basedir/bin/halt (not deprecated s6-svscanctl -t)"
metrics:
  duration: "4 minutes"
  completed: "2026-06-30"
  tasks_completed: 3
  tasks_total: 3
  files_created: 6
  files_modified: 2
---

# Phase 03 Plan 01: Process Supervision and Runtime Integration Summary

Wire both processes as s6-overlay longrun services with gRPC health gate, crash-halt finish scripts, remote-mode down file, and Supervisor watchdog — satisfying PROC-01..05.

## Tasks Completed

| Task | Description | Commit |
|------|-------------|--------|
| 1 | wait-detector.py health poller + pytest | cc2e987 |
| 2 | s6 run/finish scripts for detector and orchestrator | 5743365 |
| 3 | Remote-mode down file + watchdog declaration | 2d20d49 |

## What Was Built

**wait-detector.py** (`argus/rootfs/usr/local/bin/`): synchronous gRPC health poller exposing `check_serving(addr, service, timeout)` and `wait_until_serving(addr, service, interval, max_attempts)`. Uses `grpc_health.v1.HealthStub` over an insecure channel; returns False on any exception or non-SERVING status. Default service: `argus.v1.DetectorService`.

**detector/run**: exports `PYTHONPATH=/opt/argus/detector`, logs start line, execs `python3 -m argus_detector.server`. No in-script mode check — config-gen's down file handles remote mode at the s6 level.

**orchestrator/run**: reads `/run/argus/mode`; if `local`, invokes `wait-detector.py 127.0.0.1:50051 argus.v1.DetectorService` (blocks until SERVING); if `remote`, skips the poll. Execs `dotnet /opt/argus/orchestrator/Argus.Orchestrator.dll`.

**detector/finish + orchestrator/finish**: log fatal message with exit code `$1`, then `exec /run/s6/basedir/bin/halt` (s6-overlay v3 form). A dying service brings the container down so HA Supervisor reports the failure.

**10-config-gen.sh** (remote branch): replaced Phase 3 placeholder comment with `touch /etc/services.d/detector/down` — tells s6 to leave the local detector stopped when `detector_endpoint` is configured.

**argus/config.yaml**: added `watchdog: "tcp://[HOST]:50051"` so the HA Supervisor monitors the gRPC port as a liveness probe.

## Verification Results

| Check | Result |
|-------|--------|
| `python -m pytest detector/tests/test_wait_detector.py -x -q` | 5 passed |
| `bash -n` on all four service scripts | OK |
| `bash -n` on config-gen.sh | OK |
| `grep` — orchestrator/run contains wait-detector.py | OK |
| `grep` — finish scripts use v3 halt form | OK |
| `grep` — config-gen touches down file in remote branch | OK |
| `grep -Eq` — config.yaml watchdog key | OK |

## Deferred to Live HA (human_needed)

Per `<environment_note>`: the dev box has no Docker, s6, or HA Supervisor. The following checks require a live HA OS instance and are deferred:

- **PROC-01/PROC-02 live**: confirm both services come up on add-on start and orchestrator only begins after detector reports SERVING.
- **PROC-03 live**: kill the detector; confirm the container exits non-zero rather than looping.
- **PROC-04 live**: set `detector_endpoint` to a remote URL, restart add-on, confirm only orchestrator starts (local detector stays down).
- **PROC-05 live**: confirm HA Supervisor respects `watchdog: "tcp://[HOST]:50051"` and restarts a hung add-on.
- **Remote-mode watchdog caveat**: in remote mode no local process listens on port 50051; the watchdog interaction may need revisiting for remote-only deployments (flagged in config.yaml comment, not solved here — local mode is the default per PROJECT.md).

## Deviations from Plan

None — plan executed exactly as written.

## Known Stubs

None.

## Threat Flags

No new security surface beyond the threat model declared in the plan.

## Self-Check: PASSED

Files verified to exist:
- argus/rootfs/etc/services.d/detector/run
- argus/rootfs/etc/services.d/detector/finish
- argus/rootfs/etc/services.d/orchestrator/run
- argus/rootfs/etc/services.d/orchestrator/finish
- argus/rootfs/usr/local/bin/wait-detector.py
- detector/tests/test_wait_detector.py

Commits verified in git log:
- cc2e987: feat(03): add wait-detector.py gRPC health poller and pytest coverage
- 5743365: feat(03): add s6 longrun run/finish scripts for detector and orchestrator
- 2d20d49: feat(03): add remote-mode down file and watchdog declaration
