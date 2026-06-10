---
phase: 01-foundations-streaming
plan: "01"
subsystem: infra
tags: [grpc, protobuf, dotnet, python, grpcio-tools, grpc-tools, mono-repo]

requires: []

provides:
  - "proto/argus.proto: single-source-of-truth gRPC contract with DoubleValue wrappers (D-01) and Timestamp (D-02)"
  - "orchestrator/: .NET 8 Worker service project with Grpc.Tools auto-generating C# stubs from argus.proto at build time"
  - "detector/: Python package with grpcio-tools codegen script producing importable argus_pb2/argus_pb2_grpc stubs"
  - "deploy/: tracked empty directory for later Docker/cert artifacts"
  - "Mono-repo skeleton: proto/, orchestrator/, detector/, deploy/ (D-19)"

affects: [01-02, 01-03, 01-04, 01-05, 01-06, 01-07, 01-08]

tech-stack:
  added:
    - "Grpc.Tools 2.80.0 — .proto -> C# MSBuild codegen"
    - "Grpc.Net.Client 2.80.0 — gRPC .NET client"
    - "Grpc.Net.ClientFactory 2.80.0 — factory for DI registration"
    - "Grpc.HealthCheck 2.80.0 — grpc.health.v1 stubs bundled"
    - "Google.Protobuf 3.31.1 — maps DoubleValue to double? in C#"
    - "NetDaemon.Client 23.46.0 — HA WebSocket client (loaded, not wired yet)"
    - "MQTTnet 5.1.0.1559 — MQTT client (loaded, not wired yet)"
    - "grpcio 1.81.0 + grpcio-tools 1.81.0 — Python gRPC runtime + codegen"
    - "grpcio-health-checking 1.81.0 — grpc.health.v1 Python stubs"
    - "xunit 2.9.3 — .NET test framework"
    - "pytest 8+ — Python test framework"
  patterns:
    - "Grpc.Tools Protobuf Include pattern: stubs generated at build time, never committed"
    - "grpcio-tools codegen script with relative-import fix for argus_pb2_grpc.py"
    - "pytest pythonpath configured in pyproject.toml so tests run from repo root"
    - "Google.Protobuf 3.x maps google.protobuf.DoubleValue to double? (nullable double) in C#"

key-files:
  created:
    - proto/argus.proto
    - proto/buf.yaml
    - orchestrator/Argus.Orchestrator.sln
    - orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/Argus.Orchestrator.Tests/Argus.Orchestrator.Tests.csproj
    - orchestrator/Argus.Orchestrator.Tests/ProtoCodegenTests.cs
    - detector/requirements.txt
    - detector/pyproject.toml
    - detector/argus_detector/__init__.py
    - detector/argus_detector/proto/__init__.py
    - detector/scripts/gen_proto.py
    - detector/tests/test_proto_codegen.py
    - deploy/.gitkeep
    - README.md
  modified:
    - .gitignore

key-decisions:
  - "Google.Protobuf 3.x maps DoubleValue to double? in C# (not a DoubleValue class instance); tests updated accordingly"
  - "Google.Protobuf version bumped to 3.31.1 (from planned 3.29.3) to satisfy Grpc.HealthCheck 2.80.0 transitive requirement"
  - "pytest pythonpath=['.' ] in pyproject.toml (rootdir=detector/) enables running tests from repo root"
  - "Both .sln (classic) and .slnx (new format) solution files created for tooling compatibility"

patterns-established:
  - "Proto stubs never committed; .gitignore excludes *_pb2.py, *_pb2_grpc.py, *_pb2.pyi"
  - "gen_proto.py patches grpcio-tools relative import bug (import argus_pb2 -> from argus_detector.proto import argus_pb2)"
  - "TDD for codegen verification: RED commit (tests only) -> GREEN commit (implementation + passing tests)"

requirements-completed: [INFRA-01, INFRA-02]

duration: 8min
completed: "2026-06-10"
---

# Phase 01 Plan 01: Mono-Repo Scaffold + gRPC Contract Summary

**argus.proto wire contract with DoubleValue wrappers (D-01) committed; .NET Grpc.Tools generates Argus.Detector.V1.* C# stubs at build time; Python grpcio-tools codegen produces importable argus_pb2/argus_pb2_grpc stubs; all 4+9 tests pass**

## Performance

- **Duration:** 8 min
- **Started:** 2026-06-10T07:05:41Z
- **Completed:** 2026-06-10T07:12:47Z
- **Tasks:** 3
- **Files modified:** 15

## Accomplishments

- Mono-repo skeleton (proto/, orchestrator/, detector/, deploy/) created per D-19
- `proto/argus.proto` encodes all locked wire-format decisions: DoubleValue wrappers (D-01), Timestamp (D-02), DetectorService bidi ScoreStream + Fit RPCs (D-04/D-05), no custom Health message (D-03)
- .NET solution builds with Grpc.Tools generating `Argus.Detector.V1.Point/Verdict/DetectorService` C# types from proto; 4 xunit tests pass
- Python `detector/scripts/gen_proto.py` generates importable `argus_pb2.py`/`argus_pb2_grpc.py`; 9 pytest tests pass from repo root
- `.gitignore` excludes generated stubs and `deploy/certs/` private key material (T-01-01, T-01-02 threat mitigations applied)

## Task Commits

1. **Task 1: Mono-repo skeleton + argus.proto** - `c010008` (feat)
2. **Task 2 RED: Failing proto codegen tests** - `ad3537d` (test)
3. **Task 2 GREEN: .NET solution with Grpc.Tools** - `e3a6f2f` (feat)
4. **Task 3: Python package with grpcio-tools** - `570ec83` (feat)

