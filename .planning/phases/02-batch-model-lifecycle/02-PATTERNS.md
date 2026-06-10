# Phase 2: Batch Path + Model Lifecycle — Pattern Map

**Mapped:** 2026-06-10
**Files analyzed:** 9
**Analogs found:** 7 / 9 (2 no analog — new capability)

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` | worker (BackgroundService) | event-driven (timer) | `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs` + `HaListenerWorker.cs` | role-match |
| `orchestrator/Argus.Orchestrator/Batch/InfluxDbReader.cs` | service | request-response | none — no existing query service | no analog |
| `orchestrator/Argus.Orchestrator/Config/ConnectionSettings.cs` | config | — | same file (modify) | exact |
| `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` | config | — | same file (modify) | exact |
| `detector/argus_detector/pyod_detector.py` | model | batch | `detector/argus_detector/hst_detector.py` | role-match |
| `detector/argus_detector/stl_detector.py` | model | batch | `detector/argus_detector/hst_detector.py` | partial |
| `detector/argus_detector/model_store.py` | service | file-I/O | none — no existing model store | no analog |
| `detector/argus_detector/servicer.py` | servicer | request-response | same file (modify) | exact |
| `detector/argus_detector/registry.py` | registry | CRUD | same file (modify) | exact |
| `proto/argus.proto` | proto contract | — | same file (modify) | exact |

---

## Pattern Assignments

### `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` (worker, timer)

**Analog:** `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs` (constructor + sealed class shape) and `orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs` (ExecuteAsync body shape)

**Imports pattern** (`MqttPublisherWorker.cs` lines 1-5, `HaListenerWorker.cs` lines 1-6):
```csharp
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
```

**Class declaration pattern** (`MqttPublisherWorker.cs` lines 17-34):
```csharp
public sealed class BatchSchedulerWorker : BackgroundService
{
    private readonly ConnectionSettings _settings;
    private readonly InfluxDbReader _influxReader;
    private readonly DetectionGateway _gateway;
    private readonly IStatePublisher _statePublisher;
    private readonly EntitiesConfig _entities;
    private readonly ILogger<BatchSchedulerWorker> _logger;

    public BatchSchedulerWorker(
        ConnectionSettings settings,
        InfluxDbReader influxReader,
        DetectionGateway gateway,
        IStatePublisher statePublisher,
        EntitiesConfig entities,
        ILogger<BatchSchedulerWorker> logger)
    {
        _settings = settings;
        _influxReader = influxReader;
        _gateway = gateway;
        _statePublisher = statePublisher;
        _entities = entities;
        _logger = logger;
    }
```

**ExecuteAsync / PeriodicTimer pattern** (from RESEARCH.md Pattern 1 — no codebase analog for PeriodicTimer; use this verbatim):
```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    // Gate on detector health before starting batch loop (same as HaListenerWorker line 38)
    await _gateway.WaitForHealthyAsync(stoppingToken);
    if (stoppingToken.IsCancellationRequested) return;

    _logger.LogInformation(LogEvents.BatchSchedulerStarted,
        "BatchSchedulerWorker starting — interval {Minutes}min", _settings.BatchIntervalMinutes);

    bool _fitRunToday = false;
    int _lastFitHour = -1;

    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_settings.BatchIntervalMinutes));
    while (await timer.WaitForNextTickAsync(stoppingToken))
    {
        try
        {
            await RunBatchAsync(stoppingToken);

            int nowHour = DateTime.Now.Hour;
            if (nowHour == _settings.NightlyFitHour && !_fitRunToday)
            {
                await RunNightlyFitAsync(stoppingToken);
                _fitRunToday = true;
            }
            // Reset daily flag when hour rolls over
            if (nowHour != _lastFitHour)
            {
                _fitRunToday = false;
                _lastFitHour = nowHour;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(LogEvents.BatchSchedulerError, ex, "Batch run failed");
        }
    }
}
```

**Structured log pattern** (`HaListenerWorker.cs` lines 34-35, `MqttPublisherWorker.cs` line 38):
```csharp
_logger.Log(LogLevel.Information, LogEvents.BatchSchedulerStarted,
    "BatchSchedulerWorker starting — interval {Minutes}min", _settings.BatchIntervalMinutes);
