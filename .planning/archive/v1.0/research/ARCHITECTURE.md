# Architecture: Argus

**Domain:** Self-hosted HA anomaly detection — hybrid .NET + Python gRPC
**Researched:** 2026-06-09
**Confidence:** HIGH (all claims verified against official docs or authoritative sources)

---

## Component Map

```
┌─────────────────────────────────────────────────────┐
│  EDGE HOST (HA box)                                  │
│                                                      │
│  ┌──────────────────────────────────────────────┐   │
│  │  Orchestrator  (.NET 8 BackgroundService)     │   │
│  │                                              │   │
│  │  ┌────────────┐   ┌────────────────────────┐ │   │
│  │  │  HA Client │   │  Batch Scheduler        │ │   │
│  │  │ (WebSocket)│   │  (PeriodicTimer)        │ │   │
│  │  └─────┬──────┘   └───────────┬────────────┘ │   │
│  │        │                      │              │   │
│  │  ┌─────▼──────────────────────▼────────────┐ │   │
│  │  │        Detection Gateway                │ │   │
│  │  │  (gRPC channel manager + retry logic)   │ │   │
│  │  └────────────────┬────────────────────────┘ │   │
│  │                   │                           │   │
│  │  ┌────────────────▼────────────────────────┐ │   │
│  │  │         MQTT Publisher                  │ │   │
│  │  │  (discovery + state updates)            │ │   │
│  │  └─────────────────────────────────────────┘ │   │
│  │                                              │   │
│  │  ┌───────────────────────────────────────┐  │   │
│  │  │  InfluxDB Reader  (batch history)     │  │   │
│  │  └───────────────────────────────────────┘  │   │
│  └──────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────┘
                         │ gRPC/mTLS (LAN)
                         ▼
┌─────────────────────────────────────────────────────┐
│  GPU HOST                                            │
│                                                      │
│  ┌──────────────────────────────────────────────┐   │
│  │  Detector Server  (Python grpcio)             │   │
│  │                                              │   │
│  │  ┌────────────────────────────────────────┐  │   │
│  │  │  Detector Registry                     │  │   │
│  │  │  (entity_id → detector instance map)   │  │   │
│  │  └────────────────────────────────────────┘  │   │
│  │                                              │   │
│  │  ┌─────────────┐  ┌──────────┐  ┌────────┐  │   │
│  │  │ RobustZScore│  │   HST    │  │  STL   │  │   │
│  │  │ /MAD (PyOD) │  │ (River)  │  │(Darts) │  │   │
│  │  └─────────────┘  └──────────┘  └────────┘  │   │
│  │                                              │   │
│  │  ┌────────────────────────────────────────┐  │   │
│  │  │  Model Store  (disk, per-entity)       │  │   │
│  │  └────────────────────────────────────────┘  │   │
│  └──────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────┘
```

### Component Responsibilities

| Component | Responsibility | Communicates With |
|-----------|---------------|-------------------|
| HA Client | Subscribe to state_changed via WebSocket, parse entity value + timestamp | Detection Gateway |
| Batch Scheduler | Fire Fit + ScoreBatch on PeriodicTimer, read history from InfluxDB | InfluxDB Reader, Detection Gateway |
| Detection Gateway | Manage the gRPC channel (single long-lived channel), wrap calls with retry + deadline, expose unavailable state on connection loss | Detector Server (gRPC) |
| MQTT Publisher | Publish discovery payloads on first-seen entity, publish state updates, retain discovery | HA (via MQTT broker) |
| InfluxDB Reader | Fetch N-period history for a given entity_id | Batch Scheduler |
| Detector Registry | Map (entity_id, detector_name) → detector instance; lazy-load model from disk | Model Store, Detector impls |
| Model Store | Atomic read/write of model files; per-entity versioned directory layout | Disk |

---

## Data Flow

### Streaming Path (< 2 s latency target)

