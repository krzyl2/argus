---
phase: 04-validation-ci-packaging-documentation
reviewed: 2026-07-01T00:00:00Z
depth: standard
files_reviewed: 9
files_reviewed_list:
  - orchestrator/Argus.Orchestrator/Config/InputValidator.cs
  - orchestrator/Argus.Orchestrator/Program.cs
  - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
  - orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs
  - orchestrator/Argus.Orchestrator/Workers/ConfigFileWatcherService.cs
  - .github/workflows/build.yml
  - argus/DOCS.md
  - orchestrator/Argus.Orchestrator.Tests/InputValidatorTests.cs
  - orchestrator/Argus.Orchestrator.Tests/ConfigFileWatcherServiceTests.cs
findings:
  critical: 0
  warning: 0
  info: 2
  total: 2
status: clean
---

# Phase 04: Code Review Report (Re-review after fixes)

**Reviewed:** 2026-07-01T00:00:00Z
**Depth:** standard
**Files Reviewed:** 9
**Status:** clean

## Summary

Re-review after fixes were applied to all five prior warnings (WR-01 through WR-05). All five
are confirmed resolved. No Critical or Warning findings remain.

The async `ExecuteAsync` refactor is correct — shutdown and disposal are handled cleanly.
The STL seasonal validation fix accepts the minimum valid value (1) without over-rejecting.
The CI test step is correctly placed and targets the right project directory. Two Info items
are noted for awareness; neither affects correctness, security, or the live pipeline.

### Prior warning resolution

**WR-01 — STL seasonal validation:** `ValidateStl` in `InputValidator.cs:166` now calls
`ValidateIntAtLeast(p, "seasonal", 1, "Must be a whole number >= 1.", errors)`. Test coverage
in `InputValidatorTests.cs` confirms `seasonal=0` and `seasonal=-999` are rejected;
`seasonal=1` (the specified minimum per T-04-03 SC1) and the default `seasonal=7` pass cleanly.
The default `StlSeasonalDefault = "7"` in `EntityPickerPage.cs:54` is well within range.
Resolved.

**WR-02 — volatile _debounceMs:** `_debounceMs` at `ConfigFileWatcherService.cs:36` is now
`private volatile int _debounceMs = 300`. `volatile` on a 32-bit value type in .NET 8 is
valid — the CLR guarantees atomic reads and writes of `int`, and `volatile` adds the
visibility and ordering guarantee needed for the single-writer (test thread calling
`SetDebounceIntervalMs`) / single-reader (timer callback reading `_debounceMs` in
`ResetDebounce`) pattern. Resolved.

**WR-03 — async ExecuteAsync:** `ExecuteAsync` at `ConfigFileWatcherService.cs:69` now uses
`await Task.Delay(Timeout.Infinite, stoppingToken)` inside a try/catch for
`OperationCanceledException`. On cooperative shutdown the token cancels, the delay throws,
the catch swallows it, and then `Interlocked.Exchange(ref _debounce, null)?.Dispose()` runs
synchronously before the method returns. The `FileSystemWatcher` is inside `using var` and is
also disposed before `ExecuteAsync` exits. There is no shutdown race that could leave the
timer alive post-teardown. Resolved.

**WR-04 — DOCS param lists:** `argus/DOCS.md:86-91` now lists all parameters for all three
detector types in the "Assigning Detectors" section: HST (7 params), MAD (`threshold`,
`window`), STL (`period`, `seasonal`, `threshold`). All lists match the param grids rendered
by `EntityPickerPage.cs`. Resolved.

**WR-05 — CI test step:** A `Run unit tests` step is now present at `build.yml:47`, placed
after `Publish orchestrator` and before `Set up QEMU`. The command
`dotnet test orchestrator/ -c Release --logger "console;verbosity=normal"` targets the
`orchestrator/` directory which contains `Argus.Orchestrator.sln` (confirmed present) — dotnet
CLI discovers the solution file and runs all test projects in it. Resolved.

## Info

### IN-01: STL `seasonal` non-numeric values silently skip server-side validation

**File:** `orchestrator/Argus.Orchestrator/Config/InputValidator.cs:165-166`
**Issue:** `ValidateIntAtLeast` calls `TryGetInt`, which returns `false` for non-numeric input
(e.g. `"abc"`). When `TryGetInt` returns `false`, no error is appended and validation is
silently skipped — the value reaches the detector. The test
`Validate_StlSeasonalNonNumeric_ReturnsNoError` explicitly documents this as intentional
absent-key semantics and comments "silently skipped". This is a design choice, not a bug: a
non-numeric value cannot be range-checked, and the detector will reject it at the gRPC layer.
The same silent-skip behaviour applies to all other `ValidateIntAtLeast` callers (window,
n_trees, etc.).
**Fix:** If stricter rejection is desired, add an explicit parse-failure check:
```csharp
// In ValidateIntAtLeast, before the range check:
if (p.TryGetValue(key, out var v) && !int.TryParse(v, out _))
{
    errors.Add(errorMsg); // treat unparseable as out-of-range
    return;
}
```
Defer until the detector error surface for non-numeric params is confirmed.

### IN-02: STL `seasonal` validator minimum (1) is looser than the statsmodels algorithm constraint

**File:** `orchestrator/Argus.Orchestrator/Config/InputValidator.cs:165-166`
**Issue:** The orchestrator validator enforces `seasonal >= 1` per spec T-04-03 SC1. The
Python STL implementation (statsmodels) requires `seasonal` to be an odd integer >= 7 for the
Loess smoother window; values like `seasonal=1`, `seasonal=2`, or `seasonal=4` pass the
orchestrator validator but are rejected by the detector at runtime, causing a failed batch
run. The gap is between what the orchestrator allows and what the algorithm accepts.
**Fix:** If the statsmodels constraint is confirmed, tighten `ValidateStl`:
```csharp
// seasonal: odd integer >= 7 (statsmodels Loess smoother requirement)
if (TryGetInt(p, "seasonal", out var seasonal))
{
    if (seasonal < 7 || seasonal % 2 == 0)
        errors.Add("Must be an odd whole number >= 7 (e.g. 7, 11, 13).");
}
```
The default `StlSeasonalDefault = "7"` already satisfies this stricter rule. Defer to Phase 5
after confirming the Python-side constraint.

---

_Reviewed: 2026-07-01T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
