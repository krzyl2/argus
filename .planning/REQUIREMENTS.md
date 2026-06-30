# Requirements — Milestone v2.0: Home Assistant Add-on

**Goal:** Argus installable via the HA add-on store ("custom repository"), configured entirely through the UI — no manual tokens, `.env`, or file editing.

Status legend: `[ ]` planned · `[x]` validated. REQ-IDs are stable references for roadmap traceability.

---

## v2.0 Requirements

### Packaging & Distribution (ADDON)

- [ ] **ADDON-01**: User can add the Argus repository URL as a custom add-on repository and see "Argus" appear in the HA add-on store.
- [x] **ADDON-02**: User can install and start Argus from the store and it runs on both amd64 and aarch64 hosts.
- [ ] **ADDON-03**: The add-on image is built on `ghcr.io/home-assistant/base-debian:bookworm` (Debian, never Alpine).
- [x] **ADDON-04**: Multi-arch images (amd64 + aarch64) are built and published via the composable HA GitHub Actions on a release tag.
- [ ] **ADDON-05**: The built image stays under 2 GB compressed and contains no PyTorch (Darts core only).

### Supervisor Integration (SUPV)

- [ ] **SUPV-01**: The add-on authenticates to Home Assistant automatically via `SUPERVISOR_TOKEN` (`homeassistant_api: true`) — the user never supplies an HA URL or token.
- [ ] **SUPV-02**: The add-on obtains MQTT broker credentials automatically via `services: [mqtt:need]` — the user never enters MQTT host/user/password; the add-on fails loudly (exit non-zero) if no MQTT service is available.
- [x] **SUPV-03**: MQTT credentials are re-read on every reconnect, never cached, so broker re-provisioning does not break egress.

### UI Configuration (UICFG)

- [ ] **UICFG-01**: User selects monitored sensors as a list of `entity_id` strings in the add-on Configuration tab.
- [ ] **UICFG-02**: User configures InfluxDB (url, token, org, bucket, measurement, value_field) in the UI; leaving it empty disables the batch path without errors.
- [ ] **UICFG-03**: User can optionally set `detector_endpoint` to a remote detector URL; leaving it empty runs the bundled local detector.
- [ ] **UICFG-04**: User can set the batch schedule (interval minutes, nightly fit hour) in the UI.
- [x] **UICFG-05**: On startup the add-on logs the discovered numeric HA sensors so the user can copy entity_ids (mitigates the missing entity picker).
- [ ] **UICFG-06**: User can filter monitored entities via `include_patterns` / `exclude_patterns` globs as an alternative to an explicit list.
- [ ] **UICFG-07**: Configuration-tab field labels and descriptions are localized via `translations/` (English + Polish, per D8).
- [ ] **UICFG-08**: A startup step generates `/data/entities.yaml` from `/data/options.json` before the orchestrator starts.

### Process Supervision (PROC)

- [x] **PROC-01**: Both the detector and orchestrator run as s6-overlay longrun services inside the single add-on container.
- [x] **PROC-02**: The orchestrator begins consuming HA only after the detector reports gRPC health SERVING (readiness gate, local mode).
- [x] **PROC-03**: If either service dies, the container exits rather than entering a silent restart loop (`S6_BEHAVIOUR_IF_STAGE2_FAILS=2`).
- [x] **PROC-04**: When `detector_endpoint` is set (remote mode), the bundled local detector does not start.
- [x] **PROC-05**: A watchdog is declared on the gRPC port so the Supervisor restarts a hung add-on.

### v1 Code Changes (CODE)

- [ ] **CODE-01**: The orchestrator uses an insecure loopback gRPC channel when the detector endpoint scheme is `http://` (no certs required); the existing mTLS path is retained for `https://` (remote).
- [ ] **CODE-02**: The detector bind address is configurable via `ARGUS_GRPC_BIND` and binds `127.0.0.1` in local mode.
- [ ] **CODE-03**: The detector model root is configurable via `ARGUS_MODEL_ROOT` and persists models to `/data/models/`.

### Add-on Health (HEALTH)

- [x] **HEALTH-01**: Argus exposes its own health/status (e.g. detector SERVING / add-on alive) as an HA entity via MQTT discovery, so the user can monitor the add-on itself.

### Documentation (DOCS)

- [x] **DOCS-01**: The add-on ships `DOCS.md` (install from custom repo, configuration reference, Mosquitto prerequisite) and an `icon.png`.

---

## Future Requirements (v2.1+)

- Auto-discovery-only mode (monitor all numeric sensors with exclude list, no manual entity list).
- HACS / official add-on store submission once a stable public release exists.

---

## Out of Scope (v2.0) — with reasoning

- **Ingress / sidebar panel** — Argus has no web UI; an ingress link would be dead and add maintenance cost.
- **Alpine base image** — incompatible with .NET 8 glibc ABI and lacks aarch64 musllinux ML wheels.
- **Zigbee2MQTT embedded-broker support** — Supervisor MQTT discovery returns nothing for the Z2M built-in broker; document the official Mosquitto add-on as a prerequisite instead of coding around it.
- **Changing the detection algorithms** — v1 streaming + batch detection is unchanged; v2.0 is packaging only.
- **Removing the docker-compose path** — compose remains for HA Container/Core and remote-detector deployments.

---

## Traceability

| REQ-ID | Phase | Status |
|--------|-------|--------|
| ADDON-01 | Phase 1 | Pending |
| ADDON-02 | Phase 4 | Complete |
| ADDON-03 | Phase 1 | Pending |
| ADDON-04 | Phase 4 | Complete |
| ADDON-05 | Phase 1 | Pending |
| SUPV-01 | Phase 1 | Pending |
| SUPV-02 | Phase 1 | Pending |
| SUPV-03 | Phase 3 | Complete |
| UICFG-01 | Phase 1 | Pending |
| UICFG-02 | Phase 1 | Pending |
| UICFG-03 | Phase 1 | Pending |
| UICFG-04 | Phase 1 | Pending |
| UICFG-05 | Phase 3 | Complete |
| UICFG-06 | Phase 1 | Pending |
| UICFG-07 | Phase 1 | Pending |
| UICFG-08 | Phase 1 | Pending |
| PROC-01 | Phase 3 | Complete |
| PROC-02 | Phase 3 | Complete |
| PROC-03 | Phase 3 | Complete |
| PROC-04 | Phase 3 | Complete |
| PROC-05 | Phase 3 | Complete |
| CODE-01 | Phase 2 | Pending |
| CODE-02 | Phase 2 | Pending |
| CODE-03 | Phase 2 | Pending |
| HEALTH-01 | Phase 3 | Complete |
| DOCS-01 | Phase 4 | Complete |
