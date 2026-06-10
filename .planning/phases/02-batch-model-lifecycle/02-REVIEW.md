---
phase: 02-batch-model-lifecycle
reviewed: 2026-06-10T00:00:00Z
depth: standard
files_reviewed: 27
files_reviewed_list:
  - proto/argus.proto
  - orchestrator/Argus.Orchestrator/Config/ConnectionSettings.cs
  - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
  - orchestrator/Argus.Orchestrator/Batch/InfluxDbReader.cs
  - orchestrator/Argus.Orchestrator/Batch/IInfluxQueryApi.cs
  - orchestrator/Argus.Orchestrator/Batch/InfluxQueryApiAdapter.cs
  - orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs
  - orchestrator/Argus.Orchestrator/Batch/IBatchDetectorClient.cs
  - orchestrator/Argus.Orchestrator/Batch/IInfluxDataSource.cs
  - orchestrator/Argus.Orchestrator/Batch/BatchDetectorClientAdapter.cs
  - orchestrator/Argus.Orchestrator/Program.cs
  - orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs
  - orchestrator/Argus.Orchestrator.Tests/InfluxDbReaderTests.cs
  - orchestrator/Argus.Orchestrator.Tests/BatchSchedulerWorkerTests.cs
  - orchestrator/Argus.Orchestrator.Tests/Res02ResilienceTests.cs
  - detector/argus_detector/pyod_detector.py
  - detector/argus_detector/stl_detector.py
  - detector/argus_detector/model_store.py
  - detector/argus_detector/registry.py
  - detector/argus_detector/servicer.py
  - detector/argus_detector/server.py
  - detector/tests/test_pyod_detector.py
  - detector/tests/test_stl_detector.py
  - detector/tests/test_model_store.py
  - detector/tests/test_registry.py
  - detector/tests/test_servicer.py
  - detector/tests/test_restart_resilience.py
findings:
  critical: 3
  warning: 6
  info: 2
  total: 11
status: fixed
---

# Phase 02: Code Review Report

**Reviewed:** 2026-06-10T00:00:00Z
**Depth:** standard
**Files Reviewed:** 27
**Status:** issues_found

## Summary

Reviewed the full batch model lifecycle implementation: InfluxDB reader, batch scheduler worker, Python detector (PyOD MAD + STL), model store, registry, and gRPC servicer. The streaming and scheduling logic is structurally sound. Three blockers were found: a Flux query injection vulnerability in the InfluxDB reader, a registry key inconsistency that causes every model loaded at startup to be invisible to the `ScoreBatch` cold-start check, and unsafe `pickle.load` deserialization of River models. Six warnings cover: `fit_one("stl", ...)` crashes at runtime with `AttributeError`, unguarded direct registry dict access in the servicer, missing input validation on scheduler config values, and a `SaveModel` that silently discards all serialized bytes.

## Critical Issues

### CR-01: Flux Query Injection via `entityId` and Config Fields

**File:** `orchestrator/Argus.Orchestrator/Batch/InfluxDbReader.cs:61-68`

**Issue:** `entityId`, `_settings.InfluxMeasurement`, `_settings.InfluxValueField`, and `_settings.InfluxBucket` are interpolated directly into the Flux query string using a C# raw string literal. Flux strings are double-quoted (`"..."`) inside the query; any value containing `"` followed by Flux syntax terminates the string literal and injects arbitrary operators. `entityId` comes from `entities.yaml` (user-controlled), and all three config fields come from environment variables. An attacker who controls `entities.yaml` or any of those environment variables can exfiltrate or overwrite arbitrary InfluxDB data.

**Fix:**
The InfluxDB.Client `QueryApi` supports parameterized queries. Use `QueryAsync(Query, string?)` with a `Query` object that carries `extern` parameters, or sanitize by rejecting any `entityId` / field name that contains characters outside `[a-zA-Z0-9._-]`:

```csharp
// Allowlist: reject entity_id values that cannot be safe Flux string literals
private static readonly System.Text.RegularExpressions.Regex SafeId =
    new(@"^[a-zA-Z0-9_.\-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

public async Task<IReadOnlyList<(DateTime, double)>> QueryAsync(string entityId, CancellationToken ct)
{
    if (!SafeId.IsMatch(entityId))
        throw new ArgumentException($"Unsafe entity_id: {entityId}");
    // ... same for InfluxBucket, InfluxMeasurement, InfluxValueField at startup validation
```

At minimum, validate all four values at startup and fail fast if they contain `"` or `\`.

---

### CR-02: Registry Key Inconsistency — Startup-Loaded Models Invisible to ScoreBatch

**File:** `detector/argus_detector/model_store.py:200`, `detector/argus_detector/servicer.py:143`

**Issue:** `ModelStore.load_all_into` registers models using the **slug** form (dots replaced by underscores):

```python
# model_store.py:182-200
slug = latest_file.parent.parent.name   # e.g. "sensor_salon_temp"
registry.register(slug, detector, model)
```

But `ScoreBatch` checks `has_model` using the raw **entity_id** from the gRPC request (dots intact):

```python
# servicer.py:143
if not self._registry.has_model(entity_id, detector):   # entity_id = "sensor.salon_temp"
```

After a restart, `has_model("sensor.salon_temp", "mad")` returns `False` even though `("sensor_salon_temp", "mad")` is in the registry. Every entity cold-starts on the first ScoreBatch after restart, discarding the loaded model and re-fitting on the 24h window — eliminating the benefit of model persistence entirely. The test `test_load_model_after_fit_and_save` acknowledges this with the `or fresh_registry.has_model("sensor_load", "mad")` disjunction, proving the inconsistency exists and is known but unfixed.

**Fix:** Normalize to the same key at every boundary. The simplest fix is to convert `entity_id` to slug inside `ScoreBatch` before all registry operations:

```python
# servicer.py — apply slug consistently in ScoreBatch, Fit, SaveModel, LoadModel
entity_slug = entity_id.replace(".", "_")
if not self._registry.has_model(entity_slug, detector):
    self._registry.fit_one(entity_slug, detector, values)
scores, error = self._registry.score_batch(entity_slug, detector, values)
```

And ensure `fit_one` in `Fit` also uses the slug key. Alternatively, let `register` in the registry accept raw `entity_id` and do the normalization there — but whichever convention is chosen, it must be applied uniformly.

---

### CR-03: Unsafe `pickle.load` of River Model Files

**File:** `detector/argus_detector/model_store.py:152,192`, `detector/argus_detector/servicer.py:235`

**Issue:** River models are deserialized with `pickle.load(f)` from files under `/var/argus/models`. Pickle deserialization executes arbitrary Python code embedded in the pickled object. If those files are writable by any process other than the detector (shared volume, container escape, symlink attack), an attacker can achieve remote code execution inside the detector container. There is no integrity check (HMAC, signature, or hash verification) before loading.

**Fix:** Use a file-system permission model that restricts `/var/argus/models` to write-only by the detector process (enforced in the Dockerfile), and add a SHA-256 sidecar file written at save time and verified before load:

```python
import hashlib

def _write_hash(path: pathlib.Path) -> None:
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    path.with_suffix(path.suffix + ".sha256").write_text(digest)

def _verify_hash(path: pathlib.Path) -> None:
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    expected = path.with_suffix(path.suffix + ".sha256").read_text().strip()
    if digest != expected:
        raise ValueError(f"Hash mismatch for {path}: file may be tampered")
```

For joblib-serialized PyOD models the same concern applies; joblib also uses pickle internally.

---

## Warnings

### WR-01: `fit_one("stl", ...)` Crashes with `AttributeError` at Runtime

**File:** `detector/argus_detector/registry.py:117-141`

**Issue:** `fit_one` calls `candidate.fit(values)` on the result of `_create_detector(detector)`. When `detector == "stl"`, `_create_detector` returns a `StlDetector` instance. `StlDetector` has no `fit()` method — the test `test_no_fit_method` explicitly confirms this raises `AttributeError`. The exception propagates to `servicer.Fit`, which catches it generically and returns `ok=False` — but only after logging a misleading traceback. Since nightly fit iterates all configured detectors including `"stl"`, any entity configured with detector `"stl"` will produce a nightly fit error on every run.

**Fix:** Reject `"stl"` at the `fit_one` entry point since STL is stateless and requires no fitting:

```python
def fit_one(self, entity_id: str, detector: str, values: list[float]) -> None:
    if detector == "stl":
        raise ValueError("StlDetector is stateless; fit_one is not applicable for 'stl'")
    # ... existing logic
