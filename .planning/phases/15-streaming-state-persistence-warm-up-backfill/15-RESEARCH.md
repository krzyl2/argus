# Phase 15: Streaming State Persistence + Warm-up Backfill - Research

**Researched:** 2026-08-03
**Domain:** River (online ML) model persistence, gRPC Python graceful shutdown under s6-overlay, proto evolution, InfluxDB Flux bounded-history queries
**Confidence:** HIGH (all four source-code claims and the pickle round-trip/deepcopy-latency numbers are directly measured against this repo's installed `river==0.25.0`; the s6/grpc shutdown mechanics are MEDIUM/LOW — training + web search, not tool-verified against this repo's actual s6-overlay version)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

**Architecture**
- D-01 — The detector is the single source of truth for warm-up. The orchestrator stops counting readings and reads `warmed_up`/`n_seen` off the `Verdict`. No backward-compatibility shim — orchestrator and detector ship in one add-on image, versioned together.
- D-02 — Checkpoints live outside the versioned `ModelStore` path. New `save_checkpoint`/`load_checkpoint` writing a single overwritten `models/{slug}/{detector}/checkpoint.pkl` + `checkpoint.json` sidecar. Explicitly rejected: reusing `save_river()` + `next_version()` (tens of thousands of filesystem ops/day). Versioned storage stays for nightly batch fits.
- D-03 — Checkpoint sidecar contents: `entity_id`, `detector`, `n_seen`, `river_version`, `saved_at`. `river_version` mismatch discards the checkpoint with a WARN; that entity starts cold; other entities unaffected.
- D-04 — Pickle the whole `EntityDetector`, not just the counter. Persisting `n_seen` alone would report "warmed up" over an empty forest.

**Checkpoint writer**
- D-05 — Interval 300s, dirty-tracked. `ARGUS_CHECKPOINT_INTERVAL_SEC` default `300` (`0` disables), `ARGUS_CHECKPOINT_ENABLED` default `true`. Dirty = current `_n_seen` differs from the value recorded at the last successful checkpoint. Idle entity writes nothing.
- D-06 — Reuse the MDL-04 lock discipline. For each dirty entity: `copy.deepcopy` under `_entity_lock`, then pickle + atomic write OUTSIDE the lock. Never hold `_entity_lock` across file I/O.
- D-07 — Atomic write. `.tmp` + `Path.replace()`, mirroring `_update_latest()`.
- D-08 — SIGTERM flush. `server.py:129`'s bare `wait_for_termination()` gains signal handling: on SIGTERM flush every dirty entity, then `server.stop(grace)`. Crash/power-loss loses at most one interval — accepted.
- D-09 — Restore inside the existing health gate. Extend `load_all_into()` to also pick up `*/*/checkpoint.pkl`, keeping per-entity log-and-continue behavior.

**Warm-up plumbing**
- D-10 — Proto additions. `Verdict` gains `warmed_up` + `n_seen` + the window the detector is actually using. `Point` gains a `params` map so per-entity `window`/`n_trees` reach `score_one` (fixes D3); params honored at instance-creation time only. `EntityStatusCache.Set` moves from `ScoreStreamPipeline.cs:164` (write loop) to the verdict read loop.
- D-11 — `HysteresisGate` persistence is OUT OF SCOPE. `FrozenSensorDetector` IS in scope (D-14) because it rides free on the backfill pass.

**InfluxDB backfill**
- D-12 — New `Warmup` RPC, gated on `n_seen == 0`. Orchestrator sends historical points; detector feeds them through the normal `score_one` path without emitting verdicts, returns resulting `n_seen`/`warmed_up`. Gate is the idempotency mechanism — enforced detector-side, not orchestrator-side.
- D-13 — New parametrized history query. `InfluxDbReader.QueryAsync`'s hardcoded `range(start: -24h)` stays as-is for the batch path. Add `QueryHistoryAsync(entityId, lookback, limit)` alongside it, reusing the existing `IInfluxQueryApi` seam and `_safeFluxString` injection guards. Defaults: `ARGUS_BACKFILL_LOOKBACK=30d`, limit = the entity's configured window. Points fed chronologically ascending.
- D-14 — The same pass primes `FrozenSensorDetector` — feed the last N history rows into `entityState.FrozenDetector.AddReading()`.
- D-15 — Degrade safely. `ARGUS_BACKFILL_ENABLED=true` by default; Influx unconfigured/unreachable/erroring/fewer rows than window → WARN + proceed with normal live warm-up. Backfill must never fail startup. A partial prime is a valid, useful outcome.

**Configuration**
- D-16 — Env vars only, not surfaced in `config.yaml` schema. `ARGUS_CHECKPOINT_INTERVAL_SEC=300`, `ARGUS_CHECKPOINT_ENABLED=true`, `ARGUS_BACKFILL_ENABLED=true`, `ARGUS_BACKFILL_LOOKBACK=30d`.

### Claude's Discretion
- Exact internal shape of the dirty-tracking bookkeeping (per-entity last-checkpointed `n_seen`) — CONTEXT.md specifies the *semantics*, not the data structure.
- Whether the checkpoint writer is a method on `DetectorRegistry` itself vs. a separate collaborator class — CONTEXT.md only specifies the locking discipline it must follow.
- Exact `Warmup` RPC message shape (proto field layout) — CONTEXT.md specifies behavior (gated on `n_seen==0`, feeds history through `score_one`, returns resulting `n_seen`/`warmed_up`), not wire format.
- Measurement task result (pickle size, deepcopy latency) — CONTEXT.md flags this as "not a gate" but wants the number in the plan's notes.

