---
phase: 01
reviewed: 2026-06-10T00:00:00Z
depth: standard
files_reviewed: 40
files_reviewed_list:
  - proto/argus.proto
  - detector/argus_detector/config.py
  - detector/argus_detector/hst_detector.py
  - detector/argus_detector/logging_setup.py
  - detector/argus_detector/normalizer.py
  - detector/argus_detector/registry.py
  - detector/argus_detector/server.py
  - detector/argus_detector/servicer.py
  - detector/scripts/gen_proto.py
  - detector/tests/test_health.py
  - detector/tests/test_hst_detector.py
  - detector/tests/test_registry.py
  - detector/tests/test_score_zero_wire.py
  - detector/tests/test_server_boot.py
  - entities.yaml
  - orchestrator/Argus.Orchestrator/Config/ConnectionSettings.cs
  - orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs
  - orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs
  - orchestrator/Argus.Orchestrator/Detection/DetectionGateway.cs
  - orchestrator/Argus.Orchestrator/Detection/DetectorChannelFactory.cs
  - orchestrator/Argus.Orchestrator/Detection/EntityRuntimeState.cs
  - orchestrator/Argus.Orchestrator/Detection/FrozenSensorDetector.cs
  - orchestrator/Argus.Orchestrator/Detection/HysteresisGate.cs
  - orchestrator/Argus.Orchestrator/Detection/IScoreStreamCall.cs
  - orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs
  - orchestrator/Argus.Orchestrator/Ha/HaReading.cs
  - orchestrator/Argus.Orchestrator/Ha/IHaEventSource.cs
  - orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs
  - orchestrator/Argus.Orchestrator/Ha/ReconnectCooldown.cs
  - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
  - orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs
  - orchestrator/Argus.Orchestrator/Mqtt/FriendlyName.cs
  - orchestrator/Argus.Orchestrator/Mqtt/IStatePublisher.cs
  - orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs
  - orchestrator/Argus.Orchestrator/Mqtt/StatePublisher.cs
  - orchestrator/Argus.Orchestrator/Mqtt/UniqueId.cs
  - orchestrator/Argus.Orchestrator/Program.cs
  - orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs
  - orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs
status: fixed
critical: 0
warning: 0
info: 3
---

# Phase 1: Code Review Report

**Reviewed:** 2026-06-10
**Depth:** standard
**Files Reviewed:** 40
**Status:** fixed (5 critical fixed, 6 warning fixed, 3 info not in scope)

## Summary

End-to-end streaming path is structurally sound. Proto contract, gRPC servicer, HST detector,
hysteresis gate, frozen sensor detector, and MQTT discovery are all present and reasonably
implemented. The most dangerous issues are: (1) a race condition in `NetDaemonHaEventSource`
where the background task's exception silently disappears and the channel can complete while
the writer is still running; (2) the `ScoreStreamPipeline` passes a **synthetic reading** with
`SuppressBinarySensor=false` to `ProcessVerdictAsync`, which breaks the post-reconnect suppression
contract; (3) the `MqttConnection` reconnect loop runs with `CancellationToken.None`, so it
cannot be stopped on host shutdown; (4) `DetectorChannelFactory` creates an `X509Certificate2`
from PEM but does not call `CopyWithPrivateKey` on .NET 8 Linux where the ephemeral key can be
GC-collected before TLS handshake; (5) `DiscoveryPublisher` uses a bridge-level `availability_topic`
but `StatePublisher.PublishAvailabilityAsync` publishes to a per-entity topic — HA never sees
those per-entity messages because discovery config only declares the bridge topic, making
graceful-degradation per-entity offline unreachable in practice.

---

## Critical Issues

### CR-01: Background task exception swallowed — channel silently completes on first error

**File:** `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs:71`

**Issue:** `RunConnectionLoopAsync` is started with `_ = Task.Run(...)`. Any unhandled exception
from the inner `while` loop that escapes the outer `try/catch` (e.g., `ObjectDisposedException`
from `writer.Complete()` race, or an exception before the inner try) is silently discarded.
Additionally, when the background task finishes for any reason `writer.Complete()` is called in
`finally`, completing the channel. The `ReadAllAsync` consumer then exits normally with zero
readings — no error is surfaced to the worker, and the pipeline terminates silently instead of
triggering a host restart.

