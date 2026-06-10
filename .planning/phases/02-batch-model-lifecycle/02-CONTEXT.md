# Phase 2: Batch Path + Model Lifecycle - Context

**Gathered:** 2026-06-10
**Status:** Ready for planning

<domain>
## Phase Boundary

Deliver the batch detection path and model lifecycle: InfluxDB history reader, ScoreBatch/Fit RPCs (PyOD RobustZScore/MAD + STL), versioned per-entity model store on the detector host, nightly retraining scheduler, SaveModel/LoadModel RPCs, and restart resilience (RES-02). No streaming path changes, no GPU, no Darts covariates.

The orchestrator owns scheduling and InfluxDB reads; the Python detector owns model storage, Fit/ScoreBatch RPC implementation, and per-entity locking.

</domain>

<decisions>
## Implementation Decisions

### InfluxDB Integration

- Use InfluxDB v2 with Flux API via `InfluxDB.Client` (.NET 5.0.0 — already in csproj)
- Rolling history window: **24 hours** lookback per ScoreBatch run (covers 1 seasonal period for STL)
- Query raw readings (no downsampling) — sensors update ≤60s; raw fidelity required for STL
- Config fields: `InfluxUrl`, `InfluxToken`, `InfluxOrg`, `InfluxBucket` added to `ConnectionSettings` / env vars

### Batch Scheduler Design

- Trigger: **fixed timer only** — `BatchIntervalMinutes` (default 10 min), configurable via env/appsettings
- Nightly Fit (retraining): separate scheduled call at configurable time (default **02:00 local**); `NightlyFitHour` config key
- Missing data: **skip entity silently + structured log warning** — do not score or error if no readings in window
- Scheduler location: `BatchSchedulerWorker` as `BackgroundService` in .NET orchestrator; calls InfluxDB reader then `ScoreBatch` gRPC; calls `Fit` on nightly schedule

### Model Lifecycle & Storage

- Version key: **integer counter** (1, 2, 3…) per (entity_id, detector); `latest` symlink/file points to current version number
- Retention: **3 most recent versions** per (entity_id, detector) — prune oldest on each save
- Serialization: **joblib** for PyOD models (RobustZScore/MAD); **River `HalfSpaceTrees.to_dict()`/`from_dict()`** for HST streaming model
- Version sidecar: `version.json` alongside each model file — fields: `{version, entity_id, detector, created_at, grpcio_version, pyod_version, river_version}`
- Directory layout: `models/{entity_slug}/{detector}/v{N}/model.pkl` + `version.json`; `latest` file contains `N` as integer

### Proto Extensions (ScoreBatch + MDL-02)

- `ScoreBatch` uses new message `ScoreBatchRequest {entity_id, detector, params, repeated Point window}` — semantically distinct from `FitRequest` (Fit mutates state, ScoreBatch is read-only)
- `ScoreBatchResponse {repeated Verdict verdicts, bool ok, string error}` — one Verdict per window point; consistent with streaming path Verdict shape
- `SaveModel`/`LoadModel` transport: **in-band gRPC bytes** — `SaveModelRequest {entity_id, detector, int32 version, bytes model_bytes}` / `SaveModelResponse {ok, error}`; `LoadModelRequest {entity_id, detector, int32 version}` / `LoadModelResponse {ok, bytes model_bytes, error}` — no shared filesystem mount required
- Fit vs ScoreStream locking: per-(entity_id, detector) **`threading.Lock`** (detector uses threaded gRPC, not asyncio) — train outside lock on a deep copy, swap atomically (MDL-04)

### Claude's Discretion

- Exact Flux query syntax for InfluxDB 2.x time range and entity filter
- `BatchSchedulerWorker` timer implementation details (PeriodicTimer vs CancellationToken loop)
- PyOD model deep-copy approach before Fit (copy.deepcopy vs construct fresh)
- Model prune logic (sort versions, delete oldest beyond 3)

</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets

- `ConnectionSettings` — extend with `InfluxUrl`, `InfluxToken`, `InfluxOrg`, `InfluxBucket`, `BatchIntervalMinutes`, `NightlyFitHour`
- `EntitiesConfig` / `EntitiesConfigLoader` — already parses `entities.yaml`; reuse for entity list in batch scheduler
- `DetectionGateway` — holds singleton gRPC channel + `DetectorClient`; extend `DetectorService.DetectorServiceClient` with `ScoreBatch`, `Fit`, `SaveModel`, `LoadModel` once proto is updated
- `LogEvents` — extend with batch/fit/model lifecycle event IDs
- `IStatePublisher` / `StatePublisher` — reuse to publish ScoreBatch verdicts to MQTT (same publish path as streaming)
- Python `DetectorRegistry` — extend to support `fit_one()`, `score_batch()`, `save_model()`, `load_model()` for PyOD/STL detectors alongside existing HST

### Established Patterns

- `.NET BackgroundService` worker pattern (see `HaListenerWorker`, `MqttPublisherWorker`) — use same `ExecuteAsync(CancellationToken)` shape for `BatchSchedulerWorker`
- Structured logging with `LogEvents` event IDs and named parameters
- gRPC channel singleton via `DetectionGateway`; do NOT create a second channel
- Per-entity `Channel<T>` for fanout (Phase 1 WR-03 fix) — if batch reads need per-entity dispatch, follow same bounded-channel pattern
- Python registry keyed by `(entity_id, detector)` tuple — extend for PyOD detectors with same dict + lock pattern

### Integration Points

- `Program.cs` DI registration — register `BatchSchedulerWorker`, `InfluxDbReader`, any new services
- `argus.proto` — add `ScoreBatch`, `SaveModel`, `LoadModel` RPCs and new request/response messages; regenerate stubs (both .NET and Python)
- `StatePublisher.PublishVerdictAsync` — reuse for batch verdicts (same MQTT topics, same payload shape)
- `DetectionGateway.DetectorClient` — add typed calls for new RPCs after proto regen

</code_context>

<specifics>
## Specific Ideas

- **SaveModel/LoadModel are internal plumbing** — the orchestrator calls SaveModel after Fit (nightly), LoadModel on detector startup (MDL-03). The user never calls these directly; they're a restart-resilience mechanism, not a user-facing API.
- **ScoreBatch Fit cold start**: if no saved model exists for an entity, run Fit first using the 24h InfluxDB window before returning ScoreBatch scores. Log a "cold start fit" event.
- **STL requires ≥2× seasonal period** — if history window is shorter than 48h of data (< 2 × 24h daily period), STL ScoreBatch should return `ok=false, error="insufficient history"` rather than a bad score.
- **RES-02 validation**: orchestrator restart test — re-publish MQTT discovery without duplicating entities (already idempotent from Phase 1); detector restart test — load models before accepting any scoring connections (MDL-03 gate).
- Entity model path key uses `entity_slug` (`.` → `_`) consistent with existing `UniqueId.cs` slug formula.

</specifics>

<deferred>
## Deferred Ideas

- Darts forecasting models and covariate conditioning → v2 (SEAS-01 through SEAS-04)
- Multivariate group scoring (PyOD ECOD / Isolation Forest) → v2 (MULTI-01 through MULTI-03)
- GPU support → Phase 3 / v2
- `entities.yaml` hot-reload → v2 (ADV-03)
- CPU-only detector replica on edge host → v2 (ADV-01)
- Model age sensor in HA → v2 (ADV-02)
- PyThresh adaptive thresholds → Phase 3+

None — discussion stayed within phase scope.

</deferred>

---

*Phase: 2-Batch Path + Model Lifecycle*
*Context gathered: 2026-06-10*