```
HA WebSocket
    │  state_changed {entity_id, new_state, last_changed}
    ▼
HA Client (edge)
    │  ScoreStreamRequest {entity_id, value, timestamp}
    ▼ gRPC ScoreStream (bidi streaming, one long-lived call per entity)
Detector Registry (GPU)
    │  look up River HST instance for entity_id
    │  detector.score_one(value) → anomaly_score
    ▼
ScoreStreamResponse {entity_id, score, is_anomaly, detector}
    │
    ▼ (back over bidi stream to orchestrator)
Detection Gateway (edge)
    │
    ▼
MQTT Publisher (edge)
    │  topic: homeassistant/binary_sensor/argus_<slug>/state  payload: ON/OFF
    │  topic: homeassistant/sensor/argus_<slug>_score/state   payload: <float>
    ▼
HA (MQTT integration)
```

### Batch Path (scheduled interval)

```
PeriodicTimer fires (e.g., every 15 min)
    │
    ▼
Batch Scheduler
    │  for each entity in config:
    │    InfluxDB query → []float64 (N samples)
    │
    ▼ gRPC Fit {entity_id, detector, values[]}
Detector Registry (GPU)
    │  train PyOD/Darts model on history
    │  save to Model Store
    ▼
FitResponse {entity_id, detector, ok}
    │
    ▼ gRPC ScoreBatch {entity_id, detector, values[]}
Detector Registry
    │  score each sample
    ▼
ScoreBatchResponse {entity_id, scores[], is_anomaly[]}
    │
    ▼
MQTT Publisher
    │  publish latest score + flag
    ▼
HA
```

### Model Persistence Sub-flow

```
SaveModel RPC (called by Detector Registry after Fit):
    entity_id + detector → model bytes → write to
    models/<entity_slug>/<detector>/<version>/model.bin
    + models/<entity_slug>/<detector>/latest -> <version>  (symlink or marker)

LoadModel RPC (called at startup or on Detector Registry cold miss):
    read latest marker → load model bytes → deserialize
```

---

## Build Order

### Why This Order

Dependencies flow: proto contract → Python server stub → .NET client stub → each integration layer on top. MQTT discovery must exist before any state can appear in HA. The streaming path is the core value proposition and must be validated end-to-end before adding batch complexity.

```
Phase 1 — Foundation (no ML yet)
  1a. proto/argus.proto — defines all message types and service methods.
      Nothing else can be generated until this exists.
  1b. Python grpcio stubs (generated from proto).
      Detector server skeleton with Health RPC + ListDetectors stub.
  1c. .NET gRPC client stubs (generated from proto via Grpc.Tools).
      Orchestrator skeleton with GrpcChannel, no-op calls.
  1d. mTLS certificates (CA + server cert + client cert).
      Both sides must agree on certs before any real call can succeed.
  1e. MQTT Publisher with discovery payload generation.
      Must be tested standalone before wiring to detection results.

Phase 2 — Streaming Path (end-to-end, CPU only)
  2a. HA WebSocket client (state_changed subscription, parse + filter by entities.yaml).
  2b. ScoreStream bidi streaming on Python server — River HST wrapper.
  2c. Detection Gateway in orchestrator — open one ScoreStream per monitored entity,
      read loop in background Task, write loop from HA events.
  2d. Wire MQTT Publisher to ScoreStreamResponse.
  2e. Graceful degradation: channel health check → mark sensors unavailable when down.

Phase 3 — Batch Path
  3a. InfluxDB reader (Flux query, []float64 return).
  3b. Fit + ScoreBatch RPCs on Python server — PyOD RobustZScore/MAD wrappers.
  3c. Batch Scheduler (PeriodicTimer) — triggers Fit then ScoreBatch per entity.
  3d. SaveModel / LoadModel RPCs + Model Store on disk.
  3e. STL/Darts seasonal detector (only after batch path is stable).

Phase 4 — Hardening
  4a. Hysteresis / per-entity calibration.
  4b. Restart resilience (load models on startup, re-publish discovery).
  4c. Config-driven entities.yaml hot-reload.
  4d. GPU enablement (CUDA env on GPU host, no code changes if PyOD already installed).
```

**Critical dependency**: Phase 2c (Detection Gateway streaming) depends on Phase 1d (mTLS) being working. Attempting to debug bidi streaming while also debugging TLS is the most common cause of time waste in projects like this. Get TLS working with a trivial Health RPC first.

---

## gRPC Contract Notes

### Recommended Proto Structure (MEDIUM confidence — design rationale, not prescribed by spec)

