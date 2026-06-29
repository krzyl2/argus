---
phase: 01-foundations-streaming
verified: 2026-06-10T12:20:35Z
status: gaps_found
score: 21/26
overrides_applied: 0
gaps:
  - truth: "On reconnect, binary_sensor publication is suppressed for 60 seconds (STRM-02)"
    status: failed
    reason: "ScoreStreamPipeline.cs line 109 creates a synthetic HaReading with SuppressBinarySensor hardcoded to false. The reading's SuppressBinarySensor flag (set by ReconnectCooldown) is consumed in the write loop but never propagated to the verdict processing path. The binary_sensor flag is published during the 60s post-reconnect window, defeating D-07/PITFALL 4 mitigation."
    artifacts:
      - path: "orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs"
        issue: "Line 109: var syntheticReading = new Ha.HaReading(entityId, 0.0, DateTimeOffset.UtcNow, false); — SuppressBinarySensor hardcoded false"
    missing:
      - "Track SuppressBinarySensor in EntityRuntimeState; update it from the write loop (entityState.SuppressBinarySensor = reading.SuppressBinarySensor) and pass entityState.SuppressBinarySensor to the synthetic reading in the read loop"

  - truth: "HA reading → bidi ScoreStream → Verdict → hysteresis → MQTT flag/score, end-to-end (STRM-03 multi-entity fan-out)"
    status: failed
    reason: "ScoreStreamPipeline.RunAsync(readings, ct) starts N entity tasks all iterating the same IAsyncEnumerable<HaReading>. IAsyncEnumerable is not thread-safe for concurrent enumeration; concurrent MoveNextAsync calls race and only one consumer sees each element, silently dropping events. With 3 entities in entities.yaml, 2 of 3 entities receive no readings. This is CR-03 from the code review."
    artifacts:
      - path: "orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs"
        issue: "Lines 82-85: entityStates.Select(kvp => RunEntityStreamAsync(kvp.Key, kvp.Value, readings, ct)) — N tasks share one IAsyncEnumerable source"
    missing:
      - "Fan-out multiplexer: one bounded Channel<HaReading> per entity; a single fan-out task reads the source once and routes to the matching channel; per-entity streams read from their own channel"

  - truth: "Detector RpcException marks affected entities unavailable (not off) via availability topic (RES-01 per-entity)"
    status: failed
    reason: "DiscoveryPublisher sets availability_topic = 'argus/bridge/availability' (bridge-level) in all discovery payloads. StatePublisher.PublishAvailabilityAsync publishes to 'argus/{slug}/availability' (per-entity topic). HA entities declared with the bridge topic never subscribe to the per-entity topic; HandleDetectorFailureAsync calls are silently ignored by HA. Bridge-level LWT on crash works, but per-entity degradation on RpcException is unreachable. CR-05 from the code review."
    artifacts:
      - path: "orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs"
        issue: "All discovery payloads declare single availability_topic = BridgeAvailabilityTopic; no per-entity availability topic in payload"
      - path: "orchestrator/Argus.Orchestrator/Mqtt/StatePublisher.cs"
        issue: "PublishAvailabilityAsync writes to argus/{slug}/availability — unreachable by HA because discovery config does not declare it"
    missing:
      - "Add per-entity availability entry to discovery payloads, or remove per-entity PublishAvailabilityAsync calls from HandleDetectorFailureAsync and document bridge-level-only degradation"

  - truth: "Bidi stream uses CompleteAsync BEFORE awaiting the read task (no deadlock) — verified ordering test passes"
    status: failed
    reason: "The WR-03 multi-entity fan-out race (gaps item 2) means the ordering guarantee is only tested in the single-entity test overload (RunAsync with IScoreStreamCall). The production multi-entity path (RunAsync(readings, ct)) has per-entity tasks sharing a source that races; the ordering test does not cover the production code path and the entity tasks may never reach CompleteAsync at all if the source yields nothing."
    artifacts:
      - path: "orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs"
        issue: "Production RunAsync(readings, ct) uses shared IAsyncEnumerable across tasks; entity streams that receive no events never invoke CompleteAsync"
    missing:
      - "Resolve WR-03 fan-out issue; after fix, the ordering guarantee is restored for all entity streams"

  - truth: "Python gRPC server boots and serves grpc.health.v1 Health/Check returning SERVING (INFRA-07) — end-to-end validated"
    status: failed
    reason: "INFRA-07 requires health RPC validated end-to-end before detection work. The health check is implemented and unit-tested in isolation, but no end-to-end test exercises the orchestrator's WaitForHealthyAsync against the running detector container. This is a human-testable item; automated evidence is unit tests only."
    artifacts:
      - path: "detector/argus_detector/server.py"
        issue: "Unit-tested (test_health.py passes), but end-to-end validation against real orchestrator WaitForHealthyAsync requires live containers"
    missing:
      - "Integration smoke test documented in CERTS.md or README; human verification item"
