# Phase 15: Streaming State Persistence + Warm-up Backfill - Pattern Map

**Mapped:** 2026-08-03
**Files analyzed:** 16 (new/modified across detector, proto, orchestrator, tests)
**Analogs found:** 15 / 16 (1 has no true analog — the periodic checkpoint-writer thread — flagged explicitly, not forced)

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `detector/argus_detector/model_store.py` (+`save_checkpoint`/`load_checkpoint`) | model/persistence | file-I/O | `ModelStore._update_latest` (same file, lines 326-335) | exact |
| `detector/argus_detector/registry.py` (+`dirty_hst_keys`/`checkpoint_dirty`/`get_n_seen`/`warmup_one`) | model/service | event-driven | `DetectorRegistry.fit_one` (same file, lines 124-183) | exact (lock discipline) |
| `detector/argus_detector/hst_detector.py` (+`n_seen`/`window` properties) | model | transform | `EntityDetector.is_warmed_up` (same file, lines 90-93) | exact |
| checkpoint writer thread (new, likely in `registry.py` or `server.py`) | utility/daemon-thread | batch/event-driven | **No true analog in Python tree.** Closest design reference: `BatchSchedulerWorker` (.NET `BackgroundService`, `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs:31-76`) | none (see "No Analog Found") |
| `detector/argus_detector/server.py` (+SIGTERM handler) | config/lifecycle | event-driven | same file's existing MDL-03 health-gate sequencing (lines 71-87) | role-match |
| `detector/argus_detector/servicer.py` (+`Warmup` RPC handler) | controller/route (gRPC servicer) | request-response | `DetectorServicer.Fit` (same file, lines 95-125) | exact |
| `detector/argus_detector/config.py` (+`ARGUS_CHECKPOINT_*`) | config | — | same file's existing `ARGUS_MODEL_ROOT` pattern (lines 24-30) | exact |
| `proto/argus.proto` (`Point.params`, `Verdict.warmed_up/n_seen/window`, `Warmup` RPC+messages) | schema/contract | request-response | `FitRequest`/`FitResponse` (same file, lines 25-35) + `ScoreBatchRequest` (lines 37-42) | exact |
| `orchestrator/Argus.Orchestrator/Batch/InfluxDbReader.cs` (+`QueryHistoryAsync`) | service/query | CRUD (read) | `GroupInfluxReader.QueryGroupAsync` (`orchestrator/Argus.Orchestrator/Batch/GroupInfluxReader.cs:53-148`) — a new class built alongside `InfluxDbReader` reusing the same seam | exact |
| `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs` (warm-up call site, `EntityStatusCache.Set` relocation) | controller/pipeline | streaming | same file's existing write/read loop split (lines 130-224) | exact (self-modification) |
| `orchestrator/Argus.Orchestrator/Detection/EntityRuntimeState.cs` (verdict-driven warm-up) | model/state | transform | same file's existing `RecordReading`/`WarmedUp` (lines 22-63) | exact (self-modification) |
| `orchestrator/Argus.Orchestrator/Detection/EntityStatusCache.cs` | store/cache | CRUD | unchanged — consumer site moves, class itself likely untouched | exact (no-op) |
| `orchestrator/Argus.Orchestrator/Detection/FrozenSensorDetector.cs` (priming call site added at call-site, class likely untouched) | model | transform | `FrozenSensorDetector.AddReading` (same file, lines 27-32) — reused as-is | exact |
| Warmup .NET client call site (in `ScoreStreamPipeline` or a new helper before opening `ScoreStream`) | controller/client | request-response | .NET side of `Fit`/`FitResponse` call pattern — see `BatchSchedulerWorker`'s `_detectorClient.FitAsync`-style call (via `IBatchDetectorClient`) | role-match |
| `detector/tests/test_restart_resilience.py`, `test_model_store.py` | test | file-I/O / event-driven | existing `test_registry.py` (MDL-04 lock tests) + `model_store.py`'s own `load_all_into` try/except tests | role-match |
| Orchestrator pipeline/state tests (`ScoreStreamPipelineTests`, `EntityRuntimeStateTests`, `EntityStatusCacheTests` if present) + Influx seam fakes | test | CRUD/streaming | `BatchSchedulerWorkerTests.FakeInfluxDbReader`/`FakeBatchDetectorClient` (`orchestrator/Argus.Orchestrator.Tests/BatchSchedulerWorkerTests.cs:23-38`) | exact |

