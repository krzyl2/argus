---
phase: 15-streaming-state-persistence-warm-up-backfill
plan: 03
subsystem: detection
tags: [grpc, protobuf, influxdb, flux, river, backfill, warm-up]

requires:
  - phase: 15-streaming-state-persistence-warm-up-backfill (plan 01)
    provides: "EntityDetector.n_seen/.window, DetectorRegistry.get_warmup_state/register_checkpoint, checkpoint_dirty"
  - phase: 15-streaming-state-persistence-warm-up-backfill (plan 02)
    provides: "proto/argus.proto: Point.params, Verdict.warmed_up/n_seen/window; EntityRuntimeState.ApplyVerdictWarmup/HstParams"
provides:
  - "proto/argus.proto: WarmupRequest/WarmupResponse messages + rpc Warmup on DetectorService"
  - "DetectorRegistry.warmup_one(entity_id, detector, values, params) -> (warmed_up, n_seen, window, skipped) — the n_seen==0 idempotency gate"
  - "DetectorServicer.Warmup RPC handler"
  - "InfluxDbReader.QueryHistoryAsync(entityId, lookback, limit, ct) — bounded ascending history query, sibling of QueryAsync"
  - "IInfluxDataSource.QueryHistoryAsync / IBatchDetectorClient.WarmupAsync / BatchDetectorClientAdapter.WarmupAsync"
  - "ConnectionSettings.BackfillEnabled/BackfillLookback (ARGUS_BACKFILL_ENABLED/ARGUS_BACKFILL_LOOKBACK)"
  - "ScoreStreamPipeline.PrimeFromHistoryAsync (internal) — one bounded, ascending, idempotent prime per stream open, feeding both the detector and FrozenSensorDetector"
  - "LogEvents.WarmupPrimed/WarmupSkipped/WarmupFailed (5017-5019)"
affects: [15-04-restart-tests-uat-ship]

tech-stack:
  added: []
  patterns:
    - "RED/GREEN split via git-diff-stash-restore (carried over from 15-02): production diff extracted, reverted, tests committed against the failing/non-compiling state, diff reapplied and committed as GREEN"
    - "internal (not private) method + InternalsVisibleTo as the testability seam for a call site that would otherwise require a live gRPC channel to exercise"

key-files:
  created: []
  modified:
    - proto/argus.proto
    - detector/argus_detector/registry.py
    - detector/argus_detector/servicer.py
    - detector/tests/test_warmup.py
    - detector/tests/test_proto_codegen.py
    - orchestrator/Argus.Orchestrator/Batch/InfluxDbReader.cs
    - orchestrator/Argus.Orchestrator/Batch/IInfluxDataSource.cs
    - orchestrator/Argus.Orchestrator/Batch/IBatchDetectorClient.cs
    - orchestrator/Argus.Orchestrator/Batch/BatchDetectorClientAdapter.cs
    - orchestrator/Argus.Orchestrator/Config/ConnectionSettings.cs
    - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs
    - orchestrator/Argus.Orchestrator.Tests/InfluxDbReaderTests.cs
    - orchestrator/Argus.Orchestrator.Tests/BatchSchedulerWorkerTests.cs
    - orchestrator/Argus.Orchestrator.Tests/GroupBatchSchedulerTests.cs
    - orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs
    - orchestrator/Argus.Orchestrator.Tests/ProtoCodegenTests.cs