// or
_logger.LogInformation(LogEvents.BatchSchedulerError, "...");
```

---

### `orchestrator/Argus.Orchestrator/Batch/InfluxDbReader.cs` (service, request-response)

**No codebase analog.** Use RESEARCH.md Pattern 2 exclusively.

**Imports (new pattern — no existing analog)**:
```csharp
using InfluxDB.Client;
using InfluxDB.Client.Core.Flux.Domain;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Logging;
using Microsoft.Extensions.Logging;
```

**Core query pattern** (RESEARCH.md Pattern 2):
```csharp
public async Task<IReadOnlyList<(DateTime Timestamp, double Value)>> QueryAsync(
    string entityId, CancellationToken ct)
{
    var flux = $"""
        from(bucket: "{_settings.InfluxBucket}")
          |> range(start: -24h)
          |> filter(fn: (r) => r["_measurement"] == "{_settings.InfluxMeasurement}"
                and r["entity_id"] == "{entityId}"
                and r["_field"] == "{_settings.InfluxValueField}")
          |> sort(columns: ["_time"])
        """;
    var tables = await _queryApi.QueryAsync(flux, _settings.InfluxOrg, ct);
    var points = tables
        .SelectMany(t => t.Records)
        .Select(r => (
            Timestamp: r.GetTime()!.Value.ToDateTimeUtc(),
            // PITFALL 6: use Convert.ToDouble, NOT (double)r.GetValue()
            Value: Convert.ToDouble(r.GetValue())))
        .ToList();
    return points;
}
```

**Empty result / skip pattern** (BTCH-01 requirement — use LogEvents for structured log):
```csharp
if (points.Count == 0)
{
    _logger.LogWarning(LogEvents.BatchEntityNoData,
        "No readings in window for {EntityId} — skipping", entityId);
    return Array.Empty<(DateTime, double)>();
}
```

**DI registration** (`Program.cs` lines 39-43 as shape reference):
```csharp
// InfluxDBClient as singleton (get QueryApi per-call)
builder.Services.AddSingleton<InfluxDBClient>(_ =>
    new InfluxDBClient(connectionSettings.InfluxUrl, connectionSettings.InfluxToken));
