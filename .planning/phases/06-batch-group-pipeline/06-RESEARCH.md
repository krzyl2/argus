# Phase 6: Batch Group Pipeline - Research

**Researched:** 2026-07-02
**Domain:** .NET orchestrator wiring — InfluxDB multi-series time-alignment, gRPC group RPCs, MQTT group discovery/retraction, YAML config schema, config-load validation
**Confidence:** MEDIUM (HIGH on gRPC/MQTT/YAML — all mirror existing verified code; MEDIUM on Flux staleness semantics — one open question the planner must resolve explicitly)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

**Group Config Schema (GRP-01)**
- New top-level `groups:` list in entities.yaml, sibling to `entities:`. Retire the per-entity `EntityConfig.Covariates` / `EntityConfig.Groups` placeholders (wrong per-entity/inverted shape per REQUIREMENTS out-of-scope note) in favor of a group-centric top-level model.
- Group entry shape: `{ group_id, friendly_name, members: [entity_id…], mode: peer_divergence|joint, detector, params }`.
- One mode + one detector per group entry. Operator wanting both modes on the same members defines two group entries.
- `group_id` is an operator-assigned immutable string, slugified for MQTT unique_id. No auto-discovery, no auto-generated ids.

**InfluxDB Time-Alignment (GRP-02)**
- Single server-side Flux query: `aggregateWindow(every, fn)` + `pivot(rowKey:_time, columnKey: member, valueColumn:_value)` → an N-timestamp × M-member matrix. Alignment is .NET-side (orchestrator), matching Phase 5's assumption of pre-aligned input.
- Window + aggregation configurable per group; defaults `every=5m`, `fn=mean`.
- `staleness_cap` (configurable duration): any pivot timestamp where a member's underlying value is older than the cap (i.e. a forward-filled gap beyond the cap) is EXCLUDED from scoring — stale gaps must not be scored as real data.
- Lookback window reuses the existing batch lookback from `ConnectionSettings` (no separate per-group lookback).

**MQTT Group Entities + Retraction (GRP-08)**
- Entity layout mirrors Phase 5's response shape: peer-divergence → one binary_sensor + score sensor PER member; joint-multivariate → a single group-level binary_sensor + score sensor.
- unique_id scheme: `argus_group_{group_slug}_{member_slug}_flag|score` (peer) and `argus_group_{group_slug}_flag|score` (joint).
- Retraction: store a hash of each group's membership/config; on change, retract removed members' discovery topics (empty retained payload) BEFORE publishing the new set — reuses the v3.0 MQTT retraction pattern (`DiscoveryPublisher` / `MqttRetractionTests`). No orphaned stale entities.
- HA `device` grouping: all entities of a group are published under one HA device keyed by group_id (device block in the discovery payload) so they group in the HA UI.

**Config-Load Validation & Scheduling (GRP-04 config-time guard, GRP-02 unit guard)**
- Units sourced from HA entity state attributes (`unit_of_measurement`), cached at config-load (reuse the existing HA state/discovery path). Peer-divergence: members must share a unit — differing units → reject/warn. Joint-multivariate: mixed units are EXPECTED and fine — no unit block.
- Minimum-member floor (3, from Phase 5) enforced at config-load: a group below floor is rejected at load — no MQTT publish, no scoring, logged warning.
- Degrade-not-crash: an invalid group (bad units, below floor, unknown detector, missing members) is logged and skipped; valid groups continue; the orchestrator never crashes on bad group config.
- Scheduling: groups are scored inside the existing `BatchSchedulerWorker` cycle, after the per-entity loop, using the same per-cycle live-config read (CFG-04 pattern) and the same cadence. No separate worker.

### Claude's Discretion
(None explicitly separated in CONTEXT.md beyond the above — all decisions above are locked. Implementation details not covered by a locked decision, e.g. exact internal method names, are discretionary and researched below.)

### Deferred Ideas (OUT OF SCOPE)
- Streaming group detection (windowed + last-value-carried-forward) — STRM-01/02, out of scope this milestone.
- Group config UI, algorithm chooser, friendly-name search, area-scoped group suggestions — Phase 8 (ALGO-*, SRCH-*).
- Surfacing per-feature attribution (GRP-09) in HA/UI — Phase 8; Phase 6 may carry the contribution data through but the UI treatment is Phase 8.
- Sensitivity Low/Med/High presets — Phase 8 (ALGO-01); Phase 6 uses raw params / Phase 5 defaults.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| GRP-01 | Operator can define a named group of sensor members explicitly in config (no auto-discovery), keyed by a stable operator-assigned group_id | `EntitiesConfig`/`GroupConfig` schema (Standard Stack, Architecture Patterns), YAML deserialization notes (Code Examples), config-load validation (Common Pitfalls) |
| GRP-02 | Group members' history is time-aligned onto a common grid before scoring (InfluxDB `aggregateWindow`+`pivot`, server-side), with a staleness cap on forward-filled gaps | Flux query construction (Code Examples), FluxTable/FluxRecord parsing (Architecture Patterns), staleness semantics (Common Pitfalls, Open Questions) |
| GRP-08 | Group anomaly entities are published and retracted via MQTT discovery on group creation/membership change without orphaning stale HA entities | `DiscoveryPublisher` group extension pattern (Architecture Patterns, Code Examples), membership-hash retraction (Common Pitfalls) |
</phase_requirements>

## Summary

Phase 6 is pure wiring: every building block it needs already exists in the codebase in a form that generalizes cleanly to groups. The proto contract (`Series`, `GroupScoreRequest/Response`, `FitGroupRequest/Response`, `ScoreGroupBatch`/`FitGroup` RPCs) is done and stable (Phase 5). The .NET orchestrator's existing per-entity pipeline (`BatchSchedulerWorker` → `InfluxDbReader` → `IBatchDetectorClient` → `IStatePublisher`/`DiscoveryPublisher`) is a direct structural template: add a parallel `Groups` loop that mirrors the `Entities` loop, add a `GroupConfig` model beside `EntityConfig`, add a group-aware Flux query method beside the existing per-entity one, add `ScoreGroupBatchAsync`/`FitGroupAsync` to the detector-client adapter, and add group-specific discovery/retraction builders beside the existing entity ones.

