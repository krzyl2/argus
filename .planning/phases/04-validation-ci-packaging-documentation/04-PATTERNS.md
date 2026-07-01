# Phase 4: Validation, CI Packaging + Documentation - Pattern Map

**Mapped:** 2026-07-01
**Files analyzed:** 6 new/modified files
**Analogs found:** 6 / 6

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `orchestrator/Argus.Orchestrator/Config/InputValidator.cs` | utility (validator) | request-response | `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` | role-match (static class, BCL-only, returns errors) |
| `orchestrator/Argus.Orchestrator/Workers/ConfigFileWatcherService.cs` | worker (BackgroundService) | event-driven | `orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs` | exact (BackgroundService + inner-CTS + Swap call) |
| `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs` (EXTEND) | component (HTML builder) | request-response | itself (Phase 3 state) | self-extend |
| `orchestrator/Argus.Orchestrator/wwwroot/css/argus.css` (EXTEND) | config (CSS) | — | itself (Phase 3 state) | self-extend |
| `.github/workflows/build.yml` (EXTEND) | config (CI) | — | itself (existing steps) | self-extend |
| `argus/DOCS.md` (EXTEND) | documentation | — | itself (existing structure) | self-extend |

---

## Pattern Assignments

### `orchestrator/Argus.Orchestrator/Config/InputValidator.cs` (utility, request-response)

**Analog:** `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` — static class, BCL-only, iterates config tree, throws/returns structured errors.

**Secondary analog for double.TryParse pattern:** `orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs` `HstParams.From()` (lines 49–68).

**Imports pattern** (copy from EntitiesConfigLoader.cs lines 1–5):
```csharp
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Argus.Orchestrator.Config;
```

**Static class declaration pattern** (copy from EntitiesConfigLoader.cs lines 7, 12):
```csharp
namespace Argus.Orchestrator.Config;

public static class InputValidator
{
```

**Known-values allowlist + compiled Regex pattern** (modeled on EntitiesConfigLoader private static field conventions):
```csharp
// ← matches the project style: private static readonly, field at top of class
private static readonly Regex EntityIdRegex =
    new(@"^[a-z0-9_]+\.[a-z0-9_]+$", RegexOptions.Compiled);

private static readonly string[] KnownDetectors = { "hst", "mad", "stl" };
```

**Double.TryParse pattern** (copy from EntitiesConfig.cs lines 66–68 — HstParams.GetDouble):
```csharp
// Source: EntitiesConfig.cs lines 66-68 — the project-standard parse pattern
private static bool TryGetDouble(Dictionary<string, string> p, string key, out double val)
    => p.TryGetValue(key, out var v) &&
       double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out val);
```
Note: `NumberStyles.Any` + `CultureInfo.InvariantCulture` is the established project pattern — never use `Convert.ToDouble` or locale-sensitive overloads.

**Int parse pattern** (copy from EntitiesConfig.cs lines 63–64 — HstParams.GetInt):
```csharp
// Source: EntitiesConfig.cs lines 63-64
private static bool TryGetInt(Dictionary<string, string> p, string key, out int val)
    => p.TryGetValue(key, out var v) && int.TryParse(v, out val);
```

**Error collection + early-return pattern** (modeled on EntitiesConfigLoader.cs Validate lines 39–62):
```csharp
// Source: EntitiesConfigLoader.cs lines 39-62 — iterate + accumulate
public static List<string> Validate(
    IEnumerable<string> resolvedIds,
    Dictionary<int, List<DetectorConfig>> parsedDetectors)
{
    var errors = new List<string>();

    foreach (var id in resolvedIds)
    {
        if (!EntityIdRegex.IsMatch(id))
            errors.Add($"Invalid entity ID '{WebUtility.HtmlEncode(id)}'. Use format domain.object_id.");
    }

    foreach (var (ei, detectors) in parsedDetectors)
    {
        foreach (var (di, det) in detectors.Select((d, i) => (i, d)))
        {
            var name = det.Name?.ToLowerInvariant() ?? "";
            if (!KnownDetectors.Contains(name))
            {
                errors.Add($"Unknown detector type \"{WebUtility.HtmlEncode(det.Name)}\". Choose HST, MAD, or STL.");
                continue;  // skip param check for unknown type
            }
            // ... per-type param validation using TryGetDouble/TryGetInt above
        }
    }
    return errors;
}
```