builder.Services.AddSingleton<InfluxDbReader>();
builder.Services.AddHostedService<BatchSchedulerWorker>();
```

---

### `orchestrator/Argus.Orchestrator/Config/ConnectionSettings.cs` (config, modify)

**Analog:** Same file — `orchestrator/Argus.Orchestrator/Config/ConnectionSettings.cs`

**Existing class shape** (lines 21-42 — match exactly):
```csharp
// CONF-03: No literal defaults for secrets. Null if unset; validated at startup.
public string? InfluxUrl { get; set; }
public string? InfluxToken { get; set; }
public string? InfluxOrg { get; set; }
public string? InfluxBucket { get; set; }
// Per open question Q1: make measurement and field name configurable
public string InfluxMeasurement { get; set; } = "homeassistant";
public string InfluxValueField { get; set; } = "value";
// Batch scheduler config
public int BatchIntervalMinutes { get; set; } = 10;
public int NightlyFitHour { get; set; } = 2;
```

**Environment variable comment block** (lines 3-17 — extend the existing block, same style):
```csharp
//   ARGUS_INFLUX_URL          -> InfluxUrl
//   ARGUS_INFLUX_TOKEN        -> InfluxToken
//   ARGUS_INFLUX_ORG          -> InfluxOrg
//   ARGUS_INFLUX_BUCKET       -> InfluxBucket
//   ARGUS_INFLUX_MEASUREMENT  -> InfluxMeasurement (default: homeassistant)
//   ARGUS_INFLUX_VALUE_FIELD  -> InfluxValueField (default: value)
//   ARGUS_BATCH_INTERVAL_MIN  -> BatchIntervalMinutes (default: 10)
//   ARGUS_NIGHTLY_FIT_HOUR    -> NightlyFitHour (default: 2)
```

**Program.cs wiring** — extend the `connectionSettings` object literal (lines 21-34) to add:
```csharp
InfluxUrl = builder.Configuration["ARGUS_INFLUX_URL"],
InfluxToken = builder.Configuration["ARGUS_INFLUX_TOKEN"],
InfluxOrg = builder.Configuration["ARGUS_INFLUX_ORG"],
InfluxBucket = builder.Configuration["ARGUS_INFLUX_BUCKET"],
InfluxMeasurement = builder.Configuration["ARGUS_INFLUX_MEASUREMENT"] ?? "homeassistant",
InfluxValueField = builder.Configuration["ARGUS_INFLUX_VALUE_FIELD"] ?? "value",
BatchIntervalMinutes = int.TryParse(builder.Configuration["ARGUS_BATCH_INTERVAL_MIN"], out var bim) ? bim : 10,
NightlyFitHour = int.TryParse(builder.Configuration["ARGUS_NIGHTLY_FIT_HOUR"], out var nfh) ? nfh : 2,
```

---

### `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` (config, modify)

**Analog:** Same file — `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs`

**Existing range pattern** (lines 9-37 — add 5xxx block after existing 4xxx block, same style):
```csharp
// Batch scheduler (5xxx)
public static readonly EventId BatchSchedulerStarted   = new(5001, nameof(BatchSchedulerStarted));
public static readonly EventId BatchSchedulerStopped   = new(5002, nameof(BatchSchedulerStopped));
public static readonly EventId BatchSchedulerError     = new(5003, nameof(BatchSchedulerError));
public static readonly EventId BatchEntityNoData       = new(5004, nameof(BatchEntityNoData));
public static readonly EventId BatchColdStartFit       = new(5005, nameof(BatchColdStartFit));
public static readonly EventId BatchScoredEntity       = new(5006, nameof(BatchScoredEntity));
public static readonly EventId NightlyFitStarted       = new(5007, nameof(NightlyFitStarted));
public static readonly EventId NightlyFitCompleted     = new(5008, nameof(NightlyFitCompleted));
public static readonly EventId ModelSaved              = new(5009, nameof(ModelSaved));
public static readonly EventId ModelLoaded             = new(5010, nameof(ModelLoaded));
public static readonly EventId ModelVersionMismatch    = new(5011, nameof(ModelVersionMismatch));
```

---

### `detector/argus_detector/pyod_detector.py` (model, batch)

**Analog:** `detector/argus_detector/hst_detector.py`

**Module docstring + imports pattern** (`hst_detector.py` lines 1-18):
```python
"""
PyOD MAD batch anomaly detector.

PyODDetector: per-entity batch anomaly detector using pyod.models.mad.MAD.
  - Univariate constraint: X must be shape (n, 1) — enforced by MAD (CRITICAL FINDING)
  - fit(values): trains MAD on float list; sets _fitted flag
  - score_batch(values): returns decision_function() scores (continuous, NOT predict() labels)
  - from_params(): overrides threshold/contamination from string params map (CONF-02)

Thread safety: instances are swapped atomically by DetectorRegistry.fit_one();
scoring runs outside the lock on a snapshot reference (MDL-04).
"""

from __future__ import annotations

import numpy as np
from pyod.models.mad import MAD
```

**Class structure with from_params** (`hst_detector.py` lines 38-68 as shape):
```python
_DEFAULT_THRESHOLD = 3.5
_DEFAULT_CONTAMINATION = 0.1


def _cast_float(params: dict[str, str], key: str, default: float) -> float:
    raw = params.get(key)
    if raw is None:
        return default
    try:
        return float(raw)
    except (ValueError, TypeError):
        return default


class PyODDetector:
    def __init__(
        self,
        threshold: float = _DEFAULT_THRESHOLD,
        contamination: float = _DEFAULT_CONTAMINATION,
    ) -> None:
        self._model = MAD(threshold=threshold, contamination=contamination)
        self._fitted = False

    @classmethod
    def from_params(cls, params: dict[str, str]) -> "PyODDetector":
        threshold = _cast_float(params, "threshold", _DEFAULT_THRESHOLD)
        contamination = _cast_float(params, "contamination", _DEFAULT_CONTAMINATION)
        return cls(threshold=threshold, contamination=contamination)

    def fit(self, values: list[float]) -> None:
        # MAD requires X.shape == (n, 1) — univariate; reshape enforced here
        X = np.array(values, dtype=float).reshape(-1, 1)
        self._model.fit(X)
        self._fitted = True

    def score_batch(self, values: list[float]) -> list[float]:
        if not self._fitted:
            raise ValueError("fit() must be called before score_batch()")
        X = np.array(values, dtype=float).reshape(-1, 1)
        # decision_function(): continuous scores (higher = more anomalous)
        # Do NOT use predict() — returns binary 0/1 labels
        return self._model.decision_function(X).tolist()

    @property
    def is_fitted(self) -> bool:
        return self._fitted