## Pattern Assignments

### `detector/argus_detector/model_store.py` — `save_checkpoint`/`load_checkpoint` (model/persistence, file-I/O)

**Analog:** `ModelStore._update_latest` (`model_store.py:326-335`), verbatim per RESEARCH.md:

```python
# Source: model_store.py:326-335 (_update_latest)
def _update_latest(self, slug: str, detector: str, version: int) -> None:
    """Atomically update the latest pointer file.

    Writes to a .tmp file then renames, ensuring no reader sees a partial
    state even if the process is interrupted mid-write.
    """
    latest = self._root / slug / detector / "latest"
    tmp = latest.with_suffix(".tmp")
    tmp.write_text(str(version))
    tmp.replace(latest)  # atomic on POSIX; MoveFileExW on Windows
```

Also reuse `_write_version_json`'s sidecar-metadata shape (`model_store.py:308-324`) for the `checkpoint.json` sidecar's `river_version`/`saved_at` fields — same `grpc.__version__`/`river.__version__`/`datetime.now(timezone.utc).isoformat()` idiom.

**Error handling / restore pattern:** `load_all_into`'s per-entity try/except-and-continue (`model_store.py:290-295`):

```python
# Source: model_store.py:290-295
except Exception:
    logger.warning(
        "Failed to load model for slug=%s detector=%s; skipping",
        slug, detector,
        exc_info=True,
    )
```

Extend this exact wrapping around the new `checkpoint.pkl` glob pass (D-09), running it strictly *after* the existing `latest` glob pass per RESEARCH Pitfall 2 (checkpoint must win on key collision), with a code comment making the ordering explicit.

---

### `detector/argus_detector/registry.py` — dirty-tracking + checkpoint sweep (model/service, event-driven)

**Analog:** `DetectorRegistry.fit_one` (`registry.py:124-183`) — the MDL-04 snapshot-under-lock / expensive-work-outside-lock precedent, extracted verbatim:

```python
# Source: registry.py:160-183 (fit_one, train-outside-lock idiom)
key = (entity_id, detector)
lock = self._entity_lock(key)

# Snapshot current model reference under lock
with lock:
    current = self._detectors.get(key)

# Deep-copy before training — CPU-bound; runs OUTSIDE lock (MDL-04).
...
candidate = copy.deepcopy(current) if current else self._create_detector(detector, params)
candidate.fit(values)

# Atomic swap
with lock:
    self._detectors[key] = candidate
```

**Lock creation helper to reuse as-is:** `_entity_lock` (`registry.py:114-122`):

```python
def _entity_lock(self, key: tuple[str, str]) -> threading.Lock:
    with self._lock:
        if key not in self._entity_locks:
            self._entity_locks[key] = threading.Lock()
        return self._entity_locks[key]
```

**New method shape (per RESEARCH.md Pattern 1 — must be a `DetectorRegistry` method, not an external collaborator, since `_detectors`/`_entity_locks` are private):**

```python
# Source: pattern derived from fit_one()'s train-outside-lock idiom (RESEARCH.md Pattern 1)
def checkpoint_dirty(self, model_store, last_checkpointed: dict[tuple[str, str], int]) -> None:
    for key in self._hst_keys():
        entity_id, detector = key
        lock = self._entity_lock(key)
        with lock:
            det = self._detectors.get(key)
            if det is None:
                continue
            current_n_seen = det.n_seen
            if current_n_seen == last_checkpointed.get(key):
                continue                                 # D-05: not dirty — skip
            snapshot = copy.deepcopy(det)                # under lock (D-06) — MEASURED 56-96ms at defaults
        # pickle + atomic write OUTSIDE the lock (D-06)
        model_store.save_checkpoint(entity_id.replace(".", "_"), detector, snapshot, entity_id, current_n_seen)
        last_checkpointed[key] = current_n_seen
        time.sleep(0)  # Pitfall 1: yield between entities — deepcopy is 56-96ms at defaults
```

