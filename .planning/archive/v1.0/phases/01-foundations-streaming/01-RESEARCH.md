# Phase 1: Foundations + Streaming - Research

**Researched:** 2026-06-09
**Domain:** .NET 8 + Python gRPC hybrid; HA WebSocket; MQTT discovery; River HST streaming anomaly detection; mTLS two-host Docker deployment
**Confidence:** HIGH — all library versions verified against NuGet/PyPI; architecture patterns verified against official docs

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

- **D-01:** `google.protobuf.DoubleValue` wrapper for `score`, `expected`, `lower`, `upper` in `Verdict` — proto3 drops `0.0` on wire; wrapper makes presence explicit.
- **D-02:** `google.protobuf.Timestamp` for all timestamp fields in `Point` and `Verdict`.
- **D-03:** Health RPC uses standard `grpc.health.v1` proto — not a custom message.
- **D-04:** `ScoreStream` direction: orchestrator (client) sends `Point` stream → detector (server) streams `Verdict` back. One long-lived bidi stream per entity.
- **D-05:** `FitRequest` carries detector name, params map, and training `Window`. Shares `Point`/`Window`/`Verdict` message types with streaming path.
- **D-06:** `NetDaemon.Client` version **23.46.0** pinned (.NET 8 target). `vicfergar/HassClient` is fallback only.
- **D-07:** On HA WebSocket reconnect: call `get_states` API, then suppress `binary_sensor` state publication for **60 seconds**. No replaying burst.
- **D-08:** Per-entity **online min-max normalization** for HST input. Learn bounds from stream; clip to [0,1] until range stabilizes.
- **D-09:** River `HalfSpaceTrees` defaults: `window=250`, `n_trees=25`. Per-entity overridable from `entities.yaml`.
- **D-10:** Hysteresis gate in **.NET orchestrator**. Python detector returns raw scores only.
- **D-11:** Default hysteresis: `high_threshold: 0.7`, `low_threshold: 0.3`, `min_consecutive: 3`. Per-entity overridable.
- **D-12:** Frozen detection: rule-based in orchestrator. Default `frozen_window: 10`, `frozen_variance_threshold: 0.001`.
- **D-13:** `unique_id` formula: `argus_{entity_slug}_{detector}_{suffix}` where `entity_slug` = `entity_id` with `.` → `_`.
- **D-14:** Set `object_id` to same slug as `unique_id` suffix.
- **D-15:** All discovery payloads: `retain: true`. LWT payload = `offline`; online payload = `online`. LWT configured before any state publish.
- **D-16:** Polish friendly-name pattern: `"[Room] [SensorType] anomalia"`.
- **D-17:** Two-host with mTLS from day one. Self-signed certs in `deploy/certs/`, 2-year expiry. GPU host cert includes both LAN IP and hostname as SANs.
- **D-18:** `.NET` mTLS: `HttpClientHandler.ClientCertificates` + custom server cert validation callback. Not `SslCredentials` with non-null args.
- **D-19:** Mono-repo layout: `proto/`, `orchestrator/`, `detector/`, `deploy/`.
- **D-20:** .NET: `Microsoft.Extensions.Hosting` `BackgroundService` worker. Solution: `Argus.Orchestrator` + `Argus.Orchestrator.Tests`.
- **D-21:** Python: `pyproject.toml` with grpcio, grpcio-tools, river, pyod, numpy, pandas, pydantic, joblib. Phase 1 uses River only; PyOD loaded but not wired.

### Claude's Discretion

- Polly retry policy parameters (initial delay, max retries, jitter) for MQTTnet reconnect
- gRPC channel options (keepalive, max message size)
- Structured logging: use built-in `ILogger<T>` with structured properties; no external logging framework in Phase 1
- Python logging: standard `logging` module with JSON formatter

### Deferred Ideas (OUT OF SCOPE)

- InfluxDB batch ingestion, PyOD detectors, model persistence → Phase 2
- STL seasonal decomposition, covariate conditioning, multivariate groups → v2 phases
- GPU support, ONNX export → Phase 4
- `entities.yaml` hot-reload without restart → v2 (ADV-03)
- Adaptive thresholds / PyThresh → Phase 3+
- CPU-only detector replica on edge host → Phase 2 decision
</user_constraints>