The one area needing real design thought (not just imitation) is GRP-02's staleness cap. InfluxDB's `aggregateWindow`+`pivot` combination, when NOT combined with `fill()`, naturally produces Flux `null` for any pivot cell where a member had zero raw points in that aggregation window — HA's InfluxDB integration writes only on `state_changed`, so it never forward-fills at the source [CITED: home-assistant.io/integrations/influxdb]. This means the simplest and most correct implementation of the "no scoring of forward-filled gaps" requirement is: never call `fill()` in the group query, and treat any pivoted cell that is `null` (missing key in `FluxRecord.Values`) as an automatic exclusion for that timestamp row. A `staleness_cap` measured in wall-clock duration (as CONTEXT.md specifies) requires one additional refinement beyond bare per-window null-checking: a query per member must also carry each member's most-recent-actual-timestamp so that rows can be excluded when the true "freshness" gap exceeds the cap even across window boundaries where a stray old point might still land inside a window bucket. The recommended approach (detailed in Architecture Patterns and Common Pitfalls) is a two-query design per group cycle: (1) the pivoted aggregate matrix for scoring, and (2) a cheap `last()`-per-member freshness query to get each member's most recent raw timestamp, used to decide whether the member's most recent aggregate points are within the cap. This avoids injecting `fill()` (which would reintroduce forward-filled values into the scored matrix) while still implementing a genuine staleness cutoff.

**Primary recommendation:** Extend `EntitiesConfig` with a `List<GroupConfig> Groups`, add a `GroupInfluxReader` (or extend `InfluxDbReader`) with a `QueryGroupAsync(members, every, fn)` method returning `IReadOnlyDictionary<DateTime, IReadOnlyDictionary<string,double>>` (only cells present = fresh; missing = excluded), branch `BatchSchedulerWorker.RunBatchAsync`/`RunNightlyFitAsync` on group `mode` (peer_divergence never calls Fit; joint always does), and add `BuildGroupBinarySensorConfig`/`BuildGroupSensorConfig`/`RetractGroupAsync` to `DiscoveryPublisher` following the exact JSON shape and retain/QoS conventions already used for per-entity discovery.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Group config schema + validation (units, floor, degrade-not-crash) | API/Backend (.NET orchestrator, config-load) | — | Config-load is a backend startup/reload concern; no UI in this phase (Phase 8) |
| InfluxDB time-alignment (aggregateWindow+pivot, staleness cap) | Database/Storage query construction, executed from API/Backend | — | Flux query built and issued by the orchestrator; InfluxDB does the server-side aggregation/pivot compute, but the orchestrator owns query construction and staleness-cap decision logic |
| Group scoring (gRPC ScoreGroupBatch/FitGroup calls) | API/Backend (.NET orchestrator) | — | Orchestrator is the gRPC client; Python detector (already built, Phase 5) is the gRPC server — out of scope this phase |
| MQTT discovery publish/retract for group entities | API/Backend (.NET orchestrator) | — | Orchestrator publishes to the MQTT broker which HA's own MQTT integration then surfaces as entities; no client-side component in this phase |
| HA entity unit_of_measurement lookup | API/Backend (.NET orchestrator, via existing HA WebSocket client) | — | Sourced from the existing `IHaSensorRegistry`/HA WebSocket `get_states` path — a backend-to-backend integration, not client-facing |

## Standard Stack

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| InfluxDB.Client | 5.0.0 (already pinned, `CLAUDE.md`) | Flux query construction + `FluxTable`/`FluxRecord` parsing for group time-alignment | Already the project's InfluxDB client; `IInfluxQueryApi`/`InfluxQueryApiAdapter` abstraction already exists and is directly reusable for a new group query method [VERIFIED: existing codebase, `orchestrator/Argus.Orchestrator/Batch/InfluxQueryApiAdapter.cs`] |
| Grpc.Net.Client | 2.80.0 (already pinned) | Calling the new `ScoreGroupBatch`/`FitGroup` RPCs | Already the project's gRPC client; `DetectionGateway.DetectorClient` (typed `DetectorServiceClient`) already exposes the generated stub — the two new RPC methods are auto-generated from `proto/argus.proto` via the existing MSBuild `<Protobuf>` item, no new package needed [VERIFIED: existing codebase, `orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj:23`] |
| YamlDotNet | 16.3.0 (already pinned) | Deserializing the new top-level `groups:` YAML key | Already the project's YAML library; `EntitiesConfigLoader` already uses `DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).IgnoreUnmatchedProperties()` — a new top-level `Groups` property on `EntitiesConfig` deserializes automatically with zero new config [VERIFIED: existing codebase, `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs`] |
| MQTTnet | 5.1.0.1559 (already pinned) | Publishing/retracting group discovery configs and state | Already the project's MQTT client; `MqttConnection.PublishAsync` is the sole publish surface used by all existing discovery/state code, directly reusable [VERIFIED: existing codebase, `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs`] |

**No new NuGet packages are required for this phase** — every capability needed (Flux query construction, gRPC group RPCs, group YAML config, MQTT group discovery) is already available through packages pinned in `CLAUDE.md` and already wired into the orchestrator's DI container (`Program.cs`).

### Supporting

None beyond the Core table — this phase is additive wiring inside an existing, fully-provisioned dependency set.

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Two-query staleness design (pivoted matrix + last-per-member freshness query) | Single query with `fill(usePrevious:true)` + a separate "age" column via `elapsed()` | `fill()` reintroduces forward-filled values into the very matrix passed to the detector — directly violates the locked "stale gaps must not be scored as real data" decision. Rejected. |
| Extending `InfluxDbReader` with a new method | A separate `GroupInfluxReader` class implementing a new `IGroupInfluxDataSource` | Both are valid; a separate reader keeps `InfluxDbReader` (already tested, stable) untouched and mirrors the existing `IInfluxDataSource`/`IBatchDetectorClient` split-by-concern pattern. Planner's call — either is consistent with existing conventions. |

**Installation:** None — no `npm install`/`dotnet add package` needed.

**Version verification:** All four libraries are already pinned in `CLAUDE.md` and `Argus.Orchestrator.csproj`; no drift check needed since this phase adds no new packages.

## Package Legitimacy Audit

**Not applicable — this phase installs zero new external packages.** All required functionality (Flux query building, gRPC group RPCs, YAML group config, MQTT group discovery) is available through NuGet packages already present in `Argus.Orchestrator.csproj` (`InfluxDB.Client 5.0.0`, `Grpc.Net.Client 2.80.0`, `YamlDotNet 16.3.0`, `MQTTnet 5.1.0.1559`) and already used by existing, tested code in this repository.

## Architecture Patterns

### System Architecture Diagram