```

---

### `detector/argus_detector/stl_detector.py` (model, batch, stateless)

**Analog:** `detector/argus_detector/hst_detector.py` (docstring + module shape only; STL is stateless)

**Module docstring + imports** (`hst_detector.py` lines 1-18 as docstring shape):
```python
"""
STL residual-based batch anomaly detector (step-change / FAULT-03).

StlDetector: stateless decomposition detector using statsmodels STL.
  - score_batch(values, period): decomposes window, returns abs(residual) scores normalised [0,1]
  - robust=True mandatory: standard STL absorbs anomalies into seasonal component
  - MINIMUM DATA GUARD: requires len(values) >= 2 * period; returns error string if insufficient
  - No persistent model — no fit() / no serialization needed
  - For 60s-interval sensors: period=1440 (daily), minimum=2880 points (48h of data)
    NOTE: 24h window provides max 1440 points — STL will return "insufficient history" in Phase 2
          until BatchIntervalHistoryHours is extended to 48h+ in Phase 3

Thread safety: stateless; safe to call from multiple threads concurrently.
"""

from __future__ import annotations

import numpy as np
from statsmodels.tsa.seasonal import STL
```

**Core scoring method** (RESEARCH.md Pattern 4):
```python
_PERIOD_DAILY = 1440   # 60s-interval sensor × 1440 = 24h


class StlDetector:
    """Stateless STL residual scorer. No fit(), no saved model."""

    def score_batch(
        self,
        values: list[float],
        period: int = _PERIOD_DAILY,
    ) -> tuple[list[float], str | None]:
        """Returns (scores, error_message). error_message non-None on insufficient data."""
        n = len(values)
        if n < 2 * period:
            return [], f"insufficient history: got {n} points, need >= {2 * period}"

        x = np.array(values, dtype=float)
        result = STL(x, period=period, robust=True).fit()
        residuals = np.abs(result.resid)
        rng = residuals.max() - residuals.min()
        if rng == 0:
            return [0.0] * n, None
        scores = ((residuals - residuals.min()) / rng).tolist()
        return scores, None
```

---

### `detector/argus_detector/model_store.py` (service, file-I/O)

**No codebase analog.** Use RESEARCH.md Pattern 5 exclusively.

**Module docstring + imports**:
```python
"""
ModelStore — versioned per-entity model persistence.

Directory layout:
  models/{entity_slug}/{detector}/v{N}/model.joblib   (PyOD models)
  models/{entity_slug}/{detector}/v{N}/model.pkl      (River HST models)
  models/{entity_slug}/{detector}/v{N}/version.json   (sidecar metadata)
  models/{entity_slug}/{detector}/latest              (contains int N, atomic write)

Retention: 3 most recent versions per (entity_slug, detector) — prune on save.
Serialization:
  PyOD (MAD): joblib.dump / joblib.load
  River HST:  pickle.dump / pickle.load
  PITFALL 3: River to_dict() does NOT exist — use pickle only.
"""

from __future__ import annotations

import grpc
import joblib
import json
import pathlib
import pickle
import shutil

import pyod
import river
```

**Save pattern** (RESEARCH.md Pattern 5 — use as written):
```python
MODEL_ROOT = pathlib.Path("/var/argus/models")
_KEEP_VERSIONS = 3


