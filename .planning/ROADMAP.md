# Roadmap: Argus

## Milestones

- ✅ **v1.0 Foundations + Batch & Model Lifecycle** — Phases 1-2 (shipped 2026-06-10)
- ✅ **v2.0 Home Assistant Add-on** — Phases 1-4 (shipped 2026-06-30)
- 📋 **v3.0 Ingress Configuration UI** — Phases 1-4 (planned)

## Phases

<details>
<summary>✅ v1.0 — Foundations + Batch & Model Lifecycle (Phases 1-2) — SHIPPED 2026-06-10</summary>

All 14 plans complete, 34 requirements covered. Code review clean.
Artifacts archived under `.planning/archive/v1.0/`.

- [x] **Phase 1: Foundations + Streaming** — mono-repo, mTLS gRPC, HA WebSocket ingestion, River HST streaming detector, MQTT discovery, ScoreStreamPipeline with hysteresis
- [x] **Phase 2: Batch Path + Model Lifecycle** — InfluxDB reader, PyOD MAD + STL, ModelStore, BatchSchedulerWorker, per-entity model persistence

</details>

<details>
<summary>✅ v2.0 — Home Assistant Add-on (Phases 1-4) — SHIPPED 2026-06-30</summary>

Argus installable via the HA add-on store and configurable through the HA UI — no manual
tokens, `.env` files, or hand-edited config required. Full detail archived in
`.planning/milestones/v2.0-ROADMAP.md`.

- [x] **Phase 1: Add-on Skeleton + Config-Gen** — repository.yaml + Supervisor-valid schema + EN/PL translations + config-gen seam (options.json → env + entities.yaml) + torch-free Dockerfile — completed 2026-06-30
- [x] **Phase 2: v1 Code Changes** — conditional gRPC security (http→insecure / https→mTLS) + configurable detector bind/model_root — completed 2026-06-30
- [x] **Phase 3: Process Supervision + Runtime Integration** — s6 longrun services, detector readiness gate, live Supervisor MQTT credentials, composite health entity — completed 2026-06-30
- [x] **Phase 4: Multi-Arch CI + Integration + Documentation** — multi-arch GHCR image, image-facts gates, DOCS.md — completed 2026-06-30

**Live-verified on a real HA OS install (2026-06-30):** multi-arch image (`ghcr.io/krzyl2/argus`,
amd64+arm64) pulls and runs; both s6 services start with the readiness gate; the orchestrator
authenticates to HA via the Supervisor proxy (`ws://supervisor/core/websocket` + Bearer header on
a raw WebSocket client); MQTT connects with live Supervisor credentials; `binary_sensor.argus_addon_health`
reads OFF (healthy); startup discovery logged 412 unconfigured numeric sensors.

</details>

### 📋 v3.0 Ingress Configuration UI (Planned)

**Milestone Goal:** Replace hand-edited YAML config with a Home Assistant **Ingress** web UI served
by the add-on ("Open Web UI"): discover HA sensors and pick which Argus tracks, and assign which
detector algorithm(s) + parameters apply to each sensor — no manual `entities` list, and changes
apply without an add-on restart.

- [x] **Phase 1: Ingress Scaffold + SDK Migration + Config Seam** — SDK Worker→Web migration, Kestrel on 0.0.0.0:8099, config.yaml ingress keys, empty-entities crash fix, atomic write seam (completed 2026-06-30)
- [x] **Phase 2: Live Sensor Discovery + Entity Selection UI** — IHaSensorRegistry, /api/sensors, filterable entity picker, include/exclude pattern wiring, gen-entities.py guard
 (completed 2026-07-01)

- [x] **Phase 3: Config Read/Write + Detector Assignment + Reload** — ILiveEntitiesConfig atomic swap, ConfigApiEndpoints, detector/parameter UI, HaListenerWorker inner-CTS restart, MQTT retraction
 (completed 2026-07-01)

- [ ] **Phase 4: Validation, CI Packaging + Documentation** — server+client validation, CI image-size gate, FileSystemWatcher debounce, DOCS.md

## Phase Details

### Phase 1: Ingress Scaffold + SDK Migration + Config Seam

