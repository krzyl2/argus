# Phase 3: Config Read/Write + Detector Assignment + Reload — Pattern Map

**Mapped:** 2026-07-01
**Files analyzed:** 10 new/modified files
**Analogs found:** 10 / 10

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `Config/ILiveEntitiesConfig.cs` + `LiveEntitiesConfig.cs` | service (singleton) | event-driven | `Ha/HaSensorRegistry.cs` + `Health/ArgusHealthSignals.cs` | exact |
| `Detection/ScoreStreamPipeline.cs` | service | streaming | `Detection/ScoreStreamPipeline.cs` (self-migration) | exact |
| `Workers/HaListenerWorker.cs` | worker (BackgroundService) | event-driven | `Workers/HaListenerWorker.cs` (self-migration) + `Batch/BatchSchedulerWorker.cs` inner loop | exact |
| `Batch/BatchSchedulerWorker.cs` | worker (BackgroundService) | batch | `Batch/BatchSchedulerWorker.cs` (self-migration) | exact |
| `Workers/MqttPublisherWorker.cs` | worker (BackgroundService) | event-driven | `Workers/MqttPublisherWorker.cs` (self-migration) | exact |
| `Mqtt/DiscoveryPublisher.cs` | utility (static) | request-response | `Mqtt/DiscoveryPublisher.cs` (self-extension) | exact |
| `Program.cs` | config / route | request-response | `Program.cs` (self-migration) | exact |
| `Web/EntityPickerPage.cs` | component (HTML builder) | request-response | `Web/EntityPickerPage.cs` (self-extension) | exact |
| `wwwroot/css/argus.css` | config (stylesheet) | — | `wwwroot/css/argus.css` (self-extension) | exact |
| `Tests/` — 5 new test files | test | — | `HaSensorRegistryTests.cs`, `BatchSchedulerWorkerTests.cs`, `SaveEndpointPatternsTests.cs`, `EntityPickerPageTests.cs` | role-match |

---

## Pattern Assignments

---

### `Config/ILiveEntitiesConfig.cs` + `Config/LiveEntitiesConfig.cs` (service, event-driven)

**Primary analog:** `Ha/HaSensorRegistry.cs`
**Secondary analog:** `Health/ArgusHealthSignals.cs`

**Volatile reference pattern — from `Ha/HaSensorRegistry.cs` lines 1–52:**
```csharp
// EXACT pattern to copy for volatile reference field and direct assignment
public sealed class HaSensorRegistry : IHaSensorRegistry
{
    private volatile IReadOnlyList<HaSensorEntry> _snapshot = Array.Empty<HaSensorEntry>();

    public IReadOnlyList<HaSensorEntry> GetAll() => _snapshot;

    // ... writer assigns directly:
    _snapshot = entries;
}
```

**Volatile bool pattern — from `Health/ArgusHealthSignals.cs` lines 1–21:**
```csharp
// Pattern: volatile field declaration, no lock needed, single-writer many-reader
public sealed class ArgusHealthSignals
{
    public volatile bool HaConnected;
    public volatile bool DetectorConnected;
}
```

**Apply to `LiveEntitiesConfig`:** Replace `IReadOnlyList` with `EntitiesConfig`, add `Interlocked.Exchange` for the swap (required because `volatile` alone does not guarantee the atomic reference swap + post-swap event ordering), add `event EventHandler? ConfigChanged` fired AFTER the exchange. Namespace: `Argus.Orchestrator.Config`.

**Imports to copy from `HaSensorRegistry.cs` lines 1–3 and namespace pattern:**
```csharp
using System.Globalization;  // (HaSensorRegistry has this; LiveEntitiesConfig won't need it)

namespace Argus.Orchestrator.Config;  // ← new file goes here, not Ha/
```

---

### `Detection/ScoreStreamPipeline.cs` (service, streaming) — migration

**Analog:** `Detection/ScoreStreamPipeline.cs` (self — targeted field migration)