```

The servicer's `Fit` RPC should also guard against `detector == "stl"` and return a clear `ok=False` with a descriptive error rather than letting the AttributeError bubble up.

---

### WR-02: Direct Access to `_detectors` Private Dict Bypasses Entity Lock

**File:** `detector/argus_detector/servicer.py:117,179`

**Issue:** `Fit` and `SaveModel` read model references with `self._registry._detectors.get(...)` directly, bypassing the per-entity lock that `fit_one` and `score_batch` use. This is a TOCTOU race: `fit_one` atomically swaps the dict entry under the entity lock, but the unguarded read here can observe a partially-swapped state in CPython (GIL does not protect across multiple bytecodes). More importantly this breaks the encapsulation contract of `DetectorRegistry` and will silently fail if the locking strategy is ever changed.

**Fix:** Add a public accessor to `DetectorRegistry`:

```python
def get_model(self, entity_id: str, detector: str) -> object | None:
    key = (entity_id, detector)
    lock = self._entity_lock(key)
    with lock:
        return self._detectors.get(key)
```

Then use `self._registry.get_model(entity_id, detector)` in the servicer instead of `self._registry._detectors.get(...)`.

---

### WR-03: `SaveModel` Computes and Discards All Serialized Bytes

**File:** `detector/argus_detector/servicer.py:183-188`

**Issue:** `SaveModel._serialize_model()` serializes the entire model to a `BytesIO` buffer and calls `buf.getvalue()`, but the return value is never used:

```python
# servicer.py:184-186
self._serialize_model(model)          # bytes returned and dropped
return argus_pb2.SaveModelResponse(ok=True)
```

`SaveModelResponse` has no `model_bytes` field in the proto, so the bytes cannot be returned in the response. The model is also not persisted to disk (only `Fit` calls `_save_model_to_store`). This means `SaveModel` is a no-op beyond a serialization smoke test — it serializes, verifies no exception was raised, and discards the result. Callers who invoke `SaveModel` believing the model is saved will find no file on disk.

**Fix:** Either (a) make `SaveModel` persist to disk via `_save_model_to_store`, or (b) document clearly that `SaveModel` only validates serializability and is not a persistence mechanism, and remove the dead `buf.getvalue()` call. Given the proto's `LoadModel` expects bytes on disk, option (a) is the correct fix:

```python
def SaveModel(self, request, context):
    entity_id = request.entity_id
    detector = request.detector
    entity_slug = entity_id.replace(".", "_")
    model = self._registry.get_model(entity_id, detector)
    if model is None:
        return argus_pb2.SaveModelResponse(ok=False, error="no model for entity/detector")
    try:
        version = self._model_store.next_version(entity_slug, detector)
        self._save_model_to_store(entity_slug, detector, version, model)
        return argus_pb2.SaveModelResponse(ok=True)
    except Exception as e:
        logger.exception("SaveModel failed for %s/%s", entity_id, detector)
        return argus_pb2.SaveModelResponse(ok=False, error=str(e))
```

---

### WR-04: `BatchIntervalMinutes` Not Validated — Zero Causes Tight Spin Loop

**File:** `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs:93`, `orchestrator/Argus.Orchestrator/Program.cs:42`

**Issue:** `BatchIntervalMinutes` is read from the environment with no lower-bound validation. `PeriodicTimer(TimeSpan.FromMinutes(0))` creates a timer that fires as fast as the system allows — effectively a busy loop hammering InfluxDB and the gRPC detector on every iteration. Negative values cause `ArgumentOutOfRangeException` from `PeriodicTimer`, crashing the worker immediately.

**Fix:** Validate at startup before constructing `ConnectionSettings`:

```csharp
if (connectionSettings.BatchIntervalMinutes <= 0)
    throw new InvalidOperationException(
        $"ARGUS_BATCH_INTERVAL_MIN must be > 0, got {connectionSettings.BatchIntervalMinutes}");