**Goal**: The orchestrator serves an Ingress web endpoint ("Open Web UI") through the existing process — SDK migrated from Worker to Web, Kestrel bound on 0.0.0.0:8099, config.yaml declares ingress keys, a placeholder page loads through the HA Supervisor, all v2.0 BackgroundService functionality is verified unaffected, empty-entities no longer crashes the orchestrator, and the atomic config write path is in place from day one.
**Depends on**: v2.0
**Requirements**: UI-01, CFG-01
**Success Criteria** (what must be TRUE):

  1. The add-on page in HA shows "Open Web UI"; clicking it serves a placeholder page through the Supervisor Ingress proxy with no separate login and no additional exposed port.
  2. All v2.0 background services (streaming, MQTT, health, batch) continue operating correctly after the SDK migration — verified by restarting the add-on and confirming `binary_sensor.argus_addon_health` is healthy.
  3. Starting the orchestrator with an empty `entities.yaml` produces a log warning (not a crash); the UI endpoint remains reachable so the user can configure entities.
  4. Config writes use atomic temp-then-rename; no partial reads are possible during a concurrent file-system watcher event.
  5. Static assets (htmx.min.js, any CSS) load via the Ingress URL with HTTP 200 — not via direct port access — confirming PathBase / `<base href>` resolution is correct.

**Plans**: 2 plans
**Wave 1**

- [x] 01-01-PLAN.md — Config seam: empty-entities warning fix + atomic ConfigWriter (CFG-01)

**Wave 2** *(blocked on Wave 1 completion)*

- [x] 01-02-PLAN.md — SDK Worker→Web migration, Kestrel 0.0.0.0:8099, X-Ingress-Path middleware, placeholder page + wwwroot assets, config.yaml ingress keys (UI-01)

**UI hint**: yes

**Research flags / verification items:**

- **X-Ingress-Path / UsePathBase conflict (live test required):** STACK.md (citing supervisor source) says the Supervisor strips the prefix before forwarding, making UsePathBase unnecessary. PITFALLS.md + FEATURES.md (citing Andrew Lock and community threads) say setting `context.Request.PathBase` from `X-Ingress-Path` before `UseRouting` is required for redirect helpers, LinkGenerator, and static-file middleware to emit correct external URLs. Safe implementation: set PathBase per-request AND emit `<base href="{ingressPath}/">` in the HTML head. **Acceptance criterion:** open the UI exclusively via "Open Web UI" in HA (never direct port); confirm all assets + /api calls return 200; record behavior and close the conflict.
- **Kestrel bind address:** Must be `0.0.0.0:8099`, not loopback — Supervisor connects from `172.30.32.2`. Confirm with `ss -tlnp | grep 8099` inside the container.
- **`EntitiesConfigLoader.Validate()` empty-entities fix must land in this phase** (ARCHITECTURE.md Anti-Pattern 6) before the UI is reachable; otherwise first-boot with an empty options.json causes the orchestrator to crash before the UI can be opened.

---

### Phase 2: Live Sensor Discovery + Entity Selection UI

**Goal**: The UI lists live HA numeric sensors (reusing `IHaSensorRegistry` populated from the existing `get_states` call) and lets the user select which entities Argus tracks, with `include_patterns`/`exclude_patterns` honored as selection filters (closing the v2.0 patterns-ignored gap). The `gen-entities.py` guard is in place before the first UI save so restarts cannot erase UI-authored config.
**Depends on**: Phase 1
**Requirements**: UI-02, CFG-02
**Success Criteria** (what must be TRUE):

  1. The entity picker lists all live HA numeric sensors with their current values; the user can filter by text search on entity_id.
  2. Sensors already tracked by Argus are visually distinguished from available-but-untracked sensors.
  3. Selecting entities and saving persists the selection to `/data/entities.yaml`; the orchestrator's running entity set reflects the new selection after the next pipeline cycle.
  4. `include_patterns` and `exclude_patterns` entered in the UI are applied as real selection filters (not ignored as in v2.0).
  5. Restarting the add-on after a UI save preserves the UI-authored config — `gen-entities.py` does not overwrite it.

**Plans**: 3 plans

Plans:

- [x] 02-01-PLAN.md — HaStateDto attributes + IHaSensorRegistry populated from get_states (UI-02 foundation)
- [x] 02-02-PLAN.md — GlobExpander (include/exclude combine model) + gen-entities.py restart guard (CFG-02 pre-condition)
- [x] 02-03-PLAN.md — Entity picker page + search + save endpoint (_patterns persistence + .ui_config_present lock)

**Wave 1**

- [x] 02-01-PLAN.md — registry + HaStateDto extension foundation
- [x] 02-02-PLAN.md — glob resolver + restart guard (lands before the save endpoint)

