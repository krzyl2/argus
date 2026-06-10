# Phase 2: Batch Path + Model Lifecycle — Research

**Researched:** 2026-06-10
**Domain:** InfluxDB.Client Flux queries, PyOD MAD batch detection, STL seasonal decomposition, joblib/pickle model persistence, gRPC message sizing, .NET BackgroundService scheduling
**Confidence:** HIGH on stack/API questions; MEDIUM on PyOD class naming (critical finding below); LOW on River to_dict/from_dict claim

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- InfluxDB v2 Flux API, `InfluxDB.Client` .NET 5.0.0 — already in csproj
- Rolling history window: 24 hours, raw readings (no downsampling)
- ConnectionSettings extended with InfluxUrl/InfluxToken/InfluxOrg/InfluxBucket
- BatchSchedulerWorker as BackgroundService, BatchIntervalMinutes (default 10 min)
- Nightly Fit at configurable hour (NightlyFitHour, default 02:00 local)
- Skip entity silently + structured log warning if no readings in window
- Integer version counter, retain 3 versions, joblib for PyOD, River pickle for HST
- version.json sidecar: {version, entity_id, detector, created_at, grpcio_version, pyod_version, river_version}
- Directory: models/{entity_slug}/{detector}/v{N}/model.pkl + version.json; latest file = N
- ScoreBatch: ScoreBatchRequest {entity_id, detector, params, repeated Point window}; ScoreBatchResponse {repeated Verdict verdicts, bool ok, string error}
- SaveModel/LoadModel in-band gRPC bytes
- threading.Lock (not asyncio.Lock) per-(entity_id, detector) for Fit vs ScoreStream; train outside lock, swap atomically (MDL-04)

### Claude's Discretion
- Exact Flux query syntax for InfluxDB 2.x time range and entity filter
- BatchSchedulerWorker timer implementation details (PeriodicTimer vs CancellationToken loop)
- PyOD model deep-copy approach before Fit (copy.deepcopy vs construct fresh)
- Model prune logic (sort versions, delete oldest beyond 3)

### Deferred Ideas (OUT OF SCOPE)
- Darts forecasting models and covariate conditioning (SEAS-01 through SEAS-04)
- Multivariate group scoring (MULTI-01 through MULTI-03)
- GPU support (Phase 3)
- entities.yaml hot-reload (ADV-03)
- CPU-only detector replica on edge host (ADV-01)
- Model age sensor in HA (ADV-02)
- PyThresh adaptive thresholds (Phase 3+)
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| BTCH-01 | InfluxDB reader queries rolling history window per entity (Flux API) | Section: InfluxDB.Client Flux Query API |
| BTCH-02 | PyOD batch detector scores window via ScoreBatch gRPC RPC | Section: PyOD Detector API — CRITICAL FINDING |
| BTCH-03 | Batch scheduler runs on configurable interval + nightly retraining | Section: PeriodicTimer vs Task.Delay |
| BTCH-04 | STL seasonal-residual detector scores window via ScoreBatch | Section: STL Seasonal Decomposition |
| FAULT-03 | Step change (level shift) detected by batch detector | Section: STL for Step-Change Detection |
| MDL-01 | Per-entity model store on disk, versioned directory layout | Section: Model Storage Patterns |
| MDL-02 | SaveModel/LoadModel gRPC RPCs; version sidecar metadata | Section: gRPC Message Size Limits |
| MDL-03 | On detector startup, models loaded before accepting connections | Section: Startup Load Gate Pattern |
| MDL-04 | Concurrent Fit vs ScoreStream serialized via per-(entity,detector) lock | Section: threading.Lock in grpcio Threaded Server |
| RES-02 | All components restart independently without losing model state or duplicating HA entities | Section: MQTT Discovery Idempotency + MDL-03 |
</phase_requirements>

---

## Summary

Phase 2 adds three major capabilities on top of Phase 1's streaming path: (1) InfluxDB history reads via the .NET Flux API, (2) batch detection via PyOD MAD and STL decomposition on the Python side, and (3) model persistence so the detector can survive restarts. The orchestrator gains `BatchSchedulerWorker` (a new `BackgroundService`) and `InfluxDbReader`. The Python detector gains PyOD-backed `ScoreBatch` and `Fit` servicer methods, a `ModelStore` class for disk operations, and startup model loading.

**Critical finding:** `RobustZScore` is NOT a class in PyOD 3.6.0. The PyOD module index contains `mad` and `hbos` but no `zscore`, `robust_zscore`, or `z_score` module. The correct PyOD class for robust z-score anomaly detection on univariate data is `pyod.models.mad.MAD`. The CONTEXT.md reference to "RobustZScore" as a distinct detector is [ASSUMED] from training data — the planner must use `MAD` for both "MAD" and "RobustZScore" requirements. See Assumptions Log.

River `HalfSpaceTrees` does NOT have `to_dict()`/`from_dict()` methods. Pickle is the canonical serialization approach for River models (documented in River FAQ). The CONTEXT.md reference to `to_dict`/`from_dict` for HST is [ASSUMED] and incorrect — use `pickle.dump`/`pickle.load` for both PyOD (via joblib) and River (via pickle) models.

**Primary recommendation:** PyOD `MAD` for univariate robust scoring; `statsmodels.tsa.seasonal.STL(robust=True)` for step-change residual detection; `pickle`/`joblib` for all model persistence; `PeriodicTimer` for scheduling.

