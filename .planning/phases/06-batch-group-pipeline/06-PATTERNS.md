# Phase 6: Batch Group Pipeline - Pattern Map

**Mapped:** 2026-07-02
**Files analyzed:** 11 (new + modified)
**Analogs found:** 11 / 11

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `Config/EntitiesConfig.cs` (add `GroupConfig`, `Groups` list) | model | CRUD (config) | `Config/EntitiesConfig.cs` (same file, `EntityConfig`) | exact |
| `Config/EntitiesConfigLoader.cs` (add `ValidateGroups`) | config/validation | request-response (load) | `Config/EntitiesConfigLoader.cs` (same file, `Validate`) | exact |
| `Batch/GroupInfluxReader.cs` + `IGroupInfluxDataSource.cs` (new) | service | batch/transform (Flux query) | `Batch/InfluxDbReader.cs` + `IInfluxDataSource.cs` | role-match (per-entity → per-group query) |
| `Batch/IBatchDetectorClient.cs` (add group methods) | service (gRPC client) | request-response | `Batch/IBatchDetectorClient.cs` (same file) | exact |
| `Batch/BatchDetectorClientAdapter.cs` (add `ScoreGroupBatchAsync`/`FitGroupAsync`) | service (gRPC client) | request-response | `Batch/BatchDetectorClientAdapter.cs` (same file) | exact |
| `Batch/BatchSchedulerWorker.cs` (add group loop in `RunBatchAsync`/`RunNightlyFitAsync`) | service/worker | event-driven (timer loop) + CRUD | `Batch/BatchSchedulerWorker.cs` (same file, entity loop) | exact |
| `Mqtt/DiscoveryPublisher.cs` (add `BuildGroupBinarySensorConfig`/`BuildGroupSensorConfig`/group `RetractAsync`) | service (MQTT publish) | pub-sub | `Mqtt/DiscoveryPublisher.cs` (same file, entity builders) | exact |
| `Mqtt/UniqueId.cs` (add `GroupFlagId`/`GroupScoreId`) | utility | transform | `Mqtt/UniqueId.cs` (same file) | exact |
| `Mqtt/StatePublisher.cs` / `IStatePublisher.cs` (add group topic helpers) | service (MQTT publish) | pub-sub | `Mqtt/StatePublisher.cs` (same file) | exact |
| Tests: `GroupInfluxReaderTests.cs` (new) | test | — | `Tests/InfluxDbReaderTests.cs` | exact |
| Tests: `BatchSchedulerWorkerTests.cs` (extend), `MqttRetractionTests.cs` (extend), `EntitiesConfigTests.cs` (extend) | test | — | same files (existing test classes) | exact |

## Pattern Assignments

### `Config/EntitiesConfig.cs` — add `GroupConfig` + `Groups` list

**Analog:** same file, `EntityConfig`/`DetectorConfig` (lines 6-28)

**Core model pattern** (lines 6-28):
```csharp
public class EntitiesConfig
{
    public List<EntityConfig> Entities { get; set; } = new();
}

public class EntityConfig
{
    public string EntityId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public List<DetectorConfig> Detectors { get; set; } = new();
    public object? Covariates { get; set; }   // RETIRE per CONTEXT.md
    public object? Groups { get; set; }        // RETIRE per CONTEXT.md
}

public class DetectorConfig
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Params { get; set; } = new();
}
```
**Apply as:** Add `public List<GroupConfig> Groups { get; set; } = new();` to `EntitiesConfig` (top-level, sibling to `Entities`). Add new `GroupConfig` class following the exact same plain-property/`UnderscoredNamingConvention` shape: `GroupId`, `FriendlyName`, `Members` (`List<string>`), `Mode`, `Detector`, `Params` (`Dictionary<string,string>`). Remove `EntityConfig.Covariates`/`Groups` placeholders and their warning logic (`WarnIgnoredKeys` in loader) — confirmed retirement per REQUIREMENTS.md Out-of-Scope table; `IgnoreUnmatchedProperties()` makes this safe for existing YAML.

---

### `Config/EntitiesConfigLoader.cs` — add group validation

**Analog:** same file, `Load`/`Validate` (lines 14-63)

