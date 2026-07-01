# Phase 03 Plan 04: Gap Closure — NetDaemonHaEventSource Migration

**Type:** gap-closure (post-verifier follow-up on Plan 03-02)
**Date:** 2026-07-01

## Summary

Two gaps found by the verifier in the Phase 3 ILiveEntitiesConfig migration were fixed:

---

## GAP 1 — DI injection crash (critical)

**File:** `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs`

**Problem:** `NetDaemonHaEventSource` still injected the raw `EntitiesConfig` in its constructor
(field `_entitiesConfig`, parameter `entitiesConfig`). Plan 03-02 removed the raw `EntitiesConfig`
singleton from DI (`Program.cs` now registers only `ILiveEntitiesConfig`). At startup the .NET DI
container would throw `InvalidOperationException: Unable to resolve service for type 'EntitiesConfig'`
when resolving `IHaEventSource` (registered as `NetDaemonHaEventSource` at Program.cs:92).

**Fix:** Replaced the `EntitiesConfig entitiesConfig` constructor parameter with
`ILiveEntitiesConfig liveConfig`. The null-guard was preserved (`?? throw ArgumentNullException`).
No `Program.cs` change needed — `ILiveEntitiesConfig` was already registered as a singleton.

---

## GAP 2 — Live filter not updated on hot-reload (CFG-04 functional gap)

**File:** `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs`

**Problem:** `_configuredEntities` (a `HashSet<string>`) was built once at construction from the
initial `EntitiesConfig.Entities` and never updated. After a hot-reload that added entities,
`state_changed` events for the newly-added entity_ids were filtered out (lines ~203, ~220 used
`_configuredEntities`; line ~123 passed it to `_sensorRegistry.UpdateSnapshot`), so the live
pipeline received no readings for new entities without a restart.

**Fix:** Changed `readonly HashSet<string>` to `volatile HashSet<string>`. On construction the
set is built from `_liveConfig.Get()` via a new helper `BuildEntitySet()`. A `ConfigChanged`
handler is subscribed that atomically replaces `_configuredEntities` with a fresh HashSet built
from the new config reference (single-writer volatile reference swap, matching the
`HaSensorRegistry` pattern). All read sites (`TryMap` calls, `UpdateSnapshot` call) naturally
read the current reference since they read `_configuredEntities` at call time.

An internal test-seam property `InternalConfiguredEntities` was added (accessible via existing
`InternalsVisibleTo`) to allow unit tests to assert the rebuilt set without needing a live
WebSocket connection.

---

## Regression Tests Added

**File:** `orchestrator/Argus.Orchestrator.Tests/NetDaemonHaEventSourceLiveFilterTests.cs`

Three tests:
1. `AfterSwap_NewEntityIsAcceptedByFilter` — verifies that after `Swap()` adds `sensor.new`,
   `TryMap` accepts events for it (was filtered before the swap).
2. `AfterSwap_RemovedEntityIsRejectedByFilter` — verifies that removed entities are filtered out
   after `Swap()`.
3. `Constructor_AcceptsILiveEntitiesConfig_DoesNotThrow` — GAP 1 regression: construction with
   `ILiveEntitiesConfig` succeeds (would have failed with old `EntitiesConfig` DI gap).

---

## Build / Test Result

- `dotnet build orchestrator/Argus.Orchestrator.sln`: 0 errors, 0 warnings
- `dotnet test orchestrator/Argus.Orchestrator.sln`: 221 passed, 0 failed