**Wave 2** *(blocked on Wave 1 completion)*

- [ ] 02-03-PLAN.md — picker UI + endpoints + save (writes the .ui_config_present lock)

**UI hint**: yes

**Research flags / verification items:**

- **Supervisor `validate_session` API shape (probe live Supervisor before implementing `IngressAuthMiddleware`):** The endpoint `http://supervisor/core/api/ingress/validate_session` is sparsely documented. Probe the live Supervisor for exact request/response shape before writing IngressAuthMiddleware. Fallback if unavailable: accept all connections from `172.30.32.2` (Supervisor IP) in Phase 2 and complete IngressAuthMiddleware in Phase 4.
- **`gen-entities.py` guard is a hard pre-condition for the first UI save** (SUMMARY.md pre-condition 1, PITFALLS.md Pitfall 8). The guard (`_source: ui` marker or `.ui_config_present` lock file) must land at the START of Phase 2, before any save endpoint is wired. If it slips to Phase 3 the first user to restart after saving loses their config.
- **`IHaSensorRegistry` must be populated without opening a second WebSocket connection** (ARCHITECTURE.md ADR-4, Anti-Pattern 5). `NetDaemonHaEventSource` pushes the `get_states` snapshot to the registry after each connect; `GET /api/sensors` reads the registry directly.

---

### Phase 3: Config Read/Write + Detector Assignment + Reload

**Goal**: The user can read the current tracked-entity config in the UI, assign one or more detectors (HST/MAD/STL) with editable parameters to each entity, save, and have the running pipeline reload within seconds — without restarting the add-on. Removed entities have their MQTT discovery topics retracted. This is the highest-complexity phase: `ILiveEntitiesConfig` is the most invasive cross-cutting change.
**Depends on**: Phase 2
**Requirements**: UI-03, CFG-03, CFG-04
**Success Criteria** (what must be TRUE):

  1. The config UI shows current per-entity detector assignments (type + parameters) pre-filled from `/data/entities.yaml`.
  2. The user assigns a detector type and parameters to a tracked entity, saves, and the running pipeline begins using the new assignment within 2 seconds — no add-on restart.
  3. Multiple detectors per entity can be assigned (the model already supports it); sane defaults are shown when no explicit assignment has been made.
  4. Entities removed via the UI have their MQTT discovery topics retracted within 30 seconds; the corresponding HA entities stop showing "unavailable" and disappear.
  5. Config writes are serialized (SemaphoreSlim(1)) and atomic (temp-then-rename); a concurrent save and file-watcher event never leaves a partial or corrupt `/data/entities.yaml`.

**Plans**: 3 plans

Plans:

- [x] 03-01-PLAN.md — ILiveEntitiesConfig volatile-swap singleton + DiscoveryPublisher.RetractAsync (reload-core building blocks)
- [x] 03-02-PLAN.md — Migrate config consumers to ILiveEntitiesConfig + HaListenerWorker inner-CTS restart loop + retraction/republish + Program.cs DI
- [x] 03-03-PLAN.md — Detector-assignment UI (disclosure rows + params) + /api/detectors/new-entry + extended save + Swap

**Wave 1**

- [x] 03-01-PLAN.md — LiveEntitiesConfig + RetractAsync + LogEvents (CFG-04 foundation)

**Wave 2** *(blocked on Wave 1)*

- [x] 03-02-PLAN.md — consumer migration + restart loop + DI rewiring (CFG-04)

**Wave 3** *(blocked on Wave 2 — shares Program.cs)*

- [ ] 03-03-PLAN.md — detector UI + endpoints + save/Swap (UI-03, CFG-03)

**UI hint**: yes

**Research flags / verification items:**

