---
phase: 04-validation-ci-packaging-documentation
reviewed: 2026-07-01T00:00:00Z
depth: standard
files_reviewed: 11
files_reviewed_list:
  - orchestrator/Argus.Orchestrator/Config/InputValidator.cs
  - orchestrator/Argus.Orchestrator/Program.cs
  - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
  - orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs
  - orchestrator/Argus.Orchestrator/Workers/ConfigFileWatcherService.cs
  - orchestrator/Argus.Orchestrator/wwwroot/css/argus.css
  - .github/workflows/build.yml
  - argus/DOCS.md
  - orchestrator/Argus.Orchestrator.Tests/InputValidatorTests.cs
  - orchestrator/Argus.Orchestrator.Tests/EntityPickerPageTests.cs
  - orchestrator/Argus.Orchestrator.Tests/ConfigFileWatcherServiceTests.cs
findings:
  critical: 0
  warning: 5
  info: 4
  total: 9
status: issues_found
---

# Phase 4: Code Review Report

**Reviewed:** 2026-07-01
**Depth:** standard
**Files Reviewed:** 11
**Status:** issues_found

## Summary

The Phase 4 additions — server-side InputValidator, ConfigFileWatcherService, CI wwwroot-asset assertion, and DOCS.md — are structurally sound. XSS encoding is consistently applied via `WebUtility.HtmlEncode` throughout EntityPickerPage. The validation gate in Program.cs correctly sits before ConfigWriter. The debounce pattern in ConfigFileWatcherService uses `Interlocked.Exchange` correctly for the timer slot.

Five warnings require attention before merge:

- The STL `seasonal` field is rendered in the UI and sent in POST bodies but is entirely absent from server-side validation — a non-numeric or malformed `seasonal` value will bypass InputValidator and reach ConfigWriter. This is the highest-priority issue.
- `_debounceMs` is a plain `int` field mutated cross-thread without a memory barrier, risking a stale read.
- The `stoppingToken.WaitHandle.WaitOne()` call in ExecuteAsync blocks without timeout, preventing correct cooperative shutdown signalling.
- DOCS.md describes MAD and STL parameter sets that do not match what the UI renders (documentation mismatch risks user confusion and support burden).
- CI has no test step; unit tests are never run in the build-and-publish workflow.

No partial-write-to-disk path on invalid input was found. All validation errors return before `writer.WriteAsync`. No raw user strings were found in HTML interpolation outside of `WebUtility.HtmlEncode` calls.

---

## Warnings

### WR-01: STL `seasonal` parameter bypasses server-side validation

**File:** `orchestrator/Argus.Orchestrator/Config/InputValidator.cs:160-171`

**Issue:** `ValidateStl` validates only `period` and `threshold`. The `seasonal` parameter is rendered in `BuildStlParamGrid` (EntityPickerPage.cs:533), submitted in the POST body, parsed by `DetectorFieldParser`, and written to entities.yaml — but it is never validated by `InputValidator`. A POST body containing `seasonal=-999` or `seasonal=abc` will pass `InputValidator.Validate`, proceed through the defaulting step, and reach `ConfigWriter.WriteAsync`. The design contract (T-04-03) states per-type numeric range checks must reject out-of-range params before write.

**Fix:** Add a `seasonal` integer-at-least-1 check in `ValidateStl`:

```csharp
private static void ValidateStl(Dictionary<string, string> p, List<string> errors)
{
    // period: integer ≥ 2
    ValidateIntAtLeast(p, "period", 2, "Must be a whole number ≥ 2.", errors);

    // seasonal: integer ≥ 1
    ValidateIntAtLeast(p, "seasonal", 1, "Must be a whole number ≥ 1.", errors);

    // threshold: number > 0
    if (TryGetDouble(p, "threshold", out var threshold))
    {
        if (threshold <= 0.0)
            errors.Add("Must be greater than 0.");
    }
}
```

Add a corresponding test case in `InputValidatorTests.cs` similar to `Validate_StlPeriodAtOne_ReturnsError`.

---