**HtmlEncode pattern** (copy from EntityPickerPage.cs line 67, 262, 524):
```csharp
// T-02-07 / T-03-11: ALL user-originated strings HTML-encoded before interpolation
WebUtility.HtmlEncode(userValue)
```

**No LogEvents needed in InputValidator.** Logging happens at the call site in Program.cs (see save handler pattern below).

---

### `orchestrator/Argus.Orchestrator/Workers/ConfigFileWatcherService.cs` (worker, event-driven)

**Analog:** `orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs` — BackgroundService, constructor injection, stoppingToken pattern, inner-CTS dispose safety.

**Secondary analog:** `orchestrator/Argus.Orchestrator/Workers/HealthPublisherWorker.cs` — simpler `sealed BackgroundService` with injected logger, same constructor guard pattern.

**Imports pattern** (copy from HaListenerWorker.cs lines 1–9):
```csharp
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
```

**Class declaration pattern** (copy from HealthPublisherWorker.cs lines 27–44 — sealed + constructor guards):
```csharp
// Source: HealthPublisherWorker.cs lines 27-44 — sealed BackgroundService, null-guards
public sealed class ConfigFileWatcherService : BackgroundService
{
    private readonly ILiveEntitiesConfig _liveCfg;
    private readonly ConnectionSettings _settings;
    private readonly ILogger<ConfigFileWatcherService> _logger;

    public ConfigFileWatcherService(
        ILiveEntitiesConfig liveCfg,
        ConnectionSettings settings,
        ILogger<ConfigFileWatcherService> logger)
    {
        _liveCfg = liveCfg ?? throw new ArgumentNullException(nameof(liveCfg));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
```

**ExecuteAsync + stoppingToken.WaitHandle.WaitOne() pattern** (new for FSW; blocking synchronous wait is correct for a watcher-based service):
```csharp
// Source: HaListenerWorker.cs line 69 — override pattern; blocking variant for FSW
protected override Task ExecuteAsync(CancellationToken stoppingToken)
{
    var entitiesPath = _settings.EntitiesPath ?? "/data/entities.yaml";
    var dir = Path.GetDirectoryName(Path.GetFullPath(entitiesPath))!;
    var fileName = Path.GetFileName(entitiesPath);

    using var watcher = new FileSystemWatcher(dir)
    {
        NotifyFilter = NotifyFilters.FileName,
        EnableRaisingEvents = true,
    };

    Timer? _debounce = null;

    watcher.Renamed += (_, e) =>
    {
        if (!string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase)) return;
        // Interlocked.Exchange pattern: dispose old timer atomically (Research Q3 debounce pattern)
        Interlocked.Exchange(ref _debounce, new Timer(
            _ => Reload(entitiesPath),
            null,
            TimeSpan.FromMilliseconds(300),
            Timeout.InfiniteTimeSpan))?.Dispose();
    };

    stoppingToken.WaitHandle.WaitOne();   // block until host stops
    Interlocked.Exchange(ref _debounce, null)?.Dispose();
    return Task.CompletedTask;
}
```

**Reload helper — try/catch + LogWarning pattern** (copy from EntitiesConfigLoader.cs + HaListenerWorker.cs error handling):
```csharp
// Source: HaListenerWorker.cs lines 128-135 — catch OperationCanceledException separately;
//         EntitiesConfigLoader.cs lines 51-62 — Load throws InvalidOperationException / FileNotFoundException
private void Reload(string path)
{
    try
    {
        var newConfig = EntitiesConfigLoader.Load(path, _logger);
        _liveCfg.Swap(newConfig);
        _logger.LogInformation(LogEvents.ConfigReloadTriggered,
            "External edit detected — reloaded {Path}", path);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex,
            "External edit to {Path} failed validation — keeping current config", path);
    }
}
```

**LogEvents to use:** `LogEvents.ConfigReloadTriggered` (EventId 7004, already declared in LogEvents.cs line 51) — same event used by HaListenerWorker for pipeline restart. For a new watcher-specific event, add after 7006: `ConfigFileWatcherReloadFailed = new(7007, ...)` following the 7xxx block pattern (LogEvents.cs lines 46–53).

