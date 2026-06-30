---
phase: 02-v1-code-changes
verified: 2026-06-30T00:00:00Z
status: passed
score: 4/4 must-haves verified
behavior_unverified: 0
overrides_applied: 0
---

# Phase 2: v1 Code Changes Verification Report

**Phase Goal:** The orchestrator selects gRPC channel security by URI scheme (http → insecure, https → mTLS); the detector binds to a configurable address and stores models under a configurable root — both changes driven by the env var contract from Phase 1.
**Verified:** 2026-06-30
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|---------|
| 1 | Orchestrator builds a working gRPC channel for `http://127.0.0.1:50051` with zero cert files present and no exception thrown | ✓ VERIFIED | `DetectorChannelFactory.cs` IsLocalMode branch returns insecure channel; `DetectorChannelFactoryLocalModeTests.Create_WithHttpLoopback_NoCerts_ReturnsNonNullChannel` passes |
| 2 | Orchestrator still requires and loads mTLS certs for `https://` endpoints — the v1 path is byte-for-byte unchanged | ✓ VERIFIED | Lines 52-89 of `DetectorChannelFactory.cs` are the unchanged v1 mTLS block; `DetectorChannelFactoryTests.Create_WithValidCerts_*` (2 tests) pass |
| 3 | The Http2UnencryptedSupport AppContext switch is enabled in local mode | ✓ VERIFIED | Line 48: `AppContext.SetSwitch(Http2UnencryptedSupportSwitch, true)`; `Create_WithHttpLoopback_SetsHttp2UnencryptedSupportSwitch` passes |
| 4 | Detector binds to `127.0.0.1` when `ARGUS_GRPC_BIND=127.0.0.1` and to `[::]` when unset | ✓ VERIFIED | `config.py` line 25 reads env with `[::]` default; `server.py` lines 106/112 use `config.grpc_bind` in both port add calls; functional health-check test passes at `127.0.0.1` |
| 5 | Detector saves and loads model files from path set in `ARGUS_MODEL_ROOT`; defaults to `/var/argus/models` when unset | ✓ VERIFIED | `config.py` line 30 reads env with `/var/argus/models` default; `serve()` passes `pathlib.Path(config.model_root)` to `create_server`; ModelStore round-trip test passes under tmp_path |

