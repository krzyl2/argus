# Requirements: Argus

**Defined:** 2026-06-09
**Core Value:** Anomalies on v1 environmental sensors appear in HA as live binary_sensor + score entities within 2 seconds, with no manual entity creation.

## v1 Requirements

### Infrastructure

- [ ] **INFRA-01**: Mono-repo scaffolded with proto/, orchestrator/, detector/, deploy/ directories and build tooling wired
- [ ] **INFRA-02**: `proto/argus.proto` finalized; .NET stubs generated via Grpc.Tools; Python stubs generated via grpcio-tools
- [ ] **INFRA-03**: Docker image for .NET 8 orchestrator builds and runs as worker service
- [ ] **INFRA-04**: Docker image for Python gRPC detector builds and runs as gRPC server
- [ ] **INFRA-05**: `docker-compose.edge.yml` (orchestrator) and `docker-compose.gpu.yml` (detector) documented for two-host deploy
- [x] **INFRA-06**: mTLS certs generated; orchestrator and detector communicate over mTLS-secured gRPC
- [ ] **INFRA-07**: Health RPC (`grpc.health.v1`) validated end-to-end before any detection work

### Configuration

- [ ] **CONF-01**: `entities.yaml` schema defined and parsed; adding a new monitored entity requires only a config edit, no code change
- [ ] **CONF-02**: Per-entity detector assignment and params defined in `entities.yaml`
- [ ] **CONF-03**: Connection settings (HA URL/token, InfluxDB, MQTT broker, gRPC endpoint) loaded from environment / secrets; no credentials in source

### Streaming Detection

- [ ] **STRM-01**: HA WebSocket client authenticates, subscribes to `state_changed`, filters to configured entities, reconnects with exponential backoff
- [ ] **STRM-02**: On HA WebSocket reconnect, orchestrator calls `get_states` (not replays burst) and suppresses binary_sensor publication for 60s cooldown
- [ ] **STRM-03**: River Half-Space Trees streaming detector scores each incoming point via `ScoreStream` bidirectional gRPC RPC
- [ ] **STRM-04**: End-to-end streaming latency (HA `state_changed` → MQTT verdict update) is < 2s under normal load
- [ ] **STRM-05**: Orchestrator-layer hysteresis gate (high/low threshold + N-consecutive-reading window) prevents binary_sensor flapping

### Batch Detection

- [ ] **BTCH-01**: InfluxDB reader queries rolling history window per entity (Flux API)
- [ ] **BTCH-02**: PyOD RobustZScore/MAD batch detector scores a window via `ScoreBatch` gRPC RPC
- [ ] **BTCH-03**: Batch scheduler runs on configurable interval (5-15 min) and nightly retraining
- [ ] **BTCH-04**: STL seasonal-residual detector scores a window via `ScoreBatch` (requires ≥2× seasonal period of history)

### Fault Detection Types

- [ ] **FAULT-01**: Point spike anomaly (MAD/RobustZScore) detected and flagged
- [ ] **FAULT-02**: Frozen/stuck sensor (near-zero variance over N readings) detected via rule-based check; no ML required
- [ ] **FAULT-03**: Step change (level shift) detected by batch detector

### Model Lifecycle

- [ ] **MDL-01**: Per-entity model store on GPU host disk; keyed by `entity_id + detector + version`; versioned directory layout with `latest` pointer
- [ ] **MDL-02**: `SaveModel` and `LoadModel` gRPC RPCs implemented; version sidecar metadata (dep versions) stored alongside model file
- [ ] **MDL-03**: On detector service startup, models are loaded before accepting scoring connections
- [ ] **MDL-04**: Concurrent Fit vs ScoreStream access serialized via per-`(entity_id, detector)` lock; train outside lock, swap atomically

### MQTT / HA Integration

- [ ] **MQTT-01**: MQTT discovery config published for each entity: one `binary_sensor` (anomaly flag) + one `sensor` (anomaly score), grouped under one HA `device` per source entity
- [ ] **MQTT-02**: `unique_id` formula is `argus_{entity_slug}_{detector}_{suffix}` — deterministic from config, never random; stable across restarts
- [ ] **MQTT-03**: All discovery payloads published with `retain: true`; LWT `offline` on availability topic before any state is published
- [ ] **MQTT-04**: Discovery publish is idempotent; re-publishing config never duplicates or orphans HA entities
- [ ] **MQTT-05**: Polish friendly-names auto-generated for HA entities per constraint D8

### Resilience

- [ ] **RES-01**: Graceful degradation: if GPU/detector host is unreachable, orchestrator marks affected anomaly sensors `unavailable` (not `off`) via MQTT LWT
- [ ] **RES-02**: Orchestrator, detector, MQTT broker, and HA can each restart independently without losing per-entity model state or duplicating HA entities
- [ ] **RES-03**: Orchestrator health-checks detector before subscribing to HA `state_changed`; re-establishes gRPC channel automatically on reconnect

