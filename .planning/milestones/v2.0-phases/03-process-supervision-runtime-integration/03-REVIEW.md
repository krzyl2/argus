---
phase: 03-process-supervision-runtime-integration
reviewed: 2026-06-30T00:00:00Z
depth: standard
files_reviewed: 19
files_reviewed_list:
  - argus/config.yaml
  - argus/rootfs/etc/cont-init.d/10-config-gen.sh
  - argus/rootfs/usr/local/bin/wait-detector.py
  - detector/tests/test_wait_detector.py
  - orchestrator/Argus.Orchestrator.Tests/HealthEntityTests.cs
  - orchestrator/Argus.Orchestrator.Tests/MqttConnectionTests.cs
  - orchestrator/Argus.Orchestrator.Tests/StartupSensorLogTests.cs
  - orchestrator/Argus.Orchestrator.Tests/SupervisorMqttCredentialSourceTests.cs
  - orchestrator/Argus.Orchestrator/Detection/DetectionGateway.cs
  - orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs
  - orchestrator/Argus.Orchestrator/Health/ArgusHealthSignals.cs
  - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
  - orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs
  - orchestrator/Argus.Orchestrator/Mqtt/HealthEvaluator.cs
  - orchestrator/Argus.Orchestrator/Mqtt/IMqttCredentialSource.cs
  - orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs
  - orchestrator/Argus.Orchestrator/Mqtt/MqttCredentials.cs
  - orchestrator/Argus.Orchestrator/Mqtt/SupervisorMqttCredentialSource.cs
  - orchestrator/Argus.Orchestrator/Program.cs
  - orchestrator/Argus.Orchestrator/Workers/HealthPublisherWorker.cs
findings:
  critical: 0
  warning: 5
  info: 6
  total: 11
status: issues_found
---

# Phase 3: Code Review Report

**Reviewed:** 2026-06-30
**Depth:** standard
**Files Reviewed:** 19
**Status:** issues_found

## Summary

Phase 3 wires process supervision (s6 cont-init config-gen, gRPC health poller),
the Supervisor MQTT credential fetch (SUPV-03), composite health publishing
(HEALTH-01), and startup sensor discovery (UICFG-05). Overall the code is clean,
well-commented, and the security-sensitive paths are handled correctly.

**Security (focus area): clean.** The Supervisor credential fetch never logs
the token, username, or password — both `SupervisorMqttCredentialSource` (lines
65–67, 73–76) and `MqttConnection.BuildConnectOptionsAsync` (lines 88–90) log
host/port only. The shell config-gen writes secrets via `printf` to the s6
environment dir, never `echo`. No hardcoded secrets, no injection vectors, no
unsafe deserialization found.

The findings below are correctness and robustness concerns in the reconnect /
health paths, plus a few maintainability items. None are blocking, but WR-01
through WR-03 affect runtime behavior of the supervision/health features that
are the point of this phase.

## Warnings

### WR-01: `ArgusHealthSignals.HaConnected` is never reset to false on a graceful subscribe-loop exit

**File:** `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs:141-150`
**Issue:** `HaConnected` is set `true` after a successful connect (line 116) and
set `false` only in the `catch (Exception)` block (line 150). If
`SubscribeAndForwardAsync` returns normally — e.g. `WaitForConnectionToCloseAsync`
completes because the WS closed cleanly, or the `onCompleted` path fires — control
flows back to the top of the `while` loop without an exception. During the window
between that clean close and the next successful `ConnectAsync`, `HaConnected`
stays `true`, so `HealthPublisherWorker` reports the add-on healthy (OFF) while HA
is actually disconnected. This undermines HEALTH-01 (the composite health is
supposed to flip to ON when HA is down).
**Fix:** Clear the signal at the start of each loop iteration before reconnecting,
or immediately after the subscribe call returns:
```csharp
// After SubscribeAndForwardAsync returns (connection closed), mark disconnected
await SubscribeAndForwardAsync(connection, writer, ct).ConfigureAwait(false);
_signals.HaConnected = false; // clean close: HA is no longer connected
```
Alternatively set `HaConnected = false` at the top of the `while` body before the
connect attempt.