**DI registration pattern** (copy from Program.cs builder.Services block):
```csharp
// Source: Program.cs pattern — AddHostedService<T> for all BackgroundService registrations
builder.Services.AddHostedService<ConfigFileWatcherService>();
```

---

### `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs` (EXTEND — component, request-response)

**Analog:** itself (Phase 3 state). This is a pure extension — no new static class, no new file.

**Insertion point for InputValidator call in Program.cs save handler** (lines 315–395 of Program.cs):

Copy the existing save handler structure at Program.cs lines 279–405. The validation call inserts AFTER line 315 (`var parsedDetectors = DetectorFieldParser.Parse(formPairs);`) and BEFORE line 317 (`var snapshotById = registry.GetAll()...`):

```csharp
// Source: Program.cs lines 285-315 — existing pattern; insert validation after parsedDetectors
var validationErrors = InputValidator.Validate(resolvedIds, parsedDetectors);
if (validationErrors.Count > 0)
{
    logger.LogWarning(LogEvents.UiSaveFailed,
        "UI save blocked: {ErrorCount} validation error(s)", validationErrors.Count);
    return Results.Content(
        EntityPickerPage.BuildValidationBanner(validationErrors.Count),
        "text/html");
}
```

**BuildValidationBanner — new method pattern** (copy structure from EntityPickerPage.cs BuildErrorBanner lines 260–268):
```csharp
// Source: EntityPickerPage.cs lines 260-268 — BuildErrorBanner template to copy
public static string BuildValidationBanner(int errorCount)
{
    return $"""
        <div class="argus-banner argus-banner--validation"
             role="alert" aria-live="assertive">
          Save blocked: {errorCount} field(s) have invalid values. Correct the highlighted fields and try again.
        </div>
        """;
}
```

**BuildSuccessBanner extension — hasHst overload** (copy from EntityPickerPage.cs BuildSuccessBanner lines 233–241 and extend):
```csharp
// Source: EntityPickerPage.cs lines 233-241 — copy and add hasHst param
public static string BuildSuccessBanner(int count, bool hasHst = false)
{
    var warmupNote = hasHst
        ? """
              <p class="argus-warmup-note">
                HST detectors need ~4 minutes of readings to warm up (window=250 at ~1 reading/s).
                Anomaly scores will be low until warm-up completes.
              </p>
          """
        : "";

    return $"""
        <div class="argus-banner argus-banner--success"
             role="status" aria-live="polite">
          Saved — pipeline active. {count} {(count == 1 ? "entity" : "entities")} tracked.
          {warmupNote}
        </div>
        """;
}
```

**Param grid id rename pattern** — existing `BuildHstParamGrid`, `BuildMadParamGrid`, `BuildStlParamGrid` (EntityPickerPage.cs lines 407–506) use `id="p_{entityIdx}_{detIdx}_{key}"`. Phase 4 migrates to `id="param-{entityIdx}-{detIdx}-{key}"` per 04-UI-SPEC. The pattern to copy for each `<div class="argus-param-field">` block:

```csharp
// Source: EntityPickerPage.cs lines 420-424 — current pattern (BEFORE change)
// id="p_{entityIdx}_{detIdx}_window" → id="param-{entityIdx}-{detIdx}-window"
// Add: aria-describedby="param-{entityIdx}-{detIdx}-window-err"
// Add: aria-invalid="false"
// Add: <span class="argus-param-field__error-msg" id="param-{entityIdx}-{detIdx}-window-err" role="alert" aria-live="assertive"></span>
$"""
<div class="argus-param-field">
  <label class="argus-param-field__label" for="param-{entityIdx}-{detIdx}-{key}">{label}</label>
  <input class="argus-param-field__input" type="number"
         id="param-{entityIdx}-{detIdx}-{key}"
         name="detectors[{entityIdx}][{detIdx}][params][{key}]"
         value="{value}"
         aria-describedby="param-{entityIdx}-{detIdx}-{key}-err"
         aria-invalid="false">
  <span class="argus-param-field__error-msg"
        id="param-{entityIdx}-{detIdx}-{key}-err"
        role="alert"
        aria-live="assertive"></span>
</div>
"""
```