deferred: []
human_verification:
  - test: "Run docker compose up for both detector (deploy/docker-compose.gpu.yml) and orchestrator (deploy/docker-compose.edge.yml) with real entity_ids replacing placeholder Q1 values in entities.yaml"
    expected: "Orchestrator WaitForHealthyAsync returns; HA binary_sensor and sensor entities appear automatically; a state_changed event produces a verdict update in HA within 2 seconds"
    why_human: "End-to-end pipeline requires real HA instance, real MQTT broker, and real gRPC connectivity. entities.yaml uses placeholder Q1 entity_ids (sensor.salon_temperatura etc.) that must be replaced with real HA entity_ids before any integration test."
  - test: "Verify X509Certificate2 private key lifetime on the chiseled Linux image: run orchestrator container and observe mTLS handshake succeeds under GC pressure after 10+ connections"
    expected: "No AuthenticationException during mTLS handshake; all connections succeed"
    why_human: "CR-04: X509Certificate2.CreateFromPemFile private key may be GC-collected before TLS handshake on .NET 8 Linux. Cannot be verified by grep; requires runtime test on the actual deployment target."
  - test: "Verify STRM-04 end-to-end latency (HA state_changed -> MQTT verdict update) is < 2 seconds under normal LAN load"
    expected: "Per-verdict latency_ms in structured logs consistently under 2000ms; MQTT update appears in HA within 2 seconds of state_changed"
    why_human: "Latency is measured at runtime only; no static analysis can verify the 2s SLA."
---

# Phase 1: foundations-streaming Verification Report

