---
phase: 03-process-supervision-runtime-integration
plan: 02
subsystem: mqtt
tags: [mqtt, supervisor-api, credentials, reconnect, grpc, home-assistant-addon]

requires:
  - phase: 02-batch-model-lifecycle
    provides: "MqttConnection, StatePublisher, MqttPublisherWorker, Program.cs DI wiring"

provides:
  - "IMqttCredentialSource abstraction — per-attempt fetch contract with no-cache guarantee"
  - "MqttCredentials immutable record (Host, Port, User, Password)"
  - "SupervisorMqttCredentialSource — GET /services/mqtt with Bearer token + env-var fallback"
  - "MqttConnection refactored for per-attempt credential fetch on every connect/reconnect"
  - "LogEvents.MqttCredentialsRefreshed (4008) — host/port only, never secrets"

affects:
  - 03-03-PLAN.md (extends MqttConnection and Program.cs)
  - future plans touching MQTT connection lifecycle

tech-stack:
  added: []
  patterns:
    - "IMqttCredentialSource: inject credential source into long-running connection; never cache between attempts"
    - "Func<string?> tokenAccessor parameter for testable env-var reads"
    - "FakeHttpMessageHandler pattern for testing HttpClient-dependent services"
    - "internal + InternalsVisibleTo for exposing async build helpers to tests without public surface"

key-files:
  created:
    - orchestrator/Argus.Orchestrator/Mqtt/MqttCredentials.cs
    - orchestrator/Argus.Orchestrator/Mqtt/IMqttCredentialSource.cs
    - orchestrator/Argus.Orchestrator/Mqtt/SupervisorMqttCredentialSource.cs
    - orchestrator/Argus.Orchestrator.Tests/SupervisorMqttCredentialSourceTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs
    - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/Argus.Orchestrator.Tests/MqttConnectionTests.cs

key-decisions:
  - "BuildConnectOptionsAsync is internal (not private) so tests call it directly for LWT assertions without a live broker"
  - "MqttCredentialsRefreshed log event (4008) is emitted from MqttConnection.BuildConnectOptionsAsync (not from SupervisorMqttCredentialSource) to avoid double-logging when the credential source also logs its own Supervisor fetch"
  - "SupervisorMqttCredentialSource receives Func<string?> tokenAccessor to decouple it from Environment.GetEnvironmentVariable for test injection"
  - "HttpClient lifetime: new HttpClient() per-singleton in Program.cs; no IHttpClientFactory needed for a single credential source that runs at low frequency"
  - "Token and password never appear in any log message — only Host:Port is structured-logged (T-03-03 mitigated)"

patterns-established:
  - "Credential source abstraction: IMqttCredentialSource.GetAsync called fresh on every connect attempt"
  - "Fallback chain: Supervisor API → ConnectionSettings env-vars (preserves v1 docker-compose behavior)"
  - "Per-attempt options: BuildConnectOptionsAsync builds new MqttClientOptions each call; no _connectOptions field"

requirements-completed: [SUPV-03]

duration: 25min
completed: 2026-06-30
status: complete
---

# Phase 03 Plan 02: MQTT Supervisor Credential Source Summary

**Supervisor API credential fetch (GET /services/mqtt with Bearer token) wired into MqttConnection per-attempt via IMqttCredentialSource, with env-var fallback and no secret logging**

## Performance

- **Duration:** ~25 min
- **Started:** 2026-06-30T00:00:00Z
- **Completed:** 2026-06-30T00:25:00Z
- **Tasks:** 2 (TDD task 1 + refactor task 2)
- **Files modified:** 8 (4 created, 4 updated)

## Accomplishments

- `IMqttCredentialSource` / `MqttCredentials` abstraction with explicit no-cache contract
- `SupervisorMqttCredentialSource`: fetches `GET http://supervisor/services/mqtt` with `Authorization: Bearer` on every call; falls back to `ConnectionSettings` (ARGUS_MQTT_* env vars) when `SUPERVISOR_TOKEN` absent or API call fails
- `MqttConnection` refactored: removed static `_connectOptions` field; `BuildConnectOptionsAsync` fetches creds fresh and embeds LWT before every `ConnectAsync` (PITFALL 6 / RES-01 preserved)
- `LogEvents.MqttCredentialsRefreshed` (4008): logs host:port only — never token, user, or password (T-03-03)
- 14 new tests total: 5 for SupervisorMqttCredentialSource (parse, Bearer header, token-absent, empty-token, HTTP-exception), 6 updated/new for MqttConnection (LWT retained, 2 new per-attempt refetch assertions), 3 StatePublisher topic tests unchanged
- Full solution: 83 pass, 2 pre-existing DiscoveryPayloadTests failures (known v1 — not regressed)

