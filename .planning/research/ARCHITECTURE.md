# Architecture Research

**Domain:** Home Assistant Add-on — single container wrapping two long-running processes
**Researched:** 2026-06-29
**Confidence:** HIGH (derived directly from reading the live codebase; no speculative gaps)

---

## System Overview

```
┌─────────────────────────────────────────────────────────────────────────┐
│  HA Supervisor                                                           │
│  /data/options.json  →  [cont-init.d/10-config-gen.sh]                  │
│  SUPERVISOR_TOKEN   ─┘   ↓ writes                                       │
│  MQTT service        ─┘   ├─ /data/entities.yaml                        │
│                           ├─ s6 container env (ARGUS_* + HomeAssistant__*)│
│                           └─ /etc/services.d/detector/down (remote mode) │
│                                                                          │
│  ┌──────────────────────────┐    loopback gRPC (http, no TLS)           │
│  │  services.d/detector     │ ←─────────────────────────────────────────┐│
│  │  python -m argus_detector│                                           ││
│  │  .server                 │   LOCAL MODE ONLY                         ││
│  │  binds: 127.0.0.1:50051  │                                           ││
│  └──────────────────────────┘                                           ││
│                                                                         ││
│  ┌──────────────────────────┐                                           ││
│  │  services.d/orchestrator │ ──────────────────────────────────────────┘│
│  │  dotnet Argus.Orchestrator│    (polls health, then connects)          │
│  │  .dll                    │                                            │
│  └──────────────────────────┘                                            │
│         │                  │                                             │
│         ↓ MQTT             ↓ HA WebSocket (supervisor proxy)            │
│  [Zigbee2MQTT broker]  [homeassistant:8123]                             │
└─────────────────────────────────────────────────────────────────────────┘

REMOTE MODE: detector service is downed (down file); orchestrator dials
             external ARGUS_DETECTOR_ENDPOINT with mTLS (existing code path).
```

---

## Component Inventory: New vs Modified

### New components (add-on packaging)

| Component | Path | Type | Description |
|-----------|------|------|-------------|
| Add-on manifest | `addon/config.yaml` | NEW | HA add-on descriptor: slug, name, options schema, `hassio_roles: mqtt`, `homeassistant_api: true`, `map: [data:rw]` |
| Build manifest | `addon/build.yaml` | NEW | Multi-arch targets: `amd64`, `aarch64`; base image refs |
| Custom repo descriptor | `repository.yaml` | NEW | Sits at repo root; consumed by HA "Add custom repository" UI |
| Addon Dockerfile | `addon/Dockerfile` | NEW | Multi-stage: builds .NET orchestrator, installs Python deps, combines into HA base image |
| Config-gen init script | `addon/rootfs/etc/cont-init.d/10-config-gen.sh` | NEW | Oneshot: reads options.json → env vars + entities.yaml |
| Entities gen helper | `addon/rootfs/usr/local/bin/gen-entities.py` | NEW | Converts options.json entity list to entities.yaml YAML format |
| Detector s6 service | `addon/rootfs/etc/services.d/detector/run` | NEW | Starts Python detector (local mode) or is pre-downed (remote mode) |
| Orchestrator s6 service | `addon/rootfs/etc/services.d/orchestrator/run` | NEW | Waits for detector health gate (local mode), then starts .NET host |
| Health poller | `addon/rootfs/usr/local/bin/wait-detector.py` | NEW | Polls gRPC health endpoint until SERVING; used by orchestrator run script |

### Modified components (existing codebase)

| Component | File | Change Required | Reason |
|-----------|------|----------------|--------|
| Channel factory | `orchestrator/Argus.Orchestrator/Detection/DetectorChannelFactory.cs` | Conditional insecure path | Currently always throws if TLS vars absent (T-04-03 comment); must add loopback → insecure channel branch |
| Program.cs startup | `orchestrator/Argus.Orchestrator/Program.cs` | Skip TLS validation in local mode | Line 57-63 registers channel unconditionally via `DetectorChannelFactory.Create`; needs to detect mode before calling factory |
| Detector config | `detector/argus_detector/config.py` | Add `ARGUS_GRPC_BIND`, `ARGUS_MODEL_ROOT` env vars | Bind address and model root are currently hardcoded |
| Detector server | `detector/argus_detector/server.py` | Use `config.grpc_bind` and `config.model_root` | `[::]` bind and `/var/argus/models` path need to be configurable |

