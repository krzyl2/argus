---
phase: 03-config-readwrite-detector-assignment-reload
verified: 2026-07-01T10:00:00Z
status: human_needed
score: 10/10 must-haves verified
overrides_applied: 0
re_verification:
  previous_status: gaps_found
  previous_score: 8/10
  gaps_closed:
    - "NetDaemonHaEventSource migrated to ILiveEntitiesConfig — DI startup crash eliminated (Gap 1)"
    - "NetDaemonHaEventSource._configuredEntities rebuilt on ConfigChanged — live entity filter for reload (Gap 2)"
  gaps_remaining: []
  regressions: []
human_verification:
  - test: "Open the Argus 'Open Web UI' in Home Assistant. Observe tracked entities in the entity picker."
    expected: "Each tracked entity shows a <details> disclosure section 'Detectors (N)' with the correct detector type selected and parameter values matching what is saved in /data/entities.yaml."
    why_human: "Requires a live HA Ingress session; cannot verify browser rendering programmatically."
  - test: "With at least one tracked entity in the UI, modify a detector parameter (e.g. HST window from 250 to 300), click Save. Observe the add-on log."
    expected: "The success banner appears; the orchestrator log shows ConfigReloadTriggered and ConfigReloadComplete events within 2 seconds of the save; the add-on process does not restart (s6-supervise does not log a service restart)."
    why_human: "Wall-clock reload latency (<2 s) and absence of process restart require a live HA OS environment."
  - test: "With at least two tracked entities, deselect one and save. Wait up to 30 seconds, then check the HA entity registry."
    expected: "The binary_sensor.argus_* and sensor.argus_* entities corresponding to the removed entity disappear from HA (not just 'unavailable' — actually gone) within 30 seconds."
    why_human: "Requires live HA with MQTT discovery to observe entity registry changes."
---

# Phase 3: Config Read/Write + Detector Assignment + Reload Verification Report

**Phase Goal:** The user reads current tracked-entity config in the UI, assigns one or more detectors (HST/MAD/STL) with editable parameters per entity, saves, and the running pipeline reloads within seconds without restarting the add-on. Removed entities have their MQTT discovery topics retracted. ILiveEntitiesConfig is the invasive cross-cutting change.
**Verified:** 2026-07-01T10:00:00Z
**Status:** human_needed
**Re-verification:** Yes — after gap closure (commit 363ca59)

## Gap Closure Summary

Both gaps from the initial verification (score 8/10) are closed in commit 363ca59:

**Gap 1 — DI startup crash:** `NetDaemonHaEventSource` ctor now accepts `ILiveEntitiesConfig liveConfig` (line 46) instead of raw `EntitiesConfig`. Raw `EntitiesConfig` is not registered in DI (confirmed: `Program.cs` only has `AddSingleton<ILiveEntitiesConfig>(liveConfig)` at line 26). The DI graph resolves cleanly.

**Gap 2 — Stale entity filter on reload:** The ctor initialises `_configuredEntities` from `_liveConfig.Get()` (line 60) and subscribes `ConfigChanged` to rebuild it atomically: `_liveConfig.ConfigChanged += (_, _) => _configuredEntities = BuildEntitySet(_liveConfig.Get())` (lines 64-65). The field is `volatile`, matching the single-writer swap pattern described in the inline comment.

The private static helper `BuildEntitySet(EntitiesConfig cfg)` at line 69 takes a plain `EntitiesConfig` parameter fed from `_liveConfig.Get()` — this is a method parameter, not a DI dependency, and is correct.

Regression tests: `NetDaemonHaEventSourceLiveFilterTests` — 3/3 pass:
- `Constructor_AcceptsILiveEntitiesConfig_DoesNotThrow` (Gap 1)
- `AfterSwap_NewEntityIsAcceptedByFilter` (Gap 2, add path)
- `AfterSwap_RemovedEntityIsRejectedByFilter` (Gap 2, remove path)

