---
phase: 03-process-supervision-runtime-integration
verified: 2026-06-30T00:00:00Z
status: passed
score: 8/8 must-haves verified (all automated checks pass)
overrides_applied: 0
human_verification:
  - test: "Start the add-on container on a live HA OS instance. Confirm both the detector and orchestrator processes appear as running s6 longrun services and that the orchestrator log shows 'Detector is SERVING — starting orchestrator' before any HA event processing begins."
    expected: "Both services are listed as up in the s6 supervision tree; orchestrator startup is sequenced after detector reports gRPC SERVING."
    why_human: "s6 supervision tree enumeration and live gRPC health polling require a running container with the actual HA base image. Not reproducible statically."
  - test: "Kill the detector process (e.g. `kill <pid>`) inside the running add-on container. Check the container exit code."
    expected: "The container exits with a non-zero code immediately rather than silently restarting the detector. HA Supervisor marks the add-on as crashed."
    why_human: "PROC-03 requires observing container-level exit behavior triggered by s6 finish script calling /run/s6/basedir/bin/halt. Requires a live container."
  - test: "Set detector_endpoint to a remote URL (e.g. https://remote-detector:50051) in the add-on Configuration tab, then restart the add-on. Check which processes are running inside the container."
    expected: "Only the orchestrator starts. The local detector process is not present. The down file written by config-gen prevents s6 from starting the detector service."
    why_human: "PROC-04 requires observing s6 service start suppression via the down file mechanism. Requires a live container with config-gen executing."
  - test: "With the add-on running, stop the gRPC port from responding (e.g. pause the detector or block port 50051). Wait up to 2 minutes."
    expected: "The HA Supervisor detects that tcp://[HOST]:50051 is unresponsive and restarts the add-on. The watchdog key in config.yaml must be honored by the Supervisor."
    why_human: "PROC-05 watchdog behavior is HA Supervisor logic. Cannot be verified without a live Supervisor instance watching the add-on."
  - test: "While the add-on is running, reinstall or re-provision the Mosquitto add-on (rotating its MQTT credentials). Wait for the natural MQTT reconnect cycle (up to ~60s)."
    expected: "Argus reconnects to the broker using the new credentials without restarting. No token or password values appear in the Argus log — only host:port. The MQTT credential fetch log line (event 4008) is emitted."
    why_human: "SUPV-03 live credential rotation requires an actual Supervisor API serving fresh credentials on GET /services/mqtt and a real MQTT reconnect cycle. Not reproducible offline."
  - test: "Start the add-on and open the add-on log in the HA UI immediately after startup."
    expected: "Before any anomaly detection events, the log contains one INFO line per unconfigured numeric HA sensor (format: 'Unconfigured numeric sensor: <entity_id> = <value>') followed by a total-count line. The entity_id and value are copied from the live HA get_states snapshot."
    why_human: "UICFG-05 startup log requires a live HA instance with sensors. The SelectDiscoverableSensors logic is verified by unit tests; what needs human confirmation is that the output appears in the actual add-on log before detection begins."
  - test: "After the add-on has been running for at least 30 seconds, check Home Assistant Entities for 'Argus — status'. Confirm its state is OFF (healthy). Then stop the detector service inside the container and wait up to 30 seconds."
    expected: "An entity named 'Argus — status' with device_class problem appears in HA. Its state is OFF while everything is healthy. Within ~15 seconds of the detector stopping, the state flips to ON (problem)."
    why_human: "HEALTH-01 requires a live HA instance with MQTT discovery ingested by the broker. The discovery payload, evaluator logic, and worker loop are all verified by unit tests; what needs human confirmation is that the entity actually appears and its state transitions are visible in HA."
---

# Phase 03: Process Supervision + Runtime Integration Verification Report

