# Milestones — Argus

## v4.0 Group & Multivariate Anomaly Detection + UX (Shipped: 2026-07-06)

**Phases completed:** 6 phases, 18 plans, 42 tasks

**Key accomplishments:**

- Extended argus.proto with a real 2D-matrix group contract (Series/GroupScoreRequest/GroupScoreResponse/FitGroupRequest/FitGroupResponse) and two new DetectorService RPCs (ScoreGroupBatch, FitGroup), regenerated Python stubs, and proved the wire contract with codegen tests.
- GroupMultivariateDetector (RobustScaler + PyOD ECOD/COPOD/PCA/IForest) with joblib bundle persistence via ModelStore.save_group_bundle/load_group_bundle, keyed under a group_ namespace that never collides with per-entity model keys
- Wired peer-divergence and joint-multivariate group detectors into the gRPC boundary via `ScoreGroupBatch`/`FitGroup` servicer handlers and extended registry factory branches, closing GRP-03..07 with ragged/empty/unknown-detector input validation at the boundary.
- GroupConfig YAML schema with skip-and-warn config-load validation (3-member floor, peer-mode unit guard, nullable-registry cold-boot degrade) — dead per-entity Covariates/Groups placeholders retired.
- GroupInfluxReader issues a single aggregateWindow+pivot Flux query (no fill()) for the N×M member matrix plus a companion last()-per-member freshness query, and IBatchDetectorClient gains ScoreGroupBatchAsync/FitGroupAsync wrapping the Phase 5 group RPCs.
- Group MQTT discovery/state layer: peer-divergence emits per-member binary_sensor+score pairs, joint emits one group-level pair, all sharing a single HA device per group_id, with removed-members-only retraction.
- Group scoring loop, joint-only nightly fit, and wall-clock staleness-cap boundary policy wired into BatchSchedulerWorker; group MQTT discovery/retraction wired into MqttPublisherWorker; DI wiring in Program.cs completes the end-to-end group anomaly pipeline.
- Vite+Preact SPA in orchestrator/ui/ with a relative-fetch client, hand-rolled hash router, and 13 components reproducing v3.0's htmx entity-picker UI 1:1 (search, detector assignment, validation, save) — builds to Argus.Orchestrator/wwwroot and passes Vitest.
- Converted three htmx HTML-fragment endpoints (`/api/sensors`, `/api/detectors/new-entry`, `/api/sensors/save`) to clean JSON matching the SPA's `types.ts` contract, wired `MapFallbackToFile("index.html")` for SPA hosting, and deleted all server-rendered HTML code (`EntityPickerPage.cs`, `DetectorFieldParser.cs`, `PlaceholderPage.cs`) with zero regression to Ingress auth or the config hot-reload pipeline.
- 1. [Rule 1 - Bug] `ARG BUILD_FROM` not visible to the runtime stage's `FROM` (Docker build model constraint)
- peer_divergence.threshold and multivariate contamination/n_estimators are now real, request.params-driven knobs — presets built in later plans will genuinely change detection instead of being cosmetic.
- Four auth-guarded Minimal API endpoints (group CRUD, static detector catalog, attribution status) plus HA area/domain sensor enrichment — the exact backend contract Phase 8's SPA consumes, with the Pitfall-4 contribution-sort bug fixed before any UI reads it.
- Group list/editor/member-picker screens wired to the 08-02 endpoints with client-side floor/unit validation, plus a server-side bug fix that unblocked friendly_name search and area-grouped sensor browse.
- Guided-flow algorithm chooser with catalog-sourced presets/Advanced-override, ranked joint-anomaly attribution bars, and approve-only area-scoped group suggestions — completing the Phase 8 transparency crux (ALGO-01..04, GRP-09, SRCH-03); the final live-HA Ingress checkpoint is pending human execution.
- Lowered group member-count floor from 3 to 2 at all three config-validation layers (client TS, server C#, config-load C#) for both joint and peer_divergence modes, switched the guided chooser's "together" recommendation from ecod to copod, and rewrote all 5 DetectorCatalog BestFor entries with accurate correlation-handling/attribution copy including a 2-member peer_divergence caveat.
- New `PairwiseDeltaDetector` (delegates to the existing PyOD MAD detector) scores `member_a - member_b` for 2-member `peer_divergence` groups; servicer routes on `len(request.series) == 2` before the classic `PeerDivergenceDetector` path, leaving the N>=3 algorithm and its locked floor test completely untouched.
- Count-aware BatchSchedulerWorker staleness/publish/nightly-fit branches plus a shared `DiscoveryPublisher.UsesPerMemberEntities` helper so 2-member peer_divergence groups get fitted, scored, published, and retracted as a single group-level relationship check instead of silently misbehaving.

**Delivered:** Argus extended from single-sensor to group anomaly detection (peer-divergence + joint-multivariate), rebuilt the config UI as a Preact+Vite SPA with a guided algorithm chooser, and corrected 2-member group support + guidance defaults.

**Stats:** Phases 5-9 · 18 plans · 42 tasks · 25/25 requirements complete · 254 files changed (+28,201 / −17,000) · 135 commits · 2026-07-02 → 2026-07-06.

**Known deferred items at close: 3** — Phase 07 + Phase 08 `human_needed` verifications (live-HA Ingress round-trip for the SPA + group/algorithm-chooser/attribution UI) and 10 skipped Phase 08 UAT scenarios, all deferred by operator decision pending the planned UI rebuild. Backend paths (proto/Python detectors, group pipeline, 2-member routing) verified: Phases 05/06/09 VERIFICATION `passed`. See STATE.md Deferred Items.

---

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