class ModelStore:
    def __init__(self, root: pathlib.Path = MODEL_ROOT) -> None:
        self._root = root

    def save_pyod(self, entity_slug: str, detector: str, version: int, model: object) -> None:
        d = self._model_dir(entity_slug, detector, version)
        d.mkdir(parents=True, exist_ok=True)
        joblib.dump(model, d / "model.joblib")
        self._write_version_json(d, entity_slug, detector, version)
        self._update_latest(entity_slug, detector, version)
        self._prune(entity_slug, detector)

    def save_river(self, entity_slug: str, detector: str, version: int, model: object) -> None:
        d = self._model_dir(entity_slug, detector, version)
        d.mkdir(parents=True, exist_ok=True)
        with open(d / "model.pkl", "wb") as f:
            pickle.dump(model, f)
        self._write_version_json(d, entity_slug, detector, version)
        self._update_latest(entity_slug, detector, version)
        self._prune(entity_slug, detector)

    def load_pyod(self, entity_slug: str, detector: str, version: int | None = None) -> object:
        v = version if version is not None else self._read_latest(entity_slug, detector)
        return joblib.load(self._model_dir(entity_slug, detector, v) / "model.joblib")

    def load_river(self, entity_slug: str, detector: str, version: int | None = None) -> object:
        v = version if version is not None else self._read_latest(entity_slug, detector)
        with open(self._model_dir(entity_slug, detector, v) / "model.pkl", "rb") as f:
            return pickle.load(f)

    def _model_dir(self, slug: str, detector: str, version: int) -> pathlib.Path:
        return self._root / slug / detector / f"v{version}"

    def _write_version_json(
        self, d: pathlib.Path, entity_slug: str, detector: str, version: int
    ) -> None:
        meta = {
            "version": version,
            "entity_id": entity_slug,   # store slug; caller maps entity_id -> slug
            "detector": detector,
            "created_at": __import__("datetime").datetime.utcnow().isoformat(),
            "grpcio_version": grpc.__version__,
            "pyod_version": pyod.__version__,
            "river_version": river.__version__,
        }
        (d / "version.json").write_text(json.dumps(meta))

    def _update_latest(self, slug: str, detector: str, version: int) -> None:
        # Atomic write via tmp → rename (RESEARCH.md Pattern 5)
        latest = self._root / slug / detector / "latest"
        tmp = latest.with_suffix(".tmp")
        tmp.write_text(str(version))
        tmp.replace(latest)

    def _read_latest(self, slug: str, detector: str) -> int:
        latest = self._root / slug / detector / "latest"
        return int(latest.read_text().strip())

    def _prune(self, slug: str, detector: str) -> None:
        base = self._root / slug / detector
        versions = sorted(
            [int(d.name[1:]) for d in base.iterdir() if d.is_dir() and d.name.startswith("v")],
            reverse=True,
        )
        for old_v in versions[_KEEP_VERSIONS:]:
            shutil.rmtree(base / f"v{old_v}", ignore_errors=True)
```

---

### `detector/argus_detector/servicer.py` (servicer, request-response, modify)

**Analog:** Same file — `detector/argus_detector/servicer.py`

**Existing method shape to copy** (`servicer.py` lines 33-88):
```python
# Each new RPC method follows exact same signature as existing ScoreStream / Fit:
def ScoreBatch(self, request, context):  # noqa: N802
def SaveModel(self, request, context):   # noqa: N802
def LoadModel(self, request, context):   # noqa: N802
```

**Error handling pattern** (`servicer.py` lines 81-84 — copy exactly):
```python
except Exception:
    logger.exception("unexpected error in ScoreBatch for %s", request.entity_id)
    context.abort(grpc.StatusCode.INTERNAL, "scoring error")
    return
```

**Cold-start Fit-before-Score pattern** (CONTEXT.md specifics + LogEvents):
```python
def ScoreBatch(self, request, context):  # noqa: N802
    if not request.entity_id:
        context.abort(grpc.StatusCode.INVALID_ARGUMENT, "empty entity_id")
        return

    try:
        values = [p.value.value for p in request.window]
        detector = request.detector or "mad"

        # Cold-start: if no model, fit now (BTCH cold-start spec)
        if not self._registry.has_model(request.entity_id, detector):
            logger.info("cold start fit", extra={"entity_id": request.entity_id, "detector": detector})
            self._registry.fit_one(request.entity_id, detector, values)

        scores, error = self._registry.score_batch(request.entity_id, detector, values)
        if error:
            return argus_pb2.ScoreBatchResponse(ok=False, error=error)

        # Build one Verdict per window point
        ts = timestamp_pb2.Timestamp()
        ts.GetCurrentTime()
        verdicts = [
            argus_pb2.Verdict(
                entity_id=request.entity_id,
                score=wrappers_pb2.DoubleValue(value=s),
                is_anomaly=False,   # orchestrator's hysteresis gate decides
                detector=detector,
                timestamp=ts,
            )
            for s in scores
        ]
        return argus_pb2.ScoreBatchResponse(verdicts=verdicts, ok=True)
    except Exception:
        logger.exception("unexpected error in ScoreBatch for %s", request.entity_id)
        context.abort(grpc.StatusCode.INTERNAL, "scoring error")
        return