---

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| InfluxDB rolling-window query | .NET Orchestrator | — | Orchestrator owns all external I/O; Python detector is stateless w.r.t. data sources |
| Batch scoring trigger/scheduling | .NET Orchestrator (BatchSchedulerWorker) | — | Scheduler is a BackgroundService; already owns PeriodicTimer idiom |
| ScoreBatch scoring logic (MAD/STL) | Python Detector | — | All ML is Python (locked D2) |
| Model Fit (PyOD training) | Python Detector | — | All ML is Python (locked D2) |
| Model persistence (disk write/read) | Python Detector (ModelStore) | — | Models live on detector host disk; orchestrator never touches model files directly |
| SaveModel/LoadModel transport | Python Detector serves RPC; .NET calls RPC | — | in-band gRPC bytes — orchestrator calls SaveModel after nightly Fit; detector calls LoadModel at startup |
| MQTT batch verdict publish | .NET Orchestrator (StatePublisher) | — | Same publish path as streaming; reuse existing StatePublisher |
| Per-entity fit/score concurrency lock | Python Detector (DetectorRegistry) | — | threading.Lock in the registry; gRPC threaded server model |
| Startup model load gate | Python Detector (server.py startup) | — | MDL-03: load all models before Health/SERVING |
| MQTT discovery re-publish on restart | .NET Orchestrator (MqttPublisherWorker) | — | Already idempotent by unique_id; existing worker republishes on each start |

---

## Standard Stack

### Core (no new packages — all already in requirements/csproj)

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| InfluxDB.Client (.NET) | 5.0.0 | Flux queries for rolling window history | Already in csproj; official InfluxData client [VERIFIED: NuGet] |
| pyod.models.mad.MAD | 3.6.0 | Univariate robust anomaly scoring (replaces "RobustZScore") | Only univariate detector in PyOD using MAD/modified-Z; fits 1D sensor arrays [VERIFIED: pyod.readthedocs.io] |
| statsmodels.tsa.seasonal.STL | 0.14.x (transitive via Darts) | STL seasonal decomposition for step-change detection | Robust LOESS decomposition; `robust=True` downweights outliers [CITED: statsmodels.org] |
| joblib | 1.5.3 | PyOD model persistence | Canonical sklearn-compatible serializer; optimized for numpy arrays [VERIFIED: PyPI] |
| pickle (stdlib) | Python stdlib | River HalfSpaceTrees persistence | River FAQ's documented approach; no River-specific serialization API exists [CITED: riverml.xyz/faq] |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| copy.deepcopy (stdlib) | Python stdlib | Clone PyOD model before training outside lock | Needed for MDL-04: train on deep copy, swap atomically; safer than constructing fresh (preserves hyperparams) |
| filelock | N/A — NOT needed | File-system lock for model writes | ARCHITECTURE.md Phase 1 research mentioned it; Phase 2 uses threading.Lock within a single process — no inter-process coordination |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| MAD (pyod.models.mad) | HBOS (pyod.models.hbos) | HBOS is multivariate histogram-based; MAD is strictly univariate — MAD is correct for 1D sensor streams |
| statsmodels.STL | scipy.signal / custom | statsmodels STL has `robust=True` parameter and returns clean trend/seasonal/residual; scipy has no direct STL equivalent |
| pickle for River HST | dill / cloudpickle | River FAQ says pickle is standard; dill handles lambdas but adds dependency; no benefit for HalfSpaceTrees |
| PeriodicTimer | Task.Delay loop | Both correct; PeriodicTimer is the idiomatic .NET 6+ approach and already in ARCHITECTURE.md |

---

## Package Legitimacy Audit

No new packages are required in Phase 2. All libraries (InfluxDB.Client, pyod, joblib, statsmodels, pickle) are already declared in csproj/requirements.txt and were verified in Phase 1 research.

**Packages removed due to slopcheck [SLOP] verdict:** none  
**Packages flagged as suspicious [SUS]:** none  
**filelock** is referenced in ARCHITECTURE.md but is NOT needed in Phase 2 (single-process, threading.Lock is sufficient).

---

## Architecture Patterns

### System Architecture Diagram

```
EDGE HOST
  BatchSchedulerWorker (BackgroundService)
    PeriodicTimer (BatchIntervalMinutes=10)       NightlyFitCheck (hour=02:00)
         │                                              │
         ▼                                              ▼
    InfluxDbReader                               InfluxDbReader
    QueryAsync(Flux, org)                        QueryAsync(Flux, org)
    → List<(ts, value)>                          → List<(ts, value)>
         │                                              │
         ▼ gRPC ScoreBatch                              ▼ gRPC Fit
    DetectorClient                               DetectorClient
         │                                              │
         ▼ returns ScoreBatchResponse                   ▼ returns FitResponse
    StatePublisher.PublishFlagAsync                     │ (then)
    StatePublisher.PublishScoreAsync               DetectorClient.SaveModelAsync
         │                                              │
         ▼ MQTT retain:false                            ▼ gRPC SaveModel
    MQTT Broker → HA                             Python ModelStore.save()

GPU HOST
  DetectorServicer.ScoreBatch()
    → DetectorRegistry.score_batch(entity_id, detector, window[])
         → PyOD MAD/STL .decision_function(X)
         → returns [] Verdict

  DetectorServicer.Fit()
    → DetectorRegistry.fit(entity_id, detector, window[])
         → deep_copy current model (train outside lock)
         → model.fit(X)
         → with lock: swap model pointer
         → returns FitResponse{ok=True}

  Server startup:
    ModelStore.load_all() → populates DetectorRegistry
    → health.set_status(SERVING) once all models loaded

  DetectorServicer.SaveModel()  ← orchestrator calls after nightly Fit
    → ModelStore.save(entity_id, detector, version, model_bytes)
    → write models/{slug}/{detector}/v{N}/model.pkl + version.json
    → update 'latest' file
    → prune versions beyond 3

  DetectorServicer.LoadModel()  ← orchestrator calls on startup (cold start only)
    → ModelStore.load(entity_id, detector, version)
    → returns bytes
```

### Recommended Project Structure

