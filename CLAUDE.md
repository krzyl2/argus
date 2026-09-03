## Project

**Argus — Home Assistant Anomaly Detection**

A self-hosted, extensible anomaly-detection system for Home Assistant sensor data. It watches environmental sensors (temperature, humidity, pressure — indoor and outdoor) and surfaces anomalies back into HA as auto-created `binary_sensor` (flag) and `sensor` (score) entities via MQTT discovery. Built by one developer for personal home automation use; no cloud, no multi-tenancy.

**Core Value:** Anomalies on v1 environmental sensors appear in HA as live binary_sensor + score entities within 2 seconds of a state_changed event, with no manual entity creation and no HA restart required.

### Constraints

- **Architecture:** .NET 8 orchestrator + Python gRPC detector — locked (D2). All ML in Python.
- **Transport:** gRPC over LAN with mTLS (D4). MQTT is documented fallback only.
- **Languages:** Code/identifiers in English; HA entity friendly-names in Polish (D8).
- **Licenses:** BSD/Apache/MIT only. No GPL, no ADTK unless isolated (MPL-2.0).
- **Hosting:** Self-hosted, no cloud (D9).
- **GPU:** Phase 3 only; Phase 1–2 are CPU-only and must work without GPU.

## Technology Stack

## Orchestrator (.NET 8)
### NetDaemon.Client — 23.46.0 (pinned)
### InfluxDB.Client — 5.0.0
### MQTTnet — 5.1.0.1559
### Grpc.Net.Client — 2.80.0
### Grpc.Tools — 2.80.0
### Supporting: Microsoft.Extensions.Hosting — (framework-provided in .NET 8)
## Detector (Python)
### grpcio — 1.81.0 + grpcio-tools — 1.81.0
### PyOD — 3.6.0
### River — 0.25.0
### Darts — 0.44.1
### joblib — 1.5.3
### statsmodels — 0.14.x (transitive via Darts)
## Deployment
### Orchestrator: mcr.microsoft.com/dotnet/runtime:8.0-jammy-chiseled
### Detector (CPU-only, Phase 1-2): python:3.12-slim-bookworm
### Detector (GPU, Phase 3): nvidia/cuda:12.4.1-runtime-ubuntu22.04 + Python 3.12
### Proto generation
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
## Installation Snapshots
### Orchestrator (Argus.Orchestrator.csproj)
### Detector (detector/requirements.txt)
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

## Conventions

Conventions not yet established. Will populate as patterns emerge during development.

## Architecture

Architecture not yet mapped. Follow existing patterns found in the codebase.

## Project Skills

No project skills found. Add skills to any of: `.claude/skills/`, `.agents/skills/`, `.cursor/skills/`, `.github/skills/`, or `.codex/skills/` with a `SKILL.md` index file.

## Searching this repo

Never run a recursive search from the repo root (`grep -rn ... .`, `rg ... .`, `find . ...`).
Root-level sweeps pull dotfiles into scope and stall on permission rules. Name the source
directories instead:

    orchestrator/ detector/ proto/ argus/ deploy/ tests/ .github/

`node_modules/`, `bin/`, `obj/`, `.planning/` and `.git/` are never search targets — exclude them
explicitly when a broad sweep is unavoidable.