**Current ctor-captured field (lines 37, 51, 65):**
```csharp
private readonly EntitiesConfig _entitiesConfig;

// Production ctor (line 47):
_entitiesConfig = entitiesConfig ?? throw new ArgumentNullException(nameof(entitiesConfig));

// Test ctor (line 60):
_entitiesConfig = entitiesConfig ?? throw new ArgumentNullException(nameof(entitiesConfig));
```

**Current `BuildEntityStates` reads from field (lines 247–259):**
```csharp
private Dictionary<string, EntityRuntimeState> BuildEntityStates()
{
    var states = new Dictionary<string, EntityRuntimeState>(StringComparer.OrdinalIgnoreCase);
    foreach (var entity in _entitiesConfig.Entities)
    {
        var hstDetector = entity.Detectors.FirstOrDefault(d =>
            string.Equals(d.Name, "hst", StringComparison.OrdinalIgnoreCase));
        var hstParams = hstDetector is not null
            ? HstParams.From(hstDetector.Params)
            : new HstParams();
        states[entity.EntityId] = new EntityRuntimeState(hstParams);
    }
    return states;
}
```

**Migration:** Change both ctors to accept `ILiveEntitiesConfig liveConfig` instead of `EntitiesConfig entitiesConfig`. Store as `private readonly ILiveEntitiesConfig _liveConfig`. In `BuildEntityStates`, replace `_entitiesConfig.Entities` with `_liveConfig.Get().Entities`. The production ctor already validates with `?? throw` — keep that pattern for `liveConfig`.

**Import to add:**
```csharp
using Argus.Orchestrator.Config;  // already present — ILiveEntitiesConfig lives here
```

---

### `Workers/HaListenerWorker.cs` (worker, event-driven) — inner-CTS restart loop

**Analog:** `Workers/HaListenerWorker.cs` (self — targeted method replacement) + `Batch/BatchSchedulerWorker.cs` for OperationCanceledException handling pattern

**Current `ExecuteAsync` — the one-shot call to replace (lines 32–50):**
```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    _logger.Log(LogLevel.Information, LogEvents.HaListenerStarting,
        "HaListenerWorker starting — waiting for detector health gate (INFRA-07)");

    await _gateway.WaitForHealthyAsync(stoppingToken);

    if (stoppingToken.IsCancellationRequested)
        return;

    _logger.Log(LogLevel.Information, LogEvents.HaListenerDetectorHealthy,
        "Detector healthy — starting ScoreStreamPipeline (Plan 08)");

    await _scoreStreamPipeline.RunAsync(_haEventSource.ReadAllAsync(stoppingToken), stoppingToken);
}
```

**OperationCanceledException rethrow pattern — from `BatchSchedulerWorker.cs` lines 115–116:**
```csharp
catch (OperationCanceledException) { throw; }
```

**`when` guard pattern — from `Detection/ScoreStreamPipeline.cs` line 234:**
```csharp
catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
```

**Ctor pattern to copy from lines 20–29 (argument-null guards):**
```csharp
public HaListenerWorker(
    IHaEventSource haEventSource,
    DetectionGateway gateway,
    ScoreStreamPipeline scoreStreamPipeline,
    ILogger<HaListenerWorker> logger)
{
    _haEventSource = haEventSource ?? throw new ArgumentNullException(nameof(haEventSource));
    _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    _scoreStreamPipeline = scoreStreamPipeline ?? throw new ArgumentNullException(nameof(scoreStreamPipeline));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
}
```

**Migration:** Add `ILiveEntitiesConfig _liveConfig` and `MqttConnection _mqtt` fields (same null-guard pattern). Replace `ExecuteAsync` body with the inner-CTS restart loop. Use `LogEvents.*` named event IDs for all new log calls (copy from `Logging/LogEvents.cs` pattern).

**Imports to add:**
```csharp
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Mqtt;
```