### WR-02: MQTT reconnect loop can race / double-connect with the worker's initial `ConnectAsync`

**File:** `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs:108-142`
**Issue:** `OnDisconnectedAsync` runs an unbounded `while (true)` reconnect loop
that calls `_client.ConnectAsync` directly. There is no guard preventing this from
overlapping with another connect path, and the loop only exits via `return` (on
success) or never (it swallows every exception and retries forever). If the
broker drops and re-establishes rapidly, or if `MqttPublisherWorker.ConnectAsync`
and a fired `DisconnectedAsync` handler interleave, two concurrent `ConnectAsync`
calls can hit the same `IMqttClient`. MQTTnet does not guarantee thread-safety for
concurrent connect attempts on one client. There is also no `IsConnected` check at
the top of each loop iteration, so a reconnect that succeeds via another path still
burns one more attempt.
**Fix:** Serialize connect attempts with a `SemaphoreSlim(1,1)` around the
connect logic, and bail out early if already connected:
```csharp
private readonly SemaphoreSlim _connectGate = new(1, 1);
// in the reconnect loop, before ConnectAsync:
if (_client.IsConnected) return;
await _connectGate.WaitAsync(_cts.Token);
try { /* build options + ConnectAsync */ } finally { _connectGate.Release(); }
```

### WR-03: `OnDisconnectedAsync` reconnect loop never terminates on permanent failure and ignores cancellation exit path cleanly

**File:** `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs:119-141`
**Issue:** The `while (true)` loop catches `Exception` broadly (line 136),
including `OperationCanceledException` raised when `_cts` is cancelled during
`DisposeAsync`. On cancellation the loop logs "MQTT reconnect attempt failed",
recomputes the backoff, and immediately loops again, calling
`Task.Delay(totalDelay, _cts.Token)` which throws `OperationCanceledException`
again — a busy spin of catch/log/retry until the task is finally abandoned. The
disconnected handler task is also never awaited, so exceptions are unobserved.
**Fix:** Break the loop on cancellation and stop logging it as a failure:
```csharp
catch (OperationCanceledException) { return; }
catch (Exception ex)
{
    _logger.LogWarning(LogEvents.MqttReconnecting, ex, "MQTT reconnect attempt failed");
    delay = delay * 2 < maxDelay ? delay * 2 : maxDelay;
}
```

### WR-04: Backoff doubling can overshoot the cap and skip the max value

**File:** `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs:139`
**Issue:** `delay = delay * 2 < maxDelay ? delay * 2 : maxDelay;` evaluates the
*doubled* value against the cap. Starting at 1s: 2, 4, 8, 16, 32 — next double is
64 which is `>= 60`, so it jumps straight to 60 (fine), but the sequence never
settles cleanly and the comparison logic is subtly off: when `delay*2 == maxDelay`
it picks `maxDelay` (correct), but the intent (clamp) is clearer with `Math.Min`.
More importantly, the jitter at line 124 can produce a *negative* total delay when
`delay` is small and the random factor is near -0.1 only fractionally — actually
bounded fine here, but the clamp pattern is inconsistent with
`NetDaemonHaEventSource` line 166 which correctly uses `Math.Min(backoffSeconds * 2, BackoffMaxSeconds)`.
**Fix:** Use the same idiom as the HA source for consistency and correctness:
```csharp
delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));
```

### WR-05: `SubscribeAndForwardAsync` subscription disposed before `onError`/`onCompleted` can be observed; TCS result is never awaited

