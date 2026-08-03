# Phase 15 UAT: Streaming State Persistence + Warm-up Backfill

**Source of truth:** `.planning/ROADMAP.md`, Phase 15 "Success Criteria" (verbatim, not reworded).
**Add-on version under test:** 2.1.9 (`ghcr.io/krzyl2/argus:2.1.9`)

Nine rows, one per ROADMAP success criterion. Six are covered by an automated test named below;
three (SC-2, SC-5, SC-7) require live observation against the operator's real Home Assistant
instance and real slow-reporting sensors — no unit test can stand in for them. The Outcome column
is filled in by the operator during Task 3 of `15-04-PLAN.md`.

| # | Criterion (verbatim from ROADMAP) | Coverage | Outcome |
|---|---|---|---|
| SC-1 | Detector killed with `SIGKILL` mid-warm-up → after restart `n_seen`/`warmed_up` are restored from the checkpoint; at most one checkpoint interval of readings is lost | **Automated** — `detector/tests/test_restart_resilience.py::TestHardKillRestoresCheckpoint::test_hard_kill_restores_n_seen_within_one_interval` | |
| SC-2 | Orchestrator restarted alone → warm-up progress on the Detectors screen is unchanged (value comes from the verdict, not a local counter) | **Live observation required** — see Step 3 procedure below | |
| SC-3 | Whole add-on restarted (SIGTERM) → **zero** readings lost | **Automated** — `detector/tests/test_restart_resilience.py::TestSigtermFlush::test_sigterm_flushes_dirty_checkpoint_before_exit` (skipped on the Windows dev machine per `.planning/WINDOWS.md` #1 — production target is the Linux s6-overlay container). A live spot-check is cheap and worth doing anyway: see Step 3. | |
| SC-4 | An entity with no new readings for an hour produces **zero** disk writes | **Automated** — `detector/tests/test_restart_resilience.py::TestIdleEntityNoCheckpointWrites::test_two_idle_ticks_leave_checkpoint_file_unchanged` (also `detector/tests/test_checkpoint.py::TestCheckpointDirty::test_dirty_entity_writes_once_then_idle_writes_nothing`) | |
| SC-5 | `window: 50` configured on an entity → the detector actually uses 50 and the UI shows `x/50` | **Live observation required** — see Step 3 procedure below | |
| SC-6 | A corrupted `checkpoint.pkl` for one entity → startup succeeds, all other entities load normally | **Automated** — `detector/tests/test_restart_resilience.py::TestCorruptCheckpointDoesNotBlockStartup::test_one_garbage_checkpoint_others_still_load_and_serving` and `::TestBogusRiverVersionSidecarSkipped::test_bogus_river_version_entity_absent_others_present` | |
| SC-7 | A new entity with ≥250 points of InfluxDB history → `warmed_up = true` on its first live reading | **Live observation required** — see Step 3 procedure below | |
| SC-8 | Orchestrator restart with an existing checkpoint → **no** re-backfill (`n_seen` does not jump) | **Automated** — `orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs::PrimeFromHistoryAsync_CalledTwiceWithSkippedResponse_NoAdditionalPrimingAttempts` (also `::PrimeFromHistoryAsync_SkippedResponse_ResultsInNoFurtherWarmupCalls`) | |
| SC-9 | InfluxDB unavailable or unconfigured → startup succeeds, normal warm-up, WARN log only | **Automated** — `orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs::PrimeFromHistoryAsync_QueryThrows_NoExceptionEscapes`, `::PrimeFromHistoryAsync_NullHistorySource_NoExceptionAndNoWarmupCall`. A live spot-check is cheap and worth doing anyway: see Step 3. | |

## Step 3 live observation procedure (from `15-04-PLAN.md` Task 3)

**SC-2** — Open the Detectors screen and note the `x/N` warm-up figure for a mid-warm-up entity.
Restart only the orchestrator (not the whole add-on) and reload the screen. The figure must be
unchanged or higher, never reset to 0.

**SC-5** — Configure a tracked entity with `window: 50` on its `hst` detector, save, and let it
take at least one reading. The Detectors screen must show a denominator of 50, and the detector
log line for that entity must report the same window.

**SC-7** — Add a tracked entity that has at least 250 points of InfluxDB history and is not
already checkpointed. On its first live reading the Detectors screen must show it as warmed up,
and the orchestrator log must carry one primed line naming the point count.

**Cheap spot-checks worth doing while live (SC-3, SC-9):** restart the whole add-on and confirm
the warm-up figure did not move backwards at all (SC-3, zero readings lost); confirm the log
carries no backfill failure line unless InfluxDB is genuinely unconfigured (SC-9).

## Recording outcomes

Fill in the Outcome column above for each of the nine rows: `PASS`, or `FAIL: <observed
behavior>`. A failed criterion is gap-closure input, not a documentation problem — record what was
actually observed rather than adjusting the criterion text.