Full test suite: 221/221 pass. Build: 0 warnings, 0 errors.

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | A single volatile EntitiesConfig reference can be swapped atomically and readers always see the newest reference | VERIFIED | `LiveEntitiesConfig.cs`: volatile field + `Interlocked.Exchange(ref _current, newConfig)`. `LiveEntitiesConfigTests.cs` covers Get/Swap ordering + concurrency (500 iterations, no null, no exception). |
| 2 | Swapping the config fires ConfigChanged AFTER the reference is exchanged | VERIFIED | `LiveEntitiesConfig.Swap`: `ConfigChanged?.Invoke` follows `Interlocked.Exchange` (line 49). Test `Swap_FiresConfigChangedAfterExchange` asserts `Assert.Same(newConfig, capturedFromGet)`. |
| 3 | DiscoveryPublisher can publish empty retained payloads to a removed entity's binary_sensor + sensor config topics | VERIFIED | `DiscoveryPublisher.RetractAsync` iterates removed entities, publishes `string.Empty` retain=true to both topics. `MqttRetractionTests.cs` covers 2-per-entity, correct topics, empty payload, retain-true, empty-list publishes-nothing, non-removed not touched. |
| 4 | The four EntitiesConfig consumers (ScoreStreamPipeline, BatchSchedulerWorker, MqttPublisherWorker, NetDaemonHaEventSource) read the LIVE config, not a ctor-captured stale reference | VERIFIED | `ScoreStreamPipeline.BuildEntityStates()` reads `_liveConfig.Get().Entities` at RunAsync entry (line 262). `BatchSchedulerWorker` reads per-cycle. `MqttPublisherWorker` reads on startup and ConfigChanged. `NetDaemonHaEventSource`: ctor builds from `_liveConfig.Get()` (line 60); ConfigChanged rebuilds via `BuildEntitySet(_liveConfig.Get())` (line 65). Raw `EntitiesConfig` NOT injected via DI — only `ILiveEntitiesConfig` is registered (`Program.cs` line 26). |
| 5 | HaListenerWorker restarts the ScoreStreamPipeline.RunAsync loop on ConfigChanged via inner CTS; MQTT + gRPC stay alive | VERIFIED | `HaListenerWorker.ExecuteAsync` implements the inner-CTS restart loop (lines 83-149): ConfigChanged handler cancels `innerCts` (null-safe), loop re-enters `RunPipelineAsync`. `NetDaemonHaEventSource` now rebuilds its entity filter on ConfigChanged so newly-added entities receive `state_changed` events in the restarted pipeline. `HaListenerWorkerReloadTests.cs` covers restart count, stoppingToken exit, retraction diff, no ObjectDisposedException under rapid events. |
| 6 | On reload, removed entities are retracted from MQTT and newly-added entities get re-published discovery | VERIFIED | `HaListenerWorker.RetractAndRepublishAsync` diffs old vs new entity sets (OrdinalIgnoreCase); calls `DiscoveryPublisher.RetractAsync` for removed, `DiscoveryPublisher.PublishAllAsync` for added, using `stoppingToken` (not innerCts) so broker calls survive the pipeline cancel. `HaListenerWorkerReloadTests.ConfigChanged_RetractsRemovedEntities` asserts correct topics. |
| 7 | Host shutdown (stoppingToken) propagates cleanly and exits the restart loop | VERIFIED | OCE catch filter: `when (innerCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)` — host shutdown OCE is not caught, propagates to ASP.NET shutdown. `HaListenerWorkerReloadTests.StoppingToken_ExitsLoop` asserts exactly 1 RunAsync call. |
| 8 | The config UI shows current per-entity detector assignments pre-filled from entities.yaml, falling back to type defaults | VERIFIED | `EntityPickerPage.BuildDetectorDisclosure` reads `entityConfig.Detectors` from the passed `EntitiesConfig`; falls back to `[new DetectorConfig { Name = "hst" }]` for empty/missing. `BuildDetectorEntry` pre-fills each param from `detector.Params` or type default. `Program.cs` GET /sensors passes `liveCfg.Get()` (line 241) and GET /api/sensors passes `liveCfg.Get()` (line 255). |
| 9 | Saving persists the submitted per-entity detector lists via YamlDotNet and triggers reload via Swap | VERIFIED | `Program.cs` POST /api/sensors/save: `DetectorFieldParser.Parse` parses indexed fields; empty list defaults to HST; `SerializerBuilder` serializes root dict; `ConfigWriter.WriteAsync`; then `EntitiesConfigLoader.Load` + `liveCfg.Swap(newConfig)` (line 386). `SaveEndpointDetectorParsingTests.SwapCalledAfterWrite_LiveConfigReflectsNewEntities` asserts Swap is called. |
| 10 | Config writes are serialized (SemaphoreSlim) and atomic (temp-then-rename) | VERIFIED | `ConfigWriter.WriteAsync` uses `SemaphoreSlim(1,1)` + `File.Move(tmp, targetPath, overwrite: true)` (atomic POSIX rename). |