**Load + Validate-before-Swap pattern** (lines 14-37, 39-63):
```csharp
public static EntitiesConfig Load(string path, ILogger logger)
{
    var yaml = File.ReadAllText(path);
    var deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
    var config = deserializer.Deserialize<EntitiesConfig>(yaml) ?? new EntitiesConfig();
    Validate(config, path, logger);
    return config;
}

private static void Validate(EntitiesConfig config, string path, ILogger logger)
{
    foreach (var entity in config.Entities)
    {
        if (entity is null) throw new InvalidOperationException(...);
        if (string.IsNullOrWhiteSpace(entity.EntityId)) throw new InvalidOperationException(...);
        if (entity.Detectors == null || entity.Detectors.Count == 0) throw new InvalidOperationException(...);
    }
}
```
**Critical divergence for groups (per CONTEXT.md "degrade-not-crash"):** unlike entity validation, which `throw`s on bad config (crashes the whole load), group validation must **skip-and-warn per group**, never throw — a bad group must not block valid entities/groups from loading. Pattern to follow instead is the existing `EmptyEntitiesWarning` branch (lines 41-47, `logger.LogWarning` + `return`, no throw). Add `ValidateGroups(config, path, logger, IHaSensorRegistry? registry)`:
- reject (log warning, remove from list) if `Members.Count < 3` (floor)
- reject if `Mode`/`Detector` unrecognized
- for `peer_divergence`: reject if resolved `unit_of_measurement` differs across members (registry may be null/unpopulated at boot — degrade to skip-check-only with an info log, per Pitfall 1)
- for `joint`: no unit check
Use `LogEvents`-style structured logging (see `Logging/LogEvents.cs` constants already used, e.g. `LogEvents.EmptyEntitiesWarning`) — add new `LogEvents.GroupRejected`/`GroupConfigLoaded` constants following the same numbering convention.

---

### `Batch/GroupInfluxReader.cs` + `IGroupInfluxDataSource.cs` (new)

**Analog:** `Batch/InfluxDbReader.cs` + `Batch/IInfluxDataSource.cs` (full file, 108 lines)

**Guard + Flux-injection-safety pattern** (lines 15-19, 52-77):
```csharp
private static readonly Regex _safeFluxString = new(@"^[^""\\]+$", RegexOptions.Compiled);
// ...
if (string.IsNullOrEmpty(_settings.InfluxUrl)) { LogWarning(...); return Array.Empty<...>(); }
if (string.IsNullOrEmpty(_settings.InfluxBucket)) { LogWarning(...); return Array.Empty<...>(); }
if (!_safeFluxString.IsMatch(entityId)) throw new ArgumentException(...);
// ... same checks for InfluxBucket/InfluxMeasurement/InfluxValueField
```
**Query + parse pattern** (lines 79-106):
```csharp
var flux = $"""
    from(bucket: "{_settings.InfluxBucket}")
      |> range(start: -24h)
      |> filter(fn: (r) => r["_measurement"] == "{_settings.InfluxMeasurement}"
            and r["entity_id"] == "{entityId}"
            and r["_field"] == "{_settings.InfluxValueField}")
      |> sort(columns: ["_time"])
    """;
var tables = await _queryApi.QueryAsync(flux, _settings.InfluxOrg, ct);
var points = tables.SelectMany(t => t.Records)
    .Select(r => (Timestamp: r.GetTime()!.Value.ToDateTimeUtc(), Value: Convert.ToDouble(r.GetValue())))
    .ToList();
if (points.Count == 0) { LogWarning(...); return Array.Empty<...>(); }
return points;
```
**Apply as:** New `GroupInfluxReader` implementing new `IGroupInfluxDataSource` (mirrors `IInfluxDataSource` split-by-concern — `InfluxDbReader` stays untouched). Constructor pattern identical (production ctor wraps `InfluxDBClient`; testable ctor accepts `IInfluxQueryApi` — reuse the SAME `IInfluxQueryApi`/`InfluxQueryApiAdapter` abstraction, no new query-API type needed). Add `aggregateWindow(every, fn, createEmpty: true) |> pivot(rowKey: ["_time"], columnKey: ["entity_id"], valueColumn: "_value")` to the Flux string per RESEARCH.md Pattern 2/3. Validate every member id through `_safeFluxString` (same regex) before interpolating into the filter — either an `or`-chain (mirrors existing single-`entity_id` check) or `contains(value: r["entity_id"], set: [...])` (RESEARCH.md recommendation). Reuse `Convert.ToDouble` for value coercion (Pitfall 6 precedent) and the same "return empty on missing config, never throw" guard style.

---

### `Batch/IBatchDetectorClient.cs` + `Batch/BatchDetectorClientAdapter.cs` — add group RPCs