---

## s6 Service Layout

HA add-on base images (`ghcr.io/hassio-addons/base`) use the legacy s6-overlay abstraction — `cont-init.d` + `services.d` — not full s6-rc bundles. Do not introduce `s6-rc.d`; use the standard HA conventions.

```
addon/rootfs/
├── etc/
│   ├── cont-init.d/
│   │   └── 10-config-gen.sh        # oneshot: runs before any service
│   └── services.d/
│       ├── detector/
│       │   ├── run                 # start detector OR be pre-downed
│       │   └── finish              # (optional) prevent restart on deliberate exit
│       └── orchestrator/
│           ├── run                 # poll detector health, then start .NET
│           └── finish              # (optional)
└── usr/local/bin/
    ├── gen-entities.py             # options.json → entities.yaml conversion
    └── wait-detector.py            # gRPC health poll loop
```

### Ordering and dependencies

s6 starts `services.d` services in parallel. Ordering is handled two ways:

1. **config-gen runs first** because it is in `cont-init.d/`, which s6 processes entirely before starting any `services.d` service. Numbering (`10-`) controls ordering among multiple init scripts.

2. **Orchestrator waits for detector** via a blocking poll in `services.d/orchestrator/run` — not via s6 dependency declarations (s6-overlay legacy mode does not support `dependencies.d`).

### Conditional service activation: the `down` file pattern

The config-gen script writes `/etc/services.d/detector/down` when remote mode is active. When s6 sees a `down` file on startup, it does not start that service. This is the canonical s6 way to disable a service; no run-script conditionality needed.

```bash
# In cont-init.d/10-config-gen.sh (pseudocode):
if [ -n "$(bashio::config 'detector_endpoint')" ]; then
    # Remote mode: disable local detector
    touch /etc/services.d/detector/down
fi
```

### Detector readiness gate

The orchestrator `run` script must not start until the local detector transitions to SERVING. Use a small Python script (Python is already in the container — no extra binary):

```python
# /usr/local/bin/wait-detector.py
import grpc, sys, time
from grpc_health.v1 import health_pb2, health_pb2_grpc

addr = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1:50051"
svc  = sys.argv[2] if len(sys.argv) > 2 else "argus.v1.DetectorService"
while True:
    try:
        ch = grpc.insecure_channel(addr)
        stub = health_pb2_grpc.HealthStub(ch)
        r = stub.Check(health_pb2.HealthCheckRequest(service=svc), timeout=2)
        if r.status == health_pb2.HealthCheckResponse.SERVING:
            sys.exit(0)
    except Exception:
        pass
    time.sleep(1)
```

`services.d/orchestrator/run`:
```bash
#!/usr/bin/with-contenv bashio

if [ "$(cat /run/argus/mode)" = "local" ]; then
    bashio::log.info "Waiting for local detector to be SERVING..."
    python3 /usr/local/bin/wait-detector.py 127.0.0.1:50051 argus.v1.DetectorService
fi

exec dotnet /opt/argus/orchestrator/Argus.Orchestrator.dll
```

---

## Conditional mTLS: Orchestrator Changes

### The problem

`DetectorChannelFactory.cs` lines 28–35 unconditionally throw `ArgumentException` if any of `TlsCa`, `TlsCert`, `TlsKey`, or `DetectorEndpoint` is null. The comment `T-04-03: No insecure GrpcChannel path` is a v1 constraint that the v2 milestone explicitly overrides (D4 conditional).

### Required change: `DetectorChannelFactory.cs`

Add an `IsLocalMode()` helper and an early-return insecure branch:

```csharp
public static GrpcChannel Create(ConnectionSettings settings, ...)
{
    if (string.IsNullOrWhiteSpace(settings.DetectorEndpoint))
        throw new ArgumentException("ARGUS_DETECTOR_ENDPOINT must be set");

    if (IsLocalMode(settings.DetectorEndpoint))
    {
        // Local loopback: insecure channel, no TLS certs needed
        // gRPC insecure requires http:// scheme (not https://)
        return GrpcChannel.ForAddress(settings.DetectorEndpoint,
            new GrpcChannelOptions());
    }

    // Remote path: existing mTLS logic unchanged (lines 28–66)
    if (string.IsNullOrWhiteSpace(settings.TlsCa))
        throw new ArgumentException("ARGUS_TLS_CA must be set (path to ca.crt)");
    // ... rest unchanged
}

private static bool IsLocalMode(string endpoint) =>
    Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
    && (uri.Scheme == "http"            // explicit insecure scheme
        || uri.Host == "127.0.0.1"
        || uri.Host == "localhost"
        || uri.Host == "::1");
```