### Deferred Ideas (OUT OF SCOPE)
- `HysteresisGate` state persistence (D-11) — revisit only if post-restart flag latency matters in practice.
- Surfacing the four env knobs in the add-on options UI (D-16) — only if a default proves wrong.
- Checkpointing group/multivariate streaming state — batch-fit nightly, already persist through versioned `ModelStore`; no streaming state to lose.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| PERSIST-01 | Streaming detector state checkpointed on a recurring interval, not only at shutdown | Section "River pickle round-trip" (verified) + "Checkpoint writer threading pattern" |
| PERSIST-02 | Atomic checkpoint writes; only dirty entities write | Section "Don't Hand-Roll" (reuse `_update_latest` atomic pattern) + "Dirty-tracking bookkeeping" pitfall |
| PERSIST-03 | SIGTERM flush — clean add-on restart loses zero readings | Section "Python graceful shutdown under s6" |
| PERSIST-04 | Checkpoints restored before healthy; corrupt/incompatible checkpoint discarded per-entity | Section "load_all_into extension" + "Checkpoint/versioned-load ordering" pitfall |
| WARM-01 | Detector is single source of truth for warm-up; orchestrator drops its own counter | Section "Proto evolution" + "Where the orchestrator calls Warmup" |
| WARM-02 | Per-entity `hst` params (`window`, `n_trees`) reach the detector | Section "Proto evolution" (`Point.params`) |
| BACKFILL-01 | Cold entity primed from InfluxDB history before live streaming | Section "Flux query for bounded history" + "Warmup RPC design" |
| BACKFILL-02 | Backfill idempotent — never re-feeds an already-primed/checkpointed detector | Section "n_seen==0 gate must be detector-side" (D-12, reinforced by measured pickle round-trip) |
| BACKFILL-03 | Backfill degrades safely on Influx failure | Section "Environment Availability" + existing `InfluxDbReader.QueryAsync` guard pattern |
| BACKFILL-04 | Same backfill pass primes `FrozenSensorDetector` | Section "Where the orchestrator calls Warmup" |
</phase_requirements>

## Project Constraints (from CLAUDE.md)

- Architecture: .NET 8 orchestrator + Python gRPC detector — locked. All ML stays in Python; do not add any .NET-side model logic.
- Transport: gRPC over LAN with mTLS. New `Warmup` RPC must be added to the same `DetectorService` and inherits the existing channel/mTLS setup — no new transport.
- Pinned versions matter: River 0.25.0, grpcio 1.81.0, InfluxDB.Client 5.0.0, Grpc.Tools 2.80.0, .NET 8. All measurements below were taken against the actually-installed `river==0.25.0` and `grpcio==1.81.0` in this repo's `detector/` environment.
- Licenses: BSD/Apache/MIT only. This phase adds no new third-party dependencies (see Package Legitimacy Audit) — no license risk introduced.
- Hosting: self-hosted, no cloud. No change.
- GPU: not applicable to this phase (CPU-only streaming path).

## Summary

