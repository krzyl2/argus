---
phase: 03-config-readwrite-detector-assignment-reload
verified: 2026-07-01T00:00:00Z
status: gaps_found
score: 8/10 must-haves verified
overrides_applied: 0
gaps:
  - truth: "The three EntitiesConfig consumers (ScoreStreamPipeline, BatchSchedulerWorker, MqttPublisherWorker) read the LIVE config, not a ctor-captured stale reference"
    status: partial
    reason: "A fourth consumer — NetDaemonHaEventSource — was NOT migrated. It still takes raw EntitiesConfig in its ctor and builds a ctor-captured _configuredEntities HashSet. (1) EntitiesConfig is no longer registered in DI after Plan 02 removed AddSingleton(entitiesConfig), so the app crashes at startup with 'Unable to resolve service for type EntitiesConfig' when IHaEventSource is resolved for HaListenerWorker. (2) Even if the registration gap were patched by re-adding raw entitiesConfig to DI, the filter set is stale after any reload — state_changed events for newly-added entities will never reach the pipeline."
    artifacts:
      - path: "orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs"
        issue: "Ctor takes EntitiesConfig (line 45); builds _configuredEntities at construction (lines 58-60); never updated on ConfigChanged. Raw EntitiesConfig not in DI."
      - path: "orchestrator/Argus.Orchestrator/Program.cs"
        issue: "AddSingleton(entitiesConfig) was removed (Plan 02 intent); only AddSingleton<ILiveEntitiesConfig>(liveConfig) is registered. DI auto-resolution of NetDaemonHaEventSource fails at runtime."
    missing:
      - "Migrate NetDaemonHaEventSource ctor to accept ILiveEntitiesConfig liveConfig instead of EntitiesConfig entitiesConfig"
      - "Replace the ctor-captured _configuredEntities HashSet with a per-call read from _liveConfig.Get().Entities (or subscribe ConfigChanged to rebuild the HashSet on reload)"
      - "Update Program.cs AddSingleton<IHaEventSource> registration to resolve correctly (no change needed if ctor takes ILiveEntitiesConfig — DI auto-resolves from the already-registered singleton)"
      - "Update NetDaemonHaEventSourceTests to use MakeLive() wrapper (same pattern as other consumers)"
  - truth: "HaListenerWorker restarts the ScoreStreamPipeline.RunAsync loop on ConfigChanged by cancelling an inner CTS (not the host stoppingToken); MQTT + gRPC transport stay alive"
    status: partial
    reason: "The restart mechanism itself is correctly implemented and tested. However, because NetDaemonHaEventSource does not update its event filter on reload, new entities added via the UI will not have their state_changed events routed to the restarted pipeline — the restart is mechanically correct but functionally incomplete for the add-entities use case."
    artifacts:
      - path: "orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs"
        issue: "Stale _configuredEntities filter means restarted pipeline receives no readings for entities added since startup"
    missing:
      - "NetDaemonHaEventSource must rebuild its entity filter on ConfigChanged (or read live from ILiveEntitiesConfig per event); otherwise the restart loop cannot exercise the 'newly-added entities receive readings' path"
human_verification:
  - test: "Open the Argus UI via HA Ingress ('Open Web UI'), load the entity picker, and observe detector disclosure panels"
    expected: "For each tracked entity, a <details> section shows 'Detectors (N)' with the correct detector type selected and parameters pre-filled from entities.yaml"
    why_human: "Requires a live HA Ingress session; cannot be verified by static analysis"
  - test: "Assign a new detector type (e.g. MAD) with custom parameters to an entity, save, and observe the pipeline"
    expected: "The UI returns a success banner; the orchestrator log shows 'config reload triggered; restarting pipeline' within 2 seconds; no add-on restart occurs"
    why_human: "Wall-clock reload latency (<2 s) and absence of restart requires a live HA environment"
  - test: "Deselect an entity in the UI and save; observe HA entity registry within 30 seconds"
    expected: "The corresponding binary_sensor.argus_* and sensor.argus_* entities stop appearing as 'unavailable' and disappear from HA within 30 seconds (MQTT discovery retraction)"
    why_human: "Requires live HA with MQTT discovery to observe entity disappearance"
---

# Phase 3: Config Read/Write + Detector Assignment + Reload Verification Report

