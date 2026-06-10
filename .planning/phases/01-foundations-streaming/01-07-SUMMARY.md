---
phase: 01-foundations-streaming
plan: "07"
subsystem: orchestrator
tags: [dotnet, mqtt, discovery, ha-entities, tdd, lwt, unique-id, polish-names]

requires:
  - "01-04: ConnectionSettings (MqttHost/Port/User/Password)"
  - "01-05: IHaEventSource (HaListenerWorker TODO(plan07) marker)"

provides:
  - "orchestrator/Mqtt/UniqueId.cs: Slug/AnomalyId/ScoreId — deterministic, no GUID (PITFALL 5, D-13, D-14)"
  - "orchestrator/Mqtt/FriendlyName.cs: ForAnomaly appends ' anomalia', preserves Polish chars (D-16)"
  - "orchestrator/Mqtt/DiscoveryPublisher.cs: BuildBinarySensorConfig/BuildSensorConfig JSON; PublishAllAsync with retain=true (MQTT-01/03/04)"
  - "orchestrator/Mqtt/MqttConnection.cs: MqttClientFactory; LWT in connect options before ConnectAsync (PITFALL 6); exponential backoff reconnect (D-15)"
  - "orchestrator/Mqtt/StatePublisher.cs: FlagTopic/ScoreTopic helpers; PublishFlagAsync/ScoreAsync/AvailabilityAsync (Plan 08 surface for RES-01)"
  - "orchestrator/Workers/MqttPublisherWorker.cs: BackgroundService; connect -> discovery -> per-entity online (MQTT-01/03)"
  - "orchestrator/Program.cs: MqttConnection + StatePublisher singletons + AddHostedService<MqttPublisherWorker>"

affects:
  - "01-08: StatePublisher.PublishFlagAsync/ScoreAsync/AvailabilityAsync are the publish surface for gRPC scoring results"

tech-stack:
  added: []
  patterns:
    - "TDD RED/GREEN cycle for both tasks"
    - "MqttClientFactory (NOT MqttFactory — MQTTnet v5 state of art)"
    - "LWT configured in MqttClientOptionsBuilder before ConnectAsync — PITFALL 6 mitigation"
    - "All-static DiscoveryPublisher (no DI instance needed)"
    - "StatePublisher.SetConnection() wired by worker after connect — decouples topic helpers from live connection"
    - "Bridge-level availability topic (argus/bridge/availability) — single LWT covers all entities on orchestrator crash"

key-files:
  created:
    - orchestrator/Argus.Orchestrator/Mqtt/UniqueId.cs
    - orchestrator/Argus.Orchestrator/Mqtt/FriendlyName.cs
    - orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs
    - orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs
    - orchestrator/Argus.Orchestrator/Mqtt/StatePublisher.cs
    - orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs
    - orchestrator/Argus.Orchestrator.Tests/UniqueIdTests.cs
    - orchestrator/Argus.Orchestrator.Tests/DiscoveryPayloadTests.cs
    - orchestrator/Argus.Orchestrator.Tests/MqttConnectionTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Program.cs (replaced TODO(plan06) with real registrations)
    - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs (added 4 MQTT event IDs)

key-decisions:
  - "DiscoveryPublisher uses all-static methods — no state needed, no DI registration required"
  - "StatePublisher.SetConnection() pattern: topic helpers work without a live connection (testable); SetConnection wired by worker post-connect"
  - "const BridgeAvailabilityTopic exposed on StatePublisher — test accesses via type name not instance (C# const rule)"
  - "MqttPublisherWorker removed DiscoveryPublisher from constructor — it calls static methods directly"

metrics:
  duration: "5min"
  completed: "2026-06-10"
  tasks: 2
  files_modified: 11
---

# Phase 01 Plan 07: MQTT Discovery + State Publishing Summary

**MQTTnet 5 client with LWT-before-connect (PITFALL 6), deterministic unique_ids (PITFALL 5), retained binary_sensor + sensor discovery payloads grouped under one HA device per source entity, Polish friendly-names, and idempotent re-publish**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-06-10
- **Completed:** 2026-06-10
- **Tasks:** 2
- **Files modified:** 11

## Accomplishments