**Inline JS block — placement pattern**: append just before `</body>` in `BuildFullPage` (EntityPickerPage.cs line 154, currently `</body>` follows the footer). The script block goes after the `</footer>` tag and before `</body>`:

```csharp
// Source: EntityPickerPage.cs lines 150-156 — end of BuildFullPage template
// Insert the inline script here, before </body>
<script>
const PARAM_RULES = {
  "window":                    { min: 1, integer: true },
  "n_trees":                   { min: 1, integer: true },
  "high_threshold":            { min: 0, max: 1, exclusive_min: true, cross: "gt:low_threshold" },
  "low_threshold":             { min: 0, max: 1, exclusive_max: true, cross: "lt:high_threshold" },
  "min_consecutive":           { min: 1, integer: true },
  "frozen_window":             { min: 1, integer: true },
  "frozen_variance_threshold": { min: 0 },
  "threshold":                 { min: 0, exclusive_min: true },
  "period":                    { min: 2, integer: true }
};
// ... event delegation on #argus-picker-form (focusout, input, submit)
// ... validateField(input), setError(field, msg), clearError(field), countInvalid()
// ... disable/enable Save button based on countInvalid()
</script>
```

Note: Inline JS must stay under 2 KB minified (04-UI-SPEC contract). Use raw string literal `$$"""..."""` in C# to avoid escaping issues with the `{` characters in JS — use `{{` for literal braces in C# interpolated strings, or switch to a non-interpolated string concatenation for the script block to avoid C# interpolation conflicts with JS template literals.

---

### `orchestrator/Argus.Orchestrator/wwwroot/css/argus.css` (EXTEND — config/CSS)

**Analog:** itself (Phase 3 state). Copy existing `.argus-banner--reloading` pattern (lines 636–639) for the new `.argus-banner--validation` modifier. Copy `.argus-param-field` structure (lines 557–587) to understand where the new `__error-msg` element and `--error` modifier insert.

**Insertion point:** Append a new `/* ── Phase 4: Validation Error States ── */` section at end of file, after line 640 (`.argus-banner--reloading` rule).

**Pattern to copy** (argus.css lines 636–639 — existing modifier variant structure):
```css
/* Source: argus.css lines 636-639 — .argus-banner--reloading as modifier template */
.argus-banner--reloading {
  background-color: var(--color-accent);
  color: #ffffff;
}
```

**New modifiers to add** (from 04-RESEARCH.md Code Examples + 04-UI-SPEC):
```css
/* ── Phase 4: Validation Error States ───────────────────────────────────── */

.argus-param-field__error-msg {
  display: none;
  font-size: var(--font-size-label);
  font-weight: var(--font-weight-semibold);
  line-height: var(--line-height-label);
  color: var(--color-status-error);
  margin-top: var(--space-xs);
}

.argus-param-field--error .argus-param-field__input {
  border-color: var(--color-status-error);
  outline: none;
}

.argus-param-field--error .argus-param-field__input:focus {
  border-color: var(--color-status-error);
}

.argus-param-field--error .argus-param-field__error-msg {
  display: block;
}

.argus-banner--validation {
  background-color: var(--color-status-error);
  color: #ffffff;
}

.argus-warmup-note {
  font-size: var(--font-size-label);
  font-weight: var(--font-weight-semibold);
  line-height: var(--line-height-label);
  color: rgba(255, 255, 255, 0.85);
  margin-top: var(--space-xs);
}
```

Note: `--color-status-error` is already declared (argus.css line 17). `.argus-btn[disabled]` is already declared (argus.css lines 384–389) — no new disabled rule needed.

---

### `.github/workflows/build.yml` (EXTEND — CI config)

**Analog:** itself. Extend after the `Publish orchestrator` step (line 33) and before the `Set up QEMU` step (line 34).