## Task Commits

1. **Task 1: MqttCredentials + IMqttCredentialSource + SupervisorMqttCredentialSource** — `82c401a` (feat + test, TDD RED→GREEN)
2. **Task 2: Per-attempt credential refetch in MqttConnection + DI wiring** — `28e5830` (feat)

## Files Created/Modified

- `orchestrator/Argus.Orchestrator/Mqtt/MqttCredentials.cs` — Immutable record (Host, Port, User, Password)
- `orchestrator/Argus.Orchestrator/Mqtt/IMqttCredentialSource.cs` — Single `GetAsync` interface; no-cache contract in doc comment
- `orchestrator/Argus.Orchestrator/Mqtt/SupervisorMqttCredentialSource.cs` — Supervisor API fetch + env-var fallback; secrets never logged
- `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs` — Refactored to `BuildConnectOptionsAsync` per-attempt; `IMqttCredentialSource` injected
- `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` — Added `MqttCredentialsRefreshed = new(4008, ...)`
- `orchestrator/Argus.Orchestrator/Program.cs` — Registers `IMqttCredentialSource` singleton, passes to `MqttConnection` factory
- `orchestrator/Argus.Orchestrator.Tests/SupervisorMqttCredentialSourceTests.cs` — 5 tests with FakeHttpMessageHandler
- `orchestrator/Argus.Orchestrator.Tests/MqttConnectionTests.cs` — Updated to FakeCredentialSource + BuildConnectOptionsAsync; 2 new SUPV-03 assertions

## Decisions Made

- `BuildConnectOptionsAsync` made `internal` (not `private`) so tests verify LWT-in-options without a live broker — required by InternalsVisibleTo already in csproj
- `MqttCredentialsRefreshed` logged from `MqttConnection.BuildConnectOptionsAsync`, not from `SupervisorMqttCredentialSource`, to avoid double-logging when both could emit the same fact
- `Func<string?> tokenAccessor` constructor parameter makes `SupervisorMqttCredentialSource` testable without env-var mutation
- Retained exponential-backoff-with-jitter logic in `OnDisconnectedAsync` unchanged; only replaced `_connectOptions` with `BuildConnectOptionsAsync` call

## Deviations from Plan

None — plan executed exactly as written. Minor design choices (where to put the host:port log) were resolved per Rule 7 (surface conflicts, don't average): logged in `BuildConnectOptionsAsync` rather than in both the source and the connection.

## Issues Encountered

None. Existing InternalsVisibleTo was already configured in the main project csproj so `internal BuildConnectOptionsAsync` was immediately accessible to tests.

## Human Verification Required

Live credential-rotation behavior cannot be verified on this dev box (no HA Supervisor). Required human check:

- On a live HA OS: reinstall / re-provision the Mosquitto add-on to rotate credentials
- Trigger an MQTT reconnect (wait for reconnect cycle or force disconnect)
- Confirm Argus reconnects with the new credentials within ~60s without restarting the add-on
- Confirm no token or password values appear in Argus logs (host:port only)

This corresponds to SUPV-03 live verification and is deferred to deployment.

## Threat Surface Scan

No new network endpoints, auth paths, or schema changes introduced beyond what the plan's threat model covers. T-03-03 (secret logging) is mitigated: only host/port appear in structured logs. T-03-06 (plain http to supervisor) is accepted per plan.

## Next Phase Readiness

- `IMqttCredentialSource`, `MqttCredentials`, and `SupervisorMqttCredentialSource` are available for 03-03 to build on
- `MqttConnection` constructor now requires `IMqttCredentialSource` — 03-03 must pass a compatible source in any new wiring
- `BuildConnectOptionsAsync` internal method is stable test surface that 03-03 can extend if LWT topic changes

---
*Phase: 03-process-supervision-runtime-integration*
*Completed: 2026-06-30*