**Score:** 10/10 truths verified

### Deferred Items

None.

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `orchestrator/Argus.Orchestrator/Config/LiveEntitiesConfig.cs` | ILiveEntitiesConfig + volatile-swap + ConfigChanged | VERIFIED | Interface + sealed impl with Interlocked.Exchange; ConfigChanged fired after exchange |
| `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs` | RetractAsync with empty retained payloads | VERIFIED | `RetractAsync(delegate, removedEntities, ct)` overload, publishes `string.Empty` retain=true |
| `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` | ConfigReloadTriggered, ConfigReloadComplete, MqttRetractionPublished | VERIFIED | IDs 7004-7006 |
| `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` | Accepts ILiveEntitiesConfig; rebuilds filter on ConfigChanged | VERIFIED | Ctor line 46-66: `ILiveEntitiesConfig liveConfig`; volatile `_configuredEntities` rebuilt on `ConfigChanged`; `BuildEntitySet` is a private static helper (plain param, not DI). |
| `orchestrator/Argus.Orchestrator.Tests/NetDaemonHaEventSourceLiveFilterTests.cs` | 3 regression tests for Gap 1 + Gap 2 | VERIFIED | `Constructor_AcceptsILiveEntitiesConfig_DoesNotThrow`, `AfterSwap_NewEntityIsAcceptedByFilter`, `AfterSwap_RemovedEntityIsRejectedByFilter` — 3/3 pass |
| `orchestrator/Argus.Orchestrator.Tests/LiveEntitiesConfigTests.cs` | Swap/Get/ConfigChanged ordering + concurrency | VERIFIED | 5 test methods covering all required behaviors |
| `orchestrator/Argus.Orchestrator.Tests/MqttRetractionTests.cs` | Retract publishes empty retained payloads to correct topics | VERIFIED | 8 test methods |
| `orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs` | Inner-CTS restart loop | VERIFIED | Full restart loop with null-before-dispose, OCE guard, retraction diff, virtual seams |
| `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs` | `_liveConfig.Get()` in BuildEntityStates | VERIFIED | Line 262: `foreach (var entity in _liveConfig.Get().Entities)` |
| `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` | `_liveConfig.Get()` in RunBatchAsync + RunNightlyFitAsync | VERIFIED | Lines 128 + 217: per-cycle live reads |
| `orchestrator/Argus.Orchestrator/Program.cs` | `AddSingleton<ILiveEntitiesConfig>` only; all four consumers rewired; GET /sensors live read | VERIFIED | Line 26: `AddSingleton<ILiveEntitiesConfig>(liveConfig)`; raw `EntitiesConfig` NOT registered; `NetDaemonHaEventSource` resolves via `ILiveEntitiesConfig`; GET /sensors + GET /api/sensors pass `liveCfg.Get()`. |
| `orchestrator/Argus.Orchestrator.Tests/HaListenerWorkerReloadTests.cs` | ConfigChanged restarts RunAsync; stoppingToken exits loop | VERIFIED | 4 test methods |
| `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs` | BuildDetectorEntry, disclosure rows, reloading banner | VERIFIED | BuildDetectorEntry (line 182), BuildDetectorDisclosure (line 356), BuildReloadingBanner (line 246) |
| `orchestrator/Argus.Orchestrator/Program.cs` | GET /api/detectors/new-entry + extended POST /api/sensors/save + Swap | VERIFIED | Lines 263-277 (new-entry endpoint); lines 281-405 (extended save with detector parse + Swap at line 386) |
| `orchestrator/Argus.Orchestrator/wwwroot/css/argus.css` | Phase 3 detector-assignment BEM blocks | VERIFIED | Phase-3 section at line 440; 14 BEM blocks |
| `orchestrator/Argus.Orchestrator.Tests/SaveEndpointDetectorParsingTests.cs` | Indexed detector form parsing + empty-list HST + YAML round-trip | VERIFIED | 12 test methods |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `LiveEntitiesConfig.Swap` | `ConfigChanged event` | `ConfigChanged?.Invoke` after Interlocked.Exchange | WIRED | Line 49 |
| `DiscoveryPublisher.RetractAsync` | `MqttConnection.PublishAsync` | empty retained payload to config topics | WIRED | Iterates removed entities, publishes `string.Empty` retain=true |
| `LiveEntitiesConfig.ConfigChanged` | `HaListenerWorker inner CTS cancel` | event subscription cancels innerCts | WIRED | `_liveConfig.ConfigChanged += OnConfigChanged`; handler cancels `innerCts` |
| `LiveEntitiesConfig.ConfigChanged` | `NetDaemonHaEventSource._configuredEntities` rebuild | lambda in ctor line 64-65 | WIRED | `_liveConfig.ConfigChanged += (_, _) => _configuredEntities = BuildEntitySet(_liveConfig.Get())` |
| `HaListenerWorker reload path` | `DiscoveryPublisher.RetractAsync` | diff old vs new entity ids before restart | WIRED | `DiscoveryPublisher.RetractAsync(delegate, removed, stoppingToken)` |
| `ScoreStreamPipeline.BuildEntityStates` | `ILiveEntitiesConfig.Get().Entities` | live read at RunAsync entry | WIRED | Line 262 |
| `POST /api/sensors/save` | `ILiveEntitiesConfig.Swap` | ConfigWriter.WriteAsync then re-load + Swap | WIRED | Line 386: `liveCfg.Swap(newConfig)` |
| `EntityPickerPage detector rows` | `ILiveEntitiesConfig.Get() config` | BuildFullPage reads Detectors + Params | WIRED | `BuildDetectorDisclosure` reads from `config` passed as `liveCfg.Get()` from Program.cs |
| `GET /api/detectors/new-entry` | `EntityPickerPage.BuildDetectorEntry` | htmx fragment with HST defaults | WIRED | Lines 273-276 |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|--------------------|--------|
| `EntityPickerPage` detector disclosure | `config.Entities[*].Detectors` | `liveCfg.Get()` passed from `GET /sensors` | Yes — reads from deserialized entities.yaml via EntitiesConfigLoader | FLOWING |
| `ScoreStreamPipeline.BuildEntityStates` | `_liveConfig.Get().Entities` | `ILiveEntitiesConfig` singleton (post-Swap) | Yes — reads current atomic reference | FLOWING |
| `BatchSchedulerWorker.RunBatchAsync` | `_liveConfig.Get().Entities` | Per-cycle live read | Yes — reads current atomic reference per tick | FLOWING |
| `MqttPublisherWorker` ConfigChanged republish | `_liveConfig.Get().Entities` | ILiveEntitiesConfig.ConfigChanged | Yes — reads post-Swap reference | FLOWING |
| `NetDaemonHaEventSource._configuredEntities` | `BuildEntitySet(_liveConfig.Get())` | ConfigChanged event from ILiveEntitiesConfig | Yes — rebuilt from current atomic reference on each swap | FLOWING |

