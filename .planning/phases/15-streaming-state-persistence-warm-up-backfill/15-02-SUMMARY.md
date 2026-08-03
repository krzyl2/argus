---
phase: 15-streaming-state-persistence-warm-up-backfill
plan: 02
subsystem: detection
tags: [protobuf, grpc, river, hst, warm-up, wire-contract]

requires:
  - phase: 15-streaming-state-persistence-warm-up-backfill (plan 01)
    provides: "EntityDetector.n_seen/.window accessors, DetectorRegistry.get_warmup_state/register_checkpoint"
provides:
  - "proto/argus.proto: Point.params (map, field 4), Verdict.warmed_up/n_seen/window (fields 9-11)"
  - "servicer.ScoreStream forwards dict(point.params) into registry.score_one and populates the three new Verdict fields from registry.get_warmup_state (read AFTER scoring)"
  - "EntityRuntimeState.ApplyVerdictWarmup(warmedUp, nSeen, window) — replaces the deleted RecordReading() self-counter"
  - "EntityRuntimeState.HstParams read-only property — resolved config threaded to ToPoint without a RunAsync signature change"
  - "ScoreStreamPipeline.ToPoint(reading, hstParams) emits Point.params[window]/[n_trees]; EntityStatusCache.Set relocated from the write loop into ProcessVerdictAsync"
affects: [15-03-influxdb-backfill, 15-04-restart-tests-uat-ship]

tech-stack:
  added: []
  patterns:
    - "RED/GREEN split via git-diff-stash-restore: for each tdd=true task, the production diff was extracted, reverted, tests committed against the failing/non-compiling state, then the diff reapplied and committed as GREEN — makes the RED commit meaningful in a compiled language where 'test fails' often means 'does not compile yet'"

key-files:
  created: []
  modified:
    - proto/argus.proto
    - detector/argus_detector/servicer.py
    - detector/tests/test_proto_codegen.py
    - detector/tests/test_servicer.py
    - detector/tests/test_score_zero_wire.py
    - orchestrator/Argus.Orchestrator/Detection/EntityRuntimeState.cs
    - orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs
    - orchestrator/Argus.Orchestrator.Tests/ProtoCodegenTests.cs
    - orchestrator/Argus.Orchestrator.Tests/EntityRuntimeStateTests.cs
    - orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs

key-decisions:
  - "Field numbers final: Point.params=4, Verdict.warmed_up=9, Verdict.n_seen=10, Verdict.window=11 — all additive, git diff proto/argus.proto shows zero renumbering"
  - "HstParams threaded to ToPoint via a new read-only EntityRuntimeState.HstParams property (set once from the constructor argument), not a RunAsync signature change — preserves the ten existing RunAsync(IScoreStreamCall,...) test call sites"
  - "ApplyVerdictWarmup ignores a non-positive window argument (the (false,0,0) tuple a detector returns for an unknown entity) so WarmUpWindow keeps its constructor-seeded value instead of blanking to 0"
  - "D-11 (HysteresisGate state deliberately not persisted) documented directly in EntityRuntimeState's class doc comment, not only in 15-CONTEXT.md, so a future reader of the code finds the reasoning in place"

patterns-established:
  - "Verdict-driven per-entity state: EntityRuntimeState no longer self-counts; a single ApplyVerdictWarmup call from ProcessVerdictAsync is now the only mutation path for WarmedUp/ReadingCount/WarmUpWindow"

requirements-completed: [WARM-01, WARM-02]