The scheme check (`http://`) is the primary discriminator; the host check is a safety net. Config-gen always writes `http://127.0.0.1:50051` for local mode, `https://...` for remote.

### Required change: `Program.cs`

The `GrpcChannel` singleton registration (lines 59–63) passes `connectionSettings` to `DetectorChannelFactory.Create` unconditionally. No change needed in `Program.cs` since the factory now handles both paths internally — the factory itself validates what it needs per mode.

The TLS path validations that currently live in `Program.cs` (BatchIntervalMinutes/NightlyFitHour, lines 46–53) do not touch TLS — those stay. The TLS validation responsibility stays in the factory.

---

## Detector Local-Mode Changes

### `config.py`

Add two new env vars:

```python
self.grpc_bind: str = os.environ.get("ARGUS_GRPC_BIND", "[::]")
self.model_root: str = os.environ.get("ARGUS_MODEL_ROOT", "/var/argus/models")
```

`grpc_bind` defaults to `[::]` (existing behaviour for remote/compose deployments). Config-gen sets it to `127.0.0.1` in local mode.

### `server.py`

Two changes:

1. Replace hardcoded `[::]` with `config.grpc_bind`:

```python
# insecure path (line 112):
server.add_insecure_port(f"{config.grpc_bind}:{port}")

# secure path (line 106):
server.add_secure_port(f"{config.grpc_bind}:{port}", server_credentials)
```

2. In `serve()`, pass `model_root` from config so `/data/models` is used in the add-on:

```python
def serve() -> None:
    config = DetectorConfig()
    configure_logging(config.log_level)
    server = create_server(
        port=config.grpc_port,
        config=config,
        model_root=pathlib.Path(config.model_root),  # NEW
    )
    server.start()
    server.wait_for_termination()
```

`MODEL_ROOT` module-level constant in `model_store.py` stays as-is; only `serve()` overrides it via the constructor argument.

---

## Config-Gen Entrypoint: `cont-init.d/10-config-gen.sh`

This script is the central integration point between HA Supervisor and the two processes. It runs once at container startup, before s6 starts any supervised service.

### HA auth injection

NetDaemon.Client (`AddHomeAssistantClient()`) reads from the `HomeAssistant:*` IConfiguration section, which maps to `HomeAssistant__*` env vars in .NET. The Supervisor internal proxy endpoint is used:

```bash
# NetDaemon.Client config (IConfiguration / HomeAssistant__ prefix)
printf "supervisor"  > /var/run/s6/container_environment/HomeAssistant__Host
printf "80"          > /var/run/s6/container_environment/HomeAssistant__Port
printf "false"       > /var/run/s6/container_environment/HomeAssistant__Ssl
printf "%s" "${SUPERVISOR_TOKEN}" > /var/run/s6/container_environment/HomeAssistant__Token

# ARGUS_HA_* for ConnectionSettings (used by HA event source / other consumers)
printf "ws://supervisor/core/api/websocket" > /var/run/s6/container_environment/ARGUS_HA_URL
printf "%s" "${SUPERVISOR_TOKEN}"           > /var/run/s6/container_environment/ARGUS_HA_TOKEN
```

Writing to `/var/run/s6/container_environment/` is the s6 mechanism for persisting env vars to supervised services. Service run scripts using `#!/usr/bin/with-contenv bashio` automatically inherit these.

### MQTT Supervisor service discovery

```bash
MQTT_HOST=$(bashio::services mqtt "host")
MQTT_PORT=$(bashio::services mqtt "port")
MQTT_USER=$(bashio::services mqtt "username")
MQTT_PASS=$(bashio::services mqtt "password")

printf "%s" "${MQTT_HOST}" > /var/run/s6/container_environment/ARGUS_MQTT_HOST
printf "%s" "${MQTT_PORT}" > /var/run/s6/container_environment/ARGUS_MQTT_PORT
printf "%s" "${MQTT_USER}" > /var/run/s6/container_environment/ARGUS_MQTT_USER
printf "%s" "${MQTT_PASS}" > /var/run/s6/container_environment/ARGUS_MQTT_PASSWORD
```