```

**Imports to add** (extend `servicer.py` lines 18-22):
```python
# Already present: grpc, timestamp_pb2, wrappers_pb2, argus_pb2, argus_pb2_grpc, DetectorRegistry, logger, time
# Add:
from argus_detector.model_store import ModelStore
```

---

### `detector/argus_detector/registry.py` (registry, CRUD, modify)

**Analog:** Same file — `detector/argus_detector/registry.py`

**Existing lock pattern** (`registry.py` lines 29-46 — copy for per-entity locks):
```python
# Existing: single self._lock guards dict creation
# Phase 2: add per-entity locks dict (MDL-04) alongside existing _lock
self._entity_locks: dict[tuple[str, str], threading.Lock] = {}
```

**fit_one() using train-outside-lock pattern** (RESEARCH.md Pattern 6 — maps to existing `_get_or_create` shape):
```python
def _entity_lock(self, key: tuple[str, str]) -> threading.Lock:
    # Use existing self._lock to guard creation of per-entity locks
    with self._lock:
        if key not in self._entity_locks:
            self._entity_locks[key] = threading.Lock()
        return self._entity_locks[key]

def fit_one(self, entity_id: str, detector: str, values: list[float]) -> None:
    key = (entity_id, detector)
    lock = self._entity_lock(key)
    with lock:
        current = self._detectors.get(key)
    # Deep-copy OUTSIDE lock — CPU-bound fit does not block score_one (MDL-04)
    import copy
    candidate = copy.deepcopy(current) if current else self._create_detector(detector)
    candidate.fit(values)
    with lock:
        self._detectors[key] = candidate

def score_batch(
    self, entity_id: str, detector: str, values: list[float]
) -> tuple[list[float], str | None]:
    key = (entity_id, detector)
    lock = self._entity_lock(key)
    with lock:
        model = self._detectors.get(key)
    if model is None:
        raise ValueError(f"No model for {key}; call fit_one first")
    return model.score_batch(values)

def has_model(self, entity_id: str, detector: str) -> bool:
    return (entity_id, detector) in self._detectors
```

**_create_detector factory** (maps detector name to class — note CRITICAL FINDING):
```python
def _create_detector(self, detector: str) -> object:
    if detector in ("mad", "robust_zscore"):   # map alias — PITFALL 2
        from argus_detector.pyod_detector import PyODDetector
        return PyODDetector()
    if detector == "stl":
        from argus_detector.stl_detector import StlDetector
        return StlDetector()
    if detector == "hst":
        return EntityDetector()   # existing streaming detector
    raise ValueError(f"Unknown detector: {detector!r}")
```

---

### `proto/argus.proto` (proto contract, modify)

**Analog:** Same file — `proto/argus.proto`

**Existing message shape to follow** (`argus.proto` lines 8-35):
```protobuf
// Reuse Point and Verdict types — do NOT duplicate
// FitRequest pattern (lines 25-30) is the shape for ScoreBatchRequest