- **`BatchSchedulerWorker` config read pattern (source-read required before planning):** Verify whether `BatchSchedulerWorker` captures `EntitiesConfig.Entities` at construction (in which case it must be changed to read from `ILiveEntitiesConfig.Get()` at each batch cycle) or already reads per-cycle. This determines whether it is in the "Modified" or "Unchanged" column for Phase 3 (ARCHITECTURE.md Component Inventory, SUMMARY.md Architecture Approach).
- **`ILiveEntitiesConfig` atomic swap:** Use `Interlocked.Exchange` on a `volatile` reference; fire `ConfigChanged` event after the swap. `HaListenerWorker` subscribes to `ConfigChanged`, cancels an inner `CancellationTokenSource` (NOT the host-level `stoppingToken`), and restarts the `ScoreStreamPipeline.RunAsync` loop. The host stays alive; MQTT and gRPC transport are not torn down.
- **MQTT discovery retraction for removed entities:** Before restarting the pipeline loop, diff old vs new entity sets and publish empty payloads to the discovery topics of removed entities. This is the mechanism that removes stale HA entities.
- **`ScoreStreamPipeline.BuildEntityStates()` is called at `RunAsync` entry** (not at construction) — confirmed in ARCHITECTURE.md. On loop restart it reads from the already-swapped `ILiveEntitiesConfig.Get()`, so the new config is live without any additional wiring.
- **CFG-04 (reload-without-restart) is covered here, not in Phase 4.** Phase 4 adds the validation layer and CI gate but the reload mechanism itself is Phase 3 work.

---

### Phase 4: Validation, CI Packaging + Documentation

**Goal**: UI inputs are fully validated (server-side and client-side) with clear error messages before save; the CI multi-arch image build bundles UI assets and verifies size stays under 2 GB; `FileSystemWatcher` debounce is validated; and DOCS.md documents the complete UI workflow. The add-on can be configured entirely via UI with zero manual YAML.
**Depends on**: Phase 3
**Requirements**: UI-04, DOCS-02
**Success Criteria** (what must be TRUE):

  1. Invalid entity_id format, out-of-range detector parameters, or unknown detector names are rejected server-side with a clear error message before any write to `/data/entities.yaml`.
  2. Client-side validation mirrors server-side validation; the save button is disabled and fields are highlighted in error while invalid input is present.
  3. The multi-arch CI image build (amd64 + aarch64) bundles all UI assets into `wwwroot/` and the resulting image size is confirmed under 2 GB by a CI gate that fails the build if exceeded.
  4. A single `FileSystemWatcher` `Renamed` event (from atomic rename) triggers exactly one reload — debounce eliminates double-fire; confirmed via log timestamps.
  5. DOCS.md includes a section on the Ingress UI covering: how to open it, how to select entities, how to assign detectors, what "apply without restart" means (including the ~4-minute HST warm-up period after any reload), and how to recover a corrupted config.

**Plans**: 4 plans
**Wave 1**

- [x] 04-02-PLAN.md — Client-side inline validation + UI error states + warm-up banner (UI-04)
- [x] 04-04-PLAN.md — CI wwwroot assertion + DOCS.md Ingress UI section (DOCS-02)

**Wave 2** *(blocked on Wave 1 completion)*

- [ ] 04-01-PLAN.md — Server-side InputValidator + save-handler gate (UI-04)

**Wave 3** *(blocked on Wave 2 completion)*

- [ ] 04-03-PLAN.md — FileSystemWatcher 300ms-debounce reload service (SC4)

**UI hint**: yes

**Research flags / verification items:**

- **Standard patterns only — no live research gate required** for this phase.
- **Document the ~4-minute HST warm-up period** (River HST window=250 at ~1 reading/second per entity) in both the UI and DOCS.md so users understand why anomaly detection resumes gradually after any config change.
- **FileSystemWatcher `Renamed` event (not `Changed`)** must be used with a 300ms debounce to avoid multiple rapid-fire reload calls per single atomic write (PITFALLS.md Pitfall 11).
- **End-to-end acceptance test:** configure entirely via UI with zero manual YAML edits; verify all v2.0 functionality (streaming, batch, health) is unaffected throughout the test.

---

## Progress

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 1-2. Foundations + Batch/Model Lifecycle | v1.0 | 14/14 | Complete | 2026-06-10 |
| 1. Add-on Skeleton + Config-Gen | v2.0 | 2/2 | Complete   | 2026-06-30 |
| 2. v1 Code Changes | v2.0 | 3/3 | Complete   | 2026-07-01 |
| 3. Process Supervision + Runtime Integration | v2.0 | 3/3 | Complete   | 2026-07-01 |
| 4. Multi-Arch CI + Integration + Documentation | v2.0 | 2/4 | In Progress|  |
| 1. Ingress Scaffold + SDK Migration + Config Seam | v3.0 | 0/2 | Not started | - |
| 2. Live Sensor Discovery + Entity Selection UI | v3.0 | 0/TBD | Not started | - |
| 3. Config Read/Write + Detector Assignment + Reload | v3.0 | 0/3 | Not started | - |
| 4. Validation, CI Packaging + Documentation | v3.0 | 0/TBD | Not started | - |
