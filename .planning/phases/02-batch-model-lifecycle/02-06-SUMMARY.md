---
phase: 02-batch-model-lifecycle
plan: "06"
subsystem: orchestrator+detector-python
tags: [resilience, tdd, mqtt-discovery, model-store, idempotency, RES-02]
dependency_graph:
  requires:
    - phase: 02-04
      provides: "BatchSchedulerWorker, IBatchDetectorClient"
    - phase: 02-05
      provides: "DetectorRegistry fit_one, servicer RPCs, server.py MDL-03 gate"
    - phase: 01-08
      provides: "DiscoveryPublisher.PublishAllAsync, retain=true, unique_id"
  provides:
    - "Res02ResilienceTests: DiscoveryIdempotency (2 tests), RetainFlag (1 test)"
    - "test_restart_resilience.py: 5 tests (empty root no-op, nonexistent root, preloaded model, multiple models)"
    - "DiscoveryPublisher.PublishAllAsync delegate overload (testable, no live broker)"
    - "server._argus_registry attribute (test introspection of MDL-03 gate)"
  affects: []
tech_stack:
  added: []
  patterns:
    - "Func<string,string,bool,CancellationToken,Task> delegate overload for MQTT publish testability"
    - "server._argus_registry attribute for post-startup registry introspection"
    - "port=0 (ephemeral) in Python tests to avoid gRPC port binding conflicts"
key_files:
  created:
    - orchestrator/Argus.Orchestrator.Tests/Res02ResilienceTests.cs
    - detector/tests/test_restart_resilience.py
  modified:
    - orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs
    - detector/argus_detector/server.py
key_decisions:
  - "DiscoveryPublisher delegate overload: MqttConnection cannot be instantiated in tests without a live broker; added Func<string,string,bool,CancellationToken,Task> overload that production code delegates to — zero behavior change"
  - "server._argus_registry: minimal test-accessor attribute on grpc.Server object; exposes the DetectorRegistry built during MDL-03 startup gate for assertion without starting the server"
  - "port=0 in Python tests: create_server binds immediately on add_insecure_port; port=0 lets OS pick ephemeral port to avoid conflicts across tests"
  - "slug-based has_model assertions: load_all_into registers by slug (entity_id.replace('.','_')), so tests assert registry.has_model(slug, detector) not registry.has_model(entity_id, detector)"
requirements_completed: [RES-02]
duration: "~3 min"
completed: "2026-06-10"
---

# Phase 2 Plan 06: RES-02 Resilience Tests Summary

**RES-02 verified end-to-end: MQTT discovery restart idempotency (unique_id stable, retain=true) and detector startup model load gate (MDL-03: registry populated before SERVING).**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-06-10T00:00:00Z
- **Completed:** 2026-06-10
- **Tasks:** 3 (Task 1 TDD RED+GREEN, Task 2 TDD RED+GREEN, Task 3 regression sweep)
- **Files modified:** 4

## Accomplishments

- `Res02ResilienceTests.cs`: 3 tests — `DiscoveryIdempotency_UniqueIdsIdenticalAcrossTwoPublishes`, `DiscoveryIdempotency_TwoEntitiesProduceFourPayloads_PerPublish`, `RetainFlag_AllDiscoveryPayloadsHaveRetainTrue`
- `test_restart_resilience.py`: 5 tests — empty model root no-op (2 assertions), nonexistent root no-op, preloaded model in registry, multiple models all loaded
- Added `DiscoveryPublisher.PublishAllAsync` delegate overload so tests capture publish calls without a live MQTT broker
- Added `server._argus_registry` attribute to expose the registry built during MDL-03 startup gate
- Full regression: .NET 73 pass (75 total, 2 pre-existing DiscoveryPayload failures unchanged since 02-02); Python 105 pass, 0 failures

## Task Commits

1. **Task 1 RED — Res02ResilienceTests.cs failing** - `b8ab596` (test)
2. **Task 1 GREEN — DiscoveryPublisher delegate overload** - `d53fa33` (feat)
3. **Task 2 RED — test_restart_resilience.py failing** - `209f8f0` (test)
4. **Task 2 GREEN — server._argus_registry attribute** - `992d9ec` (feat)

## Files Created/Modified

- `orchestrator/Argus.Orchestrator.Tests/Res02ResilienceTests.cs` — 3 RES-02 orchestrator tests
- `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs` — added delegate overload; production MqttConnection overload delegates to it
- `detector/tests/test_restart_resilience.py` — 5 RES-02 detector tests
- `detector/argus_detector/server.py` — added `server._argus_registry = registry` after MDL-03 gate

## Decisions Made

- **Delegate overload for DiscoveryPublisher:** `MqttConnection` wraps `IMqttClient` from MQTTnet and calls `_client.PublishAsync` which requires a live broker. Since the test project has no mocking library, an overload accepting `Func<string, string, bool, CancellationToken, Task>` was added. The production overload calls the delegate overload via `mqtt.PublishAsync`. Zero production behavior change.
- **`server._argus_registry` attribute:** grpc.Server is a concrete class; the cleanest test-accessible hook is an instance attribute. Plan spec said: "store registry on server object (e.g. `server._argus_registry = registry`)". Applied exactly.
- **`port=0` in tests:** `create_server` calls `server.add_insecure_port` immediately, which binds the OS port before `server.start()`. Running multiple tests with the same default port (50051) causes `RuntimeError: Failed to bind`. Port=0 lets the OS pick an ephemeral port per test.
- **Slug-based `has_model`:** `ModelStore.load_all_into` uses the directory name (`slug`) as the registry key, not `entity_id`. Tests verified with `slug = entity_id.replace('.', '_')`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Port binding conflict in Python tests**
- **Found during:** Task 2 RED run
- **Issue:** `create_server(tls=False, model_root=tmp_path)` uses default `port=50051`; multiple test instances tried to bind the same port → `RuntimeError: Failed to bind to address [::]:50051`
- **Fix:** Changed all `create_server` calls in test to use `port=0` (OS-assigned ephemeral port)
- **Files modified:** `detector/tests/test_restart_resilience.py`
- **Commit:** 209f8f0 (RED test updated before GREEN)

---

**Total deviations:** 1 auto-fixed (Rule 1 — test infrastructure)
**Impact:** No production code change; test-only fix.

## TDD Gate Compliance

- Task 1: RED commit `b8ab596` (CS1660 compile errors — no delegate overload) → GREEN commit `d53fa33` (3 tests pass)
- Task 2: RED commit `209f8f0` (AttributeError: '_Server' has no _argus_registry; 3/5 fail) → GREEN commit `992d9ec` (5/5 pass)
- Task 3: tdd="false" per plan spec — regression sweep only

## Known Stubs

None — all tests verify real behavior against real implementations. No placeholder data.

## Threat Flags

None — no new network endpoints, auth paths, or production data flows. Test-only additions.

## Self-Check: PASSED

Files exist:
- `orchestrator/Argus.Orchestrator.Tests/Res02ResilienceTests.cs` — FOUND
- `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs` (modified) — FOUND
- `detector/tests/test_restart_resilience.py` — FOUND
- `detector/argus_detector/server.py` (modified) — FOUND

Commits exist:
- b8ab596 — Task 1 RED
- d53fa33 — Task 1 GREEN
- 209f8f0 — Task 2 RED
- 992d9ec — Task 2 GREEN

Test counts:
- .NET: 73 passed, 2 pre-existing failures (DiscoveryPayloadTests — unchanged from 02-02), 0 new failures
- Python: 105 passed, 0 failures
- Both counts exceed Phase 1 baseline (62 .NET per 01-08-SUMMARY)