message ScoreBatchRequest {
  string entity_id = 1;
  string detector = 2;
  map<string, string> params = 3;
  repeated Point window = 4;        // same Point type as FitRequest field 4
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
```

**Service block extension** (`argus.proto` lines 37-40 — add three RPCs to existing service):
```protobuf
service DetectorService {
  rpc ScoreStream(stream Point) returns (stream Verdict);
  rpc Fit(FitRequest) returns (FitResponse);
  rpc ScoreBatch(ScoreBatchRequest) returns (ScoreBatchResponse);    // Phase 2
  rpc SaveModel(SaveModelRequest) returns (SaveModelResponse);       // Phase 2
  rpc LoadModel(LoadModelRequest) returns (LoadModelResponse);       // Phase 2
}
```

**Post-edit stubs regeneration** (must run before any implementation):
```bash
# Python stubs (from detector/ directory)
python -m grpc_tools.protoc -I../proto --python_out=argus_detector/proto --grpc_python_out=argus_detector/proto ../proto/argus.proto
# .NET stubs — automatic on next build via Grpc.Tools MSBuild target
```

---

### `detector/argus_detector/server.py` (modify — startup model load gate, MDL-03)

**Analog:** Same file — `detector/argus_detector/server.py`

**Existing health pattern** (`server.py` lines 66-71 — modify to gate on model load):
```python
# Replace current immediate SERVING set:
#   health_servicer.set("argus.v1.DetectorService", health_pb2.HealthCheckResponse.SERVING)
# With deferred gate:

health_servicer.set("argus.v1.DetectorService", health_pb2.HealthCheckResponse.NOT_SERVING)
health_servicer.set("", health_pb2.HealthCheckResponse.NOT_SERVING)

model_store = ModelStore()
model_store.load_all_into(registry)   # populates DetectorRegistry from disk; logs warnings on missing

health_servicer.set("argus.v1.DetectorService", health_pb2.HealthCheckResponse.SERVING)
health_servicer.set("", health_pb2.HealthCheckResponse.SERVING)
```

**Import addition** (extend `server.py` lines 19-24):
```python
from argus_detector.model_store import ModelStore
```

---

## Shared Patterns

### Structured Logging (.NET)
**Source:** `orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs` lines 34-35; `MqttPublisherWorker.cs` line 38
**Apply to:** `BatchSchedulerWorker.cs`, `InfluxDbReader.cs`
```csharp
// Use LogEvents event ID + named parameters always
_logger.Log(LogLevel.Information, LogEvents.BatchSchedulerStarted, "message {Param}", value);
_logger.LogInformation(LogEvents.BatchEntityNoData, "No data for {EntityId}", entityId);
_logger.LogError(LogEvents.BatchSchedulerError, ex, "Batch run failed");
```

### gRPC Channel Singleton
**Source:** `orchestrator/Argus.Orchestrator/Detection/DetectionGateway.cs` lines 21-31; `Program.cs` lines 39-43
**Apply to:** `BatchSchedulerWorker.cs` — inject `DetectionGateway`; do NOT create a second channel
```csharp
// BatchSchedulerWorker receives DetectionGateway via DI — calls DetectorClient directly
var response = await _gateway.DetectorClient.ScoreBatchAsync(request, cancellationToken: ct);
```

### Verdict Publishing
**Source:** `orchestrator/Argus.Orchestrator/Mqtt/StatePublisher.cs` lines 42-56
**Apply to:** `BatchSchedulerWorker.RunBatchAsync()` — after receiving `ScoreBatchResponse`, publish each verdict using existing methods
```csharp
await _statePublisher.PublishFlagAsync(entityId, isAnomaly, ct);
await _statePublisher.PublishScoreAsync(entityId, score, ct);
```

### gRPC Method Signature (Python)
**Source:** `detector/argus_detector/servicer.py` lines 33, 86
**Apply to:** All new servicer methods (`ScoreBatch`, `Fit`, `SaveModel`, `LoadModel`)
```python
def ScoreBatch(self, request, context):  # noqa: N802
    # Always guard empty entity_id first (matches existing ScoreStream line 44)
    if not request.entity_id:
        context.abort(grpc.StatusCode.INVALID_ARGUMENT, "empty entity_id")
        return
```

### entity_slug Formula
**Source:** `orchestrator/Argus.Orchestrator/Mqtt/UniqueId.cs` (used in `StatePublisher.cs` lines 30-36)
**Apply to:** `ModelStore._model_dir()` — entity_slug uses same `.` → `_` substitution
```python
entity_slug = entity_id.replace(".", "_")
```

---

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `orchestrator/Argus.Orchestrator/Batch/InfluxDbReader.cs` | service | request-response | No existing query services in orchestrator; all I/O is WebSocket/MQTT in Phase 1 |
| `detector/argus_detector/model_store.py` | service | file-I/O | No existing file I/O in detector; Phase 1 is fully in-memory |

Both files must be built from RESEARCH.md patterns (Patterns 2 and 5 respectively).

---

## Metadata

**Analog search scope:** `orchestrator/Argus.Orchestrator/`, `detector/argus_detector/`, `proto/`
**Files scanned:** 10 (HaListenerWorker, MqttPublisherWorker, ConnectionSettings, LogEvents, DetectionGateway, StatePublisher, Program.cs, hst_detector, servicer, registry, server.py, argus.proto)
**Pattern extraction date:** 2026-06-10

### Critical Findings Carried Forward from RESEARCH.md

1. `pyod.models.robust_zscore.RobustZScore` does NOT exist — use `pyod.models.mad.MAD`. The `_create_detector` factory maps both `"mad"` and `"robust_zscore"` to `PyODDetector(MAD(...))`.
2. `river.HalfSpaceTrees.to_dict()` does NOT exist — use `pickle.dump`/`pickle.load` for River HST.
3. STL requires `>= 2 * period` points. With 24h window (1440 points) and `period=1440`, STL will always return `ok=False, error="insufficient history"` in Phase 2. This is correct behavior, not a bug.
4. Use `threading.Lock` (not `asyncio.Lock`) everywhere in the grpcio sync server.
5. Use `Convert.ToDouble(r.GetValue())` in InfluxDbReader, not direct cast `(double)r.GetValue()`.