---

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| INFRA-01 | Mono-repo scaffolded with proto/, orchestrator/, detector/, deploy/ | Directory layout locked (D-19); .NET worker service template (D-20); Python pyproject.toml (D-21) |
| INFRA-02 | proto/argus.proto finalized; .NET stubs via Grpc.Tools; Python stubs via grpcio-tools | Grpc.Tools 2.80.0 + grpcio-tools 1.81.0; DoubleValue wrappers (D-01); Timestamp (D-02); health.v1 (D-03) |
| INFRA-03 | Docker image for .NET 8 orchestrator | `mcr.microsoft.com/dotnet/runtime:8.0-jammy-chiseled`; multi-stage SDK→runtime build |
| INFRA-04 | Docker image for Python gRPC detector | `python:3.12-slim-bookworm`; CPU-only Phase 1 |
| INFRA-05 | docker-compose.edge.yml + docker-compose.gpu.yml for two-host deploy | Two-host from day one (D-17); certs volume-mounted |
| INFRA-06 | mTLS certs generated; orchestrator ↔ detector over mTLS gRPC | `openssl` self-signed CA + server/client certs; SAN with LAN IP + hostname; `HttpClientHandler` pattern (D-18) |
| INFRA-07 | Health RPC validated end-to-end before detection work | `grpc.health.v1`; orchestrator polls Health before subscribing to HA events (RES-03 dependency) |
| CONF-01 | entities.yaml schema defined and parsed; adding entity requires only config edit | YAML with `detectors` + `params` keys; `covariates`/`groups` parsed but ignored with warning |
| CONF-02 | Per-entity detector assignment and params in entities.yaml | `detectors:` list + `params:` map; HST defaults (D-09) per-entity overridable |
| CONF-03 | Connection settings from env/secrets; no credentials in source | IConfiguration / env vars for HA URL/token, MQTT broker, gRPC endpoint; Docker secrets or env_file |
| STRM-01 | HA WebSocket: authenticate, subscribe state_changed, filter entities, reconnect with exponential backoff | NetDaemon.Client 23.46.0; IHomeAssistantRunner; reconnect backoff 1s→2s→4s→8s→max 60s |
| STRM-02 | On reconnect: get_states (not burst replay), suppress binary_sensor for 60s | D-07; post-reconnect cooldown timer; per-entity `last_seen` tracking |
| STRM-03 | River HST streaming detector scores each point via ScoreStream bidi gRPC | River 0.25.0 HalfSpaceTrees; per-entity online min-max normalization (D-08); `score_one()` per event |
| STRM-04 | End-to-end streaming latency < 2s | Single bidi stream per entity; no buffering on hot path; LAN gRPC overhead ~1ms |
| STRM-05 | Orchestrator hysteresis gate prevents binary_sensor flapping | D-10/D-11; high=0.7, low=0.3, min_consecutive=3; state held in orchestrator, not detector |
| FAULT-01 | Point spike anomaly detected | HST score_one exceeds hysteresis high threshold for N consecutive readings — naturally handled by streaming path |
| FAULT-02 | Frozen/stuck sensor detected via rule-based variance check | D-12; orchestrator keeps rolling window of last 10 readings; var < 0.001 → flag frozen |
| MQTT-01 | MQTT discovery: binary_sensor + sensor per entity, grouped under one HA device | MQTTnet 5.1.0.1559; homeassistant/binary_sensor/{slug}/config + homeassistant/sensor/{slug}_score/config |
| MQTT-02 | unique_id deterministic formula stable across restarts | D-13: `argus_{entity_slug}_{detector}_{suffix}` |
| MQTT-03 | retain: true on all discovery; LWT offline before any state | D-15; MQTT LWT registered on connect before first publish |
| MQTT-04 | Discovery publish idempotent | Retain + stable unique_id; republish on reconnect is safe; HA deduplicates by unique_id |
| MQTT-05 | Polish friendly-names auto-generated | D-16: `"[Room] [SensorType] anomalia"`; object_id set to slug (D-14) |
| RES-01 | Detector unreachable → anomaly sensors show unavailable (not off) | LWT `offline` on availability topic; orchestrator marks per-entity channel state unavailable on RpcException |
| RES-03 | Orchestrator health-checks detector before subscribing to state_changed | grpc.health.v1 Check RPC poll; INFRA-07 validates this path; auto re-establish on reconnect |
| OBS-01 | Structured logs on both sides; events/s, verdict latency, detector errors | ILogger<T> with structured properties (.NET); Python logging with JSON formatter; log entity_id, latency_ms, detector, score per verdict |
</phase_requirements>

---

## Summary

Phase 1 delivers the complete end-to-end streaming path for the Argus anomaly detection system. It is a greenfield build with no existing code to integrate with. Every architectural decision is already locked in CONTEXT.md, which means research can focus on verifying implementation specifics rather than exploring alternatives.

The phase has three structural layers: (1) infrastructure scaffold — mono-repo, Docker images, mTLS certs, proto contract; (2) streaming detection path — HA WebSocket → gRPC ScoreStream (River HST) → MQTT discovery → HA entities; (3) resilience/quality gates — hysteresis in the orchestrator, frozen detection, graceful degradation to `unavailable`, and structured logging. All three layers must be built in dependency order: proto contract first, then mTLS, then streaming, then MQTT, then the gates.

The single biggest execution risk is the mTLS + bidi streaming combination. Both are independently fiddly; debugging both simultaneously is the most common time sink in projects of this architecture. The locked decision to validate mTLS via Health RPC (INFRA-07) before any ScoreStream work is the correct mitigation and must be enforced in the plan's wave structure.

**Primary recommendation:** Build wave-by-wave in strict dependency order — proto → stubs → mTLS/Health → entities.yaml → HA WebSocket → ScoreStream → MQTT discovery → hysteresis/frozen/graceful-degradation. Do not parallelize waves that share mTLS validation as a gate.

---

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| HA event ingestion (state_changed) | .NET Orchestrator (edge) | — | NetDaemon.Client owns WebSocket; HA runs on edge host |
| Streaming anomaly scoring | Python Detector (GPU) | — | River HST is Python; all ML locked to Python (D2) |
| Feature normalization (min-max) | Python Detector (GPU) | — | Must happen before HST sees data; co-located with detector |
| Hysteresis gate | .NET Orchestrator (edge) | — | Owns MQTT publish state; D-10 locked |
| Frozen sensor detection | .NET Orchestrator (edge) | — | Pure rule on raw values; D-12 locked |
| MQTT discovery + state publish | .NET Orchestrator (edge) | — | Owns egress to HA; single point of publish control |
| gRPC channel management + retry | .NET Orchestrator (edge) | — | Client-side; Detection Gateway pattern |
| Health check gate (pre-stream) | .NET Orchestrator (edge) | Python Detector (serves) | Orchestrator polls; detector implements grpc.health.v1 |
| mTLS cert management | deploy/ (both sides) | — | Certs generated once, volume-mounted; no runtime generation |
| entities.yaml config parsing | .NET Orchestrator (edge) | — | Orchestrator drives entity set; Python receives entity_id via gRPC |
| Structured logging | Both tiers | — | ILogger<T> (.NET); logging+JSON (Python) |