**File:** `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs:216-254`
**Issue:** A `TaskCompletionSource` (`tcs`) is created and wired to `onError`
(line 247) and `onCompleted` (line 248), and a CT registration sets it cancelled
(line 251) — but the method only awaits `connection.WaitForConnectionToCloseAsync`
(line 254), never `tcs.Task`. The `tcs` is therefore dead code: an `onError` from
the Rx stream (e.g. a subscription-level failure that does not close the WS) is
captured into `tcs` and silently discarded, so a broken event stream on a still-open
connection would not trigger reconnect. The `using var sub` is also disposed as soon
as `WaitForConnectionToCloseAsync` returns, which is the intended teardown, but the
unused `tcs` signals the original intent (await whichever completes first) was lost.
**Fix:** Await the first of the two signals so stream errors trigger reconnect:
```csharp
await Task.WhenAny(
    connection.WaitForConnectionToCloseAsync(ct),
    tcs.Task).ConfigureAwait(false);
```
Or remove the `tcs` plumbing entirely if WS-close is genuinely the only intended
exit, to avoid the misleading dead code.

## Info

### IN-01: `BridgeAvailabilityTopic` constant is duplicated across three types

**File:** `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs:23`,
`orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs:20`, and
`StatePublisher.BridgeAvailabilityTopic` (referenced in MqttConnectionTests.cs:111)
**Issue:** The literal `"argus/bridge/availability"` is defined in at least three
places (one `public`, one `private`). A future topic change risks updating one and
missing another, silently breaking LWT/availability correlation.
**Fix:** Centralize in one shared constants class (e.g. `MqttTopics`) and reference
it everywhere.

### IN-02: `HealthDiscoveryPayload` device identifiers differ between health entity and per-sensor entities

**File:** `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs:171` vs `63/97`
**Issue:** The health entity uses device identifier `"argus_addon"` while each
per-sensor entity uses `slug` (e.g. `sensor_salon_temperatura`). This is likely
intentional (health belongs to the add-on device, sensors to per-sensor devices),
but worth confirming the health entity is meant to be its own HA device rather than
grouped with anything. No action required if intentional.

### IN-03: `wait-detector.py` uses bare `except Exception` masking all failures

**File:** `argus/rootfs/usr/local/bin/wait-detector.py:39`
**Issue:** `check_serving` catches every exception and returns `False` (annotated
`# noqa: BLE001`, so intentional). This is acceptable for a liveness poller, but a
persistent programming error (e.g. wrong proto import) would be indistinguishable
from "not yet serving" and would loop forever in `wait_until_serving` with no
`max_attempts`. Consider logging the exception type at debug level so a misconfig
is diagnosable from add-on logs.

### IN-04: `HttpClient` is constructed directly in DI registration (no IHttpClientFactory)

**File:** `orchestrator/Argus.Orchestrator/Program.cs:92`
**Issue:** `new HttpClient()` is registered as part of a singleton credential
source. Since the source is a singleton and the client is reused for the life of
the process, socket exhaustion is not a concern here, but the client has no
configured timeout — a hung Supervisor API call relies solely on the caller's
`CancellationToken`, and `GetAsync` callers in the MQTT reconnect path pass
`_cts.Token` which has no timeout. A stalled Supervisor response could block a
reconnect attempt indefinitely.
**Fix:** Set `new HttpClient { Timeout = TimeSpan.FromSeconds(10) }` (or use
`IHttpClientFactory`).

### IN-05: `config.yaml` watchdog uses `[HOST]` placeholder with a known-broken remote case

**File:** `argus/config.yaml:11-15`
**Issue:** The inline comment already documents that in remote mode no local
process listens on 50051, so the TCP watchdog would falsely restart the add-on.
This is flagged as deferred to live-HA verification. Tracking only — the comment
correctly captures the risk.

### IN-06: `delays` array / attempt indexing in `DetectionGateway` logs a confusing "next attempt" number

**File:** `orchestrator/Argus.Orchestrator/Detection/DetectionGateway.cs:82-87`
**Issue:** After incrementing `attempt`, the retry log uses `attempt + 1` as the
"Next" attempt number while `delaySeconds` was selected using the pre-increment
index. The numbers are internally consistent but the off-by-context labeling
("Retrying ... attempt {Next}") can read as if it skips a number in logs. Minor
log-clarity nit; no functional bug.

---

_Reviewed: 2026-06-30_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