Requires `hassio_roles: [mqtt]` in `addon/config.yaml`.

### Mode detection and detector env

```bash
DETECTOR_EP=$(bashio::config 'detector_endpoint' || echo "")

if [ -z "${DETECTOR_EP}" ]; then
    # LOCAL MODE
    printf "http://127.0.0.1:50051" > /var/run/s6/container_environment/ARGUS_DETECTOR_ENDPOINT
    printf "127.0.0.1"             > /var/run/s6/container_environment/ARGUS_GRPC_BIND
    printf "/data/models"          > /var/run/s6/container_environment/ARGUS_MODEL_ROOT
    printf "local"                 > /run/argus/mode
    # NO TLS vars set → detector starts insecure, factory takes insecure path
else
    # REMOTE MODE
    printf "%s" "${DETECTOR_EP}"   > /var/run/s6/container_environment/ARGUS_DETECTOR_ENDPOINT
    printf "/data/certs/ca.crt"   > /var/run/s6/container_environment/ARGUS_TLS_CA
    printf "/data/certs/client.crt"> /var/run/s6/container_environment/ARGUS_TLS_CERT
    printf "/data/certs/client.key"> /var/run/s6/container_environment/ARGUS_TLS_KEY
    printf "/data/models"          > /var/run/s6/container_environment/ARGUS_MODEL_ROOT
    printf "remote"                > /run/argus/mode
    # Disable local detector
    touch /etc/services.d/detector/down
fi
```

### InfluxDB and other ARGUS_* vars

Straightforward mapping from `bashio::config 'influx_url'` etc. to `/var/run/s6/container_environment/ARGUS_INFLUX_*`.

### entities.yaml generation

```bash
ARGUS_ENTITIES_PATH="/data/entities.yaml"
printf "%s" "${ARGUS_ENTITIES_PATH}" > /var/run/s6/container_environment/ARGUS_ENTITIES_PATH

python3 /usr/local/bin/gen-entities.py /data/options.json > /data/entities.yaml
```

`gen-entities.py` reads the `entities` array from options.json and writes the entities.yaml YAML structure the orchestrator expects. This file is written to `/data/` (persistent) so the user can inspect it via the HA file editor if needed.

---

## Data Persistence Under /data

| Path | Contents | Notes |
|------|----------|-------|
| `/data/options.json` | Add-on configuration | Written by Supervisor; read-only for add-on |
| `/data/entities.yaml` | Generated entity config | Regenerated at each start from options.json; safe to overwrite |
| `/data/models/` | Trained detector models | Persisted across restarts and add-on updates; was `/var/argus/models/` in v1 |
| `/data/certs/` | mTLS certs (remote mode) | User places `ca.crt`, `client.crt`, `client.key` here; only needed in remote mode |

The `/data` volume is mounted by Supervisor automatically for all add-ons with `map: [data:rw]` in config.yaml.

---

## Addon Dockerfile Structure

Multi-stage build combining .NET 8 runtime and Python 3.12 on the HA base image:

```dockerfile
# Stage 1: build orchestrator
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-orchestrator
WORKDIR /src
COPY orchestrator/ .
RUN dotnet publish Argus.Orchestrator/Argus.Orchestrator.csproj \
    -c Release -r linux-x64 --self-contained false -o /out/orchestrator

# Stage 2: detector Python deps (cached layer)
FROM python:3.12-slim-bookworm AS build-detector
WORKDIR /detector
COPY detector/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Stage 3: HA add-on base + combine
ARG BUILD_FROM=ghcr.io/hassio-addons/base:latest
FROM ${BUILD_FROM}

# .NET 8 runtime (Alpine-compatible)
RUN apk add --no-cache dotnet8-runtime

# Python 3.12 + pip
RUN apk add --no-cache python3 py3-pip

# Copy Python site-packages from Stage 2
COPY --from=build-detector /usr/local/lib/python3.12/site-packages \
     /usr/local/lib/python3.12/site-packages

# Copy detector source
COPY detector/ /opt/argus/detector/

# Copy orchestrator publish output
COPY --from=build-orchestrator /out/orchestrator /opt/argus/orchestrator/

# Copy s6 service definitions and scripts
COPY addon/rootfs /

CMD ["/init"]
```