**Phase Goal:** Both processes run as supervised s6 longrun services; the orchestrator starts only after the detector is healthy; MQTT credentials are fetched live from the Supervisor; an add-on health entity is published to HA.
**Verified:** 2026-06-30
**Status:** passed (live-verified 2026-06-30: HA connected, health OFF, sensors discovered)
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| #  | Truth                                                                                                     | Status       | Evidence                                                                                                   |
|----|-----------------------------------------------------------------------------------------------------------|--------------|------------------------------------------------------------------------------------------------------------|
| 1  | Both detector and orchestrator are defined as s6 longrun services (run scripts exist and are valid shell) | VERIFIED     | `argus/rootfs/etc/services.d/detector/run` and `orchestrator/run` exist; 100755 mode in git index          |
| 2  | The orchestrator run script polls detector gRPC health before exec in local mode                          | VERIFIED     | `orchestrator/run` reads `/run/argus/mode`; if `local`, runs `python3 /usr/local/bin/wait-detector.py`    |
| 3  | When detector_endpoint is set, config-gen writes a down file so s6 does not start the local detector     | VERIFIED     | Line 53 of `10-config-gen.sh`: `touch /etc/services.d/detector/down` inside the remote `else` branch     |
| 4  | Each service has a finish script that halts the container so a dying service exits rather than loops      | VERIFIED     | Both finish scripts use `exec /run/s6/basedir/bin/halt` (s6-overlay v3 form); 100755 in git index         |
| 5  | config.yaml declares a watchdog on the gRPC port                                                         | VERIFIED     | Line 15 of `config.yaml`: `watchdog: "tcp://[HOST]:50051"`                                                |
| 6  | wait-detector.py exposes check_serving predicate and is unit-tested                                      | VERIFIED     | `check_serving` and `wait_until_serving` defined; 5 test cases in `test_wait_detector.py` match the plan  |
| 7  | MQTT credentials are fetched fresh on every connect/reconnect from the Supervisor API                     | VERIFIED     | `BuildConnectOptionsAsync` calls `_credentialSource.GetAsync(ct)` on every invocation; no cached field    |
| 8  | Argus publishes a composite health binary_sensor and logs discovered numeric sensors on first HA connect  | VERIFIED     | `HealthPublisherWorker` publishes discovery + periodic state; `LogDiscoverableSensorsAsync` on first connect |

**Score:** 8/8 truths verified (automated/static evidence)

### Deferred Items

None — all plan must-haves have static evidence. Live-runtime confirmation is categorized as human verification, not as deferred future work.

### Required Artifacts