---

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| NetDaemon.Client | 23.46.0 (pinned) | HA WebSocket client | Only production-ready .NET HA client; .NET 8 target [VERIFIED: NuGet] |
| MQTTnet | 5.1.0.1559 | MQTT client | De-facto standard .NET MQTT; under dotnet org; .NET 8; MQTT v5 [VERIFIED: NuGet] |
| Grpc.Net.Client | 2.80.0 | gRPC client (.NET) | Official Google client; HttpClientFactory integration; .NET 8 explicit target [VERIFIED: NuGet] |
| Grpc.Tools | 2.80.0 | .proto → C# codegen | Zero-friction MSBuild integration; pin to match Grpc.Net.Client [VERIFIED: NuGet] |
| grpcio | 1.81.0 | gRPC runtime (Python) | Only production gRPC Python impl; mTLS first-class [VERIFIED: PyPI 2026-06-01] |
| grpcio-tools | 1.81.0 | .proto → Python codegen | Must match grpcio version exactly [VERIFIED: PyPI] |
| river | 0.25.0 | HalfSpaceTrees streaming anomaly detection | Reference online ML lib; learn_one/score_one API [VERIFIED: PyPI 2026-05-31] |

### Supporting (Phase 1 — loaded not wired)
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| pyod | 3.6.0 | PyOD batch detectors (MAD, RobustZScore) | Phase 2 only; install now so Docker layer is stable [VERIFIED: PyPI 2026-06-04] |
| joblib | 1.5.3 | Model persistence | Phase 2 only; install now [VERIFIED: PyPI] |
| numpy | latest compatible | Numeric array ops | River/PyOD dependency; no direct use in Phase 1 |
| pandas | latest compatible | DataFrame support | PyOD training window; no direct use in Phase 1 |
| pydantic | latest v2 | Config validation | entities.yaml schema validation in Python if needed |
| Microsoft.Extensions.Hosting | framework | BackgroundService worker | Ships with .NET 8 Worker Service template; no extra package |

### Docker Base Images
| Image | Layer | Why |
|-------|-------|-----|
| `mcr.microsoft.com/dotnet/runtime:8.0-jammy-chiseled` | Orchestrator final | Distroless Ubuntu 22.04; ~85 MB; no ASP.NET needed [VERIFIED: MS docs] |
| `mcr.microsoft.com/dotnet/sdk:8.0` | Orchestrator build stage | SDK for multi-stage build only |
| `python:3.12-slim-bookworm` | Detector | Slim Debian 12; no CUDA needed Phase 1-2 [VERIFIED: Docker Hub] |

### Alternatives Not to Use
| Instead of | Could Use | Why We Don't |
|------------|-----------|--------------|
| NetDaemon.Client 23.46.0 | 26.x | 26.x requires .NET 10; not Phase 1 compatible |
| NetDaemon.Client | vicfergar/HassClient | 28 stars; fallback only |
| MQTTnet 5.x | MQTTnet 4.x | ManagedClient removed; new projects on v5 |
| grpcio 1.81.0 | grpc.experimental.aio | Replaced by grpc.aio since 1.32 |
| grpc.aio | sync grpc.server | Sync server + ThreadPoolExecutor is simpler; HST is not CPU-bound enough to warrant async |

**Installation:**
```xml
<!-- Argus.Orchestrator.csproj -->
<PackageReference Include="NetDaemon.Client" Version="23.46.0" />
<PackageReference Include="MQTTnet" Version="5.1.0.1559" />
<PackageReference Include="Grpc.Net.Client" Version="2.80.0" />
<PackageReference Include="Grpc.Tools" Version="2.80.0" PrivateAssets="All" />
```

```
# detector/requirements.txt
grpcio==1.81.0
grpcio-tools==1.81.0
river==0.25.0
pyod==3.6.0
joblib==1.5.3
numpy
pandas
pydantic
```

---

## Architecture Patterns

### System Architecture Diagram

```
HA WebSocket (state_changed event)
        │
        ▼
[NetDaemon.Client — IHomeAssistantRunner]
        │  entity_id, value, last_changed timestamp
        │
        ▼
[HA Event Filter] — entities.yaml entity set
        │  (drop entities not in config)
        │
        ▼
[Frozen Sensor Gate] — orchestrator
        │  var(last 10 readings) < 0.001 → frozen flag → MQTT publish
        │  else → forward to gRPC
        │
        ▼  gRPC bidi ScoreStream (one stream per entity, mTLS)
[Detection Gateway — GrpcChannel]
        │  Point { entity_id, value: DoubleValue, timestamp: Timestamp }
        │
        ▼  (LAN, ~1ms)
[Python Detector gRPC Server]
        │
        ▼
[Per-Entity Min-Max Normalizer] — online, learns from stream
        │  value normalized to [0,1]
        │
        ▼
[River HalfSpaceTrees — score_one(x)]
        │  anomaly_score: float
        │
        ▼  Verdict { entity_id, score: DoubleValue, timestamp: Timestamp }
        │  (back over bidi stream)
        │
        ▼
[Hysteresis Gate] — orchestrator
        │  N consecutive readings above high_threshold → state = ON
        │  N consecutive readings below low_threshold → state = OFF
        │  else → hold current state
        │
        ▼
[MQTT Publisher — MQTTnet 5]
        │  homeassistant/binary_sensor/{slug}/config  (retain, on first-seen)
        │  homeassistant/sensor/{slug}_score/config   (retain, on first-seen)
        │  argus/{slug}/flag/state  → ON/OFF
        │  argus/{slug}/score/state → float string
        │  argus/{slug}/availability → online/offline (LWT)
        │
        ▼
[HA MQTT Integration → HA entities visible in UI]

Detector Down Path:
  Detection Gateway catches RpcException
        │
        ▼
  argus/{slug}/availability → offline (LWT fires or orchestrator publishes)
        │
        ▼
  HA shows binary_sensor: unavailable
```

