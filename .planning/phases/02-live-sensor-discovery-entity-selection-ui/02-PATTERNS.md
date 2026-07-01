# Phase 2: Live Sensor Discovery + Entity Selection UI - Pattern Map

**Mapped:** 2026-07-01
**Files analyzed:** 9 new/modified files
**Analogs found:** 9 / 9

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `orchestrator/Argus.Orchestrator/Ha/HaWebSocketClient.cs` | model/DTO | request-response | self (extend) | self |
| `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` | service | event-driven | self (extend) | self |
| `orchestrator/Argus.Orchestrator/Ha/HaSensorRegistry.cs` (NEW) | service/singleton | request-response | `orchestrator/Argus.Orchestrator/Health/ArgusHealthSignals.cs` | role-match |
| `orchestrator/Argus.Orchestrator/Config/GlobExpander.cs` (NEW) | utility | transform | `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` (`SelectDiscoverableSensors`) | role-match |
| `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs` (NEW) | component/page-builder | request-response | `orchestrator/Argus.Orchestrator/PlaceholderPage.cs` | exact |
| `orchestrator/Argus.Orchestrator/Program.cs` | config/route | request-response | self (extend) | self |
| `orchestrator/Argus.Orchestrator/Config/ConfigWriter.cs` | utility | file-I/O | self (reuse) | self |
| `orchestrator/Argus.Orchestrator/wwwroot/css/argus.css` | config/style | — | self (extend) | self |
| `argus/rootfs/etc/cont-init.d/10-config-gen.sh` | config/init | batch | self (extend) | self |

---

## Pattern Assignments

### `orchestrator/Argus.Orchestrator/Ha/HaWebSocketClient.cs` (extend)

**Change:** Extend `HaStateDto` record with two nullable positional parameters; update the two `new HaStateDto(...)` construction sites; parse `attributes` object in `GetStatesAsync`.

**Analog:** self — read the actual file at `orchestrator/Argus.Orchestrator/Ha/HaWebSocketClient.cs`

**Existing HaStateDto record** (line 10):
```csharp
internal sealed record HaStateDto(string EntityId, string? State, DateTime LastChangedUtc);
```

**Required change — extend record** (line 10):
```csharp
internal sealed record HaStateDto(
    string EntityId,
    string? State,
    DateTime LastChangedUtc,
    string? UnitOfMeasurement,   // from attributes.unit_of_measurement
    string? FriendlyName);       // from attributes.friendly_name
```

**Construction site 1 — GetStatesAsync** (line 74, inside `foreach (var st in arr.EnumerateArray())`):
```csharp
// BEFORE:
list.Add(new HaStateDto(entityId, state, ParseUtc(st, "last_changed")));

// AFTER: parse attributes first, then construct
string? unit = null;
string? friendlyName = null;
if (st.TryGetProperty("attributes", out var attrs))
{
    if (attrs.TryGetProperty("unit_of_measurement", out var u)) unit = u.GetString();
    if (attrs.TryGetProperty("friendly_name", out var fn)) friendlyName = fn.GetString();
}
list.Add(new HaStateDto(entityId, state, ParseUtc(st, "last_changed"), unit, friendlyName));
```

**Construction site 2 — ReceiveEventsAsync** (line 125):
```csharp
// BEFORE:
onState(new HaStateDto(entityId, state, ParseUtc(ns, "last_changed")));

// AFTER: state_changed new_state also has attributes
string? unit2 = null;
string? friendlyName2 = null;
if (ns.TryGetProperty("attributes", out var attrs2))
{
    if (attrs2.TryGetProperty("unit_of_measurement", out var u2)) unit2 = u2.GetString();
    if (attrs2.TryGetProperty("friendly_name", out var fn2)) friendlyName2 = fn2.GetString();
}
onState(new HaStateDto(entityId, state, ParseUtc(ns, "last_changed"), unit2, friendlyName2));
```

**WARNING — positional record constructor break:** Any test constructing `new HaStateDto(entityId, state, dt)` with 3 args will not compile after this change. Search for `new HaStateDto(` in tests and add `null, null` as the two new trailing args.

---

### `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` (extend)

**Change:** Inject `IHaSensorRegistry` into constructor; call `UpdateSnapshot` after `client.GetStatesAsync` on every connect (both first connect and reconnect).

