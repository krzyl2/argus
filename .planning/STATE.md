---
gsd_state_version: 1.0
milestone: v1.0
milestone_name: milestone
status: milestone-complete
last_updated: "2026-06-10T18:30:00.000Z"
progress:
  total_phases: 2
  completed_phases: 2
  total_plans: 14
  completed_plans: 14
  percent: 100
---

# Project State: Argus

## Current Status

- Phase: Phase 2 COMPLETE (6/6 plans complete)
- Active phase: None — all milestone phases complete
- Last action: Completed 02-06 RES-02 resilience tests + code review fix pass — 2026-06-10

## Project Reference

See: .planning/PROJECT.md (updated 2026-06-09)

**Core value:** Anomalies on v1 environmental sensors appear in HA as live binary_sensor + score entities within 2 seconds.
**Current focus:** Milestone v1 complete — ready for deployment

## Phase Status

| Phase | Name | Status |
|-------|------|--------|
| 1 | Foundations + Streaming | Complete (8/8 plans) |
| 2 | Batch Path + Model Lifecycle | Complete (6/6 plans) |

## Performance Metrics

- Plans completed: 14 (01-01 through 02-06)
- Phases completed: 2 (Phase 1 + Phase 2)
- Requirements covered: 34/34

## Accumulated Context

### Decisions

- .NET 8 orchestrator + Python gRPC detector (locked D2)
- gRPC with mTLS for edge-to-detector transport (locked D4)
- MQTT discovery for HA entity creation (locked D6)
- PyOD MAD + STL + River HST as detection engines
- Per-entity models on detector host disk, joblib (PyOD) / pickle (River) serialization
- entity_id.txt sidecar for unambiguous slug→entity_id reconstruction (CR-02 fix)
- Model store: models/{slug}/{detector}/v{N}/; atomic latest file; retain 3 versions; prune on save
- BatchSchedulerWorker: PeriodicTimer, 10-min default, nightly Fit at hour 2
- Fit RPC saves model internally in Python (no separate SaveModel call from orchestrator)
- threading.Lock per-(entity_id, detector) for MDL-04; train outside lock on deepcopy, atomic swap
- MDL-03 gate: NOT_SERVING before load_all_into; SERVING after
- StlDetector is stateless (no fit); fit_one skips fit for stl detector type (WR-01 fix)
- Mono-repo: proto/, orchestrator/, detector/, deploy/ (locked)
- Phase 1-2 are CPU-only; GPU work is v2 (Phase 4)

### Critical Pitfalls (resolved)

- RobustZScore does NOT exist in PyOD 3.6.0 — use MAD (pyod.models.mad.MAD)
- River HST to_dict/from_dict do NOT exist — use pickle for River model persistence
- STL 24h window (1440 points) always triggers insufficient-history guard (needs 2880 / 48h for daily period)
- google.protobuf wrappers generate as double?/float? in C# with Grpc.Tools 2.80.0 — not as wrapper objects
- IInfluxDataSource and IBatchDetectorClient extracted as seams (no mocking library in test project)
- entity_id.txt sidecar required to avoid lossy slug→entity_id reconstruction

### Open Questions (deferred to deployment)

- Q1: Exact HA entity_ids for entities.yaml (needed for integration testing)
- Q2: InfluxDB location/auth (needed for batch path to actually query)
- Q3: GPU host static LAN IP/hostname (mTLS SAN placeholders in dev certs)
- Q4: MQTT broker auth — username/password or client cert?

### Blockers

- None

## Session Continuity

- Last session: 2026-06-10 (Completed Phase 2 — all 14 milestone plans done)
- Resume point: Deployment configuration (replace placeholder mTLS certs, configure entities.yaml, set ARGUS_INFLUX_* env vars)

---
*Last updated: 2026-06-10 after 02-06 complete — Milestone v1 all 14 plans done, code review clean*
