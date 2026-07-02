# Architecture Research

**Domain:** Group & Multivariate Anomaly Detection — v4.0 extension of existing .NET 8 orchestrator + Python gRPC detector
**Researched:** 2026-07-02
**Confidence:** HIGH (proto, EntitiesConfig, ModelStore, DetectorRegistry, BatchSchedulerWorker, DiscoveryPublisher, servicer.py all read from source; PyOD multivariate API confirmed via docs)

---

## Scope

This file covers ONLY what changes to add group/multivariate detection on top of the shipped v1–v3 pipeline. Streaming single-sensor detection, MQTT discovery for single entities, Ingress config UI mechanics, and HA add-on packaging are unchanged and out of scope here except where group support attaches to them.

Two detection modes, both batch-first:
- **Peer-divergence:** group of N same-kind sensors (e.g. 4 tire pressures) — flag which member diverges from the group's collective behavior.
- **Joint multivariate:** group of N different-kind sensors (e.g. room temp+humidity+pressure) — flag the joint value vector as abnormal, no single "diverging member" attribution.

---

## Baseline (v1–v3, Shipped)

```
proto/argus.proto  (univariate only)
    Point(entity_id, value, timestamp)
    Verdict(entity_id, score, expected, lower, upper, is_anomaly, detector, timestamp)
    DetectorService: ScoreStream(stream Point)->stream Verdict, Fit, ScoreBatch, SaveModel, LoadModel

.NET Orchestrator
    EntitiesConfig.Entities: List<EntityConfig>   { EntityId, FriendlyName, Detectors, Covariates(unused), Groups(unused) }
    BatchSchedulerWorker  → per-entity loop → InfluxDbReader.QueryAsync(entityId) → ScoreBatchRequest(1 entity) → publish last verdict
    DiscoveryPublisher    → per-entity → 1 binary_sensor + 1 sensor, keyed by UniqueId.AnomalyId/ScoreId(entityId, detector)

Python Detector
    DetectorRegistry: dict[(entity_id, detector) -> EntityDetector|PyODDetector|StlDetector]  — all univariate
    PyODDetector wraps pyod.models.mad.MAD; hard-reshapes to (n,1) — univariate by construction
    ModelStore: models/{entity_slug}/{detector}/v{N}/model.{joblib,pkl} — keyed by (entity_slug, detector)
```

**Critical facts confirmed in source:**

1. `EntityConfig.Groups` and `.Covariates` are typed `object?` — parsed by YamlDotNet but never read anywhere except `EntitiesConfigLoader.WarnIgnoredKeys`, which only logs a warning. No consumer exists. Deserialization shape is not yet locked to a real schema — this milestone gets to define it.
2. `PyODDetector.fit()`/`score_batch()` hard-code `.reshape(-1, 1)` — this is a **univariate-only** wrapper around a library (PyOD) whose underlying models (MAD included) are 1-D detectors by design. MAD itself is NOT a multivariate algorithm — a *different* PyOD model is needed for joint multivariate (see ADR-2).
3. `DetectorRegistry._create_detector` keys purely on detector name string (`"mad"`, `"stl"`, `"hst"`) — no notion of "how many series" a detector consumes. This factory is the natural extension point for a `group_detector` family.
4. `ModelStore` paths and `DetectorRegistry` dict keys are both `(entity_slug_or_id, detector)` tuples — a 2-tuple. Adding a group axis requires either promoting the key to a 3-tuple `(subject_id, detector, kind)` or treating `group:{group_id}` as the "entity_id" string (see ADR-3 — the latter is simpler and requires zero schema migration).
5. `BatchSchedulerWorker.RunBatchAsync` iterates `entity.Detectors` — one InfluxDB query, one `ScoreBatchRequest`, one verdict publish, **per single entity**. There is no concept of "these N entities are queried together, aligned on a shared time grid, and scored as one unit." This loop is the direct extension point for group batch scoring (see ADR-4).
6. `DiscoveryPublisher.PublishAllAsync` iterates entities 1:1 to MQTT discovery configs; `UniqueId.AnomalyId(entityId, detector)` is the sole key generator for `unique_id`. Group output needs a parallel key scheme, not a reuse of the per-entity one (a group has no single `entity_id`).
7. `InfluxDbReader.QueryAsync(entityId, ct)` returns `IReadOnlyList<(DateTime, double)>` — irregular/unaligned per-entity samples (HA writes on `state_changed`, not on a fixed grid). Two sensors in a group will have samples at different, unaligned timestamps. Nothing in the current pipeline resamples or aligns timestamps across entities — this must be built new.
8. Confirmed via PyOD docs: `PCA`, `ECOD`, `COPOD`, `HBOS`, `IForest`, `KNN`, `LOF` all accept `X` of shape `(n_samples, n_features)` **natively** and return per-sample scores via `decision_function(X) -> shape (n_samples,)`. This means one call scores an entire aligned feature matrix at once — no group-specific PyOD wrapper logic is needed beyond generalizing `PyODDetector`'s `.reshape(-1, 1)` to accept `n_features > 1`.

---

## v4.0 Target Architecture

```
proto/argus.proto  (extended — univariate messages untouched, new multivariate messages added)
    Point, Verdict, ScoreBatchRequest/Response, FitRequest, Save/LoadModel  ← UNCHANGED, wire-compatible
    NEW:
      SeriesPoint { string member_id; double value; }             // one column at one timestamp
      GroupRow    { Timestamp timestamp; repeated SeriesPoint members; }   // one aligned row
      GroupVerdict{ string group_id; Timestamp timestamp;
                    double group_score; bool is_anomaly;
                    string diverging_member;      // "" for joint-multivariate mode
                    map<string,double> member_scores;  // per-member contribution (peer mode)
                    string detector; }
      GroupScoreBatchRequest  { string group_id; string mode; string detector;
                                 map<string,string> params; repeated GroupRow rows; }
      GroupScoreBatchResponse { repeated GroupVerdict verdicts; bool ok; string error; }
      GroupFitRequest / GroupFitResponse   (mirrors FitRequest/FitResponse, group-shaped)
    DetectorService (same service, new RPCs added — NOT a new service):
      rpc GroupScoreBatch(GroupScoreBatchRequest) returns (GroupScoreBatchResponse);
      rpc GroupFit(GroupFitRequest) returns (GroupFitResponse);
      [existing 5 RPCs unchanged]

.NET Orchestrator
    EntitiesConfig
        Entities: List<EntityConfig>          ← unchanged, still drives per-sensor detection
        Groups:   List<GroupConfig>           ← NEW top-level list (sibling of Entities, not nested in EntityConfig)
            GroupConfig { GroupId, FriendlyName, Mode(peer|joint), Members: List<string> (entity_ids),
                          Detector, Params, ResampleIntervalSeconds }
    (EntityConfig.Groups/.Covariates placeholders: DEPRECATED/removed — see ADR-6)

    NEW: GroupBatchSchedulerWorker (parallel to BatchSchedulerWorker, own timer)
        for each GroupConfig:
            query InfluxDB for ALL member entity_ids over lookback window
            resample/align to common time grid (THIS LIVES IN .NET — see ADR-1)
            build GroupScoreBatchRequest (one GroupRow per aligned timestamp)
            call GroupScoreBatch RPC
            publish verdicts → group binary_sensor (+ per-member attribute or per-member sensors)

    NEW: GroupDiscoveryPublisher (parallel to DiscoveryPublisher)
        1 binary_sensor per group (+ 1 score sensor); peer mode adds a "diverging_member" attribute

Python Detector
    DetectorRegistry  ← unchanged for single-entity path
    NEW: GroupDetectorRegistry  keyed by (group_id, detector) — mirrors DetectorRegistry's lock pattern
        _create_group_detector(detector, mode) factory:
            mode == "joint"  → MultivariatePyODDetector (PCA/ECOD/HBOS — n_features > 1, no reshape(-1,1))
            mode == "peer"   → PeerDivergenceDetector (per-member z-score against group consensus, e.g.
                                leave-one-out mean/median of the row; NOT literally PyOD — thin custom logic)
    ModelStore  ← key scheme reused as-is: pass group_id as the "entity_slug" positional arg (ADR-3)
        models/{group_id}/{detector}/v{N}/model.joblib
```