**Step insertion pattern** (copy style from existing shell steps — e.g., lines 98–103, 117–141):
```yaml
# Source: build.yml lines 29-33 — "Publish orchestrator" step; insert immediately after
- name: Assert wwwroot assets present in publish output
  shell: bash
  run: |
    test -f orchestrator/publish/wwwroot/js/htmx.min.js || {
      echo "FAIL: htmx.min.js not in publish output"
      exit 1
    }
    test -f orchestrator/publish/wwwroot/css/argus.css || {
      echo "FAIL: argus.css not in publish output"
      exit 1
    }
    echo "OK: wwwroot assets present in publish output"
```

**Existing `<2 GB` size gate:** build.yml lines 117–141 (image-facts job, `Gate — compressed image size < 2 GB per arch` step). Do NOT add a second gate — the existing gate covers Phase 4 (UI assets add ~2.5 KB, negligible).

---

### `argus/DOCS.md` (EXTEND — documentation)

**Analog:** itself (269-line structure). Section structure to copy from existing `## Installation` (lines 42–56) and `## Troubleshooting` (lines 217–264).

**Insertion point:** After `## Installation` (ends around line 56) and before `## Configuration` (starts around line 58). Add a new `## Using the Ingress UI` top-level section.

**Existing section pattern to copy** (argus/DOCS.md lines 42–56 — Installation uses numbered steps with bold HA UI navigation terms):
```markdown
1. **Open ...** → navigation in bold
2. **Find and ...**: description text.
3. **Confirm...** → outcomes in plain prose.
```