### Recommended Project Structure
```
/
├── proto/
│   ├── argus.proto              # Service + message definitions; single source of truth
│   └── google/                  # WKT imports (Timestamp, DoubleValue)
├── orchestrator/
│   ├── Argus.Orchestrator/
│   │   ├── Workers/
│   │   │   ├── HaListenerWorker.cs      # BackgroundService: HA WebSocket subscription
│   │   │   └── MqttPublisherWorker.cs   # BackgroundService: MQTT lifecycle
│   │   ├── Detection/
│   │   │   ├── DetectionGateway.cs      # gRPC channel + ScoreStream management
│   │   │   ├── HysteresisGate.cs        # Per-entity threshold state machine
│   │   │   └── FrozenSensorDetector.cs  # Rule-based variance check
│   │   ├── Mqtt/
│   │   │   ├── DiscoveryPublisher.cs    # Build + publish HA discovery payloads
│   │   │   └── StatePublisher.cs        # Publish ON/OFF and score state
│   │   ├── Config/
│   │   │   ├── EntitiesConfig.cs        # entities.yaml deserialization types
│   │   │   └── ConnectionSettings.cs    # HA/MQTT/gRPC endpoint config
│   │   └── Program.cs                   # Host builder, DI wiring
│   └── Argus.Orchestrator.Tests/
│       └── ...
├── detector/
│   ├── argus_detector/
│   │   ├── server.py            # gRPC server entry point
│   │   ├── servicer.py          # DetectorService implementor
│   │   ├── registry.py          # (entity_id, detector) → instance map
│   │   ├── normalizer.py        # Per-entity online min-max
│   │   ├── hst_detector.py      # River HalfSpaceTrees wrapper
│   │   └── proto/               # Generated stubs (not checked in)
│   ├── pyproject.toml
│   └── requirements.txt
├── deploy/
│   ├── certs/                   # ca.crt, server.crt/key, client.crt/key
│   ├── docker-compose.edge.yml  # Orchestrator container
│   ├── docker-compose.gpu.yml   # Detector container
│   └── generate-certs.sh        # openssl commands
└── entities.yaml                # Monitored entities config
```

### Pattern 1: proto/argus.proto Contract

**What:** Single `.proto` file defining all message types and service RPCs. Both sides compile from this file — .NET via Grpc.Tools at build time, Python via `grpcio-tools` as a setup step.

**Key design points enforced by locked decisions:**
- `DoubleValue` wrapper for optional floats (score=0.0 must not be dropped)
- `google.protobuf.Timestamp` for all timestamps
- `grpc.health.v1` imported as the Health service (not custom)
- `ScoreStream` is bidi: orchestrator sends `Point`, detector yields `Verdict`

```protobuf
// Source: CONTEXT.md D-01 through D-05; grpc.io official proto3 docs
syntax = "proto3";
package argus.v1;
option csharp_namespace = "Argus.Detector.V1";

import "google/protobuf/timestamp.proto";
import "google/protobuf/wrappers.proto";

message Point {
  string entity_id = 1;
  google.protobuf.DoubleValue value = 2;
  google.protobuf.Timestamp timestamp = 3;
}

message Verdict {
  string entity_id = 1;
  google.protobuf.DoubleValue score = 2;
  google.protobuf.DoubleValue expected = 3;
  google.protobuf.DoubleValue lower = 4;
  google.protobuf.DoubleValue upper = 5;
  bool is_anomaly = 6;
  string detector = 7;
  google.protobuf.Timestamp timestamp = 8;
}

service DetectorService {
  rpc ScoreStream(stream Point) returns (stream Verdict);
  rpc Fit(FitRequest) returns (FitResponse);
}

// FitRequest and FitResponse for Phase 2 — define now, implement later
message FitRequest {
  string entity_id = 1;
  string detector = 2;
  map<string, string> params = 3;
  repeated Point window = 4;
}
message FitResponse {
  bool ok = 1;
  string error = 2;
}
```

Health service is provided by importing `health.proto` from the `grpc-health-probe` package rather than hand-writing it. [CITED: grpc.io/docs/guides/health-checking]

### Pattern 2: .NET gRPC mTLS Channel (D-18)

**What:** Build the GrpcChannel with `HttpClientHandler.ClientCertificates`. This is the only supported mTLS path in `Grpc.Net.Client` — `SslCredentials` with non-null args is legacy Grpc.Core and explicitly unsupported.

```csharp
// Source: ARCHITECTURE.md; grpc-dotnet issue #2112; MS docs on X509Certificate2
var caCert = new X509Certificate2("deploy/certs/ca.crt");
var clientCert = X509Certificate2.CreateFromPemFile(
    "deploy/certs/client.crt",
    "deploy/certs/client.key");

var handler = new HttpClientHandler();
handler.ClientCertificates.Add(clientCert);
handler.ServerCertificateCustomValidationCallback = (_, cert, chain, _) =>
{
    chain!.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(caCert);
    return chain.Build(cert!);
};

var channel = GrpcChannel.ForAddress(
    "https://<gpu-host-ip>:50051",
    new GrpcChannelOptions { HttpHandler = handler });
// Create ONE channel at startup. Stubs are cheap; channels are expensive.
```

### Pattern 3: .NET Bidi ScoreStream — Read/Write Loop

**What:** Open one `AsyncDuplexStreamingCall<Point, Verdict>` per monitored entity. Write loop feeds from HA events; read loop feeds into hysteresis gate and MQTT publisher. They MUST run concurrently.

```csharp
// Source: ARCHITECTURE.md; MS official gRPC client streaming docs
using var call = client.ScoreStream(cancellationToken: appLifetime.ApplicationStopping);

// Read loop — must be a separate Task
var readTask = Task.Run(async () =>
{
    await foreach (var verdict in call.ResponseStream.ReadAllAsync(ct))
    {
        hysteresisGate.Apply(verdict);  // → MQTT publisher
    }
});

// Write loop — inline in the BackgroundService
await foreach (var haEvent in haEventChannel.ReadAllAsync(ct))
{
    await call.RequestStream.WriteAsync(ToPoint(haEvent), ct);
}

// Completion: CompleteAsync FIRST, then await readTask
// Reversing this order deadlocks.
await call.RequestStream.CompleteAsync();
await readTask;
```

