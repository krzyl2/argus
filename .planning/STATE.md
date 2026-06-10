---
gsd_state_version: 1.0
milestone: v1.0
milestone_name: milestone
status: phase-1-complete
last_updated: "2026-06-10T14:20:00.000Z"
progress:
  total_phases: 2
  completed_phases: 1
  total_plans: 8
  completed_plans: 8
  percent: 50
---

# Project State: Argus

## Current Status

- Phase: Phase 1 COMPLETE (8/8 plans complete)
- Active phase: 02-batch-model-lifecycle (not started)
- Last action: Completed 01-08 ScoreStreamPipeline end-to-end — 2026-06-10

## Project Reference

See: .planning/PROJECT.md (updated 2026-06-09)

**Core value:** Anomalies on v1 environmental sensors appear in HA as live binary_sensor + score entities within 2 seconds.
**Current focus:** Phase 01 — foundations-streaming

## Phase Status

| Phase | Name | Status |
|-------|------|--------|
| 1 | Foundations + Streaming | Planned (8 plans) |
| 2 | Batch Path + Model Lifecycle | Not started |

## Performance Metrics

- Plans completed: 8 (01-01 through 01-08)
- Phases completed: 1 (Phase 1)
- Requirements covered: 34/34

## Accumulated Context

### Decisions

- .NET 8 orchestrator + Python gRPC detector (locked D2)
- gRPC with mTLS for edge-to-detector transport (locked D4)
- MQTT discovery for HA entity creation (locked D6)
- PyOD + River + Darts as detection engines (locked D10)
- Per-entity models on GPU host disk, joblib/pickle serialization (locked D7)
- Mono-repo: proto/, orchestrator/, detector/, deploy/ (locked)
- Phase 1-2 are CPU-only; GPU work is v2 (Phase 4)
- mTLS certs use placeholder values (GPU_HOST_IP=192.168.1.100, GPU_HOST_NAME=gpu-host) — must regenerate with real values before deployment (01-03)
- One bidi stream per entity for isolation — RpcException on one entity only marks that entity offline (01-08)
- ScoreStreamPipeline: CompleteAsync BEFORE await readTask (PITFALL 3 mitigated)
- binary_sensor suppressed during warm-up (WarmedUp check) and reconnect cooldown (SuppressBinarySensor) — PITFALL 8/D-07

### Open Questions (must resolve before execution)

- Q1: Exact HA entity_ids for entities.yaml and unique_id generation (Phase 1 blocked for integration testing)
- Q2: InfluxDB location, version (v1/v2/v3), and retention (Phase 2 blocked without this)
- Q3: GPU host static LAN IP or hostname (needed for mTLS SAN) — RESOLVED with placeholders for dev; real values needed before deployment
- Q4: MQTT broker auth — username/password or client cert?

### Critical Pitfalls (from research)

- Hysteresis must be Phase 1 — HST scores are uncalibrated; flat threshold oscillates
- proto3 silently drops score 0.0 — use FloatValue wrapper or explicit has_score bool; test in Phase 1
- MQTT unique_id formula must be locked before first publish: argus_{entity_slug}_{detector}_{suffix}
- mTLS SAN must include both GPU host LAN IP and hostname; validate with Health RPC first
- HA WebSocket reconnect: call get_states (not replay burst), suppress binary_sensor for 60s cooldown

### Todos

- Resolve Q1-Q4 before starting Phase 1 plans

### Blockers

- None currently

## Session Continuity

- Last session: 2026-06-10 (Completed 01-08 ScoreStreamPipeline end-to-end)
- Resume point: Phase 2 — 02-batch-model-lifecycle (not yet planned)

---
*Last updated: 2026-06-10 after 01-08 complete — Phase 1 all 8 plans done*