```
entities.yaml (groups: list)
        │
        ▼
EntitiesConfigLoader.Load()  ──validates──▶  GroupConfig.Validate()
        │  (units via IHaSensorRegistry,          │  reject: <3 members,
        │   floor check, degrade-not-crash)        │  mixed units (peer mode),
        ▼                                          │  unknown detector
LiveEntitiesConfig.Swap()  ──ConfigChanged event──▶ DiscoveryPublisher
        │  (Interlocked.Exchange, per-cycle read)      (retract removed members
        ▼                                               via membership hash,
BatchSchedulerWorker.RunBatchAsync()                    then republish current set)
   ├─ existing: foreach entity in Entities → InfluxDbReader.QueryAsync
   │                                       → ScoreBatchAsync → StatePublisher
   └─ NEW: foreach group in Groups
             │
             ▼
      GroupInfluxReader.QueryGroupAsync(members, every, fn, lookback)
             │  Flux: aggregateWindow(every,fn) |> pivot(rowKey:_time,
             │        columnKey:member, valueColumn:_value)
             │  + last()-per-member freshness query (staleness_cap check)
             ▼
      N×M matrix (timestamp rows excluded when any member stale beyond cap)
             │
             ▼
      BatchDetectorClientAdapter.ScoreGroupBatchAsync(GroupScoreRequest)
             │  detector field dispatches peer_divergence vs ecod/copod/pca/iforest
             │  server-side (Phase 5, already built)
             ▼
      GroupScoreResponse { per_member[] (peer) | group_verdict (joint), contributions[] }
             │
             ▼
      StatePublisher / DiscoveryPublisher (group variant)
             ├─ peer:  binary_sensor+sensor PER member, argus_group_{slug}_{member}_flag|score
             └─ joint: single binary_sensor+sensor,      argus_group_{slug}_flag|score
             │
             ▼
      MQTT retained discovery topics ──▶ Home Assistant auto-creates entities
```

### Recommended Project Structure

```
orchestrator/Argus.Orchestrator/
├── Config/
│   ├── EntitiesConfig.cs        # ADD: GroupConfig, GroupMode enum/string, Groups list on EntitiesConfig
│   ├── EntitiesConfigLoader.cs  # ADD: ValidateGroups() — unit/floor/member/detector checks (degrade-not-crash)
│   └── GroupSlug.cs             # NEW (optional): single slugify helper, mirrors Mqtt/UniqueId.cs pattern
├── Batch/
│   ├── BatchSchedulerWorker.cs  # ADD: group loop in RunBatchAsync + RunNightlyFitAsync (mode-branch on Fit)
│   ├── GroupInfluxReader.cs     # NEW: aggregateWindow+pivot query + staleness-cap exclusion
│   ├── IGroupInfluxDataSource.cs# NEW: testability seam, mirrors IInfluxDataSource
│   └── IBatchDetectorClient.cs  # ADD: ScoreGroupBatchAsync/FitGroupAsync method signatures
├── Mqtt/
│   ├── DiscoveryPublisher.cs    # ADD: BuildGroupBinarySensorConfig/BuildGroupSensorConfig/RetractGroupAsync
│   └── UniqueId.cs              # ADD: GroupFlagId/GroupScoreId static helpers (mirrors AnomalyId/ScoreId)
```

### Pattern 1: Group config model mirrors EntityConfig shape

