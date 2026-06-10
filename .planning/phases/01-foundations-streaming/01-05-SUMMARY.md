---
phase: 01-foundations-streaming
plan: "05"
subsystem: orchestrator
tags: [dotnet, netdaemon, websocket, ha-events, reconnect, tdd, filter, cooldown]

requires:
  - "01-04: HaListenerWorker stub, DetectionGateway, EntitiesConfig, ConnectionSettings"

provides:
  - "orchestrator/Ha/IHaEventSource.cs: IAsyncEnumerable<HaReading> ReadAllAsync(CancellationToken)"
  - "orchestrator/Ha/HaReading.cs: record with EntityId, Value, LastChanged, SuppressBinarySensor"
  - "orchestrator/Ha/ReconnectCooldown.cs: MarkReconnect(now)/IsSuppressed(now); 60s const (D-07)"
  - "orchestrator/Ha/NetDaemonHaEventSource.cs: NetDaemon.Client 23.46.0 WebSocket subscription + entity filter + reconnect backoff + get_states"
  - "orchestrator/Workers/HaListenerWorker.cs: real subscription loop via IHaEventSource; bounded Channel<HaReading>"
  - "orchestrator/Program.cs: AddHomeAssistantClient, ReconnectCooldown, IHaEventSource DI registrations"

affects:
  - "01-07: IHaEventSource.ReadAllAsync is the ingestion entry point for ScoreStream pipeline"

tech-stack:
  added:
    - "InternalsVisibleTo attribute (csproj AssemblyAttribute) — allows TryMap unit tests without making method public"
  patterns:
    - "TDD RED/GREEN cycle for ReconnectCooldown (4 tests) and HaEventFilter (4 tests)"
    - "Static internal TryMap method — testable filter/mapping without live HA"
    - "Bounded Channel<HaReading> with DropOldest — backpressure without blocking Rx callback"
    - "Exponential backoff loop: 1s→2s→4s→8s→...→60s cap (STRM-01)"
    - "D-07 get_states snapshot on reconnect + ReconnectCooldown.MarkReconnect suppresses binary_sensor 60s"

key-files:
  created:
    - orchestrator/Argus.Orchestrator/Ha/IHaEventSource.cs
    - orchestrator/Argus.Orchestrator/Ha/HaReading.cs
    - orchestrator/Argus.Orchestrator/Ha/ReconnectCooldown.cs
    - orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs
    - orchestrator/Argus.Orchestrator.Tests/ReconnectCooldownTests.cs
    - orchestrator/Argus.Orchestrator.Tests/HaEventFilterTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs (replaced stub body)
    - orchestrator/Argus.Orchestrator/Program.cs (added 3 DI registrations)
    - orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj (InternalsVisibleTo)

key-decisions:
  - "ToStateChangedEvent returns HassStateChangedEventData directly (not wrapped in a .Data property); corrected from initial assumption"
  - "InternalsVisibleTo via csproj AssemblyAttribute — preferred over AssemblyInfo.cs for SDK-style projects"
  - "TryMap takes DateTime (from HassState.LastChanged) and wraps in DateTimeOffset(dt, TimeSpan.Zero) — HassState.LastChanged has Kind=Utc from NetDaemon deserialization"
  - "Bounded channel DropOldest in HaListenerWorker — prevents blocking the Rx onNext callback if Plan 07 consumer is slow"

metrics:
  duration: "8min"
  completed: "2026-06-10"
  tasks: 3
  files_modified: 9
---

# Phase 01 Plan 05: HA WebSocket Ingestion Layer Summary

**NetDaemon.Client 23.46.0 HA event source with entity filter, exponential reconnect backoff (1s→60s), get_states snapshot on reconnect, 60s binary_sensor suppression cooldown (D-07), and a bounded channel-based HaListenerWorker**

## Performance

- **Duration:** ~8 min
- **Completed:** 2026-06-10
- **Tasks:** 3
- **Files modified:** 9

## Accomplishments

- `IHaEventSource.cs`: contract exported for Plan 07 (`IAsyncEnumerable<HaReading> ReadAllAsync(CancellationToken)`)
- `HaReading.cs`: record with `EntityId`, `Value`, `LastChanged`, `SuppressBinarySensor`
- `ReconnectCooldown.cs`: `MarkReconnect(DateTimeOffset now)` / `IsSuppressed(DateTimeOffset now)`; `SuppressionWindowSeconds = 60` const (D-07); deterministically testable by parameter injection
- `NetDaemonHaEventSource.cs`: full IHaEventSource implementation
  - `HashSet<string>` entity filter built from `EntitiesConfig` at construction time (O(1))
  - `TryMap(entityId, state, lastChanged, set, suppress, out reading)`: static internal; invariant-culture numeric parse; drops non-numeric (T-05-01); filter drop (T-05-01)
  - Connection loop: `IHomeAssistantClient.ConnectAsync(host, port, ssl, token, ct)` + `SubscribeToHomeAssistantEventsAsync("state_changed", ct)`
  - Reconnect: `RunConnectionLoopAsync` with 1s/2s/4s/.../60s exponential backoff (STRM-01, T-05-03)
  - On every reconnect (not first): `GetStatesAsync` snapshot (D-07, PITFALL 4) → `MarkReconnect`
  - `SuppressBinarySensor = cooldown.IsSuppressed(now)` on each yielded reading
  - HA token never logged (T-05-05); OBS-01 logs entity/value/suppress per reading
  - Bounded channel (1000, DropOldest) decouples Rx callback from async consumer