**Analog:** same files, full content (14 + 31 lines)

**Interface pattern:**
```csharp
public interface IBatchDetectorClient
{
    Task<ScoreBatchResponse> ScoreBatchAsync(ScoreBatchRequest request, CancellationToken ct);
    Task<FitResponse> FitAsync(FitRequest request, CancellationToken ct);
}
```
**Adapter pattern:**
```csharp
public sealed class BatchDetectorClientAdapter : IBatchDetectorClient
{
    private readonly DetectionGateway _gateway;
    public BatchDetectorClientAdapter(DetectionGateway gateway)
        => _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));

    public async Task<ScoreBatchResponse> ScoreBatchAsync(ScoreBatchRequest request, CancellationToken ct)
    {
        var call = _gateway.DetectorClient.ScoreBatchAsync(request, cancellationToken: ct);
        return await call.ResponseAsync;
    }
    // FitAsync mirrors the same shape
}
```
**Apply as:** Add `Task<GroupScoreResponse> ScoreGroupBatchAsync(GroupScoreRequest, CancellationToken)` and `Task<FitGroupResponse> FitGroupAsync(FitGroupRequest, CancellationToken)` to the interface; implement in the adapter with the identical `_gateway.DetectorClient.<Rpc>Async(request, cancellationToken: ct); return await call.ResponseAsync;` one-liner shape. No new constructor logic needed — `_gateway.DetectorClient` already exposes the Phase-5-generated stubs.

---

### `Batch/BatchSchedulerWorker.cs` — add group loop

**Analog:** same file, `RunBatchAsync`/`RunEntityBatchAsync`/`RunNightlyFitAsync` (lines 125-281)

**Fault-isolation loop pattern** (lines 125-145):
```csharp
internal async Task RunBatchAsync(CancellationToken ct)
{
    foreach (var entity in _liveConfig.Get().Entities)
    {
        foreach (var detectorCfg in entity.Detectors)
        {
            try { await RunEntityBatchAsync(entity.EntityId, detectorCfg, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogError(LogEvents.BatchSchedulerError, ex, "..."); }
        }
    }
}
```
**Publish-last-verdict pattern** (lines 169-180):
```csharp
if (response.Verdicts.Count > 0)
{
    var last = response.Verdicts[^1];
    await _statePublisher.PublishScoreAsync(entityId, last.Score ?? 0.0, ct);
    await _statePublisher.PublishFlagAsync(entityId, last.IsAnomaly, ct);
}
```
**Nightly fit skip-flag pattern** (lines 212-254) — reuse structurally; add mode-branch to skip `Fit` for `peer_divergence` (RESEARCH.md Pattern 4, `PeerDivergenceDetector` is stateless).

**Apply as:** Add a second `foreach (var group in _liveConfig.Get().Groups)` loop AFTER the entity loop in both `RunBatchAsync` and `RunNightlyFitAsync`, with the exact same per-item try/catch + `OperationCanceledException` rethrow. New private `RunGroupBatchAsync(GroupConfig, ct)` and `RunGroupFitAsync(GroupConfig, ct)` methods, calling `GroupInfluxReader.QueryGroupAsync` → `BuildGroupScoreRequest` → `_detectorClient.ScoreGroupBatchAsync`/`FitGroupAsync` → branch on `group.Mode` for per-member vs single-group publish (RESEARCH.md Code Examples "Publishing per-member Verdict data"). Skip `RunGroupFitAsync` entirely when `group.Mode == "peer_divergence"` (`continue` before the try, exactly as shown in RESEARCH.md Pattern 4).

---

### `Mqtt/DiscoveryPublisher.cs` — group discovery + retraction

**Analog:** same file, `BuildBinarySensorConfig`/`BuildSensorConfig`/`RetractAsync` (lines 39-105, 148-188)