**Analog:** self — the existing injection pattern and the `_signals.HaConnected = true` assignment immediately after `GetStatesAsync` (lines 111, 117) is the exact hook site.

**Imports pattern** (lines 1-6 — add one using):
```csharp
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Health;
using Argus.Orchestrator.Logging;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
```
Add: no new namespace needed — `IHaSensorRegistry` lives in `Argus.Orchestrator.Ha` (same namespace).

**Constructor injection pattern** (lines 42-58 — add registry parameter):
```csharp
// EXISTING constructor pattern to copy:
public NetDaemonHaEventSource(
    ConnectionSettings settings,
    EntitiesConfig entitiesConfig,
    ReconnectCooldown cooldown,
    ArgusHealthSignals signals,
    ILogger<NetDaemonHaEventSource> logger)
{
    _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    // ... same null-guard pattern for all params
    _configuredEntities = new HashSet<string>(...);
}

// EXTEND: add IHaSensorRegistry registry parameter with same null-guard pattern
private readonly IHaSensorRegistry _sensorRegistry;

// In constructor body:
_sensorRegistry = registry ?? throw new ArgumentNullException(nameof(registry));
```

**Registry update hook** (after line 117 `var states = await client.GetStatesAsync(ct)`):
```csharp
var states = await client.GetStatesAsync(ct).ConfigureAwait(false);

// NEW: populate registry on every connect (both first connect and reconnect)
// ADR-4 compliant: reuses the get_states call already made here — no second WebSocket
_sensorRegistry.UpdateSnapshot(states, _configuredEntities);

if (isFirstConnection)
{
    LogDiscoverableSensors(states);
}
else
{
    // ... existing reconnect path
}
```

---

### `orchestrator/Argus.Orchestrator/Ha/HaSensorRegistry.cs` (NEW)

**Analog:** `orchestrator/Argus.Orchestrator/Health/ArgusHealthSignals.cs`

**Volatile singleton pattern from ArgusHealthSignals** (lines 1-21):
```csharp
namespace Argus.Orchestrator.Health;

public sealed class ArgusHealthSignals
{
    // volatile ensures cross-thread visibility without a lock
    public volatile bool HaConnected;
    public volatile bool DetectorConnected;
}
```

**Imports pattern** (copy namespace/using structure from `ArgusHealthSignals.cs` + add needed usings):
```csharp
using Argus.Orchestrator.Ha;
using System.Globalization;

namespace Argus.Orchestrator.Ha;
```

**Interface to create — `IHaSensorRegistry`** (new file `Ha/IHaSensorRegistry.cs`):
```csharp
namespace Argus.Orchestrator.Ha;

public interface IHaSensorRegistry
{
    /// <summary>All cached numeric-sensor entries, ordered by entity_id.</summary>
    IReadOnlyList<HaSensorEntry> GetAll();

    /// <summary>Entries whose entity_id contains <paramref name="q"/> (case-insensitive).</summary>
    IReadOnlyList<HaSensorEntry> GetFiltered(string q);

    /// <summary>Replaces the snapshot. Thread-safe — called by NetDaemonHaEventSource on connect.</summary>
    void UpdateSnapshot(IReadOnlyList<HaStateDto> states, HashSet<string> trackedEntityIds);
}

public record HaSensorEntry(
    string EntityId,
    double CurrentValue,
    string? UnitOfMeasurement,
    string? FriendlyName,
    bool IsTracked);
```

**Core volatile-reference-swap pattern** (mirrors `ArgusHealthSignals` volatile field — single writer, many readers):
```csharp
public sealed class HaSensorRegistry : IHaSensorRegistry
{
    // volatile: latest reference is visible to all threads without a read lock.
    // Single-writer guarantee: NetDaemonHaEventSource.RunConnectionLoopAsync is single-threaded.
    private volatile IReadOnlyList<HaSensorEntry> _snapshot = Array.Empty<HaSensorEntry>();

    public IReadOnlyList<HaSensorEntry> GetAll() => _snapshot;

    public IReadOnlyList<HaSensorEntry> GetFiltered(string q) =>
        string.IsNullOrWhiteSpace(q)
            ? _snapshot
            : _snapshot.Where(e => e.EntityId.Contains(q, StringComparison.OrdinalIgnoreCase))
                       .ToList();

    public void UpdateSnapshot(IReadOnlyList<HaStateDto> states, HashSet<string> trackedEntityIds)
    {
        var entries = states
            .Where(s => double.TryParse(s.State, NumberStyles.Any,
                CultureInfo.InvariantCulture, out _))
            .Select(s =>
            {
                double.TryParse(s.State, NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var value);
                return new HaSensorEntry(
                    s.EntityId,
                    value,
                    s.UnitOfMeasurement,
                    s.FriendlyName,
                    trackedEntityIds.Contains(s.EntityId));
            })
            .OrderBy(e => e.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _snapshot = entries; // volatile write — atomic reference swap
    }
}
```