## Files Created/Modified

- `proto/argus.proto` - Single-source-of-truth gRPC contract; DoubleValue wrappers, Timestamp, DetectorService
- `proto/buf.yaml` - Contract anchor documentation
- `orchestrator/Argus.Orchestrator.sln` / `.slnx` - .NET solution files
- `orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj` - Worker SDK, all pinned NuGet deps, Protobuf Include for argus.proto
- `orchestrator/Argus.Orchestrator/Program.cs` - Minimal Host.CreateApplicationBuilder stub (TODO wave3)
- `orchestrator/Argus.Orchestrator.Tests/ProtoCodegenTests.cs` - 4 xunit tests verifying generated C# types
- `detector/requirements.txt` - Pinned Python deps (grpcio 1.81.0, river 0.25.0, pyod 3.6.0, joblib 1.5.3)
- `detector/pyproject.toml` - argus-detector package with pytest pythonpath config
- `detector/scripts/gen_proto.py` - grpcio-tools codegen + relative import patch
- `detector/tests/test_proto_codegen.py` - 9 pytest tests for Point/Verdict/DetectorServiceStub
- `.gitignore` - Added bin/, obj/, __pycache__/, generated stubs, cert private keys
- `README.md` - Repository layout and build instructions
- `deploy/.gitkeep` - Tracks deploy/ directory

## Decisions Made

- **Google.Protobuf version**: Bumped from planned 3.29.3 to 3.31.1 to satisfy `Grpc.HealthCheck 2.80.0`'s transitive requirement. All versions remain pinned and current.
- **DoubleValue C# mapping**: Google.Protobuf 3.x codegen maps `google.protobuf.DoubleValue` to `double?` (nullable double) in C#, not to a `DoubleValue` class instance. Tests were updated to use `double?` assignment. The D-01 guarantee is preserved: `null` = field absent on wire, `0.0` = explicit zero transmitted.
- **pytest pythonpath**: Set `pythonpath = ["."]` in `pyproject.toml` `[tool.pytest.ini_options]` so `argus_detector` is importable when running `python -m pytest detector/tests/` from the repo root (rootdir resolves to `detector/` because that's where `pyproject.toml` is).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Google.Protobuf version bumped to 3.31.1**
- **Found during:** Task 2 (first build attempt)
- **Issue:** `NU1605` error — Grpc.HealthCheck 2.80.0 requires Google.Protobuf >= 3.31.1 but plan specified 3.29.3
- **Fix:** Changed `Google.Protobuf` version to `3.31.1` in Argus.Orchestrator.csproj
- **Files modified:** orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj
- **Verification:** dotnet restore succeeded; `dotnet build` and `dotnet test` pass
- **Committed in:** e3a6f2f (Task 2 commit)

**2. [Rule 1 - Bug] Test assertions updated for double? codegen mapping**
- **Found during:** Task 2 (second build attempt after Grpc.Tools generated stubs)
- **Issue:** Tests used `new DoubleValue { Value = 0.0 }` but generated `Point.Value` is `double?`, not `DoubleValue`. Caused CS0029 type errors.
- **Fix:** Updated ProtoCodegenTests.cs to assign `double?` directly and assert null-assignability
- **Files modified:** orchestrator/Argus.Orchestrator.Tests/ProtoCodegenTests.cs
- **Verification:** 4 xunit tests pass; D-01 guarantee (score=0.0 not dropped) still verified by nullable check
- **Committed in:** e3a6f2f (Task 2 commit)

**3. [Rule 3 - Blocking] pytest pythonpath config added**
- **Found during:** Task 3 (test run from repo root)
- **Issue:** `python -m pytest detector/tests/` failed with `ModuleNotFoundError: No module named 'argus_detector'` because `detector/` wasn't in sys.path
- **Fix:** Added `[tool.pytest.ini_options] pythonpath = ["."]` to detector/pyproject.toml
- **Files modified:** detector/pyproject.toml
- **Verification:** `python -m pytest detector/tests/test_proto_codegen.py` passes from repo root; 9/9 tests pass
- **Committed in:** 570ec83 (Task 3 commit)

---

**Total deviations:** 3 auto-fixed (1 blocking version conflict, 1 bug in test assertions, 1 blocking import path)
**Impact on plan:** All auto-fixes necessary for build/test correctness. No scope creep. D-01 guarantee preserved despite mapping change.

## Known Stubs

- `orchestrator/Argus.Orchestrator/Program.cs` — minimal `Host.CreateApplicationBuilder` stub with `// TODO(wave3)` comment. This is intentional: Program.cs is a placeholder that compiles but has no worker registrations. Workers (HaListenerWorker, MqttPublisherWorker) are wired in wave 3 per plan spec.

## Threat Flags

No new security-relevant surface introduced beyond what the threat model covers. `.gitignore` exclusions for generated stubs (T-01-01) and cert keys (T-01-02) are in place.

## Issues Encountered

- Worker SDK (`Microsoft.NET.Sdk.Worker`) generates implicit global usings for `Microsoft.Extensions.*` which required the `Microsoft.Extensions.Hosting` package reference explicitly. Added as part of Task 2 setup. This is standard for Worker Service projects.

## Next Phase Readiness

- `proto/argus.proto` is locked and ready for all downstream plans
- .NET solution builds clean; generated C# types (`Argus.Detector.V1.*`) available for use in Plans 02-08
- Python stubs regeneratable via `python detector/scripts/gen_proto.py` before any Plan 04+ detector work
- Plan 02 (Docker scaffolding) can proceed independently in parallel wave

---
*Phase: 01-foundations-streaming*
*Completed: 2026-06-10*
