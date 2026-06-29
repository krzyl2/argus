# Phase 1: Foundations + Streaming - Context

**Gathered:** 2026-06-09
**Status:** Ready for planning

<domain>
## Phase Boundary

Deliver the end-to-end streaming path: HA `state_changed` event → gRPC `ScoreStream` (River HST) → MQTT discovery → HA `binary_sensor` + score `sensor` entities, with hysteresis, frozen detection, graceful degradation to `unavailable`, and all scaffolding (mono-repo, proto contract, mTLS, entities.yaml). No batch detection, no InfluxDB, no model persistence in this phase.

</domain>

<decisions>
## Implementation Decisions

### Proto Contract (argus.proto)

- **D-01:** Use `google.protobuf.DoubleValue` wrapper type (not raw `double`) for `score`, `expected`, `lower`, `upper` fields in `Verdict` — proto3 silently drops default value `0.0` on the wire; wrapper makes presence explicit.
  [auto] Q: "How to handle score=0.0 dropped by proto3?" → Selected: DoubleValue wrapper (recommended default)
- **D-02:** Use `google.protobuf.Timestamp` (not `int64`) for all timestamp fields in `Point` and `Verdict`.
- **D-03:** Health RPC uses the standard `grpc.health.v1` proto (not a custom `Health` message) — both `Grpc.AspNetCore.HealthChecks` (.NET) and `grpc-health-probe` (Python) have first-class support.
- **D-04:** `ScoreStream` direction: orchestrator (client) sends `Point` stream → detector (server) streams `Verdict` back. One long-lived bidi stream per entity.
- **D-05:** `FitRequest` carries both detector name and params map, plus the training `Window` — batch training and streaming share the same `Point`/`Window`/`Verdict` message types.

### HA WebSocket Client

- **D-06:** Use `NetDaemon.Client` version **23.46.0** (pinned). This is the last release targeting .NET 8; version 26.x requires .NET 10. Alternative `vicfergar/HassClient` is viable fallback only.
- **D-07:** On HA WebSocket reconnect: call `get_states` API to snapshot current sensor values, then suppress `binary_sensor` state publication for **60 seconds** (cooldown). Do NOT replay the reconnect burst — that triggers false anomaly cascades.

### River HST Streaming Detector

- **D-08:** Per-entity **online min-max normalization** for HST input features. Learn bounds from the stream; clip values to [0,1] until the observed range stabilizes (warm-up period). No static config bounds required.
- **D-09:** River `HalfSpaceTrees` default window size = 250 data points; `n_trees` = 25. These are per-entity overridable from `entities.yaml` params map.

### Hysteresis Gate

- **D-10:** Hysteresis gate lives in the **.NET orchestrator** (not the Python detector). The orchestrator owns the MQTT publish state and is the right place to debounce. The Python detector returns raw scores only.
- **D-11:** Default hysteresis config: `high_threshold: 0.7`, `low_threshold: 0.3`, `min_consecutive: 3` (N readings must exceed threshold before state change). These are per-entity overridable in `entities.yaml`.

### Frozen Sensor Detection

- **D-12:** Rule-based, no ML. Detect when N consecutive readings have variance < ε. Default: `frozen_window: 10`, `frozen_variance_threshold: 0.001`. Implemented in the orchestrator (not detector) since it only requires looking at raw values, not scores.

### MQTT Discovery

- **D-13:** `unique_id` formula: `argus_{entity_slug}_{detector}_{suffix}` where:
  - `entity_slug` = `entity_id` with `.` replaced by `_` (e.g., `sensor_salon_temperatura`)
  - `detector` = detector name (e.g., `hst`, `robust_zscore`)
  - `suffix` = `anomaly` or `score`
  - Example: `argus_sensor_salon_temperatura_hst_anomaly`
- **D-14:** Set `object_id` to the same slug in discovery payload. This prevents HA from mangling Polish characters in the entity_id when deriving it from the `name` field.
- **D-15:** All discovery payloads published with `retain: true`. Availability topic LWT payload = `offline`; online payload = `online`. LWT configured before any state publish.
- **D-16:** Polish friendly-name map: derive from room + sensor type. Pattern: `"[Room] [SensorType] anomalia"` (e.g., `"Salon temperatura anomalia"`). Exact labels resolved when Q1 (entity_ids) is answered.

### Deployment Topology

- **D-17:** **Two-host with mTLS from day one** — even during development. Self-signed certs generated once, stored in `deploy/certs/`, 2-year expiry. GPU host cert includes both LAN IP and hostname as SANs. Validate with `Health` RPC before any streaming work.
  [auto] Q: "Single-host local dev or two-host mTLS from day one?" → Selected: Two-host from day one (recommended — validates cert setup early, avoids works-locally-breaks-in-prod class of bugs)
