# Phase 3: Process Supervision + Runtime Integration - Context

**Gathered:** 2026-06-30
**Status:** Ready for planning

<domain>
## Phase Boundary

Wire the add-on runtime: run both processes under s6, gate the orchestrator on detector health, fetch MQTT credentials live from the Supervisor, publish an Argus self-health entity, and log discovered numeric sensors at startup. Requirements: PROC-01, PROC-02, PROC-03, PROC-04, PROC-05, SUPV-03, HEALTH-01, UICFG-05.

Out of scope: add-on packaging (Phase 1, done), the v1 code changes (Phase 2, done), multi-arch CI + live HA E2E + docs (Phase 4). Builds on Phase 1's config-gen (which writes the down-file/env) and Phase 2's conditional channel + configurable detector.

</domain>

<decisions>
## Implementation Decisions

### Argus Health Entity (HEALTH-01)
- Type: `binary_sensor` with `device_class: problem` (ON = problem / unavailable, OFF = healthy).
- Reflects a composite health state: detector reports gRPC SERVING AND the orchestrator is connected to HA and MQTT. A single "Argus healthy" signal, not per-component entities.
- Transport: reuse the existing MQTTnet stack — `DiscoveryPublisher` (retained MQTT discovery config, same pattern as anomaly entities) + `StatePublisher` for state. Group under an "Argus" MQTT device with a stable `unique_id`.
- Friendly name in Polish (D8): "Argus — status".

### Startup Discovered-Sensors Log (UICFG-05)
- The orchestrator performs discovery, reusing its existing HA `get_states` call on first connect (it already snapshots states).
- Logs numeric sensors that are NOT already in the entities config (candidates the user could add), plus a total count.
- Level INFO, one line per sensor (`entity_id` + last numeric value); emitted once after the first successful HA connect, before/at detection start.

### Process Supervision (s6) — Claude's Discretion within research constraints
All s6 wiring details are at Claude's discretion, grounded in `.planning/research/ARCHITECTURE.md` and `PITFALLS.md`:
- Two `services.d/{detector,orchestrator}/run` longrun scripts (HA legacy s6-overlay layout: `cont-init.d` + `services.d`).
- Orchestrator `run` script polls a `wait-detector.py` gRPC health poller (with backoff) before exec — only in local mode; the detector's existing MDL-03 NOT_SERVING→SERVING gate makes the poll meaningful (PROC-02).
- Remote mode: the bundled detector service does not start — config-gen (Phase 1) writes a `down` file; the orchestrator `run` skips the readiness poll when `detector_endpoint` is set (PROC-04).
- `finish` scripts + `S6_BEHAVIOUR_IF_STAGE2_FAILS=2` (set in Phase 1 Dockerfile) so a dying service exits the container instead of looping (PROC-03); `/run/s6/basedir/bin/halt` form.
- `watchdog: "tcp://[HOST]:50051"` declared in `argus/config.yaml` for Supervisor restart of a hung add-on (PROC-05).
- MQTT credentials fetched from the Supervisor service API on every connection attempt, never cached, so Mosquitto re-provisioning survives a reconnect (SUPV-03) — reuse/extend the existing MQTT connection layer.

</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets
- `orchestrator/Argus.Orchestrator/Mqtt/` — `DiscoveryPublisher`, `StatePublisher`, `MqttConnection`, `UniqueId`, `FriendlyName` (reuse for the health entity).
- `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` — already calls `GetStatesAsync` on (re)connect (reuse for the startup discovered-sensors log).
- `detector/argus_detector/` — gRPC health service (grpc_health.v1) already wired with MDL-03 gate (the `wait-detector.py` poll target).
- Phase 1 `argus/rootfs/etc/cont-init.d/10-config-gen.sh` (writes env, down-file) and `argus/Dockerfile` (s6 base) to extend with `services.d/`.

### Integration Points
- Health entity publishes through the same MQTT discovery topic structure as anomaly entities (homeassistant/ prefix).
- s6 `services.d` scripts consume the env vars config-gen wrote in Phase 1; orchestrator run-script honors local vs remote mode via the same `detector_endpoint`-derived signal.

</code_context>

<specifics>
## Specific Ideas

- Research flag (carried from ROADMAP): the Supervisor internal proxy hostname (`supervisor` vs `homeassistant`) and the `/api/websocket` vs `/core/websocket` path must be confirmed on a LIVE HA OS — this is a Phase 3 live-verification item, not blocking the code authoring. Phase 1 wrote `ws://supervisor:80`; if live testing shows a different path, patch here.
- Much of this phase's verification (s6 startup ordering, bashio MQTT live fetch, container exit-on-crash, health entity appearing in HA) requires a live HA OS container and will be human_needed — the dev box has no Docker/Supervisor.

</specifics>

<deferred>
## Deferred Ideas

None — phase scope is the s6 wiring, readiness gate, live MQTT fetch, health entity, and startup log.

</deferred>
