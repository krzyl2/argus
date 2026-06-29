# Milestones — Argus

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
