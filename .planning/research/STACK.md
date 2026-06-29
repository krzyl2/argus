# Stack: Argus

**Researched:** 2026-06-09
**Overall confidence:** HIGH (all versions verified against NuGet/PyPI as of research date)

---

## Orchestrator (.NET 8)

### NetDaemon.Client — 23.46.0 (pinned)
**What**: HA WebSocket client — handles auth, reconnection, event subscription, entity state access.
**Why**: The only production-ready .NET HA WebSocket client with a maintained, typed API. Provides `IHomeAssistantRunner` for background service use without the full app daemon model. The `state_changed` event subscription is a single method call. Standalone NuGet use (no addon required) is documented and supported.
**Version note**: Latest (26.x) targets .NET 10. Pin to **23.46.0**, which targets .NET 8 and is forward-compatible. Evaluate upgrading to .NET 9 after .NET 8 EOL (May 2026).
**Alternative**: `vicfergar/HassClient` (112 commits, 28 stars) — raw WebSocket wrapper, viable but far smaller community. Fall back only if NetDaemon.Client proves incompatible.
**Confidence**: HIGH — NuGet verified, .NET 8 target confirmed.

### InfluxDB.Client — 5.0.0
**What**: Official InfluxData C# client for InfluxDB 2.x. Supports Flux queries, line protocol writes, bucket/org management.
**Why**: The only official InfluxDB 2.x client for .NET. Targets .NET Standard 2.0, so works cleanly on .NET 8. Flux query API returns typed POCOs via POCO mapping or raw `FluxTable`. Async and streaming query modes available — streaming matters for large history pulls.
**Confidence**: HIGH — NuGet verified (v5.0.0, released 2026-01-13).

### MQTTnet — 5.1.0.1559
**What**: High-performance MQTT client (and optional broker). Targets .NET 8, supports MQTT v3.1.1 and v5.0.
**Why**: De-facto standard .NET MQTT library, now under the `dotnet` org. v5.x has breaking API changes vs v4 (factory split, no ManagedClient, MQTT 5 default) but is clean for new projects. MQTT discovery requires publishing retained messages with specific topic structure — MQTTnet handles this natively. Use `MqttClientFactory`, not the removed `MqttFactory`.
**Note**: ManagedClient was removed in v5. Implement reconnect logic yourself (straightforward with Polly or a retry loop on `ConnectAsync`).
**Confidence**: HIGH — NuGet verified (v5.1.0.1559, .NET 8 target confirmed).

### Grpc.Net.Client — 2.80.0
**What**: Official Google gRPC client for .NET. HTTP/2 + protobuf transport. Pairs with `Grpc.Tools` for code generation.
**Why**: First-party, maintained by Google. Integrates with .NET HttpClientFactory. mTLS is configured on the `GrpcChannel` via `HttpClientHandler.ClientCertificates` — no third-party shim needed. Targets .NET 8 explicitly.
**Confidence**: HIGH — NuGet verified (v2.80.0, released 2026-04-30).

### Grpc.Tools — 2.80.0
**What**: MSBuild integration for `.proto` → C# code generation at build time.
**Why**: Zero-friction protobuf compilation. Add `<Protobuf Include="..." GrpcServices="Client" />` in the csproj; generated stubs appear in `obj/`. No manual `protoc` invocation. Pin to same version as `Grpc.Net.Client`.
**Confidence**: HIGH — NuGet verified.

### Supporting: Microsoft.Extensions.Hosting — (framework-provided in .NET 8)
**What**: `IHostedService` / `BackgroundService` base for the worker service.
**Why**: Orchestrator runs as a .NET worker service. `BackgroundService` gives structured start/stop, DI, config, and logging out of the box. No extra package needed — ships with the Worker Service project template.
**Confidence**: HIGH.

---

## Detector (Python)

### grpcio — 1.81.0 + grpcio-tools — 1.81.0
**What**: gRPC Python runtime and protobuf code generation tooling.
**Why**: The only production gRPC implementation for Python. `grpcio-tools` compiles `.proto` → Python stubs via `python -m grpc_tools.protoc`. mTLS is first-class: pass `ssl_channel_credentials` with CA cert, server cert, and server key to `grpc.server()`. Use `grpc.aio` (asyncio) server if concurrency matters; synchronous `grpc.server` with a thread pool is fine for CPU-bound detector work.
**Confidence**: HIGH — PyPI verified (1.81.0, released 2026-06-01).

