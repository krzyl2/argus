---
phase: 15-streaming-state-persistence-warm-up-backfill
plan: 01
subsystem: detector
tags: [river, pickle, threading, checkpoint, gRPC, sigterm, streaming-ml]

requires: []
provides:
  - "EntityDetector.n_seen / EntityDetector.window public accessors"
  - "ModelStore.save_checkpoint / load_checkpoint (outside versioned path, D-02)"
  - "load_all_into extended with a checkpoint.pkl glob pass that runs after and wins over the latest-version pass"
  - "DetectorRegistry.get_warmup_state / register_checkpoint / checkpoint_dirty / _hst_keys"
  - "CheckpointWriter (new module): interval thread + synchronous flush"
  - "DetectorConfig.checkpoint_interval_sec / checkpoint_enabled"
  - "server.py SIGTERM handler wired to CheckpointWriter.flush()"
affects: [15-02-proto-orchestrator-warmup, 15-03-influxdb-backfill, 15-04-restart-tests-uat-ship]

tech-stack:
  added: []
  patterns:
    - "checkpoint sweep as a DetectorRegistry method (not an external collaborator) reusing _entity_lock/_hst_keys, deepcopy under lock + pickle/rename outside it (MDL-04/D-06)"
    - "threading.Event.wait(interval) interruptible daemon-thread loop (no in-repo Python precedent existed before this plan)"

key-files:
  created:
    - detector/argus_detector/checkpoint_writer.py
    - detector/tests/test_checkpoint.py
  modified:
    - detector/argus_detector/hst_detector.py
    - detector/argus_detector/model_store.py
    - detector/argus_detector/registry.py
    - detector/argus_detector/config.py
    - detector/argus_detector/server.py
    - detector/tests/test_restart_resilience.py

key-decisions:
  - "Checkpoint dirty-tracking bookkeeping (_last_checkpointed) lives on DetectorRegistry, never on the pickled EntityDetector — storing it on the model would restore a stale baseline on every restart (RESEARCH.md anti-pattern note)"
  - "load_all_into's checkpoint glob pass reuses load_checkpoint() internally rather than duplicating pickle/sidecar logic inline"
  - "SIGTERM grace=5s / wait=5s chosen because the flush path is fast-and-bounded (a handful of entities, sub-second once the per-entity yield bounds deepcopy cost) — no verified s6 kill-grace budget exists to size against"
  - "SIGTERM subprocess integration test skipped on win32 (Windows has no catchable SIGTERM delivery from another process); recorded in .planning/WINDOWS.md as an open skipped-test entry"

patterns-established:
  - "Pattern: dirty-tracked interval sweep — DetectorRegistry.checkpoint_dirty snapshots under _entity_lock, writes outside it, yields time.sleep(0) between entities"

requirements-completed: [PERSIST-01, PERSIST-02, PERSIST-03, PERSIST-04]

coverage:
  - id: D1
    description: "EntityDetector.n_seen/.window accessors + in-situ pickle-size/deepcopy-latency measurement confirming RESEARCH.md's numbers"
    requirement: "PERSIST-01"
    verification:
      - kind: unit
        ref: "detector/tests/test_checkpoint.py::TestEntityDetectorAccessors, TestPickleSizeAndDeepcopyLatency"
        status: pass
    human_judgment: false
  - id: D2
    description: "ModelStore.save_checkpoint/load_checkpoint atomic round-trip with river_version sidecar validation, outside the versioned v{N} layout"
    requirement: "PERSIST-02"
    verification:
      - kind: unit
        ref: "detector/tests/test_checkpoint.py::TestSaveLoadCheckpointRoundTrip"
        status: pass
    human_judgment: false
  - id: D3
    description: "load_all_into checkpoint glob pass: per-entity fault isolation on truncated files, and checkpoint wins over a stale versioned model for the same key"
    requirement: "PERSIST-04"
    verification:
      - kind: unit
        ref: "detector/tests/test_checkpoint.py::TestLoadAllIntoCheckpointPass"
        status: pass
    human_judgment: false
  - id: D4
    description: "DetectorRegistry.get_warmup_state and checkpoint_dirty: dirty-only writes, hst-only filter, per-entity fault isolation, no lock held across file I/O"
    requirement: "PERSIST-02"
    verification:
      - kind: unit
        ref: "detector/tests/test_checkpoint.py::TestGetWarmupState, TestCheckpointDirty"
        status: pass
    human_judgment: false
  - id: D5
    description: "CheckpointWriter interval thread (interruptible, disables at interval=0) + synchronous flush() + config knobs"
    requirement: "PERSIST-01"
    verification:
      - kind: unit
        ref: "detector/tests/test_checkpoint.py::TestDetectorConfigCheckpointKnobs, TestCheckpointWriter, TestCreateServerAttachesWriter"
        status: pass
    human_judgment: false
  - id: D6
    description: "SIGTERM flushes dirty checkpoints before server.stop(grace).wait() — proven end-to-end via a real subprocess"
    requirement: "PERSIST-03"
    verification:
      - kind: integration
        ref: "detector/tests/test_restart_resilience.py::TestSigtermFlush::test_sigterm_flushes_dirty_checkpoint_before_exit"
        status: unknown
    human_judgment: true
    rationale: "Test is skipped on win32 (this dev machine) — Windows has no catchable SIGTERM delivery from another process (TerminateProcess bypasses Python signal handlers). The test exists and is correct for the Linux s6-overlay production target but has not been observed passing in this session; a human/CI run on Linux should confirm before treating PERSIST-03 as fully proven."