**Multi-arch note:** `build.yaml` specifies both `amd64` and `aarch64`. The `BUILD_FROM` ARG is substituted by the HA build system with the arch-appropriate base image. The .NET publish target (`linux-x64` above) must be `linux-arm64` for aarch64 — handle this with build args or a build matrix.

**Image size warning:** Darts 0.44.1 pulls PyTorch by default — the image will be 3–5 GB. Use `pip install "darts[optional]"` pattern or explicitly exclude torch in requirements.txt for Phase 1-2 CPU-only builds. See PITFALLS.md.

---

## add-on config.yaml (Options Schema)

```yaml
name: "Argus Anomaly Detection"
slug: argus
version: "2.0.0"
description: "Anomaly detection for HA sensor data"
arch: [amd64, aarch64]
startup: application
boot: auto
map:
  - data:rw
homeassistant_api: true
hassio_api: true
hassio_roles:
  - mqtt
options:
  entities: []
  detector_endpoint: ""
  influx_url: ""
  influx_token: ""
  influx_org: ""
  influx_bucket: ""
  influx_measurement: "homeassistant"
  influx_value_field: "value"
  batch_interval_minutes: 10
  nightly_fit_hour: 2
schema:
  entities:
    - entity_id: str
      detectors: [str]
  detector_endpoint: str?
  influx_url: str?
  influx_token: str?
  influx_org: str?
  influx_bucket: str?
  influx_measurement: str
  influx_value_field: str
  batch_interval_minutes: int(1,1440)
  nightly_fit_hour: int(0,23)
```

---

## Data Flow: Local Mode Startup Sequence

```
Supervisor writes /data/options.json
    ↓
s6 runs cont-init.d/10-config-gen.sh
    ├── writes ARGUS_* to /var/run/s6/container_environment/
    ├── writes HomeAssistant__* to /var/run/s6/container_environment/
    ├── writes /data/entities.yaml
    └── writes /run/argus/mode = "local"
    ↓
s6 starts services.d/ in parallel:
    ├── detector/run  →  python3 -m argus_detector.server
    │       (binds 127.0.0.1:50051 insecure, loads /data/models/)
    │       (sets health NOT_SERVING → loads models → sets SERVING)
    └── orchestrator/run  →  wait-detector.py polls 127.0.0.1:50051
            (blocks until SERVING)
            ↓
        dotnet Argus.Orchestrator.dll
            (DetectorChannelFactory detects http:// → insecure GrpcChannel)
            (connects NetDaemon.Client to supervisor:80)
            (connects MQTT to Supervisor-discovered broker)
```

## Data Flow: Remote Mode Startup Sequence

```
cont-init.d/10-config-gen.sh
    ├── writes ARGUS_DETECTOR_ENDPOINT = https://gpu-host:50051
    ├── writes ARGUS_TLS_* paths to /data/certs/
    └── touch /etc/services.d/detector/down   ← disables local detector
    ↓
s6 starts services.d/:
    ├── detector/  →  NOT STARTED (down file present)
    └── orchestrator/run  →  /run/argus/mode = "remote" → no health poll
            ↓
        dotnet Argus.Orchestrator.dll
            (DetectorChannelFactory detects https:// → existing mTLS path)
```

---

## Integration Points Summary

| Integration | v1 Mechanism | v2 Change |
|-------------|-------------|-----------|
| HA WebSocket | `ARGUS_HA_URL` + `ARGUS_HA_TOKEN` env vars (manual) | Auto-injected: `HomeAssistant__Host=supervisor`, `HomeAssistant__Token=${SUPERVISOR_TOKEN}` |
| MQTT | `ARGUS_MQTT_*` env vars (manual) | Auto-discovered via `bashio::services mqtt` |
| Detector channel | Always mTLS (`DetectorChannelFactory` throws if no certs) | Conditional: `http://127.0.0.1` → insecure; `https://` → mTLS (unchanged code path) |
| Model storage | `/var/argus/models/` (hardcoded in `model_store.py`) | `/data/models/` via `ARGUS_MODEL_ROOT` env var → `config.model_root` → `serve()` passes to `ModelStore` |
| Detector bind address | `[::]` hardcoded | `ARGUS_GRPC_BIND` env var; `127.0.0.1` in local mode |
| entities.yaml | Manually authored file | Generated at startup from `options.json` by `gen-entities.py` |
| mTLS certs | `deploy/certs/` volume mount | `/data/certs/` (user-managed, remote mode only) |

---