### PyOD — 3.6.0
**What**: Comprehensive anomaly detection library. 60+ detectors including MAD, COPOD, ECOD, LOF, and classical statistical methods.
**Why**: Project requires RobustZScore/MAD detection. PyOD provides `MAD` and `COPOD` detectors with the standard `fit(X)` / `decision_function(X)` / `predict(X)` API. Joblib-serializable (all detectors are sklearn-compatible estimators). v3.x adds ADEngine orchestration but the classic API is unchanged — no migration cost.
**Confidence**: HIGH — PyPI verified (3.6.0, released 2026-06-04).

### River — 0.25.0
**What**: Online (streaming) machine learning library. Learns incrementally from one sample at a time.
**Why**: Project requires `HalfSpaceTrees` — River's `river.anomaly.HalfSpaceTrees` is the reference streaming anomaly detector. Works with `learn_one(x)` / `score_one(x)` API, perfect for the `state_changed` streaming path. No batch buffering required. Native serialization via `pickle`.
**Note**: HST assumes features in `[0, 1]` by default. Requires per-feature min-max normalization in the detector service (tracked as a Phase 1 implementation detail).
**Confidence**: HIGH — PyPI verified (0.25.0, released 2026-05-31).

### Darts — 0.44.1
**What**: Time series forecasting and anomaly detection library. Includes STL decomposition, `ForecastingAnomalyModel`, and a `NormScorer` for residual-based anomaly scoring.
**Why**: Project requires STL seasonal-residual detection. Darts exposes this as `ForecastingAnomalyModel(model=StatsForecastAutoTheta(...), scorer=NormScorer())` or directly via `darts.utils.statistics.stl_decomposition`. The sklearn-like API is consistent with PyOD/River patterns. Darts is the cleanest Python path to STL-based anomaly detection without rolling a custom statsmodels pipeline.
**Note**: Darts' torch-based models are optional (CPU-only install: `pip install darts` without torch extras). Phase 1-2 use only statistical models; no GPU dependency until Phase 3.
**Confidence**: HIGH — PyPI verified (0.44.1, released 2026-05-05).

### joblib — 1.5.3
**What**: Model persistence (dump/load). Optimized for large numpy arrays.
**Why**: PyOD detectors are sklearn-compatible; `joblib.dump(detector, path)` is the canonical persistence path. Darts and River use their own serialization (`model.save()` / `pickle`) but joblib handles PyOD. Keep all persistence paths consistent: PyOD via joblib, River via pickle, Darts via `model.save()`.
**Confidence**: HIGH — PyPI verified (1.5.3, released 2025-12-15).

### statsmodels — 0.14.x (transitive via Darts)
**What**: Statistical models including STL decomposition.
**Why**: Pulled in by Darts as a dependency. No direct use needed — access STL via `darts.utils.statistics` instead of raw statsmodels. Pinning is handled by Darts' requirements.
**Confidence**: HIGH.

---

## Deployment

### Orchestrator: mcr.microsoft.com/dotnet/runtime:8.0-jammy-chiseled
**What**: Microsoft's distroless Ubuntu 22.04 base for .NET 8 runtime. ~85 MB.
**Why**: Worker service has no ASP.NET surface area — use `runtime`, not `aspnet`. Chiseled (distroless) variant minimizes attack surface and image size. Multi-stage build: SDK image for build, runtime-chiseled for final stage.
**Confidence**: HIGH — Microsoft docs verified.

### Detector (CPU-only, Phase 1-2): python:3.12-slim-bookworm
**What**: Official Python 3.12 slim image on Debian Bookworm. ~130 MB base.
**Why**: No GPU needed in Phase 1-2. Slim avoids dev tools bloat. Bookworm (Debian 12) is the current stable. PyOD, River, and Darts (statistical-only install) have no native CUDA dependency.
**Confidence**: HIGH.

### Detector (GPU, Phase 3): nvidia/cuda:12.4.1-runtime-ubuntu22.04 + Python 3.12
**What**: NVIDIA CUDA 12.4 runtime base on Ubuntu 22.04, with Python 3.12 installed manually.
**Why**: Phase 3 adds GPU-accelerated detectors (Darts deep models, Anomalib). CUDA 12.4 is the current stable LTS-aligned version with broad PyTorch wheel support (`+cu124`). Use `-runtime-` not `-devel-` to keep image size down. Install Python via `apt` or deadsnakes PPA.
**Note**: Not needed until Phase 3. Defer image selection to that phase — CUDA compatibility matrix shifts.
**Confidence**: MEDIUM — NVIDIA NGC verified for CUDA 12.4 existence; specific image tag and Python install method need Phase 3 validation.

