# Roadmap: Argus

## Milestones

- v1.0 Foundations + Batch & Model Lifecycle (Phases 1-2, 14 plans) — shipped 2026-06-10
- v2.0 Home Assistant Add-on (Phases 1-4) — in progress

## Phases

<details>
<summary>v1.0 — Foundations + Batch & Model Lifecycle (SHIPPED 2026-06-10)</summary>

All 14 plans complete, 34 requirements covered. Code review clean. Artifacts archived under `.planning/archive/v1.0/`.

- [x] **Phase 1: Foundations + Streaming** — mono-repo scaffold, mTLS gRPC, HA WebSocket ingestion, River HST streaming detector, MQTT discovery stack, ScoreStreamPipeline with hysteresis
- [x] **Phase 2: Batch Path + Model Lifecycle** — InfluxDB reader, PyOD MAD + STL detectors, ModelStore, BatchSchedulerWorker, per-entity model persistence, RES-02 restart resilience

</details>

### v2.0 Home Assistant Add-on (In Progress)

**Milestone Goal:** Argus installable via the HA add-on store — install and configure entirely through the HA UI, no manual tokens, `.env` files, or config-file editing.

- [x] **Phase 1: Add-on Skeleton + Config-Gen** - Lock base image and HA schema; implement the Supervisor integration seam that converts options.json to env vars and entities.yaml <sub>(executed; 4 live-HA/Docker validation items deferred to Phase 4 CI / live HA)</sub>
- [ ] **Phase 2: v1 Code Changes** - Conditional mTLS in orchestrator and configurable bind/model_root in detector (two parallel tracks, same env var contract)
- [ ] **Phase 3: Process Supervision + Runtime Integration** - s6 service wiring, detector readiness gate, live MQTT credential fetch, add-on health entity
- [ ] **Phase 4: Multi-Arch CI + Integration + Documentation** - CI workflow, aarch64 validation, end-to-end HA test, install documentation

## Phase Details

### Phase 1: Add-on Skeleton + Config-Gen
**Goal**: The add-on schema is Supervisor-valid and the config-gen integration seam converts options.json to env vars and /data/entities.yaml before any process starts.
**Depends on**: Nothing (first v2.0 phase)
**Requirements**: ADDON-01, ADDON-03, ADDON-05, SUPV-01, SUPV-02, UICFG-01, UICFG-02, UICFG-03, UICFG-04, UICFG-06, UICFG-07, UICFG-08
**Requirement → plan map**: 01-01 [ADDON-01, UICFG-01/02/03/04/06/07] · 01-02 [SUPV-01, SUPV-02, UICFG-08] · 01-03 [ADDON-03, ADDON-05]
**Success Criteria** (what must be TRUE):
  1. User can add the Argus repository URL to HA and see "Argus" appear as an installable add-on in the store (repository.yaml + addon/config.yaml valid).
  2. The add-on Configuration tab shows all fields (entity list, InfluxDB settings, detector_endpoint, batch schedule, include/exclude patterns) with English and Polish field labels from translations/.
  3. `ha addon validate` (or equivalent Supervisor lint) passes with no errors against the addon/ folder.
  4. Running the config-gen script against a sample options.json produces a valid /data/entities.yaml matching the orchestrator's expected schema and writes all required s6 environment variables (ARGUS_* and Supervisor auth vars) without error.
  5. The Dockerfile builds on the Debian bookworm base (amd64); `ldd` confirms glibc-linked .NET 8 runtime; `python -c "import torch"` fails inside the built image confirming no PyTorch present; compressed image is under 2 GB.
**Plans**: TBD
**Plans**: 3 plans
Plans:
- [ ] 01-01-PLAN.md — Add-on metadata: repository.yaml + config.yaml schema + EN/PL translations + icons (Wave 1)
- [ ] 01-02-PLAN.md — Config-gen seam: gen-entities.py + cont-init.d env materialization + tests (Wave 1)
- [ ] 01-03-PLAN.md — Add-on Dockerfile (Debian bookworm, torch-free) + image-facts gate (Wave 2)
**UI hint**: yes
**Research flag**: Read `EntitiesConfigLoader` source before implementing gen-entities.py — the generated YAML must match the loader's expected structure exactly. Also resolve whether the orchestrator reads `ARGUS_HA_URL`/`ARGUS_HA_TOKEN` directly or only through `HomeAssistant__*` configuration keys (determines which env vars config-gen must write).

