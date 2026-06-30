---
phase: 03-process-supervision-runtime-integration
fixed_at: 2026-06-30T00:00:00Z
review_path: .planning/phases/03-process-supervision-runtime-integration/03-REVIEW.md
iteration: 1
findings_in_scope: 5
fixed: 5
skipped: 0
status: all_fixed
---

# Phase 3: Code Review Fix Report

**Fixed at:** 2026-06-30
**Source review:** .planning/phases/03-process-supervision-runtime-integration/03-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 5 (WR-01..WR-05; no Critical findings)
- Fixed: 5
- Skipped: 0

All Critical and Warning findings were addressed. The orchestrator project
builds clean (0 warnings, 0 errors) after each fix. The 2 pre-existing
`DiscoveryPayloadTests` failures (`BinarySensorPayload_AvailabilityTopicIsBridgeLevel`,
`BinarySensorPayload_PayloadAvailableOnline`) were confirmed failing on the
pre-fix baseline and are unrelated to these changes — they were not introduced
by any fix here. All 9 `MqttConnectionTests` continue to pass; no existing test
expectations were changed.

## Fixed Issues

### WR-01: `ArgusHealthSignals.HaConnected` never reset on graceful subscribe-loop exit

**Files modified:** `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs`
**Commit:** 5349874
**Applied fix:** Added `_signals.HaConnected = false;` immediately after
`SubscribeAndForwardAsync` returns in the reconnect loop. A clean WS close (no
exception) previously skipped the catch block that clears the signal, leaving
the composite health reporting the add-on healthy while HA was disconnected.

### WR-02: MQTT reconnect loop can race / double-connect with the worker's initial `ConnectAsync`

**Files modified:** `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs`
**Commit:** 41b5d1a
**Applied fix:** Added a `SemaphoreSlim(1, 1)` (`_connectGate`) serializing both
the public `ConnectAsync` and the reconnect loop's connect logic, with an early
`if (_client.IsConnected) return;` guard both before acquiring the gate and again
inside it (double-checked). The semaphore is disposed in `DisposeAsync`.

### WR-03: Reconnect loop never terminates cleanly on cancellation

**Files modified:** `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs`
**Commit:** 0e8e33c
**Applied fix:** Added `catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }`
ahead of the broad `catch (Exception)` so cancellation during `DisposeAsync`
exits the loop cleanly instead of logging a "reconnect failed" warning and
busy-spinning on `Task.Delay(_cts.Token)`.

### WR-04: Backoff doubling clamp uses inconsistent idiom

**Files modified:** `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs`
**Commit:** 4e4a68a
**Applied fix:** Replaced `delay = delay * 2 < maxDelay ? delay * 2 : maxDelay;`
with `delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));`,
matching the `NetDaemonHaEventSource` clamp idiom.

### WR-05: `tcs` (onError/onCompleted) never awaited; stream errors discarded

**Files modified:** `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs`
**Commit:** d33458b
**Applied fix:** Replaced the lone `await connection.WaitForConnectionToCloseAsync(ct)`
with `await Task.WhenAny(closeTask, tcs.Task)` followed by `await completed` so a
subscription-level `onError` (stream failure that does not close the WS) rethrows
and triggers the outer loop's backoff/reconnect instead of being silently dropped.

## Skipped Issues

None — all in-scope findings were fixed.

## Notes

- The `gsd-sdk query commit` handler returned `{"committed": false, "reason": "commit_docs disabled"}`
  and did not create commits. Fixes were therefore committed with direct `git commit`
  using the required `fix(03): {id} {description}` message format and the mandated
  Co-Authored-By trailer. Each finding was committed atomically.
- WR-01 and WR-05 both touch `NetDaemonHaEventSource.cs`; they were committed as two
  separate atomic commits (WR-01 first, then WR-05) by staging each change in isolation.
- WR-02, WR-03, WR-04 all touch `MqttConnection.cs` and were committed as three separate
  atomic commits in that order.

---

_Fixed: 2026-06-30_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
