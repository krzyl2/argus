---
phase: 15-streaming-state-persistence-warm-up-backfill
plan: 04
subsystem: testing
tags: [pytest, xunit, grpc-health, river, checkpoint, warm-up, add-on-release]

requires:
  - phase: 15-streaming-state-persistence-warm-up-backfill (plan 01)
    provides: "ModelStore.save_checkpoint/load_checkpoint, DetectorRegistry.checkpoint_dirty, CheckpointWriter, SIGTERM flush"
  - phase: 15-streaming-state-persistence-warm-up-backfill (plan 02)
    provides: "Verdict.warmed_up/n_seen/window, Point.params, EntityRuntimeState.ApplyVerdictWarmup"
  - phase: 15-streaming-state-persistence-warm-up-backfill (plan 03)
    provides: "Warmup RPC + DetectorRegistry.warmup_one, InfluxDbReader.QueryHistoryAsync, ScoreStreamPipeline.PrimeFromHistoryAsync"
provides:
  - "detector/tests/test_restart_resilience.py: TestHardKillRestoresCheckpoint (SC-1), TestCorruptCheckpointDoesNotBlockStartup + TestBogusRiverVersionSidecarSkipped (SC-6), TestIdleEntityNoCheckpointWrites (SC-4)"
  - "orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs: PrimeFromHistoryAsync_CalledTwiceWithSkippedResponse_NoAdditionalPrimingAttempts + PrimeFromHistoryAsync_SkippedResponse_FrozenDetectorStillPrimed (SC-8)"
  - "argus/config.yaml version 2.1.9 (not yet pushed to GHCR — Task 3 pending)"
  - ".planning/phases/15-streaming-state-persistence-warm-up-backfill/15-UAT.md — nine-row ROADMAP-sourced UAT checklist, outcome column empty pending Task 3"
affects: []

tech-stack:
  added: []
  patterns:
    - "Non-catchable kill (Popen.kill()) as the cross-platform stand-in for SC-1's SIGKILL — TerminateProcess on Windows and SIGKILL on POSIX are both signal-handler-bypassing, so no platform skip was needed for this test (unlike the pre-existing SIGTERM test)"
    - "Deterministic checkpoint-interval timing test: chosen interval (2s) sized so the burst-and-kill sequence provably completes before a second tick could land, rather than tolerating a timing race"

key-files:
  created:
    - .planning/phases/15-streaming-state-persistence-warm-up-backfill/15-UAT.md
  modified:
    - detector/tests/test_restart_resilience.py
    - orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs
    - argus/config.yaml

key-decisions:
  - "SC-1's hard-kill test uses Popen.kill() (not a signal number) precisely because it is non-catchable on every platform — SIGKILL on POSIX, TerminateProcess on Windows — removing the need for the win32 skip that the SIGTERM test (15-01) required"
  - "Checkpoint interval fixed at 2s for the hard-kill test: long enough that the burst-of-5-then-kill sequence (sub-second) cannot span into the second tick (due at ~4s), making the loss bound deterministic rather than a tolerated race"
  - "SC-6's two corrupt-checkpoint variants split into two tests: one exercises create_server + a live health-check RPC (garbage pkl bytes, proves SERVING is still reached), the other exercises ModelStore/DetectorRegistry directly (bogus river_version sidecar, proves the entity is absent while others load) — matching the acceptance criteria's distinct assertions rather than forcing both into one shape"
  - "SC-8's no-re-backfill test calls the SAME PrimeFromHistoryAsync twice against the same fakes rather than constructing two pipelines — this is what actually simulates 'a second stream-open attempt against an unchanged, already-primed detector', the real SC-8 scenario"
  - "Version bumped to 2.1.9 in argus/config.yaml now, ahead of the actual GHCR push — deploy/build-push.ps1 also writes this value from -Version, but the plan requires the committed repository state to be correct independent of when the script runs (HA reads master for update detection)"

patterns-established: []

requirements-completed: []  # Tasks 1-2 add test coverage only; PERSIST-01..04/WARM-01..02/BACKFILL-01..04 were already completed by 15-01..15-03. This SUMMARY does not claim new requirement completion — see "Status" below.

coverage: []  # Task 3 (live UAT + deploy) has not run; deliverable-level coverage will be finalized in a follow-up SUMMARY update once Task 3 completes.

duration: 11min (Tasks 1-2 only; Task 3 pending)
completed: 2026-08-03
status: blocked
---

# Phase 15 Plan 04: Restart Tests, Version Bump — Summary (Tasks 1-2 complete, Task 3 blocked on live UAT + deploy)

**Cross-plan restart/crash test coverage for SC-1/SC-4/SC-6/SC-8 added across both suites (10 new tests, zero production files touched), add-on version bumped to 2.1.9, and a nine-row UAT checklist written — the live-HA deploy and UAT (Task 3) is a blocking human checkpoint that has NOT been run.**

