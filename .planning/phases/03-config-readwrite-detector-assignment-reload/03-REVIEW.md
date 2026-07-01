---
phase: 03-config-readwrite-detector-assignment-reload
reviewed: 2026-07-01T00:00:00Z
depth: standard
files_reviewed: 11
files_reviewed_list:
  - orchestrator/Argus.Orchestrator/Config/LiveEntitiesConfig.cs
  - orchestrator/Argus.Orchestrator/Workers/HaListenerWorker.cs
  - orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs
  - orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs
  - orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs
  - orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs
  - orchestrator/Argus.Orchestrator/Web/DetectorFieldParser.cs
  - orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs
  - orchestrator/Argus.Orchestrator/Program.cs
  - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
  - orchestrator/Argus.Orchestrator/wwwroot/css/argus.css
findings:
  critical: 2
  warning: 3
  info: 3
  total: 8
status: issues_found
---

# Phase 03: Code Review Report

**Reviewed:** 2026-07-01T00:00:00Z
**Depth:** standard
**Files Reviewed:** 11
**Status:** issues_found

## Summary

Phase 3 adds live config hot-reload (inner-CTS restart loop in `HaListenerWorker`), detector
assignment UI (indexed form fields in `DetectorFieldParser`), MQTT retraction/republish on reload,
and atomic YAML writes via `ConfigWriter`. The high-risk concurrency areas are largely sound:
the null-before-dispose pattern is correctly implemented, the catch clause correctly distinguishes
inner-CTS cancel from stoppingToken cancel, ConfigChanged is subscribed before the loop and
unsubscribed in `finally`, and MQTT/gRPC are not torn down on reload. `LiveEntitiesConfig` uses
`Interlocked.Exchange` with a volatile field and fires ConfigChanged strictly after the swap.
`DiscoveryPublisher.RetractAsync` uses entity-derived topics and empty retained payloads only.
YAML is serialized via YamlDotNet (never string-formatted). HtmlEncode is applied consistently to
user-originated strings across the HTML builder.

Two correctness issues require fixes before this phase ships: the fan-out task in
`ScoreStreamPipeline.RunAsync` does not complete channel writers on cancellation or exception,
blocking the pipeline shutdown path; and a lock-file path bug in `Program.cs` silently misdirects
the `.ui_config_present` write when the entities path is a bare filename.

---

## Critical Issues

### CR-01: Fan-out task does not complete channel writers on cancellation or exception

**File:** `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs:90-97`

**Issue:** The fan-out task iterates `readings.WithCancellation(ct)`. When the inner CTS is
cancelled (config reload) or the stopping token fires, the `await foreach` exits via
`OperationCanceledException` before reaching the `foreach (var ch ...) ch.Writer.Complete()`
block at lines 95-96. The per-entity stream tasks are blocked on
`entityChannels[kvp.Key].Reader.ReadAllAsync(ct)` waiting for the channel writer to signal
completion or for cancellation to propagate. Since `ct` is the same token, they will eventually
unblock via cancellation — but the channel writers are never explicitly completed, so the pipeline
does not drain cleanly on reload and the per-entity tasks throw `OperationCanceledException`
rather than completing naturally. If an exception other than OCE occurs inside the fan-out loop,
the channel writers are also left open indefinitely and `Task.WhenAll` hangs.

**Fix:** Wrap the fan-out body in try/finally so writers are always completed regardless of how
the iteration exits:

```csharp
var fanOutTask = Task.Run(async () =>
{
    try
    {
        await foreach (var r in readings.WithCancellation(ct))
            if (entityChannels.TryGetValue(r.EntityId, out var ch))
                await ch.Writer.WriteAsync(r, ct);
    }
    finally
    {
        foreach (var ch in entityChannels.Values)
            ch.Writer.TryComplete();   // TryComplete: safe to call more than once
    }
}, ct);
```