**What:** A `GroupConfig` class living beside `EntityConfig` in `EntitiesConfig.cs`, deserialized via the same `UnderscoredNamingConvention` YamlDotNet pipeline already in use.
**When to use:** Always — this is the locked GRP-01 schema.
**Example:**
```csharp
// Source: pattern extrapolated from existing EntityConfig/DetectorConfig
// (orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs)
public class EntitiesConfig
{
    public List<EntityConfig> Entities { get; set; } = new();
    public List<GroupConfig> Groups { get; set; } = new();   // NEW top-level key
}

public class GroupConfig
{
    public string GroupId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public List<string> Members { get; set; } = new();
    public string Mode { get; set; } = string.Empty;   // "peer_divergence" | "joint"
    public string Detector { get; set; } = string.Empty; // "peer_divergence" | "ecod" | "copod" | "pca" | "iforest"
    public Dictionary<string, string> Params { get; set; } = new();

    // Populated at config-load time from IHaSensorRegistry — NOT deserialized from YAML.
    // Consumed by Validate() for the peer-divergence shared-unit check.
    [YamlDotNet.Serialization.YamlIgnore]
    public Dictionary<string, string?> ResolvedUnits { get; set; } = new();
}
```
Because `EntitiesConfigLoader` already builds its deserializer with `.IgnoreUnmatchedProperties()`, adding `Groups` to `EntitiesConfig` is a pure additive change — existing `entities.yaml` files with no `groups:` key deserialize `Groups` as an empty list (YamlDotNet leaves the C#-side default when a key is absent), so no migration step is required for existing installs.

### Pattern 2: Flux query for time-aligned group matrix (no `fill()`)

**What:** A single Flux query per group per batch cycle that aligns all members onto a shared timestamp grid via `aggregateWindow`+`pivot`, deliberately omitting `fill()` so gaps surface as Flux `null` rather than forward-filled values.
**When to use:** Every group batch-scoring cycle (GRP-02).
**Example:**
```csharp
// Source: pattern extrapolated from InfluxDbReader.QueryAsync (existing per-entity query)
// with aggregateWindow+pivot added per CONTEXT.md locked decision.
// Flux pivot() null-on-missing-value behavior: CITED docs.influxdata.com/flux/v0/stdlib/universe/pivot/
var membersFilter = string.Join(" or ", members.Select(m => $"r[\"entity_id\"] == \"{m}\""));
var flux = $"""
    from(bucket: "{settings.InfluxBucket}")
      |> range(start: -{lookbackHours}h)
      |> filter(fn: (r) => r["_measurement"] == "{settings.InfluxMeasurement}"
            and ({membersFilter})
            and r["_field"] == "{settings.InfluxValueField}")
      |> aggregateWindow(every: {everyDuration}, fn: {aggFn}, createEmpty: true)
      |> pivot(rowKey: ["_time"], columnKey: ["entity_id"], valueColumn: "_value")
      |> sort(columns: ["_time"])
    """;

var tables = await _queryApi.QueryAsync(flux, settings.InfluxOrg, ct);

// Parsing: after pivot, each FluxRecord's Values dict has one key per member entity_id
// (plus _time/_start/_stop/table). Missing member → FluxRecord.GetValueByKey(memberId) == null.
var rows = tables.SelectMany(t => t.Records).Select(r => new
{
    Timestamp = r.GetTime()!.Value.ToDateTimeUtc(),
    MemberValues = members.ToDictionary(
        m => m,
        m => r.GetValueByKey(m) is null ? (double?)null : Convert.ToDouble(r.GetValueByKey(m)))
});
```
**Column-name caveat:** `columnKey: ["entity_id"]` uses the raw entity_id (e.g. `sensor.living_room_temp`) as the pivoted column name. Dots in the entity_id become part of the Flux column label; `GetValueByKey("sensor.living_room_temp")` still works because it's a plain dictionary lookup by string key — no Flux identifier-escaping concern arises here (unlike Flux *query source* identifiers).

### Pattern 3: Staleness cap via a companion `last()` freshness query

**What:** Because `aggregateWindow`+`pivot` gives per-window presence/absence but not "how old is the most recent real point," a second lightweight query determines each member's most-recent raw timestamp, used to decide whether the group's most recent scoring rows should be excluded.
**When to use:** Every group batch-scoring cycle, run once alongside Pattern 2's main query (GRP-02 staleness_cap).
**Example:**
```csharp
// Source: pattern extrapolated from InfluxDbReader; last() is a standard Flux selector.
var freshnessFlux = $"""
    from(bucket: "{settings.InfluxBucket}")
      |> range(start: -{lookbackHours}h)
      |> filter(fn: (r) => r["_measurement"] == "{settings.InfluxMeasurement}"
            and ({membersFilter})
            and r["_field"] == "{settings.InfluxValueField}")
      |> group(columns: ["entity_id"])
      |> last()
    """;

var freshnessTables = await _queryApi.QueryAsync(freshnessFlux, settings.InfluxOrg, ct);
var lastSeenUtc = freshnessTables
    .SelectMany(t => t.Records)
    .ToDictionary(
        r => (string)r.GetValueByKey("entity_id")!,
        r => r.GetTime()!.Value.ToDateTimeUtc());

// Exclusion rule: a member is "stale" this cycle if utcNow - lastSeenUtc[member] > stalenessCap.
// A stale member's column is excluded from EVERY scored row this cycle (Claude's discretion —
// CONTEXT.md specifies exclusion of "timestamps where a member's value is older than the cap,"
// which for peer-divergence means dropping that member from the matrix entirely for the cycle;
// for joint-multivariate, a stale member should skip the whole group's scoring this cycle since
// joint models need a fixed feature set — see Pitfall 3 below).
```

### Pattern 4: Group loop in BatchSchedulerWorker, branching on mode

**What:** A second loop after the existing per-entity loop in `RunBatchAsync`/`RunNightlyFitAsync`, applying the exact same fault-isolation (per-item try/catch, rethrow `OperationCanceledException`) already used for entities.
**When to use:** Always — CONTEXT.md locks "no separate worker."
**Example:**
```csharp
// Source: pattern extrapolated from existing RunBatchAsync entity loop
// (orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs:125-145)
internal async Task RunBatchAsync(CancellationToken ct)
{
    foreach (var entity in _liveConfig.Get().Entities) { /* existing, unchanged */ }

    // NEW: group loop, same fault-isolation shape
    foreach (var group in _liveConfig.Get().Groups)
    {
        try
        {
            await RunGroupBatchAsync(group, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(LogEvents.BatchSchedulerError, ex,
                "Group batch failed for {GroupId}", group.GroupId);
        }
    }
}

internal async Task RunNightlyFitAsync(CancellationToken ct)
{
    foreach (var entity in _liveConfig.Get().Entities) { /* existing, unchanged */ }

    foreach (var group in _liveConfig.Get().Groups)
    {
        // Peer-divergence is stateless (Phase 5: PeerDivergenceDetector has no fit()) — skip Fit entirely.
        if (string.Equals(group.Mode, "peer_divergence", StringComparison.OrdinalIgnoreCase))
            continue;

        try { await RunGroupFitAsync(group, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(LogEvents.BatchSchedulerError, ex,
                "Group nightly fit failed for {GroupId}", group.GroupId);
        }
    }
}
```

### Pattern 5: MQTT group discovery — branch on mode for entity count

**What:** `DiscoveryPublisher` gains group-aware builder methods that branch on `mode`: peer_divergence emits N pairs of (binary_sensor, sensor) — one pair per member; joint emits exactly one pair for the whole group.
**When to use:** Publish cycle after config load/swap and after every batch scoring cycle's state update (GRP-08).
**Example:**
```csharp
// Source: pattern extrapolated from existing BuildBinarySensorConfig/BuildSensorConfig
// (orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs:39-105)
public static string BuildGroupBinarySensorConfig(GroupConfig group, string? memberId = null)
{
    var groupSlug = Slugify(group.GroupId);
    bool isPeer = string.Equals(group.Mode, "peer_divergence", StringComparison.OrdinalIgnoreCase);
    var uniqueId = isPeer
        ? $"argus_group_{groupSlug}_{Slugify(memberId!)}_flag"
        : $"argus_group_{groupSlug}_flag";
    var name = isPeer
        ? $"{group.FriendlyName} {memberId} anomalia"   // Polish friendly-name convention (D8)
        : $"{group.FriendlyName} anomalia";

    var payload = new
    {
        unique_id = uniqueId,
        object_id = uniqueId,
        name,
        state_topic = isPeer
            ? $"argus/group/{groupSlug}/{Slugify(memberId!)}/flag/state"
            : $"argus/group/{groupSlug}/flag/state",
        availability = new object[]
        {
            new { topic = "argus/bridge/availability", payload_available = "online", payload_not_available = "offline" },
        },
        payload_on = "ON",
        payload_off = "OFF",
        device_class = "problem",
        device = new
        {
            identifiers = new[] { $"argus_group_{groupSlug}" },   // ONE device per group_id (not per member)
            name = $"Argus grupa {group.FriendlyName}",
            model = "Argus Anomaly Detector",
            manufacturer = "Argus",
        }
    };
    return JsonSerializer.Serialize(payload, JsonOptions);
}
```
**Critical device-block detail:** unlike per-entity discovery (where `device.identifiers` is the per-entity slug, giving every sensor+score pair its own HA device), group discovery must use ONE shared `device.identifiers` value (`argus_group_{groupSlug}`) across ALL of a group's entities — including all N member pairs in peer mode — so HA's UI groups them together as CONTEXT.md requires ("HA device grouping: all entities of a group are published under one HA device keyed by group_id").

### Anti-Patterns to Avoid

- **Calling `fill(usePrevious:true)` in the group Flux query:** Directly reintroduces forward-filled values into the exact matrix the staleness_cap is meant to protect — the locked decision explicitly forbids scoring stale forward-filled gaps.
- **Using per-entity `device.identifiers` (per-member slug) for group entities:** Breaks the locked "one HA device per group_id" requirement — every member pair would end up as its own separate HA device instead of grouped under the group's device.
- **Calling `FitGroup` for peer-divergence groups:** `PeerDivergenceDetector` is stateless (Phase 5, `05-04-SUMMARY.md`) — the servicer's `FitGroup` handler explicitly skips persistence for `peer_divergence`; calling it from the orchestrator wastes an RPC round-trip and Phase 5's decision docs confirm this is intentionally a no-op on the Python side.
- **Re-validating groups only once at process start:** Because `EntitiesConfigLoader.Load` is re-invoked on every UI save (`Program.cs:412`, `newConfig = EntitiesConfigLoader.Load(...)`), group validation (units/floor/degrade) MUST run inside `Load`/`Validate`, not as a one-time startup-only step, or a UI-driven config change could silently publish a broken group.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Multi-series time alignment across independently-sampled sensors | A custom .NET-side resampling/interpolation loop over raw `Point` lists | InfluxDB's server-side `aggregateWindow`+`pivot` (CONTEXT.md locked decision) | Flux already implements windowed aggregation and column-pivoting correctly and efficiently server-side; a hand-rolled .NET aligner would duplicate this and risk subtle off-by-one window boundary bugs |
| Group membership-change detection for MQTT retraction | Ad hoc diffing of `entities.yaml` groups between saves | A stored hash of each group's `{group_id, members, mode, detector, params}` tuple, compared on each config Swap (mirrors the existing per-entity retraction trigger pattern already implied by `MqttRetractionTests`/`DiscoveryPublisher.RetractAsync`) | The existing v3.0 retraction pattern already solves "detect what was removed, retract only those" — reimplementing diff logic risks missing the "retract BEFORE publish new set" ordering requirement |
| Robust group statistics (median/MAD) for peer-divergence | Any new .NET-side statistics code | Nothing — this is entirely Phase 5's `PeerDivergenceDetector` (Python), already built and tested | Phase 6 only transports data to/from the existing RPC; no statistics logic belongs in the orchestrator |

**Key insight:** Every "hard" algorithmic problem in this domain (time-series alignment compute, robust statistics, multivariate scaling/scoring) is already solved by either InfluxDB's Flux engine or Phase 5's Python detectors. Phase 6's only genuine engineering judgment call is the staleness-cap exclusion policy, which is a data-plumbing decision, not an algorithm to invent.

## Runtime State Inventory

> Not applicable — this is not a rename/refactor/migration phase. It adds new config schema (`groups:`) and new code paths; existing `entities:` config and per-entity runtime state (MQTT topics, model files, InfluxDB data) are untouched.

## Common Pitfalls

### Pitfall 1: Config-load-time unit lookup races the HA WebSocket connection

**What goes wrong:** The locked decision says "Units sourced from HA entity state attributes... cached at config-load." But `EntitiesConfigLoader.Load(entitiesPath, entitiesLogger)` runs synchronously at `Program.cs:22` — **before** `builder.Services.AddSingleton<IHaSensorRegistry, HaSensorRegistry>()` is even registered (`Program.cs:88`), and long before `NetDaemonHaEventSource`'s hosted-service `get_states` call populates the registry after `app.Run()`. At the very first cold boot, `IHaSensorRegistry.GetAll()` is empty — there is no unit data available yet.
**Why it happens:** `EntitiesConfigLoader.Load` is called synchronously during DI container construction (before `builder.Build()`), a full application-lifecycle phase before any background service starts.
**How to avoid:** Two config-load call sites already exist and behave differently:
  1. **Initial boot** (`Program.cs:22`): `IHaSensorRegistry` cannot be consulted (not yet DI-registered, and even if it were, it's unpopulated). Group unit validation must degrade gracefully here — e.g. skip the unit check with a "units unknown at boot, will validate on next config load" warning, OR treat unresolvable units as "unknown, permit provisionally" until the next reload.
  2. **UI-save reload** (`Program.cs:412`, inside the `/api/sensors/save` handler): `IHaSensorRegistry` is fully wired and populated (assuming HA WebSocket connected) — full unit validation is possible and should be enforced here.
  The planner must decide: either (a) change `EntitiesConfigLoader.Load`'s signature to optionally accept an `IHaSensorRegistry` (nullable/null at boot, populated at reload), or (b) move group-unit-validation out of `EntitiesConfigLoader.Validate()` into a separate step invoked only where a populated registry is available. Recommend (a) with a null-safe null-registry parameter, degrading unit checks to warn-only until registry data exists.
**Warning signs:** A group is rejected at first boot every time (registry always empty) even though the operator's entities.yaml is valid — this is the bug this pitfall describes if not handled.

### Pitfall 2: `aggregateWindow`'s `createEmpty` interacts inconsistently with selector vs. aggregate functions

**What goes wrong:** `createEmpty: true` reliably produces null-filled empty windows for aggregate functions like `mean`, but the same flag has documented inconsistent behavior with selector functions (`last`, `min`, `max`) — GitHub issue `influxdata/flux#3428` confirms `last`/`min`/`max` do NOT produce empty windows even with `createEmpty: true` [CITED: github.com/influxdata/flux/issues/3428].
**Why it happens:** Flux's window-emptiness semantics differ between "true aggregation" (produces one value per window unconditionally when `createEmpty:true`) and "selector" functions (only emit when there is a real point to select).
**How to avoid:** The default `fn=mean` (CONTEXT.md's stated default) is safe — `mean` is a true aggregate and behaves as expected with `createEmpty:true`. If the group config's `fn` param is ever set to `last`/`min`/`max` (the CONTEXT.md schema allows `fn` to be configurable), the null-based gap-detection in Pattern 2 will NOT reliably produce nulls for genuinely-missing windows — validate at config-load time that `fn` is restricted to `mean` (or another true-aggregate function) if the staleness-null-detection design is adopted, or add an explicit validation warning for `last`/`min`/`max`.
**Warning signs:** A stale member's gap silently fails to produce `null` cells and the member gets scored as if fresh, defeating the staleness cap.

### Pitfall 3: Joint-multivariate scoring requires a complete feature vector per row — a single stale member can't just be "dropped" mid-cycle

**What goes wrong:** For peer-divergence, dropping one stale member from the matrix for this cycle is safe (the remaining members still form a valid, if smaller, comparison group — subject to the min-member floor of 3). For joint-multivariate (PCA/ECOD/COPOD/IForest), the detector was `Fit` on a FIXED feature-column set (Phase 5, `GroupMultivariateDetector`); silently dropping a stale column would change the feature vector's dimensionality and either crash the PyOD model or (worse) silently misalign columns against the fitted scaler's per-feature stats.
**Why it happens:** Peer-divergence recomputes its statistic fresh every call (stateless); joint-multivariate has a persisted model keyed to a specific member set and column order.
**How to avoid:** For joint-multivariate groups, treat ANY member's staleness-cap breach as "skip scoring the entire group this cycle" (log a warning), not a per-member exclusion. Only peer-divergence should support per-member drop-and-continue.
**Warning signs:** Detector RPC returns a dimension-mismatch error, or (more dangerously) succeeds silently but produces meaningless scores because column order shifted.

### Pitfall 4: Group Flux filter with many members creates a long `or`-chain that risks a Flux parser or query-length limit

**What goes wrong:** The per-entity query filters on a single `entity_id`; the group query (Pattern 2) filters on `r["entity_id"] == "m1" or r["entity_id"] == "m2" or ...` for potentially many members. Very large groups (tens of members) produce long generated Flux strings.
**Why it happens:** No native "IN" list operator is used in the naive filter-chain approach shown in Pattern 2.
**How to avoid:** Prefer `contains(value: r["entity_id"], set: ["m1","m2",...])` — Flux's built-in `contains()` function against an array — over an `or`-chain; it is both shorter and avoids any parser edge cases with very long boolean expressions. Still apply the existing `_safeFluxString` allowlist-regex validation (T-02-02-02, `InfluxDbReader.cs:18-19`) to every member entity_id before interpolating it into the array literal, exactly as the per-entity query already validates `entityId`/`InfluxBucket`/`InfluxMeasurement`/`InfluxValueField`.
**Warning signs:** Groups with many members either fail to query or take conspicuously longer than smaller groups.

### Pitfall 5: Retraction ordering — must retract BEFORE publishing new discovery configs, and per-member topics for peer groups need per-member hash granularity

**What goes wrong:** CONTEXT.md locks "retract removed members... BEFORE publishing the new set." For peer-divergence groups, a membership change (e.g. group shrinks from 4 members to 3) means SOME per-member topics must be retracted while OTHERS for the same group_id continue publishing. A single group-level hash comparison (old membership set vs new) is sufficient to compute the diff, but the retraction call must iterate only the removed members' topics — not blanket-retract the whole group and republish everything (which would cause a visible flicker/unavailable gap in HA for members that didn't change).
**Why it happens:** It's tempting to treat "group changed" as a single boolean and retract-then-republish the entire group; this is unnecessarily disruptive for large peer groups where only one member was removed.
**How to avoid:** Compute `removedMembers = oldMembers.Except(newMembers)` per group_id (or, if the group_id itself was removed entirely, all its members) and retract only those topics — mirroring the exact granularity of the existing `RetractAsync(IEnumerable<EntityConfig> removedEntities, ...)` which already retracts precisely the passed set and nothing else (`MqttRetractionTests.RetractAsync_OnlyRetractsPassedEntities_NotOthers`).
**Warning signs:** Members that were never removed briefly go "unavailable" in HA after any group config edit — a sign the whole group was blanket-retracted instead of a precise diff.

## Code Examples

### Building a `GroupScoreRequest` (mirrors existing `ScoreBatchRequest` construction)

```csharp
// Source: pattern extrapolated from BatchSchedulerWorker.BuildScoreBatchRequest
// (orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs:183-208)
// combined with the Series message shape from proto/argus.proto:74-77
private static GroupScoreRequest BuildGroupScoreRequest(
    GroupConfig group,
    IReadOnlyDictionary<string, List<double>> memberSeries) // pre-aligned, gaps excluded upstream
{
    var request = new GroupScoreRequest
    {
        GroupId = group.GroupId,
        Detector = group.Detector,   // "peer_divergence" | "ecod" | "copod" | "pca" | "iforest"
    };

    foreach (var (key, value) in group.Params)
        request.Params[key] = value;

    foreach (var (memberId, values) in memberSeries)
    {
        var series = new Series { MemberId = memberId };
        series.Values.AddRange(values);   // RepeatedField<double>.AddRange — standard protobuf-net pattern
        request.Series.Add(series);
    }

    return request;
}
```

### Adding group RPC methods to the detector-client abstraction

```csharp
// Source: pattern extrapolated from IBatchDetectorClient / BatchDetectorClientAdapter
// (orchestrator/Argus.Orchestrator/Batch/IBatchDetectorClient.cs,
//  orchestrator/Argus.Orchestrator/Batch/BatchDetectorClientAdapter.cs)
public interface IBatchDetectorClient
{
    Task<ScoreBatchResponse> ScoreBatchAsync(ScoreBatchRequest request, CancellationToken ct);
    Task<FitResponse> FitAsync(FitRequest request, CancellationToken ct);

    // NEW — Phase 6
    Task<GroupScoreResponse> ScoreGroupBatchAsync(GroupScoreRequest request, CancellationToken ct);
    Task<FitGroupResponse> FitGroupAsync(FitGroupRequest request, CancellationToken ct);
}

public sealed class BatchDetectorClientAdapter : IBatchDetectorClient
{
    // ...existing ScoreBatchAsync/FitAsync unchanged...

    public async Task<GroupScoreResponse> ScoreGroupBatchAsync(GroupScoreRequest request, CancellationToken ct)
    {
        var call = _gateway.DetectorClient.ScoreGroupBatchAsync(request, cancellationToken: ct);
        return await call.ResponseAsync;
    }

    public async Task<FitGroupResponse> FitGroupAsync(FitGroupRequest request, CancellationToken ct)
    {
        var call = _gateway.DetectorClient.FitGroupAsync(request, cancellationToken: ct);
        return await call.ResponseAsync;
    }
}
```
`_gateway.DetectorClient` is already the generated `DetectorService.DetectorServiceClient` — `ScoreGroupBatchAsync`/`FitGroupAsync` methods on it are auto-generated by the MSBuild `<Protobuf>` build step from `proto/argus.proto`'s `service DetectorService` block (Phase 5 already added both RPCs there), so no manual stub-writing is needed [VERIFIED: existing codebase, `proto/argus.proto:111-119`].

### Publishing per-member Verdict data for peer-divergence mode

```csharp
// Source: pattern extrapolated from RunEntityBatchAsync's "publish only the last verdict"
// convention (orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs:169-180)
if (response.Ok && string.Equals(group.Mode, "peer_divergence", StringComparison.OrdinalIgnoreCase))
{
    foreach (var verdict in response.PerMember)
    {
        await _statePublisher.PublishScoreAsync(
            $"group.{group.GroupId}.{verdict.EntityId}", verdict.Score ?? 0.0, ct);
        await _statePublisher.PublishFlagAsync(
            $"group.{group.GroupId}.{verdict.EntityId}", verdict.IsAnomaly, ct);
    }
}
else if (response.Ok) // joint-multivariate
{
    var v = response.GroupVerdict;
    await _statePublisher.PublishScoreAsync($"group.{group.GroupId}", v.Score ?? 0.0, ct);
    await _statePublisher.PublishFlagAsync($"group.{group.GroupId}", v.IsAnomaly, ct);
}
```
**Note:** `IStatePublisher`'s existing `FlagTopic`/`ScoreTopic` methods derive MQTT topics from `UniqueId.Slug(entityId)` — group topics need a distinct topic namespace (`argus/group/{slug}/...` per CONTEXT.md's device-block section) rather than reusing the per-entity `argus/{slug}/...` prefix, since a group's synthetic "entity id" (e.g. `group.{group_id}.{member}`) is not a real HA entity_id and would collide with `UniqueId.Slug`'s `.`→`_` replacement in confusing ways. The planner should add group-specific topic-building methods to `StatePublisher`/`IStatePublisher` (e.g. `GroupFlagTopic(groupId, memberId?)`) rather than overloading the existing per-entity ones with synthetic entity-id strings.

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|---------------|--------|
| Per-entity-only batch scoring (`ScoreBatchRequest`/`ScoreBatchResponse`) | Group-aware batch scoring (`GroupScoreRequest`/`GroupScoreResponse`) alongside the unchanged per-entity path | Phase 5 (2026-07-02) added the proto contract; Phase 6 wires it into the orchestrator | The orchestrator's batch loop gains a second, parallel code path — the two paths (entity vs group) never merge, they coexist |

**Deprecated/outdated:**
- `EntityConfig.Covariates`/`EntityConfig.Groups` (the old per-entity placeholder fields, `EntitiesConfig.cs:18-21`): explicitly retired per CONTEXT.md and `REQUIREMENTS.md`'s Out of Scope table ("Populating the old EntityConfig.Groups/Covariates placeholders... wrong shape; retired in favor of a group-centric top-level EntitiesConfig.Groups list"). These fields and their `WarnIgnoredKeys` warning logic in `EntitiesConfigLoader.cs:65-77` should be removed as part of this phase's config-schema work, not left dangling alongside the new top-level `Groups` list — but confirm with the planner whether removal happens in Phase 6 or is explicitly deferred, since REQUIREMENTS.md frames this as an intentional Phase 6 retirement.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `aggregateWindow(fn: mean, createEmpty: true)` on a raw (non-`fill`'d) series produces Flux `null` for windows with zero underlying points, and `pivot()` propagates that as `null` in `FluxRecord.Values` for the corresponding member column — the mechanism this research recommends for staleness detection | Architecture Patterns (Pattern 2, 3), Common Pitfalls (Pitfall 2) | If wrong (e.g. `createEmpty` + `mean` behaves differently than documented, or `pivot` drops the row instead of nulling the cell), the staleness-cap exclusion logic silently fails to exclude stale data — the exact failure mode CONTEXT.md's GRP-02 decision is meant to prevent. **Must be verified against a live InfluxDB instance with a real stale-sensor fixture before this becomes the shipped implementation** — recommend a `checkpoint:human-verify` or an integration test against a real (or InfluxDB-in-Docker) instance early in the plan. |
| A2 | `EntitiesConfigLoader.Load` at initial process boot (`Program.cs:22`) genuinely cannot consult `IHaSensorRegistry` for units (registry not yet DI-registered/populated) — verified by reading `Program.cs` line ordering, but the exact behavior of `NetDaemonHaEventSource`'s startup sequence (how soon after `app.Run()` the registry is first populated) was not traced end-to-end in this research session | Common Pitfalls (Pitfall 1) | If the registry is actually populated faster than assumed (e.g. some other startup path pre-populates it), the "degrade unit-check to warn-only at boot" design in Pitfall 1 may be overly conservative — not harmful, but possibly more permissive than necessary at first boot. Low risk either way since the locked decision already requires degrade-not-crash. |
| A3 | Flux's `contains(value: r["entity_id"], set: [...])` is preferable to an `or`-chain for filtering multiple member entity_ids in the group query, based on general Flux idiom knowledge rather than this session's live verification against InfluxDB.Client 5.0.0/InfluxDB 2.x specifically | Common Pitfalls (Pitfall 4) | Low risk — even if `contains()` has some edge case, the `or`-chain fallback shown in Pattern 2 is a safe, verified-workable alternative; worst case the plan uses the longer but functionally correct `or`-chain. |

## Open Questions

1. **Exact `staleness_cap` exclusion granularity: per-row or per-member-for-the-cycle?**
   - What we know: CONTEXT.md says "any pivot timestamp where a member's underlying value is older than the cap... is EXCLUDED from scoring." Taken literally, this is a per-row-per-member exclusion (drop that one cell/row).
   - What's unclear: For joint-multivariate detectors (Pitfall 3), per-row-per-member exclusion is incompatible with a fixed-dimensionality feature vector — you can't drop one member's value for one row without either dropping the whole row (all members) or the whole member (all rows) for that cycle.
   - Recommendation: For peer-divergence, exclude individual (timestamp, member) cells — the remaining members at that timestamp still form a valid (if smaller) row, subject to the ≥3-member floor being re-checked per-row. For joint-multivariate, exclude entire timestamp ROWS where ANY member is stale (safer and simpler than dropping columns) — this keeps the feature-vector dimensionality fixed at every scored row. This is a design decision the planner should make explicit and test.

2. **Does `EntitiesConfigLoader.Validate()` need a registry parameter, or should group-unit-validation move to a separate call site?**
   - What we know: Pitfall 1 establishes that the registry is unavailable at first boot but available at UI-save reload.
   - What's unclear: Whether the planner prefers to thread an `IHaSensorRegistry?` (nullable) through `EntitiesConfigLoader.Load`/`Validate`, or extract group-unit-validation into a distinct method called only from the reload path (leaving first-boot validation to skip units entirely and rely on the ≥3-member floor + detector-name checks only).
   - Recommendation: Thread a nullable `IHaSensorRegistry?` parameter through — keeps ALL group validation logic in one place (`Validate()`), with the unit-check internally short-circuiting to "skip, log info" when the registry is null or returns no matching units for a member. Simpler mental model than splitting validation across two call sites.

3. **Should `EntityConfig.Covariates`/`Groups` placeholder fields be deleted in this phase?**
   - What we know: REQUIREMENTS.md's Out of Scope table explicitly frames the old placeholders as "retired in favor of" the new top-level list — implying deletion is in scope.
   - What's unclear: Whether deleting them (and the associated `WarnIgnoredKeys` logic + `EntitiesConfigTests.Load_EntityWithCovariates_ParsesSuccessfullyAndLogsWarning` test) is this phase's job, or whether it's safer to leave the dead fields in place (still ignored) to avoid breaking any operator's existing YAML that happens to have stray `covariates`/`groups` keys under an entity.
   - Recommendation: Delete them as part of this phase — `IgnoreUnmatchedProperties()` on the deserializer means even if an operator's YAML retains stray per-entity `covariates`/`groups` keys after the C# properties are removed, deserialization won't fail (unmatched properties are silently ignored), so there's no compatibility risk in removing the dead C# properties. Update/remove the now-obsolete `WarnIgnoredKeys` method and its test accordingly.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| InfluxDB 2.x instance | GRP-02 group Flux queries | Not probed this session (research is code-analysis only, no live infra access) | — | Existing per-entity `InfluxDbReader` already handles `InfluxUrl`/`InfluxBucket` absence by returning an empty result set without throwing (`InfluxDbReaderTests.cs`) — the group reader should mirror this same graceful-empty-on-missing-config behavior |
| HA WebSocket / Supervisor proxy | Unit-of-measurement lookup at config-load (Pitfall 1) | Not probed this session | — | Degrade-not-crash: proceed without unit validation when HA/registry is unreachable (see Open Question 2) |
| Python detector (gRPC) with Phase 5 RPCs | ScoreGroupBatch/FitGroup calls | Confirmed present in code (Phase 5 complete, `05-04-SUMMARY.md`: "Full detector test suite (183 tests) passes with zero regressions") | Phase 5, complete 2026-07-02 | None needed — dependency is satisfied by prior phase completion |

**Missing dependencies with no fallback:** None identified — every external dependency this phase touches already has an existing graceful-degradation precedent in the codebase (InfluxDB absence, HA registry emptiness, detector health-gate backoff).

**Missing dependencies with fallback:** InfluxDB reachability and HA WebSocket reachability at any given moment — both already have established fallback/degrade patterns in the existing codebase that this phase's group code should replicate rather than reinvent.

## Security Domain

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | No | No new auth surface — this phase adds no HTTP endpoints (Phase 8 owns the group config UI) |
| V3 Session Management | No | Not applicable — no session state introduced |
| V4 Access Control | No | Not applicable — no new access-controlled resource |
| V5 Input Validation | Yes | Group member entity_ids and config-derived strings interpolated into Flux queries must pass the same `_safeFluxString` allowlist regex (`^[^"\\]+$`) already enforced in `InfluxDbReader.cs:18-19` for per-entity queries — extend this validation to every member id, `InfluxBucket`, `InfluxMeasurement`, `InfluxValueField` used in the new group query methods (T-02-02-02 precedent) |
| V6 Cryptography | No | No new cryptographic operations — gRPC transport security (mTLS/loopback) is unchanged and already covered by Phase 1-4's `DetectorChannelFactory` |

### Known Threat Patterns for .NET/InfluxDB/gRPC/MQTT stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Flux string-literal injection via unsanitized member entity_id, group_id, or param values interpolated into the group query string | Tampering | Reuse the existing `_safeFluxString` regex guard (reject values containing `"` or `\`) for every string interpolated into the new group Flux queries — group_id and member entity_ids are operator-controlled via `entities.yaml`, same trust boundary as the existing per-entity query, so the same validation applies |
| A malformed/adversarial `groups:` YAML entry (e.g. extremely long `members` list, deeply nested `params`) causing excessive Flux query length or memory use | Denial of Service | The existing `IgnoreUnmatchedProperties()` + degrade-not-crash config-load pattern already bounds the blast radius of malformed config to "this group is skipped, others still work" — no additional new mitigation needed beyond applying the same pattern to group validation |
| MQTT discovery topic collision between a group's synthetic per-member state topic and a real per-entity topic | Spoofing (entity confusion in HA) | Use a distinct topic namespace prefix (`argus/group/{slug}/...`) for all group-related MQTT topics, never reusing the per-entity `argus/{slug}/...` prefix — prevents a group entity from ever colliding with (or being confused for) a real per-entity topic, per the Code Examples section's `IStatePublisher` topic-naming note |

## Sources

### Primary (HIGH confidence)
- `orchestrator/Argus.Orchestrator/**/*.cs` (existing codebase, read directly this session) — `EntitiesConfig.cs`, `EntitiesConfigLoader.cs`, `LiveEntitiesConfig.cs`, `InputValidator.cs`, `GlobExpander.cs`, `BatchSchedulerWorker.cs`, `InfluxDbReader.cs`, `IInfluxDataSource.cs`, `IInfluxQueryApi.cs`, `InfluxQueryApiAdapter.cs`, `BatchDetectorClientAdapter.cs`, `IBatchDetectorClient.cs`, `DiscoveryPublisher.cs`, `StatePublisher.cs`, `UniqueId.cs`, `FriendlyName.cs`, `MqttConnection.cs`, `IStatePublisher.cs`, `HaSensorRegistry.cs`, `IHaSensorRegistry.cs`, `HaWebSocketClient.cs`, `ConnectionSettings.cs`, `Program.cs`, `DetectionGateway.cs`, `Argus.Orchestrator.csproj`
- `orchestrator/Argus.Orchestrator.Tests/*.cs` — `BatchSchedulerWorkerTests.cs`, `InfluxDbReaderTests.cs`, `MqttRetractionTests.cs`, `EntitiesConfigTests.cs`
- `proto/argus.proto` — full proto contract including Phase 5's `Series`, `GroupScoreRequest/Response`, `FitGroupRequest/Response`, `FeatureContribution`, `ScoreGroupBatch`/`FitGroup` RPCs
- `.planning/phases/05-group-detection-core-proto-python-detectors/05-0{1,2,3,4}-SUMMARY.md` — Phase 5 implementation decisions, key patterns, and explicit "ready for Phase 6" handoff notes
- `.planning/phases/06-batch-group-pipeline/06-CONTEXT.md` — all locked decisions
- `.planning/REQUIREMENTS.md` — GRP-01/02/08 definitions and Out of Scope table
- `raw.githubusercontent.com/influxdata/influxdb-client-csharp/master/Client.Core/Flux/Domain/FluxRecord.cs` — fetched and read this session, confirms `GetValueByKey`/`GetValue`/`GetTime`/`Values` public API surface [VERIFIED: source file fetched directly]

### Secondary (MEDIUM confidence)
- [docs.influxdata.com/flux/v0/stdlib/universe/pivot/](https://docs.influxdata.com/flux/v0/stdlib/universe/pivot/) — confirms pivot() produces `null` (not a missing row) for unmatched rowKey/columnKey combinations [CITED]
- [home-assistant.io/integrations/influxdb](https://www.home-assistant.io/integrations/influxdb) (via WebSearch synthesis) — confirms HA's InfluxDB integration writes only on `state_changed`, no source-side forward-fill [CITED, via WebSearch summary, not the page itself directly fetched]
- [github.com/influxdata/flux/issues/3428](https://github.com/influxdata/flux/issues/3428) — confirms `createEmpty:true` inconsistency between aggregate (`mean`) and selector (`last`/`min`/`max`) functions [CITED, via WebSearch summary]
- [docs.influxdata.com/flux/v0/stdlib/universe/aggregatewindow/](https://docs.influxdata.com/flux/v0/stdlib/universe/aggregatewindow/) — `createEmpty` parameter general behavior [CITED]

### Tertiary (LOW confidence)
- General Flux/protobuf/gRPC idiom knowledge for `contains()` array-filtering and `RepeatedField<T>.AddRange` — standard, well-established patterns but not independently re-verified against InfluxDB.Client 5.0.0/Grpc.Net.Client 2.80.0 specifically in this session; flagged as A3 in Assumptions Log.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — zero new packages, everything already pinned and in use in the exact same codebase
- Architecture: HIGH for the gRPC/MQTT/config wiring (directly mirrors existing tested code); MEDIUM for the Flux staleness-cap query design (recommended approach is sound per official Flux docs + HA integration behavior, but not verified against a live InfluxDB instance in this session — see Assumption A1)
- Pitfalls: HIGH for the config-load timing issue (Pitfall 1 — directly traced through `Program.cs` line-by-line); MEDIUM for the Flux `createEmpty`/selector-function inconsistency (Pitfall 2 — sourced from an InfluxDB GitHub issue, not independently reproduced)

**Research date:** 2026-07-02
**Valid until:** 2026-08-01 (30 days — stable, pinned-dependency stack; InfluxDB Flux semantics are unlikely to change but the staleness-cap design should be validated against a live instance regardless of research currency)
