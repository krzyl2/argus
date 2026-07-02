# Phase 6: Batch Group Pipeline - Context

**Gathered:** 2026-07-02
**Status:** Ready for planning

<domain>
## Phase Boundary

Wire Phase 5's group detectors into the .NET orchestrator end-to-end: operator defines a group in config → orchestrator time-aligns member history from InfluxDB → scores via Phase 5's `ScoreGroupBatch`/`FitGroup` RPCs → publishes/retracts group anomaly entities via MQTT discovery — with unit and membership guards at config-load time so broken groups degrade safely instead of producing silently-wrong scores. Batch path only (streaming groups are deferred, STRM-01/02). No UI (Phase 8). No new detector algorithms (Phase 5 is done).

Covers requirements: GRP-01 (explicit config, stable group_id), GRP-02 (InfluxDB time-alignment + staleness cap), GRP-08 (MQTT publish/retract without orphaning).
</domain>

<decisions>
## Implementation Decisions

### Group Config Schema (GRP-01)
- New top-level `groups:` list in entities.yaml, sibling to `entities:`. Retire the per-entity `EntityConfig.Covariates` / `EntityConfig.Groups` placeholders (wrong per-entity/inverted shape per REQUIREMENTS out-of-scope note) in favor of a group-centric top-level model.
- Group entry shape: `{ group_id, friendly_name, members: [entity_id…], mode: peer_divergence|joint, detector, params }`.
- One mode + one detector per group entry. Operator wanting both modes on the same members defines two group entries. (Simpler than a per-group detector list; keeps group_id→entity mapping unambiguous.)
- `group_id` is an operator-assigned immutable string, slugified for MQTT unique_id. No auto-discovery, no auto-generated ids.

### InfluxDB Time-Alignment (GRP-02)
- Single server-side Flux query: `aggregateWindow(every, fn)` + `pivot(rowKey:_time, columnKey: member, valueColumn:_value)` → an N-timestamp × M-member matrix. Alignment is .NET-side (orchestrator), matching Phase 5's assumption of pre-aligned input.
- Window + aggregation configurable per group; defaults `every=5m`, `fn=mean`.
- `staleness_cap` (configurable duration): any pivot timestamp where a member's underlying value is older than the cap (i.e. a forward-filled gap beyond the cap) is EXCLUDED from scoring — stale gaps must not be scored as real data.
- Lookback window reuses the existing batch lookback from `ConnectionSettings` (no separate per-group lookback).

### MQTT Group Entities + Retraction (GRP-08)
- Entity layout mirrors Phase 5's response shape: peer-divergence → one binary_sensor + score sensor PER member; joint-multivariate → a single group-level binary_sensor + score sensor.
- unique_id scheme: `argus_group_{group_slug}_{member_slug}_flag|score` (peer) and `argus_group_{group_slug}_flag|score` (joint).
- Retraction: store a hash of each group's membership/config; on change, retract removed members' discovery topics (empty retained payload) BEFORE publishing the new set — reuses the v3.0 MQTT retraction pattern (`DiscoveryPublisher` / `MqttRetractionTests`). No orphaned stale entities.
- HA `device` grouping: all entities of a group are published under one HA device keyed by group_id (device block in the discovery payload) so they group in the HA UI.