key-decisions:
  - "WarmupRequest mirrors FitRequest field-for-field (entity_id, detector, params map, repeated Point history) per RESEARCH.md Open Question 1's recommendation — the strongest signal for consistency with FitRequest/ScoreBatchRequest's existing shape"
  - "WarmupResponse.skipped is a deliberate addition beyond the researcher's sketch (ok/error/n_seen/warmed_up only) — SC-8 requires 'no re-backfill' to be directly observable, not inferred from an unchanged n_seen counter"
  - "The n_seen==0 gate lives inside DetectorRegistry.warmup_one, not the servicer — mirrors _get_or_create's registry-owns-the-gate idiom (RESEARCH Open Question 2) so any future caller (a debug tool, CFG-04 hot-reload) inherits the same idempotency automatically"
  - "warmup_one holds the entity lock across the whole check-then-prime feed loop — the one deliberate deviation from the train-outside-lock idiom in this codebase, justified because the call happens once before ScoreStream opens and the work is bounded by the window size; without the lock held across the feed, two concurrent Warmup calls could both pass the n_seen==0 check"
  - "10-config-gen.sh gets NO ARGUS_BACKFILL_* lines — D-16 keeps both knobs out of the add-on options UI; the class defaults (BackfillEnabled=true, BackfillLookback=30d) are correct for the operator's deployment and absent env vars are the intended normal case"
  - "Lookback validation regex: ^\\d+[smhdw]$ (one or more digits + a single unit char) — applied IN ADDITION to the existing _safeFluxString injection guard, since a value like '30 days' passes _safeFluxString but is not a valid Flux duration"
  - "PrimeFromHistoryAsync made internal (not private) plus InternalsVisibleTo(Argus.Orchestrator.Tests) as the testability seam — RunEntityStreamAsync (the plan's specified call site) is private and constructs a live gRPC call from DetectionGateway, which cannot be exercised in a unit test without a real channel; tests construct the production ScoreStreamPipeline with a dummy GrpcChannel (never dialed) and call PrimeFromHistoryAsync directly"
  - "BuildHstParamsMap extracted as a shared private static helper so ToPoint's live-scoring params and PrimeFromHistoryAsync's backfill params physically cannot drift into two differently-configured detectors"

patterns-established:
  - "Registry-owned idempotency gate: any operation whose safety depends on 'only once per cold entity' lives inside DetectorRegistry as a single guarded method, never split across servicer + registry"

requirements-completed: [BACKFILL-01, BACKFILL-02, BACKFILL-03, BACKFILL-04]

coverage:
  - id: D1
    description: "WarmupRequest/WarmupResponse added additively to proto/argus.proto; rpc Warmup on DetectorService; both stub sets regenerated and round-trip tested"
    requirement: "BACKFILL-01"
    verification:
      - kind: unit
        ref: "detector/tests/test_proto_codegen.py::TestProtoCodegen::test_warmup_request_and_response_roundtrip"
        status: pass
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/ProtoCodegenTests.cs::WarmupRequest_AndWarmupResponse_RoundtripThroughSerialization"
        status: pass
    human_judgment: false
  - id: D2
    description: "DetectorRegistry.warmup_one primes a cold entity, is a no-op (skipped=true) on a re-call and on a checkpoint-restored entity, handles partial prime and empty history without raising"
    requirement: "BACKFILL-02"
    verification:
      - kind: unit
        ref: "detector/tests/test_warmup.py::TestWarmupOnePrimesColdEntity, TestWarmupOneCheckpointRestoredIsNeverRePrimed, TestWarmupOnePartialPrime, TestWarmupOneEmptyHistory, TestWarmupOneLeavesEntityDirty"
        status: pass
    human_judgment: false
  - id: D3
    description: "DetectorServicer.Warmup: empty entity_id -> INVALID_ARGUMENT abort; happy path maps registry tuple onto response; unexpected exception -> ok=False with error, never raises into gRPC"
    requirement: "BACKFILL-01"
    verification:
      - kind: unit
        ref: "detector/tests/test_warmup.py::TestServicerWarmupGuards, TestServicerWarmupHappyPath"
        status: pass
    human_judgment: false
  - id: D4
    description: "InfluxDbReader.QueryHistoryAsync builds the range/desc-sort/limit/asc-sort flux shape, reuses all four injection guards plus a new lookback-duration regex and a validated positive-int limit, degrades to empty on missing config/zero rows; QueryAsync's existing -24h/single-sort flux is pinned unchanged"
    requirement: "BACKFILL-03"
    verification:
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/InfluxDbReaderTests.cs (11 new QueryHistoryAsync/QueryAsync-pin tests)"
        status: pass
    human_judgment: false
  - id: D5
    description: "ScoreStreamPipeline.PrimeFromHistoryAsync primes the detector and FrozenSensorDetector once per stream open, in ascending order, and degrades silently (no exception, stream still opens) across all six failure modes: null history source, null detector client, BackfillEnabled=false, zero-row query, query throwing, WarmupAsync throwing RpcException"
    requirement: "BACKFILL-04"
    verification:
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs (13 new PrimeFromHistoryAsync tests + 1 DI-resolution test)"
        status: pass
    human_judgment: false