### WR-02: `_debounceMs` read without memory barrier on FSW callback thread

**File:** `orchestrator/Argus.Orchestrator/Workers/ConfigFileWatcherService.cs:36,85,137`

**Issue:** `_debounceMs` is declared as a plain `int` (not `volatile`). `SetDebounceIntervalMs` writes it from the test thread; `ResetDebounce` reads it on the FileSystemWatcher callback thread-pool thread. Without a memory barrier, the FSW thread may observe a stale cached value. This is a real risk only in the test harness (production code never calls `SetDebounceIntervalMs`), but in production the field is only ever read from FSW callbacks after the service starts — the initial write at field initialisation happens-before any callback, so the production path is safe. However the omission breaks the internal testability seam contract.

**Fix:** Mark the field `volatile` to guarantee the write from `SetDebounceIntervalMs` is immediately visible on the callback thread:

```csharp
private volatile int _debounceMs = 300;
```

---

### WR-03: `stoppingToken.WaitHandle.WaitOne()` blocks without timeout

**File:** `orchestrator/Argus.Orchestrator/Workers/ConfigFileWatcherService.cs:67`

**Issue:** `stoppingToken.WaitHandle.WaitOne()` blocks the `ExecuteAsync` thread until the `WaitHandle` is signalled. On some platforms and shutdown sequences, the `WaitHandle` may not be signalled promptly (e.g. if the host stops before `StopAsync` is called). The `using var watcher` will be disposed when `ExecuteAsync` returns, which is correct, but the blocking call has no timeout to guarantee a clean exit within the ASP.NET Core shutdown window (default 5 s). If shutdown takes longer than the host deadline, the watcher's `Dispose` path is never reached and the debounce timer may fire post-teardown.

The host does signal the `WaitHandle` via `CancellationToken` cancellation on cooperative shutdown — the risk is low in practice — but the pattern is fragile. The idiomatic .NET approach is to use `Task.Delay(Timeout.Infinite, stoppingToken)` which integrates with the host's `CancellationToken` cleanly and avoids the `WaitHandle` allocation.

**Fix:**

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    var entitiesPath = _settings.EntitiesPath ?? "/data/entities.yaml";
    var fullPath     = Path.GetFullPath(entitiesPath);
    var dir          = Path.GetDirectoryName(fullPath)!;
    var fileName     = Path.GetFileName(fullPath);

    using var watcher = new FileSystemWatcher(dir)
    {
        NotifyFilter        = NotifyFilters.FileName,
        EnableRaisingEvents = true,
    };

    watcher.Renamed += (_, e) =>
    {
        if (!string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase)) return;
        ResetDebounce(fullPath);
    };

    try
    {
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
    catch (OperationCanceledException) { /* expected on shutdown */ }

    Interlocked.Exchange(ref _debounce, null)?.Dispose();
}
```

This integrates cleanly with `stoppingToken` and guarantees the timer is disposed before `ExecuteAsync` returns.

---

### WR-04: DOCS.md parameter lists do not match the UI

**File:** `argus/DOCS.md:87-90`

**Issue:** The DOCS.md "Assigning Detectors" section describes:
- MAD parameters as `threshold`, `min_consecutive` — but the UI renders only `threshold` and `window` (no `min_consecutive`).
- STL parameters as `period`, `threshold`, `min_consecutive` — but the UI renders `period`, `seasonal`, `threshold` (no `min_consecutive`, plus undocumented `seasonal`).

These mismatches will mislead users trying to correlate the documentation with what they see in the UI, and may cause confusion during troubleshooting.

**Fix:** Update the parameter lists in DOCS.md to reflect the actual rendered fields:

```markdown
- **MAD** (Median Absolute Deviation) — batch detector; trained on InfluxDB history.
  Parameters: `threshold`, `window`.
- **STL** (Seasonal-Trend decomposition) — batch detector; trained on InfluxDB history.
  Parameters: `period`, `seasonal`, `threshold`.