This phase is a set of surgical, source-grounded fixes to three already-diagnosed defects — it is not
an open design problem. The approved design (CONTEXT.md D-01..D-16) is sound and matches how the
existing codebase already solves adjacent problems (`_update_latest`'s atomic tmp+rename, `_entity_lock`
train-outside-lock discipline, `load_all_into`'s per-entity try/except). The main risks this research
surfaces are not "will the design work" but "where exactly do the existing conventions almost — but not
quite — cover the new code, and where do the CONTEXT.md numeric assumptions need correcting with
measured data."

Two verified findings materially affect planning. First, `pickle.dumps()` on a fully-warmed
`EntityDetector` (window=250, n_trees=25, height=8) measured **409 KiB** — inside CONTEXT.md's estimated
0.2–1 MB range, confirming the 300s interval default is safe on the operator's SSD. Second, and more
importantly, **`copy.deepcopy()` of that same warmed detector measured 56–96 ms** on this machine — this
is the CONTEXT.md Risk table's ">50 ms" trigger, and it fires at defaults, not just at scale. The
per-entity-yield mitigation the Risk table describes as conditional ("if >50ms...") should be planned as
the baseline design, not a fallback, if there is more than one entity to checkpoint per interval.

The second significant finding is architectural: `DetectorRegistry`'s dict (`_detectors`) and locks
(`_entity_locks`, `_entity_lock()`) are private. The checkpoint writer needs to enumerate all `hst`
entries and snapshot each one under its per-entity lock — this can only be done cleanly as a *method on
`DetectorRegistry` itself* (reusing its existing privates), not as an external collaborator reaching into
them. Plan 15-01 should design the writer this way from the start.

Third, `EntityDetector` currently exposes no public `n_seen`/`window` accessors (only the boolean
`is_warmed_up`) — Plan 15-02's `Verdict.n_seen`/`Verdict.window` fields need two small additive properties
on `EntityDetector` and one new `DetectorRegistry` accessor before the servicer can populate them.

**Primary recommendation:** Implement the checkpoint writer as new methods on `DetectorRegistry`
(`dirty_hst_keys()` + `checkpoint_dirty(model_store)`), reuse `ModelStore`'s existing atomic-write helper
pattern verbatim for `save_checkpoint`/`load_checkpoint`, and treat the measured 56–96 ms deepcopy latency
as proof that the per-entity-yield mitigation in the CONTEXT.md Risk table is required, not optional.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Streaming model state checkpointing (pickle + atomic write) | API/Backend (Python detector) | — | Model state lives entirely in the detector process; orchestrator never sees it (D2). |
| Warm-up truth (`n_seen`/`warmed_up`) | API/Backend (Python detector) | API/Backend (.NET orchestrator, read-only) | D-01: detector computes and owns it; orchestrator only reads it off the `Verdict` — no computation on the .NET side. |
| Per-entity `hst` params propagation | API/Backend (.NET orchestrator → proto → Python detector) | — | Orchestrator already resolves `HstParams` from `entities.yaml`; the gap is purely wire-transport (`Point.params` was never populated). |
| InfluxDB history query (backfill source) | API/Backend (.NET orchestrator) | Database/Storage (InfluxDB) | `InfluxDbReader`/`IInfluxQueryApi` already live in the orchestrator; Python detector has no InfluxDB client and must not gain one (keeps the batch-vs-streaming data-access boundary D2 established). |
| Backfill priming (feeding history through `score_one`) | API/Backend (Python detector) | — | Model mutation is detector-only; orchestrator's role ends at fetching rows and sending them over the new `Warmup` RPC. |
| `FrozenSensorDetector` priming | API/Backend (.NET orchestrator) | — | `FrozenSensorDetector` is a pure .NET class with no detector-side equivalent; it rides on the same Influx rows the orchestrator already holds for the `Warmup` RPC call (D-14). |
| Process lifecycle (SIGTERM flush) | API/Backend (Python detector) + Database/Storage (s6-overlay process supervision) | — | The flush is detector-internal; the *timing budget* for it is bounded by s6-overlay's kill-grace behavior, an infrastructure-tier constraint the detector code must respect but cannot control. |

## Standard Stack

No new third-party libraries. Every capability in this phase is built from what is already a direct or
transitive dependency:

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `river` | 0.25.0 (pinned, installed) | `HalfSpaceTrees` + `MinMaxScaler` — the model being checkpointed | Already the project's chosen streaming ML library (D2/D9 lock); `pickle` is River's own documented persistence mechanism `[VERIFIED: verified locally against installed river==0.25.0; CITED: river official docs (docs/recipes/saving-loading.ipynb, docs/faq)]` |
| `pickle` (stdlib) | Python 3.12 stdlib | Serialize/deserialize `EntityDetector` | Same mechanism `ModelStore.save_river`/`load_river` already use — River has no `to_dict()` for anomaly detectors (PITFALL 3, already documented in `model_store.py`) `[VERIFIED: model_store.py:16, 130]` |
| `threading` (stdlib) | Python 3.12 stdlib | Periodic checkpoint-writer thread + dirty-tracking | Matches `DetectorRegistry`'s existing `threading.Lock`/`threading.Event` conventions (T-06-01, MDL-04) `[VERIFIED: registry.py:17,55,58]` |
| `signal` (stdlib) | Python 3.12 stdlib | SIGTERM handler for D-08 flush-before-exit | Standard Python signal-handling API; grpc's own examples use this exact pattern `[CITED: grpc.io/docs/guides/server-graceful-stop/]` |
| `grpc.server.stop(grace)` | grpcio 1.81.0 (pinned, installed) | Graceful RPC drain after the checkpoint flush completes | Already the project's server object (`server.py`); `stop()` returns a `threading.Event`, confirmed via direct introspection of the installed 1.81.0 API `[VERIFIED: measured locally via inspect.signature(grpc.Server.stop)]` |
| `InfluxDB.Client` (.NET) | 5.0.0 (pinned, installed) | New `QueryHistoryAsync` reusing the existing `IInfluxQueryApi` seam | Already the project's InfluxDB client; `InfluxDbReader` already wraps it (D-13 explicitly reuses this seam) `[VERIFIED: InfluxDbReader.cs, IInfluxQueryApi.cs]` |
| `Grpc.Tools` (.NET) | 2.80.0 (pinned) / `grpcio-tools` (Python) 1.81.0 (pinned, installed) | Regenerate stubs after `Point`/`Verdict`/new `Warmup` RPC proto changes | Both already wired: `.csproj`'s `<Protobuf Include=.../>` item regenerates at every .NET build; `detector/scripts/gen_proto.py` regenerates at every `pytest` run via an autouse session fixture `[VERIFIED: Argus.Orchestrator.csproj:23, gen_proto.py, test_proto_codegen.py:29-35]` |

### Supporting
None — no new supporting libraries needed. `copy.deepcopy` (stdlib) is already used by `DetectorRegistry.fit_one`.

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Pickle for checkpoint | `river`'s `to_dict()`/JSON export | Does not exist for `anomaly.HalfSpaceTrees` (confirmed PITFALL 3, still true in 0.25.0 per Context7 docs check) — not a real option |
| Plain `threading.Thread` + `time.sleep` for the 300s interval | `threading.Event.wait(timeout)` loop | `Event.wait` is interruptible (needed for prompt SIGTERM-triggered flush without waiting out the remaining sleep) — see Common Pitfalls |
| New `Warmup` RPC (unary) | Reuse `ScoreStream` with a "no-verdict" flag on `Point` | CONTEXT.md D-12 already locks the new-RPC approach; reusing `ScoreStream` would conflate a batch-style backfill call with the bidi live-streaming contract — not revisited here per instructions |

**Installation:** No `pip install` / `npm install` needed — every symbol used is already a pinned dependency or stdlib.

**Version verification:**
```
detector/requirements.txt (installed, confirmed via `python -c "import river; print(river.__version__)"`):
  river==0.25.0        -> confirmed 0.25.0 installed
  grpcio==1.81.0        -> confirmed 1.81.0 installed (grpc.__version__)
```
Both match `CLAUDE.md`'s pinned versions exactly — no drift to account for.

## Package Legitimacy Audit

**No external packages are installed in this phase.** All new code (checkpoint writer, `Warmup` RPC,
`QueryHistoryAsync`) is built from libraries already present in `detector/requirements.txt` /
`Argus.Orchestrator.csproj` (see Standard Stack above) plus stdlib. The Package Legitimacy Gate does not
apply — there is nothing to run `gsd-tools query package-legitimacy check` against.

**Packages removed due to [SLOP] verdict:** none.
**Packages flagged as suspicious [SUS]:** none.

## Architecture Patterns

### System Architecture Diagram

```
                         ┌─────────────────────────── Python Detector Process ───────────────────────────┐
                         │                                                                                 │
  HA state_changed   ┌───┴────┐    Point(entity_id,       ┌──────────────┐    Verdict(score,               │
  ───────────────►   │Orches- │    value, params[NEW])    │DetectorServicer│   warmed_up[NEW],              │
  (.NET)              │trator  │ ────────ScoreStream─────► │.ScoreStream   │   n_seen[NEW], window[NEW])   │
                       │        │ ◄──────────────────────  │               │ ──────────────────────────►   │
                       └───┬────┘                          └──────┬───────┘                                │
                           │                                       │                                       │
                           │ CFG-04 reload / cold entity            │ registry.score_one(id,val,det,params) │
                           │ (n_seen==0 on this entity?)             ▼                                      │
                           │                                  ┌─────────────┐                               │
                           │  Warmup(entity_id, history[],   │DetectorRegis│  lazily creates / reuses       │
                           │         params) ────────────────►│try          │  EntityDetector per (id,det)  │
                           │  ◄──── WarmupResponse            └──────┬──────┘                               │
                           │        (n_seen, warmed_up)               │                                     │
                           │                                          │ every 300s (dirty-tracked)          │
                           │                                          ▼                                     │
                           │                                   ┌─────────────────────┐                      │
                           │                                   │Checkpoint writer     │                      │
                           │                                   │(new: DetectorRegistry│                      │
                           │                                   │ .checkpoint_dirty())  │                      │
                           │                                   └──────────┬───────────┘                      │
                           │                                              │ deepcopy under _entity_lock,      │
                           │                                              │ pickle + atomic write OUTSIDE lock│
                           │                                              ▼                                  │
                           │                                   /data/models/{slug}/hst/checkpoint.pkl        │
                           │                                   /data/models/{slug}/hst/checkpoint.json       │
                           │                                              ▲                                  │
                           │                       load_all_into() (startup, before SERVING) reads this too  │
                           │                                                                                 │
  InfluxDbReader           │ QueryHistoryAsync(entityId, lookback, limit) ── ascending chronological rows    │
  .QueryHistoryAsync [NEW] ◄┘  (only called when a cold entity's checkpoint is absent → n_seen==0 on detector)│
       │                                                                                                     │
       ▼                                                                                                     │
  entityState.FrozenDetector.AddReading()  (D-14, .NET-only, rides the same Influx rows)                     │
                                                                                                               │
                         └────────────────────────────────────────────────────────────────────────────────────┘

  On SIGTERM (add-on stop): signal handler → checkpoint_dirty(force=all) → server.stop(grace).wait(bounded)
```

### Recommended Project Structure
No new files/folders — this phase extends existing modules in place:
```
detector/argus_detector/
├── hst_detector.py     # + n_seen/window public properties (needed for Verdict fields)
├── registry.py         # + dirty_hst_keys(), checkpoint_dirty(), get_n_seen(), Warmup-priming entry point
├── model_store.py       # + save_checkpoint(), load_checkpoint(); load_all_into() extended for */*/checkpoint.pkl
├── servicer.py          # ScoreStream forwards params + populates new Verdict fields; new Warmup() RPC handler
├── config.py            # + checkpoint_interval_sec, checkpoint_enabled (ARGUS_CHECKPOINT_*)
└── server.py            # + SIGTERM handler wiring the checkpoint flush before server.stop(grace)

orchestrator/Argus.Orchestrator/
├── Config/ConnectionSettings.cs   # + BackfillEnabled, BackfillLookback (ARGUS_BACKFILL_*; NOT DetectorConfig — see Pitfall)
├── Batch/InfluxDbReader.cs         # + QueryHistoryAsync(entityId, lookback, limit) alongside existing QueryAsync
├── Detection/ScoreStreamPipeline.cs # BuildEntityStates / RunEntityStreamAsync gains a pre-stream Warmup call site
├── Detection/EntityRuntimeState.cs  # WarmedUp/ReadingCount/WarmUpWindow become verdict-driven, not self-counted
└── Detection/EntityStatusCache.cs   # .Set() call site moves from write loop to read loop (D-10)

proto/argus.proto        # Point.params map, Verdict.warmed_up/n_seen/window, new Warmup RPC + messages
```

### Pattern 1: Checkpoint writer as a `DetectorRegistry` method (not an external collaborator)
**What:** Implement the periodic checkpoint pass as new methods directly on `DetectorRegistry`
(e.g. `dirty_hst_keys() -> list[tuple[str,str]]` and `checkpoint_dirty(model_store) -> None`), since
`_detectors`, `_entity_locks`, and `_entity_lock()` are all private (leading underscore) and the MDL-04
discipline (D-06) requires touching them directly.
**When to use:** Any new cross-cutting operation over the registry's entries that must respect the
existing per-entity lock discipline.
**Example:**
```python
# Source: pattern derived from existing registry.py fit_one()/score_batch() (train-outside-lock idiom)
def checkpoint_dirty(self, model_store, last_checkpointed: dict[tuple[str, str], int]) -> None:
    for key in self._hst_keys():                      # snapshot key list under self._lock
        entity_id, detector = key
        lock = self._entity_lock(key)
        with lock:
            det = self._detectors.get(key)
            if det is None:
                continue
            current_n_seen = det._n_seen               # or a new public property
            if current_n_seen == last_checkpointed.get(key):
                continue                                 # D-05: not dirty — skip
            snapshot = copy.deepcopy(det)                # under lock (D-06) — MEASURED 56-96ms at defaults
        # pickle + atomic write happen OUTSIDE the lock (D-06)
        model_store.save_checkpoint(entity_id.replace(".", "_"), detector, snapshot)
        last_checkpointed[key] = current_n_seen
```

### Pattern 2: Atomic checkpoint write — reuse `_update_latest`'s exact tmp+rename shape
**What:** `save_checkpoint`/`load_checkpoint` should mirror `ModelStore._update_latest()`'s proven
`.tmp` write + `Path.replace()` pattern, applied to `checkpoint.pkl` and `checkpoint.json` (two files, two
independent atomic renames — no need for cross-file atomicity since a mismatched pair is caught by
`river_version` validation on load per D-03).
**When to use:** Any new on-disk artifact where "never leave a truncated file after a crash mid-write" matters (D-07).
**Example:**
```python
# Source: model_store.py:326-335 (_update_latest), adapted
def save_checkpoint(self, slug: str, detector: str, model: object, entity_id: str, n_seen: int) -> None:
    d = self._root / slug / detector
    d.mkdir(parents=True, exist_ok=True)
    tmp_pkl = d / "checkpoint.pkl.tmp"
    with open(tmp_pkl, "wb") as f:
        pickle.dump(model, f)
    tmp_pkl.replace(d / "checkpoint.pkl")               # atomic — mirrors _update_latest
    sidecar = {
        "entity_id": entity_id, "detector": detector, "n_seen": n_seen,
        "river_version": river.__version__,
        "saved_at": datetime.now(timezone.utc).isoformat(),
    }
    tmp_json = d / "checkpoint.json.tmp"
    tmp_json.write_text(json.dumps(sidecar))
    tmp_json.replace(d / "checkpoint.json")              # atomic
```

### Pattern 3: SIGTERM handler → flush → bounded grace stop
**What:** Register a `signal.signal(signal.SIGTERM, handler)` on the main thread (grpc python examples
and multiple sources confirm: Python signal handlers only fire reliably on the main thread) that
synchronously flushes all dirty checkpoints, then calls `server.stop(grace)` and waits on the returned
`threading.Event` with a bounded timeout as a safety net.
**When to use:** D-08's "clean add-on restart loses zero readings" requirement.
**Example:**
```python
# Source: pattern combines grpc.io/docs/guides/server-graceful-stop/ guidance [CITED] with this
# repo's existing server.py structure; grpc.Server.stop signature confirmed via
# inspect.signature() against the installed grpcio==1.81.0 [VERIFIED]
def _install_sigterm_flush(server, registry, model_store, last_checkpointed):
    def _handle_sigterm(signum, frame):
        registry.checkpoint_dirty(model_store, last_checkpointed)   # D-08: flush before stop
        stop_event = server.stop(grace=10)          # returns threading.Event; new RPCs rejected immediately
        stop_event.wait(10)                          # bounded safety net — do not block forever
    signal.signal(signal.SIGTERM, _handle_sigterm)
```
**Constraint from s6-overlay:** the flush + `stop(grace).wait()` combined must complete well inside
whatever kill-grace window s6-overlay allows before escalating to SIGKILL (see Common Pitfalls —
confidence on the exact default is LOW; the plan should make the writer's per-entity work fast enough
that this is a non-issue rather than depend on a precise number).

### Pattern 4: `Point.params` mirrors the already-working `FitRequest.params`/`ScoreBatchRequest.params` shape
**What:** `Point` gains `map<string, string> params = 4;` — the exact same shape `FitRequest`/
`ScoreBatchRequest`/`GroupScoreRequest` already use successfully for per-entity/per-group param overrides.
`servicer.ScoreStream` then calls `self._registry.score_one(entity_id, value, params=dict(point.params))`
instead of the current no-params call (fixes D3).
**When to use:** WARM-02.
**Example:**
```protobuf
// Source: proto/argus.proto (existing FitRequest.params=3 pattern, applied to Point)
message Point {
  string entity_id = 1;
  google.protobuf.DoubleValue value = 2;
  google.protobuf.Timestamp timestamp = 3;
  map<string, string> params = 4;   // NEW — WARM-02, mirrors FitRequest/ScoreBatchRequest convention
}
```
```csharp
// Source: ScoreStreamPipeline.cs ToPoint() — orchestrator side populates it from resolved HstParams
private static Point ToPoint(Ha.HaReading reading, HstParams hstParams)
    => new Point {
        EntityId = reading.EntityId,
        Value = reading.Value,
        Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(reading.LastChanged),
        Params = { ["window"] = hstParams.Window.ToString(), ["n_trees"] = hstParams.NTrees.ToString() },
    };
```

### Pattern 5: Flux "last N points, ascending" — extend the existing `_safeFluxString`-guarded builder
**What:** `QueryHistoryAsync(entityId, lookback, limit)` should reuse `InfluxDbReader`'s exact validation
block (same `_safeFluxString` regex applied to the same fields) and its own explicit `sort()` call before
bounding — do not rely on `tail()`'s implicit ordering guarantee without an explicit prior sort (see
Common Pitfalls).
**Example:**
```csharp
// Source: pattern extends InfluxDbReader.cs:79-86's existing flux string; sort+limit idiom
// per Context7 InfluxDB v2 docs (docs.influxdata.com/influxdb/v2/query-data/flux/sort-limit) [CITED]
var flux = $"""
    from(bucket: "{_settings.InfluxBucket}")
      |> range(start: -{lookback})
      |> filter(fn: (r) => r["_measurement"] == "{_settings.InfluxMeasurement}"
            and r["entity_id"] == "{entityId}"
            and r["_field"] == "{_settings.InfluxValueField}")
      |> sort(columns: ["_time"], desc: true)
      |> limit(n: {limit})
      |> sort(columns: ["_time"], desc: false)
    """;
// Same validation calls as QueryAsync: _safeFluxString.IsMatch(entityId/bucket/measurement/valueField),
// PLUS validate `limit` is a parsed positive int (never interpolate a raw caller-controlled limit string).
```

