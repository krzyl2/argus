# Phase 1: Ingress Scaffold + SDK Migration + Config Seam - Pattern Map

**Mapped:** 2026-06-30
**Files analyzed:** 9 new/modified files
**Analogs found:** 8 / 9

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj` | config | transform | `orchestrator/Argus.Orchestrator.Tests/Argus.Orchestrator.Tests.csproj` | role-match |
| `orchestrator/Argus.Orchestrator/Program.cs` | config/bootstrap | request-response | `orchestrator/Argus.Orchestrator/Program.cs` (self — migration) | exact (modify-in-place) |
| `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` | service | CRUD | `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` (self) | exact (modify-in-place) |
| `orchestrator/Argus.Orchestrator/Config/ConfigWriter.cs` | service | file-I/O | `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs` | role-match (SemaphoreSlim pattern) |
| `orchestrator/Argus.Orchestrator/wwwroot/js/htmx.min.js` | static | — | none (download artifact) | none |
| `orchestrator/Argus.Orchestrator/wwwroot/css/argus.css` | static | — | none (new file) | none |
| `argus/config.yaml` | config | — | `argus/config.yaml` (self — append keys) | exact (modify-in-place) |
| `orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs` | test | — | `orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs` (self — extend) | exact (extend-in-place) |
| NEW: `orchestrator/Argus.Orchestrator.Tests/ConfigWriterTests.cs` | test | file-I/O | `orchestrator/Argus.Orchestrator.Tests/MqttConnectionTests.cs` | role-match |

---

## Pattern Assignments

### `orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj` (config, SDK swap)

**Analog:** self (modify-in-place)

**Current SDK line** (line 1):
```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">
```

**Target SDK line:**
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
```

**PackageReference to remove** (line 11):
```xml
<PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.1" />
```
`Microsoft.Extensions.Hosting` is implicit in `Microsoft.NET.Sdk.Web` — removing it eliminates the explicit version pin that could drift from the framework version.

**No other csproj changes.** All existing `PackageReference` entries (`NetDaemon.Client`, `MQTTnet`, `Grpc.*`, `YamlDotNet`, `InfluxDB.Client`) remain verbatim.

---

### `orchestrator/Argus.Orchestrator/Program.cs` (bootstrap, request-response)

**Analog:** `orchestrator/Argus.Orchestrator/Program.cs` (self — migration target)

**Builder swap** (line 11):
```csharp
// BEFORE (line 11):
var builder = Host.CreateApplicationBuilder(args);

// AFTER:
var builder = WebApplication.CreateBuilder(args);
```

**Kestrel bind — insert before `builder.Build()`** (after line 139, before the conditional InfluxDB block ends):
```csharp
// NEW: Kestrel on 0.0.0.0:8099 — must be before Build() so Kestrel
// adopts the programmatic listen list instead of the default localhost:5000.
builder.WebHost.ConfigureKestrel(opts =>
    opts.Listen(System.Net.IPAddress.Any, 8099));
```

**ConfigWriter DI registration — insert alongside existing singletons:**
```csharp
// NEW: atomic config write seam (Phase 1 foundation, Phase 2+ save handler uses this)
builder.Services.AddSingleton<ConfigWriter>();
```

**Host build/run swap** (lines 145-146):
```csharp
// BEFORE:
var host = builder.Build();
host.Run();

// AFTER:
var app = builder.Build();

// NEW: middleware pipeline — order is critical (RESEARCH.md Pattern 2)
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Headers.TryGetValue("X-Ingress-Path", out var ingressPath))
        ctx.Request.PathBase = new PathString(ingressPath.ToString());
    await next();
});
app.UseRouting();        // explicit call — must follow PathBase middleware
app.UseStaticFiles();    // serves wwwroot/js/htmx.min.js, wwwroot/css/argus.css

app.MapGet("/", (HttpRequest req, ArgusHealthSignals health) =>
{
    var ip = req.Headers["X-Ingress-Path"].FirstOrDefault() ?? "";
    return Results.Content(PlaceholderPage.Build(ip, health), "text/html");
});

app.Run();
```