```
orchestrator/Argus.Orchestrator/
├── Batch/
│   ├── BatchSchedulerWorker.cs    # BackgroundService; PeriodicTimer + nightly check
│   ├── InfluxDbReader.cs          # QueryAsync wrapper; returns IReadOnlyList<(DateTime ts, double value)>
│   └── BatchParams.cs             # typed accessor for batch config params
├── Config/
│   └── ConnectionSettings.cs     # +InfluxUrl, InfluxToken, InfluxOrg, InfluxBucket, BatchIntervalMinutes, NightlyFitHour
└── Logging/
    └── LogEvents.cs               # +5xxx batch event IDs

detector/argus_detector/
├── pyod_detector.py               # PyODDetector: wraps pyod.models.mad.MAD; fit(), score_batch()
├── stl_detector.py                # StlDetector: wraps statsmodels STL; fit_window(), score_batch()
├── model_store.py                 # ModelStore: versioned disk read/write; prune()
├── registry.py                    # extend: fit_one(), score_batch(), load_all_models()
└── servicer.py                    # extend: ScoreBatch(), Fit(), SaveModel(), LoadModel()
```

### Pattern 1: PeriodicTimer in BackgroundService (.NET 8)

**What:** Fixed-interval loop using `PeriodicTimer`, checking nightly fit separately via `DateTime.Now.Hour`.
**When to use:** All periodic scheduled work in `BackgroundService.ExecuteAsync`.

```csharp
// Source: [CITED: learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services]
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_settings.BatchIntervalMinutes));
    while (await timer.WaitForNextTickAsync(stoppingToken))
    {
        try
        {
            await RunBatchAsync(stoppingToken);
            if (DateTime.Now.Hour == _settings.NightlyFitHour && !_fitRunToday)
            {
                await RunNightlyFitAsync(stoppingToken);
                _fitRunToday = true;
            }
            // Reset _fitRunToday when hour changes
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(LogEvents.BatchSchedulerError, ex, "Batch run failed");
        }
    }
}
```

**Key:** `WaitForNextTickAsync` respects `CancellationToken`; throwing `OperationCanceledException` terminates gracefully. Catch all other exceptions inside the loop to prevent the worker from dying on a single failure.

### Pattern 2: InfluxDB.Client Flux Query (.NET)

**What:** Rolling 24h window query filtered by entity_id tag.
**When to use:** Every BatchSchedulerWorker tick per entity.

```csharp
// Source: [CITED: github.com/influxdata/influxdb-client-csharp README]
var flux = $"""
    from(bucket: "{_settings.InfluxBucket}")
      |> range(start: -24h)
      |> filter(fn: (r) => r["_measurement"] == "homeassistant"
            and r["entity_id"] == "{entityId}"
            and r["_field"] == "value")
      |> sort(columns: ["_time"])
    """;
var tables = await _queryApi.QueryAsync(flux, _settings.InfluxOrg, stoppingToken);
var points = tables
    .SelectMany(t => t.Records)
    .Select(r => (Timestamp: r.GetTime()!.Value, Value: Convert.ToDouble(r.GetValue())))
    .ToList();
```

**Key points:**
- `QueryApi` is obtained via `influxDBClient.GetQueryApi()` — inject `InfluxDBClient` as a singleton, get API per-call.
- `r.GetValue()` returns `object`; safe cast to `double` for numeric fields.
- `r.GetTime()` returns `Instant?` (NodaTime). Convert to `DateTime` with `.ToDateTimeUtc()` or pass as `Timestamp` proto field directly.
- Empty result (`points.Count == 0`) → log warning and skip entity (BTCH-01 requirement).
- POCO mapping via `[Measurement]`/`[Column]` attributes is an alternative but raw `FluxRecord` is simpler for a single-field query.

### Pattern 3: PyOD MAD Batch Scoring (Python)

**What:** Fit MAD on training window; score a batch; return one score per point.
**When to use:** ScoreBatch RPC implementation for "mad" detector.

```python
# Source: [CITED: pyod.readthedocs.io MAD source + BaseDetector API]
import numpy as np
from pyod.models.mad import MAD

class PyODDetector:
    """Wraps pyod.models.mad.MAD for univariate batch scoring."""

    def __init__(self, threshold: float = 3.5, contamination: float = 0.1):
        self._model = MAD(threshold=threshold, contamination=contamination)
        self._fitted = False

    def fit(self, values: list[float]) -> None:
        # MAD requires X.shape = (n_samples, 1) — univariate constraint enforced by MAD
        X = np.array(values, dtype=float).reshape(-1, 1)
        self._model.fit(X)
        self._fitted = True

    def score_batch(self, values: list[float]) -> list[float]:
        if not self._fitted:
            raise ValueError("fit() must be called before score_batch()")
        X = np.array(values, dtype=float).reshape(-1, 1)
        # decision_function returns raw scores (higher = more anomalous)
        return self._model.decision_function(X).tolist()
```

**Critical shape constraint:** MAD raises `ValueError` if `X.shape[1] != 1`. Always reshape to `(-1, 1)` before calling `fit()` or `decision_function()`.

**Threshold note:** The MAD `threshold` parameter (default 3.5) is the modified Z-score cutoff used by `predict()`. `decision_function()` returns raw scores independent of threshold. For ScoreBatch, return `decision_function()` scores (continuous), not `predict()` labels (binary). The orchestrator's hysteresis gate handles the binary decision.

### Pattern 4: STL Residual Scoring for Step-Change Detection (Python)

**What:** Decompose a time series into trend + seasonal + residual; return residual magnitudes as anomaly scores.
**When to use:** ScoreBatch RPC for "stl" detector (BTCH-04, FAULT-03).