Use `TryComplete()` (not `Complete()`) so a double-call in edge cases (e.g., the foreach
completed naturally before cancellation) is benign.

---

### CR-02: Lock-file path is relative to CWD when `entitiesPath` is a bare filename

**File:** `orchestrator/Argus.Orchestrator/Program.cs:367`

**Issue:** `Path.GetDirectoryName("entities.yaml")` returns an empty string `""` on .NET (not
`null`), so `Path.Combine("", ".ui_config_present")` produces the relative path
`".ui_config_present"`, written into the process working directory rather than into
`/data/` (the directory containing `entities.yaml`). The null-forgiving operator `!` suppresses
the CS8602 warning without addressing the logic. When `ARGUS_ENTITIES_PATH` is unset the default
is `"entities.yaml"`, so this bug is triggered in every bare-filename deployment.

**Fix:** Resolve the directory robustly:

```csharp
var entitiesDir = Path.GetDirectoryName(Path.GetFullPath(entitiesPath))
    ?? Path.GetTempPath(); // absolute fallback; GetFullPath never returns ""
var lockPath = Path.Combine(entitiesDir, ".ui_config_present");
File.WriteAllText(lockPath, string.Empty);
```

`Path.GetFullPath` converts relative paths using the CWD, producing an absolute path whose
`GetDirectoryName` is never null or empty.

---

## Warnings

### WR-01: `BuildListFragment` passes an empty `EntitiesConfig` — detector disclosure lost on htmx search refresh

**File:** `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs:163-167`

**Issue:** The GET `/api/sensors` route returns the htmx list fragment built by
`BuildListFragment`, which internally constructs `new EntitiesConfig()` (zero entities, zero
detectors). `BuildListRows` uses this config to populate the detector disclosure panels for
tracked entities. When the user types a search term and htmx refreshes the list, the refresh
fragment shows tracked entities (correct — `IsTracked` comes from the registry snapshot) but
without detector disclosure panels, because `entityConfigById` is always empty. Any expanded
detector configuration is invisible after the first keystroke.

**Fix:** Thread the live config through to `BuildListFragment`:

```csharp
// EntityPickerPage.cs — update signature
public static string BuildListFragment(IHaSensorRegistry registry, EntitiesConfig config, string q)
{
    var entries = registry.GetFiltered(q);
    return BuildListRows(entries, config, q);
}
```

```csharp
// Program.cs — update call site
app.MapGet("/api/sensors", (HttpRequest req, IHaSensorRegistry registry, ILiveEntitiesConfig liveCfg) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);
    var q = req.Query["q"].FirstOrDefault() ?? "";
    return Results.Content(
        EntityPickerPage.BuildListFragment(registry, liveCfg.Get(), q),
        "text/html");
});
```

---

### WR-02: `Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => Task.CompletedTask)` swallows cancellation silently

**File:** `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs:107`

**Issue:** `Task.Delay(Timeout.Infinite, stoppingToken)` throws `TaskCanceledException` when
`stoppingToken` fires. The `.ContinueWith(_ => Task.CompletedTask)` continuation runs regardless
of the antecedent's status (it does not check `_.IsCanceled` or `_.IsFaulted`) and returns a
successfully-completed task. The `ExecuteAsync` contract is met and the `finally` block runs
correctly, so the functional behavior happens to be correct. However the pattern obscures intent
and will confuse readers who expect `await Task.Delay(Timeout.Infinite, ct)` to throw on cancel.

**Fix:** Replace with the idiomatic ASP.NET Core keep-alive pattern:

```csharp
// Keep alive until cancellation — TaskCanceledException propagates; finally still runs
await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
```

Or, equivalently (and more readable):

```csharp
try { await Task.Delay(Timeout.Infinite, stoppingToken); }
catch (OperationCanceledException) { /* normal shutdown */ }
```

---

### WR-03: `DetectorFieldParser` uses `int.Parse` on regex-captured digit groups — `OverflowException` on crafted input