### Config-Load Validation & Scheduling (GRP-04 config-time guard, GRP-02 unit guard)
- Units sourced from HA entity state attributes (`unit_of_measurement`), cached at config-load (reuse the existing HA state/discovery path). **Peer-divergence:** members must share a unit — differing units → reject/warn (divergence across mixed units is meaningless). **Joint-multivariate:** mixed units are EXPECTED and fine (Phase 5 RobustScaler handles scale) — no unit block.
- Minimum-member floor (3, from Phase 5) enforced at config-load: a group below floor is rejected at load — no MQTT publish, no scoring, logged warning.
- Degrade-not-crash: an invalid group (bad units, below floor, unknown detector, missing members) is logged and skipped; valid groups continue; the orchestrator never crashes on bad group config (consistent with the v3.0 no-crash config guarantee — Validate-before-Swap).
- Scheduling: groups are scored inside the existing `BatchSchedulerWorker` cycle, after the per-entity loop, using the same per-cycle live-config read (CFG-04 pattern) and the same cadence. No separate worker.
</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs` — root config type (`Entities` list; dead `EntityConfig.Covariates`/`Groups` placeholders to retire). Add a top-level `Groups: List<GroupConfig>`.
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` — YAML load + `Validate()` (Validate-before-Swap, LogWarning-not-throw on bad config). Extend with group validation (unit/floor/members).
- `orchestrator/Argus.Orchestrator/Config/LiveEntitiesConfig.cs` (`ILiveEntitiesConfig`) — `Interlocked.Exchange` hot-swap + `ConfigChanged` event. Groups read per-cycle via `.Get()`.
- `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` — `RunBatchAsync` iterates `_liveConfig.Get().Entities` per-cycle; add a parallel group loop after the entity loop. Fault-isolation pattern (per-item try/catch, rethrow OperationCanceledException) applies to groups too.
- `orchestrator/Argus.Orchestrator/Batch/InfluxDbReader.cs` + `IInfluxDataSource` / `IInfluxQueryApi` — existing per-entity Flux query (`QueryAsync(entityId)`). Add a group-aligned query method (aggregateWindow+pivot) returning the N×M matrix.
- `orchestrator/Argus.Orchestrator/Batch/BatchDetectorClientAdapter.cs` + `IBatchDetectorClient` — gRPC client wrapper (`ScoreBatchAsync`/`FitAsync`). Add `ScoreGroupBatchAsync`/`FitGroupAsync` wrapping the new Phase 5 RPCs.
- `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs`, `StatePublisher.cs`, `UniqueId.cs`, `FriendlyName.cs`, `IStatePublisher` — MQTT discovery publish + retract. Reuse for group entities; `MqttRetractionTests` is the retraction analog.
- `orchestrator/Argus.Orchestrator/Config/InputValidator.cs`, `GlobExpander.cs` — validation + entity-id expansion patterns.
- Proto: Phase 5 added `Series`, `GroupScoreRequest/Response`, `FitGroupRequest/Response`, `FeatureContribution`, RPCs `ScoreGroupBatch`/`FitGroup` (csharp_namespace `Argus.Detector.V1`) — .NET stubs regen automatically via MSBuild `<Protobuf>`.

### Established Patterns
- Per-cycle live-config read (CFG-04) so a config Swap is picked up next tick without restart.
- Fault isolation: per-item try/catch, always rethrow `OperationCanceledException`.
- `google.protobuf.DoubleValue` → C# `double?`; `Timestamp.FromDateTime(ts.ToUniversalTime())`.
- Validate-before-Swap: bad config logs a warning and is skipped, never crashes the live pipeline.
- Batch publishes only the last verdict (most recent point) per entity — group per-member peer output likely mirrors this (last aligned timestamp), joint publishes the last group verdict.

### Integration Points
- `EntitiesConfigLoader.Validate()` — add group unit/floor/member checks here (config-load time).
- `BatchSchedulerWorker.RunBatchAsync` / `RunNightlyFitAsync` — add group loop; groups need Fit too (joint models) via `FitGroup` in the nightly path.
- `DiscoveryPublisher` — group device block + retraction hash on `ConfigChanged`.
- HA state attributes (`unit_of_measurement`) — reuse the existing HA WebSocket/get_states discovery path to source member units at load.
</code_context>

<specifics>
## Specific Ideas

- Peer-divergence publishes per-member entities; joint publishes one group-level entity — the MQTT layout must branch on `mode`.
- Staleness cap is the key correctness guard for GRP-02: a member that stopped reporting must not have its last value forward-filled and scored as if live. Test: a stale member beyond cap → its timestamps excluded, not scored.
- Membership-change retraction test (GRP-08): remove a member from a peer group → that member's discovery topics retracted (empty payload), no orphan left in HA.
- Config-load guard tests (GRP-04): peer group with mixed units → rejected/warned; group with 2 members → rejected (below floor); both degrade without crashing, valid groups still publish.
- Joint models need a Fit lifecycle (nightly `FitGroup`); peer-divergence is stateless (no fit) — the group loop must not try to fit peer groups.
</specifics>

<deferred>
## Deferred Ideas

- Streaming group detection (windowed + last-value-carried-forward) — STRM-01/02, out of scope this milestone.
- Group config UI, algorithm chooser, friendly-name search, area-scoped group suggestions — Phase 8 (ALGO-*, SRCH-*).
- Surfacing per-feature attribution (GRP-09) in HA/UI — Phase 8; Phase 6 may carry the contribution data through but the UI treatment is Phase 8.
- Sensitivity Low/Med/High presets — Phase 8 (ALGO-01); Phase 6 uses raw params / Phase 5 defaults.
</deferred>