[CITED: ARCHITECTURE.md — .NET Bidi Streaming Gotchas, item 3]

### Pattern 4: Python gRPC Sync Server ScoreStream Servicer

**What:** The Python server uses the sync gRPC API (not `grpc.aio`) with a thread pool. Each `ScoreStream` call gets its own thread and its own `request_iterator`. Per-entity detector state is stored in `DetectorRegistry` keyed by `entity_id`.

```python
# Source: ARCHITECTURE.md; official grpcio docs
import grpc
from concurrent import futures

class DetectorServicer(argus_pb2_grpc.DetectorServiceServicer):
    def __init__(self, registry):
        self._registry = registry

    def ScoreStream(self, request_iterator, context):
        for point in request_iterator:
            if not context.is_active():
                return
            entity_id = point.entity_id
            value = point.value.value  # unwrap DoubleValue
            score = self._registry.score_one(entity_id, value)
            verdict = argus_pb2.Verdict(
                entity_id=entity_id,
                score=wrappers_pb2.DoubleValue(value=score),
                is_anomaly=False,  # orchestrator applies hysteresis
                detector="hst",
                timestamp=Timestamp(),
            )
            timestamp_pb.GetCurrentTime(verdict.timestamp)
            yield verdict

server = grpc.server(futures.ThreadPoolExecutor(max_workers=10))
argus_pb2_grpc.add_DetectorServiceServicer_to_server(DetectorServicer(registry), server)
```

### Pattern 5: MQTT Discovery Payload

**What:** Publish retained discovery config before first state. Every entity gets two HA entities (binary_sensor + sensor) under one HA device.

```python
# Source: ARCHITECTURE.md MQTT Discovery Notes; HA official MQTT integration docs
entity_slug = entity_id.replace(".", "_")
unique_id_anomaly = f"argus_{entity_slug}_hst_anomaly"
unique_id_score   = f"argus_{entity_slug}_hst_score"

device = {
    "identifiers": [entity_slug],
    "name": f"Argus {entity_slug}",
    "model": "Argus Anomaly Detector",
    "manufacturer": "Argus"
}

binary_sensor_config = {
    "unique_id": unique_id_anomaly,
    "object_id": unique_id_anomaly,
    "name": f"{room} {sensor_type} anomalia",   # Polish friendly-name (D-16)
    "state_topic": f"argus/{entity_slug}/flag/state",
    "availability_topic": f"argus/{entity_slug}/availability",
    "payload_available": "online",
    "payload_not_available": "offline",
    "payload_on": "ON",
    "payload_off": "OFF",
    "device": device,
    "device_class": "problem"
}
# Publish to: homeassistant/binary_sensor/{unique_id_anomaly}/config
# retain=True, qos=1
```

### Pattern 6: MQTTnet 5 Client Connect + LWT

**What:** MQTTnet v5 removed ManagedClient. Reconnect is handled manually (Polly or retry loop). LWT must be configured in the connect options BEFORE connecting — it cannot be set post-connect.

```csharp
// Source: STACK.md; MQTTnet v5 migration notes
var factory = new MqttClientFactory();
var mqttClient = factory.CreateMqttClient();

var willTopic = $"argus/{entitySlug}/availability";
var connectOptions = new MqttClientOptionsBuilder()
    .WithTcpServer(mqttHost, mqttPort)
    .WithCredentials(mqttUser, mqttPassword)
    .WithWillTopic(willTopic)
    .WithWillPayload("offline")
    .WithWillRetain(true)
    .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
    .Build();

await mqttClient.ConnectAsync(connectOptions, ct);
// Immediately publish "online" to availability topic after connect
await mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
    .WithTopic(willTopic)
    .WithPayload("online")
    .WithRetainFlag()
    .Build(), ct);
```

### Pattern 7: Hysteresis Gate State Machine (.NET)

**What:** Per-entity state machine. Tracks consecutive readings above/below thresholds. Only flips binary_sensor state when `min_consecutive` readings consistently cross threshold.

```csharp
// Source: CONTEXT.md D-10/D-11; PITFALLS.md alert-fatigue section
public class HysteresisState
{
    public bool IsAnomalous { get; private set; }
    private int _consecutiveHigh;
    private int _consecutiveLow;
    private readonly double _highThreshold;
    private readonly double _lowThreshold;
    private readonly int _minConsecutive;

    public bool Apply(double score)
    {
        if (score >= _highThreshold) { _consecutiveHigh++; _consecutiveLow = 0; }
        else if (score <= _lowThreshold) { _consecutiveLow++; _consecutiveHigh = 0; }
        else { _consecutiveHigh = 0; _consecutiveLow = 0; }

        if (!IsAnomalous && _consecutiveHigh >= _minConsecutive)
            IsAnomalous = true;
        else if (IsAnomalous && _consecutiveLow >= _minConsecutive)
            IsAnomalous = false;

        return IsAnomalous;
    }
}
```

### Pattern 8: Frozen Sensor Detection (.NET)

**What:** Orchestrator maintains a rolling circular buffer of the last N raw values per entity. If variance < threshold → emit frozen flag instead of anomaly score.

```csharp
// Source: CONTEXT.md D-12
public class FrozenSensorDetector
{
    private readonly Queue<double> _window = new();
    private readonly int _windowSize;
    private readonly double _varianceThreshold;

    public bool IsFrozen(double newValue)
    {
        _window.Enqueue(newValue);
        if (_window.Count > _windowSize) _window.Dequeue();
        if (_window.Count < _windowSize) return false;

        var mean = _window.Average();
        var variance = _window.Average(v => Math.Pow(v - mean, 2));
        return variance < _varianceThreshold;
    }
}
// Default: windowSize=10, varianceThreshold=0.001 (D-12)
```

### Pattern 9: entities.yaml Schema