duration: 9min
completed: 2026-08-03
status: complete
---

# Phase 15 Plan 01: Detector Streaming State Checkpoints Summary

**Streaming HST detector state now survives process restarts via a dirty-tracked, atomically-written `checkpoint.pkl`/`checkpoint.json` pair per entity, a 300s interval writer thread reusing the existing MDL-04 lock discipline, and a SIGTERM flush before shutdown.**

## Performance

- **Duration:** 9 min (commit span 10:09:52–10:19:13 UTC+2)
- **Tasks:** 3 (each with RED test commit + GREEN implementation commit)
- **Files modified:** 7 (2 new: `checkpoint_writer.py`, `test_checkpoint.py`; 5 modified)

## Accomplishments
- `EntityDetector.n_seen`/`.window` accessors, closing the "no public accessor" gap RESEARCH.md flagged (Pitfall 4)
- `ModelStore.save_checkpoint`/`load_checkpoint`: atomic `.tmp`+`Path.replace()` writes for both the pickle and its JSON sidecar, living outside the versioned `v{N}` layout (D-02) so no `next_version`/`version.json`/`_prune` ever runs on the checkpoint path
- `load_all_into` extended with a second glob pass over `*/*/checkpoint.pkl`, run strictly after the existing `latest`-pointer pass — proven by an executable test that a checkpoint wins over a stale versioned model for the same `(slug, detector)` key (D-01/D-09, RESEARCH.md Pitfall 2), not just asserted in a comment
- `DetectorRegistry.checkpoint_dirty`: dirty-only writes (idle entities produce zero disk I/O), `hst`-only filter, per-entity fault isolation (one entity's disk error never blocks the others or advances that entity's baseline), and a proven no-lock-across-file-I/O guarantee via a blocking-store concurrency test
- `CheckpointWriter`: new module built on `threading.Event.wait(interval)` (interruptible — no in-repo Python daemon-thread precedent existed, per 15-PATTERNS.md's explicit "No Analog Found"), with `start()`/`stop()`/`flush()`/`is_running`
- `server.py` wires a SIGTERM handler that calls `writer.flush()` then `server.stop(grace=5).wait(5)` before exit — verified end-to-end via a real subprocess test (skipped on this Windows dev machine; see Known Stubs/Deviations below)

## Measured Values (recorded per plan's `<output>` requirement)

- **Pickle size** (500 `score_one` calls at defaults, window=250, n_trees=25): **419291 bytes** — exactly matches RESEARCH.md's measured figure, inside the 200000–1200000 byte assertion band.
- **`copy.deepcopy` latency** (same warmed detector): **90.3 ms** — within RESEARCH.md's measured 56–96 ms range, confirming CONTEXT.md's Risk table ">50ms" trigger fires at defaults with zero scale. This is why Task 2's per-entity `time.sleep(0)` yield in `checkpoint_dirty` was implemented as baseline design, not a conditional fallback.
- **s6 kill-grace grep findings:** `grep -rn "S6_KILL_GRACETIME|timeout-kill|timeout-finish"` across the repo found **zero matches** under `argus/rootfs/etc/services.d/detector/` or `argus/Dockerfile` (the only hits are in this phase's own planning docs and a cached web-search note). Confirmed: no `timeout-kill`/`timeout-finish` file exists in the detector's s6 service directory; no `S6_KILL_GRACETIME` env line in the Dockerfile.
- **Chosen values:** `server.stop(grace=5.0)` and `stop_event.wait(5.0)`. Rationale: since the real s6 kill-grace budget is unverified (RESEARCH.md Assumption A1, confirmed still unverified by the grep above), the flush path was designed to be fast and bounded regardless of the actual budget — a handful of entities' pickles complete in well under a second once the Task 2 yield bounds cumulative deepcopy cost. 5s is comfortably inside even the LOW-confidence "5–10s" web-search figure cited in RESEARCH.md, without depending on that figure being accurate.

## Task Commits

Each task followed RED (failing test) → GREEN (implementation) TDD gates:

1. **Task 1: End-to-end checkpoint round-trip** — `f956a7c` (test, RED) → `9dc7793` (feat, GREEN)
2. **Task 2: Dirty-tracked checkpoint sweep** — `672ae51` (test, RED) → `8e91493` (feat, GREEN)
3. **Task 3: Interval writer, config knobs, SIGTERM flush** — `6556cee` (test, RED) → `a5fac6c` (feat, GREEN)

## Files Created/Modified
- `detector/argus_detector/hst_detector.py` — added `n_seen`/`window` `@property` accessors
- `detector/argus_detector/model_store.py` — added `_checkpoint_dir`, `save_checkpoint`, `load_checkpoint`; extended `load_all_into` with the checkpoint glob pass
- `detector/argus_detector/registry.py` — added `_last_checkpointed` dict, `get_warmup_state`, `_hst_keys`, `checkpoint_dirty`, `register_checkpoint`
- `detector/argus_detector/checkpoint_writer.py` (new) — `CheckpointWriter` class
- `detector/argus_detector/config.py` — added `checkpoint_interval_sec`/`checkpoint_enabled` env knobs
- `detector/argus_detector/server.py` — wires `CheckpointWriter` into `create_server`/`serve()`, installs SIGTERM handler
- `detector/tests/test_checkpoint.py` (new) — all Task 1-3 unit tests
- `detector/tests/test_restart_resilience.py` — added the SIGTERM subprocess integration test

## Decisions Made
- Dirty-tracking baseline (`_last_checkpointed`) lives on the registry, never on the pickled `EntityDetector` — matches RESEARCH.md's explicit anti-pattern warning.
- `load_all_into`'s checkpoint pass calls `load_checkpoint()` internally (rather than re-implementing pickle/sidecar logic inline) to avoid a second, parallel version of the same load logic.
- SIGTERM grace/wait values (5s/5s) chosen from the "keep the flush fast, don't depend on the grace budget" principle, since the actual s6 kill-grace value could not be confirmed in this repo.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] SIGTERM subprocess test cannot exercise real signal delivery on Windows**
- **Found during:** Task 3 (SIGTERM flush verification)
- **Issue:** `Popen.send_signal(signal.SIGTERM)` and `os.kill(pid, signal.SIGTERM)` on Windows both call `TerminateProcess()` directly (CPython's own implementation), bypassing Python signal handlers entirely. The test as written could never observe the SIGTERM handler firing on this Windows dev machine, regardless of correctness.
- **Fix:** Added `@pytest.mark.skipif(sys.platform == "win32", ...)` with a documented rationale on `TestSigtermFlush`. The production target is the Linux s6-overlay add-on container (`argus/rootfs/etc/services.d/detector/run` uses `exec`), where a real SIGTERM is delivered and the test's assumption holds. The underlying `signal.signal(SIGTERM, ...)` registration and the writer's `flush()`/tick logic are independently exercised by `TestCreateServerAttachesWriter` and `TestCheckpointWriter` on every platform.
- **Files modified:** `detector/tests/test_restart_resilience.py`
- **Verification:** Test collected and correctly skipped (not silently omitted) — `1 skipped` visible in pytest output with the reason printed.
- **Recorded in ledger:** `.planning/WINDOWS.md` entry #1 (kind: `skipped-test`, status: `open`) — flags this for a Linux/CI run before PERSIST-03 is treated as fully proven end-to-end.
- **Committed in:** `a5fac6c` (Task 3 GREEN commit)

---

**Total deviations:** 1 auto-fixed (1 blocking — platform limitation, not a code defect)
**Impact on plan:** No production code was changed to work around this; the deviation is entirely test-infrastructure scoped. The SIGTERM handler code itself is unchanged and correct for the Linux target.

## Issues Encountered
None beyond the Windows SIGTERM test-platform issue documented above.

## Known Stubs
None — no hardcoded empty values, placeholder text, or unwired data sources were introduced. The one gap is the skipped test (documented above and in `.planning/WINDOWS.md`), not a stub in shipped functionality.

## User Setup Required
None — no external service configuration required. The two new env vars (`ARGUS_CHECKPOINT_INTERVAL_SEC`, `ARGUS_CHECKPOINT_ENABLED`) have correct defaults (300/true) and are not surfaced in the add-on options UI per D-16.

## Next Phase Readiness
- 15-02 (proto + orchestrator warm-up-from-verdict) can now consume `EntityDetector.n_seen`/`.window` and `DetectorRegistry.get_warmup_state` to populate the new `Verdict.warmed_up`/`n_seen`/`window` fields — both exist and are tested.
- Full detector suite: 234 passed, 1 skipped (documented), 0 failed.
- Scope fence honored: `git status --short` shows changes only under `detector/`; no file under `orchestrator/`, `proto/`, or `argus/` was modified.
- Open item for a human/CI pass on Linux: confirm `TestSigtermFlush::test_sigterm_flushes_dirty_checkpoint_before_exit` passes in the actual Linux container (or during 15-04's restart-test/UAT pass) — see `.planning/WINDOWS.md` entry #1.

---
*Phase: 15-streaming-state-persistence-warm-up-backfill*
*Completed: 2026-08-03*

## Self-Check: PASSED

All 8 created/modified source and test files confirmed present on disk; all 6 task commit hashes
(`f956a7c`, `9dc7793`, `672ae51`, `8e91493`, `6556cee`, `a5fac6c`) confirmed present in git log.
