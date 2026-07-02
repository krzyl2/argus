# Milestones — Argus

## v3.0 Ingress Configuration UI (Shipped: 2026-07-02)

**Phases completed:** 4 phases, 12 plans, 11 tasks

**Key accomplishments:**

- EntitiesConfigLoader softened (empty entities now warns + returns) and atomic ConfigWriter established via temp-then-rename + SemaphoreSlim(1,1) — orchestrator no longer crashes on first boot with no entities configured
- Worker SDK migrated to Web SDK; Kestrel co-hosted on 0.0.0.0:8099 with X-Ingress-Path PathBase middleware, server-rendered placeholder page (htmx 2.0.10, CSS token foundation), and config.yaml ingress manifest keys.
- Thread-safe `IHaSensorRegistry` volatile-snapshot singleton fed from the existing `get_states` call, with `HaStateDto` extended to carry `unit_of_measurement` and `friendly_name` from HA attributes.
- BCL `FileSystemName.MatchesSimpleExpression`-based glob resolver implementing the authoritative include/exclude/manual-override combine model, plus the `.ui_config_present` restart guard protecting UI-authored `entities.yaml` from regeneration on add-on restart.
- Server-rendered entity picker (GET /sensors + GET /api/sensors + POST /api/sensors/save) with htmx search, YamlDotNet combined-root YAML persistence (_patterns + entities), ConfigWriter atomic write, and .ui_config_present lock file activation.
- `ILiveEntitiesConfig` volatile-swap singleton (Interlocked.Exchange + ConfigChanged event) plus `DiscoveryPublisher.RetractAsync` delegate-overload for removed-entity MQTT discovery retraction.
- Migrated all three EntitiesConfig consumers to ILiveEntitiesConfig and replaced HaListenerWorker's one-shot ExecuteAsync with an inner-CTS restart loop that reloads the streaming pipeline on ConfigChanged, retracts removed entities from MQTT, and republishes discovery for added ones (CFG-04 hot-reload mechanism).
- CI `test -f` assertion guards htmx.min.js/argus.css in publish output before Docker build; DOCS.md gains a complete zero-YAML Ingress UI workflow section with HST warm-up disclosure and corrupted-config recovery.

**Live-verified on real HA OS (2026-07-02, add-on 2.0.9):** add-on starts (orchestrator + detector + Ingress UI), HA WebSocket connects, UI serves, entity save + hot-reload work. Three real-world fixes found during bring-up: aspnet runtime base (2.0.7), ScoreStreamPipeline DI into HaListenerWorker (2.0.8), GlobExpander empty-pattern semantics — empty include patterns now select nothing instead of all entities (2.0.9).

**Known deferred items at close: 8** (4 UAT + 4 verification, all live-HA sign-off — see STATE.md Deferred Items). Formal UAT skipped by operator decision after successful live bring-up.

---

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