```python
# Source: [CITED: statsmodels.org/stable/generated/statsmodels.tsa.seasonal.STL]
import numpy as np
from statsmodels.tsa.seasonal import STL

# Period for 60s-interval daily seasonality = 1440 points/day
_PERIOD_DAILY = 1440

class StlDetector:
    """STL residual-based anomaly detector. Stateless — no persistent model."""

    def score_batch(
        self,
        values: list[float],
        period: int = _PERIOD_DAILY,
    ) -> tuple[list[float], str | None]:
        """
        Returns (scores, error_message).
        error_message is non-None when data is insufficient.
        """
        n = len(values)
        if n < 2 * period:
            return [], f"insufficient history: got {n} points, need >= {2 * period}"

        x = np.array(values, dtype=float)
        result = STL(x, period=period, robust=True).fit()
        residuals = np.abs(result.resid)
        # Normalise to [0, 1] range for comparison with MAD scores
        rng = residuals.max() - residuals.min()
        if rng == 0:
            return [0.0] * n, None
        scores = ((residuals - residuals.min()) / rng).tolist()
        return scores, None
```

**Key points:**
- `robust=True` is mandatory — standard STL absorbs anomalies into the seasonal component; robust STL downweights outliers during decomposition so step changes appear in the residual [CITED: statsmodels.org].
- Minimum data: `2 * period` points required. For 60s data with daily period: 2880 points (48h). The 24h rolling window provides at most 1440 points — **STL cannot meet its minimum history requirement from a 24h window alone**. See Pitfall 3.
- STL is stateless — it decomposes whatever window is passed; no pre-fitted model is persisted for STL.
- Step changes manifest as large residuals after trend/seasonal removal, which is why STL detects level shifts (FAULT-03).

### Pattern 5: Model Persistence (joblib + pickle)

```python
# Source: [CITED: riverml.xyz/faq, joblib.readthedocs.io, sklearn model persistence docs]
import joblib, pickle, json, pathlib

MODEL_ROOT = pathlib.Path("/var/argus/models")

def save_pyod_model(entity_slug: str, detector: str, version: int, model) -> pathlib.Path:
    d = MODEL_ROOT / entity_slug / detector / f"v{version}"
    d.mkdir(parents=True, exist_ok=True)
    joblib.dump(model, d / "model.joblib")
    return d

def save_river_model(entity_slug: str, detector: str, version: int, model) -> pathlib.Path:
    d = MODEL_ROOT / entity_slug / detector / f"v{version}"
    d.mkdir(parents=True, exist_ok=True)
    with open(d / "model.pkl", "wb") as f:
        pickle.dump(model, f)
    return d

def write_version_json(model_dir: pathlib.Path, meta: dict) -> None:
    import grpc, pyod, river
    meta.update({
        "grpcio_version": grpc.__version__,
        "pyod_version": pyod.__version__,
        "river_version": river.__version__,
    })
    (model_dir / "version.json").write_text(json.dumps(meta))

def update_latest_pointer(entity_slug: str, detector: str, version: int) -> None:
    latest_path = MODEL_ROOT / entity_slug / detector / "latest"
    # Atomic write: write to tmp then rename
    tmp = latest_path.with_suffix(".tmp")
    tmp.write_text(str(version))
    tmp.replace(latest_path)

def prune_old_versions(entity_slug: str, detector: str, keep: int = 3) -> None:
    base = MODEL_ROOT / entity_slug / detector
    versions = sorted(
        [int(d.name[1:]) for d in base.iterdir() if d.is_dir() and d.name.startswith("v")],
        reverse=True,
    )
    for old_v in versions[keep:]:
        import shutil
        shutil.rmtree(base / f"v{old_v}", ignore_errors=True)
```

### Pattern 6: Threading Lock for Fit vs ScoreStream (Python)

**What:** Per-(entity_id, detector) lock; train outside lock, swap atomically.
**When to use:** DetectorRegistry.fit_one() implementation.

```python
# Source: [CITED: .planning/research/ARCHITECTURE.md + grpcio threading model docs]
import threading, copy

class DetectorRegistry:
    def __init__(self):
        self._detectors: dict[tuple, object] = {}
        self._registry_lock = threading.Lock()   # guards dict creation
        self._entity_locks: dict[tuple, threading.Lock] = {}

    def _entity_lock(self, key: tuple) -> threading.Lock:
        with self._registry_lock:
            if key not in self._entity_locks:
                self._entity_locks[key] = threading.Lock()
            return self._entity_locks[key]

    def fit_one(self, entity_id: str, detector: str, values: list[float]) -> None:
        key = (entity_id, detector)
        lock = self._entity_lock(key)
        # Get current model snapshot OUTSIDE the entity lock
        with lock:
            current = self._detectors.get(key)
        # Deep-copy before training — prevents contaminating live model
        candidate = copy.deepcopy(current) if current else self._create_detector(detector)
        candidate.fit(values)         # CPU-bound; runs outside lock (MDL-04)
        # Atomic swap
        with lock:
            self._detectors[key] = candidate

    def score_batch(self, entity_id: str, detector: str, values: list[float]) -> list[float]:
        key = (entity_id, detector)
        lock = self._entity_lock(key)
        with lock:
            model = self._detectors.get(key)
        if model is None:
            raise ValueError(f"No model for {key}; call fit first")
        return model.score_batch(values)   # read-only; no lock held during execution
```

**Key:** `score_batch` reads the model reference under the lock but executes scoring outside it. This is safe because the swap in `fit_one` replaces the entire reference — the scoring thread holds a reference to the old model (which remains valid until GC).

### Pattern 7: Startup Model Load Gate (Python)

**What:** Load all persisted models from disk before health transitions to SERVING.
**When to use:** `server.py` startup sequence (MDL-03).