**What:** Config file listing monitored entities. Phase 1 parses `detectors` and `params`; ignores `covariates` and `groups` with a logged warning.

```yaml
# entities.yaml
entities:
  - entity_id: sensor.salon_temperatura
    friendly_name: "Salon temperatura"  # used to derive Polish HA name
    detectors:
      - name: hst
        params:
          window: 250
          n_trees: 25
          high_threshold: 0.7
          low_threshold: 0.3
          min_consecutive: 3
  - entity_id: sensor.outdoor_temperature
    friendly_name: "Zewnątrz temperatura"
    detectors:
      - name: hst
        params: {}  # use defaults
```

### Anti-Patterns to Avoid

- **Using `SslCredentials` with non-null args in Grpc.Net.Client:** Unsupported; use `HttpClientHandler.ClientCertificates` (D-18).
- **Sequential read+write on bidi stream:** Deadlocks. Always run read loop in separate `Task.Run`.
- **Awaiting readTask before calling CompleteAsync:** Deadlocks. `CompleteAsync` must precede `await readTask`.
- **Using `Task.Result` or `.Wait()` on gRPC calls:** Thread pool starvation.
- **Random UUID in unique_id:** Any restart creates duplicate HA entities. Must be deterministic (D-13).
- **Publishing discovery without `retain: true`:** HA loses entity config on broker restart.
- **Replaying HA reconnect burst through ScoreStream:** False anomaly cascade. Always `get_states` + 60s suppression (D-07).
- **Using `grpc.experimental.aio`:** Old import path, use `import grpc.aio` or sync `import grpc`.
- **Using ML.NET:** Locked out-of-scope (D2); all ML is Python.
- **Creating multiple GrpcChannels:** One channel per process; create multiple stubs from the same channel.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| HA WebSocket protocol | Custom WS client with HA auth | NetDaemon.Client 23.46.0 | Auth, reconnect, event subscription, typed API all handled |
| Half-space tree anomaly detection | Custom streaming detector | River `HalfSpaceTrees` | Reference implementation; handles window management and score calibration |
| MQTT protocol | Raw TCP + MQTT framing | MQTTnet 5.1.0.1559 | QoS, retain, LWT, reconnect, MQTT v5 protocol |
| gRPC protobuf serialization | Manual byte serialization | Grpc.Tools + grpcio-tools | Code-gen eliminates all serialization bugs; field numbers handle wire format |
| mTLS certificate validation | Custom TLS verification | `X509ChainTrustMode.CustomRootTrust` pattern | One incorrect null-check breaks security; use the documented pattern exactly |
| Online normalization | Welford's running stats from scratch | `river.preprocessing.MinMaxScaler` | Built-in in River; handles warm-up and clip correctly |

**Key insight:** Every component of the streaming path (HA client, ML detector, transport, MQTT, TLS) has a production-quality library. The orchestrator's job is to wire them — not to replicate any of them.

---

## Common Pitfalls

### Pitfall 1: proto3 Silently Drops score=0.0
**What goes wrong:** Detector returns score 0.0; orchestrator receives no `score` field; treats absence as "no verdict" or panics on null dereference.
**Why it happens:** proto3 default value (0.0 for double) is not transmitted on the wire.
**How to avoid:** Use `google.protobuf.DoubleValue` wrapper (D-01). The wrapper is only absent when the server explicitly doesn't set it. Test with a detector that intentionally returns score=0.0 in Phase 1.
**Warning signs:** Score sensor in HA shows `unknown` for readings that should be 0.0.

### Pitfall 2: mTLS SAN Mismatch
**What goes wrong:** gRPC channel gets `RemoteCertificateNameMismatch` on .NET side; `StatusCode.UNAVAILABLE` with SSL error on Python side.
**Why it happens:** Server cert CN alone is insufficient; modern TLS requires SAN. If GPU host IP changes, hostname SAN fails.
**How to avoid:** Generate server cert with `subjectAltName=IP:<lan-ip>,DNS:<hostname>` in the extension file. Validate with `grpc.health.v1` Health RPC (INFRA-07) before any ScoreStream work — SAN errors surface immediately on first connect.
**Warning signs:** Health RPC fails with SSL handshake error; succeeds on localhost but fails cross-host.

### Pitfall 3: Bidi Stream Deadlock (CompleteAsync order)
**What goes wrong:** Application hangs on shutdown; `await readTask` never completes.
**Why it happens:** Awaiting the read task before calling `RequestStream.CompleteAsync()` causes both sides to wait for the other.
**How to avoid:** Always call `await call.RequestStream.CompleteAsync()` then `await readTask`. Never reverse the order. [CITED: ARCHITECTURE.md — .NET Bidi Streaming Gotchas, item 3]
**Warning signs:** Graceful shutdown hangs; cancellation token timeout is hit instead of clean exit.

### Pitfall 4: HA Reconnect Burst → False Anomaly Cascade
**What goes wrong:** After reconnect, HA sends burst of `state_changed` events. River HST receives correlated burst; scores spike; hysteresis gate opens; binary sensors flip to anomalous.
**Why it happens:** HA does not replay events — but reconnect triggers synthetic state_changed for all entities in rapid succession.
**How to avoid:** On reconnect, call `get_states` once (D-07), feed only the current values, then suppress `binary_sensor` publication for 60 seconds while continuing to score (so model warms up). [CITED: PITFALLS.md — HA WebSocket Reconnect]
**Warning signs:** All binary sensors flip ON simultaneously right after orchestrator restart.

### Pitfall 5: MQTT unique_id Instability
**What goes wrong:** HA accumulates duplicate entities (`binary_sensor.argus_..._2`, `binary_sensor.argus_..._3`) across restarts. Old entities persist as unavailable.
**Why it happens:** unique_id changed between runs (random UUIDs, or entity_id string mutation).
**How to avoid:** Use deterministic formula (D-13): `argus_{entity_slug}_{detector}_{suffix}`. Set `object_id` to same value (D-14). Test idempotency by publishing discovery twice and verifying no duplicate appears in HA.
**Warning signs:** HA entity list grows after each restart; `_2` suffix entities appear.