**Phase Goal:** The user reads current tracked-entity config in the UI, assigns one or more detectors (HST/MAD/STL) with editable parameters per entity, saves, and the running pipeline reloads within seconds without restarting the add-on. Removed entities have their MQTT discovery topics retracted. ILiveEntitiesConfig is the invasive cross-cutting change.
**Verified:** 2026-07-01T00:00:00Z
**Status:** gaps_found
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | A single volatile EntitiesConfig reference can be swapped atomically and readers always see the newest reference | VERIFIED | `LiveEntitiesConfig.cs`: volatile field + `Interlocked.Exchange(ref _current, newConfig)`. `LiveEntitiesConfigTests.cs` covers Get/Swap ordering + concurrency (500 iterations, no null, no exception). |
| 2 | Swapping the config fires ConfigChanged AFTER the reference is exchanged | VERIFIED | `LiveEntitiesConfig.Swap`: `ConfigChanged?.Invoke` follows `Interlocked.Exchange`. Test `Swap_FiresConfigChangedAfterExchange` asserts `Assert.Same(newConfig, capturedFromGet)`. |
| 3 | DiscoveryPublisher can publish empty retained payloads to a removed entity's binary_sensor + sensor config topics | VERIFIED | `DiscoveryPublisher.RetractAsync` iterates removed entities, publishes `string.Empty` retain=true to both topics. `MqttRetractionTests.cs` covers 2-per-entity, correct topics, empty payload, retain-true, empty-list publishes-nothing, non-removed not touched. |
| 4 | The three EntitiesConfig consumers read the LIVE config, not a ctor-captured stale reference | PARTIAL | `ScoreStreamPipeline.BuildEntityStates()` reads `_liveConfig.Get().Entities` at RunAsync entry (line 262). `BatchSchedulerWorker.RunBatchAsync` and `RunNightlyFitAsync` read `_liveConfig.Get().Entities` per cycle (lines 128, 217). `MqttPublisherWorker` reads `_liveConfig.Get().Entities` in startup and in ConfigChanged handler. **HOWEVER**: `NetDaemonHaEventSource` (the event source feeding the pipeline) still takes raw `EntitiesConfig` and builds a ctor-captured `_configuredEntities` filter that is never updated. `EntitiesConfig` is not registered in DI — startup will throw `InvalidOperationException: Unable to resolve service for type 'EntitiesConfig'`. |
| 5 | HaListenerWorker restarts the ScoreStreamPipeline.RunAsync loop on ConfigChanged via inner CTS; MQTT + gRPC stay alive | PARTIAL | `HaListenerWorker.ExecuteAsync` implements the inner-CTS restart loop (lines 83-149): ConfigChanged handler cancels `innerCts` (null-safe), loop re-enters `RunPipelineAsync`. `HaListenerWorkerReloadTests.cs` covers restart count, stoppingToken exit, retraction diff, no ObjectDisposedException under rapid events. The mechanism is correct. **HOWEVER**: because `NetDaemonHaEventSource._configuredEntities` is stale after a reload, new entities added via the UI will not have their HA state_changed events routed to the restarted pipeline — making the reload functionally incomplete for the add-entities case. |
| 6 | On reload, removed entities are retracted from MQTT and newly-added entities get re-published discovery | VERIFIED | `HaListenerWorker.RetractAndRepublishAsync` diffs old vs new entity sets (OrdinalIgnoreCase); calls `DiscoveryPublisher.RetractAsync` for removed, `DiscoveryPublisher.PublishAllAsync` for added, using `stoppingToken` (not innerCts) so broker calls survive the pipeline cancel. `MqttPublisherWorker` also fires-and-forgets republish on ConfigChanged. `HaListenerWorkerReloadTests.ConfigChanged_RetractsRemovedEntities` asserts correct topics. |
| 7 | Host shutdown (stoppingToken) propagates cleanly and exits the restart loop | VERIFIED | OCE catch filter: `when (innerCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)` — host shutdown OCE is not caught, propagates to ASP.NET shutdown. `HaListenerWorkerReloadTests.StoppingToken_ExitsLoop` asserts exactly 1 RunAsync call. |
| 8 | The config UI shows current per-entity detector assignments pre-filled from entities.yaml, falling back to type defaults | VERIFIED | `EntityPickerPage.BuildDetectorDisclosure` reads `entityConfig.Detectors` from the passed `EntitiesConfig`; falls back to `[new DetectorConfig { Name = "hst" }]` for empty/missing. `BuildDetectorEntry` pre-fills each param from `detector.Params` or type default. `Program.cs` GET /sensors passes `liveCfg.Get()` (line 241) and GET /api/sensors passes `liveCfg.Get()` (line 255). |
| 9 | Saving persists the submitted per-entity detector lists via YamlDotNet and triggers reload via Swap | VERIFIED | `Program.cs` POST /api/sensors/save: `DetectorFieldParser.Parse` parses indexed fields; empty list defaults to HST; `SerializerBuilder` serializes root dict; `ConfigWriter.WriteAsync`; then `EntitiesConfigLoader.Load` + `liveCfg.Swap(newConfig)` (line 386). `SaveEndpointDetectorParsingTests.SwapCalledAfterWrite_LiveConfigReflectsNewEntities` asserts Swap is called. |
| 10 | Config writes are serialized (SemaphoreSlim) and atomic (temp-then-rename) | VERIFIED | `ConfigWriter.WriteAsync` uses `SemaphoreSlim(1,1)` + `File.Move(tmp, targetPath, overwrite: true)` (atomic POSIX rename). |