**Pitfall guard — null-before-dispose (from RESEARCH.md Pitfall 3):**
```csharp
finally
{
    var toDispose = innerCts;
    innerCts = null;  // null FIRST so handler sees null, not a disposed CTS
    toDispose?.Dispose();
}
```

---

### `Batch/BatchSchedulerWorker.cs` (worker, batch) — migration

**Analog:** `Batch/BatchSchedulerWorker.cs` (self — field replacement)

**Current field and both ctor assignments (lines 37, 56, 69):**
```csharp
private readonly EntitiesConfig _entities;

// test ctor (line 56):
_entities = entities ?? throw new ArgumentNullException(nameof(entities));

// production ctor (line 69) calls this ctor via : this(...)
```

**Both usage sites to migrate (lines 127, 215):**
```csharp
// RunBatchAsync (line 127):
foreach (var entity in _entities.Entities)

// RunNightlyFitAsync (line 215):
foreach (var entity in _entities.Entities)
```

**Migration:** Replace `private readonly EntitiesConfig _entities` with `private readonly ILiveEntitiesConfig _liveConfig`. Both usage sites become `_liveConfig.Get().Entities`. Both ctors accept `ILiveEntitiesConfig liveConfig` instead of `EntitiesConfig entities`. The `?? throw` guard pattern is unchanged.

**Program.cs factory registration (lines 143–150) — change one argument:**
```csharp
builder.Services.AddHostedService<BatchSchedulerWorker>(sp => new BatchSchedulerWorker(
    sp.GetRequiredService<ConnectionSettings>(),
    sp.GetRequiredService<IInfluxDataSource>(),
    sp.GetRequiredService<IBatchDetectorClient>(),
    sp.GetRequiredService<IStatePublisher>(),
    sp.GetRequiredService<EntitiesConfig>(),    // ← change to ILiveEntitiesConfig
    sp.GetRequiredService<DetectionGateway>(),
    sp.GetRequiredService<ILogger<BatchSchedulerWorker>>()));
```

**Import to add:**
```csharp
using Argus.Orchestrator.Config;  // already present — ILiveEntitiesConfig lives here
```

---

### `Workers/MqttPublisherWorker.cs` (worker, event-driven) — migration + ConfigChanged subscription

**Analog:** `Workers/MqttPublisherWorker.cs` (self — field migration + new subscription)

**Current field and ctor (lines 21–34):**
```csharp
private readonly EntitiesConfig _entities;

public MqttPublisherWorker(
    MqttConnection mqtt,
    StatePublisher statePublisher,
    EntitiesConfig entities,
    ILogger<MqttPublisherWorker> logger)
{
    _mqtt = mqtt;
    _statePublisher = statePublisher;
    _entities = entities;
    _logger = logger;
}
```

**Current startup-only discovery publish (lines 47–55):**
```csharp
await DiscoveryPublisher.PublishAllAsync(_mqtt, _entities.Entities, stoppingToken);
_logger.LogInformation(LogEvents.MqttDiscoveryPublished,
    "Discovery published for {Count} entities", _entities.Entities.Count);

foreach (var entity in _entities.Entities)
{
    await _statePublisher.PublishAvailabilityAsync(entity.EntityId, online: true, stoppingToken);
}
```

**Keep-alive pattern (line 60) — copy exactly:**
```csharp
await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => Task.CompletedTask);
```

**Migration:** Add `ILiveEntitiesConfig _liveConfig` field. Store `stoppingToken` as a field (`private CancellationToken _stoppingToken`) for use in the ConfigChanged handler. Subscribe to `ConfigChanged` before the keep-alive; unsubscribe in a `finally`. On `ConfigChanged`, fire-and-forget: `Task.Run(() => DiscoveryPublisher.PublishAllAsync(_mqtt, _liveConfig.Get().Entities, _stoppingToken))`. Replace `_entities.Entities` with `_liveConfig.Get().Entities` for the initial publish.

---