**Discovery payload pattern** (lines 39-71):
```csharp
public static string BuildBinarySensorConfig(EntityConfig entity)
{
    var slug = UniqueId.Slug(entity.EntityId);
    var uniqueId = UniqueId.AnomalyId(entity.EntityId, detector);
    var payload = new
    {
        unique_id = uniqueId, object_id = uniqueId, name = friendlyName,
        state_topic = $"argus/{slug}/flag/state",
        availability = new object[] { new { topic = BridgeAvailabilityTopic, ... }, new { topic = $"argus/{slug}/availability", ... } },
        payload_on = "ON", payload_off = "OFF", device_class = "problem",
        device = new { identifiers = new[] { slug }, name = $"Argus {slug}", model = Model, manufacturer = Manufacturer }
    };
    return JsonSerializer.Serialize(payload, JsonOptions);
}
```
**Retraction pattern** (lines 169-188):
```csharp
public static async Task RetractAsync(
    Func<string, string, bool, CancellationToken, Task> publish,
    IEnumerable<EntityConfig> removedEntities, CancellationToken ct)
{
    foreach (var entity in removedEntities)
    {
        var anomalyId = UniqueId.AnomalyId(entity.EntityId, detector);
        var scoreId = UniqueId.ScoreId(entity.EntityId, detector);
        await publish($"homeassistant/binary_sensor/{anomalyId}/config", string.Empty, true, ct);
        await publish($"homeassistant/sensor/{scoreId}/config", string.Empty, true, ct);
    }
}
```
**Apply as:** Add `BuildGroupBinarySensorConfig(GroupConfig group, string? memberId = null)` / `BuildGroupSensorConfig(...)` branching on `group.Mode` for `uniqueId`/`name`/`state_topic` (RESEARCH.md Pattern 5 gives the exact payload shape). **Critical deviation from the per-entity pattern:** `device.identifiers` must be `argus_group_{groupSlug}` (ONE shared value across ALL of a group's entities, including every member pair in peer mode) — NOT a per-member slug, otherwise HA won't group them under one device (this is the one place group code must NOT literally copy the per-entity `identifiers = new[] { slug }` line). Add a testable-delegate `RetractGroupAsync` overload identical in shape to `RetractAsync`, but retracting only `removedMembers = oldMembers.Except(newMembers)` per group (Pitfall 5) — never blanket-retract the whole group on a partial membership change.

---

### `Mqtt/UniqueId.cs` — group id helpers

**Analog:** same file (full, 20 lines)

```csharp
public static class UniqueId
{
    public static string Slug(string entityId) => entityId.Replace(".", "_");
    public static string AnomalyId(string entityId, string detector) => $"argus_{Slug(entityId)}_{detector}_anomaly";
    public static string ScoreId(string entityId, string detector) => $"argus_{Slug(entityId)}_{detector}_score";
}
```
**Apply as:** Add `GroupFlagId(string groupId, string? memberId = null)` → `argus_group_{Slug(groupId)}_flag` or `argus_group_{Slug(groupId)}_{Slug(memberId)}_flag` (branch on `memberId is null`), and `GroupScoreId` analogously — matches the exact unique_id scheme locked in CONTEXT.md (`argus_group_{group_slug}_{member_slug}_flag|score` / `argus_group_{group_slug}_flag|score`). Reuse the same `Slug` helper (dot→underscore) for both `groupId` and `memberId`.

---

### `Mqtt/StatePublisher.cs` / `IStatePublisher.cs` — group state topics

**Analog:** same file, `FlagTopic`/`ScoreTopic`/`PublishFlagAsync`/`PublishScoreAsync` (lines 29-56)

```csharp
public string FlagTopic(string entityId) => $"argus/{UniqueId.Slug(entityId)}/flag/state";
public string ScoreTopic(string entityId) => $"argus/{UniqueId.Slug(entityId)}/score/state";

public async Task PublishFlagAsync(string entityId, bool on, CancellationToken ct)
{
    EnsureConnected();
    var payload = on ? "ON" : "OFF";
    await _mqtt!.PublishAsync(FlagTopic(entityId), payload, retain: false, ct);
}
```
**Apply as:** Add `GroupFlagTopic(string groupId, string? memberId, out ...)`/`GroupScoreTopic(...)` returning `argus/group/{slug}/{memberSlug}/flag/state` (peer) or `argus/group/{slug}/flag/state` (joint) — a DISTINCT `argus/group/...` namespace prefix, never reusing the per-entity `argus/{slug}/...` prefix (RESEARCH.md Code Examples note — avoids topic collision between synthetic group "entity ids" and real entity_ids). Add `PublishGroupFlagAsync`/`PublishGroupScoreAsync` overloads with the identical `EnsureConnected()` + `retain: false` publish shape as the existing methods.

---

### Test files

**`GroupInfluxReaderTests.cs`** — analog `Tests/InfluxDbReaderTests.cs` (full file). Copy the `EmptyQueryApi`/`ThrowingQueryApi` fake pattern (lines 20-32) and the `ValidSettings()`/`NullUrlSettings()`/`NullBucketSettings()` helper pattern (lines 36-62); add a fixture with a real multi-column `FluxTable`/`FluxRecord` (via `InfluxDB.Client.Core.Flux.Domain`) to test the pivot-null → staleness-exclusion logic, plus a "stale member beyond cap → excluded" test per CONTEXT.md specifics.

**`BatchSchedulerWorkerTests.cs` (extend)** — analog same file, `FakeInfluxDbReader`/`FakeBatchDetectorClient` (lines 22-67). Add `FakeGroupInfluxDataSource`/extend `FakeBatchDetectorClient` with `ScoreGroupBatchAsync`/`FitGroupAsync` call-count tracking (same `CallCount`/`ThrowOn*` init-property shape), plus a test asserting `FitGroupAsync` is NEVER called for `peer_divergence` groups (Pitfall re: stateless peer mode).

**`MqttRetractionTests.cs` (extend)** — analog same file, `PublishCall` recorder + `MakeRecorder()` (lines 15-28) and the "N entities → 2N messages" / "correct topic" test shapes (lines 42-80+). Add group-membership-change tests: shrinking a group from 4→3 members retracts only the removed member's 2 topics (not the whole group) — mirrors `RetractAsync_OnlyRetractsPassedEntities_NotOthers` pattern.

**`EntitiesConfigTests.cs` (extend)** — analog same file, `Load_OneEntityWithHstParams_ParsesCorrectly` (lines 18-51) and `Load_EntityWithCovariates_ParsesSuccessfullyAndLogsWarning` (lines 53-70+, uses `CapturingLoggerProvider`). Add: valid group parses; group below 3-member floor is skipped+warns (not thrown); peer-divergence group with mixed units skipped+warns; group config load never throws (degrade-not-crash assertion, contrasts with entity `Validate`'s `throw` behavior).

## Shared Patterns

### Fault isolation (per-item try/catch, rethrow OperationCanceledException)
**Source:** `Batch/BatchSchedulerWorker.cs` lines 132-142
**Apply to:** Group loop in `RunBatchAsync`/`RunNightlyFitAsync`
```csharp
try { await RunGroupBatchAsync(group, ct); }
catch (OperationCanceledException) { throw; }
catch (Exception ex) { _logger.LogError(LogEvents.BatchSchedulerError, ex, "Group batch failed for {GroupId}", group.GroupId); }
```

### Per-cycle live-config read (CFG-04)
**Source:** `Batch/BatchSchedulerWorker.cs` line 128 (`_liveConfig.Get().Entities`)
**Apply to:** Group loop must read `_liveConfig.Get().Groups` fresh every cycle, same as entities — no caching.

### Flux string-literal injection guard
**Source:** `Batch/InfluxDbReader.cs` lines 15-19, 70-77
**Apply to:** `GroupInfluxReader` — validate every member id, bucket, measurement, value-field through the same `_safeFluxString` regex before interpolation.

### Retained MQTT discovery + retraction (retract-before-publish)
**Source:** `Mqtt/DiscoveryPublisher.cs` lines 108-188; `Tests/MqttRetractionTests.cs`
**Apply to:** Group discovery/retraction — retract removed members' topics BEFORE publishing the current set; retraction scope limited to exactly the removed set (no blanket retract-republish).

### google.protobuf.DoubleValue → C# double?
**Source:** `Batch/BatchSchedulerWorker.cs` line 174 (`last.Score ?? 0.0`)
**Apply to:** `GroupScoreResponse.PerMember[].Score` / `GroupVerdict.Score` handling.

### Validate-before-Swap, degrade-not-crash
**Source:** `Config/EntitiesConfigLoader.cs` lines 39-47 (empty-entities warning branch, NOT the throwing branch)
**Apply to:** All new group validation — must warn+skip, never throw (deviates from the entity `Validate` method's throw-on-bad-entity behavior; CONTEXT.md explicitly requires groups degrade instead).

## No Analog Found

None — every file in this phase has a direct, exact-role analog already in the codebase (per RESEARCH.md: "Phase 6 is pure wiring").

## Metadata

**Analog search scope:** `orchestrator/Argus.Orchestrator/Config/`, `orchestrator/Argus.Orchestrator/Batch/`, `orchestrator/Argus.Orchestrator/Mqtt/`, `orchestrator/Argus.Orchestrator.Tests/`
**Files scanned:** 11 source analogs + 4 test analogs (all read directly this session)
**Pattern extraction date:** 2026-07-02
