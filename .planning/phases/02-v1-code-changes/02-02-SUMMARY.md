---
phase: 02-v1-code-changes
plan: "02"
subsystem: detector
tags: [config, grpc, model-store, local-mode]
dependency_graph:
  requires: [02-01]
  provides: [configurable-bind, configurable-model-root]
  affects: [detector/argus_detector/config.py, detector/argus_detector/server.py]
tech_stack:
  added: []
  patterns: [env-var-config, tdd]
key_files:
  modified:
    - detector/argus_detector/config.py
    - detector/argus_detector/server.py
  created:
    - detector/tests/test_local_mode.py
decisions:
  - "grpc_bind default is [[::]] (double-bracket preserved): backward-compatible v1 all-interfaces bind"
  - "model_root passed as pathlib.Path to create_server; MODEL_ROOT constant in model_store.py untouched"
  - "monkeypatch.setenv/delenv used in tests for clean env isolation per test"
metrics:
  duration: "~8 minutes"
  completed: "2026-06-30"
  tasks_completed: 3
  tasks_total: 3
status: complete
requirements: [CODE-02, CODE-03]
---

# Phase 02 Plan 02: Configurable Detector Bind Address and Model Root Summary

**One-liner:** ARGUS_GRPC_BIND and ARGUS_MODEL_ROOT env vars added to DetectorConfig; server.py consumes both with backward-compatible [::] / /var/argus/models defaults.

## What Was Built

Added two new environment-driven config fields to `DetectorConfig` and wired them into `server.py`:

- `ARGUS_GRPC_BIND` → `config.grpc_bind` (default `[::]`) — controls the host portion of both `add_secure_port` and `add_insecure_port` in `create_server`. The add-on sets this to `127.0.0.1` to bind loopback-only.
- `ARGUS_MODEL_ROOT` → `config.model_root` (default `/var/argus/models`) — passed as `pathlib.Path(config.model_root)` to `create_server` via `serve()`, flowing into `ModelStore(root=...)`. `model_store.py` was not modified.

Seven new tests in `test_local_mode.py` cover config defaults, overrides, a functional loopback-bound server health check, and a ModelStore round-trip under a temporary root.

## Tasks

| # | Name | Commit | Files |
|---|------|--------|-------|
| 1 | Add grpc_bind and model_root to DetectorConfig | 89faf9a | detector/argus_detector/config.py |
| 2 | Consume grpc_bind and model_root in server.py | 074219a | detector/argus_detector/server.py |
| 3 | Unit tests for configurable bind and model_root | 6a32a00 | detector/tests/test_local_mode.py |

## Verification

- Inline check (Task 1): default [::] and /var/argus/models; ARGUS_GRPC_BIND=127.0.0.1 and ARGUS_MODEL_ROOT=/data/models overrides — PASSED.
- `test_server_boot.py` (4 tests) — PASSED (backward compatibility confirmed).
- `test_local_mode.py` (7 tests) — PASSED.
- Full suite (112 tests) — PASSED, 8 existing PyOD RuntimeWarnings (pre-existing, unrelated).

## Deviations from Plan

None — plan executed exactly as written.

## Known Stubs

None.

## Threat Flags

None. T-02-04 mitigated: local mode binds 127.0.0.1 via ARGUS_GRPC_BIND, reducing the v1 all-interfaces exposure. T-02-05 accepted: ARGUS_MODEL_ROOT is operator-supplied, same pickle-trust posture as v1.

## Self-Check: PASSED

- detector/argus_detector/config.py — exists, contains ARGUS_GRPC_BIND
- detector/argus_detector/server.py — exists, contains config.grpc_bind
- detector/tests/test_local_mode.py — exists, contains ARGUS_GRPC_BIND
- Commits 89faf9a, 074219a, 6a32a00 — verified in git log
