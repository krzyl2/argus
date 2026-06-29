---
phase: 01-foundations-streaming
plan: "04"
subsystem: orchestrator
tags: [dotnet, grpc, mtls, yaml, config, docker, tdd, health-check]

requires:
  - "01-01: proto/argus.proto + .NET Grpc.Tools stubs (Argus.Detector.V1.*)"
  - "01-02: detector Health/Check serving argus.v1.DetectorService = SERVING"
  - "01-03: deploy/certs/ ca.crt + client.crt/key (mTLS trust material)"

provides:
  - "orchestrator/Config/EntitiesConfig.cs: EntitiesConfig POCOs + HstParams resolver with D-09/D-11/D-12 defaults"
  - "orchestrator/Config/EntitiesConfigLoader.cs: YamlDotNet YAML loader; covariates/groups ignored with structured warning"
  - "orchestrator/Config/ConnectionSettings.cs: all secrets from env vars (CONF-03); no literal defaults for tokens/passwords"
  - "orchestrator/Logging/LogEvents.cs: structured EventId definitions for OBS-01"
  - "orchestrator/Detection/DetectorChannelFactory.cs: single mTLS GrpcChannel via HttpClientHandler.ClientCertificates + CustomRootTrust (D-18)"
  - "orchestrator/Detection/DetectionGateway.cs: INFRA-07 health gate; WaitForHealthyAsync with 1s/2s/4s/8s/60s backoff (RES-03)"
  - "orchestrator/Workers/HaListenerWorker.cs: BackgroundService stub gated on health check; TODO(plan05) marker"
  - "orchestrator/Program.cs: full host wiring — env ConnectionSettings, singleton GrpcChannel, AddHostedService<HaListenerWorker>"
  - "entities.yaml: 3 placeholder entities (Q1 comment) with hst detector"
  - "deploy/Dockerfile.orchestrator: mcr.microsoft.com/dotnet/sdk:8.0 build + runtime:8.0-jammy-chiseled final (INFRA-03)"
  - "deploy/docker-compose.edge.yml: edge-host compose; certs volume; all secrets via env (INFRA-05, CONF-03)"

affects:
  - "01-05: HaListenerWorker.ExecuteAsync TODO(plan05) is the fill point for HA subscription"
  - "01-06: MQTTnet publisher — TODO(plan06) comment in Program.cs marks DI registration point"
  - "01-07: HstParams resolver consumed by Plan 07 for per-entity detector params"
  - "01-08: integration test validates Health gate + entities.yaml path"

tech-stack:
  added:
    - "YamlDotNet 16.3.0 — entities.yaml deserialization with UnderscoredNamingConvention"
    - "Microsoft.Extensions.Logging 8.0.1 — added to test project for capturing logger"
  patterns:
    - "TDD RED/GREEN cycle for both EntitiesConfig (3 tests) and DetectorChannelFactory (2 tests)"
    - "HttpClientHandler.ClientCertificates + X509ChainTrustMode.CustomRootTrust for mTLS (D-18)"
    - "Exponential backoff in WaitForHealthyAsync: 1s/2s/4s/8s/16s/30s/max 60s"
    - "BackgroundService stub pattern: stable constructor signature, TODO marker for Plan 05"
    - "Singleton GrpcChannel from factory lambda in DI (one channel per process)"

key-files:
  created:
    - orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs
    - orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs
    - orchestrator/Argus.Orchestrator/Config/ConnectionSettings.cs
    - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
    - orchestrator/Argus.Orchestrator/Detection/DetectorChannelFactory.cs
    - orchestrator/Argus.Orchestrator/Detection/DetectionGateway.cs
    - orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs
    - orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs
    - orchestrator/Argus.Orchestrator.Tests/DetectorChannelFactoryTests.cs
    - entities.yaml
    - deploy/Dockerfile.orchestrator
    - deploy/docker-compose.edge.yml
  modified:
    - orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj (added YamlDotNet)
    - orchestrator/Argus.Orchestrator.Tests/Argus.Orchestrator.Tests.csproj (added Microsoft.Extensions.Logging)
    - orchestrator/Argus.Orchestrator/Program.cs (replaced TODO stub with full host wiring)

