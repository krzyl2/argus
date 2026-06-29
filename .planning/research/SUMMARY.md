# Research Summary: Argus

**Project:** Argus — Home Assistant Anomaly Detection
**Domain:** Self-hosted environmental sensor anomaly detection, .NET + Python gRPC hybrid
**Researched:** 2026-06-09
**Confidence:** HIGH

---

## Recommended Stack

| Dependency | Version | Purpose |
|---|---|---|
| NetDaemon.Client | 23.46.0 | .NET 8 HA WebSocket client — state_changed subscription |
| InfluxDB.Client | 5.0.0 | Official .NET InfluxDB 2.x client — Flux queries for batch history |
| MQTTnet | 5.1.0.1559 | .NET 8 MQTT — discovery publish, state updates, LWT |
| Grpc.Net.Client + Grpc.Tools | 2.80.0 | .NET gRPC bidi streaming client + MSBuild proto codegen |
| grpcio + grpcio-tools | 1.81.0 | Python gRPC runtime + proto codegen — pin both at same version |
| pyod | 3.6.0 | RobustZScore/MAD batch detectors |
| river | 0.25.0 | HalfSpaceTrees streaming detector — primary Phase 1-2 model |
| darts | 0.44.1 | STL seasonal decomposition — Phase 3 only |
| joblib | 1.5.3 | PyOD model serialization |

Containers: Orchestrator on `mcr.microsoft.com/dotnet/runtime:8.0-jammy-chiseled`; Detector on `python:3.12-slim-bookworm` (Phase 1-2), `nvidia/cuda:12.4.1-runtime-ubuntu22.04` (Phase 3+).

**What NOT to use:** NetDaemon.Runtime/AppModel (.NET 9+ only), ML.NET (constraint D2), MQTTnet v4 ManagedClient (removed), ADTK (MPL-2.0 license violation).

---

## Table Stakes Features

Must be present in v1 or the system is not credible:

1. **Point spike detection (MAD/RobustZScore)** — most visible anomaly class
2. **Frozen/stuck sensor detection** — Zigbee sensors reporting last-known value indefinitely is common; rule-based, no ML
3. **Streaming path < 2s latency** — core value proposition
4. **MQTT discovery entities (binary_sensor + score)** — auto-create with stable `unique_id` and `retain: true`
5. **Hysteresis / anti-flap on binary sensor** — without it the system looks broken on first demo; must be Phase 1
6. **Graceful degradation to `unavailable`** — LWT-based; if detector unreachable, sensors go `unavailable` not `off`
7. **Config-driven entity registration** — entities.yaml edit only, no redeploy
8. **Polish friendly-names** — constraint D8; auto-generate from room-label map

---

## Key Architecture Decisions

**1. Proto contract is the critical path dependency.**
`proto/argus.proto` must be finalized before anything else. Fatal trap: proto3 silently drops default values — score `0.0` is not transmitted. Use `google.protobuf.FloatValue` wrapper or explicit `has_score` bool. Use `google.protobuf.Timestamp` not `int64`.

**2. One bidi gRPC stream per entity; DetectorRegistry serializes Fit vs ScoreStream with per-entity locks.**
.NET opens one long-lived `AsyncDuplexStreamingCall` per entity. Python maps `(entity_id, detector)` → detector instance. Train new model outside the lock; swap atomically. One `GrpcChannel` at startup, multiple stubs from it.

**3. MQTT entity model must be locked before detector work starts.**
`unique_id` formula: `argus_{entity_slug}_{detector}_{suffix}` — derived deterministically from config, never random. Changing it post-deployment creates orphaned HA entities. Retain on all discovery payloads. LWT `offline` on availability topic.

---

## Critical Pitfalls

Ordered by probability × impact:

1. **Hysteresis not in Phase 1 → system looks broken on first demo.** HST scores are uncalibrated; any flat threshold oscillates. Must be in Phase 1.
2. **Proto default-value drops silently corrupt pipeline.** Score `0.0` disappears in proto3. Integration test this in Phase 1, not Phase 2.
3. **MQTT unique_id instability causes orphaned entities.** Lock the formula before first publish.
4. **mTLS SAN mismatch on first cross-host connection.** Server cert must include both GPU host LAN IP and hostname as SANs. Validate with Health RPC before streaming.
5. **HA WebSocket reconnect burst triggers false anomaly cascade.** Call `get_states` on reconnect (not replay burst), suppress binary_sensor publication for 60s cooldown.

Secondary: joblib model incompatibility after Python upgrade (pin full deps + store version in sidecar); STL insufficient history (gate behind 2× seasonal period); clock skew (always use event's `last_changed`, not `DateTime.UtcNow`).

---

## Build Order Recommendation

**Phase 1 — Foundation + Streaming (CPU)**
- Finalize `proto/argus.proto`; generate stubs for both .NET and Python
- Python gRPC server: Health + ScoreStream (River HST)
- .NET orchestrator: GrpcChannel + HA WebSocket state_changed
- mTLS certs; validate with Health RPC before streaming
- MQTT Publisher: discovery, state, LWT
- HA WebSocket → ScoreStream → MQTT end-to-end
- Hysteresis, frozen detection, HA reconnect cooldown
- Graceful degradation to `unavailable`
- `entities.yaml` config

**Phase 2 — Batch Path + Model Lifecycle**
- InfluxDB reader (Flux → `[]float64`)
- `Fit` + `ScoreBatch` RPCs; PyOD RobustZScore/MAD
- Batch Scheduler (`PeriodicTimer`)
- Model Store: versioned dir layout, `filelock`, version sidecar
- `SaveModel` / `LoadModel` RPCs
- Step change detection
- Restart resilience (model load on startup, health-check before HA subscribe, re-publish discovery on restart)

**Phase 3 — Seasonality, Multivariate, Covariates**
- STL via Darts (gate on 2× seasonal period of history)
- Outdoor covariate conditioning for room sensors
- Multivariate group detectors (ECOD, IForest)
- Adaptive threshold auto-tuning

**Phase 4 — GPU + Hardening**
- GPU deep learning (PyOD AE/VAE or Darts neural)
- Per-entity calibration window, model age sensor, cert rotation, hot-reload

---

## Open Questions (Must Resolve Before Execution)

1. **Q1 — Exact HA entity_ids.** Required for `entities.yaml` and `unique_id` generation. Phase 1 cannot be integration-tested without these.
2. **Q2 — InfluxDB location, version (v1/v2/v3), and retention.** Phase 2 blocked without this. If retention < 30 days, STL is not viable.
3. **GPU host details.** Static LAN IP or hostname (needed for mTLS SAN), OS, CUDA version.
4. **MQTT broker auth.** Zigbee2MQTT broker — username/password or client cert?

---

*Synthesized: 2026-06-09*