**Key pattern notes:**
- `volatile` on the reference field (not `lock`) — matches `ArgusHealthSignals.HaConnected` pattern exactly.
- Numeric filter uses `double.TryParse(..., NumberStyles.Any, CultureInfo.InvariantCulture, out _)` — same invariant-culture pattern as `SelectDiscoverableSensors` in `NetDaemonHaEventSource.cs` (line 259).
- `Array.Empty<HaSensorEntry>()` as initial value — zero-allocation empty, matches `Array.Empty` conventions in .NET 8.

---

### `orchestrator/Argus.Orchestrator/Config/GlobExpander.cs` (NEW)

**Analog:** `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` — `SelectDiscoverableSensors` static pure method (lines 250-263)

**Pure-static-method pattern from SelectDiscoverableSensors** (lines 250-263):
```csharp
internal static IReadOnlyList<(string EntityId, double Value)> SelectDiscoverableSensors(
    IEnumerable<(string EntityId, string? State)> states,
    HashSet<string> configuredEntities)
{
    var result = new List<(string, double)>();
    foreach (var (entityId, state) in states)
    {
        if (configuredEntities.Contains(entityId))
            continue;
        if (!double.TryParse(state, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            continue;
        result.Add((entityId, value));
    }
    return result;
}
```

**Core pattern for GlobExpander** (pure static, no deps, `FileSystemName.MatchesSimpleExpression`):
```csharp
using System.IO.Enumeration;

namespace Argus.Orchestrator.Config;

/// <summary>
/// Pure static glob expander: applies include/exclude glob patterns to a sensor snapshot.
/// Uses System.IO.Enumeration.FileSystemName.MatchesSimpleExpression (BCL, net8.0, zero deps).
/// </summary>
public static class GlobExpander
{
    public static HashSet<string> Resolve(
        IReadOnlyList<HaSensorEntry> snapshot,
        IEnumerable<string> includePatterns,
        IEnumerable<string> excludePatterns,
        IEnumerable<string> manuallyChecked,
        IEnumerable<string> manuallyUnchecked)
    {
        var allIds = snapshot.Select(e => e.EntityId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var includes = includePatterns
            .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var excludes = excludePatterns
            .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

        HashSet<string> patternSelected;
        if (includes.Count == 0)
        {
            patternSelected = new HashSet<string>(allIds, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            patternSelected = allIds
                .Where(id => includes.Any(p =>
                    FileSystemName.MatchesSimpleExpression(p, id, ignoreCase: true)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var id in allIds.Where(id =>
            excludes.Any(p => FileSystemName.MatchesSimpleExpression(p, id, ignoreCase: true))))
            patternSelected.Remove(id);

        foreach (var id in manuallyChecked) patternSelected.Add(id);
        foreach (var id in manuallyUnchecked) patternSelected.Remove(id);

        return patternSelected;
    }
}
```

**Key pattern notes:**
- `using System.IO.Enumeration;` — BCL namespace, no NuGet, net8.0 built-in.
- `FileSystemName.MatchesSimpleExpression(pattern, input, ignoreCase: true)` — supports `*` and `?`; no `[...]` brackets needed for HA entity_id patterns.
- Same `StringComparer.OrdinalIgnoreCase` HashSet convention as `_configuredEntities` in `NetDaemonHaEventSource`.
- `internal` visibility matches `SelectDiscoverableSensors` convention — but `public` is acceptable since it will be called from `Program.cs` save handler.

---

### `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs` (NEW)

**Analog:** `orchestrator/Argus.Orchestrator/PlaceholderPage.cs` (lines 1-71)