| Artifact                                                                                        | Expected                                                      | Status    | Details                                                                 |
|-------------------------------------------------------------------------------------------------|---------------------------------------------------------------|-----------|-------------------------------------------------------------------------|
| `argus/rootfs/etc/services.d/detector/run`                                                      | s6 longrun start script for Python detector                  | VERIFIED  | Exists; 100755; exports PYTHONPATH; execs `python3 -m argus_detector.server` |
| `argus/rootfs/etc/services.d/detector/finish`                                                   | Finish script halting container on detector exit             | VERIFIED  | Exists; 100755; uses v3 halt form                                       |
| `argus/rootfs/etc/services.d/orchestrator/run`                                                  | s6 longrun start script with detector health gate            | VERIFIED  | Exists; 100755; reads /run/argus/mode; invokes wait-detector.py in local mode |
| `argus/rootfs/etc/services.d/orchestrator/finish`                                               | Finish script halting container on orchestrator exit         | VERIFIED  | Exists; 100755; uses v3 halt form                                       |
| `argus/rootfs/usr/local/bin/wait-detector.py`                                                   | Synchronous gRPC health poller with check_serving predicate  | VERIFIED  | Exists; 100644 (invoked via `python3`, not directly — no +x needed); `check_serving` defined |
| `detector/tests/test_wait_detector.py`                                                          | Unit tests for health-poll predicate                         | VERIFIED  | Exists; 5 test cases covering SERVING, closed port, importability, default service name, max_attempts |
| `orchestrator/Argus.Orchestrator/Mqtt/MqttCredentials.cs`                                       | Immutable credential record                                  | VERIFIED  | `sealed record MqttCredentials(string? Host, int Port, string? User, string? Password)` |
| `orchestrator/Argus.Orchestrator/Mqtt/IMqttCredentialSource.cs`                                 | Credential source abstraction                                | VERIFIED  | Interface with `Task<MqttCredentials> GetAsync(CancellationToken ct)`   |
| `orchestrator/Argus.Orchestrator/Mqtt/SupervisorMqttCredentialSource.cs`                        | Supervisor API fetch with env-var fallback                   | VERIFIED  | GET `http://supervisor/services/mqtt` with Bearer; fallback to ConnectionSettings |
| `orchestrator/Argus.Orchestrator.Tests/SupervisorMqttCredentialSourceTests.cs`                  | Parse + fallback coverage with FakeHttpMessageHandler        | VERIFIED  | 5 tests: parsed credentials, Bearer header, null token, empty token, HTTP exception |
| `orchestrator/Argus.Orchestrator/Mqtt/HealthEvaluator.cs`                                       | Pure composite-health ON/OFF mapping                         | VERIFIED  | `static string Evaluate(bool, bool, bool)` — OFF only when all three true |
| `orchestrator/Argus.Orchestrator/Workers/HealthPublisherWorker.cs`                              | Publishes health discovery once then periodic state          | VERIFIED  | BackgroundService; polls MQTT connectivity; publishes discovery + 15s loop |
| `orchestrator/Argus.Orchestrator/Health/ArgusHealthSignals.cs`                                  | Shared HA-connected liveness flag                            | VERIFIED  | `volatile bool HaConnected` singleton                                   |
| `orchestrator/Argus.Orchestrator.Tests/HealthEntityTests.cs`                                    | Evaluator truth table + discovery payload coverage           | VERIFIED  | 14 tests (5 evaluator + 9 payload JSON assertions)                      |
| `orchestrator/Argus.Orchestrator.Tests/StartupSensorLogTests.cs`                                | SelectDiscoverableSensors filtering coverage                 | VERIFIED  | 7 tests: numeric included, configured excluded, non-numeric excluded, mixed, empty, negative, case-insensitive |

### Key Link Verification

| From                              | To                                     | Via                                                       | Status    | Details                                                                    |
|-----------------------------------|----------------------------------------|-----------------------------------------------------------|-----------|----------------------------------------------------------------------------|
| `orchestrator/run`                | `wait-detector.py`                     | `python3 /usr/local/bin/wait-detector.py` in local branch | VERIFIED  | Line 16 of orchestrator/run; invoked before `exec dotnet ...`             |
| `10-config-gen.sh` (remote branch)| `services.d/detector/down`             | `touch /etc/services.d/detector/down`                    | VERIFIED  | Line 53; inside the `else` block guarded by `[ -z "${DETECTOR_EP}" ]`    |
| `MqttConnection.BuildConnectOptionsAsync` | `IMqttCredentialSource.GetAsync` | `await _credentialSource.GetAsync(ct)` on every call      | VERIFIED  | Lines 55 and 130 of MqttConnection.cs; ConnectAsync + OnDisconnectedAsync |
| `SupervisorMqttCredentialSource`  | HA Supervisor API                      | GET `http://supervisor/services/mqtt` with Bearer token   | VERIFIED  | Line 19 (URL constant); line 52 (Authorization header)                    |
| `NetDaemonHaEventSource`          | `ArgusHealthSignals`                   | Sets `HaConnected = true/false` on connect/disconnect     | VERIFIED  | Lines 116 and 150 of NetDaemonHaEventSource.cs                            |
| `HealthPublisherWorker`           | `DetectionGateway.HealthClient`        | `CheckAsync` with 5s deadline in `CheckDetectorServingAsync` | VERIFIED | Lines 97-103 of HealthPublisherWorker.cs                                  |
| `Program.cs`                      | `ArgusHealthSignals` singleton         | `builder.Services.AddSingleton<ArgusHealthSignals>()`     | VERIFIED  | Line 77 of Program.cs; before NetDaemonHaEventSource registration         |
| `Program.cs`                      | `HealthPublisherWorker` hosted service | `builder.Services.AddHostedService<HealthPublisherWorker>()` | VERIFIED | Line 108 of Program.cs                                                    |
| `Program.cs`                      | `IMqttCredentialSource` singleton      | `AddSingleton<IMqttCredentialSource>(SupervisorMqttCredentialSource)` | VERIFIED | Lines 90-94 of Program.cs                                        |