**Score:** 4/4 success criteria verified (5/5 plan must-haves verified)

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `orchestrator/Argus.Orchestrator/Detection/DetectorChannelFactory.cs` | Scheme-based conditional channel security; contains `IsLocalMode` | ✓ VERIFIED | 122 lines; `IsLocalMode` helper at line 116; local-mode branch at line 45; mTLS path at lines 52-89 |
| `orchestrator/Argus.Orchestrator.Tests/DetectorChannelFactoryTests.cs` | Local-mode regression tests including zero-cert build; contains `http://127.0.0.1` | ✓ VERIFIED | `DetectorChannelFactoryLocalModeTests` class (lines 115-176) with 3 new tests; `http://127.0.0.1` at lines 128/148 |
| `detector/argus_detector/config.py` | `grpc_bind` and `model_root` config fields read from env with v1 defaults; contains `ARGUS_GRPC_BIND` | ✓ VERIFIED | Lines 25/30 add both fields with correct defaults |
| `detector/argus_detector/server.py` | Bind address from `config.grpc_bind`; `serve()` passes `config.model_root`; contains `config.grpc_bind` | ✓ VERIFIED | Lines 106/112 use `config.grpc_bind`; line 126 passes `pathlib.Path(config.model_root)` |
| `detector/tests/test_local_mode.py` | Unit tests for configurable bind address and model_root; contains `ARGUS_GRPC_BIND` | ✓ VERIFIED | 7 tests covering config defaults, overrides, functional loopback bind, ModelStore round-trip |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `DetectorChannelFactory.cs` | `ConnectionSettings.cs` | `settings.DetectorEndpoint` scheme/host discrimination before any cert load | ✓ WIRED | Lines 42, 45, 49, 86, 98 all use `settings.DetectorEndpoint`; `IsLocalMode` branch precedes TLS checks |
| `detector/argus_detector/server.py` | `detector/argus_detector/config.py` | `config.grpc_bind` in add_secure_port / add_insecure_port; `serve()` reads `config.model_root` | ✓ WIRED | `config.grpc_bind` at lines 106 and 112; `config.model_root` at line 126 via `pathlib.Path(config.model_root)` |

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| 5 DetectorChannelFactory tests (3 local-mode + 2 mTLS) | `dotnet test --filter FullyQualifiedName~DetectorChannelFactory` | 5/5 passed, 0 failures, 126 ms | ✓ PASS |
| 7 test_local_mode.py tests (bind config, model_root config, loopback health, ModelStore round-trip) | `python -m pytest detector/tests/test_local_mode.py -v` | 7/7 passed, 1.81s | ✓ PASS |
| Full detector test suite (backward compat) | `python -m pytest detector/tests/ -q` | 112/112 passed, 8 pre-existing PyOD RuntimeWarnings | ✓ PASS |
| Full orchestrator suite | `dotnet test Argus.Orchestrator.sln --no-build` | 76/78 passed; 2 DiscoveryPayloadTests failures are pre-existing v1 issues (KeyNotFoundException on dict key); no Phase 2 test fails | ✓ PASS (Phase 2 scope) |

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|---------|
| CODE-01 | 02-01 | Orchestrator uses insecure loopback channel for `http://`; retains mTLS for `https://` | ✓ SATISFIED | `IsLocalMode` helper + insecure branch in `DetectorChannelFactory.cs`; 5 tests pass |
| CODE-02 | 02-02 | Detector bind address configurable via `ARGUS_GRPC_BIND`; binds `127.0.0.1` in local mode | ✓ SATISFIED | `config.py` + `server.py` wiring; 3 config tests + functional loopback test pass |
| CODE-03 | 02-02 | Detector model root configurable via `ARGUS_MODEL_ROOT`; persists to `/data/models` in add-on | ✓ SATISFIED | `config.py` + `serve()` wiring; ModelStore round-trip test passes under tmp_path |

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `DetectorChannelFactoryTests.cs` | 85 | Word "placeholder" in comment | ℹ️ Info | Describes the test cert's hostname field ("placeholder IP/hostname") — documentation, not a code stub. Not a debt marker. |

No `TBD`, `FIXME`, or `XXX` markers in any Phase 2 file. No empty implementations. No hardcoded empty returns in production paths.

### Human Verification Required

None. All success criteria are verifiable programmatically and tests confirm behavior end-to-end.

---

## Notes

**Pre-existing failures excluded from scope:** `DiscoveryPayloadTests.BinarySensorPayload_AvailabilityTopicIsBridgeLevel` and `DiscoveryPayloadTests.BinarySensorPayload_PayloadAvailableOnline` fail with `KeyNotFoundException` at line 70/81 of `DiscoveryPayloadTests.cs`. These are pre-existing v1 issues unrelated to Phase 2 changes (no Phase 2 plan touches `DiscoveryPayloadTests.cs` or the discovery payload serializer).

**Test class separation (plan deviation accepted):** `DetectorChannelFactoryLocalModeTests` is a separate class in the same file rather than extending `DetectorChannelFactoryTests`. This was necessary because the existing class has a `FindCertDir()` static initializer that throws `DirectoryNotFoundException` when `deploy/certs/` is absent — which would break zero-cert local-mode tests on any CI environment. The spirit of the plan (extend the test file) is met; the behavior is correct.

**Commits verified in git log:** b444c95, c7c6da3 (02-01); 89faf9a, 074219a, 6a32a00 (02-02).

---

_Verified: 2026-06-30_
_Verifier: Claude (gsd-verifier)_