**Imports pattern** (lines 1-5 of PlaceholderPage.cs):
```csharp
using Argus.Orchestrator.Health;
using System.Net;
using System.Reflection;

namespace Argus.Orchestrator;
```
Copy for EntityPickerPage (adjust namespace and usings):
```csharp
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Health;
using System.Net;
using System.Reflection;

namespace Argus.Orchestrator.Web;
```

**Static class + version field pattern** (lines 18-20 of PlaceholderPage.cs):
```csharp
public static class PlaceholderPage
{
    private static readonly string _version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
```
Copy verbatim for `EntityPickerPage`:
```csharp
public static class EntityPickerPage
{
    private static readonly string _version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
```

**HTML encoding safety pattern** (line 36 of PlaceholderPage.cs):
```csharp
// T-01-08: HTML-encode ingressPath to prevent attribute injection
var safeIngressPath = WebUtility.HtmlEncode(ingressPath);
```
Apply same `WebUtility.HtmlEncode` to ALL user-derived values interpolated into HTML:
- `ingressPath` → `safeIngressPath` (base href)
- search query `q` → `WebUtility.HtmlEncode(q)` (no-results copy)
- entity_id values → `WebUtility.HtmlEncode(entry.EntityId)` (checkbox value + display text)
- friendly_name values → `WebUtility.HtmlEncode(entry.FriendlyName)` (display text)
- current value (double) → safe (rendered via `.ToString("G")`, not user input)

**Raw-string interpolation pattern** (lines 41-70 of PlaceholderPage.cs — `$$"""..."""`):
```csharp
return $$"""
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="UTF-8">
      <meta name="viewport" content="width=device-width, initial-scale=1.0">
      <title>Argus</title>
      <base href="{{safeIngressPath}}/">
      <link rel="stylesheet" href="css/argus.css">
      <script src="js/htmx.min.js" defer></script>
    </head>
    <body>
      <header class="argus-header">
        <span class="argus-heading">Argus</span>
      </header>
      <main class="argus-main">
        ...
      </main>
      <footer class="argus-footer">
        <span class="argus-label">v{{_version}}</span>
      </footer>
    </body>
    </html>
    """;
```

**Two methods to implement** (following PlaceholderPage.Build signature convention):
```csharp
/// <summary>Builds the full picker page (GET /sensors).</summary>
public static string BuildFullPage(string ingressPath, IHaSensorRegistry registry,
    EntitiesConfig config, ArgusHealthSignals health, string q)

/// <summary>Builds the sensor list fragment only (GET /api/sensors, htmx swap target).</summary>
public static string BuildListFragment(IHaSensorRegistry registry,
    EntitiesConfig config, string q)
```

**List row HTML pattern** (inline in the raw-string interpolation, per 02-UI-SPEC.md):
```html
<li class="argus-list-row argus-list-row--tracked">
  <input class="argus-checkbox" type="checkbox" name="entities"
         value="{entry.EntityId}" checked aria-label="{entry.EntityId}">
  <div class="argus-row-content">
    <span class="argus-row-entity-id">{entry.EntityId}</span>
    <!-- conditional: only when FriendlyName is present and differs from EntityId -->
    <span class="argus-row-friendly-name">{entry.FriendlyName}</span>
  </div>
  <div class="argus-row-meta">
    <span class="argus-row-value">{entry.CurrentValue:G} {entry.UnitOfMeasurement}</span>
    <!-- conditional: only when IsTracked -->
    <span class="argus-pill argus-pill--tracked">tracked</span>
  </div>
</li>
```

**Empty state HTML pattern** (per 02-UI-SPEC.md, rendered inside `#argus-sensor-list`):
```html
<div class="argus-empty">
  <p class="argus-body">No sensors found.</p>
  <p class="argus-label">Argus has not yet received a sensor snapshot from Home Assistant.
  Check that the add-on can reach the Supervisor and that the detector is running.</p>
</div>
```

---

### `orchestrator/Argus.Orchestrator/Program.cs` (extend)

**Analog:** self — copy endpoint registration pattern from existing `app.MapGet("/", ...)` (lines 185-189)

**Existing endpoint registration pattern** (lines 185-189):
```csharp
app.MapGet("/", (HttpRequest req, ArgusHealthSignals health) =>
{
    var ip = req.Headers["X-Ingress-Path"].FirstOrDefault() ?? "";
    return Results.Content(PlaceholderPage.Build(ip, health), "text/html");
});
```