### Anti-Patterns to Avoid
- **Storing checkpoint dirty-state on `EntityDetector` itself:** keep "last-checkpointed n_seen" bookkeeping
  in the registry/writer, not on the pickled object — otherwise the bookkeeping value gets serialized and
  restored as stale on every restart, corrupting the dirty check on the very first tick after boot.
- **Calling `registry._detectors`/`registry._entity_locks` from outside `DetectorRegistry`:** breaks
  encapsulation and duplicates the lock-acquisition logic that already exists in `_entity_lock()` —
  implement the checkpoint sweep as a registry method instead (Pattern 1).
- **Reading `ARGUS_BACKFILL_ENABLED`/`ARGUS_BACKFILL_LOOKBACK` in Python `DetectorConfig`:** these two
  knobs govern the *orchestrator's* Influx query and RPC call decision — they belong in .NET
  `ConnectionSettings`/`Program.cs`, not `detector/argus_detector/config.py`. Only
  `ARGUS_CHECKPOINT_INTERVAL_SEC`/`ARGUS_CHECKPOINT_ENABLED` belong in Python `DetectorConfig`. See
  Common Pitfalls.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Atomic file write | A new tmp-file+rename helper | Copy the exact 5-line pattern from `ModelStore._update_latest()` (`model_store.py:326-335`) | Already correct, already tested, `Path.replace()` is atomic on both POSIX and Windows dev boxes per its own docstring |