### Proto generation
**What**: Protobuf IDL lives in `proto/` at mono-repo root.
**Why**: Single source of truth for the .NET client stubs (`Grpc.Tools`) and Python server stubs (`grpcio-tools`). Both code-gen pipelines reference the same `.proto` files. Compile in CI; do not check in generated files.
**Confidence**: HIGH.

---

## What NOT to Use

| What | Why Not |
|------|---------|
| `NetDaemon.Runtime` / `NetDaemon.AppModel` (v25+) | App model framework adds excessive overhead; targets .NET 9+. Use `NetDaemon.Client` standalone. |
| ML.NET | Explicitly out of scope (PROJECT.md constraint D2). All ML is Python. |
| `grpc.experimental.aio` (old import path) | Replaced by `grpc.aio` since grpcio 1.32. Use `import grpc.aio`. |
| MQTTnet v4.x (`ManagedClient`) | ManagedClient removed in v5. New projects start on v5 directly. |
| ADTK (anomaly detection toolkit) | MPL-2.0 license — excluded by license constraint (BSD/Apache/MIT only). |
| `influxdb` (Python, v1.x client) | Wrong version; project uses InfluxDB 2.x. Use `influxdb-client` (Python) or `InfluxDB.Client` (.NET). |
| Direct Recorder DB access | Explicitly prohibited in PROJECT.md — use HA WebSocket + InfluxDB only. |
| Anomalib | GPU-only, deferred to post-Phase 3 if camera data is added (PROJECT.md out of scope). |

---

## Installation Snapshots

### Orchestrator (Argus.Orchestrator.csproj)
```xml
<PackageReference Include="NetDaemon.Client" Version="23.46.0" />
<PackageReference Include="InfluxDB.Client" Version="5.0.0" />
<PackageReference Include="MQTTnet" Version="5.1.0.1559" />
<PackageReference Include="Grpc.Net.Client" Version="2.80.0" />
<PackageReference Include="Grpc.Tools" Version="2.80.0" PrivateAssets="All" />
```

### Detector (detector/requirements.txt)
```
grpcio==1.81.0
grpcio-tools==1.81.0
pyod==3.6.0
river==0.25.0
darts==0.44.1
joblib==1.5.3
```

---

## Sources

- [NetDaemon.Client 23.46.0 on NuGet](https://www.nuget.org/packages/NetDaemon.Client/23.46.0) — .NET 8 target confirmed
- [NetDaemon.Client 26.21.0 on NuGet](https://www.nuget.org/packages/NetDaemon.Client) — latest targets .NET 10
- [InfluxDB.Client 5.0.0 on NuGet](https://www.nuget.org/packages/InfluxDB.Client/) — InfluxDB 2.x support confirmed
- [MQTTnet 5.1.0.1559 on NuGet](https://www.nuget.org/packages/MQTTnet) — .NET 8 target, MQTT v5 confirmed
- [Grpc.Net.Client 2.80.0 on NuGet](https://www.nuget.org/packages/grpc.net.client) — mTLS support documented
- [Grpc.Tools 2.80.0 on NuGet](https://www.nuget.org/packages/grpc.tools/)
- [grpcio 1.81.0 on PyPI](https://pypi.org/project/grpcio/) — released 2026-06-01
- [PyOD 3.6.0 on PyPI](https://pypi.org/project/pyod/) — released 2026-06-04
- [River 0.25.0 on PyPI](https://pypi.org/project/river/) — released 2026-05-31
- [Darts 0.44.1 on PyPI](https://pypi.org/project/darts/) — released 2026-05-05
- [joblib 1.5.3 on PyPI](https://pypi.org/project/joblib/) — released 2025-12-15
- [NetDaemon Client API docs](https://netdaemon.xyz/docs/user/advanced/advanced_client/) — standalone use without addon confirmed
- [MQTTnet v5 migration notes](https://github.com/dotnet/MQTTnet/wiki/Upgrading-guide) — ManagedClient removal confirmed
- [Official .NET Docker images](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/net-core-net-framework-containers/official-net-docker-images)