```python
# Source: [ASSUMED — standard gRPC health service pattern]
import grpc
from grpc_health.v1 import health, health_pb2_grpc, health_pb2

def serve():
    health_servicer = health.HealthServicer()
    # Mark NOT_SERVING while loading
    health_servicer.set("argus.v1.DetectorService", health_pb2.HealthCheckResponse.NOT_SERVING)

    registry = DetectorRegistry()
    model_store = ModelStore()
    model_store.load_all_into(registry)   # blocks until all models loaded or logs warning on missing

    # Now safe to accept traffic
    health_servicer.set("argus.v1.DetectorService", health_pb2.HealthCheckResponse.SERVING)
    # ... add_servicer, start server ...
```

### Anti-Patterns to Avoid

- **asyncio.Lock in grpcio sync server:** The grpcio sync server (`grpc.server(ThreadPoolExecutor(...))`) runs each RPC in a real OS thread. `asyncio.Lock` cannot protect against concurrent threads — only against concurrent coroutines on a single thread. Use `threading.Lock`. [CITED: Python asyncio docs + grpcio thread model]
- **Holding entity lock during Fit training:** PyOD `fit()` can take 100ms–2s on 1440 points. Holding the lock for this duration blocks `ScoreStream` for the same entity. Always train outside the lock on a deep copy.
- **Scoring before Fit:** MAD `decision_function()` raises `NotFittedError` if called before `fit()`. Implement a cold-start path: if no model exists, run Fit first before returning ScoreBatch scores.
- **Single `ScoreBatchResponse` per entity instead of per-point verdicts:** The contract is `repeated Verdict verdicts` — one Verdict per input Point. Returning a single score or a flat list breaks the proto contract.
- **Using `predict()` instead of `decision_function()` for ScoreBatch scores:** `predict()` returns 0/1 binary labels. `decision_function()` returns continuous scores (higher = more anomalous). The orchestrator's hysteresis gate needs continuous scores.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Robust univariate anomaly scoring | Custom MAD calculation | `pyod.models.mad.MAD` | Handles edge cases (all-same values, single point); battle-tested; sklearn API |
| STL decomposition | Custom LOESS smoother | `statsmodels.tsa.seasonal.STL(robust=True)` | Robust LOESS is non-trivial; statsmodels has 15 years of bug fixes |
| Model versioning / atomic writes | Custom version tracking | Pattern 5 above (latest-file + rename) | Atomic rename is OS-guaranteed; file-based version counter is simpler than a DB |
| Periodic scheduling with cancellation | `Task.Delay` loop with manual tracking | `PeriodicTimer` (.NET 6+) | PeriodicTimer skips missed ticks (no burst after downtime); CancellationToken-native |
| gRPC message framing for model bytes | Custom chunking | Single unary RPC with `bytes` field + raise `MaxReceiveMessageSize` limit | Default 4MB is sufficient for typical PyOD MAD models (~KB range); only chunk if River HST grows beyond 4MB |

**Key insight:** PyOD models trained on 1440 univariate points are tiny (typically <100KB). gRPC 4MB default is 40× headroom. No chunking needed for Phase 2.

---

## CRITICAL FINDING: PyOD Detector Names

**`RobustZScore` does not exist as a PyOD class.** The PyOD module index (verified at `pyod.readthedocs.io`) lists these univariate-suitable detectors:

| Class | Module | Suitable for 1D sensor? | Notes |
|-------|--------|------------------------|-------|
| `MAD` | `pyod.models.mad` | YES — strictly univariate | `X.shape[1] must == 1`; uses modified Z-score (0.6745 * |x-median| / MAD) |
| `HBOS` | `pyod.models.hbos` | YES — multivariate, works 1D | Histogram-based; less robust for small datasets |
| `COPOD` | `pyod.models.copod` | YES — multivariate, works 1D | Copula-based; good for multivariate (Phase 2+ potential) |

**Recommendation for Phase 2:** Use `MAD` for all univariate sensor scoring. Map both "RobustZScore" and "MAD" detector names in the registry to the same `PyODDetector(MAD(...))` implementation. Log a structured warning if "robust_zscore" is passed as detector name — map it to "mad".

[ASSUMED: training data refers to "RobustZScore" but official PyOD docs show no such class — verified via pyod.readthedocs.io module index]

---

## Common Pitfalls

### Pitfall 1: STL Requires 2× Period — 24h Window is Insufficient for Daily Seasonality
**What goes wrong:** 60s-interval sensors generate 1440 points/day. STL with `period=1440` needs `>= 2880` points (48h). The 24h rolling window provides at most 1440 points. STL will raise an error or produce unreliable residuals.
**Why it happens:** STL uses LOESS smoothing that requires seeing multiple full periods to fit the seasonal component reliably.
**How to avoid:** The `StlDetector.score_batch()` method must validate `len(values) >= 2 * period` and return `ok=False, error="insufficient history"` if the check fails (as specified in CONTEXT.md). For Phase 2, STL may never produce useful results with only 24h history. The planner should note this as an expected outcome of the validation test.
**Warning signs:** ScoreBatch returns `ok=False` with "insufficient history" for all "stl" detector calls during Phase 2 testing.
**Mitigation:** STL becomes useful when the orchestrator queries a longer window (48h+) — extend `BatchIntervalHistoryHours` config key in Phase 3 if needed. Phase 2 implements the guard correctly; the detector is not broken, merely data-starved.

### Pitfall 2: PyOD `RobustZScore` Does Not Exist
**What goes wrong:** `from pyod.models.robust_zscore import RobustZScore` raises `ModuleNotFoundError`.
**Why it happens:** The PyOD library does not have this class. Training data conflated it with the Apache Beam adaptation which wraps a similar algorithm under a different name.
**How to avoid:** Use `from pyod.models.mad import MAD`. The MAD detector uses the identical modified Z-score algorithm (0.6745 × |x − median| / MAD).
**Warning signs:** Any import path containing "robust_zscore" in a PyOD context.