**Score:** 8/10 truths verified (2 partial = gaps)

### Deferred Items

None — no truths in this phase are addressed by a later milestone phase.

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `orchestrator/Argus.Orchestrator/Config/LiveEntitiesConfig.cs` | ILiveEntitiesConfig + volatile-swap + ConfigChanged | VERIFIED | Interface + sealed impl with Interlocked.Exchange; ConfigChanged fired after exchange |
| `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs` | RetractAsync with empty retained payloads | VERIFIED | `RetractAsync(delegate, removedEntities, ct)` and `RetractAsync(MqttConnection, ...)` overloads, both publishing string.Empty retain=true |
| `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` | ConfigReloadTriggered, ConfigReloadComplete, MqttRetractionPublished | VERIFIED | Lines 51-53: IDs 7004-7006 |
| `orchestrator/Argus.Orchestrator.Tests/LiveEntitiesConfigTests.cs` | Swap/Get/ConfigChanged ordering + concurrency | VERIFIED | Substantive: 5 test methods covering all 4 required behaviors |
| `orchestrator/Argus.Orchestrator.Tests/MqttRetractionTests.cs` | Retract publishes empty retained payloads to correct topics | VERIFIED | 8 test methods; covers per-entity 2-publish, topics, empty payload, retain, empty-list, non-removed scope |
| `orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs` | Inner-CTS restart loop | VERIFIED | Full restart loop with null-before-dispose, OCE guard, retraction diff, virtual seams |
| `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs` | `_liveConfig.Get()` in BuildEntityStates | VERIFIED | Line 262: `foreach (var entity in _liveConfig.Get().Entities)` in BuildEntityStates |
| `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` | `_liveConfig.Get()` in RunBatchAsync + RunNightlyFitAsync | VERIFIED | Lines 128 + 217: per-cycle live reads |
| `orchestrator/Argus.Orchestrator/Program.cs` | `AddSingleton<ILiveEntitiesConfig>` + rewired consumers + GET /sensors live read | VERIFIED (with gap) | ILiveEntitiesConfig registered (line 26); all three target consumers rewired; GET /sensors + GET /api/sensors pass `liveCfg.Get()`. **GAP**: `NetDaemonHaEventSource` still requires raw `EntitiesConfig` which is unregistered — startup crash. |
| `orchestrator/Argus.Orchestrator.Tests/HaListenerWorkerReloadTests.cs` | ConfigChanged restarts RunAsync; stoppingToken exits loop | VERIFIED | 4 test methods; TestableHaListenerWorker virtual-seam pattern; covers restart, shutdown, retraction diff, rapid-event safety |
| `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs` | BuildDetectorEntry, disclosure rows, reloading banner | VERIFIED | BuildDetectorEntry (line 182), BuildDetectorDisclosure (line 356), BuildReloadingBanner (line 246); per-type param grids; HtmlEncode on name + params |
| `orchestrator/Argus.Orchestrator/Program.cs` | GET /api/detectors/new-entry + extended POST /api/sensors/save + Swap | VERIFIED | Lines 263-277 (new-entry endpoint); lines 281-405 (extended save with detector parse + Swap at line 386) |
| `orchestrator/Argus.Orchestrator/wwwroot/css/argus.css` | Phase 3 detector-assignment BEM blocks (existing tokens only) | VERIFIED | Phase-3 section at line 440; 14 BEM blocks; no new `--` custom properties |
| `orchestrator/Argus.Orchestrator.Tests/SaveEndpointDetectorParsingTests.cs` | Indexed detector form parsing + empty-list HST + YAML round-trip | VERIFIED | 12 test methods covering parser, correlation, round-trip, overflow safety, Swap invocation |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `LiveEntitiesConfig.Swap` | `ConfigChanged event` | `ConfigChanged?.Invoke` after Interlocked.Exchange | WIRED | Line 49: `ConfigChanged?.Invoke(this, EventArgs.Empty)` |
| `DiscoveryPublisher.RetractAsync` | `MqttConnection.PublishAsync` | empty retained payload to config topics | WIRED | Lines 169-188: iterates removed entities, publishes `string.Empty` retain=true |
| `LiveEntitiesConfig.ConfigChanged` | `HaListenerWorker inner CTS cancel` | event subscription cancels innerCts | WIRED | Line 95: `_liveConfig.ConfigChanged += OnConfigChanged`; handler cancels `innerCts` (lines 89-94) |
| `HaListenerWorker reload path` | `DiscoveryPublisher.RetractAsync` | diff old vs new entity ids before restart | WIRED | Lines 212-219: `DiscoveryPublisher.RetractAsync(delegate, removed, stoppingToken)` |
| `ScoreStreamPipeline.BuildEntityStates` | `ILiveEntitiesConfig.Get().Entities` | live read at RunAsync entry | WIRED | Line 262: `foreach (var entity in _liveConfig.Get().Entities)` |
| `POST /api/sensors/save` | `ILiveEntitiesConfig.Swap` | ConfigWriter.WriteAsync then re-load + Swap | WIRED | Line 386: `liveCfg.Swap(newConfig)` after `ConfigWriter.WriteAsync` |
| `EntityPickerPage detector rows` | `ILiveEntitiesConfig.Get() config` | BuildFullPage reads Detectors + Params | WIRED | `BuildDetectorDisclosure` reads from passed `config` (which is `liveCfg.Get()` from Program.cs) |
| `GET /api/detectors/new-entry` | `EntityPickerPage.BuildDetectorEntry` | htmx fragment with HST defaults | WIRED | Lines 273-276: `EntityPickerPage.BuildDetectorEntry(ei, dj, new DetectorConfig { Name = "hst" })` |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|--------------------|--------|
| `EntityPickerPage` detector disclosure | `config.Entities[*].Detectors` | `liveCfg.Get()` passed from `GET /sensors` | Yes — reads from deserialized entities.yaml via EntitiesConfigLoader | FLOWING |
| `ScoreStreamPipeline.BuildEntityStates` | `_liveConfig.Get().Entities` | `ILiveEntitiesConfig` singleton (post-Swap) | Yes — reads current atomic reference | FLOWING |
| `BatchSchedulerWorker.RunBatchAsync` | `_liveConfig.Get().Entities` | Per-cycle live read | Yes — reads current atomic reference per tick | FLOWING |
| `MqttPublisherWorker` ConfigChanged republish | `_liveConfig.Get().Entities` | ILiveEntitiesConfig.ConfigChanged | Yes — reads post-Swap reference | FLOWING |