**File:** `orchestrator/Argus.Orchestrator/Web/DetectorFieldParser.cs:48-49`

**Issue:** The regex `\d+` captures arbitrarily long digit strings. `int.Parse` throws
`OverflowException` for values exceeding `int.MaxValue` (e.g.,
`detectors[2147483648][0][name]=hst`). The save handler's outer catch in `Program.cs` catches
`Exception` and returns a 200 with an error banner, so the process does not crash. However the
error message shown is "unexpected error" with no guidance, and the parsing throws unchecked from
a hot path that should do explicit input validation.

**Fix:** Replace both `int.Parse` calls with `int.TryParse` and skip invalid entries:

```csharp
if (!int.TryParse(match.Groups[1].Value, out var ei)) continue;
if (!int.TryParse(match.Groups[2].Value, out var di)) continue;
```

Alternatively bound the regex: `\[(\d{1,5})\]` limits each index to 5 digits (max 99999),
making OverflowException impossible.

---

## Info

### IN-01: `Console.WriteLine` used in `Program.cs` instead of `ILogger`

**File:** `orchestrator/Argus.Orchestrator/Program.cs:157`

**Issue:** The "InfluxDB not configured — batch path disabled" message is written via
`Console.WriteLine` rather than the structured logger. All other diagnostic output in the file
uses `LogInformation`. The console write bypasses log-level filtering and structured fields.

**Fix:**
```csharp
// Replace Console.WriteLine with a logger call at the top of the else branch
var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
startupLogger.LogInformation(
    "InfluxDB not configured (influx_url empty) — batch path disabled; running streaming-only.");
```

---

### IN-02: `safeDetectorName` variable computed but not used in `BuildDetectorEntry`

**File:** `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs:179`

**Issue:** `safeDetectorName` is assigned (`WebUtility.HtmlEncode(detector.Name)`) at line 179
but never referenced in the returned HTML. The select dropdown uses pre-rendered `hstSelected`,
`madSelected`, `stlSelected` boolean attributes and the timing caption uses `detectorNameLower`.
The variable is dead code that may mislead readers into thinking the HTML uses it.

**Fix:** Remove the unused variable:
```csharp
// Remove line 179 — safeDetectorName is unused; the select uses selected-attribute booleans
```

---

### IN-03: `trackedEntityIdx` counter only increments for `IsTracked` entries but entity-index correlation in the save handler sorts ALL resolved IDs

**File:** `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs:286-327`

**Issue:** `BuildListRows` increments `trackedEntityIdx` only when `entry.IsTracked` is true and
uses it as the `entityIdx` parameter for `BuildDetectorEntry` (and therefore for the form field
names `detectors[{ei}][...]`). The save handler in `Program.cs` sorts `resolvedIds` (the
*post-save* resolved set, which always equals the checked set) alphabetically and correlates
`detectors[ei]` by position in that sorted list. This is consistent: on the GET page the
`trackedEntityIdx` counter spans only tracked entries, and on the POST side only checked entities
are in `resolvedIds`. The logic is correct. However the variable name `trackedEntityIdx` and the
comment "Track entity index within the TRACKED set" are subtly misleading — on first read it
appears the index might differ between a freshly-rendered page (where `IsTracked` reflects the
registry snapshot) and a POST (where the resolved set reflects the submitted checkboxes). A brief
clarifying comment linking these two contexts would prevent future confusion.

**Fix:** Add a comment:
```csharp
// trackedEntityIdx is the entity's 0-based position in the sorted tracked-entity list.
// This MUST match the correlation used in the save handler (POST /api/sensors/save):
// sortedIds = resolvedIds.OrderBy(id => id, OrdinalIgnoreCase) — same alphabetical sort.
// New entities checked by the user increment this counter; unchecked entries do not appear in
// either list, so the mapping is stable between GET and POST.
var trackedEntityIdx = 0;
```

---

_Reviewed: 2026-07-01T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