**Troubleshooting sub-pattern** (argus/DOCS.md lines 238–264 — add-on won't start troubleshooting uses bullet lists + code blocks for entity IDs):
```markdown
### Recovering a Corrupted Configuration

1. Open the add-on **Log** tab — the error message identifies the YAML problem.
2. Option A (fix YAML): SSH into the host, edit `/data/entities.yaml` directly, then restart.
3. Option B (reset): delete `/data/entities.yaml` AND `/data/.ui_config_present`, then restart.
```

**New section outline to implement:**
```markdown
## Using the Ingress UI

> The Ingress UI replaces manual YAML editing for entity selection and detector configuration.

### Opening the UI
### Selecting Entities
### Assigning Detectors
### Applying Changes (No Restart Required)
  - ~4 minute HST warm-up note (window=250 at ~1 reading/s/entity derived figure)
  - MAD/STL have no comparable warm-up (batch detectors)
### Recovering a Corrupted Configuration
  - Steps per 04-RESEARCH.md Research Question 5 (verified: EntitiesConfigLoader.cs lines 16-18, 51-62)
```

---

## Shared Patterns

### Authentication guard
**Source:** `orchestrator/Argus.Orchestrator/Program.cs` lines 213–225 (`IsAuthorizedRequest`) and lines 285, 251, 265.
**Apply to:** All Minimal API endpoints (already in place — no new endpoints added in Phase 4).
```csharp
// Source: Program.cs lines 213-225
static bool IsAuthorizedRequest(HttpContext ctx)
{
    var remote = ctx.Connection.RemoteIpAddress;
    if (remote is null) return false;
    if (System.Net.IPAddress.IsLoopback(remote)) return true;
    if (remote.Equals(System.Net.IPAddress.Parse("172.30.32.2"))) return true;
    return false;
}
// Usage at every endpoint: if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);
```

### LogEvents pattern
**Source:** `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` lines 1–67.
**Apply to:** All new log calls in `InputValidator` invocation (Program.cs), `ConfigFileWatcherService`.

Existing events to reuse:
- `LogEvents.UiSaveFailed` (EventId 7003, line 48) — use for validation-blocked save warning.
- `LogEvents.ConfigReloadTriggered` (EventId 7004, line 51) — use for watcher-triggered reload.

New events to add to LogEvents.cs (7xxx block, after line 53):
```csharp
// Source: LogEvents.cs lines 46-53 — 7xxx block pattern; add after ConfigReloadComplete (7005) / MqttRetractionPublished (7006)
public static readonly EventId ConfigFileWatcherReloadFailed = new(7007, nameof(ConfigFileWatcherReloadFailed));
```

### HtmlEncode defensive pattern
**Source:** `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs` lines 67–70, 262, 516–517.
**Apply to:** All `InputValidator` error messages that interpolate user-supplied strings (`entity_id` values, detector `Name` from POST body).
```csharp
// T-02-07 / T-03-11: user-submitted strings must be HTML-encoded in error messages
WebUtility.HtmlEncode(userValue)
```

### Results.Content("text/html") pattern
**Source:** `orchestrator/Argus.Orchestrator/Program.cs` lines 395, 403 (save handler success/error paths).
**Apply to:** The new validation-failure return in the save handler.
```csharp
// Source: Program.cs lines 395, 403 — htmx fragment returns are always Results.Content(html, "text/html")
return Results.Content(EntityPickerPage.BuildValidationBanner(validationErrors.Count), "text/html");
```

### BackgroundService constructor guard pattern
**Source:** `orchestrator/Argus.Orchestrator/Workers/HealthPublisherWorker.cs` lines 33–43.
**Apply to:** `ConfigFileWatcherService` constructor.
```csharp
// Source: HealthPublisherWorker.cs lines 33-43 — null-guard all injected services
_liveCfg = liveCfg ?? throw new ArgumentNullException(nameof(liveCfg));
_settings = settings ?? throw new ArgumentNullException(nameof(settings));
_logger = logger ?? throw new ArgumentNullException(nameof(logger));
```

### Try/catch(Exception) + LogWarning pattern (for non-crashing background work)
**Source:** `orchestrator/Argus.Orchestrator/Workers/HealthPublisherWorker.cs` lines 74–82.
**Apply to:** `ConfigFileWatcherService.Reload()` — invalid external edit must be logged and ignored, never crash the pipeline.
```csharp
// Source: HealthPublisherWorker.cs lines 74-82 — catch everything except OCE, log, continue
catch (OperationCanceledException) { throw; }
catch
{
    // Any error → treat as failure, log, continue
    serving = false;  // or in ConfigFileWatcherService: keep _liveCfg unchanged
}
```

### xUnit test pattern for static utility class
**Source:** `orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs` lines 1–151 — tests `EntitiesConfigLoader` (the closest structural analog to `InputValidator`).
**Apply to:** `InputValidatorTests.cs` (new test file for Phase 4).

Key test patterns to copy:
- Namespace: `namespace Argus.Orchestrator.Tests;`
- Using block: `using Argus.Orchestrator.Config; using Xunit;`
- Test class: `public class InputValidatorTests`
- Fact naming: `MethodName_Scenario_ExpectedOutcome`
- Arrange/Act/Assert structure (no BDD framework)
- `WriteTempYaml` equivalent: create test `DetectorConfig` / `List<string>` inline (no file I/O needed for pure validator)

```csharp
// Source: EntitiesConfigTests.cs lines 1-5 — header pattern
using Argus.Orchestrator.Config;
using Xunit;

namespace Argus.Orchestrator.Tests;

public class InputValidatorTests
{
    [Fact]
    public void Validate_ValidEntityIdAndParams_ReturnsNoErrors()
    {
        // Arrange
        var ids = new[] { "sensor.salon_temperatura" };
        var detectors = new Dictionary<int, List<DetectorConfig>>
        {
            [0] = [new DetectorConfig { Name = "hst",
                       Params = new() { ["window"] = "250", ["n_trees"] = "25",
                                        ["high_threshold"] = "0.7", ["low_threshold"] = "0.3",
                                        ["min_consecutive"] = "3", ["frozen_window"] = "10",
                                        ["frozen_variance_threshold"] = "0.001" } }]
        };

        // Act
        var errors = InputValidator.Validate(ids, detectors);

        // Assert
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("sensor.UPPER")]       // uppercase — fails regex
    [InlineData("sensor")]             // missing dot
    [InlineData("sensor.bad id")]      // space
    public void Validate_InvalidEntityId_ReturnsError(string badId)
    {
        var errors = InputValidator.Validate([badId], []);
        Assert.NotEmpty(errors);
    }
}
```

---

## No Analog Found

None. All files have sufficient analogs within the codebase.

---

## Metadata

**Analog search scope:** `orchestrator/Argus.Orchestrator/`, `orchestrator/Argus.Orchestrator.Tests/`, `.github/workflows/`, `argus/`
**Files scanned:** 18 source files, 1 CI workflow, 1 Dockerfile, 1 DOCS.md, 1 argus.css
**Pattern extraction date:** 2026-07-01