**Warmup gate (`n_seen == 0`) belongs here too**, mirroring `_get_or_create`'s "lazily creates, gate concern is registry-owned" idiom (`registry.py:60-72`):

```python
# Source: registry.py:60-72 (_get_or_create) — same "registry owns the gate" idiom, applied to warmup_one
def _get_or_create(self, entity_id, detector, params):
    key = (entity_id, detector)
    with self._lock:
        if key not in self._detectors:
            self._detectors[key] = EntityDetector.from_params(params or {})
        return self._detectors[key]
```

---

### `detector/argus_detector/hst_detector.py` — `n_seen`/`window` accessors (model, transform)

**Analog:** existing `is_warmed_up` property, same file:

```python
# Source: hst_detector.py:90-93
@property
def is_warmed_up(self) -> bool:
    """True when at least window_size readings have been processed."""
    return self._n_seen >= self._model.window_size
```

Add two additive siblings (`n_seen` → `self._n_seen`, `window` → `self._model.window_size`) in the same style — no other changes to `EntityDetector`'s constructor/`from_params`/`score_one`.

---

### Checkpoint writer thread — NO STRONG ANALOG (flagged explicitly)

No existing Python code in this repo runs a periodic background daemon thread. The detector process is purely request-driven (gRPC servicer) plus a one-shot startup load (`load_all_into`). Do not force a weak match.

**Design reference only (different language/framework, use for shape not code):** `BatchSchedulerWorker` (`orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs:31-76`), a .NET `BackgroundService` that ticks on an interval, queries a data source, and isolates per-entity exceptions:

```csharp
// Source: BatchSchedulerWorker.cs:15-30 (docstring) — shape reference only, not code to port
/// Per tick:
///   1. Queries InfluxDB for each entity (BTCH-01).
///   2. Calls ScoreBatchAsync per entity/detector (BTCH-02/BTCH-04).
///   3. Publishes last verdict via IStatePublisher.
/// Fault isolation (T-02-04-04):
///   Per-entity exceptions are caught and logged; worker never dies from a single entity failure.
```

For Python, per RESEARCH.md's Standard Stack table, build the writer directly on stdlib `threading.Thread` + `threading.Event.wait(timeout)` (interruptible sleep, needed for SIGTERM-triggered immediate flush) — there is no in-repo Python precedent for this shape, so follow RESEARCH.md's Pattern 1/Pattern 3 code examples verbatim rather than adapting an existing file.

---

### `detector/argus_detector/server.py` — SIGTERM flush wiring (config/lifecycle, event-driven)

**Analog:** the file's own existing MDL-03 health-gate sequencing discipline (`server.py:71-87`) — same "wire a lifecycle event around the registry/model_store pair" shape:

```python
# Source: server.py:71-87 (MDL-03 gate) — sequencing precedent for the new SIGTERM handler
health_servicer.set("argus.v1.DetectorService", health_pb2.HealthCheckResponse.NOT_SERVING)
health_servicer.set("", health_pb2.HealthCheckResponse.NOT_SERVING)

registry = DetectorRegistry()
root = model_root if model_root is not None else MODEL_ROOT
model_store = ModelStore(root=root)
model_store.load_all_into(registry)

servicer = DetectorServicer(registry, model_store)
argus_pb2_grpc.add_DetectorServiceServicer_to_server(servicer, server)

health_servicer.set("argus.v1.DetectorService", health_pb2.HealthCheckResponse.SERVING)
health_servicer.set("", health_pb2.HealthCheckResponse.SERVING)
```

`serve()`'s current bare block to modify (`server.py:121-129`):

```python
def serve() -> None:
    config = DetectorConfig()
    configure_logging(config.log_level)
    server = create_server(port=config.grpc_port, config=config, model_root=pathlib.Path(config.model_root))
    server.start()
    logger.info("detector started", extra={"port": config.grpc_port})
    server.wait_for_termination()
```

RESEARCH.md's exact SIGTERM handler code (Pattern 3) is the target shape — install `signal.signal(signal.SIGTERM, handler)` before `server.start()`, handler calls `registry.checkpoint_dirty(...)` then `server.stop(grace).wait(...)`.