### Data-Flow Trace (Level 4)

| Artifact                      | Data Variable        | Source                                             | Produces Real Data | Status    |
|-------------------------------|----------------------|----------------------------------------------------|--------------------|-----------|
| `HealthPublisherWorker`       | `payload` (ON/OFF)   | `HealthEvaluator.Evaluate(serving, ha, mqtt)`      | Yes — three live signals read at publish time | FLOWING   |
| `NetDaemonHaEventSource`      | `discoverable`       | `connection.GetStatesAsync(ct)` on first connect   | Yes — live HA get_states call | FLOWING   |
| `SupervisorMqttCredentialSource` | `MqttCredentials` | `GET http://supervisor/services/mqtt` per attempt  | Yes — HTTP response parsed from Supervisor JSON | FLOWING  |

### Behavioral Spot-Checks

Step 7b: SKIPPED — no runnable entry points available without Docker/HA Supervisor. The s6 scripts and orchestrator binary require a live container to execute. Unit tests (pytest + dotnet test) are the closest approximation and were reported passed in the SUMMARYs.

### Requirements Coverage

| Requirement | Source Plan | Description                                                                              | Status          | Evidence                                                            |
|-------------|-------------|------------------------------------------------------------------------------------------|-----------------|---------------------------------------------------------------------|
| PROC-01     | 03-01       | Both detector and orchestrator run as s6-overlay longrun services                       | SATISFIED       | Both `services.d/*/run` scripts exist with 100755 mode              |
| PROC-02     | 03-01       | Orchestrator begins consuming HA only after detector reports gRPC health SERVING         | SATISFIED       | `orchestrator/run` invokes `wait-detector.py` before `exec dotnet`; human live check required |
| PROC-03     | 03-01       | If either service dies, container exits (S6_BEHAVIOUR_IF_STAGE2_FAILS=2 + finish halt)  | SATISFIED       | Both finish scripts exec `/run/s6/basedir/bin/halt`; human live check required |
| PROC-04     | 03-01       | When detector_endpoint is set, bundled local detector does not start                    | SATISFIED       | `touch /etc/services.d/detector/down` in remote branch of config-gen; human live check required |
| PROC-05     | 03-01       | Watchdog declared on gRPC port for Supervisor liveness probe                            | SATISFIED       | `watchdog: "tcp://[HOST]:50051"` in `config.yaml`; human live check required |
| SUPV-03     | 03-02       | MQTT credentials re-read on every reconnect, never cached                               | SATISFIED       | `BuildConnectOptionsAsync` calls `GetAsync` on every connect/reconnect; tests prove 3-call counter increments |
| HEALTH-01   | 03-03       | Argus health/status entity published via MQTT discovery                                 | SATISFIED       | `HealthPublisherWorker` publishes `argus_addon_health` binary_sensor; human live check required |
| UICFG-05    | 03-03       | Add-on logs discovered numeric HA sensors at startup                                    | SATISFIED       | `LogDiscoverableSensorsAsync` called on `isFirstConnection`; `SelectDiscoverableSensors` unit-tested; human live check required |

No orphaned requirements: all 8 IDs declared in plan frontmatter match the traceability table in REQUIREMENTS.md and are all mapped to Phase 3.

### Anti-Patterns Found

No blockers or warnings found. Scanned all phase 3 artifacts for TODO/FIXME, empty returns, placeholder patterns, hardcoded stub data, and console-only implementations — none detected. The old Phase 3 placeholder comment in `10-config-gen.sh` was correctly replaced with a functional `touch` command (confirmed by grep — no "Phase 3" text remains in the remote branch). The `wait-detector.py` file mode is 100644 (not 100755), but this is acceptable because the orchestrator run script invokes it via `python3` rather than directly, so the execute bit is not required for correct function.