**Honest status: this plan is 2/3 tasks complete.** Task 3 requires the operator to build+push the
2.1.9 image to GHCR, update their live Home Assistant add-on, and walk the nine UAT rows against
real sensors — none of which this executor performed, per the plan's own blocking-checkpoint
design and this session's explicit instruction not to run any deploy. Do not treat Phase 15 as
shipped until Task 3's checkpoint clears and a follow-up SUMMARY update (or the continuation
agent's own summary) records the nine UAT outcomes and the deploy commit.

## Performance

- **Duration:** 11 min (10:59:45–11:10:54 UTC+2, Tasks 1-2 only)
- **Tasks:** 2 of 3 complete (Task 3 is the blocking checkpoint, not executed)
- **Files modified:** 3 (2 test files, 1 config file) + 1 new file (15-UAT.md)

## Accomplishments (Tasks 1-2 only)

- **SC-1 (hard kill):** `TestHardKillRestoresCheckpoint` in `test_restart_resilience.py` — a detector
  subprocess is scored, a checkpoint tick is confirmed on disk, 5 more readings are sent, then the
  process is hard-killed (`Popen.kill()` — SIGKILL on POSIX, `TerminateProcess` on Windows, both
  non-catchable). A second `create_server` on the same root restores `n_seen > 0`, bounded to at
  most the 5 readings sent after the last confirmed tick. Runs on every platform — no skip needed,
  unlike the pre-existing SIGTERM test.
- **SC-6 (corrupt checkpoint):** two tests. `TestCorruptCheckpointDoesNotBlockStartup` builds a
  three-entity root (one garbage-bytes `checkpoint.pkl`, one valid checkpoint, one valid versioned
  batch model), starts a real server, and asserts the gRPC Health `Check` RPC returns `SERVING` for
  `argus.v1.DetectorService` with both healthy entities registered. `TestBogusRiverVersionSidecarSkipped`
  proves a valid pickle with a mismatched `river_version` sidecar is silently discarded while a sibling
  entity on the same root still loads.
- **SC-4 (idle):** `TestIdleEntityNoCheckpointWrites` proves two consecutive `checkpoint_dirty` ticks
  with no intervening `score_one` leave the checkpoint file's mtime AND byte size unchanged (extending
  15-01's existing mtime-only check).
- **SC-8 (no re-backfill) + variant:** two new tests in `ScoreStreamPipelineTests.cs`.
  `PrimeFromHistoryAsync_CalledTwiceWithSkippedResponse_NoAdditionalPrimingAttempts` runs the same
  pipeline's `PrimeFromHistoryAsync` twice against fakes that always report `Skipped=true`, and
  asserts the call count grows by exactly 1 per run (no retry/compensation loop) with the response's
  own `NSeen` never changing between runs.
  `PrimeFromHistoryAsync_SkippedResponse_FrozenDetectorStillPrimed` proves the `FrozenDetector` is
  still primed from history even when the detector-side Warmup RPC reports skipped — confirmed
  against production code: `FrozenDetector.AddReading` happens in the same loop that builds the
  `WarmupRequest`, strictly before the (possibly skipped) response is even known.