- `UniqueId.cs`: `Slug(entityId)` = `entityId.Replace(".", "_")`; `AnomalyId(entityId, detector)` = `argus_{slug}_{detector}_anomaly`; `ScoreId` similarly. No GUID, no Random — deterministic across restarts (PITFALL 5, D-13)
- `FriendlyName.cs`: `ForAnomaly(friendlyName)` = `$"{friendlyName} anomalia"` (D-16). UTF-8/Polish characters preserved (`Zewnatrz temperatura anomalia` passes)
- `DiscoveryPublisher.cs`: all-static; `BuildBinarySensorConfig` + `BuildSensorConfig` produce JSON payloads with `unique_id == object_id` (D-14), `device_class = "problem"` on binary_sensor, `availability_topic = "argus/bridge/availability"` (bridge-level, all entities share one LWT), `payload_available = "online"` / `payload_not_available = "offline"`, device `identifiers = [slug]`, `name = "Argus {slug}"`, `model = "Argus Anomaly Detector"`, `manufacturer = "Argus"`. `PublishAllAsync` publishes both configs per entity with `retain=true`, QoS `AtLeastOnce`, to `homeassistant/binary_sensor/{id}/config` and `homeassistant/sensor/{id}/config`. Idempotency (MQTT-04) inherent: stable unique_id + retain; republish is safe
- `MqttConnection.cs`: `MqttClientFactory` (correct v5 API; NOT `MqttFactory` from v4); LWT configured in `MqttClientOptionsBuilder.WithWillTopic("argus/bridge/availability").WithWillPayload("offline").WithWillRetain(true).WithWillQualityOfServiceLevel(AtLeastOnce)` BEFORE any `ConnectAsync` call (PITFALL 6 mitigation); publishes `"online"` to bridge availability topic immediately after connect; exponential backoff with jitter in `DisconnectedAsync` handler (1s->2s->4s->...->60s max, Claude's Discretion)
- `StatePublisher.cs`: `FlagTopic(entityId)` = `argus/{slug}/flag/state`; `ScoreTopic(entityId)` = `argus/{slug}/score/state`; `BridgeAvailabilityTopic` const = `"argus/bridge/availability"`; `PublishFlagAsync/PublishScoreAsync/PublishAvailabilityAsync/PublishBridgeAvailabilityAsync` all via injected `MqttConnection` (Plan 08 integration surface for RES-01). Score formatted with `InvariantCulture`
- `MqttPublisherWorker.cs`: `BackgroundService`; calls `MqttConnection.ConnectAsync` (LWT already in options); wires `StatePublisher.SetConnection`; calls `DiscoveryPublisher.PublishAllAsync` for all configured entities; publishes initial per-entity availability `"online"`; structured logging (OBS-01)
- `Program.cs`: registers `MqttConnection` (singleton, LWT ctor), `StatePublisher` (singleton), `AddHostedService<MqttPublisherWorker>()`. Replaced `// TODO(plan06)` comment. Existing Plan 04/05 registrations preserved
- `LogEvents.cs`: added `MqttBridgeOnline` (4003), `MqttReconnecting` (4004), `MqttDiscoveryPublished` (4005), `MqttWorkerStarted` (4006)
- 22 new tests; 39/39 total pass; `dotnet build` exits 0 with 0 warnings

## Task Commits

1. **Task 1 RED: Failing UniqueId + DiscoveryPayload tests** — `746efa3` (test)
2. **Task 1 GREEN: UniqueId, FriendlyName, DiscoveryPublisher, MqttConnection** — `90487ea` (feat)
3. **Task 2 RED: Failing MqttConnection + StatePublisher tests** — `d9fe086` (test)
4. **Task 2 GREEN: StatePublisher, MqttPublisherWorker, Program.cs wiring** — `f196563` (feat)

## Files Created/Modified

- `orchestrator/Argus.Orchestrator/Mqtt/UniqueId.cs` — Slug/AnomalyId/ScoreId, no randomness
- `orchestrator/Argus.Orchestrator/Mqtt/FriendlyName.cs` — ForAnomaly, Polish-safe
- `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs` — Static JSON builder + PublishAllAsync
- `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs` — MqttClientFactory, LWT-before-connect, reconnect backoff
- `orchestrator/Argus.Orchestrator/Mqtt/StatePublisher.cs` — Topic helpers + publish surface for Plan 08
- `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs` — BackgroundService lifecycle
- `orchestrator/Argus.Orchestrator/Program.cs` — MQTT registrations (TODO(plan06) resolved)
- `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` — 4 new MQTT event IDs
- `orchestrator/Argus.Orchestrator.Tests/UniqueIdTests.cs` — 5 tests: slug, AnomalyId, ScoreId, determinism
- `orchestrator/Argus.Orchestrator.Tests/DiscoveryPayloadTests.cs` — 10 tests: payload shape, device, availability, Polish names
- `orchestrator/Argus.Orchestrator.Tests/MqttConnectionTests.cs` — 7 tests: LWT options, StatePublisher topics

## Decisions Made

- **DiscoveryPublisher all-static:** No instance state is needed (all inputs come from parameters). Removed DI registration. Worker calls static methods directly.
- **StatePublisher.SetConnection() pattern:** Allows topic helper unit tests without a live broker. Worker wires the live connection post-connect. Keeps test surface clean.
- **const BridgeAvailabilityTopic:** C# const accessed via type name; test fixed from instance access to type-qualified access (Rule 1 auto-fix during GREEN phase).
- **Bridge-level LWT vs per-entity:** Single `argus/bridge/availability` topic covers all entities — one orchestrator crash sets all sensors to `unavailable` via the shared `availability_topic` in every discovery payload.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] C# const accessed via instance in test**
- **Found during:** Task 2 GREEN (build error CS0176)
- **Issue:** Test file written during RED phase accessed `publisher.BridgeAvailabilityTopic` as instance member, but `BridgeAvailabilityTopic` is declared as `const string`. C# prohibits accessing const members via instance reference.
- **Fix:** Changed test assertion to `StatePublisher.BridgeAvailabilityTopic` (type-qualified access).
- **Files modified:** `MqttConnectionTests.cs`
- **Committed in:** f196563 (Task 2 GREEN)