| River model serialization | Any custom `to_dict()`/JSON export | `pickle.dump`/`pickle.load` | River's own docs confirm pickle is the supported, complete-state-preserving mechanism for anomaly detectors `[CITED: river official docs]`; verified round-trip locally |
| Per-entity locking for concurrent checkpoint-vs-score access | A new lock type | `DetectorRegistry._entity_lock()` (existing MDL-04 mechanism) | Already solves exactly this problem for `fit_one`/`score_batch`; a second, parallel locking scheme would be a correctness bug waiting to happen (two independent counters is literally the D2 defect being fixed) |
| Graceful shutdown coordination | A custom drain/wait loop | `grpc.Server.stop(grace)` → `threading.Event` | Built into grpcio 1.81.0; confirmed via direct API introspection — no need to hand-roll RPC draining |

**Key insight:** every piece of this phase's new machinery has a near-identical existing precedent in this
codebase (atomic write, per-entity lock discipline, params map, gRPC health-gate-before-serving). The
highest-risk mistake is not "missing a pattern" but "building a second parallel version of a pattern that
already exists" — exactly how D2 (the second warm-up counter) happened in the first place.

## Common Pitfalls

### Pitfall 1: `deepcopy` under lock is measurably >50ms at defaults, not just "at scale"
**What goes wrong:** CONTEXT.md's Risk table treats ">50ms" as a conditional trigger for the
per-entity-yield mitigation. Measured locally: `copy.deepcopy()` of a fully-warmed `EntityDetector`
(window=250, n_trees=25, height=8, `_n_seen`=1000) takes **56–96 ms** on this development machine — this
is the trigger condition, at defaults, with zero scale.
**Why it happens:** `HalfSpaceTrees` with 25 trees × height 8 builds a nontrivial nested tree structure;
`copy.deepcopy` walks and copies all of it, node by node.
**How to avoid:** Plan the checkpoint sweep to hold each entity's `_entity_lock` for only that one
entity's `deepcopy` (already required by D-06), and insert a yield point (e.g. `time.sleep(0)` or an
`await`-equivalent boundary if ported to async) between entities in the sweep loop — do not treat this as
optional. With N entities all dirty in the same 300s tick, N×~75ms of *cumulative* lock-holding could
otherwise visibly stall the gRPC ScoreStream read/write loops for those same entities.
**Warning signs:** ScoreStream latency logs (`OBS-01`/`STRM-04`, already emitted per verdict) showing a
periodic latency spike every ~300s correlated across multiple entities.