- **D-18:** Orchestrator uses `HttpClientHandler.ClientCertificates` + custom server cert validation callback for mTLS (not `SslCredentials` with non-null args — that's explicitly unsupported in `Grpc.Net.Client`).

### Mono-Repo Scaffold

- **D-19:** Directory layout per PROJECT.md Section 5.5: `proto/`, `orchestrator/` (.NET 8), `detector/` (Python package), `deploy/`. No deviations.
- **D-20:** .NET project: `Microsoft.Extensions.Hosting` `BackgroundService` worker. Single solution with `Argus.Orchestrator` + `Argus.Orchestrator.Tests` projects.
- **D-21:** Python package: `pyproject.toml` with `grpcio`, `grpcio-tools`, `river`, `pyod`, `numpy`, `pandas`, `pydantic`, `joblib` deps. Phase 1 uses River only; PyOD loaded but not wired until Phase 2.

### Claude's Discretion

- Exact Polly retry policy parameters (initial delay, max retries, jitter) for MQTTnet reconnect — standard exponential backoff with jitter.
- gRPC channel options (keepalive, max message size) — use sensible defaults for LAN; no tuning needed in Phase 1.
- Structured logging format (Serilog vs Microsoft.Extensions.Logging) — use built-in `ILogger<T>` with structured properties; no external logging framework required in Phase 1.
- Python logging configuration — standard `logging` module with JSON formatter for consistency.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Project & Requirements

- `.planning/PROJECT.md` — Architecture decisions D1–D10 (locked), v1 entity set, out-of-scope boundaries
- `.planning/REQUIREMENTS.md` — All 34 v1 requirements; Phase 1 covers INFRA-01–07, CONF-01–03, STRM-01–05, FAULT-01–02, MQTT-01–05, RES-01, RES-03, OBS-01
- `.planning/ROADMAP.md` — Phase 1 success criteria (5 items) — the verifier checks these

### Research Findings

- `.planning/research/STACK.md` — Pinned library versions with rationale (NetDaemon.Client 23.46.0, MQTTnet 5.1.0.1559, Grpc.Net.Client 2.80.0, grpcio 1.81.0, river 0.25.0)
- `.planning/research/ARCHITECTURE.md` — Component map, gRPC bidi streaming .NET gotcha (RequestStream.CompleteAsync must precede read-task await), mTLS setup pattern, MQTT unique_id notes
- `.planning/research/PITFALLS.md` — Top pitfalls for this phase: hysteresis-must-be-phase-1, proto-default-drops, unique_id-instability, mTLS-SAN-mismatch, HA-reconnect-burst

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- None — greenfield project. No existing code.

### Established Patterns
- None — first phase establishes all patterns.

### Integration Points
- HA instance (WebSocket API): authenticate with long-lived access token
- InfluxDB: NOT used in Phase 1 (Phase 2 only)
- MQTT broker (Zigbee2MQTT's broker): publish discovery + state; requires auth credentials (Q: username/password or client cert — to be confirmed)
- GPU host (Python detector): gRPC endpoint over LAN; requires mTLS certs pre-generated

</code_context>

<specifics>
## Specific Ideas

- entities.yaml Section 5.6 of PROJECT.md shows the exact config schema including `covariates` (Phase 2) and `groups` (Phase 2+). Phase 1 parser must support `detectors` and `params` keys; `covariates` and `groups` keys are parsed but ignored with a logged warning.
- The `device` grouping in MQTT discovery: one HA device per source entity (`sensor.salon_temperatura`) grouping its `binary_sensor.argus_..._anomaly` and `sensor.argus_..._score` children. Device `identifiers` field uses the entity_slug.
- Detector service must call `grpc.health.v1.Health/Check` readiness before orchestrator subscribes to HA events.

</specifics>

<deferred>
## Deferred Ideas

- InfluxDB batch ingestion, PyOD detectors, model persistence → Phase 2
- STL seasonal decomposition, covariate conditioning, multivariate groups → v2 phases
- GPU support, ONNX export → Phase 4
- `entities.yaml` hot-reload without restart → v2 (ADV-03)
- Adaptive thresholds / PyThresh → Phase 3+
- CPU-only detector replica on edge host for resilience (R1 mitigation) → decision deferred to Phase 2

None — discussion stayed within phase scope.

</deferred>

---

*Phase: 1-Foundations + Streaming*
*Context gathered: 2026-06-09*