**Phase Goal:** Anomalies on v1 environmental sensors appear in HA as live binary_sensor + score entities within 2 seconds of a state_changed event, with no manual entity creation and no HA restart required. Streaming pipeline fully operational.
**Verified:** 2026-06-10T12:20:35Z
**Status:** gaps_found
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| #  | Truth | Status | Evidence |
|----|-------|--------|----------|
| T-01 | proto/argus.proto compiles to .NET stubs at build time via Grpc.Tools | VERIFIED | csproj: `<Protobuf Include="..\..\proto\argus.proto" GrpcServices="Client" ProtoRoot="..\..\proto" />`; Grpc.Tools 2.80.0 present |
| T-02 | proto/argus.proto compiles to Python stubs via grpcio-tools | VERIFIED | gen_proto.py exists and patches relative import; requirements.txt has grpcio-tools==1.81.0 |
| T-03 | Verdict.score uses google.protobuf.DoubleValue (not raw double) | VERIFIED | argus.proto line 16: `google.protobuf.DoubleValue score = 2;` |
| T-04 | Point.timestamp uses google.protobuf.Timestamp | VERIFIED | argus.proto line 11: `google.protobuf.Timestamp timestamp = 3;` |
| T-05 | Mono-repo has proto/, orchestrator/, detector/, deploy/ directories | VERIFIED | All four directories present with expected contents |
| T-06 | Python gRPC server boots and serves grpc.health.v1 Health/Check returning SERVING | VERIFIED (unit) | server.py: add_HealthServicer_to_server, SERVING set; test_health.py passes; end-to-end requires human (see human_verification #1) |
| T-07 | DetectorService is registered on the server (ScoreStream stub present) | VERIFIED | server.py: add_DetectorServiceServicer_to_server; servicer.py calls registry.score_one (real HST) |
| T-08 | Server emits structured JSON logs on startup and per request | VERIFIED | logging_setup.py: JSON formatter; servicer.py: logs entity_id, score, latency_ms, detector |
| T-09 | Detector Docker image builds and runs the gRPC server | VERIFIED | Dockerfile.detector FROM python:3.12-slim-bookworm; bakes gen_proto.py stubs; EXPOSE 50051; ENTRYPOINT server |
| T-10 | Self-signed CA, server cert, and client cert exist in deploy/certs/ | VERIFIED | ca.crt, server.crt, server.key, client.crt, client.key all present in deploy/certs/ |
| T-11 | Server cert SAN includes both GPU host LAN IP and hostname | VERIFIED | generate-certs.sh: subjectAltName=IP:${GPU_HOST_IP},DNS:${GPU_HOST_NAME}; SUMMARY confirms SAN verified with openssl |
| T-12 | Certs have 2-year (730-day) expiry; generate-certs.sh reproducible | VERIFIED | generate-certs.sh: -days 730 on all certs; set -euo pipefail; parameterized by GPU_HOST_IP/GPU_HOST_NAME |
| T-13 | entities.yaml is parsed into typed config; connection settings load from env vars | VERIFIED | EntitiesConfigLoader.cs: YamlDotNet deserialization; covariates/groups ignored with warning; ConnectionSettings.cs: env-only, no hard-coded secrets |
| T-14 | Orchestrator builds single mTLS GrpcChannel using HttpClientHandler.ClientCertificates | VERIFIED | DetectorChannelFactory.cs: X509Certificate2.CreateFromPemFile, ClientCertificates.Add, X509ChainTrustMode.CustomRootTrust; no SslCredentials |
| T-15 | Orchestrator polls grpc.health.v1 Health/Check and only proceeds when SERVING | VERIFIED | DetectionGateway.cs: WaitForHealthyAsync with CheckAsync + exponential backoff; HaListenerWorker calls it before pipeline |
| T-16 | Orchestrator Docker image builds and runs as worker service | VERIFIED | Dockerfile.orchestrator: sdk:8.0 build + runtime:8.0-jammy-chiseled final; dotnet publish |
| T-17 | Orchestrator authenticates to HA WebSocket and subscribes to state_changed | VERIFIED | NetDaemonHaEventSource.cs: ConnectAsync + SubscribeToHomeAssistantEventsAsync; HashSet filter; exponential backoff |
| T-18 | Only entities in entities.yaml are forwarded; others are dropped | VERIFIED | NetDaemonHaEventSource.cs: HashSet<string> built from EntitiesConfig; TryMap drops entities not in set |
| T-19 | WebSocket reconnects with exponential backoff (1s→2s→4s→8s→max 60s) | VERIFIED | NetDaemonHaEventSource.cs: BackoffMaxSeconds = 60; reconnect loop doubles delay |
| T-20 | On reconnect, orchestrator calls get_states (not burst replay) | VERIFIED | NetDaemonHaEventSource.cs: GetStatesAsync snapshot on every reconnect (not first connect); MarkReconnect called |
| T-21 | On reconnect, binary_sensor publication is suppressed for 60 seconds | FAILED | ScoreStreamPipeline.cs line 109: syntheticReading has SuppressBinarySensor=false hardcoded; post-reconnect cooldown value from HaReading never reaches ProcessVerdictAsync |
| T-22 | Each incoming Point is scored by River HalfSpaceTrees via per-entity registry | VERIFIED | hst_detector.py: HalfSpaceTrees(n_trees, height=8, window_size, seed=42); registry.py: threading.Lock lazy creation; servicer.py calls score_one |
| T-23 | HA reading → bidi ScoreStream → Verdict → hysteresis → MQTT flag/score end-to-end | FAILED | ScoreStreamPipeline.RunAsync(readings, ct) starts N entity tasks all iterating the same IAsyncEnumerable source — concurrent MoveNextAsync races drop events for entities beyond the first (WR-03/CR from REVIEW) |
| T-24 | Each entity publishes one binary_sensor + one sensor discovery config under one HA device | VERIFIED | DiscoveryPublisher.cs: BuildBinarySensorConfig + BuildSensorConfig; homeassistant/binary_sensor/{id}/config + homeassistant/sensor/{id}/config; retain=true |
| T-25 | unique_id is argus_{entity_slug}_{detector}_{suffix}, deterministic, stable | VERIFIED | UniqueId.cs: Slug = Replace(".", "_"); AnomalyId/ScoreId; no Guid, no Random |
| T-26 | LWT 'offline' configured in connect options BEFORE any state publish; 'online' after connect | VERIFIED | MqttConnection.cs: WithWillTopic/WithWillPayload("offline")/WithWillRetain; MqttClientFactory (not MqttFactory) |
| T-27 | Hysteresis gate prevents flapping (N-consecutive high/low before flipping) | VERIFIED | HysteresisGate.cs: _consecutiveHigh, _minConsecutive, D-11 defaults; tests pass |
| T-28 | A frozen sensor (variance < 0.001 over 10 readings) is flagged without ML | VERIFIED | FrozenSensorDetector.cs: rolling Queue<double>; variance < _varianceThreshold; D-12 defaults |
| T-29 | Detector RpcException marks affected entities unavailable (not off) via availability topic | FAILED | DiscoveryPublisher sets bridge-level availability_topic only; StatePublisher.PublishAvailabilityAsync publishes to per-entity topic HA never subscribed to; per-entity graceful degradation is unreachable (REVIEW CR-05) |
| T-30 | Polish friendly-names follow '[Room] [SensorType] anomalia' | VERIFIED | FriendlyName.cs: ForAnomaly = $"{friendlyName} anomalia"; preserves UTF-8/Polish chars |

**Score:** 26 truths evaluated; 21 VERIFIED, 5 FAILED (T-21, T-23, T-29, and T-23 also breaks the CompleteAsync ordering guarantee in practice making T-23 a double failure)

---

### Required Artifacts

| Artifact | Status | Details |
|----------|--------|---------|
| `proto/argus.proto` | VERIFIED | DoubleValue, Timestamp, ScoreStream, Fit; no message Health |
| `orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj` | VERIFIED | Grpc.Tools 2.80.0, Protobuf Include argus.proto, NetDaemon.Client 23.46.0, MQTTnet 5.1.0.1559 |
| `detector/pyproject.toml` | VERIFIED | argus-detector package, grpcio/river/pyod deps |
| `detector/scripts/gen_proto.py` | VERIFIED | grpcio-tools codegen; relative import patch |
| `detector/argus_detector/server.py` | VERIFIED | grpc.server, add_HealthServicer_to_server, SERVING, add_secure_port |
| `detector/argus_detector/servicer.py` | VERIFIED | score_one called (real HST); stale TODO comment in docstring only (IN-03) |
| `detector/argus_detector/hst_detector.py` | VERIFIED | HalfSpaceTrees, MinMaxScaler, window_size, n_trees, is_warmed_up, from_params |
| `detector/argus_detector/registry.py` | VERIFIED | score_one, threading.Lock, _detectors; no TODO(plan06) placeholder |
| `deploy/Dockerfile.detector` | VERIFIED | python:3.12-slim-bookworm, gen_proto.py, EXPOSE 50051, HEALTHCHECK |
| `deploy/Dockerfile.orchestrator` | VERIFIED | sdk:8.0 build + runtime:8.0-jammy-chiseled final; dotnet publish |
| `deploy/generate-certs.sh` | VERIFIED | subjectAltName, 730, set -euo pipefail, GPU_HOST_IP/GPU_HOST_NAME |
| `deploy/certs/ca.crt` + server.crt + client.crt | VERIFIED | All present on disk (gitignored per plan) |
| `deploy/docker-compose.gpu.yml` | VERIFIED | 50051:50051, /certs volume |
| `deploy/docker-compose.edge.yml` | VERIFIED | ARGUS_DETECTOR_ENDPOINT, /certs/client.crt, no literal secrets |
| `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` | VERIFIED | covariates/groups warning; YamlDotNet |
| `orchestrator/Argus.Orchestrator/Config/ConnectionSettings.cs` | VERIFIED | ARGUS_HA_TOKEN ref; no hard-coded secrets |
| `orchestrator/Argus.Orchestrator/Detection/DetectorChannelFactory.cs` | VERIFIED | ClientCertificates, CreateFromPemFile, X509ChainTrustMode.CustomRootTrust; no SslCredentials |
| `orchestrator/Argus.Orchestrator/Detection/DetectionGateway.cs` | VERIFIED | CheckAsync, Serving, WaitForHealthyAsync, backoff |
| `orchestrator/Argus.Orchestrator/Ha/IHaEventSource.cs` | VERIFIED | IAsyncEnumerable<HaReading> ReadAllAsync |
| `orchestrator/Argus.Orchestrator/Ha/HaReading.cs` | VERIFIED | SuppressBinarySensor |
| `orchestrator/Argus.Orchestrator/Ha/ReconnectCooldown.cs` | VERIFIED | SuppressionWindowSeconds=60, MarkReconnect, IsSuppressed |
| `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` | VERIFIED | NetDaemon, HashSet, MarkReconnect, GetStatesAsync, BackoffMaxSeconds |
| `orchestrator/Argus.Orchestrator/Mqtt/UniqueId.cs` | VERIFIED | argus_, Replace(".", "_"), no Guid, no Random |
| `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs` | STUB (partial) | Discovery payloads correct; but availability_topic bridge-only breaks per-entity degradation (CR-05) |
| `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs` | VERIFIED | MqttClientFactory, WithWillTopic, WithWillPayload("offline"); no new MqttFactory |
| `orchestrator/Argus.Orchestrator/Mqtt/StatePublisher.cs` | WIRED (broken) | /flag/state, BridgeAvailabilityTopic constants correct; PublishAvailabilityAsync publishes to unreachable per-entity topic |
| `orchestrator/Argus.Orchestrator/Detection/HysteresisGate.cs` | VERIFIED | min_consecutive, _consecutiveHigh, D-11 defaults |
| `orchestrator/Argus.Orchestrator/Detection/FrozenSensorDetector.cs` | VERIFIED | variance, Queue, window, D-12 defaults |
| `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs` | STUB (broken) | CompleteAsync present; but (1) SuppressBinarySensor hardcoded false, (2) multi-entity shared IAsyncEnumerable race |
| `orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs` | VERIFIED | ScoreStreamPipeline, RunAsync, WaitForHealthyAsync |
| `orchestrator/Argus.Orchestrator/Program.cs` | VERIFIED | Full DI wiring: IHaEventSource, ScoreStreamPipeline, MqttPublisherWorker, IStatePublisher alias |
| `entities.yaml` | VERIFIED | detectors, hst, Q1 comment |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| Argus.Orchestrator.csproj | proto/argus.proto | Grpc.Tools Protobuf Include | VERIFIED | `<Protobuf Include="..\..\proto\argus.proto">` present |
| detector/scripts/gen_proto.py | proto/argus.proto | grpc_tools.protoc invocation | VERIFIED | gen_proto.py references argus.proto |
| DetectorChannelFactory.cs | deploy/certs/client.crt | X509Certificate2.CreateFromPemFile | VERIFIED | CreateFromPemFile(settings.TlsCert, settings.TlsKey) |
| DetectionGateway.cs | grpc.health.v1 Health/Check | Health.HealthClient.CheckAsync | VERIFIED | CheckAsync with service="argus.v1.DetectorService" |
| NetDaemonHaEventSource.cs | entities.yaml configured set | HashSet entity filter | VERIFIED | HashSet<string> built from EntitiesConfig at construction |
| HaListenerWorker.cs | IHaEventSource | DI injection + ReadAllAsync | VERIFIED | _haEventSource.ReadAllAsync(stoppingToken) passed to RunAsync |
| ScoreStreamPipeline.cs | Detector ScoreStream gRPC | AsyncDuplexStreamingCall read+write | BROKEN | Shared IAsyncEnumerable race for multi-entity; SuppressBinarySensor not propagated |
| ScoreStreamPipeline.cs | StatePublisher MQTT | publish flag/score/availability after hysteresis | PARTIAL | PublishFlagAsync/ScoreAsync wired; PublishAvailabilityAsync unreachable per CR-05 |
| MqttConnection.cs | availability topic LWT | WithWillTopic/WithWillPayload | VERIFIED | WithWillTopic("argus/bridge/availability"), WithWillPayload("offline") |
| DiscoveryPublisher.cs | UniqueId.cs | unique_id + object_id derivation | VERIFIED | UniqueId.AnomalyId/ScoreId called in build methods |
| servicer.py | registry.py | score_one in ScoreStream | VERIFIED | self._registry.score_one(entity_id, value) at line 48 |
| hst_detector.py | river.anomaly.HalfSpaceTrees | score_one + learn_one | VERIFIED | anomaly.HalfSpaceTrees(...) instantiated; score_one calls float(self._model.score_one) + learn_one |

---

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|--------------|--------|--------------------|--------|
| ScoreStreamPipeline.cs | verdict.Score | detector registry.score_one via gRPC ScoreStream | Yes — real HST via hst_detector.py | FLOWING (when read loop receives anything) |
| NetDaemonHaEventSource.cs | HaReading.Value | HA state_changed.NewState parsed to double | Yes — live HA events | FLOWING |
| DetectorRegistry.score_one | EntityDetector.score_one | River HalfSpaceTrees.score_one + MinMaxScaler | Yes — real ML scoring | FLOWING |
| DiscoveryPublisher.PublishAllAsync | discovery payload | EntitiesConfig (entities.yaml) | Yes — config-driven | FLOWING |
| ScoreStreamPipeline (multi-entity) | reading per entity | IAsyncEnumerable<HaReading> shared source | No — concurrent MoveNextAsync race drops events | DISCONNECTED (WR-03) |

---

### Behavioral Spot-Checks

Step 7b: SKIPPED — no runnable entry points without live HA, MQTT broker, and detector containers. Tests verified instead.

---

### Probe Execution

Step 7c: No probe scripts found in scripts/*/tests/probe-*.sh. SKIPPED.

---

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| INFRA-01 | 01-01 | Mono-repo scaffolded with proto/, orchestrator/, detector/, deploy/ | SATISFIED | All four directories present with build tooling wired |
| INFRA-02 | 01-01 | proto/argus.proto finalized; .NET stubs via Grpc.Tools; Python stubs via grpcio-tools | SATISFIED | csproj Protobuf Include; gen_proto.py; stubs generated |
| INFRA-03 | 01-04 | Docker image for .NET 8 orchestrator builds and runs as worker service | SATISFIED | Dockerfile.orchestrator 8.0-jammy-chiseled; docker build exits 0 per SUMMARY |
| INFRA-04 | 01-02 | Docker image for Python gRPC detector builds and runs | SATISFIED | Dockerfile.detector python:3.12-slim-bookworm; docker build exits 0 per SUMMARY |
| INFRA-05 | 01-04 | docker-compose.edge.yml + docker-compose.gpu.yml for two-host deploy | SATISFIED | Both compose files exist with correct service topology |
| INFRA-06 | 01-03 | mTLS certs generated; orchestrator and detector communicate over mTLS | SATISFIED | generate-certs.sh, ca/server/client certs on disk; DetectorChannelFactory uses mTLS |
| INFRA-07 | 01-02, 01-04 | Health RPC validated end-to-end before detection work | PARTIAL | Health RPC unit-tested; end-to-end validation requires live containers (human_verification #1) |
| CONF-01 | 01-04 | entities.yaml schema defined; new entity requires only config edit | SATISFIED | EntitiesConfigLoader.cs parses entities.yaml; entities.yaml has 3 placeholder entities |
| CONF-02 | 01-04 | Per-entity detector assignment and params in entities.yaml | SATISFIED | entities.yaml has detectors + params; HstParams.From() resolves with defaults |
| CONF-03 | 01-04 | Connection settings from environment; no credentials in source | SATISFIED | ConnectionSettings.cs: ARGUS_HA_TOKEN, ARGUS_MQTT_PASSWORD env-only; compose uses ${VAR} substitution |
| STRM-01 | 01-05 | HA WebSocket subscribes, filters, reconnects with exponential backoff | SATISFIED | NetDaemonHaEventSource.cs; HashSet filter; BackoffMaxSeconds=60; GetStatesAsync on reconnect |
| STRM-02 | 01-05, 01-08 | On reconnect, get_states called; binary_sensor suppressed for 60s | BLOCKED | get_states called (satisfied); but 60s suppression broken by hardcoded SuppressBinarySensor=false in ScoreStreamPipeline.cs:109 |
| STRM-03 | 01-06, 01-08 | River HST scores each point via ScoreStream bidi gRPC | BLOCKED | HST scoring implemented and tested; but multi-entity ScoreStreamPipeline shared IAsyncEnumerable race drops events for N>1 entities |
| STRM-04 | 01-08 | End-to-end latency < 2s | NEEDS HUMAN | latency_ms logged per verdict (OBS-01); actual < 2s SLA requires live test |
| STRM-05 | 01-08 | Orchestrator hysteresis gate prevents binary_sensor flapping | SATISFIED | HysteresisGate.cs: N-consecutive logic; min_consecutive=3; tests pass |
| FAULT-01 | 01-06 | Point spike detected and flagged | SATISFIED | test_hst_detector: spike score strictly > baseline; HST warm-up tracking |
| FAULT-02 | 01-08 | Frozen/stuck sensor detected via rule-based check | SATISFIED | FrozenSensorDetector.cs: rolling variance Queue; IsFrozen when < 0.001; tests pass |
| MQTT-01 | 01-07 | MQTT discovery: binary_sensor + sensor per entity under one device | SATISFIED | DiscoveryPublisher.cs: both payloads per entity; device.identifiers; retain=true |
| MQTT-02 | 01-07 | unique_id formula deterministic, never random | SATISFIED | UniqueId.cs: argus_{slug}_{detector}_{suffix}; no Guid; no Random |
| MQTT-03 | 01-07 | Discovery payloads retain=true; LWT offline before first state | SATISFIED | PublishAllAsync retain=true; MqttConnection WithWillTopic before ConnectAsync |
| MQTT-04 | 01-07 | Discovery publish idempotent | SATISFIED | Stable unique_id + retain; republish replaces, never duplicates |
| MQTT-05 | 01-07 | Polish friendly-names auto-generated | SATISFIED | FriendlyName.cs: ForAnomaly appends " anomalia"; UTF-8 preserved |
| RES-01 | 01-07, 01-08 | Graceful degradation: unavailable (not off) on detector loss | BLOCKED | Bridge LWT on crash works; but per-entity unavailable via HandleDetectorFailureAsync is unreachable (CR-05 availability topic mismatch) |
| RES-03 | 01-04 | Health-checks detector before HA subscription; re-establishes on reconnect | SATISFIED | WaitForHealthyAsync with backoff; HaListenerWorker gates on it |
| OBS-01 | 01-02, 01-04, 01-08 | Structured logs on both sides; latency, events/s, verdict latency | SATISFIED | logging_setup.py JSON; LogEvents.cs 16 EventIds; per-verdict latency_ms in ScoreStreamPipeline |

---

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs` | 109 | `new Ha.HaReading(entityId, 0.0, DateTimeOffset.UtcNow, false)` — SuppressBinarySensor hardcoded false | BLOCKER | Post-reconnect 60s suppression silently disabled; false anomaly cascade on reconnect |
| `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs` | 82-83 | Multiple tasks iterate same IAsyncEnumerable concurrently | BLOCKER | Events dropped for all entities beyond first; STRM-03 broken for multi-entity config |
| `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs` | 43 | `availability_topic = BridgeAvailabilityTopic` only — no per-entity availability | BLOCKER | RES-01 per-entity degradation unreachable; HandleDetectorFailureAsync silent no-op |
| `detector/argus_detector/servicer.py` | 35-37 | Stale docstring: "Placeholder: score=0.0" — real HST scoring already wired at line 48 | Warning | Maintenance hazard; contradicts implementation (IN-03 from REVIEW) |
| `detector/argus_detector/registry.py` | 41-43 | Double-checked locking — unlocked read path; CPython GIL-safe but fragile | Warning | Concurrent dict resize could corrupt unlocked reads (WR-01 from REVIEW) |
| `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs` | ~100-102 | Reconnect loop uses CancellationToken.None — cannot be stopped on shutdown | Warning | Process will not exit cleanly on SIGTERM; Docker stop delays to SIGKILL (CR-03) |
| `orchestrator/Argus.Orchestrator/Detection/DetectorChannelFactory.cs` | 42 | X509Certificate2.CreateFromPemFile without CopyWithPrivateKey on .NET 8 Linux | Warning | Intermittent mTLS handshake failure under GC pressure on deployment target (CR-04) |
| `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` | 71 | Background task started with `_ = Task.Run(...)` — exceptions swallowed | Warning | Channel completes silently on error; pipeline terminates without worker restart (CR-01) |
| `detector/argus_detector/normalizer.py` | whole file | OnlineMinMaxScaler never imported; EntityDetector uses MinMaxScaler directly | Info | Dead code; two divergent normalizer paths if either is updated (IN-01 from REVIEW) |

---

### Human Verification Required

### 1. End-to-End Integration with Real HA

**Test:** Replace placeholder Q1 entity_ids in entities.yaml with real HA entity_ids; run `docker compose -f deploy/docker-compose.gpu.yml up` (detector) and `docker compose -f deploy/docker-compose.edge.yml up` (orchestrator) with real env vars; trigger a sensor state_changed event in HA.
**Expected:** HA automatically creates binary_sensor + sensor entities (no manual creation); MQTT discovery visible in HA MQTT integration; state update appears within 2 seconds.
**Why human:** Requires real HA instance, real MQTT broker, real LAN connectivity; placeholder entity_ids in entities.yaml block any automated integration test.

### 2. mTLS Handshake Stability on Linux

**Test:** Run the orchestrator container (`docker run argus-orchestrator:test`) against a real detector; observe mTLS handshake success/failure over 50+ connection attempts under GC pressure.
**Expected:** No `AuthenticationException` during handshake; all connections succeed.
**Why human:** CR-04 (X509Certificate2 private key GC issue) is a runtime behavior on .NET 8 Linux that cannot be verified by grep. Only observable under load on the actual deployment target.

### 3. STRM-04 End-to-End Latency SLA

**Test:** Monitor structured logs for `latency_ms` values across 100+ verdicts under normal LAN load.
**Expected:** All `latency_ms` values well under 2000; P99 < 2000ms.
**Why human:** Latency is a runtime metric; only verifiable with live system under realistic load.

---

## Gaps Summary

Three blockers prevent the phase goal from being achieved in any multi-entity configuration:

**Blocker 1 — Post-reconnect suppression broken (T-21, STRM-02):** `ScoreStreamPipeline.cs` line 109 creates a synthetic `HaReading` with `SuppressBinarySensor=false`. The reconnect cooldown value from the actual `HaReading` (set by `ReconnectCooldown.IsSuppressed()`) is consumed in the write loop but never forwarded to the verdict processing path. Result: binary_sensor flags are published during the 60s post-reconnect window, producing the false-anomaly cascade D-07 was designed to prevent.

**Blocker 2 — Multi-entity fan-out race (T-23, STRM-03):** `ScoreStreamPipeline.RunAsync(readings, ct)` starts one task per entity but all tasks share the same `IAsyncEnumerable<HaReading>` source. `IAsyncEnumerable` is not thread-safe for concurrent enumeration; concurrent `MoveNextAsync` calls race and only one task receives each event. With 3 entities configured in entities.yaml, 2 of 3 entities will receive no readings. This is the single most critical bug: the pipeline is entirely non-functional for the multi-entity use case.

**Blocker 3 — Per-entity graceful degradation unreachable (T-29, RES-01):** `DiscoveryPublisher` declares only `availability_topic = "argus/bridge/availability"` in all discovery payloads. `StatePublisher.PublishAvailabilityAsync` publishes to `argus/{slug}/availability` (per-entity topic) — a topic HA has never subscribed to because the discovery config does not declare it. `HandleDetectorFailureAsync` calls to publish per-entity offline are silently discarded. Bridge-level LWT on crash works correctly; per-entity detector failure degradation is dead code.

These three gaps share a common root: the `ScoreStreamPipeline` was designed and tested for single-entity scenarios (the test overload using `IScoreStreamCall` injection) but the production multi-entity code path was not validated with the same rigor.

**Recommended fix sequence:**
1. Fix WR-03 fan-out (add per-entity `Channel<HaReading>` fan-out before spawning entity tasks)
2. Fix CR-02 SuppressBinarySensor propagation (track in EntityRuntimeState)
3. Fix CR-05 availability topic (add per-entity availability to discovery payloads or align publish path with bridge-level)

Items 2 and 3 can be addressed in the same plan as item 1 since they all touch `ScoreStreamPipeline` and `DiscoveryPublisher`.

---

_Verified: 2026-06-10T12:20:35Z_
_Verifier: Claude (gsd-verifier)_