```protobuf
syntax = "proto3";
package argus.v1;
option csharp_namespace = "Argus.Detector.V1";

// ---- Shared types ----

message EntitySample {
  string entity_id = 1;
  double value = 2;
  int64  timestamp_ms = 3;  // Unix ms; avoids Timestamp import complexity
}

message DetectorRef {
  string entity_id = 1;
  string detector   = 2;   // "hsTree" | "robustZScore" | "stlResidual"
}

// ---- Service ----

service DetectorService {
  rpc ListDetectors  (ListDetectorsRequest)   returns (ListDetectorsResponse);
  rpc Fit            (FitRequest)             returns (FitResponse);
  rpc ScoreBatch     (ScoreBatchRequest)      returns (ScoreBatchResponse);
  rpc ScoreStream    (stream ScoreStreamRequest) returns (stream ScoreStreamResponse);
  rpc SaveModel      (SaveModelRequest)       returns (SaveModelResponse);
  rpc LoadModel      (LoadModelRequest)       returns (LoadModelResponse);
  rpc Health         (HealthRequest)          returns (HealthResponse);
}
```

**Use the standard `grpc.health.v1` Health proto instead of a custom Health RPC.** Both Python (`grpc-health-checking` package) and .NET (`Grpc.HealthCheck`) have first-class support. The Detection Gateway can then use `HealthClient.CheckAsync` for probing.

**ScoreStream direction**: client (orchestrator) streams sensor events to GPU host; server (GPU) streams scored results back. This is the correct direction — the .NET side owns the event source (HA WebSocket) and the Python side owns the detector state. One bidi stream per entity is the simplest model; multiplexing all entities on a single stream adds sequencing complexity with no benefit on LAN.

### .NET Bidi Streaming Gotchas (HIGH confidence — official MS docs)

1. **Always dispose `AsyncDuplexStreamingCall`** — it implements `IDisposable`. Wrap in `using`.
2. **Separate read and write loops** — run the read loop in a `Task.Run` background task, write from the main async path. They must be concurrent, not sequential.
3. **Completion order matters**: call `RequestStream.CompleteAsync()` before awaiting the read task, then await the read task. Reversing this order deadlocks.
4. **Never use `Task.Result` or `Task.Wait()`** on gRPC call objects — causes thread pool starvation.
5. **Reconnect strategy**: the `GrpcChannel` auto-reconnects at the transport level, but a broken bidi stream must be detected (catch `RpcException`) and a new `AsyncDuplexStreamingCall` opened. Keep reconnect logic in the Detection Gateway, not in callers.
6. **One channel, multiple streams** — create `GrpcChannel` once at startup; create multiple stub instances from it. Channels are expensive; stubs are cheap.
7. **Deadline on bidi streams**: do not set a short deadline on long-lived ScoreStream calls. Use a `CancellationToken` tied to the application lifetime instead.

### Python grpcio Streaming Gotchas (HIGH confidence — official grpcio docs)

1. **Thread safety**: `grpc.aio` objects (asyncio API) are single-threaded; `grpc` (sync API) uses a `ThreadPoolExecutor`. For this project, the sync API with `max_workers` is simpler and sufficient — River HST is stateful per-entity but not CPU-bound.
2. **Bidi server method signature**: `def ScoreStream(self, request_iterator, context)` — iterate `request_iterator`, `yield` responses. Each connection gets its own method invocation, so per-entity state stored in `self._registry[entity_id]` is safely accessed across requests from the same stream.
3. **Blocking calls in servicer**: if Fit (PyOD training) is long-running, run it in a `ThreadPoolExecutor` via `context.run_in_executor` or just accept that gRPC will allocate a separate thread per RPC (sync server). Fit is infrequent enough that blocking is acceptable.

---

## Model Store Notes

### Disk Layout

```
/var/argus/models/
  <entity_slug>/               # slugified entity_id, e.g., sensor__outdoor_temp
    hsTree/
      v1/
        model.pkl              # River half-space tree (native pickle)
      v2/
        model.pkl
      latest                   # plain text file containing "v2"
    robustZScore/
      v1/
        model.joblib           # PyOD model via joblib.dump
      latest
    stlResidual/
      v1/
        model.pkl              # Darts model
      latest
```

**Entity slug**: replace all non-`[a-z0-9]` with `_`, lowercase. `sensor.outdoor_temperature` → `sensor__outdoor_temperature`. Use a single canonical function in the Python server to avoid drift.