### `Mqtt/DiscoveryPublisher.cs` (utility, request-response) — add retract path

**Analog:** `Mqtt/DiscoveryPublisher.cs` lines 110–146 (existing `PublishAllAsync` delegate overload)

**Topic formula — from lines 130–145 (exact strings to reuse):**
```csharp
var anomalyId = UniqueId.AnomalyId(entity.EntityId, detector);
var scoreId   = UniqueId.ScoreId(entity.EntityId, detector);

await publish(
    $"homeassistant/binary_sensor/{anomalyId}/config",
    BuildBinarySensorConfig(entity),
    true,
    ct);

await publish(
    $"homeassistant/sensor/{scoreId}/config",
    BuildSensorConfig(entity),
    true,
    ct);
```

**Detector name helper (line 182) — copy for retraction:**
```csharp
private static string GetDetectorName(EntityConfig entity)
    => entity.Detectors.Count > 0 ? entity.Detectors[0].Name : "hst";
```

**`MqttConnection.PublishAsync` signature (line 79) — retraction call shape:**
```csharp
public async Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct)
```

**New static method to add — `RetractAsync` (place below `PublishAllAsync`):**
```csharp
public static async Task RetractAsync(
    MqttConnection mqtt,
    IEnumerable<EntityConfig> removedEntities,
    CancellationToken ct)
{
    foreach (var entity in removedEntities)
    {
        var detector = GetDetectorName(entity);
        var anomalyId = UniqueId.AnomalyId(entity.EntityId, detector);
        var scoreId   = UniqueId.ScoreId(entity.EntityId, detector);

        await mqtt.PublishAsync(
            $"homeassistant/binary_sensor/{anomalyId}/config",
            string.Empty, retain: true, ct);
        await mqtt.PublishAsync(
            $"homeassistant/sensor/{scoreId}/config",
            string.Empty, retain: true, ct);
    }
}
```

`string.Empty` with `retain: true` produces a zero-byte retained payload — the MQTT retained-message deletion mechanism.

---

### `Program.cs` (config / route) — registration migration + new endpoints

**Analog:** `Program.cs` (self — targeted line changes + new endpoint additions)

**Current `AddSingleton(entitiesConfig)` (lines 22–23) — to replace:**
```csharp
var entitiesConfig = EntitiesConfigLoader.Load(entitiesPath, entitiesLogger);
builder.Services.AddSingleton(entitiesConfig);
```

**Becomes (insert before existing registration block):**
```csharp
var entitiesConfig = EntitiesConfigLoader.Load(entitiesPath, entitiesLogger);
var liveConfig = new LiveEntitiesConfig(entitiesConfig);
builder.Services.AddSingleton<ILiveEntitiesConfig>(liveConfig);
// Remove builder.Services.AddSingleton(entitiesConfig) once all consumers migrated
```

**Current `GET /sensors` handler (lines 224–235) — parameter to migrate:**
```csharp
app.MapGet("/sensors", (HttpRequest req, IHaSensorRegistry registry,
    EntitiesConfig config, ArgusHealthSignals health) =>   // ← EntitiesConfig → ILiveEntitiesConfig
{
    ...
    var html = EntityPickerPage.BuildFullPage(
        ip, registry, config, health, q,                   // ← config → liveConfig.Get()
        lastIncludePatterns, lastExcludePatterns);
```

**New endpoint pattern — add `GET /api/detectors/new-entry` after line 244 (after `/api/sensors`):**
```csharp
// Copy IsAuthorizedRequest guard pattern from existing endpoints (line 228 pattern):
app.MapGet("/api/detectors/new-entry", (HttpRequest req) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);

    var entityIdx = req.Query["entity_idx"].FirstOrDefault() ?? "0";
    var detIdx    = req.Query["det_idx"].FirstOrDefault() ?? "0";
    return Results.Content(
        EntityPickerPage.BuildDetectorEntry(
            int.Parse(entityIdx), int.Parse(detIdx),
            new DetectorConfig { Name = "hst", Params = [] }),
        "text/html");
});
```