- `HaListenerWorker.cs`: real subscription loop — `WaitForHealthyAsync` gate (INFRA-07), `await foreach` over `IHaEventSource.ReadAllAsync`, push to `Channel<HaReading>`, `TODO(plan07)` marker
- `Program.cs`: added `AddHomeAssistantClient()`, `AddSingleton<ReconnectCooldown>()`, `AddSingleton<IHaEventSource, NetDaemonHaEventSource>()`
- 8 new tests pass (4 ReconnectCooldown + 4 HaEventFilter); 17/17 total tests pass; `dotnet build` exits 0

## Task Commits

1. **Task 1 RED: Failing ReconnectCooldown tests** — `86828dc` (test)
2. **Task 1 GREEN: IHaEventSource, HaReading, ReconnectCooldown** — `d5bbf89` (feat)
3. **Task 2 RED: Failing HaEventFilter tests** — `b49c342` (test)
4. **Task 2 GREEN: NetDaemonHaEventSource + InternalsVisibleTo** — `693fa0d` (feat)
5. **Task 3: HaListenerWorker + Program.cs DI** — `42067ef` (feat)

## Files Created/Modified

- `orchestrator/Argus.Orchestrator/Ha/IHaEventSource.cs` — exported contract for Plan 07
- `orchestrator/Argus.Orchestrator/Ha/HaReading.cs` — pipeline data model
- `orchestrator/Argus.Orchestrator/Ha/ReconnectCooldown.cs` — 60s suppression window (D-07)
- `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` — NetDaemon.Client 23.46.0 implementation
- `orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs` — replaced Plan 04 stub
- `orchestrator/Argus.Orchestrator/Program.cs` — 3 new DI registrations
- `orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj` — InternalsVisibleTo attribute
- `orchestrator/Argus.Orchestrator.Tests/ReconnectCooldownTests.cs` — 4 tests
- `orchestrator/Argus.Orchestrator.Tests/HaEventFilterTests.cs` — 4 tests

## Decisions Made

- **`ToStateChangedEvent` return type:** The extension method returns `HassStateChangedEventData` directly (not wrapped in a class with a `.Data` property). Initial code used `.Data?.NewState` which caused CS1061; corrected to `.NewState` directly. [Rule 1 auto-fix during GREEN phase]
- **`InternalsVisibleTo` via csproj `AssemblyAttribute`:** Preferred over `AssemblyInfo.cs` for SDK-style projects — generates the attribute at build time without a separate file.
- **`HassState.LastChanged` is `DateTime` not `DateTimeOffset`:** Wrapped with `new DateTimeOffset(dt, TimeSpan.Zero)` in `TryMap` since NetDaemon deserializes UTC timestamps as `DateTime.Kind=Utc`.
- **Bounded channel `DropOldest` in both source and worker:** Prevents backpressure from blocking the Rx `onNext` callback. DropOldest is the correct policy for sensor data (stale values are less valuable than recent ones).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] `HassStateChangedEventData.Data` property does not exist**
- **Found during:** Task 2 GREEN (first build attempt after writing NetDaemonHaEventSource.cs)
- **Issue:** Initial implementation accessed `stateChanged?.Data?.NewState`. `HassStateChangedEventData` has `NewState` directly as a top-level property; there is no `.Data` wrapper.
- **Fix:** Changed to `stateChanged?.NewState`
- **Files modified:** `NetDaemonHaEventSource.cs`
- **Committed in:** 693fa0d (Task 2 GREEN)

## Known Stubs

- `HaListenerWorker.cs:65` — `// TODO(plan07): forward reading to ScoreStream + frozen/hysteresis/MQTT pipeline`. Intentional: Plan 07 wires `Channel<HaReading>.Reader` into the gRPC ScoreStream write loop.

## Threat Flags

All STRIDE mitigations from the plan's threat model applied:
- T-05-01: `TryMap` validates numeric value via invariant-culture `double.TryParse`; non-numeric states dropped (acceptance-tested)
- T-05-03: Exponential backoff capped at `BackoffMaxSeconds = 60` prevents tight reconnect loop
- T-05-04: `GetStatesAsync` snapshot on reconnect (not burst replay) + `ReconnectCooldown.MarkReconnect` suppresses binary_sensor for 60s
- T-05-05: `HaToken` used only in `ConnectAsync` call; never passed to any logger

## Self-Check: PASSED

- [x] `orchestrator/Argus.Orchestrator/Ha/IHaEventSource.cs` — exists, contains `IAsyncEnumerable<HaReading>`
- [x] `orchestrator/Argus.Orchestrator/Ha/HaReading.cs` — exists, contains `SuppressBinarySensor`
- [x] `orchestrator/Argus.Orchestrator/Ha/ReconnectCooldown.cs` — exists, contains `60`, `MarkReconnect`, `IsSuppressed`
- [x] `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` — exists, contains `NetDaemon`, `GetStatesAsync`, `HashSet`, `MarkReconnect`, `BackoffMaxSeconds`
- [x] `orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs` — contains `IHaEventSource`, `ReadAllAsync`, `WaitForHealthyAsync`, `TODO(plan07)`
- [x] `orchestrator/Argus.Orchestrator/Program.cs` — contains `AddSingleton<IHaEventSource, NetDaemonHaEventSource>`, `AddHomeAssistantClient`, `ReconnectCooldown`
- [x] `dotnet build orchestrator/Argus.Orchestrator.sln` — exit 0, 0 warnings, 0 errors
- [x] `dotnet test --filter "FullyQualifiedName~ReconnectCooldown"` — 4/4 pass
- [x] `dotnet test --filter "FullyQualifiedName~HaEventFilter"` — 4/4 pass
- [x] `dotnet test orchestrator/Argus.Orchestrator.sln` — 17/17 pass
- [x] Commits 86828dc, d5bbf89, b49c342, 693fa0d, 42067ef — verified in git log