### Versioning Strategy

- On each `Fit`, write to a new `v{N+1}` directory (read `latest`, parse version int, increment).
- Write model file to new directory.
- Only after successful write, overwrite `latest` file.
- Old versions are never auto-deleted in v1 — keep last 3, add cleanup as a maintenance task later.

### Concurrent Access

The gRPC sync server runs each RPC in a separate thread. Two RPCs for the same entity can race on `SaveModel` / `LoadModel`.

**Solution**: use `filelock` (PyPI package, cross-platform) with a per-entity lock file at `<entity_slug>/<detector>/.lock`. This is lightweight and avoids introducing a database.

```python
from filelock import FileLock

def save_model(entity_slug, detector, version, model):
    lock_path = f"{MODEL_ROOT}/{entity_slug}/{detector}/.lock"
    with FileLock(lock_path, timeout=10):
        # write model to versioned dir
        # update latest marker
```

`filelock` is thread-safe by default (uses `threading.local` internally). For the write-then-update-marker pattern, the lock ensures atomicity at the file level. Since the Python server is a single process, this is sufficient — no inter-process coordination needed.

---

## MQTT Discovery Notes

### Topic Structure

```
homeassistant/binary_sensor/argus_<slug>_flag/config     ← discovery payload (retained)
homeassistant/sensor/argus_<slug>_score/config           ← discovery payload (retained)

argus/<slug>/flag/state                                   ← live state: "ON" / "OFF"
argus/<slug>/score/state                                  ← live state: "23.7"
argus/<slug>/availability                                 ← "online" / "offline"
```

Two entities per source sensor (binary_sensor for flag, sensor for score) grouped under one HA device using the `device` block in the discovery payload.

### Stable `unique_id`

**Formula**: `argus_<entity_slug>_<detector>_<suffix>` where:
- `entity_slug` = slugified `entity_id` from HA (e.g., `sensor__outdoor_temperature`)
- `detector` = detector name (e.g., `hsTree`)
- `suffix` = `flag` or `score`

Example: `argus_sensor__outdoor_temperature_hsTree_flag`

**Why this is stable**: it is derived entirely from `entity_id` + `detector` — both come from `entities.yaml` which is config, not runtime state. The orchestrator can reconstruct the same `unique_id` after restart without any stored state.

**Implementation note**: `unique_id` and `object_id` (which becomes the entity_id in HA) should both be set. Set `object_id` = same slug as the `unique_id` suffix to get predictable entity_ids (`binary_sensor.argus_sensor__outdoor_temperature_hstree_flag`). Without `object_id`, HA derives the entity_id from the `name` field, which is unpredictable if the name contains Polish characters or changes.

### Discovery Idempotency

Discovery payloads are retained (`retain=True`). On reconnect, republish all discovery payloads. HA deduplicates by `unique_id`. Publishing the same payload twice is safe. Publishing to the same topic with an empty payload removes the entity — never do this except on explicit teardown.

### Availability

Each entity's discovery payload should include:
```json
{
  "availability_topic": "argus/<slug>/availability",
  "payload_available": "online",
  "payload_not_available": "offline"
}
```

The orchestrator publishes `online` on startup (after discovery) and registers an MQTT Last Will testament of `offline` on the availability topic. This ensures graceful degradation: if the orchestrator dies, HA marks the sensors unavailable within the LWT timeout.

---

## mTLS Setup

### Certificate Setup (self-signed, LAN only)

```bash
# CA
openssl genrsa -out ca.key 4096
openssl req -new -x509 -key ca.key -out ca.crt -days 3650 -subj "/CN=ArgusCA"

# GPU host (server cert) — include LAN IP in SAN
openssl genrsa -out server.key 4096
openssl req -new -key server.key -out server.csr -subj "/CN=gpu-host"
# ext file: subjectAltName=IP:192.168.x.y,DNS:gpu-host
openssl x509 -req -in server.csr -CA ca.crt -CAkey ca.key -CAcreateserial \
  -out server.crt -days 3650 -extfile server-ext.cnf

# Edge host (client cert)
openssl genrsa -out client.key 4096
openssl req -new -key client.key -out client.csr -subj "/CN=edge-host"
openssl x509 -req -in client.csr -CA ca.crt -CAkey ca.key -CAcreateserial \
  -out client.crt -days 3650
```