**POST `/api/sensors/save` extension — add `ILiveEntitiesConfig` to the handler params and call `Swap` after `WriteAsync` (same position pattern as existing `writer.WriteAsync` call at line 320):**
```csharp
// After writer.WriteAsync:
var newConfig = EntitiesConfigLoader.Load(entitiesPath, logger);  // re-read + validate
liveConfig.Swap(newConfig);   // triggers ConfigChanged → HaListenerWorker restart
```

**Return banner for reload state — add `BuildReloadingBanner` call before `BuildSuccessBanner`:**
```csharp
// Reloading banner returned as the POST response (synchronous — swap already done above)
return Results.Content(EntityPickerPage.BuildSuccessBanner(entities.Count), "text/html");
// Or if a "reloading" intermediate step is needed:
// return Results.Content(EntityPickerPage.BuildReloadingBanner(entities.Count), "text/html");
```

---

### `Web/EntityPickerPage.cs` (component, request-response) — disclosure rows + detector entry

**Analog:** `Web/EntityPickerPage.cs` (self — extend `BuildListRows` + add new static methods)

**String builder pattern in `BuildListRows` (lines 186–234) — copy exactly for new sections:**
```csharp
var sb = new StringBuilder();
foreach (var entry in entries)
{
    var safeEntityId = WebUtility.HtmlEncode(entry.EntityId);
    // ... other encoded vars ...
    sb.AppendLine($"""
        <li class="argus-list-row{trackedClass}">
          ...
        </li>
        """);
}
return sb.ToString();
```

**T-02-07 pattern — encode ALL user-originated strings before interpolation (lines 191–195):**
```csharp
// Encode any string that originates from user data or config:
var safeEntityId = WebUtility.HtmlEncode(entry.EntityId);
var safeValue    = entry.CurrentValue.ToString("G");  // numeric — no encode needed
var safeUnit     = entry.UnitOfMeasurement is not null
    ? WebUtility.HtmlEncode(entry.UnitOfMeasurement)
    : null;
```

**Banner builder pattern (lines 151–173) — copy for `BuildReloadingBanner`:**
```csharp
public static string BuildSuccessBanner(int count)
{
    return $"""
        <div class="argus-banner argus-banner--success"
             role="status" aria-live="polite">
          Configuration saved. {count} {(count == 1 ? "entity" : "entities")} tracked.
        </div>
        """;
}
```

**New methods to add:**

1. `BuildDetectorEntry(int entityIdx, int detIdx, DetectorConfig detector)` — returns one `.argus-detector-entry` fragment. Uses the `$$"""..."""` raw string literal pattern. HtmlEncodes `detector.Name` and param values.

2. Extend `BuildListRows` to accept `EntitiesConfig config` parameter. For each tracked entry, after the closing `</label>`, append a `<details class="argus-detectors-details">` block. Get the entity's `Detectors` list from `config.Entities.FirstOrDefault(e => e.EntityId == entry.EntityId)`.

3. `BuildFullPage` signature change: add `EntitiesConfig config` (it already has this parameter — pass it through to the extended `BuildListRows`).

**Raw string literal pattern for multi-line HTML (line 52+):**
```csharp
return $$"""
    <!DOCTYPE html>
    ...
    {{someInterpolatedValue}}
    ...
    """;
```
For fragments (non-interpolating `{` and `}`), use `$"""..."""` with `{var}` — copy the banner builder pattern.

**HstParams defaults — read from `EntitiesConfig.cs` `HstParams` class (lines 36–48) for pre-fill values:**
```csharp
// Pre-fill defaults per type (copy from HstParams):
// window=250, n_trees=25, high_threshold=0.7, low_threshold=0.3,
// min_consecutive=3, frozen_window=10, frozen_variance_threshold=0.001
// MAD: threshold=3.5, window=20
// STL: period=24, seasonal=7, threshold=3.0
```