duration: 13min
completed: 2026-08-03
status: complete
---

# Phase 15 Plan 03: InfluxDB Backfill Summary

**A cold streaming detector is now primed from InfluxDB history before its first live reading: a new `Warmup` RPC gated detector-side on `n_seen == 0` (the sole idempotency mechanism, immune to who calls it or how often), a parametrized `QueryHistoryAsync` sibling to the untouched 24h batch query, and an orchestrator call site that also rides the same rows into `FrozenSensorDetector` — with every failure mode degrading to normal live warm-up instead of touching startup.**

## Performance

- **Duration:** 13 min (10:43:51–10:56:18 UTC+2)
- **Tasks:** 3 (each with RED test commit + GREEN implementation commit)
- **Files modified:** 18

## Accomplishments

- `proto/argus.proto`: `WarmupRequest` (entity_id, detector, params map, `repeated Point history`) and `WarmupResponse` (ok, error, n_seen, warmed_up, skipped) mirror `FitRequest`/`FitResponse` field-for-field; `rpc Warmup` added to `DetectorService` alongside the existing seven — `git diff proto/argus.proto` is additive-only, no field number renumbered
- `DetectorRegistry.warmup_one`: the `n_seen == 0` gate lives here, under the per-entity lock held across the whole check-then-prime feed — proven idempotent against a repeat call (SC-8) and against a checkpoint-restored entity (`register_checkpoint` seed with `n_seen=100` returns `skipped=True, n_seen=100` unchanged), proven to leave the entity dirty for the next `checkpoint_dirty` tick (SC-7 survives a subsequent restart), and proven safe on a partial prime (40/250) and an empty history list
- `DetectorServicer.Warmup`: mirrors `Fit`'s INVALID_ARGUMENT-abort / try-except-`ok=False` shape; logs only `entity_id`/`history_points`/`n_seen`/`skipped` (T-02-03 safe-field policy) — never raw values
- `InfluxDbReader.QueryHistoryAsync(entityId, lookback, limit, ct)`: sibling of `QueryAsync` — `QueryAsync`'s method body is byte-for-byte unchanged (pinned by a new regression test asserting `-24h` + exactly one sort). The new query builds `range(-{lookback}) -> sort(desc) -> limit(n) -> sort(asc)` — an explicit sort pair rather than `tail()`, per RESEARCH Pattern 5/Assumption A3. Reuses all four existing `_safeFluxString` injection guards verbatim, adds a Flux-duration-shape regex for `lookback`, and rejects `limit <= 0` with `ArgumentOutOfRangeException` before the limit is ever interpolated (D-13/ASVS V5: never a raw caller string)
- `ConnectionSettings.BackfillEnabled`(default `true`)/`BackfillLookback`(default `"30d"`); `Program.cs` reads `ARGUS_BACKFILL_ENABLED`/`ARGUS_BACKFILL_LOOKBACK` with fallback-to-`true` on an unparseable enabled value (D-15 — a bad value degrades, never fails startup); deliberately absent from `argus/config.yaml` and `10-config-gen.sh` (D-16)
- `ScoreStreamPipeline.PrimeFromHistoryAsync` (internal): called from `RunEntityStreamAsync` immediately before each `LiveScoreStreamCall` opens. Bails out silently when either backfill dependency is null or `BackfillEnabled` is false; queries with the entity's configured window as the limit (D-13 — exactly the points needed to reach `warmed_up`); feeds the same history rows into both the `WarmupRequest.history` and `entityState.FrozenDetector.AddReading` in one loop (D-14); the whole body is one try/catch logging under `WarmupFailed` so InfluxDB unreachable/erroring, a zero-row/partial result, or `WarmupAsync` throwing `RpcException` can never prevent the stream from opening
- `Program.cs`'s `AddSingleton<ScoreStreamPipeline>()` replaced with an explicit factory: `GetRequiredService` for the required deps, `GetService` for the two backfill deps — this is what gives the no-Influx streaming-only deployment its degrade path for free (both resolve to null, no separate feature check), proven by a new DI-resolution test that builds a `ServiceCollection` with neither `IInfluxDataSource` nor `IBatchDetectorClient` registered and asserts the pipeline still resolves