**Fix:** Propagate the background task exception to the channel so the consumer gets an error:

```csharp
var loopTask = Task.Run(() => RunConnectionLoopAsync(channel.Writer, ct), ct);

await foreach (var reading in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
{
    yield return reading;
}

// Propagate any exception from the background task
await loopTask; // throws if RunConnectionLoopAsync faulted
```

---

### CR-02: SuppressBinarySensor always false in verdict processing — post-reconnect suppression broken

**File:** `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs:109`

**Issue:** Inside the `readTask` lambda in `RunAsync(IScoreStreamCall, ...)`, verdicts are
processed with a synthetic `HaReading` created as:
```csharp
var syntheticReading = new Ha.HaReading(entityId, 0.0, DateTimeOffset.UtcNow, false);
```
`SuppressBinarySensor` is hardcoded to `false`. The post-reconnect cooldown suppression (D-07)
and PITFALL 8 warm-up suppression are evaluated in `ProcessVerdictAsync` from
`reading.SuppressBinarySensor`. The original `HaReading` with the correct `SuppressBinarySensor`
value is consumed in the write loop but its flag is never forwarded to the verdict processing
path. Result: the binary_sensor flag is published during the 60s post-reconnect window,
producing the false-anomaly cascade that D-07 was designed to prevent.

**Fix:** Track `SuppressBinarySensor` in `EntityRuntimeState` (it is per-entity, not per-reading
in the verdict path):

```csharp
// In EntityRuntimeState: add
public bool SuppressBinarySensor { get; set; }

// In write loop, before call.WriteAsync:
entityState.SuppressBinarySensor = reading.SuppressBinarySensor;

// In readTask, replace synthetic reading with:
await ProcessVerdictAsync(
    new Ha.HaReading(entityId, 0.0, DateTimeOffset.UtcNow, entityState.SuppressBinarySensor),
    verdict, entityState, ct);
```

---

### CR-03: MQTT reconnect loop ignores cancellation — service cannot shut down cleanly

**File:** `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs:102`

**Issue:** `OnDisconnectedAsync` is a fire-and-forget event handler that runs an infinite
`while (true)` loop. Both the `Task.Delay` and `ConnectAsync` calls inside the loop use
`CancellationToken.None`. When the host triggers shutdown, `DisposeAsync` removes the handler
and calls `DisconnectAsync`, but the loop is already spinning and cannot be interrupted. On
Docker/systemd shutdown the process will not exit until the OS SIGKILL after the grace period.

```csharp
// Current — cannot be cancelled:
await Task.Delay(totalDelay);  // line ~100
await _client.ConnectAsync(_connectOptions, CancellationToken.None);  // line 102
```

**Fix:** Thread a `CancellationTokenSource` through the connection lifecycle:

```csharp
private readonly CancellationTokenSource _cts = new();

// In OnDisconnectedAsync:
await Task.Delay(totalDelay, _cts.Token);
await _client.ConnectAsync(_connectOptions, _cts.Token);

// In DisposeAsync:
_cts.Cancel();
_client.DisconnectedAsync -= OnDisconnectedAsync;
...
```

---

### CR-04: X509Certificate2 private key may be freed before TLS handshake on .NET 8 Linux

**File:** `orchestrator/Argus.Orchestrator/Detection/DetectorChannelFactory.cs:42`

**Issue:** On .NET 8 on Linux (the deployment target — `dotnet:8.0-jammy-chiseled`),
`X509Certificate2.CreateFromPemFile` creates a certificate where the private key is held as an
ephemeral `RSA`/`ECDsa` object. The `HttpClientHandler` internally copies the cert reference,
but the original `clientCert` local can be GC-collected between the time the handler is created
and the TLS handshake occurs (particularly under GC pressure). The documented fix on .NET 8 is
to call `.CopyWithPrivateKey(...)` or use `X509CertificateLoader` (.NET 9+). This manifests as
intermittent `AuthenticationException: The credentials supplied to the package were not recognized`
during mTLS handshake on Linux.

```csharp
// Current:
var clientCert = X509Certificate2.CreateFromPemFile(settings.TlsCert, settings.TlsKey);
handler.ClientCertificates.Add(clientCert);
```