---

### `wwwroot/css/argus.css` (config, stylesheet) — new BEM blocks

**Analog:** `wwwroot/css/argus.css` lines 171–end (Phase 2 additions section)

**Phase 2 section marker pattern (line 171) — add Phase 3 section at end of file:**
```css
/* ── Phase 3: Detector Assignment ────────────────────────────────────────── */
```

**Existing button variant pattern to copy for `--destructive-ghost` and `--add-detector` (copy from `.argus-btn` + `.argus-btn--primary` Phase 2 classes):**
```css
.argus-btn {
  /* existing base */
}
.argus-btn--primary {
  background: var(--color-accent);
  color: #ffffff;
  /* ... */
}
```

**Token references — all new classes MUST use existing `--color-*`, `--space-*`, `--font-*` tokens only. No new variables.** The `--color-destructive` token is already declared at line 14 (light) and dark override at line 50.

**All new classes listed in 03-UI-SPEC.md** (exact spec with properties — see New Component Classes section):
- `.argus-detectors-details`
- `.argus-disclosure-toggle` + `::before` + `details[open] >` variant + `:hover`
- `.argus-detectors-panel`
- `.argus-detector-entry`
- `.argus-detector-header`
- `.argus-detector-select` + `:focus`
- `.argus-timing-caption`
- `.argus-param-grid` + `@media (max-width: 480px)` + `.argus-param-grid--span2`
- `.argus-param-field` + `__label` + `__input` + `__input:focus`
- `.argus-btn--destructive-ghost` + `:hover` + `:active`
- `.argus-btn--add-detector` + `:hover` + `:active`
- `.argus-add-detector-row`
- `.argus-banner--reloading`

---

### Test files (5 new, unit) — xUnit pattern

**Primary analog:** `HaSensorRegistryTests.cs`, `BatchSchedulerWorkerTests.cs`, `SaveEndpointPatternsTests.cs`, `EntityPickerPageTests.cs`

**File header + using pattern (from `HaSensorRegistryTests.cs` lines 1–11):**
```csharp
using Argus.Orchestrator.Config;   // or relevant namespace
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for {Component}. Fully offline — no live services required.
/// </summary>
public class {Component}Tests
{
```

**Fact method pattern (from `HaSensorRegistryTests.cs` lines 28–42):**
```csharp
[Fact]
public void {MethodName}_{Condition}_{ExpectedResult}()
{
    // Arrange
    // Act
    // Assert — Assert.Single / Assert.Equal / Assert.Contains / Assert.Empty
}
```

**Async Fact pattern (from `SaveEndpointPatternsTests.cs` lines 198–217):**
```csharp
[Fact]
public async Task {MethodName}_{Condition}_{ExpectedResult}()
{
    // ... await ...
    Assert.True(...);
}
```

**Fake/stub pattern (from `BatchSchedulerWorkerTests.cs` lines 21–63):**
```csharp
private sealed class Fake{Interface} : I{Interface}
{
    public int CallCount { get; private set; }

    public Task {MethodAsync}(... , CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult(...);
    }
}
```

**Test helper wrapping `EntitiesConfig` in `LiveEntitiesConfig` (new pattern for Phase 3 tests):**
```csharp
// In test setup — wrap static config in the live wrapper:
private static ILiveEntitiesConfig MakeLive(EntitiesConfig cfg) => new LiveEntitiesConfig(cfg);

// Usage:
var worker = new BatchSchedulerWorker(
    DefaultSettings(), influx, detector, publisher,
    MakeLive(OneEntityOneDetector()),  // ← ILiveEntitiesConfig instead of EntitiesConfig
    NullLogger<BatchSchedulerWorker>.Instance);
```