## Task Commits

Each task followed RED (failing test) → GREEN (implementation) TDD gates:

1. **Task 1: Warmup RPC end-to-end with the detector-side idempotency gate** — `bae7f7d` (test, RED) → `04ad489` (feat, GREEN)
2. **Task 2: Parametrized Influx history query + backfill knobs** — `70ea5f2` (test, RED) → `d47ca03` (feat, GREEN)
3. **Task 3: Orchestrator call site — prime detector + frozen window** — `62cc2f1` (test, RED) → `170ea37` (feat, GREEN)

## Files Created/Modified

- `proto/argus.proto` — `WarmupRequest`/`WarmupResponse` + `rpc Warmup`
- `detector/argus_detector/registry.py` — `warmup_one`
- `detector/argus_detector/servicer.py` — `Warmup` handler
- `detector/tests/test_warmup.py` (new) — registry + servicer warm-up tests
- `detector/tests/test_proto_codegen.py` — `WarmupRequest`/`WarmupResponse` round-trip + stub-exposure assertions
- `orchestrator/Argus.Orchestrator/Batch/InfluxDbReader.cs` — `QueryHistoryAsync` + `_safeFluxDuration` regex
- `orchestrator/Argus.Orchestrator/Batch/IInfluxDataSource.cs` — `QueryHistoryAsync` interface member
- `orchestrator/Argus.Orchestrator/Batch/IBatchDetectorClient.cs` — `WarmupAsync` interface member
- `orchestrator/Argus.Orchestrator/Batch/BatchDetectorClientAdapter.cs` — `WarmupAsync` implementation
- `orchestrator/Argus.Orchestrator/Config/ConnectionSettings.cs` — `BackfillEnabled`/`BackfillLookback`
- `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` — `WarmupPrimed`/`WarmupSkipped`/`WarmupFailed` (5017-5019)
- `orchestrator/Argus.Orchestrator/Program.cs` — `ARGUS_BACKFILL_*` env reads + explicit `ScoreStreamPipeline` factory
- `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs` — `PrimeFromHistoryAsync`, `BuildHstParamsMap`, three optional trailing production-constructor params
- `orchestrator/Argus.Orchestrator.Tests/InfluxDbReaderTests.cs` — `QueryHistoryAsync` shape/validation/degrade tests + `QueryAsync` regression pin
- `orchestrator/Argus.Orchestrator.Tests/BatchSchedulerWorkerTests.cs` / `GroupBatchSchedulerTests.cs` — existing fakes stubbed for the two new interface members
- `orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs` — `FakeInfluxHistorySource`/`FakeWarmupDetectorClient` + 14 new tests
- `orchestrator/Argus.Orchestrator.Tests/ProtoCodegenTests.cs` — `WarmupRequest`/`WarmupResponse` round-trip + stub-exposure assertions

## Decisions Made

- `WarmupRequest`/`WarmupResponse` shape and the `skipped` field addition — see key-decisions above
- `n_seen == 0` gate placed inside `DetectorRegistry.warmup_one`, holding the entity lock across the whole feed (documented deviation from train-outside-lock, justified in the method's own docstring)
- `10-config-gen.sh` gets no `ARGUS_BACKFILL_*` lines (D-16)
- Lookback validation regex: `^\d+[smhdw]$`
- `PrimeFromHistoryAsync` made `internal` + `InternalsVisibleTo` for direct testability without a live gRPC channel
- `BuildHstParamsMap` extracted as a shared helper between `ToPoint` and `PrimeFromHistoryAsync`

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] `DetectorServiceClient_ExposesWarmupRpc` test's initial `GetMethod` overload lookup failed on the generated client's multi-parameter RPC method signature**
- **Found during:** Task 1 (`.NET stub regeneration + round-trip test`)
- **Issue:** `clientType.GetMethod("Warmup", new[] { typeof(WarmupRequest) })` returned null — Grpc.Tools generates the sync client method with a `(request, headers, deadline, cancellationToken)` signature, not a single-parameter overload.
- **Fix:** Changed the assertion to `GetMethods().Where(m => m.Name == "Warmup")` plus `Assert.NotEmpty`, and added the `using System.Linq;` this required.
- **Files modified:** `orchestrator/Argus.Orchestrator.Tests/ProtoCodegenTests.cs`
- **Verification:** Test passes; `dotnet test --filter FullyQualifiedName~ProtoCodegen` → 7/7 passed.
- **Committed in:** `04ad489` (Task 1 GREEN commit)