### Pitfall 3: River HST `to_dict()`/`from_dict()` Does Not Exist
**What goes wrong:** `model.to_dict()` raises `AttributeError` on River `HalfSpaceTrees`.
**Why it happens:** River's official serialization API is `pickle`. The `to_dict`/`from_dict` methods exist on some River preprocessing classes but not on anomaly detectors.
**How to avoid:** Use `pickle.dump(model, f)` / `pickle.load(f)` for River HST persistence. The HST state (all tree structures, seen counts, min/max bounds) is fully captured by pickle.
**Warning signs:** Any code calling `.to_dict()` on a River anomaly detector.

### Pitfall 4: joblib Version Mismatch After Python Upgrade
**What goes wrong:** After upgrading scikit-learn or numpy, `joblib.load()` raises `InconsistentVersionWarning` or silently produces wrong predictions.
**Why it happens:** joblib's pickle protocol embeds scikit-learn version metadata. PyOD models inherit this because they extend `sklearn.base.BaseEstimator`. [CITED: scikit-learn.org/stable/model_persistence.html]
**How to avoid:** Store `pyod_version`, `grpcio_version`, and `river_version` in version.json sidecar (already in CONTEXT.md decisions). On model load, check stored version against current runtime version; if mismatched, log a warning and trigger a cold-start re-fit rather than loading the stale model.
**Warning signs:** `InconsistentVersionWarning` in Python logs after any `pip install --upgrade`.

### Pitfall 5: asyncio.Lock in grpcio Sync Server Deadlocks
**What goes wrong:** Using `asyncio.Lock` in a `grpc.server(ThreadPoolExecutor(...))` servicer method deadlocks immediately — `asyncio.Lock` is not thread-safe.
**Why it happens:** grpcio sync server runs each RPC in a separate OS thread from the executor pool. `asyncio.Lock` uses the event loop's single-threaded cooperative scheduler. Calling `asyncio.Lock.acquire()` from a non-event-loop thread raises `RuntimeError` or deadlocks. [CITED: Python asyncio docs, superfastpython.com]
**How to avoid:** Use `threading.Lock` for all synchronization in the grpcio sync server. `threading.Lock` is OS-level and works correctly across thread pool threads.
**Warning signs:** Any `asyncio.Lock` or `async with` in a grpcio sync servicer method.

### Pitfall 6: InfluxDB `r.GetValue()` Returns `object`, Not `double`
**What goes wrong:** `(double)r.GetValue()` throws `InvalidCastException` on InfluxDB integer fields.
**Why it happens:** InfluxDB line protocol stores integers and floats differently. Home Assistant writes sensor values as floats but some fields may be stored as `long` depending on the HA→InfluxDB integration config.
**How to avoid:** Use `Convert.ToDouble(r.GetValue())` instead of direct cast. `Convert.ToDouble` handles both `long` and `double` returned values.
**Warning signs:** `InvalidCastException` in `InfluxDbReader` logs; affects specific entity measurements.

### Pitfall 7: MAD Cold Start — No Model Exists for New Entity
**What goes wrong:** `ScoreBatch` called before `Fit` returns `NotFittedError` from PyOD.
**Why it happens:** A new entity in entities.yaml has no saved model; orchestrator calls `ScoreBatch` before the nightly Fit has run.
**How to avoid:** Implement cold-start logic: if `ScoreBatch` is called for an entity with no model in the registry, run `Fit` first using the supplied `window` data, then score. Log `LogEvents.BatchColdStartFit` event. (Specified in CONTEXT.md specifics.)
**Warning signs:** `NotFittedError` propagating out of `ScoreBatch` as gRPC `INTERNAL` status.

---

## gRPC Contract: Proto Extensions

### New Messages and RPCs for argus.proto

```protobuf
// Source: [CITED: 02-CONTEXT.md — locked decisions]
message ScoreBatchRequest {
  string entity_id = 1;
  string detector = 2;
  map<string, string> params = 3;
  repeated Point window = 4;        // same Point type as FitRequest — reuse
}

message ScoreBatchResponse {
  repeated Verdict verdicts = 1;    // one Verdict per input Point
  bool ok = 2;
  string error = 3;
}

message SaveModelRequest {
  string entity_id = 1;
  string detector = 2;
  int32 version = 3;
  bytes model_bytes = 4;
}

message SaveModelResponse {
  bool ok = 1;
  string error = 2;
}

message LoadModelRequest {
  string entity_id = 1;
  string detector = 2;
  int32 version = 3;               // 0 = load latest
}

message LoadModelResponse {
  bool ok = 1;
  bytes model_bytes = 2;
  string error = 3;
}

// Add to DetectorService:
service DetectorService {
  rpc ScoreStream(stream Point) returns (stream Verdict);
  rpc Fit(FitRequest) returns (FitResponse);
  rpc ScoreBatch(ScoreBatchRequest) returns (ScoreBatchResponse);    // NEW
  rpc SaveModel(SaveModelRequest) returns (SaveModelResponse);       // NEW
  rpc LoadModel(LoadModelRequest) returns (LoadModelResponse);       // NEW
}
```

### Adding RPCs is Non-Breaking

Adding new RPC methods to an existing proto service is backwards compatible. Old clients calling against a server that has the new methods simply don't call them. Old servers called by a new client return `UNIMPLEMENTED` — the client should handle this gracefully by catching `RpcException` with `StatusCode.Unimplemented`. [CITED: learn.microsoft.com/en-us/aspnet/core/grpc/versioning]

### Stub Regeneration Workflow