```

---

### WR-05: CI workflow has no test step

**File:** `.github/workflows/build.yml`

**Issue:** The build-and-publish workflow runs `dotnet publish` and the wwwroot-asset assertion, then proceeds directly to Docker image build. There is no `dotnet test` step. The unit tests in `Argus.Orchestrator.Tests` (InputValidatorTests, EntityPickerPageTests, ConfigFileWatcherServiceTests) are never executed in CI. A regression introduced in InputValidator or EntityPickerPage would not be caught before a tag-triggered release.

**Fix:** Add a test step after the publish step and before the image build:

```yaml
- name: Run unit tests
  run: >-
    dotnet test orchestrator/Argus.Orchestrator.Tests/Argus.Orchestrator.Tests.csproj
    -c Release --no-build --logger "console;verbosity=normal"
```

Note: `--no-build` requires the test project to reference the orchestrator and be built as part of the publish step, or replace with a separate `dotnet build` + `dotnet test` sequence. A simpler alternative is:

```yaml
- name: Run unit tests
  run: dotnet test orchestrator/ -c Release --logger "console;verbosity=normal"
```

---

## Info

### IN-01: `UiSaveFailed` log event ID never used

**File:** `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs:48` and `orchestrator/Argus.Orchestrator/Program.cs:420`

**Issue:** `LogEvents.UiSaveFailed` (event ID 7003) is declared but the catch block in the POST /api/sensors/save handler uses `logger.LogError(ex, "UI save failed")` without the structured event ID. This breaks the OBS-01 contract where event IDs enable log filtering and alerting.

**Fix:** Change the log call at Program.cs:420 to use the event ID:

```csharp
logger.LogError(LogEvents.UiSaveFailed, ex, "UI save failed");
```

---

### IN-02: `ValidateIntAtLeast` silently passes when the field is absent from POST body

**File:** `orchestrator/Argus.Orchestrator/Config/InputValidator.cs:200-212`

**Issue:** The doc comment on `ValidateIntAtLeast` reads "Validates that an integer param is present and ≥ minValue" but the implementation returns without error when the key is absent (`TryGetInt` returns false → the if-body is not entered). A POST body with no `window` field for an HST detector passes validation; the save handler then defaults to the HST default at runtime. This behaviour is intentional ("absent = default") but the summary comment is misleading and inconsistent with T-04-03's "per-type numeric range checks" framing.

**Fix:** Update the doc comment to match the implementation:

```csharp
/// <summary>
/// If the key is present, validates that the value parses as an integer and is ≥ minValue.
/// Absent keys are silently skipped — callers default them downstream.
/// </summary>
```

---

### IN-03: Sensor current value interpolated without HtmlEncode

**File:** `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs:332,352-353`

**Issue:** `entry.CurrentValue.ToString("G")` is a `double`-to-string conversion and the result is interpolated directly into the HTML value display (`valueDisplay`). Double-to-string output can only contain digits, `E`, `+`, `-`, `.`, and `∞`/`NaN` — none of which are HTML-special characters. The risk is effectively zero, but the pattern is inconsistent with the surrounding HtmlEncode calls and could become a risk if `CurrentValue` is ever changed to a string or object type.

**Fix:** Apply encoding for consistency and future safety:

```csharp
var safeValue = WebUtility.HtmlEncode(entry.CurrentValue.ToString("G"));
```

---

### IN-04: `hx-target` attribute uses unencoded CSS attribute selector with server integer

**File:** `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs:412`

**Issue:** `hxTarget` is built as `$".argus-add-detector-row[data-entity-idx='{entityIdx}']"` where `entityIdx` is a server-computed integer. This is safe because `entityIdx` is always a C# `int` and cannot contain injection characters. However the `hxTarget` value is interpolated into the `hx-target` attribute without HtmlEncode:

```csharp
hx-target="{hxTarget}"
```

For an integer this produces correct output (`hx-target=".argus-add-detector-row[data-entity-idx='0']"`). No actual injection risk exists. Noted for completeness — no action required unless `entityIdx` is ever derived from user input.

---

_Reviewed: 2026-07-01_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