---

### `detector/argus_detector/servicer.py` — `Warmup` RPC handler (controller/route, request-response)

**Analog:** `DetectorServicer.Fit` (`servicer.py:95-125`) — closest existing unary RPC shape ("prime with a repeated Point window"):

```python
# Source: servicer.py:95-125 (Fit)
def Fit(self, request, context):  # noqa: N802
    if not request.entity_id:
        context.abort(grpc.StatusCode.INVALID_ARGUMENT, "empty entity_id")
        return None  # WR-06: after abort, gRPC ignores the return value — return None

    try:
        entity_id = request.entity_id
        detector = request.detector or "mad"
        values = [p.value.value for p in request.window]
        entity_slug = entity_id.replace(".", "_")

        version = self._model_store.next_version(entity_slug, detector)
        self._registry.fit_one(entity_id, detector, values)

        model = self._registry.get_model(entity_id, detector)
        if model is not None:
            self._save_model_to_store(entity_slug, detector, version, model, entity_id=entity_id)

        return argus_pb2.FitResponse(ok=True)

    except Exception as e:
        logger.exception("unexpected error in Fit for %s", request.entity_id)
        return argus_pb2.FitResponse(ok=False, error=str(e))
```

New `Warmup` handler follows the same shape but calls `self._registry.warmup_one(entity_id, "hst", history_values, params)` instead of `fit_one`+`save`, and returns `n_seen`/`warmed_up` instead of `ok`/`error` only. `ScoreStream`'s existing param-less call (`servicer.py:63`, the D3 bug site) must change to forward `point.params`:

```python
# Source: servicer.py:63 (current — the D3 bug) — change to pass params=dict(point.params)
score: float = self._registry.score_one(entity_id, value)
```

---

### `detector/argus_detector/config.py` — `ARGUS_CHECKPOINT_*` (config)

**Analog:** the file's existing `ARGUS_MODEL_ROOT` line (`config.py:30`):

```python
# Source: config.py:24-30
self.grpc_port: int = int(os.environ.get("ARGUS_GRPC_PORT", "50051"))
...
self.model_root: str = os.environ.get("ARGUS_MODEL_ROOT", "/var/argus/models")
```

Add `self.checkpoint_interval_sec = int(os.environ.get("ARGUS_CHECKPOINT_INTERVAL_SEC", "300"))` and `self.checkpoint_enabled = os.environ.get("ARGUS_CHECKPOINT_ENABLED", "true").lower() == "true"` in the same style. **Do not** add `ARGUS_BACKFILL_*` here — RESEARCH.md Pitfall 3 explicitly flags this as orchestrator-side (.NET `ConnectionSettings`), not Python.

---

### `proto/argus.proto` — `Point.params`, `Verdict` new fields, `Warmup` RPC (schema, request-response)

**Analog:** `FitRequest`/`FitResponse` (lines 25-35) for the RPC message shape; `ScoreBatchRequest.params` (line 40) for the map field convention:

```protobuf
// Source: proto/argus.proto:25-35
message FitRequest {
  string entity_id = 1;
  string detector = 2;
  map<string, string> params = 3;
  repeated Point window = 4;
}

message FitResponse {
  bool ok = 1;
  string error = 2;
}
```

Apply the exact same `map<string, string> params = N;` field to `Point` (per RESEARCH.md Pattern 4 recommendation, field number 4). For `Warmup`, RESEARCH.md's Open Question 1 recommends mirroring `FitRequest` exactly: `repeated Point history` + `map<string, string> params`, returning `n_seen`/`warmed_up` (mirrors `ScoreBatchResponse`'s `ok`/`error` plus payload shape, lines 44-48).

`Verdict` to extend (lines 14-23) — add `bool warmed_up`, `int32 n_seen`, `int32 window` as new trailing fields (never renumber existing 1-8).

`service DetectorService` block (lines 111-119) — add `rpc Warmup(WarmupRequest) returns (WarmupResponse);` alongside the existing 7 RPCs, same style.

---