1. Edit `proto/argus.proto` — add new messages and RPC declarations
2. .NET: rebuild project; `Grpc.Tools` MSBuild target auto-regenerates `obj/Debug/net8.0/Argus.cs` and `ArgusGrpc.cs`
3. Python: run `python -m grpc_tools.protoc -I../proto --python_out=. --grpc_python_out=. ../proto/argus.proto` from `detector/`
4. Verify generated stubs contain new method signatures before writing implementation

### gRPC Message Size Limits

Default maximum per-message size: **4 MB** (4,194,304 bytes) — applies to both send and receive, on both .NET client and Python server. [CITED: grpc/grpc issue #7927]

**For Phase 2 model bytes:**
- PyOD MAD model trained on 1440 univariate points: estimated < 50 KB
- River HST with 250-point window, 25 trees: estimated < 500 KB
- Both are well within the 4MB default — **no size configuration needed**.

**For ScoreBatch request:**
- 1440 Points × (entity_id ~30B + DoubleValue ~16B + Timestamp ~12B) ≈ 84 KB
- Well within 4MB default.

**If future models exceed 4MB** (e.g., after Phase 3 deep models):

```python
# Python server — [CITED: grpc/grpc issue #15738]
server = grpc.server(
    futures.ThreadPoolExecutor(max_workers=10),
    options=[
        ('grpc.max_receive_message_length', 64 * 1024 * 1024),  # 64MB
        ('grpc.max_send_message_length', 64 * 1024 * 1024),
    ]
)
```

```csharp
// .NET client — [CITED: grpc.github.io/grpc/csharp-dotnet GrpcChannelOptions]
var channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
{
    HttpHandler = handler,
    MaxReceiveMessageSize = 64 * 1024 * 1024,
    MaxSendMessageSize = 64 * 1024 * 1024,
});
```

---

## Runtime State Inventory

> SKIPPED — Phase 2 is not a rename/refactor/migration phase. No existing runtime state with old names to migrate.

---

## Environment Availability

> Libraries are verified installed in pinned requirements — no new external services. InfluxDB is a pre-existing service (Q2 from Phase 1).

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| InfluxDB v2 | BTCH-01 | Unknown — depends on operator env | 2.x required | None — BTCH-01 blocked without it |
| statsmodels | BTCH-04 (STL) | Transitive via Darts 0.44.1 in requirements.txt | 0.14.x | None — already present |
| pyod.models.mad | BTCH-02 | PyOD 3.6.0 in requirements.txt | 3.6.0 | None — already present |

**Missing dependencies with no fallback:**
- InfluxDB v2 instance — must be configured by operator before batch path can be validated.

**Missing dependencies with fallback:**
- None.

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `river.HalfSpaceTrees.to_dict()` (assumed) | `pickle.dump(model, f)` | River has always used pickle; `to_dict` was a false assumption | No code changes vs assumption — use pickle |
| `pyod.models.robust_zscore.RobustZScore` (assumed) | `pyod.models.mad.MAD` | PyOD never had RobustZScore as a standalone class | Code must import from `pyod.models.mad`, not `pyod.models.robust_zscore` |
| System.Threading.Timer (older .NET) | PeriodicTimer (.NET 6+) | .NET 6 introduced PeriodicTimer | PeriodicTimer skips accumulated ticks on slow runs; avoids burst after downtime |

**Deprecated/outdated:**
- `river.anomaly.HalfSpaceTrees.to_dict()`: Does not exist. Use pickle.
- `pyod.models.robust_zscore`: Module does not exist. Use `pyod.models.mad.MAD`.
- `grpc.experimental.aio`: Replaced by `grpc.aio` since grpcio 1.32 (already in PITFALLS.md).

---

## MQTT Discovery Idempotency (RES-02)

The existing `MqttPublisherWorker.ExecuteAsync()` already calls `DiscoveryPublisher.PublishAllAsync()` on every startup. Discovery payloads are `retain: true`. HA deduplicates by `unique_id`. This means:

- Orchestrator restart → re-publishes all discovery payloads → HA receives them and updates in-place (same `unique_id`) → no orphans, no duplicates. [CITED: .planning/research/PITFALLS.md + .planning/research/ARCHITECTURE.md]

**Phase 2 addition:** `BatchSchedulerWorker` uses the same `StatePublisher.PublishFlagAsync` / `PublishScoreAsync` methods that the streaming path uses. No new MQTT topics or new discovery entries are needed for the batch path — batch verdicts publish to the same `argus/{slug}/flag/state` and `argus/{slug}/score/state` topics. Idempotency is inherited automatically.

---

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `RobustZScore` is "RobustZScore" in CONTEXT.md — research shows it maps to `pyod.models.mad.MAD` | CRITICAL FINDING + Pitfall 2 | Import error at runtime if planner uses `RobustZScore`; must use `MAD` |
| A2 | River `HalfSpaceTrees.to_dict()`/`from_dict()` — research shows these methods do not exist; pickle is correct | Pitfall 3 | `AttributeError` at runtime if planner uses `to_dict()`; must use `pickle` |
| A3 | PyOD MAD model size for 1440 univariate points is well under 4MB gRPC default | gRPC Message Size Limits | If wrong (unlikely), models fail to transfer — mitigated by version.json size check |
| A4 | InfluxDB field name is `value` and measurement is `homeassistant` in the operator's InfluxDB instance | Pattern 2 (Flux query) | Wrong field/measurement name → empty query results → all entities skipped silently |
| A5 | River HST pickle is stable across minor Python version bumps | Model Persistence | If pickle protocol incompatible after upgrade → cold-start re-fit required; version.json sidecar mitigates |

---

## Open Questions (RESOLVED)

1. **InfluxDB measurement and field names** — RESOLVED
   - What we know: The operator uses Home Assistant's InfluxDB integration; common measurement is `homeassistant` with tag `entity_id` and field `value`.
   - What's unclear: The actual measurement name and field name in the operator's InfluxDB instance (Q2 from Phase 1 is unresolved). If the schema differs, the Flux query pattern must change.
   - Recommendation: Make measurement name and field name configurable in `ConnectionSettings` (`InfluxMeasurement`, `InfluxValueField`). Default to `homeassistant` / `value`.
   - **Resolution:** `InfluxMeasurement` (default "homeassistant") and `InfluxValueField` (default "value") added to `ConnectionSettings` in Plan 02-02. Planner adopts this.

2. **STL period for non-daily sensors** — RESOLVED
   - What we know: Pressure sensors may not have strong daily seasonality; period=1440 may be wrong.
   - What's unclear: Whether the operator wants per-entity STL period config.
   - Recommendation: Add `stl_period` as an optional param in `entities.yaml` per-detector params map; default to 1440 (daily for 60s data).
   - **Resolution:** `stl_period` supported via per-detector `params` map in `entities.yaml` (e.g. `stl_period: "720"`); default is 1440. Plans 02-03 and 02-05 implement this.

3. **SaveModel RPC timing — who calls it?** — RESOLVED
   - CONTEXT.md says orchestrator calls SaveModel after nightly Fit. But the model bytes are on the detector side — the detector would need to serialize the model and return bytes in `FitResponse`, OR the orchestrator calls `SaveModel` separately.
   - Recommendation: Add `model_bytes` to `FitResponse` so the orchestrator receives the serialized model in one call and then calls `SaveModel` to persist it. This avoids a second gRPC round-trip.
   - Alternatively (simpler): detector saves the model itself after Fit and returns version number in `FitResponse`; orchestrator doesn't need `SaveModel` at all except for explicit export. This is cleaner but deviates from CONTEXT.md. Flag for planner to decide.
   - **Resolution:** Simpler approach adopted — detector saves model internally after Fit (Plan 02-05). `SaveModel`/`LoadModel` RPCs remain in proto for explicit orchestrator-triggered operations only; `BatchSchedulerWorker` calls `FitAsync` only (no separate `SaveModelAsync` call after Fit).

---

## Sources

### Primary (HIGH confidence)
- [pyod.readthedocs.io py-modindex](https://pyod.readthedocs.io/en/latest/py-modindex.html) — confirmed `pyod.models.mad` exists, `pyod.models.robust_zscore` does NOT exist
- [pyod.readthedocs.io MAD source](https://pyod.readthedocs.io/en/latest/_modules/pyod/models/mad.html) — MAD constructor, `decision_function`, univariate constraint `X.shape[1] == 1`
- [riverml.xyz FAQ](https://riverml.xyz/0.11.1/faq/) — pickle is the documented serialization approach for River models; `dill`/`cloudpickle` mentioned as alternatives
- [statsmodels.org STL docs](https://www.statsmodels.org/stable/generated/statsmodels.tsa.seasonal.STL.html) — constructor params, `robust=True`, `period` parameter
- [github.com/influxdata/influxdb-client-csharp README](https://github.com/influxdata/influxdb-client-csharp/blob/master/README.md) — `QueryAsync(flux, org)` pattern, `FluxTable.Records`, `GetTime()`, `GetValue()`, POCO mapping
- [learn.microsoft.com hosted-services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-8.0) — `PeriodicTimer` + `WaitForNextTickAsync(stoppingToken)` pattern
- [grpc.github.io GrpcChannelOptions](https://grpc.github.io/grpc/csharp-dotnet/api/Grpc.Net.Client.GrpcChannelOptions.html) — `MaxReceiveMessageSize`, `MaxSendMessageSize` on .NET client
- [scikit-learn.org model persistence](https://scikit-learn.org/stable/model_persistence.html) — joblib version incompatibility when scikit-learn version changes
- [learn.microsoft.com grpc versioning](https://learn.microsoft.com/en-us/aspnet/core/grpc/versioning?view=aspnetcore-10.0) — adding new RPC methods is non-breaking

### Secondary (MEDIUM confidence)
- [grpc/grpc issue #7927](https://github.com/grpc/grpc/issues/7927) — 4MB default gRPC message size (applies to both Python and .NET)
- [grpc/grpc issue #15738](https://github.com/grpc/grpc/issues/15738) — Python `grpc.max_receive_message_length` option syntax
- [superfastpython.com asyncio-use-threading-lock](https://superfastpython.com/asyncio-use-threading-lock/) — `asyncio.Lock` deadlocks when used from non-event-loop threads

### Tertiary (LOW confidence)
- Training knowledge for PyOD model size estimates (< 50KB for MAD on 1440 univariate points) — [ASSUMED], not measured
- STL minimum observations "at least 2 full periods" — confirmed in `seasonal_decompose` source (`seasonal.py`); STL docs don't state it explicitly but the constraint is equivalent per statsmodels source examination

---

## Metadata

**Confidence breakdown:**
- PyOD MAD API: HIGH — verified from official pyod.readthedocs.io source
- PyOD RobustZScore does NOT exist: HIGH — verified from official pyod.readthedocs.io module index
- River pickle serialization: HIGH — cited from riverml.xyz FAQ
- River to_dict/from_dict does NOT exist: HIGH — documented methods list shows no such methods
- InfluxDB.Client QueryAsync: HIGH — cited from official GitHub README
- PeriodicTimer pattern: HIGH — cited from official Microsoft docs
- STL minimum history requirement: MEDIUM — stated in seasonal_decompose source; STL itself may differ
- gRPC default 4MB limit: HIGH — cited from grpc/grpc official issue
- PyOD model size estimates: LOW — training knowledge only

**Research date:** 2026-06-10
**Valid until:** 2026-12-10 (stable libraries; PyOD 3.x API unlikely to change for MAD)