---

## Architectural Decision Records

### ADR-1: Resampling/Time-Alignment Lives in .NET, Not Python

**Decision:** The orchestrator (.NET) queries InfluxDB per group member and resamples/aligns to a common time grid BEFORE sending data over gRPC. The Python detector receives only pre-aligned `GroupRow` matrices — it does zero timestamp reasoning.

**Reasoning:**
- InfluxDB's Flux query language has native `aggregateWindow()` / `pivot()` for exactly this resample-and-align operation, and `InfluxDbReader` already owns all Flux query construction (with the existing injection-safety guard `_safeFluxString`). Extending the existing Flux builder to emit an aligned multi-column table is a natural fit for code that already lives there.
- The gRPC proto boundary should carry "already-a-valid-input-matrix" data, not raw irregular streams needing further processing on the other side. This mirrors the existing `ScoreBatchRequest` contract: the orchestrator always does the querying and shaping; the detector always just fits/scores what it's given. Keeping the boundary invariant (.NET queries+shapes, Python models) avoids introducing a second responsibility split partway through the group feature.
- Python already has zero InfluxDB client dependency (`influxdb-client` per STACK.md is .NET-side or absent) — pushing resampling to Python would require adding a new dependency and duplicating query logic that .NET already has authenticated access to.
- Keeps the Python side pure numerics (PyOD in, scores out) — easier to test with plain arrays, no time-series alignment edge cases (missing member, late sensor, clock skew) leaking into ML code.