### Phase 2: v1 Code Changes
**Goal**: The orchestrator selects gRPC channel security by URI scheme (http → insecure, https → mTLS); the detector binds to a configurable address and stores models under a configurable root — both changes driven by the env var contract from Phase 1.
**Depends on**: Phase 1
**Requirements**: CODE-01, CODE-02, CODE-03
**Note**: CODE-01 (DetectorChannelFactory.cs in orchestrator) and CODE-02/CODE-03 (config.py + server.py in detector) touch different codebases and can be executed in parallel within this phase.
**Success Criteria** (what must be TRUE):
  1. The orchestrator connects to a local detector at `http://127.0.0.1:50051` with no cert files present; gRPC calls succeed and no SSL handshake error occurs (negative test: zero certs + local mode must pass).
  2. The orchestrator connects to a remote detector at `https://host:50051` using the existing mTLS path unchanged from v1.
  3. The detector binds to `127.0.0.1` when `ARGUS_GRPC_BIND=127.0.0.1` and to `[::]` when unset (backward-compatible default).
  4. The detector saves and loads model files from the path set in `ARGUS_MODEL_ROOT`; when unset it defaults to the v1 path (`/var/argus/models`).
**Requirement → plan map**: 02-01 [CODE-01] · 02-02 [CODE-02, CODE-03]
**Plans**: 2 plans
Plans:
- [ ] 02-01-PLAN.md — Orchestrator conditional channel factory: http→insecure loopback / https→mTLS unchanged + zero-cert regression test (Wave 1)
- [ ] 02-02-PLAN.md — Detector configurable ARGUS_GRPC_BIND + ARGUS_MODEL_ROOT in config.py/server.py + tests (Wave 1)

### Phase 3: Process Supervision + Runtime Integration
**Goal**: Both processes run as supervised s6 longrun services; the orchestrator starts only after the detector is healthy; MQTT credentials are fetched live from the Supervisor; an add-on health entity is published to HA.
**Depends on**: Phase 2
**Requirements**: PROC-01, PROC-02, PROC-03, PROC-04, PROC-05, SUPV-03, HEALTH-01, UICFG-05
**Success Criteria** (what must be TRUE):
  1. Starting the add-on container brings up both detector and orchestrator as s6 longrun services; the orchestrator only begins consuming HA events after the detector reports gRPC health SERVING.
  2. Killing either service causes the container to exit non-zero rather than looping; the Supervisor watchdog also restarts the add-on when the gRPC port becomes unresponsive.
  3. Setting `detector_endpoint` to a remote URL leaves the local detector service stopped (down file written by config-gen); only the orchestrator starts.
  4. MQTT credentials are fetched from the Supervisor service API on each connection attempt; reinstalling the Mosquitto add-on and triggering a reconnect delivers a successful connection with the new credentials without restarting the Argus add-on.
  5. The add-on logs a list of discovered numeric HA sensors at startup before anomaly detection begins, and an Argus health/status entity appears in HA via MQTT discovery after startup.
**Plans**: TBD
**Research flag**: Confirm the Supervisor internal proxy hostname (`supervisor` vs `homeassistant`) on a live HA OS instance before finalising the ARGUS_HA_URL env var value written by config-gen. Wrong hostname causes NetDaemon.Client to fail silently.

### Phase 4: Multi-Arch CI + Integration + Documentation
**Goal**: The add-on is published as a verified multi-arch image by CI, passes an end-to-end anomaly detection test on a live HA OS instance, and ships with install documentation.
**Depends on**: Phase 3
**Requirements**: ADDON-02, ADDON-04, DOCS-01
**Success Criteria** (what must be TRUE):
  1. A release tag triggers the composable GHA workflow and publishes an amd64 + aarch64 manifest to GHCR without manual intervention; the compressed image is under 2 GB and `import torch` fails inside both arch images.
  2. Installing the add-on on an aarch64 HA OS host starts successfully with no Python wheel source-compilation during install.
  3. An anomaly on a monitored sensor appears in HA as a `binary_sensor` and score `sensor` entity within 2 seconds of the `state_changed` event on a live HA OS install from the custom repo.
  4. `DOCS.md` (install steps, configuration reference, Mosquitto prerequisite) and `icon.png` are present in the add-on folder and visible in the HA documentation tab.
**Plans**: TBD
**Research flag**: Confirm native ARM64 GitHub Actions runner availability before writing the CI matrix; if unavailable, fall back to QEMU emulation + `pip install --prefer-binary` with an extended timeout gate.

## Progress

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 1. Foundations + Streaming | v1.0 | 8/8 | Complete | 2026-06-10 |
| 2. Batch Path + Model Lifecycle | v1.0 | 6/6 | Complete | 2026-06-10 |
| 1. Add-on Skeleton + Config-Gen | v2.0 | 0/3 | Planned | - |
| 2. v1 Code Changes | v2.0 | 0/2 | Planned | - |
| 3. Process Supervision + Runtime Integration | v2.0 | 0/TBD | Not started | - |
| 4. Multi-Arch CI + Integration + Documentation | v2.0 | 0/TBD | Not started | - |
