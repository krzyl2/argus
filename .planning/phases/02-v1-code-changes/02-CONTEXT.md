# Phase 2: v1 Code Changes - Context

**Gathered:** 2026-06-30
**Status:** Ready for planning
**Mode:** Auto-generated (infrastructure phase — discuss skipped)

<domain>
## Phase Boundary

Make the two v1 source changes the add-on needs, driven by the Phase 1 env-var contract:
- **CODE-01 (orchestrator):** `DetectorChannelFactory` selects gRPC channel security by URI scheme — `http://` → insecure loopback channel (no certs, enable unencrypted HTTP/2), `https://` → existing mTLS path unchanged.
- **CODE-02 (detector):** bind address configurable via `ARGUS_GRPC_BIND` (binds `127.0.0.1` in local mode; defaults to `[::]` when unset).
- **CODE-03 (detector):** model root configurable via `ARGUS_MODEL_ROOT` → `/data/models/`; defaults to the v1 path (`/var/argus/models`) when unset.

Out of scope: s6 service wiring / readiness gate (Phase 3), add-on packaging (Phase 1, done), CI (Phase 4). Both v1 behaviors must remain backward-compatible — unset env vars and `https://` endpoints behave exactly as v1.

</domain>

<decisions>
## Implementation Decisions

### Claude's Discretion
All implementation choices are at Claude's discretion — pure infrastructure phase, no user-facing behavior. Use the ROADMAP phase goal, success criteria, the codebase conventions, and the resolved findings in `.planning/research/ARCHITECTURE.md` (the exact lines in `DetectorChannelFactory.cs` that throw without TLS; `server.py`'s existing `tls=False`/`add_insecure_port` path; `MODEL_ROOT` hardcode in `model_store.py`) to guide the changes.

Key constraints (from PROJECT.md v2.0 milestone decisions + ARCHITECTURE.md):
- Conditional mTLS is an intended override of locked D4 — keep mTLS for `https://`, bypass only for loopback `http://`. Requires `AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true)` in local mode (h2c).
- Scheme-level discrimination must happen BEFORE channel creation, not just before cert loading (PITFALLS.md pitfall 2).
- Backward compatibility: unset `ARGUS_GRPC_BIND` → `[::]`; unset `ARGUS_MODEL_ROOT` → `/var/argus/models`.
- New unit tests on both sides; the orchestrator negative test (local mode, zero cert files, must succeed) is the key regression guard.

</decisions>

<code_context>
## Existing Code Insights

### Files to modify
- `orchestrator/Argus.Orchestrator/Detection/DetectorChannelFactory.cs` — currently throws if TLS vars absent (`T-04-03: No insecure GrpcChannel path`); add the `http://` loopback branch.
- `detector/argus_detector/config.py` — add `ARGUS_GRPC_BIND`, `ARGUS_MODEL_ROOT`.
- `detector/argus_detector/server.py` — use `config.grpc_bind`; pass `config.model_root` through.
- `detector/argus_detector/model_store.py` — `MODEL_ROOT` hardcode → configurable.

### Established Patterns
- Detector `create_server(tls=False)` already calls `add_insecure_port` — Python side is nearly ready.
- Orchestrator tests live in `orchestrator/Argus.Orchestrator.Tests/` (e.g. `DetectorChannelFactoryTests.cs`); detector tests in `detector/tests/`.

### Integration Points
- The env vars consumed here (`ARGUS_GRPC_BIND`, `ARGUS_MODEL_ROOT`, and the `http://127.0.0.1:50051` endpoint) are written by Phase 1's config-gen for local mode.

</code_context>

<specifics>
## Specific Ideas

No specific requirements — infrastructure phase. See `.planning/research/ARCHITECTURE.md` and `PITFALLS.md` for the concrete, codebase-grounded change locations.

</specifics>

<deferred>
## Deferred Ideas

None — phase scope is the three code changes.

</deferred>