### Pitfall 6: Missing LWT or LWT After First State Publish
**What goes wrong:** Orchestrator crashes; HA shows `off` (last published state) instead of `unavailable`.
**Why it happens:** LWT must be registered in MQTT connect options before the connection is established. If LWT is omitted or configured post-connect, it is ignored.
**How to avoid:** Configure `WithWillTopic` / `WithWillPayload("offline")` in `MqttClientOptionsBuilder` before calling `ConnectAsync`. Publish `"online"` immediately after connect. [CITED: ARCHITECTURE.md — MQTT Discovery Notes]
**Warning signs:** Killing the orchestrator process leaves binary sensors showing last state instead of unavailable.

### Pitfall 7: HST Scores Not Comparable Across Entities
**What goes wrong:** Global threshold (0.7) works for indoor temperature but generates constant alerts for outdoor temperature, or vice versa.
**Why it happens:** Raw HST anomaly scores are not calibrated across entities with different feature ranges. Even with min-max normalization, the score distribution varies by entity.
**How to avoid:** Per-entity threshold overrides in `entities.yaml` (D-11). During Phase 1 warm-up, log score percentiles and adjust thresholds before declaring the system production-ready.
**Warning signs:** One entity class always shows anomalous or never shows anomalous regardless of actual readings.

### Pitfall 8: River HST Requires Warm-Up Period
**What goes wrong:** First N readings all score as anomalous (high scores) before the model has seen enough data to build a normal profile.
**Why it happens:** HalfSpaceTrees starts with no prior; all inputs are novel until window is filled (default 250 readings).
**How to avoid:** During warm-up (fewer than `window` readings processed), continue to update the model but suppress `binary_sensor` publication. Log warm-up state. Use the same 60s post-reconnect suppression window as a partial mitigation.
**Warning signs:** All sensors show anomalous immediately after first deploy.

---

## Code Examples

### MQTTnet 5 — Publish Retained Discovery
```csharp
// Source: MQTTnet v5 official API (dotnet/MQTTnet GitHub); STACK.md
// Note: MqttFactory replaced by MqttClientFactory in v5
var message = new MqttApplicationMessageBuilder()
    .WithTopic($"homeassistant/binary_sensor/{uniqueId}/config")
    .WithPayload(JsonSerializer.Serialize(discoveryPayload))
    .WithRetainFlag(true)
    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
    .Build();
await mqttClient.PublishAsync(message, cancellationToken);
```

### River HalfSpaceTrees with Min-Max Normalizer
```python
# Source: River 0.25.0 official docs; river.readthedocs.io
from river import anomaly, preprocessing

class EntityDetector:
    def __init__(self, window=250, n_trees=25):
        self._normalizer = preprocessing.MinMaxScaler()
        self._model = anomaly.HalfSpaceTrees(
            n_trees=n_trees,
            height=8,
            window_size=window,
            seed=42,
        )
        self._n_seen = 0

    def score_one(self, value: float) -> float:
        x = {"value": value}
        x_norm = self._normalizer.learn_one(x).transform_one(x)
        score = self._model.score_one(x_norm)
        self._model.learn_one(x_norm)
        self._n_seen += 1
        return score

    @property
    def is_warmed_up(self) -> bool:
        return self._n_seen >= self._model.window_size
```

### grpc.health.v1 — Python Server Registration
```python
# Source: grpc-health-probe PyPI; official grpcio health checking docs
from grpc_health.v1 import health, health_pb2_grpc, health_pb2

health_servicer = health.HealthServicer()
health_pb2_grpc.add_HealthServicer_to_server(health_servicer, server)
# Set service status to SERVING after models loaded
health_servicer.set("argus.v1.DetectorService", health_pb2.HealthCheckResponse.SERVING)
```

```csharp
// .NET health check poll before subscribing to HA events
// Source: Grpc.Net.Client docs; ARCHITECTURE.md
var healthClient = new Health.HealthClient(channel);
var response = await healthClient.CheckAsync(
    new HealthCheckRequest { Service = "argus.v1.DetectorService" },
    deadline: DateTime.UtcNow.AddSeconds(5));
if (response.Status != HealthCheckResponse.Types.ServingStatus.Serving)
    throw new InvalidOperationException("Detector not ready");
```

### openssl mTLS Cert Generation
```bash
# Source: ARCHITECTURE.md mTLS Setup; openssl official docs
# CA
openssl genrsa -out ca.key 4096
openssl req -new -x509 -key ca.key -out ca.crt -days 730 -subj "/CN=ArgusCA"

# GPU host (server cert) — CRITICAL: include both IP and hostname in SAN
cat > server-ext.cnf << EOF
subjectAltName=IP:192.168.1.100,DNS:gpu-host
EOF
openssl genrsa -out server.key 4096
openssl req -new -key server.key -out server.csr -subj "/CN=gpu-host"
openssl x509 -req -in server.csr -CA ca.crt -CAkey ca.key -CAcreateserial \
  -out server.crt -days 730 -extfile server-ext.cnf

# Edge host (client cert)
openssl genrsa -out client.key 4096
openssl req -new -key client.key -out client.csr -subj "/CN=edge-host"
openssl x509 -req -in client.csr -CA ca.crt -CAkey ca.key -CAcreateserial \
  -out client.crt -days 730
```

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `MqttFactory` (MQTTnet v4) | `MqttClientFactory` + manual reconnect (v5) | MQTTnet v5 (2023) | ManagedClient removed; reconnect is explicit |
| `grpc.experimental.aio` | `grpc.aio` | grpcio 1.32 | Old path still works but deprecated |
| `SslCredentials(caCert, ...)` (Grpc.Core) | `HttpClientHandler.ClientCertificates` (Grpc.Net.Client) | .NET 5+ | Legacy API unsupported in Grpc.Net.Client |
| NetDaemon.AppModel (addon model) | NetDaemon.Client standalone NuGet | NetDaemon 23.x | No addon required; pure NuGet use for worker service |