**New singleton registrations to add** (after existing `builder.Services.AddSingleton<ConfigWriter>()` at line 114):
```csharp
// Register HaSensorRegistry singleton (Phase 2): populated by NetDaemonHaEventSource on connect
builder.Services.AddSingleton<IHaSensorRegistry, HaSensorRegistry>();
```

**New endpoint registrations to add** (after existing `app.MapGet("/", ...)` at line 185):
```csharp
// Redirect root to picker (Phase 2 replaces placeholder)
app.MapGet("/", () => Results.Redirect("/sensors"));

// GET /sensors — full picker page
app.MapGet("/sensors", (HttpRequest req, IHaSensorRegistry registry,
    EntitiesConfig config, ArgusHealthSignals health) =>
{
    var ip = req.Headers["X-Ingress-Path"].FirstOrDefault() ?? "";
    var q = req.Query["q"].FirstOrDefault() ?? "";
    return Results.Content(
        EntityPickerPage.BuildFullPage(ip, registry, config, health, q), "text/html");
});

// GET /api/sensors — htmx list fragment
app.MapGet("/api/sensors", (HttpRequest req, IHaSensorRegistry registry, EntitiesConfig config) =>
{
    var q = req.Query["q"].FirstOrDefault() ?? "";
    return Results.Content(
        EntityPickerPage.BuildListFragment(registry, config, q), "text/html");
});

// POST /api/sensors/save — save selection + patterns; returns banner fragment
app.MapPost("/api/sensors/save", async (HttpRequest req, IHaSensorRegistry registry,
    ConfigWriter writer, ConnectionSettings settings,
    ILogger<Program> logger, CancellationToken ct) =>
{
    try
    {
        var form = await req.ReadFormAsync(ct);
        var selectedIds = form["entities"].Where(s => !string.IsNullOrEmpty(s)).ToList();
        var includeRaw = form["include_patterns"].FirstOrDefault() ?? "";
        var excludeRaw = form["exclude_patterns"].FirstOrDefault() ?? "";
        // ... resolve, build YAML, write, create lock file
        // return Results.Content(<banner-html>, "text/html")
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "UI save failed");
        return Results.Content(<error-banner-html>, "text/html");
    }
});
```

**X-Ingress-Path read pattern** (lines 186-188 — copy verbatim for all new endpoints):
```csharp
var ip = req.Headers["X-Ingress-Path"].FirstOrDefault() ?? "";
```

**ConnectionSettings.EntitiesPath access pattern** (lines 15-16 of Program.cs):
```csharp
var entitiesPath = builder.Configuration["ARGUS_ENTITIES_PATH"] ?? "entities.yaml";
// ... stored in connectionSettings.EntitiesPath = entitiesPath (line 36)
```
Access in save handler via injected `ConnectionSettings settings`:
```csharp
var entitiesPath = settings.EntitiesPath ?? "/data/entities.yaml";
```

---

### `orchestrator/Argus.Orchestrator/Config/ConfigWriter.cs` (reuse — no change)

**Analog:** self — already implements the atomic write pattern. No modifications needed in Phase 2.

**Existing WriteAsync signature** (lines 16-37):
```csharp
public async Task WriteAsync(string targetPath, string yaml,
    CancellationToken ct = default)
```
Call from save handler:
```csharp
await writer.WriteAsync(entitiesPath, fullYaml, ct);
```