**Existing DI registration pattern to follow** (lines 59-63 — factory lambda for singleton):
```csharp
builder.Services.AddSingleton<GrpcChannel>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<DetectionGateway>>();
    return DetectorChannelFactory.Create(connectionSettings, logger);
});
```
Use the same `sp =>` factory pattern if `ConfigWriter` needs constructor parameters resolved from DI.

**Conditional registration guard pattern** (lines 117-143 — InfluxDB block):
```csharp
if (!string.IsNullOrWhiteSpace(connectionSettings.InfluxUrl))
{
    builder.Services.AddSingleton<InfluxDBClient>(_ => ...);
    // ...
}
else
{
    Console.WriteLine("[Argus] InfluxDB not configured ...");
}
```
Follow this same guard pattern for any future conditional Ingress wiring (e.g., phase 4 auth middleware).

---

### `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` (service, CRUD)

**Analog:** self (modify-in-place)

**Current `Validate` signature and crash line** (lines 41-45):
```csharp
private static void Validate(EntitiesConfig config, string path)
{
    if (config.Entities == null || config.Entities.Count == 0)
        throw new InvalidOperationException(
            $"entities.yaml at '{path}' contains no entities");
```

**Target — add `ILogger logger` parameter, replace throw with LogWarning:**
```csharp
private static void Validate(EntitiesConfig config, string path, ILogger logger)
{
    if (config.Entities == null || config.Entities.Count == 0)
    {
        logger.LogWarning(
            "entities.yaml at '{Path}' contains no entities " +
            "— orchestrator running with empty pipeline; configure via UI.", path);
        return; // no throw; BackgroundServices start; UI can load
    }
    foreach (var entity in config.Entities)
    {
        // existing per-entity throws remain unchanged (lines 47-56)
```

**Call site update** (line 32 in `Load()`):
```csharp
// BEFORE:
Validate(config, path);

// AFTER:
Validate(config, path, logger);
```

**Existing logger injection pattern** — `logger` is already the second parameter of `Load()` (line 17):
```csharp
public static EntitiesConfig Load(string path, ILogger logger)
```
The `logger` parameter propagates directly into `Validate` — no new DI plumbing required.

**Existing structured log pattern** (lines 35-37) — copy this style for the new warning:
```csharp
logger.Log(LogLevel.Information, LogEvents.EntityConfigLoaded,
    "Loaded {EntityCount} entities from {Path}", config.Entities.Count, path);
```
Add a matching `LogEvents.EmptyEntitiesWarning` EventId in `LogEvents.cs` (next available ID in the 1xxx config range is 1003).

**`WarnIgnoredKeys` guard** (line 63) — `WarnIgnoredKeys` iterates `config.Entities`; it must be guarded after the empty-entities early return, otherwise it will throw a NullReferenceException on an empty list. Current call order in `Load()` (lines 32-33):
```csharp
Validate(config, path);      // line 32
WarnIgnoredKeys(config, logger); // line 33
```
After the fix, if `Validate` returns early (empty entities), `WarnIgnoredKeys` must not run. Guard it:
```csharp
Validate(config, path, logger);
if (config.Entities?.Count > 0)
    WarnIgnoredKeys(config, logger);
```

---

### `orchestrator/Argus.Orchestrator/Config/ConfigWriter.cs` (service, file-I/O) — NEW

**Analog:** `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs` (SemaphoreSlim serialization pattern)

**SemaphoreSlim field declaration** (`MqttConnection.cs` line 34):
```csharp
private readonly SemaphoreSlim _connectGate = new(1, 1);
```

**SemaphoreSlim acquire/release pattern** (`MqttConnection.cs` lines 61-73):
```csharp
await _connectGate.WaitAsync(ct);
try
{
    // ... critical section ...
}
finally
{
    _connectGate.Release();
}
```

