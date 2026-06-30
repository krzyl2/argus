---
phase: 02-v1-code-changes
plan: "01"
subsystem: orchestrator
tags: [grpc, mtls, local-mode, channel-factory, dotnet]
status: complete

dependency_graph:
  requires: []
  provides: [CODE-01, local-mode-grpc-channel]
  affects:
    - orchestrator/Argus.Orchestrator/Detection/DetectorChannelFactory.cs
    - orchestrator/Argus.Orchestrator.Tests/DetectorChannelFactoryTests.cs

tech_stack:
  added: []
  patterns:
    - "URI scheme discrimination (http vs https) before any cert load or channel creation"
    - "AppContext.SetSwitch for Http2UnencryptedSupport in local mode"
    - "Separate test class to isolate local-mode tests from FindCertDir static initializer"

key_files:
  modified:
    - orchestrator/Argus.Orchestrator/Detection/DetectorChannelFactory.cs
    - orchestrator/Argus.Orchestrator.Tests/DetectorChannelFactoryTests.cs

decisions:
  - "Separate test class DetectorChannelFactoryLocalModeTests added to same file — avoids FindCertDir static initializer throwing when certs are absent, while keeping local-mode tests in DetectorChannelFactoryTests.cs per plan intent"
  - "IsLocalMode uses Uri.UriSchemeHttp (scheme primary) + loopback host safety net — matches ARCHITECTURE.md pattern exactly"
  - "Http2UnencryptedSupportSwitch set inside IsLocalMode branch, before GrpcChannel.ForAddress — required for h2c per Pitfall 11"

metrics:
  duration_seconds: 95
  completed_date: "2026-06-30"
  tasks_completed: 2
  tasks_total: 2
  files_modified: 2
---

# Phase 02 Plan 01: DetectorChannelFactory Local-Mode Branch Summary

**One-liner:** Scheme-based conditional channel security — http://127.0.0.1 → insecure h2c (zero certs), https:// → existing mTLS path byte-for-byte unchanged.

## What Was Built

Modified `DetectorChannelFactory.cs` to discriminate on URI scheme before any certificate loading or channel creation:

- **`IsLocalMode(string endpoint)`** — private static helper; returns true for `http://` scheme or loopback host (`127.0.0.1`, `localhost`, `::1`). Scheme is primary discriminator; host is safety net.
- **Endpoint null-check moved to top** — runs before TLS checks so local mode does not require cert env vars.
- **Local-mode branch** — enables `AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true)` for h2c, then returns `GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions())` with no handler or cert loading.
- **Remote mTLS path** — completely unchanged from v1 (cert loading, `HttpClientHandler.ClientCertificates`, `CustomRootTrust` callback, `handlerCapture` invocation).
- **Logger overload** — logs "Insecure loopback gRPC channel" in local mode, "mTLS gRPC channel" in remote mode.
- **XML doc updated** — removed stale T-04-03 "no insecure path" claim; documents both modes.

Added `DetectorChannelFactoryLocalModeTests` class to the test file with three new tests:
1. KEY regression guard: `http://127.0.0.1:50051` + null TLS paths → non-null channel, no exception
2. `Http2UnencryptedSupport` AppContext switch is true after local-mode Create
3. Null endpoint → `ArgumentException` naming `ARGUS_DETECTOR_ENDPOINT`

## Verification

```
dotnet build Argus.Orchestrator.sln → success, 0 warnings, 0 errors
dotnet test --filter DetectorChannelFactory → 5/5 passed
  - DetectorChannelFactoryLocalModeTests.Create_WithHttpLoopback_NoCerts_ReturnsNonNullChannel [15ms]
  - DetectorChannelFactoryLocalModeTests.Create_WithHttpLoopback_SetsHttp2UnencryptedSupportSwitch [<1ms]
  - DetectorChannelFactoryLocalModeTests.Create_WithNullEndpoint_ThrowsArgumentException [13ms]
  - DetectorChannelFactoryTests.Create_WithValidCerts_SetsClientCertificateOnHandler [79ms]
  - DetectorChannelFactoryTests.Create_WithValidCerts_CustomValidationCallbackPinsArgusCA [54ms]
```

Manual read confirms the https mTLS branch (cert loading, CustomRootTrust callback, handlerCapture) is byte-for-byte identical to v1 — only the structure above it changed (endpoint check moved up, IsLocalMode branch inserted).

## Commits

| Hash | Type | Description |
|------|------|-------------|
| b444c95 | feat | add scheme-based local-mode branch to DetectorChannelFactory |
| c7c6da3 | test | add local-mode unit tests including zero-cert regression guard |

## Deviations from Plan

### Structural Decision (not a rule deviation)

**Test class separation:** The plan says "Extend DetectorChannelFactoryTests with new [Fact] methods". The existing class has a `FindCertDir()` static initializer that throws `DirectoryNotFoundException` if `deploy/certs/` is absent. Adding local-mode tests directly to that class would cause the static initializer to fail on any environment without the cert directory — defeating the requirement that local-mode tests run with zero cert files.

Resolution: Added `DetectorChannelFactoryLocalModeTests` as a second class in the same file (`DetectorChannelFactoryTests.cs`). This satisfies the spirit ("extend the test file") while preserving the zero-cert isolation requirement. Existing mTLS tests are entirely unchanged.

## Known Stubs

None. Both modes (insecure and mTLS) are fully wired.

## Threat Flags

No new security surface beyond what the plan's threat model covers:
- T-02-01: h2c on loopback — accepted (same container, no LAN exposure)
- T-02-02: Scheme discrimination prevents https endpoints from downgrading — mitigated
- T-02-03: Http2UnencryptedSupport switch is process-global but does not force cleartext on https — accepted

## Self-Check: PASSED

- [x] `orchestrator/Argus.Orchestrator/Detection/DetectorChannelFactory.cs` — exists and contains `IsLocalMode`
- [x] `orchestrator/Argus.Orchestrator.Tests/DetectorChannelFactoryTests.cs` — exists and contains `http://127.0.0.1`
- [x] Commit b444c95 — present in git log
- [x] Commit c7c6da3 — present in git log
- [x] All 5 DetectorChannelFactory tests pass
- [x] Solution builds with 0 warnings