### Pitfall 2: `load_all_into`'s two glob passes can race on load order for the same key
**What goes wrong:** `load_all_into()` currently globs `*/*/latest` (versioned batch models). D-09 extends
it to also glob `*/*/checkpoint.pkl`. Both patterns can match files at the *same directory depth*
(`root/slug/detector/...`) for the same `(slug, detector)` key if a stale versioned river model ever
existed there (pre-this-phase, `save_river` was reachable only via the never-called `SaveModel` RPC, so
this is currently only a live risk for `mad`/`stl`-detector entries, not `hst` — but the code must not
assume that forever). Since `registry.register()` unconditionally overwrites the dict entry, whichever
glob pass runs *last* silently wins.
**Why it happens:** Two independent `glob()` calls with no defined relative ordering guarantee across
Python versions/filesystems.
**How to avoid:** Run the `checkpoint.pkl` glob pass strictly *after* the `latest` glob pass in
`load_all_into()`, so a checkpoint (D-01's source of truth for streaming state) always wins over any
stale versioned artifact for the same key — make this ordering explicit in code with a comment, not
implicit via glob call order.
**Warning signs:** A restart test asserting `n_seen` after restore returns a suspiciously round or wrong
number that doesn't match the checkpoint's actual sidecar `n_seen`.

### Pitfall 3: `ARGUS_BACKFILL_*` env vars are orchestrator-side (.NET), not detector-side (Python) — despite CONTEXT.md's wording
**What goes wrong:** D-16 says all four new knobs "follow the existing `DetectorConfig` env-var
convention." Read literally, this suggests adding all four to `detector/argus_detector/config.py`. But
`ARGUS_BACKFILL_ENABLED`/`ARGUS_BACKFILL_LOOKBACK` govern **`InfluxDbReader.QueryHistoryAsync`**, which is
.NET orchestrator code — the Python detector has no InfluxDB client and must not gain one (this would
violate the D2 architecture lock: all ML/data-access boundaries stay where they are). Only
`ARGUS_CHECKPOINT_INTERVAL_SEC`/`ARGUS_CHECKPOINT_ENABLED` are genuinely detector-side.
**Why it happens:** CONTEXT.md's D-16 bullet lists all four together without distinguishing which
process reads which — a natural reading trap.
**How to avoid:** `ARGUS_CHECKPOINT_*` → Python `DetectorConfig` (mirrors `ARGUS_MODEL_ROOT` pattern,
`config.py:14`). `ARGUS_BACKFILL_*` → .NET `ConnectionSettings` + `Program.cs`'s
`builder.Configuration["ARGUS_..."]` read pattern (mirrors `ARGUS_BATCH_INTERVAL_MIN`,
`Program.cs:31-52`) — plain flat env var names, no `10-config-gen.sh` change needed since these are
process-env, not add-on `config.yaml` options (D-16 confirms not surfaced in schema), but they DO need a
line each in `10-config-gen.sh` if they are to have non-default values ever settable — confirm with the
plan whether hardcoded defaults baked into the two config classes suffice (likely yes, since D-16 says
"defaults are correct for the operator's deployment").
**Warning signs:** A plan that adds `ARGUS_BACKFILL_LOOKBACK` parsing to `detector/argus_detector/config.py`
and then has no code path that ever reads it (dead code) while the actual `QueryHistoryAsync` call site
hardcodes `30d`.

### Pitfall 4: `EntityDetector`/`DetectorRegistry` currently expose no `n_seen`/`window` accessors for `Verdict` population
**What goes wrong:** D-10 requires `Verdict.n_seen` and `Verdict.window` (the window the detector is
*actually* using). `EntityDetector` only exposes `is_warmed_up` (a bool); `_n_seen` and
`self._model.window_size` are both private/internal. `DetectorRegistry` only exposes `is_warmed_up()`
(also a bool), no `n_seen`. A plan that tries to populate the new `Verdict` fields directly from
`servicer.ScoreStream` will need these two small additive changes first, or it will reach into `det._n_seen`
from outside the class (breaks the same encapsulation this phase is trying to clean up).
**Why it happens:** `is_warmed_up` was sufficient before this phase; nothing needed the raw counter or
window outside `EntityDetector` itself.
**How to avoid:** Add `EntityDetector.n_seen` (property, returns `self._n_seen`) and `EntityDetector.window`
(property, returns `self._model.window_size`) in Plan 15-01 (or as a small addition inside 15-02, since
15-02 is the consumer) — cheap, and D-06's checkpoint-dirty check already needs to read `_n_seen`
externally-ish (from within the registry, which is fine since it's the owning module).
**Warning signs:** Plan 15-02 tasks that read `registry._detectors[key]._n_seen` directly from `servicer.py`.

### Pitfall 5: s6-overlay's kill-grace default is not reliably known for this repo's actual base image — do not hardcode a `grace` value assuming a specific number
**What goes wrong:** The SIGTERM flush (D-08) needs `server.stop(grace).wait(timeout)` to complete before
s6-overlay's own timeout escalates to SIGKILL. Web search results suggest an `S6_KILL_GRACETIME`
environment variable (order of 5–10 seconds is a commonly cited value) plus per-service
`timeout-kill`/`timeout-finish` files, but this was **not verified against this repo's actual
`argus/rootfs/etc/services.d/detector/` directory contents** (no `timeout-kill` file was found there —
only a `run` script and, in remote mode, a `down` file) `[ASSUMED — LOW confidence, websearch only, not
cross-checked against this repo's s6-overlay version or Dockerfile]`.
**Why it happens:** s6-overlay's default kill-grace has changed across major versions (v2 vs v3), and the
add-on's actual base image / s6-overlay version was not independently confirmed in this research pass.
**How to avoid:** Keep the flush path itself fast (it is — see Pitfall 1's mitigation; checkpointing a
handful of entities' pickles is sub-second once deepcopy latency is bounded by the yield pattern) so the
exact grace-period number matters less. If the plan wants a hard number, it should grep
`argus/rootfs/etc/services.d/detector/` for a `timeout-kill` file and/or check `argus/Dockerfile` for an
`S6_KILL_GRACETIME` ENV line as a verification task, rather than trust this research's web-search figure.
**Warning signs:** UAT scenario 3 (SIGTERM → zero readings lost) intermittently fails only on slower
hardware or when many entities are dirty simultaneously.

## Code Examples

### Measured: River `EntityDetector` pickle round-trip (verified locally, not assumed)
```python
# Source: measured directly against this repo's detector/argus_detector/hst_detector.py
# and the installed river==0.25.0, via a throwaway script run in this research session.
from argus_detector.hst_detector import EntityDetector
import pickle, copy

det = EntityDetector(window=250, n_trees=25)   # D-09 defaults
for _ in range(500):
    det.score_one(20.0)   # simulate warm-up

blob = pickle.dumps(det)
print(len(blob))            # -> 419291 bytes (~409 KiB) — matches CONTEXT.md's 0.2-1MB estimate

restored = pickle.loads(blob)
print(restored._n_seen, restored.is_warmed_up)   # -> 500 True — full state round-trips correctly

# Deepcopy latency (the D-06 lock-holding operation):
c = copy.deepcopy(det)   # measured 56-96ms across 20 runs at n_seen=1000 — SEE PITFALL 1
```
Result confirms: (1) pickle size estimate in CONTEXT.md was accurate, (2) round-trip preserves full
learned state (scores match bit-for-bit before/after restore on a held-out value), (3) the deepcopy
latency risk in the CONTEXT.md Risk table is real at defaults, not just hypothetical at scale.

### `grpc.Server.stop()` signature (verified against installed grpcio==1.81.0)
```python
# Source: measured via inspect.signature(grpc.server(...).stop) against installed grpcio==1.81.0
# stop(grace: Optional[float]) -> threading.Event
# wait_for_termination(timeout: Optional[float] = None) -> bool
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|---------------|--------|
| `SaveModel`/`LoadModel` RPCs as the (unused) persistence path for streaming HST state | Direct in-process checkpoint writer + startup restore, entirely internal to the detector | This phase (D-02) | Removes the orchestrator's need to ever call `SaveModel`/`LoadModel` for `hst` — those RPCs remain for the versioned batch path only |
| Orchestrator-side `_readingCount` warm-up counter | Detector-side `n_seen`/`warmed_up` on `Verdict`, orchestrator reads only | This phase (D-01/WARM-01) | Eliminates the two-independent-counters defect class entirely |

**Deprecated/outdated:** none of River's own APIs are deprecated in 0.25.0 relevant to this phase — the
library's pickle-based persistence guidance is unchanged and current per the official docs checked this
session.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | s6-overlay's default kill-grace before SIGKILL is on the order of 5-10 seconds (`S6_KILL_GRACETIME`), and no per-service `timeout-kill` override currently exists in `argus/rootfs/etc/services.d/detector/` | Common Pitfalls / Pitfall 5 | If the actual grace window is much shorter than assumed, the SIGTERM flush could be killed mid-write on a slow/loaded machine — mitigated by keeping the flush fast (Pitfall 1's yield pattern), but the plan should still verify the real value via a repo grep task rather than trust this number |
| A2 | Python signal handlers registered via `signal.signal()` in `server.py`'s main thread will reliably fire when s6 sends SIGTERM to the detector process (i.e., nothing about the s6/exec chain double-forks or changes the signal-receiving PID) | Architecture Patterns / Pattern 3 | If s6's `exec python3 -m argus_detector.server` (confirmed in `services.d/detector/run:17` — it does use `exec`, which is the correct pattern to avoid this exact problem) somehow didn't preserve PID 1 signal delivery, the flush would never fire; `exec` usage was independently confirmed in the actual `run` script, which lowers this risk substantially |
| A3 | Flux's `tail()` function preserves the input table's existing row order (rather than re-sorting) when picking the last N rows | Architecture Patterns / Pattern 5 | If wrong, an implementation using bare `tail(n:)` without an explicit prior `sort()` could return rows in an unexpected order, breaking BACKFILL-01's "chronologically ascending" requirement — mitigated by the recommendation to always use explicit `sort()` + `limit()` (Pattern 5) rather than relying on `tail()`'s implicit behavior, so this assumption does not actually gate correctness of the recommended approach |

**All three assumptions above are mitigated by design choices already reflected in this document's
recommendations (fast flush, `exec`-based process start already confirmed, explicit sort+limit)** — none
of them, if wrong, would go undetected without breaking a specific UAT scenario from CONTEXT.md's
Success Criteria list, which the plan should include as explicit test cases regardless.

## Open Questions

1. **Exact `Warmup` RPC message shape**
   - What we know: behavior is fully locked (D-12) — gated on `n_seen==0`, feeds history through the
     normal `score_one` path without emitting verdicts, returns final `n_seen`/`warmed_up`.
   - What's unclear: whether history should travel as `repeated Point` (reusing the existing message,
     consistent with `ScoreBatchRequest.window`) or a new lighter `repeated double values` — the former
     is more consistent with existing proto conventions (`FitRequest.window`/`ScoreBatchRequest.window`
     both use `repeated Point`).
   - Recommendation: use `repeated Point history` + `map<string,string> params` (mirrors `FitRequest`
     exactly) — this is Claude's Discretion per CONTEXT.md, and consistency with the existing
     `FitRequest`/`ScoreBatchRequest` shape (both already `repeated Point` + `params` map) is the
     strongest signal for what the planner should pick.

2. **Where exactly the `n_seen==0` gate lives**
   - What we know: D-12 says "the gate must be enforced detector-side, not orchestrator-side, so it holds
     no matter who calls."
   - What's unclear: whether the check belongs in `servicer.Warmup()` (checking
     `registry.get_n_seen(entity_id, detector) == 0` before touching the model) or inside a new
     `DetectorRegistry.warmup_one()` method that self-guards.
   - Recommendation: put the guard inside the registry method (mirrors `_get_or_create`'s existing
     "always creates lazily but the gate concern is registry-owned" pattern) so future call sites
     (e.g. a future gRPC-console debug tool) inherit the same safety automatically.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| `river` (Python) | Checkpoint pickle round-trip, `EntityDetector` | ✓ | 0.25.0 (confirmed installed) | — |
| `grpcio` (Python) | `Warmup` RPC, SIGTERM/`server.stop` | ✓ | 1.81.0 (confirmed installed) | — |
| `pytest` (Python) | Detector test suite | ✓ | confirmed runnable (`test_registry.py` — 20 passed) | — |
| `dotnet` / xunit | Orchestrator test suite | assumed ✓ (existing `Argus.Orchestrator.sln`/`.Tests` project build cleanly per project history) | — | — |
| InfluxDB 2.x instance | BACKFILL-01..04 live behavior; NOT required for the code to compile/test (D-15 mandates graceful degrade) | not probed this session (no live instance reachable from this research context) | — | D-15: unconfigured/unreachable Influx already degrades to normal live warm-up with a WARN — this is a designed fallback, not a gap |
| s6-overlay version / `S6_KILL_GRACETIME` actual value | PERSIST-03 (SIGTERM flush budget) | not confirmed — no `timeout-kill` file found under `argus/rootfs/etc/services.d/detector/` | unknown | Plan should add a verification task (grep repo for `S6_KILL_GRACETIME`/`timeout-kill`) rather than rely on this research's assumed default (see Assumption A1) |

**Missing dependencies with no fallback:** none — every genuinely required dependency is already
installed and pinned.

**Missing dependencies with fallback:** InfluxDB availability (D-15's designed degrade path); exact
s6 kill-grace value (mitigated by keeping the flush fast — Pitfall 1).

## Security Domain

`security_enforcement` is not disabled in `.planning/config.json` (absent = enabled per the workflow
default), so this section is included, scoped to what this phase actually touches.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-------------------|
| V5 Input Validation | yes | `_safeFluxString` regex guard (existing, `InfluxDbReader.cs:18-19`) must be applied identically to any new interpolated value in `QueryHistoryAsync` (lookback string, limit as parsed int not raw string) — D-13 explicitly calls this out as non-negotiable |
| V6 Cryptography | no change | mTLS channel setup is untouched by this phase — `Warmup` RPC rides the same existing `DetectorService` channel |
| V1 Architecture/Design | yes (informational) | `pickle.load()` on `checkpoint.pkl` carries the same accepted risk already documented for `load_river`/`load_pyod` (`model_store.py:229-231`, threat model note T-02-03-01: arbitrary code execution on load, accepted for this single-operator self-hosted deployment where `/data/models` is writable only by the detector process). The new checkpoint file is the same risk class, not a new one — no new mitigation is owed, but the plan's code comments should reference T-02-03-01 the same way `load_river` already does, for consistency. |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Flux string-literal injection via interpolated entity_id/bucket/measurement/field | Tampering | `_safeFluxString` regex (existing) — extend identically to the new `QueryHistoryAsync` lookback/limit parameters; never string-interpolate a raw `limit` — parse to `int` first |
| Deserializing an attacker-controlled `checkpoint.pkl` | Tampering/Elevation of Privilege | Not newly introduced by this phase — same accepted-risk class as existing `load_river`; the file is only ever written by the detector process itself onto a volume it exclusively controls (`ARGUS_MODEL_ROOT=/data/models`, add-on-private volume) |
| A malformed/truncated checkpoint blocking startup for other entities | Denial of Service | D-09's per-entity try/except in `load_all_into` (already the existing pattern for versioned model loads) — extend the exact same wrapping to the checkpoint glob pass |

## Sources

### Primary (HIGH confidence)
- Local measurement against this repo's `detector/argus_detector/hst_detector.py` + installed
  `river==0.25.0` — pickle round-trip correctness, pickle size (419291 bytes), `copy.deepcopy` latency
  (56-96ms) — `[VERIFIED: measured locally this session]`
- Local introspection of installed `grpcio==1.81.0`'s `grpc.Server.stop`/`wait_for_termination` signatures
  via `inspect.signature()` — `[VERIFIED: measured locally this session]`
- Direct reading of `detector/argus_detector/{hst_detector,registry,model_store,server,servicer,config}.py`,
  `proto/argus.proto`, `orchestrator/Argus.Orchestrator/Detection/*.cs`,
  `orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs`, `orchestrator/Argus.Orchestrator/Batch/*.cs`,
  `orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs`, `orchestrator/Argus.Orchestrator/Program.cs`,
  `argus/rootfs/etc/cont-init.d/10-config-gen.sh`, `argus/rootfs/etc/services.d/detector/run` — `[VERIFIED: read this session]`

### Secondary (MEDIUM confidence)
- Context7 `/online-ml/river` — official River docs confirming pickle as the supported model-persistence
  mechanism `[CITED: github.com/online-ml/river/blob/main/docs/recipes/saving-loading.ipynb,
  docs/faq/index.md]`
- Context7 `/websites/influxdata_influxdb_v2` — official InfluxDB v2 Flux docs on `sort()`/`limit()`/
  `first()`/`last()` ordering semantics `[CITED: docs.influxdata.com/influxdb/v2/query-data/flux/sort-limit,
  first-last]`

### Tertiary (LOW confidence)
- WebSearch: s6-overlay `S6_KILL_GRACETIME`/`timeout-kill`/`timeout-finish` semantics — not cross-checked
  against this repo's actual base image or s6-overlay version `[ASSUMED — see Assumption A1]`
- WebSearch: Python grpc SIGTERM/`server.stop(grace)` graceful-shutdown pattern — general community/
  grpc.io guidance, not specific to this repo `[CITED: grpc.io/docs/guides/server-graceful-stop/, but
  treat the exact timing numbers as unverified]`

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new dependencies; every symbol verified either by reading source or by
  running code against the actually-installed pinned versions.
- Architecture: HIGH for the Python detector side (all read directly from source, patterns tested against
  existing conventions); MEDIUM for the exact `Warmup` RPC wire shape (Claude's Discretion per CONTEXT.md,
  recommendation given but not locked).
- Pitfalls: HIGH for Pitfalls 1-4 (all derived from direct source reading + measurement); LOW for Pitfall 5
  (s6 kill-grace specifics — flagged explicitly as unverified, with a concrete verification task
  recommended for the plan).

**Research date:** 2026-08-03
**Valid until:** 30 days (stable stack, pinned versions, no fast-moving dependencies in scope)