**Fix:**
```csharp
// .NET 8 safe pattern — persist private key into the cert object:
using var tempCert = X509Certificate2.CreateFromPemFile(settings.TlsCert, settings.TlsKey);
var clientCert = new X509Certificate2(tempCert.Export(X509ContentType.Pkcs12));
handler.ClientCertificates.Add(clientCert);
```

---

### CR-05: Per-entity availability topic mismatch — graceful degradation is unreachable

**File:** `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs:43` and
`orchestrator/Argus.Orchestrator/Mqtt/StatePublisher.cs:63`

**Issue:** `DiscoveryPublisher.BuildBinarySensorConfig` and `BuildSensorConfig` both set:
```csharp
availability_topic = BridgeAvailabilityTopic  // "argus/bridge/availability"
```
There is no per-entity `availability_topic` in the discovery payload. However,
`StatePublisher.PublishAvailabilityAsync` publishes to `argus/{slug}/availability` (the per-entity
topic). When `HandleDetectorFailureAsync` calls `PublishAvailabilityAsync`, HA never receives
the per-entity offline message because HA is not subscribed to that topic — the discovery config
declared the bridge topic only. The entity stays `online` (or reverts to the LWT `offline`) but
per-entity graceful degradation is inert. RES-01 is not met for single-entity failures.

**Fix:** Either add per-entity `availability_topic` to the discovery payload (preferred for
per-entity granularity), or document that only bridge-level availability is supported and remove
`PublishAvailabilityAsync` calls from `HandleDetectorFailureAsync`:

```csharp
// In BuildBinarySensorConfig / BuildSensorConfig, add:
availability = new[]
{
    new { topic = BridgeAvailabilityTopic, payload_available = "online", payload_not_available = "offline" },
    new { topic = $"argus/{slug}/availability", payload_available = "online", payload_not_available = "offline" },
},
```
*(HA supports `availability` list since 2022.9.)*

---

## Warnings

### WR-01: Double-checked locking unsound under Python's memory model (GIL caveat)

**File:** `detector/argus_detector/registry.py:41-43`

**Issue:** The fast path `det = self._detectors.get(key)` at line 42 reads the dict without
holding `_lock`. Python's GIL makes this safe for CPython today, but the comment "no lock needed
for read after creation" is only correct because CPython's dict.get is atomic under the GIL.
Grpcio's `futures.ThreadPoolExecutor` does use real OS threads. More critically: `dict.get` is
NOT guaranteed atomic if a resize is happening concurrently — a concurrent `__setitem__` under
the lock can trigger a resize, and a simultaneous unlocked `get` can read a partially-rehashed
state in edge cases. The current pattern is fragile; a standard `threading.Lock` around both
read and write is simpler and unambiguously correct:

```python
def _get_or_create(self, entity_id, detector, params):
    key = (entity_id, detector)
    with self._lock:
        if key not in self._detectors:
            self._detectors[key] = EntityDetector.from_params(params or {})
        return self._detectors[key]
```

---

### WR-02: `logging_setup.py` — `skip` set built from class `__dict__` keys, not instance attribute names

**File:** `detector/argus_detector/logging_setup.py:31`

**Issue:**
```python
skip = logging.LogRecord.__dict__.keys() | { ... }
```
`logging.LogRecord.__dict__` returns the **class** dict (methods, descriptors, class variables).
Instance attributes like `name`, `levelname`, `msg`, `args`, etc. are set in `__init__` and live
on instances. The class dict does not contain all of them. The manual set literal partially
compensates, but it does not include every standard instance attribute (e.g., `stack_info` is
conditionally set; some versions set additional attributes). The robust pattern is to compare
against a reference instance:

```python
_STANDARD_ATTRS = frozenset(logging.LogRecord(
    "", 0, "", 0, "", (), None
).__dict__.keys()) | {"message", "asctime"}

# In format():
for key, value in record.__dict__.items():
    if key not in _STANDARD_ATTRS:
        payload[key] = value
```

---

### WR-03: `ScoreStreamPipeline.RunAsync` fans out all entities over a shared `IAsyncEnumerable` — each entity stream sees all readings