- **Version bump:** `argus/config.yaml` `version: "2.1.8"` → `"2.1.9"`.
- **15-UAT.md:** nine rows, verbatim ROADMAP Success Criteria text, six naming a covering automated
  test (from 15-01/15-03/this plan's Task 1), three (SC-2, SC-5, SC-7) marked live-observation-required
  with concrete steps (screen, restart scope, expected figure), SC-3/SC-9 additionally note a cheap
  live spot-check. Outcome column is intentionally empty — Task 3 fills it in.

## Task Commits

1. **Task 1: Cross-plan restart and crash cases no single plan owns** — `8ec14f1` (test)
2. **Task 2: Version bump and full-suite release gate** — `a70eacf` (feat)
3. **Task 3: Deploy 2.1.9 and run the nine-criterion live UAT** — NOT STARTED (blocking human checkpoint)

## Files Created/Modified

- `detector/tests/test_restart_resilience.py` — added imports (`time`, `health_pb2`/`health_pb2_grpc`,
  `EntityDetector`, `DetectorRegistry`) + 4 new test classes (`TestHardKillRestoresCheckpoint`,
  `TestCorruptCheckpointDoesNotBlockStartup`, `TestBogusRiverVersionSidecarSkipped`,
  `TestIdleEntityNoCheckpointWrites`)
- `orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs` — 2 new tests using the
  existing `FakeInfluxHistorySource`/`FakeWarmupDetectorClient` fakes from 15-03
- `argus/config.yaml` — `version: "2.1.8"` → `"2.1.9"`
- `.planning/phases/15-streaming-state-persistence-warm-up-backfill/15-UAT.md` — new, nine-row checklist

## Decisions Made

See `key-decisions` in frontmatter — summarized: `Popen.kill()` chosen for SC-1 specifically because
it is non-catchable on every platform (no win32 skip needed, unlike the pre-existing SIGTERM test);
2s checkpoint interval chosen to make the SC-1 loss bound deterministic rather than a tolerated race;
SC-6's two variants kept as separate tests matching the plan's two distinct acceptance-criteria
assertions; SC-8's no-re-backfill test calls the same pipeline instance's `PrimeFromHistoryAsync`
twice (not two separate pipelines) to actually simulate the "second stream-open attempt" scenario;
version bumped in the repo now, ahead of the actual GHCR push, per the plan's "commit the file
independent of when the script runs" instruction.

## Deviations from Plan

### Auto-fixed Issues

None — Task 1 explicitly required "Add nothing to production code in this task," and both new test
suites pass against the existing 15-01/15-02/15-03 production code with zero production-file changes.
`git diff --stat` for Task 1's commit shows changes only under `detector/tests/` and
`orchestrator/Argus.Orchestrator.Tests/`.

### Noted (not a deviation — inherited from a prior plan)

**Detector suite reports 1 skipped, not 0, contrary to Task 2's literal acceptance criterion.**
`.planning/WINDOWS.md` entry #1 (recorded in 15-01, still open) documents that
`TestSigtermFlush::test_sigterm_flushes_dirty_checkpoint_before_exit` is `@pytest.mark.skipif`'d on
win32 because Windows has no catchable SIGTERM delivery from another process — this is a platform
limitation of the Windows dev machine this plan is executing on, not something introduced by this
plan, and not something this plan can fix without a Linux/CI run. Per this plan's own
`phase_specific_notes` ("report honestly what actually ran versus skipped on this machine"), this is
reported as-is rather than silently claimed as 0-skipped. The production target (Linux s6-overlay
add-on container) is unaffected — the SIGTERM handler registration itself is exercised on every
platform by `TestCreateServerAttachesWriter`/`TestCheckpointWriter`.

---

**Total deviations:** 0 auto-fixed. 1 pre-existing, already-ledgered platform limitation surfaced
honestly rather than hidden.
**Impact on plan:** None on correctness — this plan's own new tests (including the SIGKILL-based
SC-1 test, which is NOT platform-skipped) all passed on this machine.

## Issues Encountered

None during Tasks 1-2. The orchestrator's `RunAsync_CompleteAsyncCalledBeforeReadTaskAwaited` test
(flagged pre-existing-flaky in `.planning/WINDOWS.md` entry #2, from 15-03) passed cleanly in this
session's single full-suite run — not re-run to chase a second confirmation, per the instruction not
to loop hunting for green; if it flakes in a future run, that is the same already-ledgered
pre-existing issue, not a regression from this plan.

## User Setup Required

**Task 3 is a blocking human checkpoint that has NOT been executed in this session.** The operator
must, on their own workstation:
1. `docker login ghcr.io` with a `write:packages` PAT (one-time prerequisite, if not already done).
2. Run `deploy/build-push.ps1 -Version 2.1.9` to build and push the multi-arch image.
3. Commit/push `argus/config.yaml` (already bumped to 2.1.9 in this session's Task 2 commit — no
   further edit needed) and update the add-on in Home Assistant (Settings → Add-ons → Argus → Check
   for updates → Update).
4. Walk the nine rows of `15-UAT.md` and record outcomes, paying special attention to the three
   live-only rows (SC-2, SC-5, SC-7).

No environment variables or dashboard config beyond the above — the four Phase 15 env knobs
(`ARGUS_CHECKPOINT_INTERVAL_SEC`/`ARGUS_CHECKPOINT_ENABLED`/`ARGUS_BACKFILL_ENABLED`/
`ARGUS_BACKFILL_LOOKBACK`) all have correct defaults per D-16 and need no operator action.

## Next Phase Readiness

- **Phase 15 is NOT ready to be marked shipped.** Tasks 1-2 (test coverage + version bump) are done
  and committed; Task 3 (live deploy + UAT) is the blocking gate and must be completed by the operator
  before this phase can close.
- Full test suites as of this commit: detector 261 passed/1 skipped (pre-existing, documented above);
  orchestrator 457 passed/0 failed/0 skipped.
- Scope fence honored: `git status --short` shows changes only under `detector/tests/`,
  `orchestrator/Argus.Orchestrator.Tests/`, `argus/config.yaml`, and
  `.planning/phases/15-streaming-state-persistence-warm-up-backfill/15-UAT.md` — no MQTT contract,
  `ModelStore` versioned path, `InfluxDbReader.QueryAsync`, `HysteresisGate`, or `orchestrator/ui/`
  file was touched.
- To resume: run `/gsd-execute-phase 15` again (or address Task 3 directly) once the operator has
  built, pushed, and deployed 2.1.9 and is ready to walk the live UAT.

---
*Phase: 15-streaming-state-persistence-warm-up-backfill*
*Completed: 2026-08-03 (Tasks 1-2 only — Task 3 pending)*

## Self-Check: PASSED

Both modified test files and `argus/config.yaml` confirmed present on disk with the expected changes;
`15-UAT.md` confirmed present; both task commit hashes (`8ec14f1`, `a70eacf`) confirmed present in
`git log`.