key-decisions:
  - "X509CertificateLoader.LoadCertificateFromFile is .NET 9+ only; used new X509Certificate2(path) ctor on .NET 8 (Rule 1 auto-fix)"
  - "YamlDotNet 16.3.0 selected (latest stable); UnderscoredNamingConvention maps snake_case YAML keys to PascalCase C# props"
  - "DetectorChannelFactory.Create exposes optional Action<HttpClientHandler> callback for test cert capture without leaking internal handler"
  - "CapturingLoggerProvider in tests: in-memory logger collects warning messages to assert covariates/groups warning fires"

metrics:
  duration: "6min"
  completed: "2026-06-10"
  tasks: 3
  files_modified: 14
---

# Phase 01 Plan 04: Orchestrator Host + mTLS Channel + Docker Image Summary

**Orchestrator host loads entities.yaml + env-based ConnectionSettings; builds a single mTLS GrpcChannel via HttpClientHandler.ClientCertificates + CustomRootTrust (D-18); gates startup on grpc.health.v1 SERVING (INFRA-07); ships as mcr.microsoft.com/dotnet/runtime:8.0-jammy-chiseled Docker image with edge docker-compose**

## Performance

- **Duration:** 6 min
- **Started:** 2026-06-10T08:07:04Z
- **Completed:** 2026-06-10T08:13:44Z
- **Tasks:** 3
- **Files modified:** 14

## Accomplishments