**2. [Rule 1 - Bug] DiscoveryPublisher instance injected but methods are static**
- **Found during:** Task 2 GREEN (CS0176 error in MqttPublisherWorker)
- **Issue:** `MqttPublisherWorker` was written with `DiscoveryPublisher _discovery` field and calling `_discovery.PublishAllAsync(...)` but `PublishAllAsync` is static.
- **Fix:** Removed `DiscoveryPublisher` from constructor, call `DiscoveryPublisher.PublishAllAsync(...)` directly. Also removed `AddSingleton<DiscoveryPublisher>()` from Program.cs.
- **Files modified:** `MqttPublisherWorker.cs`, `Program.cs`
- **Committed in:** f196563 (Task 2 GREEN)

## Known Stubs

None — all plan goals delivered. StatePublisher is wired but `PublishFlagAsync`/`PublishScoreAsync` are only called by Plan 08 (no stub markers needed; the methods exist and are tested via topic helper assertions).

## Threat Flags

All STRIDE mitigations from the plan's threat model applied:
- T-07-01: `WithCredentials(user, password)` from `ConnectionSettings` env vars (CONF-03); credentials never logged
- T-07-02: Deterministic `unique_id + object_id` (no GUID, grep-gated by tests); retain=true; republish safe (MQTT-02/04, PITFALL 5)
- T-07-03: LWT `"offline"` configured in `MqttClientOptionsBuilder` before `ConnectAsync` (PITFALL 6); RES-01 unavailable path ready via `StatePublisher.PublishBridgeAvailabilityAsync`
- T-07-04: MQTT password only in `ConnectionSettings.MqttPassword` from env; `MqttConnection` passes it to `WithCredentials` only; no logger call includes the password

## TDD Gate Compliance

- RED gate commit: `746efa3` (test — failing UniqueId + DiscoveryPayload tests)
- RED gate commit: `d9fe086` (test — failing MqttConnection + StatePublisher tests)
- GREEN gate commit: `90487ea` (feat — UniqueId, FriendlyName, DiscoveryPublisher, MqttConnection)
- GREEN gate commit: `f196563` (feat — StatePublisher, MqttPublisherWorker, Program.cs)

## Self-Check: PASSED

- [x] `orchestrator/Argus.Orchestrator/Mqtt/UniqueId.cs` — exists, contains `argus_`, `Replace(".", "_")`, no `Guid`, no `Random`
- [x] `orchestrator/Argus.Orchestrator/Mqtt/FriendlyName.cs` — exists, contains `anomalia`
- [x] `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs` — exists, contains `homeassistant/binary_sensor/`, `homeassistant/sensor/`, `object_id`, `identifiers`, `device_class`
- [x] `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs` — exists, contains `MqttClientFactory`, `WithWillTopic`, `WithWillPayload`, `offline`; does NOT contain `new MqttFactory`
- [x] `orchestrator/Argus.Orchestrator/Mqtt/StatePublisher.cs` — exists, contains `/flag/state`, `/availability`
- [x] `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs` — exists, `BackgroundService` subclass
- [x] `orchestrator/Argus.Orchestrator/Program.cs` — exists, contains `AddHostedService<MqttPublisherWorker>`
- [x] `dotnet build orchestrator/Argus.Orchestrator.sln` — exit 0, 0 warnings, 0 errors
- [x] `dotnet test --filter "FullyQualifiedName~UniqueId|FullyQualifiedName~DiscoveryPayload"` — 15/15 pass
- [x] `dotnet test --filter "FullyQualifiedName~Mqtt"` — 7/7 pass
- [x] `dotnet test orchestrator/Argus.Orchestrator.sln` — 39/39 pass
- [x] Commits 746efa3, 90487ea, d9fe086, f196563 — verified in git log