coverage:
  - id: D1
    description: "Point.params map (field 4) and Verdict.warmed_up/n_seen/window (fields 9-11) added additively to proto/argus.proto; Python (grpcio-tools) and .NET (Grpc.Tools) stubs regenerated and round-trip tested on both sides"
    requirement: "WARM-01"
    verification:
      - kind: unit
        ref: "detector/tests/test_proto_codegen.py::TestProtoCodegen::test_point_params_and_verdict_warmup_fields_roundtrip"
        status: pass
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/ProtoCodegenTests.cs::ProtoCodegenTests.PointParams_AndVerdictWarmupFields_RoundtripThroughSerialization"
        status: pass
    human_judgment: false
  - id: D2
    description: "servicer.ScoreStream forwards dict(point.params) into registry.score_one (fixes D3) and populates Verdict.warmed_up/n_seen/window from registry.get_warmup_state read after scoring, including correct continuation from a checkpoint-restored n_seen"
    requirement: "WARM-02"
    verification:
      - kind: unit
        ref: "detector/tests/test_servicer.py::TestScoreStreamParams (4 tests) + TestScoreStreamCheckpointRestore::test_restored_checkpoint_n_seen_continues_from_seed + TestScoreStreamExistingBehaviorUnchanged (3 tests)"
        status: pass
    human_judgment: false
  - id: D3
    description: "EntityRuntimeState.RecordReading() deleted; WarmedUp/ReadingCount/WarmUpWindow are set exclusively by ApplyVerdictWarmup(warmedUp, nSeen, window), closing defect D2 (the second independent orchestrator-side warm-up counter)"
    requirement: "WARM-01"
    verification:
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/EntityRuntimeStateTests.cs (7 tests) + ScoreStreamPipelineTests.cs::RunAsync_FeedingReadings_DoesNotChangeReadingCount"
        status: pass
    human_judgment: false
  - id: D4
    description: "EntityStatusCache.Set relocated from the write loop into ProcessVerdictAsync (verdict read loop); a restored-warm entity (Verdict.WarmedUp=true) publishes its binary_sensor flag on the first verdict after a restart without waiting out a fresh 250 readings"
    requirement: "WARM-01"
    verification:
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs::ProcessVerdictAsync_WritesStatusCacheFromVerdictNumbers + ProcessVerdictAsync_VerdictWarmedUpTrue_PublishesFlagEvenWithZeroLocalReadingCount"
        status: pass
    human_judgment: false
  - id: D5
    description: "ScoreStreamPipeline.ToPoint(reading, hstParams) populates Point.params[window]/[n_trees] from the entity's resolved HstParams, closing the WARM-02 wire path end-to-end"
    requirement: "WARM-02"
    verification:
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs::RunAsync_WriteLoop_EmitsPointWithWindowAndNTreesParams"
        status: pass
    human_judgment: false

duration: 13min
completed: 2026-08-03
status: complete
---

# Phase 15 Plan 02: Proto + Orchestrator Warm-up-from-Verdict Summary

**Detector becomes the single source of truth for HST warm-up: `Point.params`/`Verdict.warmed_up`+`n_seen`+`window` added additively to the proto, `servicer.ScoreStream` forwards per-entity params into `score_one` and reports the detector's own warm-up numbers, and the orchestrator's `EntityRuntimeState` drops its self-incrementing counter in favor of a single `ApplyVerdictWarmup` call driven by the verdict.**

## Performance

- **Duration:** 13 min (08:22:57–08:35:58 UTC)
- **Started:** 2026-08-03T08:22:57Z
- **Completed:** 2026-08-03T08:35:58Z
- **Tasks:** 3 (Task 1 tracer; Tasks 2-3 each RED test commit + GREEN implementation commit)
- **Files modified:** 10

## Accomplishments