- `EntitiesConfig.cs` + `EntitiesConfigLoader.cs`: YamlDotNet deserializer with `UnderscoredNamingConvention`; parses `detectors` + `params`; `covariates`/`groups` keys parsed but ignored with structured warning (CONF-01/02, OBS-01)
- `HstParams.From()`: typed resolver for HST config with D-09/D-11/D-12 defaults (window=250, n_trees=25, high=0.7, low=0.3, min_consecutive=3, frozen_window=10, frozen_variance_threshold=0.001)
- `ConnectionSettings.cs`: all secrets from env vars (ARGUS_HA_TOKEN, ARGUS_MQTT_PASSWORD, etc.); no literal defaults for any credential (CONF-03); null if unset, validated at startup
- `LogEvents.cs`: 12 structured EventId definitions for OBS-01 (config load, channel establish, health check retry/success, HA listener start, MQTT connect)
- `DetectorChannelFactory.cs`: D-18 pattern exactly — `X509Certificate2.CreateFromPemFile` for client cert, `new X509Certificate2(path)` for CA, `HttpClientHandler.ClientCertificates.Add(clientCert)`, `ServerCertificateCustomValidationCallback` with `X509ChainTrustMode.CustomRootTrust` + `CustomTrustStore.Add(caCert)` + `chain.Build(cert)`. No Grpc.Core credential API used. Singleton.
- `DetectionGateway.cs`: holds singleton channel; exposes `DetectorServiceClient` + `HealthClient`; `WaitForHealthyAsync` polls `Health/Check` for `argus.v1.DetectorService` with 1s/2s/4s/8s/16s/30s/max 60s exponential backoff (INFRA-07, RES-03, T-04-04)
- `HaListenerWorker.cs`: `BackgroundService` stub; calls `WaitForHealthyAsync` then logs "detector healthy — HA subscription will start"; `TODO(plan05)` delay loop
- `Program.cs`: full host wiring — env ConnectionSettings, singleton GrpcChannel from factory, `AddHostedService<HaListenerWorker>`, `TODO(plan06)` MQTT comment
- `entities.yaml`: 3 placeholder entities (`sensor.salon_temperatura`, `sensor.outdoor_temperature`, `sensor.outdoor_humidity`) with `# Q1: replace placeholder entity_ids` comment
- `deploy/Dockerfile.orchestrator`: `mcr.microsoft.com/dotnet/sdk:8.0` build stage (Grpc.Tools generates C# stubs); `runtime:8.0-jammy-chiseled` final; chiseled distroless runtime
- `deploy/docker-compose.edge.yml`: edge-host service; `./certs:/certs:ro` volume; `../entities.yaml:/app/entities.yaml:ro`; all secrets via `${ARGUS_*}` substitution
- 5 new tests pass (3 EntitiesConfig + 2 DetectorChannelFactory); 9/9 total tests pass; `docker build` exits 0

## Task Commits

1. **Task 1 RED: Failing EntitiesConfig tests** — `fe45288` (test)
2. **Task 1 GREEN: entities.yaml loader, ConnectionSettings, LogEvents** — `4c99e5e` (feat)
3. **Task 2 RED: Failing DetectorChannelFactory tests** — `698be7c` (test)
4. **Task 2 GREEN: mTLS channel factory, DetectionGateway, host wiring** — `597df50` (feat)
5. **Task 3: Orchestrator Docker image and docker-compose.edge.yml** — `a146d4e` (feat)

## Files Created/Modified

- `orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs` — POCOs: EntitiesConfig, EntityConfig (with Covariates/Groups), DetectorConfig, HstParams resolver
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` — Load(path, logger); UnderscoredNamingConvention; covariates/groups warning; validation
- `orchestrator/Argus.Orchestrator/Config/ConnectionSettings.cs` — 11 env-var-sourced properties; no hard-coded secrets
- `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` — 12 EventId definitions for OBS-01
- `orchestrator/Argus.Orchestrator/Detection/DetectorChannelFactory.cs` — D-18 mTLS channel; Action<HttpClientHandler> test hook
- `orchestrator/Argus.Orchestrator/Detection/DetectionGateway.cs` — INFRA-07 health gate; exponential backoff; singleton channel + stubs
- `orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs` — BackgroundService stub; health gate call; TODO(plan05)
- `orchestrator/Argus.Orchestrator/Program.cs` — Full host wiring replacing Plan 01 stub
- `orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs` — 3 tests: params parse, covariates warning, defaults
- `orchestrator/Argus.Orchestrator.Tests/DetectorChannelFactoryTests.cs` — 2 tests: client cert count, CustomRootTrust chain policy
- `entities.yaml` — 3 placeholder entities with Q1 comment
- `deploy/Dockerfile.orchestrator` — Multi-stage: sdk:8.0 build + runtime:8.0-jammy-chiseled final
- `deploy/docker-compose.edge.yml` — Edge-host compose; certs volume; env-var secrets

## Decisions Made

- **X509Certificate2 ctor for CA cert**: `X509CertificateLoader.LoadCertificateFromFile` is .NET 9+. Used `new X509Certificate2(path)` which is the correct .NET 8 API.
- **DetectorChannelFactory test hook**: Exposed `Action<HttpClientHandler>? handlerCapture` parameter so tests can assert the handler state without reflection. Zero-cost at runtime; null by default.
- **YamlDotNet 16.3.0**: Latest stable at time of execution. `UnderscoredNamingConvention` handles `entity_id` -> `EntityId` mapping without manual configuration.
- **CapturingLoggerProvider in tests**: Simpler than Moq/NSubstitute — inline implementation that appends formatted messages to a list; sufficient for asserting warning text.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] X509CertificateLoader is .NET 9+, not available in .NET 8**
- **Found during:** Task 2 (first build attempt after writing DetectorChannelFactory.cs)
- **Issue:** The plan's interfaces block shows `X509CertificateLoader.LoadCertificateFromFile(...)` but this API was introduced in .NET 9. The project targets .NET 8. Build error: `CS0103`.
- **Fix:** Replaced with `new X509Certificate2(settings.TlsCa)` — the standard .NET 8 constructor. Functionally identical for PEM/DER cert files.
- **Files modified:** `DetectorChannelFactory.cs`, `DetectorChannelFactoryTests.cs`
- **Committed in:** 597df50 (Task 2 GREEN)

**2. [Rule 1 - Bug] xUnit2013 warning: Assert.Equal for collection size**
- **Found during:** Task 2 (build warning in test file)
- **Fix:** Changed `Assert.Equal(1, capturedHandler!.ClientCertificates.Count)` to `Assert.Single(capturedHandler!.ClientCertificates)`
- **Files modified:** `DetectorChannelFactoryTests.cs`
- **Committed in:** 597df50 (Task 2 GREEN)

## Known Stubs

- `orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs` — `ExecuteAsync` body after health gate is a `Task.Delay(30s)` loop with `// TODO(plan05): subscribe to HA state_changed`. Intentional: Plan 05 wires NetDaemon.Client subscription here. Constructor signature is stable.
- `orchestrator/Argus.Orchestrator/Program.cs` — `// TODO(plan06): register MqttPublisher` comment. Intentional: MQTT publisher DI registration is Plan 06's responsibility.

## Threat Flags

All STRIDE mitigations from the plan's threat model applied:
- T-04-01: `CustomRootTrust` + `CustomTrustStore.Add(caCert)` pins the Argus CA
- T-04-02: CONF-03 enforced — `ConnectionSettings.cs` has no string literals for tokens/passwords
- T-04-03: `DetectorChannelFactory.cs` does not contain the Grpc.Core credential API
- T-04-04: `WaitForHealthyAsync` uses exponential backoff (1->60s) preventing tight-loop hammering
- T-04-05: OBS-01 — `LogEvents` EventIds on health check attempts, channel establish, config load

## Self-Check: PASSED

- [x] `orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs` — exists, contains `HstParams`, `FrozenWindow`
- [x] `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` — exists, contains `covariates`, `CovariatesIgnored`
- [x] `orchestrator/Argus.Orchestrator/Config/ConnectionSettings.cs` — exists, contains `ARGUS_HA_TOKEN` reference; no literal secret
- [x] `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` — exists, contains `StartupHealthCheck`, `CovariatesIgnored`, `ChannelEstablished`
- [x] `orchestrator/Argus.Orchestrator/Detection/DetectorChannelFactory.cs` — exists, contains `ClientCertificates`, `CreateFromPemFile`, `CustomRootTrust`; Grpc.Core credential API absent
- [x] `orchestrator/Argus.Orchestrator/Detection/DetectionGateway.cs` — exists, contains `CheckAsync`, `Serving`, `WaitForHealthyAsync`
- [x] `orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs` — exists, `BackgroundService` subclass, `TODO(plan05)` present
- [x] `orchestrator/Argus.Orchestrator/Program.cs` — exists, contains `AddHostedService<HaListenerWorker>`, `GrpcChannel` singleton
- [x] `entities.yaml` — exists, contains `detectors`, `hst`, `Q1` comment
- [x] `deploy/Dockerfile.orchestrator` — exists, contains `8.0-jammy-chiseled`, `dotnet/sdk:8.0`, `dotnet publish`
- [x] `deploy/docker-compose.edge.yml` — exists, contains `ARGUS_DETECTOR_ENDPOINT`, `/certs/client.crt`; no literal secrets
- [x] Argus.Orchestrator.csproj contains `YamlDotNet`
- [x] `dotnet build orchestrator/Argus.Orchestrator.sln` — exit 0, 0 warnings, 0 errors
- [x] `dotnet test orchestrator/Argus.Orchestrator.sln` — 9/9 pass
- [x] `docker build -f deploy/Dockerfile.orchestrator -t argus-orchestrator:test .` — exit 0
- [x] Commits fe45288, 4c99e5e, 698be7c, 597df50, a146d4e — verified in git log