### `orchestrator/Argus.Orchestrator/Batch/InfluxDbReader.cs` — `QueryHistoryAsync` (service/query, CRUD-read)

**Analog:** `GroupInfluxReader` (`orchestrator/Argus.Orchestrator/Batch/GroupInfluxReader.cs`) — explicitly built as a **new class** reusing the existing `IInfluxQueryApi` seam without touching `InfluxDbReader`, which is the exact shape D-13 asks for `QueryHistoryAsync` (a new method *alongside* the existing one, same class this time since D-13 says "alongside" not "new class" — but the injection-guard convention transplants directly):

**`_safeFluxString` injection-guard convention** (`InfluxDbReader.cs:18-19`, reused verbatim by `GroupInfluxReader.cs:23-24` with one extra excluded char):

```csharp
// Source: InfluxDbReader.cs:15-19
// T-02-02-02: allowlist guard — reject values that contain double-quote or backslash
// which would allow Flux string-literal injection. Entity IDs and config field names
// are operator-controlled (accepted risk), but must not contain these characters.
private static readonly Regex _safeFluxString =
    new(@"^[^""\\]+$", RegexOptions.Compiled);
```

**Guard-then-query shape** (`InfluxDbReader.cs:52-88`, and how `GroupInfluxReader` extends it for new interpolated values at lines 76-94):

```csharp
// Source: InfluxDbReader.cs:68-77 — apply identically to lookback (validated string) and
// limit (parsed int, NEVER interpolated as a raw caller string — new requirement per D-13/ASVS V5)
if (!_safeFluxString.IsMatch(entityId))
    throw new ArgumentException($"Unsafe entityId for Flux query: {entityId}", nameof(entityId));
if (!_safeFluxString.IsMatch(_settings.InfluxBucket))
    throw new ArgumentException($"Unsafe InfluxBucket for Flux query: {_settings.InfluxBucket}");
if (!string.IsNullOrEmpty(_settings.InfluxMeasurement) && !_safeFluxString.IsMatch(_settings.InfluxMeasurement))
    throw new ArgumentException($"Unsafe InfluxMeasurement for Flux query: {_settings.InfluxMeasurement}");
if (!string.IsNullOrEmpty(_settings.InfluxValueField) && !_safeFluxString.IsMatch(_settings.InfluxValueField))
    throw new ArgumentException($"Unsafe InfluxValueField for Flux query: {_settings.InfluxValueField}");
```

**Flux query builder to extend with explicit sort+limit+sort (RESEARCH.md Pattern 5 — do not rely on bare `tail()`):**

```csharp
// Source: InfluxDbReader.cs:79-86 (QueryAsync's existing flux, -24h hardcoded — DO NOT touch this method)
var flux = $"""
    from(bucket: "{_settings.InfluxBucket}")
      |> range(start: -24h)
      |> filter(fn: (r) => r["_measurement"] == "{_settings.InfluxMeasurement}"
            and r["entity_id"] == "{entityId}"
            and r["_field"] == "{_settings.InfluxValueField}")
      |> sort(columns: ["_time"])
    """;
```

New `QueryHistoryAsync(entityId, lookback, limit)` sibling method, same guard block plus `range(start: -{lookback})` and explicit `sort(desc:true) |> limit(n:{limit}) |> sort(desc:false)` (per RESEARCH.md Pattern 5) — validate `limit` is a parsed positive `int`, never string-interpolate the raw caller value.

**Degrade-safe empty-result shape to mirror** (`InfluxDbReader.cs:52-66`, `98-103`):

```csharp
if (string.IsNullOrEmpty(_settings.InfluxUrl))
{
    _logger.LogWarning(LogEvents.BatchEntityNoData,
        "InfluxUrl not configured — skipping query for {EntityId}", entityId);
    return Array.Empty<(DateTime, double)>();
}
```

---

### `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs` — Warmup call site + `EntityStatusCache.Set` relocation (controller/pipeline, streaming)

**Self-analog (same file):** write loop currently at lines 152-181, read loop at lines 138-149. `_statusCache?.Set(...)` currently sits in the write loop (line 164) — D-10 requires moving it to the read loop (`ProcessVerdictAsync`, lines 190-224), since warm-up data now arrives on the `Verdict`, not the reading:

```csharp
// Source: ScoreStreamPipeline.cs:161-164 (write loop) — Set() call to MOVE, not delete
_statusCache?.Set(new EntityStatusEntry(entityId, entityState.WarmedUp, entityState.ReadingCount, entityState.WarmUpWindow));
```

Target location for the moved call: inside `ProcessVerdictAsync` (`ScoreStreamPipeline.cs:190-224`), reading `verdict.WarmedUp`/`verdict.NSeen`/`verdict.Window` instead of `entityState.WarmedUp`/`ReadingCount`/`WarmUpWindow` (which stop being self-counted per D-01/WARM-01).

**Warmup call site** (new, before opening each `ScoreStream` — analogous to `RunEntityStreamAsync`'s existing gateway-based call construction, lines 270-292):

```csharp
// Source: ScoreStreamPipeline.cs:276-279 (RunEntityStreamAsync) — same "call gateway client before/around
// the per-entity stream" shape the new pre-stream Warmup() call should follow
var call = new LiveScoreStreamCall(_gateway!.DetectorClient.ScoreStream(cancellationToken: ct));
await RunAsync(call, entityId, readings, entityState, ct);
```

**Degrade-safe pattern to apply around the Warmup call (D-15):** mirror `InfluxDbReader`'s own empty-on-failure convention — catch `RpcException`/InfluxDB failure, log WARN, proceed to normal live warm-up (do not throw, do not block `RunEntityStreamAsync`).

---

### `orchestrator/Argus.Orchestrator/Detection/EntityRuntimeState.cs` — verdict-driven warm-up (model/state, transform)

**Self-analog (same file):** current self-counted fields to stop mutating from the write loop (`EntityRuntimeState.cs:22-63`):

```csharp
// Source: EntityRuntimeState.cs:22-28, 40-41, 60-63 — D-01/WARM-01 removes self-counting;
// WarmedUp/ReadingCount/WarmUpWindow become properties SET from the verdict, not computed here
public bool WarmedUp => _readingCount >= _warmUpWindow;
public int ReadingCount => _readingCount;
public int WarmUpWindow => _warmUpWindow;
...
private int _readingCount;
private readonly int _warmUpWindow;
...
public void RecordReading() => _readingCount++;
```

Per D-01, replace the self-incrementing `RecordReading()`/computed `WarmedUp` with settable properties updated from `Verdict.WarmedUp`/`Verdict.NSeen`/`Verdict.Window` in the read loop — `ScoreStreamPipeline.RunAsync`'s call to `entityState.RecordReading()` (`ScoreStreamPipeline.cs:158`) is removed per D2's fix.

---

### `orchestrator/Argus.Orchestrator/Detection/FrozenSensorDetector.cs` — backfill priming (model, transform)

**Analog:** the class's own existing `AddReading` (`FrozenSensorDetector.cs:27-32`), reused as-is — no class changes, only a new call site during backfill (D-14):

```csharp
// Source: FrozenSensorDetector.cs:27-32 — call this in a loop over history rows during Warmup priming
public void AddReading(double value)
{
    if (_readings.Count >= _window)
        _readings.Dequeue();
    _readings.Enqueue(value);
}
```

Call site shape: `foreach (var row in historyRows) entityState.FrozenDetector.AddReading(row.Value);` — same loop shape already used in the live write loop (`ScoreStreamPipeline.cs:157`).

---

### Test analogs

**Python side — `test_registry.py` (MDL-04 lock tests)** is the closest analog for new `test_restart_resilience.py`/`test_model_store.py` checkpoint tests: assert dirty-tracking correctness (score N times, checkpoint, assert file exists; score again without reaching threshold, assert no re-write) and lock discipline (mock a slow `deepcopy` and assert `score_one` isn't blocked). Reuse `model_store.py`'s own `load_all_into` per-entity try/except test pattern (inject a corrupt `checkpoint.pkl`, assert other entities still load and startup reaches `SERVING`).

**Orchestrator side — Influx seam fakes.** `BatchSchedulerWorkerTests.FakeInfluxDbReader` is the exact shape to copy for a `QueryHistoryAsync` fake:

```csharp
// Source: BatchSchedulerWorkerTests.cs:23-33
private sealed class FakeInfluxDbReader : IInfluxDataSource
{
    private readonly IReadOnlyList<(DateTime Timestamp, double Value)> _rows;

    public FakeInfluxDbReader(IReadOnlyList<(DateTime Timestamp, double Value)> rows)
        => _rows = rows;

    public Task<IReadOnlyList<(DateTime Timestamp, double Value)>> QueryAsync(
        string entityId, CancellationToken ct)
        => Task.FromResult(_rows);
}
```

A new `IInfluxDataSource.QueryHistoryAsync` (or a new seam interface, per D-13's "seam") test fake should follow this identical constructor-injected-rows shape. `FakeBatchDetectorClient` (`BatchSchedulerWorkerTests.cs:35-38+`) is the analog for a `Warmup` RPC client fake — track call count + allow injecting a canned `WarmupResponse`.

## Shared Patterns

### Atomic file write (D-07)
**Source:** `model_store.py:326-335` (`_update_latest`)
**Apply to:** `save_checkpoint`'s `checkpoint.pkl` and `checkpoint.json` writes (two independent atomic renames, no cross-file atomicity needed per D-03's `river_version` validation catching a mismatched pair).

### Per-entity lock discipline / MDL-04 (D-06)
**Source:** `registry.py:114-183` (`_entity_lock`, `fit_one`)
**Apply to:** `checkpoint_dirty`'s deepcopy-under-lock / pickle-outside-lock sweep; `warmup_one`'s `n_seen==0` gate check.

### Flux injection guard (`_safeFluxString`)
**Source:** `InfluxDbReader.cs:18-19`, extended by `GroupInfluxReader.cs:23-24`
**Apply to:** `QueryHistoryAsync`'s `entityId`/`InfluxBucket`/`InfluxMeasurement`/`InfluxValueField`/`lookback` validation; `limit` must be a parsed `int`, never interpolated as a raw string (new, non-negotiable per D-13/ASVS V5).

### Per-entity/per-call try/except-and-continue (fault isolation)
**Source:** `model_store.py:290-295` (`load_all_into`); `BatchSchedulerWorker.cs:27-29` docstring ("Per-entity exceptions are caught and logged; worker never dies from a single entity failure")
**Apply to:** checkpoint restore glob pass (D-09); Warmup/backfill degrade-safe handling (D-15) — never let one entity's failure block startup or other entities.

### `map<string, string> params` proto convention
**Source:** `proto/argus.proto` `FitRequest.params`/`ScoreBatchRequest.params`/`GroupScoreRequest.params` (lines 28, 40, 87)
**Apply to:** `Point.params` (WARM-02), `WarmupRequest.params`.

## No Analog Found

| File | Role | Data Flow | Reason |
|---|---|---|---|
| Periodic checkpoint-writer thread (new code inside `registry.py`/`server.py`) | utility/daemon-thread | batch/event-driven | No existing Python code in this repo runs a periodic background thread — the detector is purely request-driven plus one-shot startup load. Closest design reference is the .NET `BatchSchedulerWorker` (`BackgroundService`, different language/framework) — use for shape only. Build directly from RESEARCH.md's Pattern 1/Pattern 3 code examples (`threading.Thread` + `threading.Event.wait(timeout)`), not from an in-repo adaptation. |

## Metadata

**Analog search scope:** `detector/argus_detector/**`, `detector/tests/**`, `proto/argus.proto`, `orchestrator/Argus.Orchestrator/**`, `orchestrator/Argus.Orchestrator.Tests/**`
**Files scanned:** `model_store.py`, `registry.py`, `hst_detector.py`, `server.py`, `servicer.py`, `config.py`, `argus.proto`, `InfluxDbReader.cs`, `GroupInfluxReader.cs`, `ScoreStreamPipeline.cs`, `EntityRuntimeState.cs`, `EntityStatusCache.cs`, `FrozenSensorDetector.cs`, `BatchSchedulerWorker.cs`, `BatchSchedulerWorkerTests.cs`
**Pattern extraction date:** 2026-08-03