---

**Total deviations:** 1 auto-fixed (1 bug in a self-authored test assertion, caught and fixed before the GREEN commit)
**Impact on plan:** Test-infrastructure scoped only; no production code was changed to work around it.

## Issues Encountered

**Pre-existing flaky test, unrelated to this plan's changes.** `ScoreStreamPipelineTests.RunAsync_CompleteAsyncCalledBeforeReadTaskAwaited` failed intermittently while running the Task 3 suite. Investigated via `git stash` to run the identical test against the pre-15-03 codebase: it fails identically in isolation there too (deterministic `--filter`-scoped run), and passes most of the time as part of the full 455-test suite (thread-pool scheduling timing). Root cause: the test's correctness depends on the read-task's `Task.Run` completing *after* the write loop's `CompleteAsync()` call — an ordering that is not actually guaranteed by the production code's design (both tasks race), only usually true under a busier thread pool. This is a pre-existing test-design issue in a code path (`RunAsync(IScoreStreamCall,...)`'s own internals) this plan's Task 3 never touched — confirmed out of scope per the scope-boundary rule ("failures in unrelated files/paths are out of scope; do not fix them"). Logged to `.planning/WINDOWS.md` entry #2 (kind: `deviation`, status: `open`) for a follow-up fix (e.g. an explicit synchronization point in the test rather than relying on scheduling luck).

## Known Stubs

None — no hardcoded empty values, placeholder text, or unwired data sources were introduced.

## Threat Flags

None beyond the plan's own `<threat_model>` table — T-15-03-01..07 and T-15-03-SC are addressed exactly as specified: the four existing `_safeFluxString` checks are reused verbatim (T-15-03-01), the new lookback regex plus injection guard covers T-15-03-02, `limit` is typed `int` and range-checked before interpolation (T-15-03-03), the `n_seen==0` gate under the entity lock covers T-15-03-04, the full-body try/catch in `PrimeFromHistoryAsync` covers T-15-03-05, no raw sensor values appear in any new log line (T-15-03-07), and no package-manager installs occurred this plan (T-15-03-SC).

## User Setup Required

None — no external service configuration required. `ARGUS_BACKFILL_ENABLED`/`ARGUS_BACKFILL_LOOKBACK` have correct defaults (`true`/`30d`) and are not surfaced in the add-on options UI per D-16.

## Next Phase Readiness

- 15-04 (restart tests, UAT, ship) can now write executable restart/crash tests against the full warm-up/checkpoint/backfill stack this plan and 15-01/15-02 built: SC-7 (new entity + history → warmed_up on first live reading), SC-8 (restart with existing checkpoint → no re-backfill), and SC-9 (Influx unavailable → normal warm-up, WARN only) all have unit-level executable proofs already; 15-04's job is the end-to-end/live-HA layer on top.
- Full detector suite: 257 passed, 1 skipped (pre-existing win32 SIGTERM platform skip from 15-01, unrelated to this plan).
- Full orchestrator suite: 455 passed, 0 failed, 0 skipped (the one intermittently-failing pre-existing test is documented above and in `.planning/WINDOWS.md`, not counted as a regression).
- `dotnet build orchestrator/Argus.Orchestrator.sln`: 0 warnings, 0 errors.
- Scope fence honored: `git status --short` shows no changes under `orchestrator/ui/`, `argus/config.yaml`, or `argus/rootfs/`.

---
*Phase: 15-streaming-state-persistence-warm-up-backfill*
*Completed: 2026-08-03*

## Self-Check: PASSED

All 18 modified/created files confirmed present on disk; all 6 task commit hashes
(`bae7f7d`, `04ad489`, `70ea5f2`, `d47ca03`, `62cc2f1`, `170ea37`) confirmed present in git log.