### Behavioral Spot-Checks

Step 7b: SKIPPED — No live HA/MQTT/gRPC environment available in the automated pipeline. The unit test suite (221/221) covers the mechanisms end-to-end.

### Requirements Coverage

| Requirement | Source Plans | Description | Status | Evidence |
|-------------|-------------|-------------|--------|----------|
| UI-03 | 03-03 | UI assigns detectors with editable params | SATISFIED | `BuildDetectorEntry` renders HST/MAD/STL dropdowns + per-type param grids; pre-fills from entities.yaml; `DetectorEntryEndpointTests` and `EntityPickerPageTests` green |
| CFG-03 | 03-03 | Per-entity detector/param assignments persist; multiple detectors; sane defaults | SATISFIED | Extended save: `DetectorFieldParser.Parse` + HST default for empty list + YamlDotNet serialize; `SaveEndpointDetectorParsingTests` multi-detector round-trip passes |
| CFG-04 | 03-01, 03-02 | Config changes apply within seconds via reload, no restart | SATISFIED (mechanically) | Swap fires ConfigChanged → HaListenerWorker restarts pipeline → NetDaemonHaEventSource rebuilds entity filter for new entities. Full DI graph resolves (no raw `EntitiesConfig` in DI). Wall-clock latency and live-HA behavior require human verification. |