**Full `ConfigWriter` class — copy namespace + sealed + SemaphoreSlim pattern from `MqttConnection`:**
```csharp
using Argus.Orchestrator.Config;

namespace Argus.Orchestrator.Config;

/// <summary>
/// Atomic YAML config writer: temp-file + File.Move(overwrite:true) serialized
/// by SemaphoreSlim(1,1). POSIX rename() is atomic — readers always see a
/// complete file, never a partial write during a concurrent FileSystemWatcher event.
/// </summary>
public sealed class ConfigWriter
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task WriteAsync(string targetPath, string yaml,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var dir = Path.GetDirectoryName(targetPath)!;
            var tmp = Path.Combine(dir, $".entities.tmp.{Guid.NewGuid():N}.yaml");
            await File.WriteAllTextAsync(tmp, yaml, ct);
            File.Move(tmp, targetPath, overwrite: true); // atomic POSIX rename
        }
        finally
        {
            _lock.Release();
        }
    }
}
```

**sealed modifier:** Copy from `MqttConnection.cs` line 21 (`public sealed class MqttConnection`) — all single-responsibility singletons in this codebase are `sealed`.

**Namespace:** `Argus.Orchestrator.Config` — same as `EntitiesConfigLoader` and `EntitiesConfig`.

---

### `argus/config.yaml` (config, manifest append)

**Analog:** self (modify-in-place)

**Existing watchdog line for placement reference** (line 17):
```yaml
watchdog: "tcp://[HOST]:50051"
```

**Keys to append** — insert after `watchdog:` line and before `init: false`:
```yaml
ingress: true
ingress_port: 8099
panel_icon: "mdi:tune-variant"
panel_title: "Argus"
```

**Do NOT add a `ports:` entry** — this is a security constraint (CONTEXT.md locked decision). Ingress is the only permitted access path.

---

### `orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs` (test, CRUD — extend)

**Analog:** self (extend-in-place)

**Existing test structure pattern** (lines 9-17) — copy for new test method:
```csharp
public class EntitiesConfigTests
{
    private static (ILogger<EntitiesConfigLoader> logger, List<string> messages) CreateCapturingLogger()
    {
        var messages = new List<string>();
        var factory = LoggerFactory.Create(b =>
            b.AddProvider(new CapturingLoggerProvider(messages)));
        return (factory.CreateLogger<EntitiesConfigLoader>(), messages);
    }
```

**`WriteTempYaml` helper** (lines 111-115) — reuse for new test:
```csharp
private static string WriteTempYaml(string content)
{
    var path = System.IO.Path.GetTempFileName() + ".yaml";
    System.IO.File.WriteAllText(path, content);
    return path;
}
```

**Existing warning assertion pattern** (lines 73-78) — copy for empty-entities warning test:
```csharp
Assert.Contains(messages, m =>
    m.Contains("covariates") || m.Contains("groups") || m.Contains("ignored"));
```

**New test to add** — `Load_EmptyEntities_LogsWarning_DoesNotThrow`:
```csharp
[Fact]
public void Load_EmptyEntities_LogsWarning_DoesNotThrow()
{
    // Arrange
    var yaml = "entities: []";
    var path = WriteTempYaml(yaml);
    var (logger, messages) = CreateCapturingLogger();

    // Act — must NOT throw
    var config = EntitiesConfigLoader.Load(path, logger);

    // Assert: returned config has empty entities (not null)
    Assert.NotNull(config);
    Assert.Empty(config.Entities);

    // Assert: warning logged mentioning empty pipeline / UI
    Assert.Contains(messages, m =>
        m.Contains("no entities") || m.Contains("empty pipeline") || m.Contains("configure via UI"));
}
```

