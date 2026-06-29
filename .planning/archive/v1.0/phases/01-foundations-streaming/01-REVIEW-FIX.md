---
phase: 01
fixed_at: 2026-06-10T00:00:00Z
review_path: .planning/phases/01-foundations-streaming/01-REVIEW.md
iteration: 1
findings_in_scope: 11
fixed: 11
skipped: 0
status: all_fixed
---

# Phase 1: Code Review Fix Report

**Fixed at:** 2026-06-10
**Source review:** .planning/phases/01-foundations-streaming/01-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 11 (5 critical, 6 warning)
- Fixed: 11
- Skipped: 0

## Fixed Issues

### CR-01: Background task exception swallowed — channel silently completes on first error

**Files modified:** `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs`
**Commit:** 90c3196
**Applied fix:** Changed `_ = Task.Run(...)` to `var loopTask = Task.Run(...)` and added `await loopTask` after the `ReadAllAsync` loop to propagate any unhandled exception from `RunConnectionLoopAsync` to the `IAsyncEnumerable` consumer.

---

### CR-02: SuppressBinarySensor always false in verdict processing — post-reconnect suppression broken

**Files modified:** `orchestrator/Argus.Orchestrator/Detection/EntityRuntimeState.cs`, `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs`
**Commit:** b2bc8cc
**Applied fix:** Added `SuppressBinarySensor { get; set; }` property to `EntityRuntimeState`. In the write loop, set `entityState.SuppressBinarySensor = reading.SuppressBinarySensor` before forwarding to detector. In the read task, replaced the hardcoded `false` synthetic reading with `entityState.SuppressBinarySensor` so the verdict path correctly applies post-reconnect suppression.

---

### CR-03: MQTT reconnect loop ignores cancellation — service cannot shut down cleanly

**Files modified:** `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs`
**Commit:** bc4a016
**Applied fix:** Added `private readonly CancellationTokenSource _cts = new()` field. In `OnDisconnectedAsync`, changed `Task.Delay(totalDelay)` to `Task.Delay(totalDelay, _cts.Token)`, `ConnectAsync(..., CancellationToken.None)` to `ConnectAsync(..., _cts.Token)`, and `PublishOnlineAsync(CancellationToken.None)` to `PublishOnlineAsync(_cts.Token)`. In `DisposeAsync`, added `_cts.Cancel()` before removing the handler.

---

### CR-04: X509Certificate2 private key may be freed before TLS handshake on .NET 8 Linux

**Files modified:** `orchestrator/Argus.Orchestrator/Detection/DetectorChannelFactory.cs`
**Commit:** 7c181d3
**Applied fix:** Changed single-step `X509Certificate2.CreateFromPemFile` to the two-step pattern: `using var tempCert = X509Certificate2.CreateFromPemFile(...)` followed by `var clientCert = new X509Certificate2(tempCert.Export(X509ContentType.Pkcs12))`. This persists the private key into the cert object, preventing GC collection before TLS handshake.

---

### CR-05: Per-entity availability topic mismatch — graceful degradation is unreachable

**Files modified:** `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs`
**Commit:** f7e2237
**Applied fix:** Replaced the single `availability_topic` + `payload_available` + `payload_not_available` fields in both `BuildBinarySensorConfig` and `BuildSensorConfig` with an `availability` array containing two entries: the bridge-level topic and the per-entity `argus/{slug}/availability` topic. HA 2022.9+ supports the availability list format, enabling per-entity graceful degradation.

---

### WR-01: Double-checked locking unsound under Python's memory model

**Files modified:** `detector/argus_detector/registry.py`
**Commit:** e561500
**Applied fix:** Replaced the double-checked locking pattern (unlocked fast-path read + locked slow-path create) with a simple `with self._lock` around both the existence check and creation. Eliminates the risk of reading a partially-rehashed dict during concurrent resize.

---

### WR-02: `logging_setup.py` — `skip` set built from class `__dict__` keys

**Files modified:** `detector/argus_detector/logging_setup.py`
**Commit:** c78c5a3
**Applied fix:** Replaced the inline `skip = logging.LogRecord.__dict__.keys() | {...}` with a module-level `_STANDARD_ATTRS` frozenset built from a reference `LogRecord` instance: `frozenset(logging.LogRecord("", 0, "", 0, "", (), None).__dict__.keys()) | {"message", "asctime"}`. Updated the `format()` method to use `_STANDARD_ATTRS` instead of the old `skip` set.

---

### WR-03: `ScoreStreamPipeline.RunAsync` fans out all entities over a shared `IAsyncEnumerable`

**Files modified:** `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs`
**Commit:** 153538a
**Applied fix:** Added `using System.Threading.Channels`. In the multi-entity `RunAsync` overload, replaced the direct fan-out of the shared `IAsyncEnumerable` to per-entity tasks with: (1) one bounded `Channel<HaReading>` per entity keyed by entity ID; (2) a `fanOutTask` that reads the source `IAsyncEnumerable` once and routes each reading to the matching channel; (3) per-entity tasks that consume from their dedicated channel's `ReadAllAsync`. `Task.WhenAll` now includes `fanOutTask`.

---

### WR-04: `servicer.py` does not handle empty `entity_id` or loop exceptions

**Files modified:** `detector/argus_detector/servicer.py`
**Commit:** 1265079
**Applied fix:** Added `import grpc`. Added `if not point.entity_id` guard with a `logger.warning` and `continue` before processing. Wrapped the entire scoring block in `try/except Exception` that calls `context.abort(grpc.StatusCode.INTERNAL, "scoring error")` and returns on any unexpected exception, ensuring the client receives a proper status code rather than `StatusCode.Unknown`.

---

### WR-05: `MqttPublisherWorker` uses duplicate `LogEvents.MqttWorkerStarted` for "ready" log

**Files modified:** `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs`, `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs`
**Commit:** faa5b2d
**Applied fix:** Added `public static readonly EventId MqttWorkerReady = new(4007, nameof(MqttWorkerReady))` to `LogEvents.cs`. Updated the "ready" log line in `MqttPublisherWorker.cs` from `LogEvents.MqttWorkerStarted` to `LogEvents.MqttWorkerReady`.

---

### WR-06: `ConnectionSettings` configured twice from the same env vars

**Files modified:** `orchestrator/Argus.Orchestrator/Program.cs`
**Commit:** 2af412c
**Applied fix:** Removed the `builder.Services.Configure<ConnectionSettings>(...)` call entirely. Added `MqttPort = int.TryParse(builder.Configuration["ARGUS_MQTT_PORT"], out var mqttPort) ? mqttPort : 1883` to the single authoritative `ConnectionSettings` instance. All DI consumers now receive the one singleton that includes a properly parsed `MqttPort`.

---

_Fixed: 2026-06-10_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
