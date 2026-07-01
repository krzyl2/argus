---
phase: 03-config-readwrite-detector-assignment-reload
fixed_at: 2026-07-01T00:00:00Z
review_path: .planning/phases/03-config-readwrite-detector-assignment-reload/03-REVIEW.md
iteration: 1
findings_in_scope: 8
fixed: 8
skipped: 0
status: all_fixed
---

# Phase 03: Code Review Fix Report

**Fixed at:** 2026-07-01T00:00:00Z
**Source review:** `.planning/phases/03-config-readwrite-detector-assignment-reload/03-REVIEW.md`
**Iteration:** 1

**Summary:**
- Findings in scope: 8
- Fixed: 8
- Skipped: 0

## Fixed Issues

### CR-01: Fan-out task does not complete channel writers on cancellation or exception

**Files modified:** `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs`
**Commit:** 25a2cb7
**Applied fix:** Wrapped the `await foreach` body in try/finally. The `finally` block calls
`ch.Writer.TryComplete()` on every per-entity channel writer regardless of how the iteration
exits (normal completion, `OperationCanceledException`, or any other exception). Changed
`Complete()` to `TryComplete()` so a double-call in edge cases is benign.

---

### CR-02: Lock-file path is relative to CWD when `entitiesPath` is a bare filename

**Files modified:** `orchestrator/Argus.Orchestrator/Program.cs`
**Commit:** ee9120a
**Applied fix:** Replaced `Path.GetDirectoryName(entitiesPath)!` with
`Path.GetDirectoryName(Path.GetFullPath(entitiesPath)) ?? Path.GetTempPath()`.
`Path.GetFullPath` converts bare filenames to absolute paths using CWD, so
`GetDirectoryName` never returns `null` or `""`. Removed the null-forgiving `!`.

---

### WR-01: `BuildListFragment` passes an empty `EntitiesConfig` — detector disclosure lost on htmx search refresh

**Files modified:** `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs`,
`orchestrator/Argus.Orchestrator/Program.cs`,
`orchestrator/Argus.Orchestrator.Tests/EntityPickerPageTests.cs`
**Commit:** b9f48a6
**Applied fix:** Updated `BuildListFragment` signature to accept `EntitiesConfig config`
as a second parameter. Updated the `GET /api/sensors` route to inject `ILiveEntitiesConfig`
and pass `liveCfg.Get()` at each request. Updated all existing test call sites to pass
`new EntitiesConfig()`. Added `BuildListFragment_WithRealConfig_PreservesDetectorDisclosurePanelsOnSearchRefresh`
regression test verifying the fix.

---

### WR-02: `Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => Task.CompletedTask)` swallows cancellation silently

**Files modified:** `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs`
**Commit:** c651413
**Applied fix:** Replaced the `ContinueWith` pattern with idiomatic
`try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }`.
The `finally` block that unsubscribes `ConfigChanged` is unchanged and still executes on stop.

---

### WR-03: `DetectorFieldParser` uses `int.Parse` on regex-captured digit groups — `OverflowException` on crafted input

**Files modified:** `orchestrator/Argus.Orchestrator/Web/DetectorFieldParser.cs`,
`orchestrator/Argus.Orchestrator.Tests/SaveEndpointDetectorParsingTests.cs`
**Commit:** aa198f2
**Applied fix:** Replaced both `int.Parse` calls with `int.TryParse` + `continue` so
fields with digit groups exceeding `int.MaxValue` are silently skipped rather than
throwing `OverflowException`. Added two regression tests:
`Parse_OverflowingEntityIndex_IsSkippedNotThrown` and
`Parse_OverflowingDetectorIndex_IsSkippedNotThrown`.

---

### IN-01: `Console.WriteLine` used in `Program.cs` instead of `ILogger`

**Files modified:** `orchestrator/Argus.Orchestrator/Program.cs`
**Commit:** 1d9e17d
**Applied fix:** Created a `startupLogger` from `entitiesLoggerFactory` (already wired at
that startup point) and replaced `Console.WriteLine` with `startupLogger.LogInformation(...)`.
This respects log-level filtering and appears in structured log output alongside other startup
messages.

---

### IN-02: `safeDetectorName` variable computed but not used in `BuildDetectorEntry`

**Files modified:** `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs`
**Commit:** b381465
**Applied fix:** Removed the dead `var safeDetectorName = WebUtility.HtmlEncode(detector.Name);`
line. The select dropdown uses pre-rendered `hstSelected`/`madSelected`/`stlSelected` boolean
attributes; the timing caption uses `detectorNameLower`.

---

### IN-03: `trackedEntityIdx` counter only increments for `IsTracked` entries but correlation contract is implicit

**Files modified:** `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs`
**Commit:** 777937f
**Applied fix:** Replaced the terse one-line comment above `trackedEntityIdx` with a
multi-line comment explaining the GET/POST correlation contract: that this counter
must match the `resolvedIds.OrderBy(id => id, OrdinalIgnoreCase)` alphabetical sort
used in `POST /api/sensors/save`, and why both sides produce the same `detectors[ei]`
mapping even though one side is a registry snapshot and the other is a submitted checkbox set.

---

## Skipped Issues

None.

---

_Fixed: 2026-07-01T00:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