**New test to add** — `Load_NullEntitiesKey_LogsWarning_DoesNotThrow`:
```csharp
[Fact]
public void Load_NullEntitiesKey_LogsWarning_DoesNotThrow()
{
    // Arrange — YAML with no `entities:` key at all (options.json first-boot scenario)
    var yaml = "# empty config";
    var path = WriteTempYaml(yaml);
    var (logger, messages) = CreateCapturingLogger();

    // Act — must NOT throw
    var config = EntitiesConfigLoader.Load(path, logger);

    Assert.NotNull(config);
    Assert.Contains(messages, m => m.Contains("no entities") || m.Contains("empty pipeline"));
}
```

---

### NEW: `orchestrator/Argus.Orchestrator.Tests/ConfigWriterTests.cs` (test, file-I/O)

**Analog:** `orchestrator/Argus.Orchestrator.Tests/MqttConnectionTests.cs`

**Imports pattern** (`MqttConnectionTests.cs` lines 1-8):
```csharp
using Argus.Orchestrator.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.Orchestrator.Tests;
```
For `ConfigWriterTests`: replace `Mqtt` namespace with `Config`; no `NullLogger` needed (ConfigWriter has no logger).

**Async test method pattern** (`MqttConnectionTests.cs` lines 24-28):
```csharp
[Fact]
public async Task BuildConnectOptionsAsync_WillTopic_EndsWithAvailability()
{
    var conn = MakeConn();
    var opts = await conn.BuildConnectOptionsAsync(CancellationToken.None);
```

**New test class skeleton:**
```csharp
using Argus.Orchestrator.Config;
using Xunit;

namespace Argus.Orchestrator.Tests;

public class ConfigWriterTests
{
    [Fact]
    public async Task WriteAsync_ProducesFileWithExpectedContent()
    {
        // Arrange
        var target = Path.Combine(Path.GetTempPath(), $"entities-test-{Guid.NewGuid():N}.yaml");
        var writer = new ConfigWriter();
        var yaml = "entities:\n  - entity_id: sensor.test\n";

        // Act
        await writer.WriteAsync(target, yaml);

        // Assert
        Assert.True(File.Exists(target));
        Assert.Equal(yaml, await File.ReadAllTextAsync(target));

        // Cleanup
        File.Delete(target);
    }

    [Fact]
    public async Task WriteAsync_ConcurrentCalls_NeitherThrows()
    {
        // Arrange
        var target = Path.Combine(Path.GetTempPath(), $"entities-conc-{Guid.NewGuid():N}.yaml");
        var writer = new ConfigWriter();

        // Act — two concurrent writes; SemaphoreSlim(1,1) serializes them
        var t1 = writer.WriteAsync(target, "entities: []\n");
        var t2 = writer.WriteAsync(target, "entities: []\n");
        await Task.WhenAll(t1, t2);

        // Assert — file exists with valid content (one of the two writes won)
        Assert.True(File.Exists(target));

        // Cleanup
        File.Delete(target);
    }

    [Fact]
    public async Task WriteAsync_NoTempFileLeftBehind()
    {
        // Arrange
        var dir = Path.GetTempPath();
        var target = Path.Combine(dir, $"entities-tmp-{Guid.NewGuid():N}.yaml");
        var writer = new ConfigWriter();

        // Act
        await writer.WriteAsync(target, "entities: []\n");

        // Assert — no .tmp. files remain in the directory
        var orphans = Directory.GetFiles(dir, ".entities.tmp.*.yaml");
        Assert.Empty(orphans);

        // Cleanup
        File.Delete(target);
    }
}
```

---

### `orchestrator/Argus.Orchestrator/wwwroot/` (static assets)

**No codebase analog** — these are new binary/CSS files with no existing equivalent.

**Directory structure to create:**
```
orchestrator/Argus.Orchestrator/wwwroot/
├── js/
│   └── htmx.min.js    (download from cdn.jsdelivr.net/npm/htmx.org@2.0.10/dist/htmx.min.js)
└── css/
    └── argus.css       (new file — CSS custom properties per UI-SPEC.md)
```