**YAML construction pattern for the save endpoint** (caller's responsibility per ConfigWriter comment line 9):
```csharp
// Build _patterns: block as literal string prefix (safe because YamlDotNet
// deserializer has IgnoreUnmatchedProperties() — EntitiesConfigLoader.cs line 24)
var patternsBlock = BuildPatternsYaml(includePatterns, excludePatterns);

// Serialize entities using YamlDotNet with UnderscoredNamingConvention
var serializer = new SerializerBuilder()
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .Build();
var entitiesYaml = serializer.Serialize(new EntitiesConfig { Entities = resolvedEntities });

var fullYaml = patternsBlock + entitiesYaml;
await writer.WriteAsync(entitiesPath, fullYaml, ct);
```

**Lock file creation pattern** (after successful WriteAsync):
```csharp
var lockPath = Path.Combine(Path.GetDirectoryName(entitiesPath)!, ".ui_config_present");
await File.WriteAllTextAsync(lockPath, string.Empty, ct);
```

**`_patterns:` YAML block format** (safe to prepend — `IgnoreUnmatchedProperties()` verified on `EntitiesConfigLoader.cs` line 24):
```yaml
_patterns:
  include:
    - sensor.*temp*
  exclude:
    - sensor.*test*
```

---

### `orchestrator/Argus.Orchestrator/wwwroot/css/argus.css` (extend)

**Analog:** self — extend the existing file; never redefine tokens declared in lines 7-39.

**Existing token reference pattern** (lines 7-39 — all tokens to reference, not redefine):
```css
/* Use these tokens — do NOT redeclare them */
--color-surface, --color-element, --color-accent, --color-border
--color-text-primary, --color-text-secondary
--color-status-ok, --color-status-error
--space-xs (4px), --space-sm (8px), --space-md (16px), --space-lg (24px)
--font-size-body (14px), --font-size-label (12px), --font-size-heading (20px)
--font-weight-regular (400), --font-weight-semibold (600)
--line-height-body (1.5), --line-height-label (1.4), --line-height-heading (1.2)
--font-family
```

**Existing BEM class naming convention** (lines 91-170 — follow same `argus-{block}__element--modifier` pattern):
```css
/* Existing examples to copy naming convention from: */
.argus-header { ... }
.argus-main { ... }
.argus-status { ... }
.argus-status-dot { ... }
.argus-status-dot.status-ok { ... }
```

**New blocks to add** (per 02-UI-SPEC.md — append after existing rules):
- `.argus-search` / `.argus-search__input`
- `.argus-list` / `.argus-list-row` / `.argus-list-row--tracked` / `.argus-list-row:last-child` / `.argus-list-row:hover`
- `.argus-checkbox`
- `.argus-row-content` / `.argus-row-entity-id` / `.argus-row-friendly-name`
- `.argus-row-meta` / `.argus-row-value`
- `.argus-pill` / `.argus-pill--tracked`
- `.argus-filters` / `.argus-filters__group` / `.argus-filters__label` / `.argus-filters__textarea`
- `.argus-save-bar`
- `.argus-btn` / `.argus-btn--primary` / `.argus-btn--primary:hover` / `.argus-btn--primary:active` / `.argus-btn[disabled]` / `.argus-btn.htmx-request`
- `#argus-spinner` / `#argus-spinner.htmx-request` / `@keyframes argus-spin`
- `.argus-banner` / `.argus-banner--success` / `.argus-banner--error`
- `.argus-empty`

**Spinner CSS — critical correction** (per 02-RESEARCH.md §7, Pitfall 3):
```css
/* htmx adds .htmx-request TO the indicator element, not a parent */
#argus-spinner { display: none; width: 16px; height: 16px;
    border: 2px solid var(--color-border);
    border-top-color: var(--color-accent);
    border-radius: 50%; animation: argus-spin 0.6s linear infinite; }
#argus-spinner.htmx-request { display: inline-block; }  /* NOT .htmx-request #argus-spinner */
@keyframes argus-spin { to { transform: rotate(360deg); } }
```

---

### `argus/rootfs/etc/cont-init.d/10-config-gen.sh` (extend)

**Analog:** self — edit lines 110-112 only; all other sections unchanged.

**Existing pattern to modify** (lines 110-112):
```bash
# ── entities.yaml Generation (UICFG-08) ────────────────────────────────────
printf "/data/entities.yaml" > /var/run/s6/container_environment/ARGUS_ENTITIES_PATH
python3 /usr/local/bin/gen-entities.py /data/options.json > /data/entities.yaml
```

**Replacement — add `.ui_config_present` guard** (replace lines 111-112):
```bash
# ── entities.yaml Generation (UICFG-08) ────────────────────────────────────
printf "/data/entities.yaml" > /var/run/s6/container_environment/ARGUS_ENTITIES_PATH
if [ -f /data/.ui_config_present ]; then
    bashio::log.info "UI config present — skipping gen-entities.py (entities.yaml preserved)."
else
    python3 /usr/local/bin/gen-entities.py /data/options.json > /data/entities.yaml
fi
```

**Existing bashio pattern to follow** (lines 21-28, `write_optional_env`, `bashio::log.info` at line 114):
```bash
bashio::log.info "Config-gen complete."
```
Use the same `bashio::log.info` call style for the skip message.

**SEQUENCING REQUIREMENT:** This file MUST be edited and committed BEFORE the `POST /api/sensors/save` endpoint is wired. See 02-RESEARCH.md Pitfall 2.

---

## Tests

### `orchestrator/Argus.Orchestrator.Tests/HaSensorRegistryTests.cs` (NEW)

**Analog:** `orchestrator/Argus.Orchestrator.Tests/StartupSensorLogTests.cs` and `orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs`

**Test file structure pattern** (from `StartupSensorLogTests.cs` lines 1-10):
```csharp
using Argus.Orchestrator.Ha;
using Xunit;

namespace Argus.Orchestrator.Tests;

public class HaSensorRegistryTests
{
    // static HashSet<string> TrackedEntities = ...
    // [Fact] methods
}
```

**HaStateDto construction in tests** — after extending the record to 5 positional args, construct as:
```csharp
new HaStateDto("sensor.outdoor_temp", "21.5", DateTime.UtcNow, "°C", "Outdoor Temp")
// OR with nulls for non-attribute tests:
new HaStateDto("sensor.outdoor_temp", "21.5", DateTime.UtcNow, null, null)
```

**Test scenarios to cover:**
1. `UpdateSnapshot` with numeric states returns correct `HaSensorEntry` records
2. `UpdateSnapshot` filters non-numeric states (matches `SelectDiscoverableSensors` test pattern)
3. `GetFiltered("")` returns full snapshot
4. `GetFiltered("temp")` returns only matching entries (case-insensitive)
5. `IsTracked` correctly set based on `trackedEntityIds` HashSet
6. Thread-safety: concurrent `UpdateSnapshot` + `GetAll` does not throw

### `orchestrator/Argus.Orchestrator.Tests/GlobExpanderTests.cs` (NEW)

**Analog:** `orchestrator/Argus.Orchestrator.Tests/StartupSensorLogTests.cs`

**Pure-static test pattern** (from `StartupSensorLogTests.cs`):
```csharp
public class GlobExpanderTests
{
    private static IReadOnlyList<HaSensorEntry> MakeSnapshot(params string[] entityIds) =>
        entityIds.Select(id => new HaSensorEntry(id, 0, null, null, false)).ToList();

    [Fact]
    public void Resolve_IncludePattern_SelectsMatchingEntities() { ... }
    [Fact]
    public void Resolve_ExcludePattern_RemovesMatchingEntities() { ... }
    [Fact]
    public void Resolve_ManualCheckOverridesExclude() { ... }
    [Fact]
    public void Resolve_ManualUncheckOverridesInclude() { ... }
    [Fact]
    public void Resolve_NoIncludePatterns_AllEntitiesAreBase() { ... }
    [Fact]
    public void Resolve_CaseInsensitiveMatch() { ... }
}
```

### `orchestrator/Argus.Orchestrator.Tests/SaveEndpointPatternsTests.cs` (NEW)

**Analog:** `orchestrator/Argus.Orchestrator.Tests/ConfigWriterTests.cs`

**Test patterns to copy:**
```csharp
// WriteTempYaml helper pattern (from EntitiesConfigTests.cs lines 145-150):
private static string WriteTempYaml(string content)
{
    var path = Path.GetTempFileName() + ".yaml";
    File.WriteAllText(path, content);
    return path;
}

// ConfigWriter round-trip pattern (from ConfigWriterTests.cs lines 9-25):
var target = Path.Combine(Path.GetTempPath(), $"entities-test-{Guid.NewGuid():N}.yaml");
var writer = new ConfigWriter();
await writer.WriteAsync(target, yaml);
Assert.True(File.Exists(target));
Assert.Equal(yaml, await File.ReadAllTextAsync(target));
```

**Scenarios to cover:**
1. `_patterns:` block in saved YAML is skipped by `EntitiesConfigLoader` (round-trip safe)
2. Lock file `.ui_config_present` is created after successful save
3. Zero entities selected results in `entities: []` (not an error)
4. `BuildPatternsYaml` produces valid YAML with correct `_patterns:` structure

### Existing tests requiring updates

**`HaEventFilterTests.cs` and `StartupSensorLogTests.cs`** — no changes needed (these tests use `NetDaemonHaEventSource.TryMap` and `SelectDiscoverableSensors` which only take `entityId` and `State`, not the full `HaStateDto` record).

**Any test constructing `new HaStateDto(...)` with 3 positional args** — must be updated to 5 args. Search for `new HaStateDto(` in the test project and add `, null, null` trailing args. Based on current codebase inspection, production code construction sites are only in `HaWebSocketClient.cs` lines 74 and 125; verify no test files directly construct the record.

---

## Shared Patterns

### HTML Safety (WebUtility.HtmlEncode)
**Source:** `orchestrator/Argus.Orchestrator/PlaceholderPage.cs` line 36
**Apply to:** `EntityPickerPage.cs` — ALL user-originated strings interpolated into HTML attributes or text nodes
```csharp
var safeIngressPath = WebUtility.HtmlEncode(ingressPath); // base href attribute
// Also apply to: entity_id values in checkbox value= and display text
// Also apply to: friendly_name values in display text
// Also apply to: search query q in no-results copy
```

### Structured Logging (LogEvents EventId constants)
**Source:** `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` (lines 10-57)
**Apply to:** All new log calls in `HaSensorRegistry`, `Program.cs` save handler
```csharp
// Add new EventId constants to LogEvents.cs for Phase 2 events (7xxx range):
public static readonly EventId SensorRegistryUpdated = new(7001, nameof(SensorRegistryUpdated));
public static readonly EventId UiSaveSucesss          = new(7002, nameof(UiSaveSuccess));
public static readonly EventId UiSaveFailed            = new(7003, nameof(UiSaveFailed));
```
Follow the existing naming convention: 4-digit ID, PascalCase name matching field name.

### Invariant-Culture Numeric Parsing
**Source:** `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` lines 257-259
**Apply to:** `HaSensorRegistry.UpdateSnapshot` (numeric state filter)
```csharp
if (!double.TryParse(state, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
    continue;
```

### CapturingLogger Test Helper
**Source:** `orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs` lines 153-173
**Apply to:** Any Phase 2 test that needs to assert on log output
```csharp
internal class CapturingLoggerProvider : ILoggerProvider { ... }
internal class CapturingLogger : ILogger { ... }
```
The helper is already defined in `EntitiesConfigTests.cs`. Do NOT duplicate it — either move it to a shared `TestHelpers.cs` file or reference the existing class (note: it is `internal` to the test project, so accessible within the same assembly).

### Null-Guard Constructor Pattern
**Source:** `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` lines 49-57
**Apply to:** `HaSensorRegistry` constructor (if it takes constructor params — it should be a no-arg singleton)
```csharp
_settings = settings ?? throw new ArgumentNullException(nameof(settings));
```

### SemaphoreSlim Serialization
**Source:** `orchestrator/Argus.Orchestrator/Config/ConfigWriter.cs` lines 14-37
**Apply to:** `ConfigWriter.WriteAsync` — already implemented; reuse without modification
```csharp
private readonly SemaphoreSlim _lock = new(1, 1);
await _lock.WaitAsync(ct);
// ... temp-file + File.Move(overwrite:true) ...
_lock.Release();
```

---

## No Analog Found

All files have close matches. No entries in this section.

---

## Metadata

**Analog search scope:** `orchestrator/Argus.Orchestrator/`, `orchestrator/Argus.Orchestrator.Tests/`, `argus/rootfs/etc/cont-init.d/`
**Files scanned:** 13 source files read in full
**Pattern extraction date:** 2026-07-01

**Critical implementation order (sequencing constraint):**
1. Extend `HaStateDto` record + fix construction sites (compile gate — everything else depends on this)
2. Edit `10-config-gen.sh` guard (must precede save endpoint — Pitfall 2)
3. Implement `IHaSensorRegistry` + `HaSensorRegistry` + inject into `NetDaemonHaEventSource`
4. Implement `GlobExpander`
5. Implement `EntityPickerPage`
6. Add CSS classes to `argus.css`
7. Register in `Program.cs` (GET /sensors, GET /api/sensors, POST /api/sensors/save)
8. Tests