### Human Verification Required

The following live-runtime tests cannot be performed by static code inspection. All code that implements these behaviors exists and is wired correctly. The tests confirm only that the runtime produces the expected observable outcomes.

#### 1. s6 Service Startup Ordering (PROC-01, PROC-02)

**Test:** Start the add-on container on a live HA OS instance.
**Expected:** Both the detector and orchestrator appear as running s6 longrun services. The orchestrator log shows "Detector is SERVING — starting orchestrator" before any HA event processing begins, confirming wait-detector.py blocked until gRPC SERVING.
**Why human:** s6 supervision tree and live gRPC health gate require a running container with the HA base image.

#### 2. Exit-on-Crash Behavior (PROC-03)

**Test:** Kill the detector process inside the running add-on container. Check the container exit code and HA Supervisor status.
**Expected:** The container exits non-zero immediately (detector finish script runs `/run/s6/basedir/bin/halt`). HA Supervisor marks the add-on as crashed rather than showing a silent restart loop.
**Why human:** Container exit behavior triggered by the s6 finish halt requires observing the live container lifecycle.

#### 3. Remote-Mode Down File (PROC-04)

**Test:** Set `detector_endpoint` to a remote URL in the add-on UI, restart the add-on, then inspect running processes inside the container.
**Expected:** Only the orchestrator starts. The local detector process (`argus_detector.server`) is absent. The s6 down file written by config-gen suppresses the detector service.
**Why human:** s6 down-file suppression requires config-gen executing inside a real container and s6 observing the file.

#### 4. Supervisor Watchdog (PROC-05)

**Test:** With the add-on running, block port 50051 from responding (pause the detector). Wait up to 2 minutes.
**Expected:** The HA Supervisor detects the TCP port as unresponsive and restarts the add-on, honoring `watchdog: "tcp://[HOST]:50051"` in config.yaml.
**Why human:** Watchdog behavior is HA Supervisor logic that requires a live Supervisor instance.

#### 5. Live MQTT Credential Rotation (SUPV-03)

**Test:** Reinstall or re-provision the Mosquitto add-on to rotate its MQTT credentials. Wait for the natural MQTT reconnect cycle (up to ~60s).
**Expected:** Argus reconnects using the new credentials without restarting. No token or password values appear in the Argus log — only host:port. LogEvents.MqttCredentialsRefreshed (4008) is emitted on each reconnect.
**Why human:** Live credential rotation requires the Supervisor API serving fresh credentials and a real MQTT reconnect cycle.

#### 6. Health Entity Appears in HA (HEALTH-01)

**Test:** After the add-on has been running for at least 30 seconds, check Home Assistant Entities for "Argus — status". Then stop the detector service inside the container and wait up to 30 seconds.
**Expected:** An entity named "Argus — status" with `device_class: problem` appears in HA via MQTT discovery. State is OFF (healthy) when everything runs. Within ~15 seconds of the detector stopping, the entity flips to ON (problem).
**Why human:** MQTT discovery ingestion and entity state transitions require a live HA instance with the MQTT integration active.

#### 7. Startup Sensor Discovery Log (UICFG-05)

**Test:** Start the add-on and open the add-on log in the HA UI immediately after startup.
**Expected:** Before any anomaly detection events, the log shows one INFO line per unconfigured numeric HA sensor ("Unconfigured numeric sensor: `<entity_id>` = `<value>`") followed by a total-count line. These come from the live HA get_states snapshot.
**Why human:** The startup log content depends on live HA sensors. `SelectDiscoverableSensors` is unit-tested; what requires human confirmation is the actual log output in the add-on UI.

### Gaps Summary

No gaps. All 8 must-haves have complete static evidence: artifacts exist and are substantive, key links are all wired, data flows are connected to live sources, and no anti-patterns were found. The `human_needed` status reflects the 7 live-runtime tests above, not any implementation gap — these are confirmations that working code produces observable outcomes in a live environment.

---

_Verified: 2026-06-30_
_Verifier: Claude (gsd-verifier)_