### Behavioral Spot-Checks

Step 7b: SKIPPED — No live HA/MQTT/gRPC environment available in automated pipeline. The unit test suite (218/218, per orchestrator notes) covers the mechanisms.

### Requirements Coverage

| Requirement | Source Plans | Description | Status | Evidence |
|-------------|-------------|-------------|--------|----------|
| UI-03 | 03-03 | UI assigns detectors with editable params | SATISFIED | `BuildDetectorEntry` renders HST/MAD/STL dropdowns + per-type param grids; pre-fills from entities.yaml; `DetectorEntryEndpointTests` and `EntityPickerPageTests` green |
| CFG-03 | 03-03 | Per-entity detector/param assignments persist; multiple detectors; sane defaults | SATISFIED | Extended save: `DetectorFieldParser.Parse` + HST default for empty list + `YamlDotNet` serialize; `SaveEndpointDetectorParsingTests` multi-detector round-trip passes |
| CFG-04 | 03-01, 03-02 | Config changes apply within seconds via reload, no restart | PARTIAL | Mechanism is implemented: Swap fires ConfigChanged → HaListenerWorker restarts pipeline. BLOCKED in production by `NetDaemonHaEventSource` DI startup crash (unregistered `EntitiesConfig`). Even after patching DI, event filter is stale for new entities. |

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` | 45, 58-60 | `EntitiesConfig entitiesConfig` ctor param; `_configuredEntities` HashSet built at construction; never updated on ConfigChanged | BLOCKER | Runtime DI crash on startup (unregistered type); stale event filter prevents new entities from receiving pipeline readings after a reload |
| `orchestrator/Argus.Orchestrator/Program.cs` | 92 | `AddSingleton<IHaEventSource, NetDaemonHaEventSource>()` — DI auto-resolution requires `EntitiesConfig` which is not registered | BLOCKER | App will throw `InvalidOperationException` at startup when `IHaEventSource` is first resolved |

### Human Verification Required

### 1. Detector disclosure pre-fill via Ingress

**Test:** Open the Argus "Open Web UI" in Home Assistant. Observe tracked entities in the entity picker.
**Expected:** Each tracked entity shows a `<details>` disclosure section "Detectors (N)" with the correct detector type selected and parameter values matching what is saved in `/data/entities.yaml`.
**Why human:** Requires a live HA Ingress session; cannot verify the browser rendering pipeline programmatically.

### 2. Sub-2-second hot-reload without restart

**Test:** With at least one tracked entity in the UI, modify a detector parameter (e.g. HST window from 250 to 300), click Save. Observe the add-on log.
**Expected:** The success banner appears; the orchestrator log shows `ConfigReloadTriggered` and `ConfigReloadComplete` events within 2 seconds of the save; the add-on process does not restart (s6-supervise does not log a service restart).
**Why human:** Wall-clock latency (<2 s) and absence of process restart require a live HA OS environment.

### 3. Removed entity MQTT retraction within 30 seconds

**Test:** With at least two tracked entities, deselect one and save. Wait up to 30 seconds, then check the HA entity registry.
**Expected:** The `binary_sensor.argus_*` and `sensor.argus_*` entities corresponding to the removed entity disappear from HA (not just "unavailable" — actually gone) within 30 seconds.
**Why human:** Requires live HA with MQTT discovery to observe entity registry changes.

### Gaps Summary

**2 gaps** blocking complete goal achievement, both rooted in the same missed migration:

**Root cause: `NetDaemonHaEventSource` was not included in the ILiveEntitiesConfig consumer migration.**

The plans (03-01, 03-02) defined three target consumers for migration (ScoreStreamPipeline, BatchSchedulerWorker, MqttPublisherWorker). All three were migrated correctly and are fully wired. However, `NetDaemonHaEventSource` — the fourth consumer of `EntitiesConfig` — was not migrated. This causes two cascading issues:

1. **Runtime DI crash (startup blocker):** Plan 02 removed `AddSingleton(entitiesConfig)` (the raw `EntitiesConfig` registration). `NetDaemonHaEventSource` still requires `EntitiesConfig` as a ctor parameter via DI auto-resolution. .NET DI will throw `InvalidOperationException: Unable to resolve service for type 'Argus.Orchestrator.Config.EntitiesConfig'` at startup when `HaListenerWorker` (a hosted service) is resolved. The app cannot start.

2. **Stale event filter (CFG-04 functional gap):** Even if the DI crash were patched by re-adding `entitiesConfig` to DI, `NetDaemonHaEventSource._configuredEntities` is built at construction from the initial config and never updated. After a hot-reload that adds new entities, `state_changed` events for the new entities will not pass the filter and will never reach the restarted `ScoreStreamPipeline`. The "live" reload only works for parameter changes on existing entities; adding new entities via the UI cannot work end-to-end without this fix.

**Fix:** Migrate `NetDaemonHaEventSource` to accept `ILiveEntitiesConfig` instead of `EntitiesConfig`, and read the entity filter set dynamically on each event (or subscribe to `ConfigChanged` to rebuild the HashSet). This is the same migration pattern applied to the three other consumers.

---

_Verified: 2026-07-01T00:00:00Z_
_Verifier: Claude (gsd-verifier)_