### Anti-Patterns Found

No blockers remain. The two previously-flagged blockers are resolved:

| File | Line | Previous Issue | Resolution |
|------|------|----------------|------------|
| `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` | 45-65 | `EntitiesConfig` ctor param; stale `_configuredEntities` never updated | FIXED in commit 363ca59: ctor now takes `ILiveEntitiesConfig`; filter rebuilt on `ConfigChanged` |
| `orchestrator/Argus.Orchestrator/Program.cs` | 26 | `AddSingleton<IHaEventSource>` required unregistered `EntitiesConfig` | FIXED: only `ILiveEntitiesConfig` registered; `NetDaemonHaEventSource` resolves cleanly |

### Human Verification Required

### 1. Detector disclosure pre-fill via Ingress

**Test:** Open the Argus "Open Web UI" in Home Assistant. Observe tracked entities in the entity picker.
**Expected:** Each tracked entity shows a `<details>` disclosure section "Detectors (N)" with the correct detector type selected and parameter values matching what is saved in `/data/entities.yaml`.
**Why human:** Requires a live HA Ingress session; cannot verify browser rendering programmatically.

### 2. Sub-2-second hot-reload without restart

**Test:** With at least one tracked entity in the UI, modify a detector parameter (e.g. HST window from 250 to 300), click Save. Observe the add-on log.
**Expected:** The success banner appears; the orchestrator log shows `ConfigReloadTriggered` and `ConfigReloadComplete` events within 2 seconds of the save; the add-on process does not restart (s6-supervise does not log a service restart).
**Why human:** Wall-clock latency (<2 s) and absence of process restart require a live HA OS environment.

### 3. Removed entity MQTT retraction within 30 seconds

**Test:** With at least two tracked entities, deselect one and save. Wait up to 30 seconds, then check the HA entity registry.
**Expected:** The `binary_sensor.argus_*` and `sensor.argus_*` entities corresponding to the removed entity disappear from HA (not just "unavailable" — actually gone) within 30 seconds.
**Why human:** Requires live HA with MQTT discovery to observe entity registry changes.

### Gaps Summary

No gaps remain. Both blockers from the initial verification are closed. All 10 truths are VERIFIED. The phase is gated on human verification of live-HA behaviors (sub-2-second reload wall-clock, MQTT entity retraction, and Ingress UI rendering) — these are not failures but runtime behaviors that cannot be confirmed without a deployed add-on.

---

_Verified: 2026-07-01T10:00:00Z_
_Verifier: Claude (gsd-verifier)_