**File:** `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs:82-85`

**Issue:** The multi-entity `RunAsync` overload starts one `Task` per entity, each calling
`RunEntityStreamAsync(kvp.Key, ..., readings, ct)` where `readings` is the **same**
`IAsyncEnumerable<HaReading>`. Inside the per-entity loop the code filters by
`if (reading.EntityId != entityId) continue`. However a single `IAsyncEnumerable` cannot be
safely iterated from multiple concurrent tasks — `await foreach` on the same source from N
tasks races at the enumerator level. The design intent (one stream per entity) requires either
a fan-out multiplexer or one independent `IAsyncEnumerable` per entity.

The current code will silently drop events: when two entity tasks both await the shared
enumerator concurrently, `MoveNextAsync` is not thread-safe and only one consumer sees each
element.

**Fix:** Add a fan-out layer before the per-entity tasks:

```csharp
// Create N bounded channels, one per entity
var entityChannels = entityStates.Keys.ToDictionary(
    id => id,
    _ => Channel.CreateBounded<HaReading>(500));

// Fan-out task: read once, write to matching channel
var fanOutTask = Task.Run(async () =>
{
    await foreach (var r in readings.WithCancellation(ct))
        if (entityChannels.TryGetValue(r.EntityId, out var ch))
            await ch.Writer.WriteAsync(r, ct);
    foreach (var ch in entityChannels.Values)
        ch.Writer.Complete();
}, ct);

var tasks = entityStates.Select(kvp =>
    RunEntityStreamAsync(kvp.Key, kvp.Value, entityChannels[kvp.Key].Reader.ReadAllAsync(ct), ct));

await Task.WhenAll(tasks.Append(fanOutTask));
```

---

### WR-04: `servicer.py` does not handle `point.value` being unset — crashes on `None.value`

**File:** `detector/argus_detector/servicer.py:47`

**Issue:**
```python
value: float = point.value.value  # unwrap DoubleValue
```
`point.value` is a `google.protobuf.DoubleValue` (a wrapper type). If the client sends a `Point`
without the `value` field set (e.g., a partially constructed message), `point.value` is the
default `DoubleValue` instance with `value=0.0` — not `None` — so this specific line is safe.
However, `point.entity_id` being empty string is not validated. An empty `entity_id` will
create a registry entry under `("", "hst")`, producing an HST model that aggregates all
unidentified points.

More critically: the `for point in request_iterator` loop has no exception handler. Any
exception raised inside the loop (e.g., a corrupt proto, a `grpc.RpcError` from the iterator)
will propagate out of the generator, causing the stream to terminate without sending the client a
proper status code — the client gets `StatusCode.Unknown` instead of a meaningful error.

**Fix:**
```python
for point in request_iterator:
    if not context.is_active():
        return
    if not point.entity_id:
        logger.warning("received Point with empty entity_id — skipping")
        continue
    try:
        # ... scoring logic ...
        yield verdict
    except Exception:
        logger.exception("unexpected error scoring point for %s", point.entity_id)
        context.abort(grpc.StatusCode.INTERNAL, "scoring error")
        return
```

---

### WR-05: `MqttPublisherWorker` uses `LogEvents.MqttWorkerStarted` for the "ready" log — duplicate event ID usage

**File:** `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs:57`

**Issue:**
```csharp
_logger.LogInformation(LogEvents.MqttWorkerStarted, "MqttPublisherWorker ready — discovery + availability published");
```
`LogEvents.MqttWorkerStarted` (EventId 4006) is already used at line 38 for "starting". Using
the same event ID for two semantically different states ("starting" vs "ready") breaks structured
log filtering and alerting — monitoring rules that trigger on 4006 cannot distinguish startup
from readiness.

**Fix:**
```csharp
// Add to LogEvents.cs:
public static readonly EventId MqttWorkerReady = new(4007, nameof(MqttWorkerReady));

// In MqttPublisherWorker.cs line 57:
_logger.LogInformation(LogEvents.MqttWorkerReady, "MqttPublisherWorker ready ...");
```

---

### WR-06: `ConnectionSettings` configured twice from the same env vars — second instance never receives IOptions updates

**File:** `orchestrator/Argus.Orchestrator/Program.cs:12-48`