### Observability

- [ ] **OBS-01**: Structured logs on both orchestrator (.NET) and detector (Python) sides; includes events/s, verdict latency, detector errors

## v2 Requirements

### Seasonality & Covariates

- **SEAS-01**: Darts forecasting model integration for seasonality-aware batch scoring
- **SEAS-02**: Hour-of-day and day-of-week covariates condition per-room sensor models
- **SEAS-03**: Outdoor weather entities (temp, humidity, pressure) used as covariates for indoor room sensors
- **SEAS-04**: Evaluation harness: precision/recall on labeled/synthetic anomalies; per-entity threshold tuning
- **SEAS-05**: Nightly retraining job with operator-gated clean window validation

### Multivariate Groups

- **MULTI-01**: PyOD ECOD detector on `weather_vector` group (outdoor temp + humidity + pressure)
- **MULTI-02**: PyOD Isolation Forest on per-room climate group
- **MULTI-03**: Multivariate group definition in `entities.yaml` (`groups:` section)

### GPU Deep Learning (Phase 4)

- **GPU-01**: PyOD AutoEncoder/VAE or Darts neural model for multivariate signal
- **GPU-02**: CUDA runtime verified; trained weights in model store
- **GPU-03**: Resource guard: GPU detectors run only on GPU host; CPU detectors unaffected; graceful fallback if GPU unavailable
- **GPU-04**: Optional ONNX export for CPU inference

### Advanced Resilience

- **ADV-01**: Optional CPU-only detector replica on edge host for streaming path resilience (R1 mitigation)
- **ADV-02**: Model age sensor exposed in HA; cert rotation procedure documented
- **ADV-03**: `entities.yaml` hot-reload without orchestrator restart

## Out of Scope

| Feature | Reason |
|---------|--------|
| Image/camera anomaly detection (Anomalib) | Out of scope unless camera data added to v1 entity set |
| Acting on anomalies (notifications, automations, TTS) | Argus only exposes entities; operator wires reactions in HA/Node-RED |
| Custom HA dashboards | Auto-created entities are sufficient |
| ML.NET detection | All ML is Python (locked decision D2) |
| Cloud services | Self-hosted only (locked decision D9) |
| Multi-user / remote-access | Single operator use case |
| Real-time alert delivery (Telegram, email) | v2+ if needed; out of scope for anomaly detection system |
| Hand-rolled detector algorithms | Reuse PyOD/River/Darts only (locked decision D10) |
| ADTK library | MPL-2.0 license violation |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| INFRA-01 | Phase 1 | Pending |
| INFRA-02 | Phase 1 | Pending |
| INFRA-03 | Phase 1 | Pending |
| INFRA-04 | Phase 1 | Pending |
| INFRA-05 | Phase 1 | Pending |
| INFRA-06 | Phase 1 | Complete |
| INFRA-07 | Phase 1 | Pending |
| CONF-01 | Phase 1 | Pending |
| CONF-02 | Phase 1 | Pending |
| CONF-03 | Phase 1 | Pending |
| STRM-01 | Phase 1 | Pending |
| STRM-02 | Phase 1 | Pending |
| STRM-03 | Phase 1 | Pending |
| STRM-04 | Phase 1 | Pending |
| STRM-05 | Phase 1 | Pending |
| FAULT-01 | Phase 1 | Pending |
| FAULT-02 | Phase 1 | Pending |
| MQTT-01 | Phase 1 | Pending |
| MQTT-02 | Phase 1 | Pending |
| MQTT-03 | Phase 1 | Pending |
| MQTT-04 | Phase 1 | Pending |
| MQTT-05 | Phase 1 | Pending |
| RES-01 | Phase 1 | Pending |
| RES-03 | Phase 1 | Pending |
| OBS-01 | Phase 1 | Pending |
| BTCH-01 | Phase 2 | Pending |
| BTCH-02 | Phase 2 | Pending |
| BTCH-03 | Phase 2 | Pending |
| BTCH-04 | Phase 2 | Pending |
| FAULT-03 | Phase 2 | Pending |
| MDL-01 | Phase 2 | Pending |
| MDL-02 | Phase 2 | Pending |
| MDL-03 | Phase 2 | Pending |
| MDL-04 | Phase 2 | Pending |
| RES-02 | Phase 2 | Pending |

**Coverage:**
- v1 requirements: 34 total
- Mapped to phases: 34
- Unmapped: 0 ✓

---
*Requirements defined: 2026-06-09*
*Last updated: 2026-06-09 after initial definition*
