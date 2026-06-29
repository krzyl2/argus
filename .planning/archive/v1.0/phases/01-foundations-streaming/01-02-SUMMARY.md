---
phase: 01-foundations-streaming
plan: "02"
subsystem: detector
tags: [grpc, python, health-check, docker, logging, tdd]

requires:
  - "01-01: proto/argus.proto + Python codegen stubs (argus_pb2, argus_pb2_grpc)"

provides:
  - "detector gRPC server skeleton (server.py): Health + DetectorService registered, SERVING"
  - "DetectorServiceServicer (servicer.py): ScoreStream placeholder, context.is_active() guard, structured log"
  - "DetectorRegistry (registry.py): score_one placeholder — stable interface for Plan 06 River HST swap"
  - "DetectorConfig (config.py): env-var config, mTLS auto-detect, no hard-coded secrets"
  - "JSON logging (logging_setup.py): stdlib logging + JSON formatter, ts/level/logger/msg + extra fields"
  - "deploy/Dockerfile.detector: python:3.12-slim-bookworm, proto stubs baked in, HEALTHCHECK"
  - "deploy/docker-compose.gpu.yml: two-host GPU topology, certs volume, mTLS env vars"

affects:
  - "01-03: orchestrator will poll Health/Check before subscribing to HA events"
  - "01-06: Plan 06 wires River HalfSpaceTrees into registry.score_one"
  - "01-08: integration test validates end-to-end gRPC path"

tech-stack:
  added:
    - "grpcio-health-checking 1.81.0 — grpc.health.v1 Python server implementation"
    - "pydantic 2.x — installed (used by config indirectly; config.py uses stdlib os.environ directly)"
    - "python:3.12-slim-bookworm Docker image — detector container base"
  patterns:
    - "TDD: RED commit (test_health.py + test_server_boot.py) → GREEN commit (server/servicer/registry/config/logging_setup)"
    - "create_server(port, tls) factory: decouples test startup from serve() blocking entrypoint"
    - "Health status set AFTER registry init — mirrors Phase 2 contract (models loaded → SERVING)"
    - "context.is_active() guard on every ScoreStream iteration (T-02-02 DoS mitigation)"
    - "JSON structured log: keys ts, level, logger, msg + entity_id, score, latency_ms, detector (T-02-03)"
    - "insecure port for unit tests; add_secure_port with require_client_auth=True when TLS config present (T-02-01)"

key-files:
  created:
    - detector/argus_detector/server.py
    - detector/argus_detector/servicer.py
    - detector/argus_detector/registry.py
    - detector/argus_detector/config.py
    - detector/argus_detector/logging_setup.py
    - detector/tests/test_health.py
    - detector/tests/test_server_boot.py
    - deploy/Dockerfile.detector
    - deploy/docker-compose.gpu.yml
  modified: []

key-decisions:
  - "create_server() factory takes explicit port + tls args for testability; serve() wraps it with env config"
  - "Health set SERVING for both '' (overall) and 'argus.v1.DetectorService' to satisfy all client polling patterns"
  - "HEALTHCHECK uses Python one-liner (grpcio-health-checking already installed) — no grpc-health-probe binary download needed"
  - "docker-compose.gpu.yml build context is repo root (..) so Dockerfile can COPY proto/ and detector/"

metrics:
  duration: "12min"
  completed: "2026-06-10"
  tasks: 2
  files_modified: 9
---

# Phase 01 Plan 02: Detector gRPC Server Skeleton Summary

**Python gRPC server boots and registers grpc.health.v1 Health (SERVING) + DetectorService with ScoreStream placeholder stub; structured JSON logs; ships in python:3.12-slim-bookworm Docker image**

## Performance

- **Duration:** 12 min
- **Started:** 2026-06-10T07:20:00Z
- **Completed:** 2026-06-10T07:32:00Z
- **Tasks:** 2
- **Files modified:** 9

## Accomplishments

- `argus_detector.server` boots a `grpc.server(ThreadPoolExecutor(max_workers=10))`; registers both `grpc.health.v1.Health` and `DetectorService`; sets SERVING on both `""` and `"argus.v1.DetectorService"` after registry init
- `DetectorServicer.ScoreStream` iterates incoming `Point` stream; checks `context.is_active()` on each iteration (T-02-02); yields `Verdict(entity_id, score=DoubleValue(0.0), is_anomaly=False, detector="hst", timestamp)` with structured log
- `DetectorRegistry.score_one` returns `0.0` placeholder with stable interface and `TODO(plan06)` marker
- `DetectorConfig` reads `ARGUS_GRPC_PORT/TLS_CERT/TLS_KEY/TLS_CA/LOG_LEVEL` from environment; no hard-coded secrets (CONF-03)
- `configure_logging()` installs JSON formatter on root logger; each line: `{"ts": ..., "level": ..., "logger": ..., "msg": ..., ...extra}` (OBS-01)
- `create_server(port, tls=False)` factory allows tests to bind an ephemeral port without blocking
- `deploy/Dockerfile.detector`: `python:3.12-slim-bookworm`; pip installs requirements.txt; COPYs proto/ and detector/; runs `gen_proto.py` to bake stubs; `EXPOSE 50051`; `HEALTHCHECK` via Python one-liner; `ENTRYPOINT ["python", "-m", "argus_detector.server"]`
- `deploy/docker-compose.gpu.yml`: two-host GPU topology (D-17); ports `50051:50051`; certs volume; mTLS env vars; `restart: unless-stopped`
- All 15 pytest tests pass (9 from Plan 01 proto codegen + 6 new: 2 Health + 4 ScoreStream)
- Docker image `argus-detector:test` builds successfully from repo root