**Issue:** `ConnectionSettings` is registered twice:
1. Via `builder.Services.Configure<ConnectionSettings>(...)` (line 12) — bound into
   `IOptions<ConnectionSettings>`, available to DI consumers that inject `IOptions<ConnectionSettings>`.
2. As a plain `AddSingleton<ConnectionSettings>(connectionSettings)` (line 48) — a manually
   constructed instance.

`NetDaemonHaEventSource`, `MqttConnection`, `DetectorChannelFactory`, and others all depend on
the singleton `ConnectionSettings` (constructor-injected, not `IOptions`). They will receive the
manually constructed instance at line 35-47, which does **not** include `MqttPort` because
`int.TryParse` on line 17 is applied only to the `IOptions` configure action, not to the manual
construction. The manual instance always uses the default `MqttPort = 1883` even when
`ARGUS_MQTT_PORT` is set.

**Fix:** Remove the duplicate. Use only the `IOptions` pattern and inject
`IOptions<ConnectionSettings>` into consumers, or build one `ConnectionSettings` instance and
register it as both `AddSingleton` and use it in the configure action.

```csharp
// Build one authoritative instance:
var connectionSettings = new ConnectionSettings
{
    ...
    MqttPort = int.TryParse(builder.Configuration["ARGUS_MQTT_PORT"], out var p) ? p : 1883,
    ...
};
builder.Services.AddSingleton(connectionSettings);
// Remove the separate Configure<ConnectionSettings> call (it is never read by DI consumers)
```

---

## Info

### IN-01: `normalizer.py` is dead code — `EntityDetector` duplicates the normalizer inline

**File:** `detector/argus_detector/normalizer.py`

**Issue:** `OnlineMinMaxScaler` wraps `preprocessing.MinMaxScaler` and is never imported or used.
`EntityDetector` in `hst_detector.py` directly instantiates `preprocessing.MinMaxScaler` in its
own `__init__`. The module comment says "exposed as a module so Plan 2+ can swap" but there is no
import of it from `hst_detector.py` or `servicer.py`. This creates two divergent normalizer paths
if someone updates one but not the other.

Either wire `EntityDetector` to use `OnlineMinMaxScaler`, or remove `normalizer.py` until it is
actually needed.

---

### IN-02: `gen_proto.py` regex only fixes the first occurrence of the relative import

**File:** `detector/scripts/gen_proto.py:66-72`

**Issue:**
```python
fixed = re.sub(
    r"^import argus_pb2 as argus__pb2",
    "from argus_detector.proto import argus_pb2 as argus__pb2",
    content,
    flags=re.MULTILINE,
)
```
`re.sub` with default `count=0` replaces all occurrences. This is fine. However, the regex
anchors to `^` (start of line) but does not account for the possibility that grpcio-tools
generates `import argus_pb2 as argus__pb2` in a try/except fallback block on a different line
pattern in future versions. The fix is silently skipped if `fixed == content` with only a print
confirming change. If grpcio-tools ever changes the import pattern, the fix will silently not
apply and the import will break at runtime.

Add an assertion or warning when the pattern is not found:

```python
if fixed == content:
    print("WARNING: expected relative import pattern not found in argus_pb2_grpc.py — manual inspection required", file=sys.stderr)
```

---

### IN-03: `servicer.py` comment says "placeholder score=0.0" but real HST scoring is wired

**File:** `detector/argus_detector/servicer.py:35-37`

**Issue:** The docstring and `TODO(plan06)` comment say the servicer uses a placeholder with
`score=0.0`:
```python
"""
Placeholder: score=0.0, is_anomaly=False, detector="hst".
TODO(plan06): real River HST scoring wired through registry.
"""
```
But the implementation at line 48 already calls `self._registry.score_one(entity_id, value)`,
which returns the real River HST score. The `is_anomaly` field is still hardcoded to `False`
(line 56), so the comment is partially stale. Stale comments that contradict the implementation
are a maintenance hazard.

Update docstring to reflect current state:
- Real HST scoring is live (remove placeholder claim and TODO(plan06))
- `is_anomaly` is currently always `False` — note this as the remaining gap

---

_Reviewed: 2026-06-10_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
