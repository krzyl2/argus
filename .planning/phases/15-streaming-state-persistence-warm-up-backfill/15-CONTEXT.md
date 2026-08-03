# Phase 15: Streaming State Persistence + Warm-up Backfill - Context

**Gathered:** 2026-08-03
**Status:** Ready for planning
**Source:** Operator-reported defect + source investigation during the same session, design proposed
and **explicitly approved by the operator** (no separate discuss-phase run). Every code reference
below was read and verified at diagnosis time — treat them as source-grounded, but re-read before
editing.

<domain>
## Phase Boundary

Make HST warm-up survive restarts. This is a **backend-only** phase — .NET orchestrator + Python
detector + proto. **No UI work**: the Detectors screen already renders warm-up progress from
`GET /api/sensors` (quick task 260722-ltt); this phase only makes the numbers it shows correct and
durable.

**In scope:**
- Detector-side periodic checkpointing of streaming (`hst`) state to disk + SIGTERM flush + restore
  on startup.
- Proto + orchestrator change making the detector the single source of truth for warm-up.
- Per-entity `hst` params actually reaching the detector.
- InfluxDB history backfill priming cold entities (and the orchestrator's `FrozenSensorDetector`).
- Restart/crash test coverage, UAT on live HA, add-on version bump + GHCR deploy.

**Out of scope (decided, not deferred-by-omission):**
- `HysteresisGate` state persistence — see D-11.
- Persisting batch/group models differently — the versioned `ModelStore` path is untouched.
- Any change to MQTT publication semantics, discovery, or the score/flag contract.
- UI changes.

## The defect

Operator observed warm-up appearing to restart after service/machine restarts. Confirmed — three
linked defects:

| # | Location | Defect |
|---|----------|--------|
| D1 | `detector/argus_detector/hst_detector.py:57`, `registry.py:71` | `EntityDetector` (River `HalfSpaceTrees` + `MinMaxScaler` + `_n_seen`) is created lazily in RAM and never persisted. `ModelStore.save_river()` exists but is only reachable from the `SaveModel` RPC (`servicer.py:488`), and the orchestrator has **zero** `SaveModel`/`LoadModel` call sites. `load_all_into()` (`server.py:79`) therefore only ever restores batch models. |
| D2 | `orchestrator/.../Detection/EntityRuntimeState.cs:40` | `_readingCount` is a second, independent warm-up counter, incremented in `ScoreStreamPipeline.RunAsync`'s write loop (`ScoreStreamPipeline.cs:158`) and reset every time `BuildEntityStates()` runs. |
| D3 | `detector/argus_detector/servicer.py:63` | `ScoreStream` calls `self._registry.score_one(entity_id, value)` with no `params`, so `EntityDetector.from_params` never sees the entity's configured `window`/`n_trees`. A configured `window: 50` warms the orchestrator at 50 (`HstParams.From`, `EntitiesConfig.cs:69`) while HST still calibrates on the 250 default. |

Impact: 250 readings of warm-up restart from zero on every restart. The operator's sensors report
infrequently (~30 min for several), so that is ~5 days with `binary_sensor` flags suppressed
(`ScoreStreamPipeline.cs:206`). Score keeps publishing but is not meaningful.

## What already works (do not rebuild)

- `/data/models` is a persistent HA add-on volume, created and exported as `ARGUS_MODEL_ROOT` by
  `argus/rootfs/etc/cont-init.d/10-config-gen.sh:80-81`. Persistence storage exists.
- `ModelStore` already has atomic-write precedent: `_update_latest()` writes `.tmp` then
  `Path.replace()` (`model_store.py:326-335`).
- `server.py:71-87` already gates health `NOT_SERVING` → load models → `SERVING` (MDL-03). Checkpoint
  restore belongs inside that existing gate.
- `load_all_into()` already wraps each entity's load in `try/except` and logs-and-continues
  (`model_store.py:290-295`).
- `DetectorRegistry` already has the per-entity lock discipline (MDL-04) that the checkpoint writer
  must reuse: snapshot under `_entity_lock`, expensive work outside it (`registry.py:114-183`).
- Orchestrator already has an Influx seam: `IInfluxDataSource` / `InfluxDbReader` /
  `IInfluxQueryApi` / `InfluxQueryApiAdapter` under `orchestrator/Argus.Orchestrator/Batch/`.
- `EntityStatusCache` + `GET /api/sensors` warm-up projection (`Program.cs:282-298`) already exist
  and already feed the UI.

</domain>

<decisions>
## Implementation Decisions (locked — approved by operator 2026-08-03)

### Architecture

- **D-01 — The detector is the single source of truth for warm-up.** The orchestrator stops counting
  readings and reads `warmed_up` / `n_seen` off the `Verdict`. Rationale: two independent counters is
  precisely what produced this bug; a .NET-side fallback counter would reintroduce it. No backward
  compatibility shim is needed — orchestrator and detector ship in one add-on image and are versioned
  together.

- **D-02 — Checkpoints live outside the versioned `ModelStore` path.** New
  `save_checkpoint`/`load_checkpoint` writing a single overwritten
  `models/{slug}/{detector}/checkpoint.pkl` + `checkpoint.json` sidecar. **Explicitly rejected:**
  reusing `save_river()` + `next_version()`, which would create a new `v{N}` directory, a
  `version.json`, and a `shutil.rmtree` prune on every interval — tens of thousands of filesystem
  operations per day. Versioned storage stays for nightly batch fits.

- **D-03 — Checkpoint sidecar contents:** `entity_id`, `detector`, `n_seen`, `river_version`,
  `saved_at`. `river_version` is the invalidation key: River pickles are not safe across versions, so
  a mismatch discards the checkpoint with a WARN and that entity starts cold. Other entities are
  unaffected.

- **D-04 — Pickle the whole `EntityDetector`, not just the counter.** Persisting `n_seen` alone would
  report "warmed up" over an empty forest and publish garbage flags — strictly worse than the current
  bug.

### Checkpoint writer

- **D-05 — Interval 300 s, dirty-tracked.** `ARGUS_CHECKPOINT_INTERVAL_SEC` default `300`
  (`0` disables), `ARGUS_CHECKPOINT_ENABLED` default `true`. Dirty = current `_n_seen` differs from
  the value recorded at the last successful checkpoint. An idle entity writes nothing. Operator runs
  SSD, so write amplification is not a binding constraint — but dirty-tracking stays because it is
  also the correctness-neutral way to keep the writer cheap.

- **D-06 — Reuse the MDL-04 lock discipline.** For each dirty entity: `copy.deepcopy` under
  `_entity_lock`, then pickle + atomic write **outside** the lock. Never hold `_entity_lock` across
  file I/O.

- **D-07 — Atomic write.** `.tmp` + `Path.replace()`, mirroring `_update_latest()`. A crash mid-write
  must never leave a truncated `checkpoint.pkl`.

- **D-08 — SIGTERM flush.** `server.py:129`'s bare `wait_for_termination()` gains signal handling: on
  SIGTERM flush every dirty entity, then `server.stop(grace)`. s6 sends SIGTERM on add-on stop, so a
  clean restart must lose zero readings. Crash/power-loss loses at most one interval — accepted, and
  the reason the interval exists at all rather than shutdown-only saving.

- **D-09 — Restore inside the existing health gate.** Extend `load_all_into()` to also pick up
  `*/*/checkpoint.pkl`, keeping its per-entity log-and-continue behavior, so the detector never
  reports `SERVING` before restore finishes and one bad file cannot block startup.

### Warm-up plumbing

- **D-10 — Proto additions.** `Verdict` gains `warmed_up` + `n_seen` (and the window the detector is
  actually using, so the UI's `x/N` denominator is the detector's, not the orchestrator's guess).
  `Point` gains a `params` map so per-entity `window`/`n_trees` reach `score_one` (fixes D3); params
  are honored at instance-creation time only, matching existing registry semantics.
  `EntityStatusCache.Set` moves from `ScoreStreamPipeline.cs:164` (write loop) to the verdict read
  loop, since the data now arrives with the verdict.

- **D-11 — `HysteresisGate` persistence is OUT OF SCOPE.** Its state (`_consecutiveHigh/Low`,
  `IsAnomalous`) derives from *scores*, not raw readings, so backfill cannot rebuild it, and
  persisting it would require a new .NET-side state-file layer for a ≤3-reading benefit. Document the
  behavior; do not build it. **`FrozenSensorDetector` IS in scope** (D-14) because it rides free on
  the backfill pass.

### InfluxDB backfill

- **D-12 — New `Warmup` RPC, gated on `n_seen == 0`.** The orchestrator sends historical points; the
  detector feeds them through the normal `score_one` path without emitting verdicts and returns the
  resulting `n_seen`/`warmed_up`. The gate is the idempotency mechanism: without it, every
  orchestrator restart would re-feed the same history and distort the model. A checkpointed entity is
  never re-primed.

- **D-13 — New parametrized history query.** `InfluxDbReader.QueryAsync` hardcodes
  `range(start: -24h)` (`InfluxDbReader.cs:81`) and must stay as-is for the batch path. Add
  `QueryHistoryAsync(entityId, lookback, limit)` alongside it, reusing the existing `IInfluxQueryApi`
  seam and the existing `_safeFluxString` injection guards (`InfluxDbReader.cs:68-77` — non-negotiable,
  the same validation must apply to any new interpolated value). Defaults:
  `ARGUS_BACKFILL_LOOKBACK=30d`, limit = the entity's configured window. Points must be fed
  chronologically ascending.

- **D-14 — The same pass primes `FrozenSensorDetector`.** The orchestrator already holds the history
  rows; feeding the last N into `entityState.FrozenDetector.AddReading()` costs a loop and removes the
  post-restart blind window for frozen-sensor detection.

- **D-15 — Degrade safely.** `ARGUS_BACKFILL_ENABLED=true` by default; Influx unconfigured,
  unreachable, erroring, or returning fewer rows than the window → WARN + proceed with normal live
  warm-up. Backfill must never be able to fail startup. A partial prime (e.g. 40 of 250 points) is a
  valid, useful outcome — take it.

### Configuration

- **D-16 — Env vars only, not surfaced in the add-on `config.yaml` schema.** Four knobs:
  `ARGUS_CHECKPOINT_INTERVAL_SEC=300`, `ARGUS_CHECKPOINT_ENABLED=true`,
  `ARGUS_BACKFILL_ENABLED=true`, `ARGUS_BACKFILL_LOOKBACK=30d`. Defaults are correct for the operator's
  deployment; keeping them out of the HA options UI avoids gauge-clutter for settings nobody should
  need to touch. They follow the existing `DetectorConfig` env-var convention.

</decisions>

<specifics>
## Plan Shape (approved)

Four plans. P1 and P2 touch disjoint trees and could wave-parallelize, but P2's proto change is what
P3 builds on, so treat P3 as blocked on P2.

- **15-01 — Detector checkpoints** (PERSIST-01..04)
  `ModelStore.save_checkpoint`/`load_checkpoint`, registry dirty-tracking, the 300 s writer thread,
  SIGTERM flush, `river_version` validation, `load_all_into` extension.

- **15-02 — Proto + orchestrator warm-up-from-verdict** (WARM-01, WARM-02)
  `Verdict.warmed_up`/`n_seen`/window, `Point.params`, `servicer.ScoreStream` forwarding params and
  populating the new verdict fields, `EntityRuntimeState` reading warm-up from the verdict,
  `EntityStatusCache.Set` relocated to the read loop.

- **15-03 — InfluxDB backfill** (BACKFILL-01..04)
  `Warmup` RPC + detector-side priming, `InfluxDbReader.QueryHistoryAsync`, orchestrator call before
  opening each `ScoreStream`, `FrozenDetector` priming, degrade-safe error handling.

- **15-04 — Restart tests, UAT, ship**
  Crash/restart test suite, live-HA UAT, add-on version bump, GHCR deploy per the project's
  `build-push.ps1` recipe.

## Measurement task (first thing in 15-01)

Measure a pickled `EntityDetector` at defaults (25 trees, height 8, window 250). Expected order
0.2–1 MB. This is not a gate — SSD, and dirty-tracking bounds the write rate — but the number belongs
in the plan's notes so the interval default can be revisited with evidence rather than by feel.

## Risks

| Risk | Mitigation |
|------|-----------|
| `deepcopy` of a large model blocks the scoring path while holding `_entity_lock` | Measure it. If >50 ms, checkpoint one entity at a time with a yield between entities. Pickle is already outside the lock (D-06). |
| River version bump silently invalidates every checkpoint | `river_version` sidecar (D-03) makes it explicit and logged, entity-by-entity. |
| `Warmup` re-priming corrupts a model | The `n_seen == 0` gate (D-12) is the single guard — it must be enforced detector-side, not orchestrator-side, so it holds no matter who calls. |
| Historical Influx values differ in scale from live (unit change, sensor swap) | Accepted. Same exposure as any online learner; `MinMaxScaler` adapts. |

## Success Criteria (from ROADMAP)

1. `SIGKILL` mid-warm-up → `n_seen`/`warmed_up` restored, ≤1 interval lost
2. Orchestrator-only restart → warm-up progress unchanged in the UI
3. Add-on restart (SIGTERM) → zero readings lost
4. Idle entity for an hour → zero disk writes
5. `window: 50` configured → detector uses 50, UI shows `x/50`
6. One corrupt `checkpoint.pkl` → startup succeeds, other entities load
7. New entity with ≥250 points of history → `warmed_up` on first live reading
8. Orchestrator restart with existing checkpoint → no re-backfill (`n_seen` does not jump)
9. Influx unavailable → startup succeeds, normal warm-up, WARN only

</specifics>

<deferred>
## Deferred

- **`HysteresisGate` state persistence** — see D-11. Revisit only if post-restart flag latency turns
  out to matter in practice.
- **Surfacing the four env knobs in the add-on options UI** (D-16) — only if a default proves wrong.
- **Checkpointing group/multivariate streaming state** — those detectors are batch-fit nightly and
  already persist through the versioned `ModelStore`; there is no streaming state to lose.
</deferred>

<scope_fence>
Do not modify: MQTT discovery/publication contracts, the versioned `ModelStore` save/load/prune paths
used by the batch scheduler, `InfluxDbReader.QueryAsync`'s existing `-24h` batch behavior,
`HysteresisGate`, or anything under `orchestrator/ui/`.
</scope_fence>