**Deprecated/outdated:**
- `ManagedMqttClient` (MQTTnet): removed in v5. Use raw client + Polly retry.
- `NetDaemon.AppModel` v25+: targets .NET 9+; out of range for this project.
- `grpc.SslCredentials` with non-null root/client certs in Grpc.Net.Client: never supported — explicitly documented unsupported in grpc-dotnet issue #2112.

---

## Environment Availability

> Phase 1 requires Docker, .NET 8 SDK, Python 3.12, and openssl on the developer machine for local validation. Actual runtime is two separate Docker hosts.

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Docker + Docker Compose | INFRA-03, INFRA-04, INFRA-05 | Assumed developer has it | unknown | None — required |
| .NET 8 SDK | INFRA-03 build stage | [ASSUMED] | unknown | None — required |
| Python 3.12 | INFRA-04 build + local test | [ASSUMED] | unknown | None — required |
| openssl | INFRA-06 cert generation | [ASSUMED] — standard on Linux/macOS | unknown | None — required for mTLS |
| HA instance (WebSocket) | STRM-01 integration test | [ASSUMED] — user's home setup | unknown | Mock HA for unit tests |
| MQTT broker (Zigbee2MQTT) | MQTT-01 integration | [ASSUMED] — user's home setup | unknown | Mosquitto container in docker-compose for dev |
| GPU host (Python detector runtime) | INFRA-04, STRM-03 | [ASSUMED] — user's GPU machine | unknown | Run detector locally for dev |

**Note on open questions:** Q1 (exact HA entity_ids) blocks integration testing with real HA entities but does not block Phase 1 implementation — placeholder entity_ids work for all unit and component tests. Q3 (GPU host LAN IP/hostname) blocks mTLS SAN generation — must be resolved before INFRA-06 can be completed.

---

## Open Questions (RESOLVED)

1. **Q1: Exact HA entity_ids for entities.yaml** — RESOLVED: use placeholder entity_ids during Phase 1 implementation; substitute real IDs before integration testing. All plan tasks proceed with placeholders; only integration test tasks are gated on real IDs.

2. **Q3: GPU host static LAN IP or hostname** — RESOLVED: cert generation task in Plan 03 Task 1 is flagged as `checkpoint:human-action`; executor pauses for the developer to supply the real IP/hostname before running `generate-certs.sh`. `<GPU_HOST_IP>` placeholder used until then.

3. **Q4: MQTT broker auth — username/password or client cert?** — RESOLVED: username/password per Assumption A3. `WithCredentials(user, password)` loaded from env vars in `ConnectionSettings`. Client cert MQTT auth is uncommon and would require additional broker config.

---

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Developer machine has Docker, .NET 8 SDK, Python 3.12, and openssl available | Environment Availability | Scaffold tasks fail at execution; add install steps |
| A2 | GPU host accepts gRPC connections on port 50051 (no firewall blocking) | INFRA-06 | mTLS validation (INFRA-07) blocks; plan must include firewall note |
| A3 | Zigbee2MQTT MQTT broker uses username/password auth (not client cert) | CONF-03, MQTT-03 | MQTTnet connect options wrong; easy to fix if wrong |
| A4 | River MinMaxScaler is suitable for online normalization (clips to [0,1] immediately) | STRM-03, Pattern 8 | First N readings outside [0,1] until range stabilizes; warm-up suppression handles this |
| A5 | grpc-health-probe Python package (`grpcio-health-checking`) is the right package name | INFRA-07 | Wrong package name; `pip install grpcio-health-checking` is the correct PyPI name |

---

## Sources

### Primary (HIGH confidence)
- [VERIFIED: NuGet] NetDaemon.Client 23.46.0 — .NET 8 target confirmed
- [VERIFIED: NuGet] MQTTnet 5.1.0.1559 — .NET 8, MQTT v5, ManagedClient removed
- [VERIFIED: NuGet] Grpc.Net.Client 2.80.0 — released 2026-04-30
- [VERIFIED: NuGet] Grpc.Tools 2.80.0
- [VERIFIED: PyPI] grpcio 1.81.0 — released 2026-06-01
- [VERIFIED: PyPI] river 0.25.0 — released 2026-05-31
- [VERIFIED: PyPI] pyod 3.6.0 — released 2026-06-04
- [CITED: .planning/research/STACK.md] — all versions with rationale
- [CITED: .planning/research/ARCHITECTURE.md] — bidi streaming gotchas, mTLS pattern, MQTT discovery structure
- [CITED: .planning/research/PITFALLS.md] — all Phase 1 pitfalls
- [CITED: .planning/phases/01-foundations-streaming/01-CONTEXT.md] — all locked decisions D-01 through D-21
- [CITED: grpc-dotnet issue #2112] — SslCredentials unsupported in Grpc.Net.Client (via ARCHITECTURE.md)
- [CITED: HA MQTT integration docs] — unique_id, object_id, discovery idempotency (via ARCHITECTURE.md)

### Secondary (MEDIUM confidence)
- [ASSUMED] `grpc-health-probe` Python package name is `grpcio-health-checking`
- [ASSUMED] MQTT broker uses username/password auth

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all versions NuGet/PyPI verified as of 2026-06-09
- Architecture: HIGH — patterns from official MS gRPC docs + grpcio docs; locked in CONTEXT.md
- Pitfalls: HIGH — documented in PITFALLS.md with root cause analysis; all are Phase 1 relevant
- Environment availability: LOW — developer environment not probed (greenfield on Windows dev machine targeting Linux Docker containers)

**Research date:** 2026-06-09
**Valid until:** 2026-07-09 (stable libraries; River/PyOD/grpcio have no breaking-change cadence in this window)