## Suggested Phase Build Order

Order driven by dependencies: each phase must not assume anything the previous phase hasn't delivered.

| Phase | Name | Deliverable | Depends On |
|-------|------|-------------|------------|
| 1 | Add-on skeleton | `repository.yaml`, `addon/config.yaml`, `addon/build.yaml`, bare `addon/Dockerfile` (no processes yet), validates HA can install and render config UI | Nothing — pure packaging |
| 2 | Config-gen entrypoint | `cont-init.d/10-config-gen.sh`, `gen-entities.py`, s6 env var injection, `down` file logic, `/data/entities.yaml` generation | Phase 1 (Dockerfile exists to embed scripts) |
| 3 | Conditional channel factory | Modify `DetectorChannelFactory.cs` (loopback → insecure), unit tests for both paths | Phase 2 (need to know what env vars config-gen sets) |
| 4 | Detector local-mode bind | Add `ARGUS_GRPC_BIND` + `ARGUS_MODEL_ROOT` to `config.py`; update `server.py` bind address and `serve()` model_root pass-through; unit tests | Phase 2 (env vars defined in config-gen) |
| 5 | s6 service wiring + health gate | `services.d/detector/run`, `services.d/orchestrator/run`, `wait-detector.py`; integration test: both processes start, gRPC call succeeds | Phases 3 and 4 (both processes must support the local-mode changes) |
| 6 | Multi-arch Dockerfile + CI | Finalize `addon/Dockerfile` with multi-arch `BUILD_FROM` ARG, GitHub Actions workflow, push to GHCR | Phase 5 (all runtime components stable) |
| 7 | End-to-end HA integration | Install from custom repo, configure entities, verify streaming + batch paths in live HA | Phase 6 (publishable image) |

---

## Anti-Patterns to Avoid

### Avoid: Full s6-rc bundle management

Using `s6-rc.d/` bundles and `dependencies.d/` for conditional service activation is unnecessary complexity for an HA add-on. The `down` file pattern + `wait-detector.py` poll achieves the same result with less risk of HA base image incompatibility.

### Avoid: Hardcoding Supervisor's internal URLs

The internal HA WebSocket URL (`ws://supervisor/core/api/websocket`) is not the same as the external URL (`ws://homeassistant.local:8123`). `NetDaemon.Client` takes Host+Port separately — not a full URL. Set `HomeAssistant__Host=supervisor` and `HomeAssistant__Port=80`, not a full URL in `HomeAssistant__Host`.

### Avoid: `grpc.aio` in the health poller

The `wait-detector.py` script should use synchronous `grpc` (not `grpc.aio`). An async loop for a startup poller is unnecessary complexity and may behave differently across Python versions.

### Avoid: Persistent entities.yaml checked into /data

The entities.yaml at `/data/entities.yaml` must be treated as a generated artefact and overwritten on every start. If it is persisted and the user adds entities in options.json, the old file would be stale on the next start. Always regenerate.

### Avoid: Torch in the add-on image

Darts 0.44.1 pulls PyTorch when installed without extras restrictions. A naive `pip install darts` produces a 3–5 GB image. Pin the Darts extras in `requirements.txt` to avoid torch in Phases 1–2.

---

## Open Questions

- **NetDaemon.Client `HomeAssistant__*` config key names**: Confirmed from NetDaemon docs that the config section is `HomeAssistant` and the keys are `Host`, `Port`, `Ssl`, `Token`. The exact internal Supervisor proxy hostname (`supervisor` vs `homeassistant`) should be verified against a live HA OS installation before Phase 5.
- **`ARGUS_HA_URL` / `ARGUS_HA_TOKEN` consumers**: `ConnectionSettings.HaUrl` and `HaToken` are set in `Program.cs` but their downstream consumers beyond `ConnectionSettings` storage are not visible in the files read. Verify whether `NetDaemonHaEventSource` reads these or only reads via `IHomeAssistantClient` (which gets its config from `HomeAssistant__*`). If only the latter, `ARGUS_HA_URL/TOKEN` env vars can be dropped from config-gen.
- **entities.yaml schema**: `EntitiesConfigLoader` was not read. `gen-entities.py` must match the exact YAML structure the loader expects. Resolve in Phase 2 by reading the loader before implementing the generator.

---

*Architecture research for: Argus v2.0 Home Assistant Add-on*
*Researched: 2026-06-29*
