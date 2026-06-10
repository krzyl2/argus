# Roadmap: Argus

**Project:** Argus — Home Assistant Anomaly Detection
**Milestone:** v1
**Granularity:** Coarse
**Coverage:** 34/34 v1 requirements
**Created:** 2026-06-09

---

## Phases

- [x] **Phase 1: Foundations + Streaming** — Scaffold, proto contract, gRPC skeleton with mTLS, HA WebSocket → ScoreStream → MQTT end-to-end streaming path including hysteresis, frozen detection, and graceful degradation
- [ ] **Phase 2: Batch Path + Model Lifecycle** — InfluxDB reader, ScoreBatch/Fit RPCs with PyOD detectors, versioned model store, batch scheduler, STL detection, restart resilience

---

## Phase Details

### Phase 1: Foundations + Streaming
**Goal**: Anomalies on configured environmental sensors appear as live binary_sensor + score entities in HA within 2 seconds, sourced from streaming detection, with no manual entity creation.
**Depends on**: Nothing
**Requirements**: INFRA-01, INFRA-02, INFRA-03, INFRA-04, INFRA-05, INFRA-06, INFRA-07, CONF-01, CONF-02, CONF-03, STRM-01, STRM-02, STRM-03, STRM-04, STRM-05, FAULT-01, FAULT-02, MQTT-01, MQTT-02, MQTT-03, MQTT-04, MQTT-05, RES-01, RES-03, OBS-01
**Success Criteria** (what must be TRUE):
  1. A state_changed event from HA results in a binary_sensor and score sensor update in HA within 2 seconds, end-to-end, with no manual entity creation required
  2. Editing entities.yaml to add a new entity and restarting the orchestrator causes MQTT discovery to auto-create new HA entities with Polish friendly-names and stable unique_ids
  3. Shutting down the detector container causes all anomaly sensors to show as `unavailable` in HA (not `off`); restarting the detector restores them
  4. A sensor sending an identical value repeatedly is flagged as frozen; a single spike value is flagged as anomalous by MAD/RobustZScore point detection
  5. The binary_sensor does not flap on borderline readings — hysteresis prevents rapid on/off toggling
**Plans**: TBD

### Phase 2: Batch Path + Model Lifecycle
**Goal**: Batch detectors score historical sensor windows on a schedule, per-entity models persist across restarts, and all components can restart independently without losing state or duplicating HA entities.
**Depends on**: Phase 1
**Requirements**: BTCH-01, BTCH-02, BTCH-03, BTCH-04, FAULT-03, MDL-01, MDL-02, MDL-03, MDL-04, RES-02
**Success Criteria** (what must be TRUE):
  1. After the batch scheduler runs, PyOD-detected anomaly scores are reflected in the HA score sensor for each configured entity
  2. A step change (level shift) in a sensor's history is detected by the batch detector and surfaces an anomaly flag
  3. Restarting the detector service loads previously trained models from disk before accepting any scoring connections — no cold-start on restart
  4. Restarting the orchestrator re-publishes MQTT discovery without creating duplicate or orphaned HA entities
  5. A Fit RPC call and a concurrent ScoreStream call for the same entity do not corrupt model state — training runs outside the lock and swaps atomically
**Plans**: TBD

---

## Progress

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Foundations + Streaming | 8/8 | Complete | 2026-06-10 |
| 2. Batch Path + Model Lifecycle | 0/0 | Not started | - |

---

*Last updated: 2026-06-10 after 01-08 complete (Phase 1 all 8 plans done)*