## Task Commits

1. **Task 1 RED: Failing Health + ScoreStream tests** — `b6fa510` (test)
2. **Task 1 GREEN: Detector server implementation** — `8461428` (feat)
3. **Task 2: Dockerfile + docker-compose.gpu.yml** — `9a81da9` (feat)

## Files Created/Modified

- `detector/argus_detector/server.py` — gRPC server; Health + DetectorService registration; insecure/mTLS branching; create_server() factory; serve() entrypoint
- `detector/argus_detector/servicer.py` — DetectorServicer; ScoreStream with context.is_active() guard; structured log per verdict; Fit stub returning not-implemented
- `detector/argus_detector/registry.py` — DetectorRegistry; score_one(entity_id, value) → 0.0 placeholder; TODO(plan06) marker
- `detector/argus_detector/config.py` — DetectorConfig; env-var reading; mtls_enabled property; no secrets
- `detector/argus_detector/logging_setup.py` — configure_logging(level); JSON formatter; extra fields merged; root handler cleared then reinstalled
- `detector/tests/test_health.py` — 2 tests: Health/Check SERVING for argus.v1.DetectorService + ""
- `detector/tests/test_server_boot.py` — 4 tests: ScoreStream entity_id roundtrip, detector=hst, is_anomaly=False, multiple points
- `deploy/Dockerfile.detector` — full multi-step build; gen_proto.py bakes stubs; HEALTHCHECK
- `deploy/docker-compose.gpu.yml` — GPU-host detector service; certs volume; mTLS env mapping

## Decisions Made

- **create_server() factory vs serve() entrypoint**: Separated blocking `serve()` from testable `create_server(port, tls)`. Tests call `create_server()` directly with ephemeral ports and `tls=False`, never blocking on `wait_for_termination()`.
- **Health set for both "" and "argus.v1.DetectorService"**: The overall-health check (empty service name) is tested separately. Both are set to SERVING so `grpc-health-probe` and orchestrator polling work out of the box.
- **HEALTHCHECK via Python one-liner**: Avoids downloading grpc-health-probe binary. grpcio-health-checking (already a requirement) provides the HealthStub needed for the check. Keeps the image self-contained.
- **docker-compose build context = ".." (repo root)**: The Dockerfile must `COPY proto/` and `COPY detector/` which both live at repo root. Setting context to `..` from `deploy/` directory achieves this without path tricks.

## Deviations from Plan

None — plan executed exactly as written. All acceptance criteria met on first implementation attempt.

## Known Stubs

- `detector/argus_detector/registry.py` — `DetectorRegistry.score_one` always returns `0.0`. This is intentional per plan spec: Plan 06 wires River HalfSpaceTrees here. The `TODO(plan06)` comment marks the swap point. All downstream code (servicer, tests) uses the placeholder score and asserts `is_anomaly=False`, which is correct behavior until Plan 06.

## Threat Flags

No new security-relevant surface beyond what the plan's threat model covers. All STRIDE mitigations applied:
- T-02-01: `add_secure_port` with `require_client_auth=True` when TLS config present; insecure port only when `ARGUS_TLS_*` env vars absent
- T-02-02: `context.is_active()` guard in ScoreStream iteration loop
- T-02-03: Structured log fields limited to `entity_id, score, latency_ms, detector` — no cert paths, no secrets

## Self-Check: PASSED

- `detector/argus_detector/server.py` — exists, contains add_HealthServicer_to_server, add_DetectorServiceServicer_to_server, SERVING, max_workers, add_secure_port
- `detector/argus_detector/servicer.py` — exists, contains DoubleValue, detector="hst"
- `detector/argus_detector/registry.py` — exists
- `detector/argus_detector/config.py` — exists, no hard-coded secrets
- `detector/argus_detector/logging_setup.py` — exists
- `detector/tests/test_health.py` — exists
- `detector/tests/test_server_boot.py` — exists
- `deploy/Dockerfile.detector` — exists, contains python:3.12-slim-bookworm, gen_proto.py, EXPOSE 50051
- `deploy/docker-compose.gpu.yml` — exists, contains 50051:50051, /certs
- Commits b6fa510, 8461428, 9a81da9 — verified in git log
- `python -m pytest detector/tests/` — 15/15 PASSED
- `docker build -f deploy/Dockerfile.detector -t argus-detector:test .` — exit 0