**How alignment works concretely:**
1. Query each member's raw points via existing `InfluxDbReader.QueryAsync` pattern, OR (better) a single Flux query using `pivot(rowKey: ["_time"], columnKey: ["entity_id"], valueColumn: "_value")` after `aggregateWindow(every: <group.resample_interval>, fn: mean)` — this produces one InfluxDB table with one column per member, already aligned, in one round trip.
2. Rows with any `null` member value (a sensor didn't report in that window) are either dropped (simplest, recommended for batch-first v4.0) or forward-filled — dropping is simpler and avoids the "last-value-carried-forward" complexity explicitly deferred for streaming (see "Deferred" section below). For batch mode, dropping incomplete rows is acceptable: batch runs over a lookback window with many samples, losing a few unaligned edges is not user-visible.
3. Resulting aligned table → `GroupRow` list → `GroupScoreBatchRequest.rows`.

**Rejected alternative:** Send raw unaligned points per member over gRPC in a new `GroupFitRequest`/`GroupScoreBatchRequest` shape, and let Python resample using pandas `resample()`/`asfreq()`. Rejected because it duplicates Flux's native resample capability in a second language, adds a pandas dependency to the detector purely for this, and moves a data-correctness concern (which InfluxDB is authoritative for) into the ML process. It also breaks the existing "orchestrator prepares complete data" contract mid-migration.

### ADR-2: Two Distinct Detector Families Behind One New RPC, Not Two RPCs

**Decision:** A single `GroupScoreBatch` RPC serves both peer-divergence and joint-multivariate modes; `mode` is a request field (`"peer"` | `"joint"`), and `GroupDetectorRegistry._create_group_detector` branches on it. Do not add `PeerScoreBatch` and `JointScoreBatch` as separate RPCs.

**Reasoning:**
- Both modes share the identical request shape: an aligned `GroupRow` matrix over a group's members. The only difference is what happens inside the detector (per-member leave-one-out comparison vs. a single joint decision_function call). This is exactly the existing `_create_detector(detector: str)` factory pattern in `DetectorRegistry` — one factory, branching on a string, already established as the project's idiom. `mode` is just another such branch key, alongside `detector`.
- `GroupVerdict` already carries fields for both cases: `diverging_member` (populated only in peer mode, empty string in joint mode) and `member_scores` (populated in peer mode as per-member deviation, and optionally in joint mode as a debugging/attribution aid even though joint mode has no single culprit by design — e.g. via SHAP-like feature contribution, which is explicitly out of scope for v4.0 but the field doesn't block adding it later).
- One RPC keeps the .NET orchestrator's calling code (`GroupBatchSchedulerWorker`) uniform: build the request the same way regardless of mode, read `.Mode` from `GroupConfig`, and pass it through. Two RPCs would mean two near-identical client call paths and two response-handling branches in .NET for no benefit.

**Detector algorithm choice per mode:**
| Mode | Algorithm | PyOD/library | Why |
|------|-----------|---------------|-----|
| Joint multivariate | PCA, ECOD, or HBOS (pick one as default; expose others behind the "algorithm chooser" UX-track work) | PyOD (native `(n, d)` input, BSD-2 license, already a dependency) | Confirmed via PyOD docs: `decision_function(X)` on `(n_samples, n_features)` requires zero custom code beyond generalizing `PyODDetector`'s reshape. No new dependency. |
| Peer divergence | Custom leave-one-out z-score/MAD per member per row (`abs(member_value - median(other_members)) / MAD(other_members)`) | Hand-rolled (numpy only) | No off-the-shelf PyOD model directly answers "which of these N correlated series is the odd one out at time t" — that is inherently a per-member relative computation, not a whole-vector anomaly score. This is intentionally simple statistics, not a new heavy dependency. |

**Rejected alternative:** Force peer-divergence through a PyOD multivariate model too (e.g. flag the row as anomalous, then post-hoc attribute to "whichever member has the highest per-feature reconstruction error" using PCA reconstruction error per column). Rejected for v1 of this feature because it's more complex than the direct leave-one-out approach and PCA reconstruction-error attribution is a well-known but noisier heuristic — the direct approach is simpler and matches the concrete driving example (tire pressure) better. Can be revisited as an "advanced" peer algorithm later without proto changes (it's still `mode="peer"`, just a different `detector` value).

### ADR-3: Group Keying Reuses Existing 2-Tuple Pattern — `group_id` Substitutes for `entity_id`

**Decision:** `GroupDetectorRegistry` and `ModelStore` group calls use the exact same `(subject_id, detector)` key shape as the existing single-entity path, where `subject_id = group_id` (a user-assigned string, e.g. `"tire_pressures"`). No new key dimension, no schema change to `ModelStore`.

**Reasoning:**
- `ModelStore.save_pyod(entity_slug, detector, version, model, entity_id=...)` and `DetectorRegistry`'s `dict[(entity_id, detector) -> ...]` are generic over what string is passed as the first component — nothing in either component's code assumes the string is a dotted HA `entity_id`. Passing a `group_id` string works with zero code change to `ModelStore`.
- Directory layout becomes `models/{group_id}/{detector}/v{N}/model.joblib` — trivially distinguishable from single-entity models by directory name convention (group IDs are operator-chosen, e.g. `"tire_pressures"`, vs. HA entity slugs like `"sensor_salon_temp"`), but no code needs to distinguish them structurally; they simply never collide because `group_id` and `entity_id` occupy the same string-keyed namespace with no persisted routing metadata needed.
- Avoids inventing a 3-tuple `(kind, subject_id, detector)` scheme that would require touching `ModelStore`'s public API, `_model_dir`, `_write_version_json`, `load_all_into` (which globs `*/*/latest`) — all of which currently assume a 2-level directory structure keyed only by 2 strings. `load_all_into`'s glob pattern `*/*/latest` naturally also picks up group models with zero change, since it doesn't care what the first-level directory name means.

**One collision risk to flag for planning:** if an operator names a group with the same string as an existing `entity_id` (unlikely but possible, e.g. group_id `"sensor.outdoor_temp"`), `ModelStore` would silently conflate them. Mitigation: validate at config-load time (`EntitiesConfigLoader` / new `GroupsConfigLoader`) that `group_id` values are disjoint from all `entity_id` values, and recommend `group_id` naming convention without dots (e.g. `tire_pressures` not `group.tire_pressures`) to make the visual distinction obvious. This is a cheap validation check, not an architecture change.

**Rejected alternative:** Prefix group_id with a fixed namespace internally (e.g. always store as `f"group__{group_id}"`) transparently inside `GroupDetectorRegistry`/group-facing `ModelStore` calls. This is simpler still and removes the collision risk entirely with one line of code (`slug = f"group__{group_id}"`) — **recommend doing this** as a refinement of ADR-3, since it costs nothing and removes the validation burden above. Flag this specific refinement for the phase that implements `GroupDetectorRegistry`.

### ADR-4: Group Batch Pipeline Is a New Parallel Worker, Not a Branch Inside `BatchSchedulerWorker`

**Decision:** Introduce `GroupBatchSchedulerWorker` as a new `BackgroundService`, structurally parallel to `BatchSchedulerWorker`, with its own timer and its own `RunGroupBatchAsync` loop. Do not add an `if (isGroup)` branch inside the existing `BatchSchedulerWorker.RunBatchAsync`.

**Reasoning:**
- `BatchSchedulerWorker.RunBatchAsync` iterates `entity.Detectors` and calls `_influxReader.QueryAsync(entityId, ct)` — a single-entity-shaped query. Group batch needs a *different* query shape (multi-member, aligned, pivoted) and a *different* request builder (`GroupScoreBatchRequest` vs `ScoreBatchRequest`). Shoehorning both into one loop with type-branches produces exactly the kind of dual-purpose method the codebase currently avoids (compare: `BatchSchedulerWorker` and `InfluxDbReader` are already single-purpose, single-entity-shaped classes).
- A separate worker means a separate, independently-tunable timer interval — directly serves the "group latency target separate from single-sensor" requirement (see ADR-5). If group batches (which must wait for N members' data + alignment) run on the same `PeriodicTimer` as single-entity batches, either single-entity batches slow down waiting for group work, or the group work is forced onto the same short interval even though group alignment is inherently slower. Separating the workers means each can have its own interval config (existing `BatchIntervalMinutes` for entities; a new `GroupBatchIntervalMinutes` for groups, likely coarser).
- Fault isolation follows the existing pattern (`BatchSchedulerWorker` already isolates per-entity exceptions so one bad entity doesn't kill the batch tick) — a separate worker isolates group failures from entity failures at the process level too: if group logic has a bug, single-entity streaming/batch keeps working uninterrupted, and vice versa. This matches `PROJECT.md`'s existing "graceful degradation" pattern (D-workers fail independently already, per HaListenerWorker/BatchSchedulerWorker/MqttPublisherWorker/HealthPublisherWorker all being independent BackgroundServices today).
- `IBatchDetectorClient` (existing interface for `ScoreBatchAsync`/`FitAsync`) gets a sibling `IGroupDetectorClient` (or is extended with `GroupScoreBatchAsync`/`GroupFitAsync` methods) — either works; extending the existing interface is slightly less new surface area and keeps one gRPC client wrapper, which is preferred since `DetectionGateway` already holds one shared channel and one set of stub clients (`DetectorServiceClient`) — the new RPCs live on the same generated client class automatically because they're added to the same `DetectorService` (ADR-2's "one RPC, one service" choice reinforces this: no second gRPC client/channel needed).

**Rejected alternative:** Extend `BatchSchedulerWorker` with an internal `IsGroup` flag path. Rejected because it couples two independently-evolving concerns (single-entity batch cadence/query shape vs. group batch cadence/query shape) into one class and one timer, working against the explicit v4.0 requirement that group latency is a separate, looser target.

### ADR-5: Group Latency Is a Separate, Explicitly Looser Target — Not Wired Into the <2s Core Value

**Decision:** The existing Core Value "<2s" latency target applies only to single-sensor streaming (`ScoreStreamPipeline` via `HaListenerWorker`), unchanged. Group detection gets its own target, expressed in terms of the (new, separate) `GroupBatchIntervalMinutes` scheduling cadence — e.g. "a group verdict is available within one batch cycle (default 10-15 min) of all members having reported," not a sub-2-second guarantee.

**Reasoning:**
- Group detection is batch-first by explicit milestone decision (`PROJECT.md`: "Batch-first (InfluxDB resampling for time-alignment...); streaming groups later"). Batch-first inherently means the fastest a group verdict can appear is bounded by the batch tick interval, not by event latency. Applying a <2s target to a batch pipeline is a category error — it would force reducing `GroupBatchIntervalMinutes` to near-zero, defeating the purpose of batch mode (efficient periodic InfluxDB scans, not per-event RPCs).
- Practically, group alignment requires waiting for the slowest-reporting member in a window before a row is complete (ADR-1) — an inherent floor beneath which no amount of engineering effort in .NET or Python moves the needle; the floor is set by upstream HA sensor reporting cadence for the slowest member, not by Argus's own pipeline.
- This target separation is explicitly named as a milestone decision already in PROJECT.md ("Group latency: the Core Value '< 2 s' target is single-sensor only; group detection needs a separate, looser latency target"). This ADR operationalizes that decision architecturally: it is satisfied structurally by ADR-4 (separate worker, separate timer) rather than needing new latency-measurement infrastructure.

**Recommendation for the roadmap:** State the group latency target explicitly as a phase acceptance criterion, e.g. "A group verdict reflecting a change in member data appears in HA within 1 batch cycle + gRPC round-trip (target: under `GroupBatchIntervalMinutes` + 5 seconds)." Do not attempt to define a numeric SLA tighter than the batch interval itself.

### ADR-6: `EntityConfig.Groups`/`.Covariates` Placeholders Are Retired, Not Populated — Groups Become a Top-Level Config List

**Decision:** Do NOT give the existing `EntityConfig.Groups` / `EntityConfig.Covariates` (`object?`) fields a real type and start using them. Instead, add a new top-level `EntitiesConfig.Groups: List<GroupConfig>` list, sibling to `Entities`, and delete (or leave permanently null/deprecated with a loader warning) the old per-entity placeholders.

**Reasoning:**
- The placeholders were shaped as **per-entity** fields (`EntityConfig.Groups` — "this entity belongs to these groups," an inverted/embedded membership model). But the natural authoring and query pattern for groups is **group-centric**: an operator defines a group once (`{group_id: "tire_pressures", members: [4 entity_ids], mode: "peer", detector: "mad"}`) rather than annotating each of 4 entities with "I'm in group tire_pressures" and hoping the group's shared settings (mode, detector, resample interval) are kept consistent across all 4 annotations. A top-level `GroupConfig` list is the single source of truth for a group's settings; per-entity back-references would require the loader to cross-validate consistency across N places whenever a group's detector or mode changes — pure duplication risk with no upside.
- `GroupBatchSchedulerWorker` (ADR-4) needs to iterate "all configured groups," not "all entities, checking whether any carry a Groups annotation, then reconstructing implied group membership by inverting the annotation." Top-level `Groups: List<GroupConfig>` is exactly the shape the new worker wants to consume directly — zero inversion logic needed.
- `Covariates` (as opposed to `Groups`) was seemingly intended for something related but distinct — likely "these other entities are contextual inputs to this entity's OWN detector, without being a symmetric peer group" (e.g. outdoor temp as a covariate for indoor temp's expected value). Nothing in v4.0's scope (peer-divergence + joint-multivariate over symmetric member lists) requires this asymmetric-covariate concept. Recommend explicitly marking `Covariates` as still-deferred (not this milestone), separate from `Groups` which is fully retired in favor of the new top-level list.
- `EntitiesConfigLoader.WarnIgnoredKeys` currently fires this exact warning today: `"covariates/groups ignored in phase 1 for {EntityId} — these keys are parsed but not used until Phase 2"`. This milestone IS "Phase 2" from that comment's perspective — but the resolution is "the field was retired in favor of a better shape," not "the field now works as originally stubbed." The roadmap should include a plan step to update this warning message (and ideally remove the dead `EntityConfig.Groups`/`.Covariates` properties from the C# model, or at minimum stop warning about `Groups` since it's superseded) to avoid the misleading log line persisting once real groups exist elsewhere in the config.

**Migration/compat note:** Because `EntityConfig.Groups`/`.Covariates` were NEVER functionally used (parsed-and-warned only, per D-09), there is no real data migration concern — no operator has ever put meaningful data there that needs preserving. This is a pure "unused stub → real feature, different shape" situation, not a breaking change to any working behavior.

**Rejected alternative:** Type `EntityConfig.Groups` as `List<string>` (group IDs this entity belongs to) and derive `GroupConfig` objects by inverting this at load time, keeping group-level settings (mode, detector) in a second lookup table keyed by group_id. Rejected because it still requires a second group-centric config surface for settings, so the per-entity list adds a layer of indirection (entity→group_id→group settings) without removing the need for the group-centric table — strictly more moving parts than just making `Groups` the top-level, group-centric list directly.

### ADR-7: Proto Backward Compatibility — Additive Only, Existing Univariate Path Untouched

**Decision:** All new group/multivariate messages and RPCs are added to `argus.proto` without modifying any existing message field, field number, or RPC signature. `Point`, `Verdict`, `ScoreBatchRequest/Response`, `FitRequest/Response`, `Save/LoadModelRequest/Response`, and the existing 5 RPCs on `DetectorService` are byte-for-byte unchanged.

**Reasoning:**
- Protobuf's wire compatibility rules mean adding new message types and new RPCs to an existing service is always safe — old clients/servers that don't know about the new messages/RPCs simply never construct or call them; no existing serialization changes. This is the standard "grow, never mutate" proto evolution pattern and requires no special handling beyond "don't touch existing field numbers."
- Both orchestrator and detector are deployed together as one versioned add-on image (per `PROJECT.md`: "Local buildx→GHCR release... version==image tag") — there is no independent-deployment/rolling-upgrade scenario where an old detector talks to a new orchestrator or vice versa within this project's actual deployment model. Strict wire compatibility is still the right default (it's free and it's the correct engineering practice), but it is a safety margin here, not a hard operational requirement driven by mixed-version fleets.
- Keeping the univariate `DetectorService.ScoreStream`/`Fit`/`ScoreBatch`/`SaveModel`/`LoadModel` RPCs completely untouched means `HaListenerWorker`, `ScoreStreamPipeline`, `BatchSchedulerWorker`, `DetectorRegistry`, `ModelStore`'s single-entity paths, and all their existing tests require zero changes for this milestone's group feature to land. This directly satisfies "keep backward compat with univariate detectors" as a hard constraint, and it's the natural consequence of an additive-only proto change plus ADR-4's "new parallel worker" choice.

**Concrete proto sketch** (for the phase that implements this — not final field numbers, illustrative shape only):

```protobuf
// Additive to existing argus.proto — existing messages/RPCs unchanged above this line.

message SeriesPoint {
  string member_id = 1;      // entity_id of the group member this value came from
  google.protobuf.DoubleValue value = 2;
}

message GroupRow {
  google.protobuf.Timestamp timestamp = 1;   // aligned timestamp (post-resample)
  repeated SeriesPoint members = 2;           // one entry per group member at this row
}

message GroupVerdict {
  string group_id = 1;
  google.protobuf.Timestamp timestamp = 2;
  google.protobuf.DoubleValue group_score = 3;
  bool is_anomaly = 4;
  string diverging_member = 5;               // "" when mode == "joint"
  map<string, double> member_scores = 6;     // per-member score/attribution (peer mode; optional in joint)
  string detector = 7;
}

message GroupScoreBatchRequest {
  string group_id = 1;
  string mode = 2;               // "peer" | "joint"
  string detector = 3;
  map<string, string> params = 4;
  repeated GroupRow rows = 5;
}

message GroupScoreBatchResponse {
  repeated GroupVerdict verdicts = 1;
  bool ok = 2;
  string error = 3;
}

message GroupFitRequest {
  string group_id = 1;
  string mode = 2;
  string detector = 3;
  map<string, string> params = 4;
  repeated GroupRow rows = 5;      // training window, same shape as scoring
}

message GroupFitResponse {
  bool ok = 1;
  string error = 2;
}

service DetectorService {
  // ... existing 5 RPCs, UNCHANGED ...
  rpc GroupScoreBatch(GroupScoreBatchRequest) returns (GroupScoreBatchResponse);
  rpc GroupFit(GroupFitRequest) returns (GroupFitResponse);
}
```

Note: `SaveModel`/`LoadModel` are reused as-is for groups (ADR-3 — `group_id` passed in the existing `entity_id` field of those messages); no `GroupSaveModel`/`GroupLoadModel` RPCs are needed since the persistence contract doesn't care what kind of subject the string names.

---

## Component Inventory

### New Components

| Component | Layer | Path (suggested) | Purpose |
|-----------|-------|-------------------|---------|
| Group/multivariate proto messages + 2 RPCs | proto | `proto/argus.proto` (additive) | `SeriesPoint`, `GroupRow`, `GroupVerdict`, `GroupScoreBatchRequest/Response`, `GroupFitRequest/Response`; `GroupScoreBatch`/`GroupFit` RPCs on existing `DetectorService` |
| `GroupConfig` | .NET config | `Config/EntitiesConfig.cs` (new class, same file or sibling) | `{ GroupId, FriendlyName, Mode, Members: List<string>, Detector, Params, ResampleIntervalSeconds }` |
| `EntitiesConfig.Groups` | .NET config | `Config/EntitiesConfig.cs` | New top-level `List<GroupConfig>`, sibling to `Entities` |
| Group config validation | .NET config | `Config/EntitiesConfigLoader.cs` (extend) | Validate `group_id` non-empty, `members` non-empty and reference known `entity_id`s, `mode` in {peer,joint}; disjoint namespace check or `group__` prefix (ADR-3 refinement) |
| `GroupBatchSchedulerWorker` | .NET worker | `Batch/GroupBatchSchedulerWorker.cs` | New `BackgroundService`; own `PeriodicTimer` (`GroupBatchIntervalMinutes`); queries+aligns+scores+publishes per group |
| Group-aware Influx query | .NET batch | `Batch/InfluxDbReader.cs` (extend) or new `GroupInfluxDbReader` | Flux `pivot()` + `aggregateWindow()` query producing one aligned multi-column table per group |
| `IGroupDetectorClient` (or extend `IBatchDetectorClient`) | .NET detection | `Detection/` | `GroupScoreBatchAsync`, `GroupFitAsync` wrapping the new gRPC stubs on the existing `DetectionGateway.DetectorClient` |
| `GroupDiscoveryPublisher` | .NET MQTT | `Mqtt/GroupDiscoveryPublisher.cs` | Builds/publishes 1 binary_sensor (+1 score sensor) per group; `diverging_member` and `member_scores` surfaced as HA entity attributes (peer mode) |
| Group `UniqueId` scheme | .NET MQTT | `Mqtt/UniqueId.cs` (extend) | `GroupAnomalyId(groupId, detector)` / `GroupScoreId(groupId, detector)` — parallel to existing entity-keyed methods |
| `GroupDetectorRegistry` | Python detector | `argus_detector/group_registry.py` | Mirrors `DetectorRegistry`'s lock pattern; keyed `(group_id, detector)`; `_create_group_detector(detector, mode)` factory |
| `MultivariatePyODDetector` | Python detector | `argus_detector/multivariate_pyod_detector.py` | Generalizes `PyODDetector` to `n_features > 1` (no forced `.reshape(-1,1)`); wraps PCA/ECOD/HBOS |
| `PeerDivergenceDetector` | Python detector | `argus_detector/peer_divergence_detector.py` | Leave-one-out per-member z-score/MAD against group consensus per row; returns per-member scores + argmax as `diverging_member` |
| `GroupScoreBatch`/`GroupFit` servicer methods | Python detector | `argus_detector/servicer.py` (extend) | New RPC handlers, structurally parallel to existing `ScoreBatch`/`Fit`; delegate to `GroupDetectorRegistry` |
| Group model persistence calls | Python detector | `argus_detector/model_store.py` (reused, not modified) | `ModelStore.save_pyod/load_pyod` called with `group_id` (or `f"group__{group_id}"`) as the `entity_slug` argument — no code change to `ModelStore` itself |

### Modified Components

| Component | File | Change |
|-----------|------|--------|
| `argus.proto` | `proto/argus.proto` | Additive: new messages + 2 new RPCs on `DetectorService`. Existing messages/RPCs byte-identical. |
| `EntitiesConfig` | `orchestrator/.../Config/EntitiesConfig.cs` | Add `Groups: List<GroupConfig>` at top level; add `GroupConfig` class; deprecate/remove `EntityConfig.Groups`/`.Covariates` object stubs (ADR-6) |
| `EntitiesConfigLoader` | `orchestrator/.../Config/EntitiesConfigLoader.cs` | Add group validation pass; update/remove the now-stale `WarnIgnoredKeys` message about `covariates/groups` |
| `Program.cs` | `orchestrator/.../Program.cs` | Register `GroupBatchSchedulerWorker` as a hosted service; register `GroupDiscoveryPublisher`/group client wrapper in DI |
| `DetectionGateway` | `orchestrator/.../Detection/DetectionGateway.cs` | No structural change — same channel/client already exposes the new RPCs once proto regenerates; may need a typed accessor if `IGroupDetectorClient` wraps it |
| `DetectorRegistry` | `detector/argus_detector/registry.py` | No change — remains the single-entity registry; `GroupDetectorRegistry` is a separate class, not a refactor of this one (keeps existing tests untouched) |
| `PyODDetector` | `detector/argus_detector/pyod_detector.py` | No change to this class — `MultivariatePyODDetector` is a new sibling class, not a modification of the univariate one (preserves ADR-7's "don't touch the univariate path") |
| `servicer.py` | `detector/argus_detector/servicer.py` | Add `GroupScoreBatch`/`GroupFit` methods to the same `DetectorServicer` class implementing the extended service |
| `argus/rootfs/.../gen-entities.py` | `argus/rootfs/usr/local/bin/gen-entities.py` | Extend to also emit `groups:` from `options.json` if a groups-authoring path is added to add-on options (or: groups are UI-only for v4.0, config-gen stays entity-only — a roadmap/UX decision, not an architecture one) |
| `config.yaml` (add-on options schema) | `argus/config.yaml` | If groups are configurable via Supervisor options (unlikely given the "modern SPA" UI direction) vs. Ingress-UI-only — likely UI-only, no options.json schema change needed |

### Unchanged Components

`Point`, `Verdict`, `ScoreBatchRequest/Response`, `FitRequest/Response`, `SaveModelRequest/Response`, `LoadModelRequest/Response` (proto messages); `ScoreStream`/`Fit`/`ScoreBatch`/`SaveModel`/`LoadModel` (RPCs); `HaListenerWorker`, `NetDaemonHaEventSource`, `ScoreStreamPipeline` (streaming path — groups are batch-first only per milestone scope); `BatchSchedulerWorker` (single-entity batch, unchanged, runs alongside the new group worker); `InfluxDbReader`'s existing single-entity `QueryAsync` method (group query is additive, not a replacement); `DetectorRegistry`, `PyODDetector`, `StlDetector`, `EntityDetector` (single-entity detector path); `ModelStore` (reused as-is, zero code change, per ADR-3); `DiscoveryPublisher` (single-entity MQTT discovery, unchanged; `GroupDiscoveryPublisher` is a new sibling class); `HealthPublisherWorker`, `MqttConnection`, `MqttPublisherWorker`; `DetectionGateway`'s health-gate logic (`WaitForHealthyAsync` — one health check still gates both single-entity and group workers, since they share one detector process).

---

## Data Flows

### Group Batch Cycle (New — Peer or Joint Mode)

```
GroupBatchSchedulerWorker.ExecuteAsync   (own PeriodicTimer, GroupBatchIntervalMinutes)
    ↓ per tick
for each GroupConfig in ILiveEntitiesConfig.Get().Groups:
    ↓
InfluxDB query (Flux: aggregateWindow + pivot on group.Members' entity_ids)
    → one table: columns = [_time, member1_value, member2_value, ..., memberN_value]
    → rows with any null member dropped (batch-mode simplification, ADR-1)
    ↓
Build GroupRow list (one row per aligned timestamp, one SeriesPoint per member)
    ↓
GroupScoreBatchRequest { group_id, mode, detector, params, rows }
    ↓ gRPC (existing DetectionGateway channel — no new channel/mTLS setup)
GroupDetectorRegistry._get_or_create(group_id, detector, mode, params)
    mode=="joint" → MultivariatePyODDetector.score_batch(rows)  — one decision_function() call, all members as features
    mode=="peer"  → PeerDivergenceDetector.score_batch(rows)   — per-row leave-one-out per member
    ↓
GroupScoreBatchResponse { verdicts: [GroupVerdict, ...] }
    ↓
GroupBatchSchedulerWorker reads last verdict:
    group_score, is_anomaly, diverging_member (peer) / "" (joint), member_scores map
    ↓
GroupDiscoveryPublisher / IStatePublisher (extended)
    publish group binary_sensor state (ON/OFF)
    publish group score sensor state
    publish diverging_member + member_scores as MQTT attributes (peer mode) — HA JSON attributes topic
    ↓
HA shows: 1 binary_sensor "Tire pressures — anomaly" (attribute: diverging_member="sensor.tire_fl")
          1 sensor "Tire pressures — anomaly score"
```

### Group Model Training (New — Nightly Fit Analogue)

```
GroupBatchSchedulerWorker (or a shared nightly-fit hook, mirroring BatchSchedulerWorker.RunNightlyFitAsync)
    ↓ once/day, per GroupConfig
Same Influx query + alignment as batch cycle, but over the training lookback window
    ↓
GroupFitRequest { group_id, mode, detector, params, rows }
    ↓ gRPC
GroupDetectorRegistry.fit_group(group_id, detector, mode, rows)
    train-outside-lock pattern reused from DetectorRegistry.fit_one (MDL-04 precedent)
    ↓
ModelStore.save_pyod(group_id, detector, version, model)   ← group_id passed where entity_slug normally goes
    (or f"group__{group_id}" per ADR-3 refinement)
```

### Config Load (Extended)

```
EntitiesConfigLoader.Load(path, logger)
    deserialize entities.yaml → EntitiesConfig { Entities: [...], Groups: [...] }   ← NEW top-level key
    Validate(config)
        existing entity validation, unchanged
        NEW: group validation — group_id non-empty, members non-empty,
             each member entity_id exists in Entities (or is independently valid — decide in planning:
             does a group member need its OWN EntityConfig entry too, or can group membership reference
             any entity_id HA exposes without requiring a parallel single-entity detector config?)
        NEW: warn/reject if group_id collides with any entity_id (or apply group__ prefix internally, ADR-3)
    return config
```

**Open design question for planning (flagged, not resolved here):** should a sensor that is *only* used inside a group (never individually monitored) require a redundant `EntityConfig` entry in `Entities`, or should `GroupConfig.Members` be able to reference bare HA `entity_id` strings that exist in HA but have no corresponding `EntityConfig`? The InfluxDB query for group batch only needs the entity_id string (to build the Flux filter) — it does NOT need an `EntityConfig`/`DetectorConfig` for that entity. Recommend: **group members do NOT require a parallel `EntityConfig` entry** — `GroupConfig.Members` is a free list of entity_id strings, independent of whether those entities are also individually tracked in `Entities`. This avoids forcing "must also single-sensor-monitor every group member" and matches the natural UX ("pick 4 sensors to group" shouldn't require "...and also configure each one individually first").

---

## Suggested Build Order (Phase Sequencing)

Dependency rule: each phase delivers a shippable, testable increment; no phase assumes work from a later phase.

### Phase A — Proto + Python Detector Core (foundation, no .NET wiring yet)

**Goal:** `GroupScoreBatch`/`GroupFit` RPCs exist, are wire-compatible additions, and can be called directly (e.g. via a test client or grpcurl) with a hand-built aligned matrix to produce correct group verdicts for both modes.

Steps:
1. Extend `proto/argus.proto` additively per ADR-7 sketch; regenerate C# and Python stubs (`Grpc.Tools`, `grpcio-tools` — both already in STACK.md).
2. Implement `MultivariatePyODDetector` (joint mode) — generalize `PyODDetector` pattern to `(n, d)`, wrap PCA or ECOD (pick one default per FEATURES-track "algorithm chooser" decision, to be made alongside the algorithm-library expansion track).
3. Implement `PeerDivergenceDetector` (peer mode) — leave-one-out z-score/MAD, pure numpy.
4. Implement `GroupDetectorRegistry` — mirror `DetectorRegistry`'s lock pattern (creation lock + per-key entity lock, train-outside-lock).
5. Add `GroupScoreBatch`/`GroupFit` to `DetectorServicer`.
6. Reuse `ModelStore` unmodified — pass `group_id` (prefixed `group__` per ADR-3 refinement) as the slug argument; unit-test that group and entity models never collide in `load_all_into`'s glob.
7. Unit tests: both modes, cold-start fit, model persistence round-trip, proto backward-compat test (existing univariate RPCs still pass all existing tests unmodified — this is the acceptance gate for ADR-7).

**Dependencies:** None — pure proto + Python, independently testable without any .NET or InfluxDB involvement.

### Phase B — Batch-First Group Pipeline (.NET orchestrator wiring)

**Goal:** A configured group of real HA sensors, with data already in InfluxDB, produces a live group binary_sensor + score sensor in HA on the existing batch cadence, end to end.

Steps:
1. Add `GroupConfig` + `EntitiesConfig.Groups` list; extend `EntitiesConfigLoader` validation (ADR-6).
2. Extend `InfluxDbReader` (or add sibling reader) with the Flux `pivot`+`aggregateWindow` group query (ADR-1).
3. Add `IGroupDetectorClient` methods (or extend `IBatchDetectorClient`) wrapping the new gRPC stubs via the existing `DetectionGateway` channel.
4. Implement `GroupBatchSchedulerWorker` — own timer (`GroupBatchIntervalMinutes`), query→align→request→publish loop (ADR-4), fault-isolated per group like `BatchSchedulerWorker` isolates per entity.
5. Add `UniqueId.GroupAnomalyId`/`GroupScoreId`; implement `GroupDiscoveryPublisher` — group binary_sensor + score sensor, `diverging_member`/`member_scores` as MQTT JSON attributes (peer mode).
6. Register `GroupBatchSchedulerWorker` + `GroupDiscoveryPublisher` in `Program.cs`.
7. Integration test: hand-author a `groups:` block in `entities.yaml` for 2-3 real InfluxDB-backed sensors; verify group binary_sensor + score sensor appear in HA via MQTT discovery within one batch cycle; verify peer mode's `diverging_member` attribute updates correctly when one member's values are perturbed.

**Dependencies:** Phase A (proto + Python detector must exist and be correct before wiring the .NET caller).

### Phase C — Config/UX Wiring for Groups (Ingress UI, or whatever v4.0's UI track lands on)

**Goal:** An operator can create/edit/delete groups (choose members, mode, detector) through the UI without hand-editing YAML, mirroring how v3.0 did this for single entities.

Steps:
1. `GET /api/groups` (list configured groups) / `POST /api/groups` (create/update, validate via `EntitiesConfigLoader`'s group validation, atomic write + `ILiveEntitiesConfig.Reload()`).
2. Group authoring UI: multi-select from the existing sensor discovery list (`IHaSensorRegistry`, unchanged from v3.0) to build `Members`; mode toggle (peer/joint); detector + params (reuses whatever "algorithm chooser" UX work lands from the parallel FEATURES track).
3. Reload path: `GroupBatchSchedulerWorker` reads `ILiveEntitiesConfig.Get().Groups` per tick (same pattern as `BatchSchedulerWorker.RunBatchAsync` already does for `.Entities` — CFG-04 precedent applies directly, zero new reload mechanism needed since the existing `ILiveEntitiesConfig` atomic-swap + per-tick-read pattern already covers this).
4. End-to-end test: create a group via UI, see it scored within one batch cycle, delete it, verify MQTT retraction (extend `DiscoveryPublisher.RetractAsync`'s pattern to `GroupDiscoveryPublisher`).

**Dependencies:** Phase B (group batch pipeline must work before exposing it in UI); depends on the UI-stack decision already locked in PROJECT.md (light SPA) but is otherwise independent of the algorithm-chooser UX track's internal details.

### Phase D (Explicitly Deferred, Not Designed) — Streaming Groups

**Not part of this milestone's build order.** Recorded here only so the roadmap does not accidentally schedule it.

Streaming group detection (scoring a group on every `state_changed` event, the way single-entity `ScoreStreamPipeline` does today) requires solving:
- **Windowing across N independently-arriving streams:** each member fires `state_changed` at its own cadence; a "row" for the group detector isn't naturally defined the way a single Point is.
- **Last-value-carried-forward (LVCF) semantics:** when member A reports a new value but members B/C/D haven't reported recently, does the group detector score using B/C/D's stale last-known values, wait, or skip? This is a genuinely hard problem (staleness bounds, what counts as "too stale to use," how it interacts with the existing `FrozenSensorDetector` concept for single sensors) that has NO existing analogue in the codebase to extend from — `ScoreStreamPipeline` today builds one entity's state per stream, with no cross-entity synchronization concept at all.
- **Latency target for streaming groups**, if ever defined, would need its own ADR distinct from both the single-sensor <2s target and this milestone's batch-cycle-bound group target (ADR-5).

This milestone deliberately ships batch-first per PROJECT.md's explicit scope decision, proving the detection model (peer/joint, algorithm choice, output shape) before taking on the LVCF/windowing complexity. Do not attempt to design the streaming group data flow as part of this milestone's phases — flag it as a distinct future milestone once batch groups are validated live.

---

## Anti-Patterns to Avoid

### Anti-Pattern 1: Resampling Inside the Python Detector

**What people do:** Send raw unaligned per-member points over gRPC and use pandas `resample()`/`merge_asof()` inside the Python detector to align them before scoring.

**Why it's wrong:** Duplicates Flux's native `aggregateWindow`/`pivot` capability in a second language and a second dependency (pandas, not currently in `detector/requirements.txt` per STACK.md's Darts/PyOD/River/joblib list — though Darts pulls in some pandas-adjacent deps transitively, it's not the project's stated resampling tool). It also moves a data-correctness concern (what does InfluxDB actually have, at what actual timestamps) into ML code that should only ever see "already a valid matrix." Breaks the established .NET-queries/Python-scores boundary that the existing single-entity batch path already establishes.

**Do this instead:** Resample/align in .NET via Flux (`InfluxDbReader` extension, ADR-1). Python receives only complete, aligned `GroupRow` matrices.

### Anti-Pattern 2: One RPC Per Algorithm or Per Mode

**What people do:** Add `PeerScoreBatch`, `JointScoreBatch`, `PcaScoreBatch`, `HbosScoreBatch`, etc. as separate RPCs as new algorithms are added.

**Why it's wrong:** Every new algorithm or mode combination would require a proto change, a new .NET client method, and a new `Program.cs`/DI wiring path. This directly fights the milestone's OTHER stated goal ("expanded algorithm library with a user-friendly chooser") — a chooser implies algorithms are a runtime-selectable string/enum, not a compile-time RPC choice.

**Do this instead:** One `GroupScoreBatch` RPC; `mode` and `detector` are request fields; the Python-side factory (`_create_group_detector`) is the single place new algorithms get registered (mirrors how `_create_detector` already handles `mad`/`stl`/`hst` today).

### Anti-Pattern 3: Making Group Detection a Fork of the Single-Entity Pipeline

**What people do:** Copy `BatchSchedulerWorker`, `DetectorRegistry`, `PyODDetector`, `DiscoveryPublisher` wholesale into `Group*` versions with duplicated logic, drifting apart over time as bugs get fixed in one copy but not the other.

**Why it's wrong:** the milestone's ADRs above do introduce new parallel classes (`GroupBatchSchedulerWorker`, `GroupDetectorRegistry`, `GroupDiscoveryPublisher`) — but each is parallel because the *data shape* is genuinely different (N-member matrix vs. single value), not because logic was blindly duplicated. Real duplication risk: don't re-implement `ModelStore`, don't re-implement `DetectionGateway`'s channel/health-gate, don't re-implement the retain/QoS/availability MQTT publishing conventions in `DiscoveryPublisher` — reuse those directly (ADR-3, and this inventory's "Unchanged Components" list).

**Do this instead:** Share `ModelStore`, `DetectionGateway`, MQTT connection/publishing conventions (topic naming pattern, retain=true, QoS AtLeastOnce, bridge+per-subject availability list) as-is. Only fork the pieces where the underlying data model genuinely differs (registry keyed differently, request/response shape, resampling).

### Anti-Pattern 4: Requiring Every Group Member to Also Be an Individually-Configured Entity

**What people do:** Require `GroupConfig.Members` entries to match an existing `EntityConfig.EntityId` in `Entities`, forcing the operator to first set up single-sensor detection for every sensor before it can join a group.

**Why it's wrong:** Couples two independent concerns (does the operator want single-sensor anomaly detection on sensor X? vs. does sensor X participate in a group?) that don't need to be coupled. A sensor might be interesting ONLY as a group member (e.g. one of 4 tire pressures — nobody wants a standalone "this one tire's pressure is weird relative to its own history" detector, only "is this tire diverging from its siblings"). Forcing redundant per-entity config creates busywork and confusing UI (why do I have to also configure a detector I don't want?).

**Do this instead:** `GroupConfig.Members` is a free list of HA `entity_id` strings, validated only against what HA actually exposes (via `IHaSensorRegistry`, unchanged from v3.0) — independent of whether those entities also appear in `Entities`.

### Anti-Pattern 5: Skipping the Group/Entity Namespace Collision Guard

**What people do:** Let `group_id` and `entity_id` share the same `ModelStore`/`DetectorRegistry` key namespace (ADR-3) without any collision guard, assuming operators will never pick a group_id matching an entity_id.

**Why it's wrong:** Silent model conflation is a nasty, hard-to-diagnose bug class — a group's model could overwrite or read a single entity's model (or vice versa) if names collide, with no error raised anywhere. Precedent in this codebase: multiple existing threat-model notes (T-02-02-02 Flux injection guard, T-1-05 YAML escaping) show the project already takes "validate operator-controlled strings before they become storage keys" seriously.

**Do this instead:** Apply the `group__` prefix internally (ADR-3's recommended refinement) so the two namespaces can never collide, regardless of what an operator names a group.

---

## Integration Points

### External (Unchanged from v1–v3)

| Integration | Mechanism | Change for v4.0 |
|-------------|-----------|-------------------|
| HA WebSocket | `HaWebSocketClient` / `NetDaemonHaEventSource` | None — groups are batch-first, no streaming subscription needed for group members beyond what single-entity tracking (if any) already does |
| MQTT broker | `MqttConnection` / `MqttPublisherWorker` | None to the connection itself; new topics published (group binary_sensor/sensor state + config) using the same connection |
| Python gRPC detector | `DetectionGateway` (single shared channel) | None to the channel/health-gate; new RPCs (`GroupScoreBatch`/`GroupFit`) added to the same `DetectorService`, served over the same channel |
| InfluxDB | `InfluxDbReader` | Extended with a new group-shaped Flux query method (pivot + aggregateWindow); existing single-entity query method untouched |

### New Integration Points (v4.0)

| Integration | Mechanism | Notes |
|-------------|-----------|-------|
| Group gRPC RPCs | `GroupScoreBatch`/`GroupFit` on existing `DetectorService`, same channel | No new mTLS setup, no new port, no new health-gate — reuses `DetectionGateway.WaitForHealthyAsync` |
| Group MQTT discovery | `GroupDiscoveryPublisher` → `homeassistant/binary_sensor/{group_id}_anomaly/config` etc. | Parallel topic scheme to entity discovery; `diverging_member`/`member_scores` via HA's `json_attributes_topic` pattern (same MQTT discovery mechanism, additional `json_attributes_topic` key in the discovery payload) |
| Group config surface | `EntitiesConfig.Groups` in `entities.yaml`; `ILiveEntitiesConfig` reload (existing v3.0 mechanism, unmodified) | Reuses the exact atomic-swap + `ConfigChanged` reload path already built for entities — no new reload infrastructure |
| Group Influx query | Flux `pivot()`+`aggregateWindow()` in `InfluxDbReader` (extended) | Reuses existing `_safeFluxString` injection guard pattern for member entity_ids and group_id interpolated into Flux |

---

## Sources

- Read directly from repository source (HIGH confidence, no speculative gaps): `proto/argus.proto`, `orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs`, `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs`, `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs`, `orchestrator/Argus.Orchestrator/Batch/InfluxDbReader.cs`, `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs`, `orchestrator/Argus.Orchestrator/Detection/DetectionGateway.cs`, `detector/argus_detector/model_store.py`, `detector/argus_detector/registry.py`, `detector/argus_detector/servicer.py`, `detector/argus_detector/pyod_detector.py`, `.planning/PROJECT.md`, `argus/config.yaml`, `argus/rootfs/usr/local/bin/gen-entities.py`.
- [pyod.models.pca — PyOD docs](https://pyod.readthedocs.io/en/latest/_modules/pyod/models/pca.html) — confirms `(n_samples, n_features)` input shape for multivariate models
- [pyod.models.ecod source — GitHub](https://github.com/yzhao062/pyod/blob/master/pyod/models/ecod.py) — confirms ECOD's native multivariate support and `decision_function` shape `(n_samples,)`
- [PyOD full model list — Read the Docs](https://pyod.readthedocs.io/en/latest/pyod.models.html) — confirms PCA/ECOD/COPOD/HBOS/IForest/KNN/LOF all follow the same `fit`/`decision_function` API surface used elsewhere in this codebase's `PyODDetector`

---
*Architecture research for: Argus v4.0 Group & Multivariate Anomaly Detection*
*Researched: 2026-07-02*
