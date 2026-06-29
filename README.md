# Argus — Home Assistant Anomaly Detection

A self-hosted anomaly-detection system for Home Assistant sensor data. Watches environmental sensors and surfaces anomalies as HA `binary_sensor` (flag) and `sensor` (score) entities via MQTT discovery.

## Repository Layout

| Directory | Purpose |
|-----------|---------|
| `proto/` | Single-source-of-truth gRPC contract (`argus.proto`). Both sides compile from this file. |
| `orchestrator/` | .NET 8 worker service — HA WebSocket ingestion, hysteresis gate, MQTT discovery egress. |
| `detector/` | Python gRPC server — River HalfSpaceTrees streaming anomaly detection. |
| `deploy/` | Docker Compose files, mTLS cert generation scripts, deployment config. |

## Build

### .NET Orchestrator

```bash
dotnet build orchestrator/Argus.Orchestrator.sln
dotnet test orchestrator/Argus.Orchestrator.sln
```

Proto stubs are generated automatically at build time via `Grpc.Tools`.

### Python Detector

```bash
# Install dependencies
pip install -r detector/requirements.txt

# Generate Python proto stubs (run once, or after proto changes)
python detector/scripts/gen_proto.py

# Run tests
python -m pytest detector/tests/
```
