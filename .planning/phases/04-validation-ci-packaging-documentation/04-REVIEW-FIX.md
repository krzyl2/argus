---
phase: 04-validation-ci-packaging-documentation
fixed_at: 2026-07-01T11:16:44Z
review_path: .planning/phases/04-validation-ci-packaging-documentation/04-REVIEW.md
iteration: 1
findings_in_scope: 5
fixed: 5
skipped: 0
status: all_fixed
---

# Phase 4: Code Review Fix Report

**Fixed at:** 2026-07-01T11:16:44Z
**Source review:** .planning/phases/04-validation-ci-packaging-documentation/04-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 5
- Fixed: 5
- Skipped: 0

## Fixed Issues

### WR-01: STL `seasonal` parameter bypasses server-side validation

**Files modified:** `orchestrator/Argus.Orchestrator/Config/InputValidator.cs`, `orchestrator/Argus.Orchestrator.Tests/InputValidatorTests.cs`
**Commit:** 8adb500
**Applied fix:** Added `ValidateIntAtLeast(p, "seasonal", 1, ...)` call to `ValidateStl` (closes SC1 / T-04-03). Updated `OneStlDetector` helper to include `seasonal = "7"` in its default params so all existing STL tests remain green (absent key is silently skipped by design; the helper now carries a valid value that can be overridden). Added four new test cases: `Validate_StlSeasonalAtZero_ReturnsError`, `Validate_StlSeasonalNegative_ReturnsError`, `Validate_StlSeasonalAtOne_ReturnsNoError`, `Validate_StlSeasonalNonNumeric_ReturnsNoError`. Test suite grew from 275 to 279; all pass.

---

### WR-04: DOCS.md parameter lists do not match the UI

**Files modified:** `argus/DOCS.md`
**Commit:** 2f7ed32
**Applied fix:** Updated MAD parameter list from `threshold`, `min_consecutive` to `threshold`, `window`. Updated STL parameter list from `period`, `threshold`, `min_consecutive` to `period`, `seasonal`, `threshold`. Both now match what `BuildMadParamGrid` and `BuildStlParamGrid` in EntityPickerPage.cs actually render.

---

### WR-02: `_debounceMs` read without memory barrier on FSW callback thread

**Files modified:** `orchestrator/Argus.Orchestrator/Workers/ConfigFileWatcherService.cs`
**Commit:** 78fd3bd
**Applied fix:** Changed `private int _debounceMs = 300;` to `private volatile int _debounceMs = 300;`. The `volatile` keyword ensures writes from `SetDebounceIntervalMs` (test thread or control path) are immediately visible on FSW callback thread-pool threads that read `_debounceMs` inside `ResetDebounce`.

---

### WR-05: CI workflow has no test step

**Files modified:** `.github/workflows/build.yml`
**Commit:** 1d3db2e
**Applied fix:** Inserted a `Run unit tests` step (`dotnet test orchestrator/ -c Release --logger "console;verbosity=normal"`) between the wwwroot-asset assertion step and the Docker image build (Set up QEMU). This runs the full Argus.Orchestrator.Tests suite in Release mode on every tag push and workflow_dispatch before any Docker artifact is produced.

---

### WR-03: `stoppingToken.WaitHandle.WaitOne()` blocks without timeout

**Files modified:** `orchestrator/Argus.Orchestrator/Workers/ConfigFileWatcherService.cs`
**Commit:** 3b8344d
**Applied fix:** Changed `ExecuteAsync` from synchronous (returning `Task.CompletedTask` after a blocking `WaitHandle.WaitOne()`) to `async Task` using `await Task.Delay(Timeout.Infinite, stoppingToken)` inside a `try/catch(OperationCanceledException)`. The `OperationCanceledException` thrown when the host cancels `stoppingToken` is expected on cooperative shutdown and is silently swallowed. Timer disposal via `Interlocked.Exchange(ref _debounce, null)?.Dispose()` is unchanged and still runs before the method returns. All 279 tests pass after the change; no test calls `ExecuteAsync` directly so the async refactor does not affect any test seam.

---

_Fixed: 2026-07-01T11:16:44Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
