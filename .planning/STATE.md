---
gsd_state_version: 1.0
milestone: v2.0
milestone_name: Home Assistant Add-on
current_phase: 4
status: executing
last_updated: "2026-06-30T16:10:21.843Z"
last_activity: 2026-06-30
last_activity_desc: Phase 4 complete
progress:
  total_phases: 4
  completed_phases: 4
  total_plans: 10
  completed_plans: 10
  percent: 100
current_phase_name: Multi-Arch CI + Integration + Documentation
---

# Project State: Argus

## Current Status

- Milestone: v2.0 Home Assistant Add-on — roadmap approved (4 phases)
- Phase: 1 (Add-on Skeleton + Config-Gen) — ready to plan
- Last action: Created and approved v2.0 roadmap — 2026-06-29

## Project Reference

See: .planning/PROJECT.md (updated 2026-06-29)

**Core value:** Anomalies appear in HA as live binary_sensor + score entities within 2 seconds.
**Current focus:** Phase 04 — multi-arch-ci-integration-documentation

## Phase Status

| Phase | Name | Status |
|-------|------|--------|
| 1 | Add-on Skeleton + Config-Gen | Not started |
| 2 | v1 Code Changes | Not started |
| 3 | Process Supervision + Runtime Integration | Not started |
| 4 | Multi-Arch CI + Integration + Documentation | Not started |

v1.0 phases archived under `.planning/archive/v1.0/`.

## Accumulated Context

> v1.0 decisions/pitfalls below are retained as historical reference. v2.0 overrides
> (mTLS conditional, single-container default) are recorded in PROJECT.md.

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

- Last session: 2026-06-29 (Archived v1.0, started v2.0 milestone)
- Resume point: Research HA add-on ecosystem → define v2.0 requirements → roadmap

## Current Position

Phase: 4
Plan: Not started
Status: Executing Phase 04
Last activity: 2026-06-30 — Phase 4 complete

---
*Last updated: 2026-06-29 — Milestone v2.0 (Home Assistant Add-on) started*