**`argus.css` pattern** — CSS custom properties / BEM class naming from RESEARCH.md placeholder HTML:
```css
/* CSS custom properties for color tokens */
:root {
  --argus-bg: #1a1a2e;
  --argus-surface: #16213e;
  --argus-accent: #0f3460;
  --argus-text: #e0e0e0;
  --argus-ok: #4caf50;
  --argus-error: #f44336;
}

/* BEM-style class names matching placeholder HTML */
.argus-header { ... }
.argus-heading { ... }
.argus-main { ... }
.argus-display { ... }
.argus-body { ... }
.argus-status { ... }
.argus-status-dot { ... }
.argus-status-dot.status-ok { background-color: var(--argus-ok); }
.argus-status-dot.status-error { background-color: var(--argus-error); }
.argus-label { ... }
.argus-footer { ... }
```

---

## Shared Patterns

### SemaphoreSlim Async Serialization
**Source:** `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs` lines 34, 61-73
**Apply to:** `Config/ConfigWriter.cs`
```csharp
private readonly SemaphoreSlim _lock = new(1, 1);

await _lock.WaitAsync(ct);
try { /* critical section */ }
finally { _lock.Release(); }
```

### Structured Logging with EventId
**Source:** `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` + `Config/EntitiesConfigLoader.cs` lines 35-37
**Apply to:** `EntitiesConfigLoader.cs` (new warning), `LogEvents.cs` (new EventId)

LogEvents ID ranges already in use:
- 1xxx: Config loading (next available: 1003)
- 2xxx: gRPC / health (next available: 2014)
- 3xxx: HA listener (next available: 3004)
- 4xxx: MQTT (next available: 4009)
- 5xxx: Batch (next available: 5012)
- 6xxx: Health publisher (next available: 6002)

New EventId to add to `LogEvents.cs`:
```csharp
public static readonly EventId EmptyEntitiesWarning = new(1003, nameof(EmptyEntitiesWarning));
```

### Sealed Singleton Class Pattern
**Source:** `orchestrator/Argus.Orchestrator/Mqtt/MqttConnection.cs` line 21; `orchestrator/Argus.Orchestrator/Health/ArgusHealthSignals.cs` line 8
**Apply to:** `Config/ConfigWriter.cs`
```csharp
public sealed class ConfigWriter  // sealed — single-responsibility singleton
```

### Test: CapturingLogger Infrastructure
**Source:** `orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs` lines 119-139
**Apply to:** All new tests in `EntitiesConfigTests.cs` that assert log output — reuse the existing `CapturingLoggerProvider` and `CapturingLogger` inner classes already in the file; do not duplicate them.

### Test: NullLogger for No-Output Services
**Source:** `orchestrator/Argus.Orchestrator.Tests/MqttConnectionTests.cs` line 19
**Apply to:** `ConfigWriterTests.cs` — `ConfigWriter` has no `ILogger` parameter, so `NullLogger` is not needed. For any future service test that takes an `ILogger`, use `NullLogger<T>.Instance` (already imported via `Microsoft.Extensions.Logging.Abstractions` in tests csproj).

### YamlDotNet Deserializer Configuration
**Source:** `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` lines 24-27
**Apply to:** `Config/ConfigWriter.cs` serialize path (Phase 2+) — when serializing, use matching `UnderscoredNamingConvention` so round-trip YAML keys are consistent:
```csharp
var deserializer = new DeserializerBuilder()
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .IgnoreUnmatchedProperties()
    .Build();
```

---

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `orchestrator/Argus.Orchestrator/wwwroot/js/htmx.min.js` | static | — | Third-party download artifact; no codebase analog |
| `orchestrator/Argus.Orchestrator/wwwroot/css/argus.css` | static | — | First CSS file in project; no codebase analog; follow UI-SPEC.md token names |

---

## Metadata

**Analog search scope:** `orchestrator/Argus.Orchestrator/`, `orchestrator/Argus.Orchestrator.Tests/`, `argus/`
**Files scanned:** 15 source files read directly
**Pattern extraction date:** 2026-06-30
