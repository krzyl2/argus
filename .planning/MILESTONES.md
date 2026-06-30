# Milestones — Argus

## v2.0 Home Assistant Add-on (Shipped: 2026-06-30)

**Phases completed:** 4 phases, 10 plans, 5 tasks

**Key accomplishments:**

- repository.yaml
- gen-entities.py
- Task 1 — argus/Dockerfile
- Scheme-based conditional channel security — http://127.0.0.1 → insecure h2c (zero certs), https:// → existing mTLS path byte-for-byte unchanged.
- ARGUS_GRPC_BIND and ARGUS_MODEL_ROOT env vars added to DetectorConfig; server.py consumes both with backward-compatible [::] / /var/argus/models defaults.
- wait-detector.py
- Supervisor API credential fetch (GET /services/mqtt with Bearer token) wired into MqttConnection per-attempt via IMqttCredentialSource, with env-var fallback and no secret logging
- 1. [Rule 1 - Bug] DetectionGateway namespace collision
- Two-job GitHub Actions workflow — QEMU single-step `docker/build-push-action@v6` (amd64+arm64) with `dotnet publish` pre-step, followed by an image-facts gate asserting both arches present, torch-free, and compressed size < 2 GB.
- HA add-on DOCS.md with Mosquitto `mqtt:need` prerequisite, custom-repo install steps, all 13 config.yaml schema fields with defaults/semantics, and `binary_sensor.argus_addon_health` troubleshooting.

---

## v1.0 — Foundations + Batch & Model Lifecycle ✓ (2026-06-10)

Self-hosted Home Assistant anomaly detection: .NET 8 orchestrator + Python gRPC detector.

**Shipped:**

- Phase 1 — Foundations + Streaming (8 plans): mono-repo scaffold, mTLS gRPC, HA WebSocket ingestion, River HST streaming detector, MQTT discovery stack, ScoreStreamPipeline with hysteresis + frozen-sensor detection.
- Phase 2 — Batch Path + Model Lifecycle (6 plans): proto ScoreBatch/SaveModel/LoadModel, InfluxDB reader, PyOD (MAD) + STL detectors + ModelStore, BatchSchedulerWorker, per-entity model persistence, RES-02 restart resilience.

**Result:** 14/14 plans, 34/34 requirements, code review clean. Two-host architecture (edge + GPU) with mTLS, CPU-only.

Artifacts archived under `.planning/archive/v1.0/`.

---

## v2.0 — Home Assistant Add-on (in progress)

**Goal:** Argus installable via HA add-on store ("custom repository") — install and configure through the UI, no manual tokens, `.env`, or file editing.

Started: 2026-06-29.
