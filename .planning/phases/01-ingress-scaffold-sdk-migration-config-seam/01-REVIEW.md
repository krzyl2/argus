---
phase: 01-ingress-scaffold-sdk-migration-config-seam
reviewed: 2026-06-30T00:00:00Z
depth: standard
files_reviewed: 12
files_reviewed_list:
  - argus/config.yaml
  - orchestrator/Argus.Orchestrator.Tests/ConfigWriterTests.cs
  - orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs
  - orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj
  - orchestrator/Argus.Orchestrator/Config/ConfigWriter.cs
  - orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs
  - orchestrator/Argus.Orchestrator/Health/ArgusHealthSignals.cs
  - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
  - orchestrator/Argus.Orchestrator/PlaceholderPage.cs
  - orchestrator/Argus.Orchestrator/Program.cs
  - orchestrator/Argus.Orchestrator/Workers/HealthPublisherWorker.cs
  - orchestrator/Argus.Orchestrator/wwwroot/css/argus.css
findings:
  critical: 0
  warning: 3
  info: 4
  total: 7
status: issues_found
---

# Phase 01: Code Review Report

**Reviewed:** 2026-06-30T00:00:00Z
**Depth:** standard
**Files Reviewed:** 12
**Status:** issues_found

## Summary

Phase 1 migrated the orchestrator from Worker SDK to Web SDK and introduced: Ingress PathBase middleware, a server-rendered placeholder page, atomic ConfigWriter, and the ArgusHealthSignals volatile-field health seam. All v2.0 DI registrations are present and correctly ordered. No secrets are logged. The critical path (middleware ordering, HTML encoding, atomic write semantics) is implemented correctly.

Three warnings are raised: an orphan temp file is left on disk when `File.WriteAllTextAsync` throws after the semaphore is acquired; `WarnIgnoredKeys` contains an unchecked null dereference on `config.Entities` (reachable from `Load` if YAML deserialization produces a non-null config with a null list); and `PathString` in the Ingress middleware accepts the raw header value without validation, which lets a malformed path corrupt routing for the lifetime of the request. Four info items cover redundant `using` directives (CS8019) and a minor test gap.

---

## Warnings

### WR-01: Orphan temp file left on disk when WriteAllTextAsync throws

**File:** `orchestrator/Argus.Orchestrator/Config/ConfigWriter.cs:22-26`

**Issue:** The temp file is created at line 23 and written at line 24. If `File.WriteAllTextAsync` throws (disk full, I/O error, cancellation after the write has begun), control passes to `finally` which only releases the semaphore. The temp file already exists on disk but `File.Move` at line 25 is never reached, so the `.entities.tmp.*.yaml` file is never deleted. On a Home Assistant `/data` volume that is typically a small SD card or eMMC partition, repeated write failures accumulate orphaned files. The `ConfigWriterTests.WriteAsync_NoTempFileLeftBehind` test only covers the success path; it does not exercise the failure path.

**Fix:** Add a cleanup block that attempts to delete the temp file on failure:

```csharp
public async Task WriteAsync(string targetPath, string yaml,
    CancellationToken ct = default)
{
    await _lock.WaitAsync(ct);
    string? tmp = null;
    try
    {
        var dir = Path.GetDirectoryName(targetPath)!;
        tmp = Path.Combine(dir, $".entities.tmp.{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(tmp, yaml, ct);
        File.Move(tmp, targetPath, overwrite: true);
        tmp = null; // Move succeeded — do not delete
    }
    finally
    {
        if (tmp != null)
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
        }
        _lock.Release();
    }
}
```

---

### WR-02: Null dereference in WarnIgnoredKeys when Entities list is null

**File:** `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs:33-35` and `66`

**Issue:** `Load` calls `WarnIgnoredKeys(config, logger)` only when `config.Entities?.Count > 0` (line 33), which guards against a null or empty list. However `WarnIgnoredKeys` at line 66 iterates `config.Entities` with a plain `foreach` and no null guard. If the call-site guard is ever relaxed (e.g., by a future refactoring that adds another call site), or if the `EntitiesConfig` default initialiser is changed, this becomes a `NullReferenceException`. The method is `private static` so it cannot be called directly today without the guard, but the lack of an internal null check is a latent defect.

Additionally, the `Validate` method at line 52 iterates `config.Entities` unconditionally after its early-return at line 44 only covers `null || Count == 0`. If `config.Entities` is somehow non-null but contains a null element (malformed YAML where a list entry is `~`), accessing `entity.EntityId` at line 54 throws a `NullReferenceException` rather than the descriptive `InvalidOperationException` below it.

**Fix — WarnIgnoredKeys:** Add a null guard at the top of the method:

```csharp
private static void WarnIgnoredKeys(EntitiesConfig config, ILogger logger)
{
    if (config.Entities is null) return;
    foreach (var entity in config.Entities)
    { ... }
}
```

**Fix — Validate null element:** Add an element null check:

```csharp
foreach (var entity in config.Entities)
{
    if (entity is null)
        throw new InvalidOperationException(
            "entities.yaml contains a null entity entry (check for bare '-' list items)");
    if (string.IsNullOrWhiteSpace(entity.EntityId))
        ...
}
```