```

---

### WR-05: `NightlyFitHour` Not Validated — Out-of-Range Value Silently Disables Nightly Fit

**File:** `orchestrator/Argus.Orchestrator/Config/ConnectionSettings.cs:63`, `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs:109`

**Issue:** `NightlyFitHour` is accepted without range validation. Values outside `[0, 23]` will never equal `DateTime.Now.Hour`, silently disabling nightly model retraining indefinitely. There is no log message indicating the fit was skipped due to an invalid configuration — the failure is completely silent.

**Fix:**

```csharp
if (connectionSettings.NightlyFitHour < 0 || connectionSettings.NightlyFitHour > 23)
    throw new InvalidOperationException(
        $"ARGUS_NIGHTLY_FIT_HOUR must be in [0, 23], got {connectionSettings.NightlyFitHour}");
```

---

### WR-06: `_FakeContext.abort()` Does Not Stop Execution — Tests Do Not Catch Post-Abort Code Paths

**File:** `detector/tests/test_servicer.py:39-43`

**Issue:** The fake gRPC context stub sets `self.aborted = True` on `abort()` but does not halt the method under test. In the real gRPC synchronous servicer, `context.abort()` sets an internal flag but also does not halt execution — code after `abort()` continues to run. However, `servicer.Fit` returns `argus_pb2.FitResponse(ok=False, ...)` after calling `context.abort()` (line 102), which is dead code in production (gRPC ignores the return value after abort). The tests exercise the `ctx.aborted` check but do not verify that no response object is returned, and do not catch cases where post-abort code modifies state or raises unexpectedly.

**Fix:** Add assertions in tests that verify no state mutations occur after abort, and assert the returned value from the servicer method is `None` (not a response proto), which is the correct contract:

```python
def test_fit_empty_entity_id_aborts(self, servicer):
    ...
    result = svc.Fit(request, ctx)
    assert ctx.aborted
    assert result is None, "After abort, return value should be None (gRPC ignores it anyway)"
```

---

## Info

### IN-01: `server._argus_registry` Is a Non-Standard Attribute on gRPC Server Object

**File:** `detector/argus_detector/server.py:91`

**Issue:** `server._argus_registry = registry` attaches a custom attribute to the opaque gRPC `server` object for test introspection. This works in CPython today but relies on the gRPC server object allowing arbitrary attribute assignment. A gRPC library update that uses `__slots__` or a C extension type would break this with `AttributeError` at test startup, with no compile-time warning.

**Fix:** Return the registry from `create_server` as a named tuple or dataclass alongside the server:

```python
from dataclasses import dataclass

@dataclass
class ServerBundle:
    server: grpc.Server
    registry: DetectorRegistry

def create_server(...) -> ServerBundle:
    ...
    return ServerBundle(server=server, registry=registry)
```

---

### IN-02: Nightly Fit Redundantly Re-Queries InfluxDB for Each Detector of the Same Entity

**File:** `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs:215-249`

**Issue:** `RunNightlyFitAsync` iterates `entity.Detectors` inside a loop over entities, calling `_influxReader.QueryAsync(entity.EntityId, ct)` once **per detector** per entity. An entity with 2 detectors (`mad` + `stl`) triggers 2 InfluxDB queries for the same entity ID and the same 24h window. The data returned is identical both times.

**Fix:** Hoist the query outside the detector loop:

```csharp
var points = await _influxReader.QueryAsync(entity.EntityId, ct);
if (points.Count == 0) { ... continue; }

foreach (var detectorCfg in entity.Detectors)
{
    // use points — no second query
}
```

This same pattern already appears correctly in `RunEntityBatchAsync`; `RunNightlyFitAsync` should mirror it.

---

_Reviewed: 2026-06-10T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