**Thread-safety test pattern (from `HaSensorRegistryTests.cs` lines 207–239):**
```csharp
[Fact]
public async Task ConcurrentUpdateAndGetAll_DoesNotThrow()
{
    var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
    var writerTask = Task.Run(() => { for (int i=0; i<500; i++) try { ... } catch(Exception ex) { exceptions.Add(ex); } });
    var readerTask = Task.Run(() => { for (int i=0; i<500; i++) try { ... } catch(Exception ex) { exceptions.Add(ex); } });
    await Task.WhenAll(writerTask, readerTask);
    Assert.Empty(exceptions);
}
```

**ConfigChanged event test pattern (new — use `TaskCompletionSource`):**
```csharp
[Fact]
public void Swap_FiresConfigChangedAfterExchange()
{
    var initial = new EntitiesConfig();
    var live = new LiveEntitiesConfig(initial);
    EntitiesConfig? capturedFromGet = null;

    live.ConfigChanged += (s, e) => capturedFromGet = live.Get();

    var newConfig = new EntitiesConfig();
    live.Swap(newConfig);

    Assert.Same(newConfig, capturedFromGet);  // handler sees the new config
    Assert.Same(newConfig, live.Get());
}
```

---

## Shared Patterns

### Argument-Null Guards in Constructors
**Source:** `Workers/HaListenerWorker.cs` lines 25–29, `Detection/ScoreStreamPipeline.cs` lines 49–52
**Apply to:** All new/migrated constructors
```csharp
_field = param ?? throw new ArgumentNullException(nameof(param));
```

### LogEvents Named Event IDs
**Source:** `Logging/LogEvents.cs` (all log calls use `LogEvents.*` constant IDs)
**Apply to:** All new `_logger.Log*` calls in HaListenerWorker and MqttPublisherWorker
```csharp
_logger.Log(LogLevel.Information, LogEvents.HaListenerStarting, "...");
_logger.LogInformation(LogEvents.MqttWorkerStarted, "...");
```
Add new `LogEvents` constants for: `ConfigReloadTriggered`, `ConfigReloadComplete`, `MqttRetractionPublished`.

### HTML Encode All User Strings
**Source:** `Web/EntityPickerPage.cs` lines 46–49 and 191–195 (T-02-07)
**Apply to:** All new HTML interpolations in `EntityPickerPage.cs`
```csharp
var safeDetectorName = WebUtility.HtmlEncode(detector.Name);
var safeParamValue   = WebUtility.HtmlEncode(paramValue);
```

### YAML Serialization via YamlDotNet (T-02-08)
**Source:** `Program.cs` lines 296–316 (POST save handler)
**Apply to:** Extended save handler in `Program.cs`
```csharp
var serializer = new SerializerBuilder()
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .Build();
// Never string-format YAML — always use the serializer
```

### IsAuthorizedRequest Guard
**Source:** `Program.cs` lines 206–218 (static local function)
**Apply to:** Every new endpoint in `Program.cs`
```csharp
if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);
```

### ConfigWriter Atomic Write
**Source:** `Config/ConfigWriter.cs` lines 13–37 (SemaphoreSlim + temp-rename)
**Apply to:** Extended POST `/api/sensors/save` handler — reuse `writer.WriteAsync`, do not bypass
```csharp
await writer.WriteAsync(entitiesPath, fullYaml, ct);
// Then call liveConfig.Swap(...) AFTER WriteAsync succeeds
```

### OperationCanceledException Rethrow
**Source:** `Batch/BatchSchedulerWorker.cs` line 115
**Apply to:** All per-entity catch blocks in `HaListenerWorker` restart loop and any new loops
```csharp
catch (OperationCanceledException) { throw; }
```

---

## No Analog Found

All files have close analogs in the codebase. No files require falling back to RESEARCH.md patterns exclusively.

---

## Metadata

**Analog search scope:** `orchestrator/Argus.Orchestrator/` and `orchestrator/Argus.Orchestrator.Tests/`
**Files scanned:** 26 source files + 24 test files
**Pattern extraction date:** 2026-07-01