### Python grpcio Server

```python
with open("server.key", "rb") as f: server_key = f.read()
with open("server.crt", "rb") as f: server_crt = f.read()
with open("ca.crt",     "rb") as f: ca_crt     = f.read()

credentials = grpc.ssl_server_credentials(
    [(server_key, server_crt)],
    root_certificates=ca_crt,
    require_client_auth=True,          # mTLS: reject clients without a cert
)
server.add_secure_port("0.0.0.0:50051", credentials)
```

### .NET Grpc.Net.Client

`GrpcChannel` uses `HttpClient` internally. **Do not use `SslCredentials` with non-null arguments** — that is the legacy Grpc.Core API and is explicitly unsupported in `Grpc.Net.Client` (confirmed in grpc-dotnet issue #2112).

```csharp
// Load CA cert to validate the server's self-signed cert
var caCert = new X509Certificate2("ca.crt");

// Load client cert + key for mTLS
var clientCert = X509Certificate2.CreateFromPemFile("client.crt", "client.key");

var handler = new HttpClientHandler();
handler.ClientCertificates.Add(clientCert);

// Custom validation: accept server cert signed by our CA
handler.ServerCertificateCustomValidationCallback = (_, cert, chain, _) =>
{
    chain!.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(caCert);
    return chain.Build(cert!);
};

var channel = GrpcChannel.ForAddress(
    "https://192.168.x.y:50051",
    new GrpcChannelOptions { HttpHandler = handler });
```

**Key points**:
- `X509Certificate2.CreateFromPemFile` requires .NET 5+; available in .NET 8. (HIGH confidence — .NET docs)
- The custom `ServerCertificateCustomValidationCallback` is required because the server cert is self-signed; the OS trust store won't have it.
- Do not install the CA into the OS trust store — keep it file-based in `deploy/certs/` so certs are portable between machines.
- Cert files should be in `deploy/certs/` and mounted into both containers (or deployed alongside binaries). Never bake them into Docker images.

---

## Scheduler Coordination (Streaming vs Batch)

**The conflict**: the Batch Scheduler calls `Fit` on the same entity that has a live `ScoreStream` open. Both paths touch the same detector instance in the Python registry.

**Resolution**: Fit and ScoreStream are separate RPCs. The Python gRPC server runs each in its own thread. The Detector Registry must use a per-(entity, detector) `threading.Lock` to serialize access to the detector instance during Fit (write) vs score_one (read in ScoreStream).

Pattern:
```python
class DetectorRegistry:
    def __init__(self):
        self._detectors: dict[str, BaseDetector] = {}
        self._locks: dict[str, threading.Lock] = defaultdict(threading.Lock)

    def score_one(self, key, value):
        with self._locks[key]:       # brief — River HST score is microseconds
            return self._detectors[key].score(value)

    def fit(self, key, values):
        new_model = train(values)    # train outside the lock
        with self._locks[key]:
            self._detectors[key] = new_model   # swap atomically
```

Train outside the lock (potentially seconds for PyOD); swap inside the lock (microseconds). This keeps the ScoreStream unblocked during training.

On the .NET side, the Batch Scheduler and HA Client are separate `BackgroundService` instances. Use `Channel<T>` (System.Threading.Channels) for the streaming event queue rather than sharing mutable state. The Scheduler fires independently via `PeriodicTimer` and does not need to coordinate with the streaming path at the .NET layer — coordination is entirely in the Python registry.

---

## Confidence Assessment

| Area | Confidence | Source |
|------|------------|--------|
| .NET bidi streaming API | HIGH | Official MS docs (aspnetcore/grpc/client) |
| Python grpcio streaming API | HIGH | Official grpc.io docs |
| mTLS .NET side | HIGH | grpc-dotnet issue #2112 + MS docs |
| mTLS Python side | HIGH | Official grpcio docs |
| MQTT discovery unique_id | HIGH | HA official MQTT integration docs |
| Model store disk layout | MEDIUM | Standard MLOps pattern, no single authoritative source |
| filelock for concurrent access | HIGH | filelock PyPI docs + grpcio threading model |
| Scheduler coordination | MEDIUM | Threading patterns, derived from grpcio sync server model |