- `proto/argus.proto`: `Point.params` (map<string,string>, field 4, mirrors the existing `FitRequest.params`/`ScoreBatchRequest.params` convention) and `Verdict.warmed_up`/`n_seen`/`window` (bool/int32/int32, fields 9-11) — strictly additive, `git diff proto/argus.proto` shows zero renumbering
- Both stub sets regenerated and proven live with round-trip tests: Python via `python detector/scripts/gen_proto.py` (`grep -c "warmed_up|n_seen|window" argus_pb2.pyi` → 11 matches) and .NET via `dotnet build` (Grpc.Tools' `Protobuf` MSBuild item)
- `servicer.ScoreStream` now passes `dict(point.params)` into `registry.score_one` (closes defect D3 — a configured `window: 50` now actually reaches `EntityDetector.from_params` instead of silently defaulting to 250) and reads `registry.get_warmup_state` AFTER scoring so `Verdict.n_seen` reflects the point just processed, including on a checkpoint-restored entity (continues from the restored `n_seen`, never resets to 1)
- `EntityRuntimeState.RecordReading()` deleted outright (not deprecated) — any surviving call site is now a compile error, which is the enforcement mechanism proving defect D2 (the second independent warm-up counter) is closed. `WarmedUp`/`ReadingCount`/`WarmUpWindow` are now set exclusively by `ApplyVerdictWarmup(bool warmedUp, int nSeen, int window)`, called once as the first thing in `ProcessVerdictAsync`
- `ApplyVerdictWarmup` ignores a non-positive `window` argument (the `(false, 0, 0)` tuple a detector returns for an entity it has no entry for) — `WarmUpWindow` keeps its constructor-seeded value so `GET /api/sensors` never renders a zero denominator
- `EntityStatusCache.Set` relocated from the write loop (`ScoreStreamPipeline.cs`, formerly line 164) into the verdict read loop (`ProcessVerdictAsync`) — the cache now reflects the detector's own numbers, and a restored-warm entity (`Verdict.WarmedUp=true`) publishes its `binary_sensor` flag on the very first verdict after a restart instead of waiting out a fresh 250 readings
- `ScoreStreamPipeline.ToPoint` now takes `(HaReading reading, HstParams hstParams)` and populates `Point.params["window"]`/`["n_trees"]` — closes the WARM-02 wire path end-to-end (config → orchestrator → detector). `HstParams` is threaded via a new read-only property on `EntityRuntimeState` (set once from the constructor argument already computed by `BuildEntityStates`) rather than changing the `RunAsync(IScoreStreamCall,...)` signature, preserving all ten existing test call sites against that overload
- `D-11` (HysteresisGate state deliberately not persisted, since it derives from scores rather than raw readings and backfill cannot rebuild it) is now documented directly in `EntityRuntimeState`'s class doc comment, discoverable by the next code reader without needing to open `15-CONTEXT.md`

## Task Commits

Each task was committed atomically; Tasks 2-3 (both `tdd="true"`) followed RED (failing/non-compiling test) → GREEN (implementation) gates:

1. **Task 1: Proto contract, regenerated and asserted on BOTH sides** — `43eed14` (feat)
2. **Task 2: Detector side — forward Point.params, populate Verdict warm-up fields** — `888a3a0` (test, RED) → `1608bf6` (feat, GREEN)
3. **Task 3: Orchestrator side — delete the second counter, read warm-up from the verdict** — `fef4b99` (test, RED) → `3a3bd90` (feat, GREEN)

**Plan metadata:** (this commit, following SUMMARY.md + STATE.md/ROADMAP.md updates)

## Files Created/Modified

- `proto/argus.proto` — `Point.params` (field 4), `Verdict.warmed_up`/`n_seen`/`window` (fields 9-11)
- `detector/argus_detector/servicer.py` — `ScoreStream` forwards `dict(point.params)`, populates the three new `Verdict` fields, adds them to the existing structured "scored" log
- `detector/tests/test_proto_codegen.py` — round-trip test for `Point.params`/`Verdict` warm-up fields
- `detector/tests/test_servicer.py` — `TestScoreStreamParams` (4), `TestScoreStreamCheckpointRestore` (1), `TestScoreStreamExistingBehaviorUnchanged` (3) — 8 new tests, no unit-level `ScoreStream` tests existed before this plan
- `detector/tests/test_score_zero_wire.py` — wire-level round-trip assertion for `n_seen=0`/`warmed_up=False`/`window=250`
- `orchestrator/Argus.Orchestrator/Detection/EntityRuntimeState.cs` — `RecordReading()` deleted; `ApplyVerdictWarmup` + `HstParams` property added
- `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs` — write loop no longer counts/caches; `ProcessVerdictAsync` calls `ApplyVerdictWarmup` + writes the status cache; `ToPoint` takes `HstParams`
- `orchestrator/Argus.Orchestrator.Tests/ProtoCodegenTests.cs` — round-trip test for the new fields (`using Google.Protobuf;` added for `ToByteArray()`)
- `orchestrator/Argus.Orchestrator.Tests/EntityRuntimeStateTests.cs` — rewritten off `RecordReading`; 7 tests (2 pre-existing kept unchanged, 2 pre-existing rewritten, 3 new)
- `orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs` — 6 pre-existing call sites rewritten off `RecordReading` (via an extended `MakeVerdict` helper carrying `warmedUp`/`nSeen`/`window`), 4 new tests added, `OrderTrackingDuplexCall` extended to capture written `Point`s, `AsyncEnumerableHelper.FromItems` overloaded for multi-reading feeds

**Count of pre-existing tests rewritten off `RecordReading` (per plan's `<output>` requirement):** 6 — 4 in `EntityRuntimeStateTests.cs` (`ReadingCount_IncrementsByOnePerRecordReading` and `WarmedUp_FlipsToTrueExactlyWhenReadingCountReachesWindow` were removed/replaced with `ApplyVerdictWarmup`-based equivalents; the other 2 kept their assertions but dropped the `RecordReading()` setup) and 2 in `ScoreStreamPipelineTests.cs` were absorbed into the 6 call-site rewrites listed above (5 `entityState.RecordReading()` removals + 1 `verdictState.RecordReading()` removal, all replaced by `MakeVerdict(..., warmedUp: true, ...)`).

## Decisions Made

- Field numbers final and additive: `Point.params=4`, `Verdict.warmed_up=9`, `Verdict.n_seen=10`, `Verdict.window=11`
- `HstParams` threaded to `ToPoint` via a new read-only `EntityRuntimeState.HstParams` property rather than a `RunAsync` signature change (plan's explicit preference — avoids touching the primary tested surface)
- `ApplyVerdictWarmup` ignores a non-positive `window` argument so the UI denominator never blanks to 0 for an entity the detector has no entry for yet
- D-11 rationale (HysteresisGate not persisted) written directly into `EntityRuntimeState`'s class doc comment for future-reader discoverability

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Missing `using Google.Protobuf;` for `ToByteArray()` extension method**
- **Found during:** Task 1 (`.NET stub regeneration + round-trip test`)
- **Issue:** `dotnet build` failed with CS1061 — `Point`/`Verdict` don't expose `ToByteArray()` without the `Google.Protobuf` extension-method namespace in scope.
- **Fix:** Added `using Google.Protobuf;` to `ProtoCodegenTests.cs`.
- **Files modified:** `orchestrator/Argus.Orchestrator.Tests/ProtoCodegenTests.cs`
- **Verification:** `dotnet build` clean (0 errors, 0 warnings); `dotnet test --filter FullyQualifiedName~ProtoCodegen` → 5/5 passed.
- **Committed in:** `43eed14` (Task 1 commit)

**2. [Rule 1 - Bug] Checkpoint-restore test initially seeded only the registry's dirty-tracking baseline, not the detector's own counter**
- **Found during:** Task 2 (`TestScoreStreamCheckpointRestore`)
- **Issue:** First test draft called `registry.register_checkpoint(entity_id, "hst", restored, n_seen=100)` with a freshly-constructed `EntityDetector` (internal `_n_seen=0`) — the `n_seen=100` argument only seeds the registry's own dirty-tracking bookkeeping (`_last_checkpointed`), not the pickled detector's actual counter. The live point after restore produced `n_seen=1`, not the expected `101`.
- **Fix:** Fed the restored detector 100 `score_one` calls before registering, so its real `n_seen` matches the checkpoint scenario being simulated, then asserted `n_seen == 101` after one more live point.
- **Files modified:** `detector/tests/test_servicer.py`
- **Verification:** Test passes; confirms the assertion actually proves what it claims (restored `n_seen` continues, no reset).
- **Committed in:** `888a3a0` (Task 2 RED commit — caught and fixed before the RED commit was made)

---

**Total deviations:** 2 auto-fixed (1 blocking build error, 1 test-logic bug caught during authoring)
**Impact on plan:** Both fixes are test/build-infrastructure scoped; no production behavior was changed to work around either issue.

## Issues Encountered

None beyond the two auto-fixed deviations above.

## Known Stubs

None — no hardcoded empty values, placeholder text, or unwired data sources were introduced.

## Threat Flags

None beyond the plan's own `<threat_model>` table (already reviewed at plan time — T-15-02-01..04 and T-15-02-SC are addressed by the implementation as written: params degrade safely via `_cast_int`, warm-up gating trust boundary is accepted per D-01/D-04, a stuck warm-up is visible via `n_seen`/`window` in `GET /api/sensors`, and the proto diff is verified additive-only in this SUMMARY).

## User Setup Required

None — no external service configuration required.

## Next Phase Readiness

- 15-03 (InfluxDB backfill) can now build the `Warmup` RPC on top of a proto that already carries `Point.params` and `Verdict.warmed_up`/`n_seen`/`window` — the wire contract this plan added is exactly what 15-CONTEXT.md's D-12 backfill design consumes.
- `EntityRuntimeState.ApplyVerdictWarmup` is the single mutation point 15-03 should call through (via a verdict-shaped result from the new `Warmup` RPC) rather than adding a second entry point.
- Full detector suite: 244 passed, 1 skipped (pre-existing Windows SIGTERM platform skip from 15-01, unrelated to this plan).
- Full orchestrator suite: 429 passed, 0 failed, 0 skipped.
- Scope fence honored: `git status --short` after this plan touches only `proto/`, `detector/`, and `orchestrator/Argus.Orchestrator*/` — nothing under `orchestrator/ui/` or `argus/`.

---
*Phase: 15-streaming-state-persistence-warm-up-backfill*
*Completed: 2026-08-03*

## Self-Check: PASSED

All 10 created/modified files confirmed present on disk; all 5 task commit hashes
(`43eed14`, `888a3a0`, `1608bf6`, `fef4b99`, `3a3bd90`) confirmed present in git log.