---

### WR-03: X-Ingress-Path PathString set from unvalidated header value

**File:** `orchestrator/Argus.Orchestrator/Program.cs:162-163`

**Issue:** The Ingress middleware reads the `X-Ingress-Path` header and assigns it directly to `ctx.Request.PathBase` as a `PathString` without any validation:

```csharp
ctx.Request.PathBase = new Microsoft.AspNetCore.Http.PathString(ingressPath.ToString());
```

`PathString` accepts any string; it does not require the value to be a valid URL path segment. A value containing `?`, `#`, or a null byte is accepted without error but can corrupt downstream routing decisions (e.g., `UseStaticFiles` constructs file paths from PathBase). In an HA add-on the header is set by the Supervisor proxy and is not directly user-controlled, so the real-world risk is low; however it is a correctness concern for robustness. The value is correctly HTML-encoded before being written into the `<base href>` attribute (T-01-08), so there is no injection risk in that path.

**Fix:** Validate before assignment; reject or sanitize values that are not valid path-only strings:

```csharp
if (ctx.Request.Headers.TryGetValue("X-Ingress-Path", out var ingressPath))
{
    var raw = ingressPath.ToString();
    // Accept only non-empty strings that look like absolute paths
    if (!string.IsNullOrEmpty(raw) && raw.StartsWith('/') &&
        !raw.Contains('?') && !raw.Contains('#') && !raw.Contains('\0'))
    {
        ctx.Request.PathBase = new Microsoft.AspNetCore.Http.PathString(raw);
    }
}
```

---

## Info

### IN-01: Redundant explicit using directives — CS8019 (EntitiesConfigLoader.cs)

**File:** `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs:1-3`

**Issue:** The project sets `<ImplicitUsings>enable</ImplicitUsings>` in the .csproj, which globally imports `System`, `System.Collections.Generic`, `System.IO`, and several other BCL namespaces. Lines 1–3 (`using System;`, `using System.Collections.Generic;`, `using System.IO;`) are therefore redundant and will produce CS8019 (unnecessary using directive) warnings from the C# analyzer/LSP.

**Fix:** Remove lines 1–3:

```csharp
// Remove these three lines:
using System;
using System.Collections.Generic;
using System.IO;
```

---

### IN-02: Redundant explicit using directives — CS8019 (PlaceholderPage.cs and EntitiesConfigTests.cs)

**File:** `orchestrator/Argus.Orchestrator/PlaceholderPage.cs:2-3` and `orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs:1`

**Issue:** `PlaceholderPage.cs` explicitly imports `System.Net` and `System.Reflection`; these are not covered by ImplicitUsings for `Microsoft.NET.Sdk.Web` (only BCL core namespaces are implicit), so these are not redundant. However `EntitiesConfigTests.cs` line 1 (`using System.Collections.Generic;`) is covered by ImplicitUsings in the test project, and `ConfigWriterTests.cs` has no such redundancy. Confirm whether the test project also enables `<ImplicitUsings>enable</ImplicitUsings>`; if so, `using System.Collections.Generic;` in `EntitiesConfigTests.cs:1` is redundant.

**Fix:** Check the test `.csproj` for `<ImplicitUsings>enable</ImplicitUsings>` and remove the covered directive if present.

---

### IN-03: ConfigWriterTests does not cover the failure/orphan path

**File:** `orchestrator/Argus.Orchestrator.Tests/ConfigWriterTests.cs:47-63`

**Issue:** `WriteAsync_NoTempFileLeftBehind` only verifies the success path — it confirms no orphan file remains after a successful write. There is no test that injects a failure (e.g., cancellation after `WriteAllTextAsync` begins, or an invalid path that makes `File.Move` throw) and then asserts that no temp file is left. Until WR-01 is fixed, the failure-path behaviour is untested.

**Fix:** Add a test that cancels a write mid-flight (or passes an invalid target path) and asserts `Directory.GetFiles(dir, ".entities.tmp.*.yaml")` is empty afterward.

---

### IN-04: HealthPublisherWorker logs the same EventId for two distinct events

**File:** `orchestrator/Argus.Orchestrator/Workers/HealthPublisherWorker.cs:61` and `91`

**Issue:** Both the initial discovery-published log (line 61) and the per-cycle health-state log (line 91) use `LogEvents.HealthEntityPublished` (EventId 6001). These are semantically distinct events (once-on-startup vs. every-15-seconds), and sharing the same EventId makes log filtering by EventId ambiguous — a filter on 6001 intended to catch "discovery published" will also match every periodic state update and vice versa.

**Fix:** Add a second EventId to `LogEvents.cs`:

```csharp
// Health publisher (6xxx)
public static readonly EventId HealthEntityPublished   = new(6001, nameof(HealthEntityPublished));
public static readonly EventId HealthStatePublished    = new(6002, nameof(HealthStatePublished));
```

Then use `LogEvents.HealthStatePublished` at line 91 of `HealthPublisherWorker.cs`.

---

_Reviewed: 2026-06-30T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
