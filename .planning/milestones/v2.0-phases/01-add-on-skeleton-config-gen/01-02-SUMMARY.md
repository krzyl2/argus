---
phase: 01-add-on-skeleton-config-gen
plan: "02"
subsystem: config-gen
tags: [config-gen, gen-entities, cont-init, s6, bashio, SUPV-01, SUPV-02, UICFG-08]
dependency_graph:
  requires: [01-01]
  provides: [argus/rootfs/usr/local/bin/gen-entities.py, argus/rootfs/etc/cont-init.d/10-config-gen.sh, tests/test_gen_entities.py]
  affects: [orchestrator env surface, detector env surface, /data/entities.yaml]
tech_stack:
  added: [PyYAML (host test dep; in-image via darts transitive), bashio services API, s6 container_environment injection]
  patterns: [cont-init.d oneshot, printf-to-s6-env, yaml.dump YAML generation]
key_files:
  created:
    - argus/rootfs/usr/local/bin/gen-entities.py
    - argus/rootfs/etc/cont-init.d/10-config-gen.sh
    - tests/test_gen_entities.py
  modified:
    - .gitignore (negation for argus/rootfs/usr/local/bin/)
decisions:
  - "Use yaml.dump exclusively for YAML generation (T-1-05 YAML injection mitigation)"
  - "ARGUS_HA_URL=ws://supervisor:80 with explicit :80 (ParseHaUrl quirk — Pitfall 1)"
  - "printf not echo for secret writes (T-1-04 information disclosure mitigation)"
  - "Empty entity list exits 0 from gen-entities.py; orchestrator owns hard failure"
  - "Comment text adjusted to not contain HomeAssistant__ substring (structural grep test compliance)"
metrics:
  duration: "~15 minutes"
  completed: "2026-06-29"
  tasks_completed: 3
  tasks_total: 3
  files_created: 3
  files_modified: 1
status: complete
---

# Phase 01 Plan 02: Config-Gen Integration Seam Summary

Config-gen oneshot and entity YAML generator: options.json + SUPERVISOR_TOKEN + bashio MQTT API materialized into the full ARGUS_* s6 env surface and /data/entities.yaml before any service starts.

## Tasks Completed

| # | Task | Commit | Files |
|---|------|--------|-------|
| 1 | gen-entities.py options.json -> entities.yaml generator | 491c287 | argus/rootfs/usr/local/bin/gen-entities.py, .gitignore |
| 2 | 10-config-gen.sh cont-init oneshot env materialization | a9dc97e | argus/rootfs/etc/cont-init.d/10-config-gen.sh |
| 3 | deterministic gen-entities test | edda2b1 | tests/test_gen_entities.py |

## What Was Built

**gen-entities.py** (`argus/rootfs/usr/local/bin/gen-entities.py`): Reads `options.json` (argv[1] or `/data/options.json`), extracts `entities` list, emits YAML matching `EntitiesConfigLoader`'s required structure. Each entity gets `entity_id`, `friendly_name: ""`, and one `hst` detector with `params: {}`. Empty entity list emits `entities: []` and exits 0 — orchestrator fails loud at startup via `Validate()`. Uses `yaml.dump` exclusively for YAML injection safety (T-1-05).

**10-config-gen.sh** (`argus/rootfs/etc/cont-init.d/10-config-gen.sh`): `#!/usr/bin/with-contenv bashio` oneshot committed as mode 100755. Writes the complete ARGUS_* env surface to `/var/run/s6/container_environment/` before any `services.d/` starts. Key behaviors:
- SUPV-01: `ARGUS_HA_URL=ws://supervisor:80` (explicit `:80` required by ParseHaUrl) + `ARGUS_HA_TOKEN` from `$SUPERVISOR_TOKEN`
- SUPV-02: `bashio::services.available "mqtt"` guard with `exit 1` and fatal log if absent; then writes `ARGUS_MQTT_HOST/PORT/USER/PASSWORD` via `bashio::services mqtt`
- Detector mode: local (`http://127.0.0.1:50051`) or remote (mTLS paths); mode recorded at `/run/argus/mode` for Phase 3
- InfluxDB: optional fields written empty when unset (empty URL disables batch in orchestrator)
- Log level: `ARGUS_LOG_LEVEL` uppercased for detector; `Logging__LogLevel__Default` with .NET casing for orchestrator
- UICFG-08: writes `ARGUS_ENTITIES_PATH=/data/entities.yaml`, runs `gen-entities.py`
- All secrets written via `printf` not `echo` (T-1-04)

**tests/test_gen_entities.py**: Two pytest tests covering the output contract:
- `test_gen_entities_minimal`: 2-entity options.json → 2 entities, each with `hst` detector and `params == {}`
- `test_gen_entities_empty`: empty list → `{entities: []}` exit 0

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocker] .gitignore bin/ pattern blocked staging of gen-entities.py**
- **Found during:** Task 1 commit
- **Issue:** Root `.gitignore` has `bin/` which matches `argus/rootfs/usr/local/bin/` anywhere in the tree. `git add` refused without `-f`.
- **Fix:** Added negation lines to `.gitignore`:
  ```
  !argus/rootfs/usr/local/bin/
  !argus/rootfs/usr/local/bin/**
  ```
- **Files modified:** `.gitignore`
- **Commit:** 491c287

**2. [Rule 1 - Bug] Comment text contained `HomeAssistant__` substring**
- **Found during:** Task 2 structural verification
- **Issue:** Plan's verify command asserts `'HomeAssistant__' not in s` on the whole file. A comment "Do NOT write HomeAssistant__* keys" failed this check.
- **Fix:** Rephrased comment to avoid the substring while preserving the intent.
- **Files modified:** `argus/rootfs/etc/cont-init.d/10-config-gen.sh`
- **Commit:** a9dc97e

## Deferred Verification (Live Container)

The following acceptance criteria require a running HA OS / Supervisor environment and cannot be tested on a Windows dev box:

- **Live: MQTT guard path** — running container without Mosquitto add-on must exit non-zero. Deferred to Phase 3 / live HA (bashio unavailable in dev env).
- **Live: entities.yaml on disk** — `/data/entities.yaml` populated before services start. Deferred to Phase 3 integration test.
- **Live: s6 env injection** — `ARGUS_*` vars visible in orchestrator + detector processes. Deferred to Phase 3.

## Known Stubs

None — all logic in this plan is fully implemented. `gen-entities.py` emits real YAML from real input; the bashio/s6 script is complete for in-container execution.

## Threat Surface Scan

No new network endpoints or trust boundaries beyond what the plan's threat model covers. All T-1-04 through T-1-07 mitigations implemented as specified.

## Self-Check: PASSED

- argus/rootfs/usr/local/bin/gen-entities.py: exists, verified
- argus/rootfs/etc/cont-init.d/10-config-gen.sh: exists, mode 100755, bash -n clean
- tests/test_gen_entities.py: exists, pytest passes (2/2)
- Commits: 491c287, a9dc97e, edda2b1 — all present in git log
