---
phase: 04-validation-ci-packaging-documentation
verified: 2026-07-01T00:00:00Z
status: human_needed
score: 5/5 must-haves verified (automated)
overrides_applied: 0
human_verification:
  - test: "Open a live HA Ingress session, enter an invalid value in a param field (e.g. window=-1), and confirm: (a) the field border turns red, (b) the inline error message appears below the field, (c) the Save button becomes greyed-out/disabled. Then correct the value and confirm the Save button re-enables."
    expected: "Invalid field highlighted red with error message visible; Save disabled while any field is invalid; Save re-enables when all fields are valid."
    why_human: "CSS .argus-param-field--error modifier and JS disabled-attribute toggling are confirmed in code and unit tests, but the visual rendering and interactive behaviour in a real browser session routed through HA Ingress cannot be asserted programmatically."
  - test: "Make a valid UI save that includes at least one HST detector. Observe the flash banner after save completes."
    expected: "The banner contains the HST warm-up note: 'HST detectors need ~4 minutes of readings to warm up (window=250 at ~1 reading/s). Anomaly scores will be low until warm-up completes.'"
    why_human: "BuildSuccessBanner(count, hasHst=true) is unit-tested but the warm-up note rendering in the live HA Ingress browser session requires human confirmation."
  - test: "On a live Linux/HA-OS host, trigger a UI save and observe the add-on log. Record the timestamp of the Renamed event and the 'External edit detected — reloaded' log line."
    expected: "Exactly one 'reloaded' log line appears approximately 300ms after the rename event. No duplicate reload lines."
    why_human: "inotify IN_MOVED_TO → Renamed mapping on real Linux cannot be reproduced in the Windows/test environment. The 300ms debounce and single-Swap guarantee are confirmed by unit tests (50ms debounce, 3 rapid SimulateRenamedEvent calls → 1 Swap), but log-timestamp proof of exactly-one-reload on a live host requires human observation."
  - test: "Push a version tag to trigger the GitHub Actions workflow. Confirm: (a) the 'Assert wwwroot assets present in publish output' step passes (green), (b) the built multi-arch image size stays under 2 GB per architecture (reported by the 'Gate — compressed image size < 2 GB per arch' step)."
    expected: "CI step passes; image size < 2 GB per arch."
    why_human: "The CI assertion step and size gate are correctly wired in build.yml (verified statically), but a real Actions run against a tagged push is required to confirm dotnet publish emits wwwroot assets and the resulting image is within budget. No Actions runner is available in this environment."
---

# Phase 04: Validation, CI, Packaging, Documentation — Verification Report

**Phase Goal:** UI inputs are fully validated (server-side and client-side) with clear error messages before save; the CI multi-arch image build bundles UI assets and verifies size stays under 2 GB; FileSystemWatcher debounce is validated; and DOCS.md documents the complete UI workflow. The add-on can be configured entirely via UI with zero manual YAML.
**Verified:** 2026-07-01T00:00:00Z
**Status:** human_needed
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| #  | Truth | Status | Evidence |
|----|-------|--------|----------|
| 1  | Invalid entity_id / out-of-range detector params / unknown detector names rejected SERVER-side before any write to /data/entities.yaml | ✓ VERIFIED | `InputValidator.Validate` called at Program.cs line 325, after `DetectorFieldParser.Parse` (line 320), before `snapshotById`/entity-list build (line 336), before `writer.WriteAsync` (line 388). Validation-failure branch returns `BuildValidationBanner` and exits via `return` — `WriteAsync` and `liveCfg.Swap` are unreachable. |
| 2  | Client-side validation mirrors server-side; Save disabled + fields highlighted while invalid | ✓ VERIFIED (partial — visual is human_needed) | EntityPickerPage.cs: all 12 param inputs carry `aria-describedby`, `aria-invalid="false"`, and a sibling `.argus-param-field__error-msg` span. Inline `<script>` with `var PR=` PARAM_RULES mirrors server ranges. `argus.css` has `.argus-param-field--error`, `.argus-param-field__error-msg`, `.argus-banner--validation`, `.argus-warmup-note`. 37 EntityPickerPageTests pass. Visual confirmation requires live HA Ingress session (human_needed #1 and #2). |
| 3  | CI multi-arch build bundles wwwroot assets with a gate that FAILS the build if missing, and the <2 GB size gate exists | ✓ VERIFIED (static — live run is human_needed) | `build.yml` lines 34–45: `Assert wwwroot assets present in publish output` step runs `test -f orchestrator/publish/wwwroot/js/htmx.min.js` and `test -f orchestrator/publish/wwwroot/css/argus.css`, each with `exit 1` on failure. Step is positioned after `Publish orchestrator` and before Docker build steps. `grep -c "compressed image size < 2 GB"` returns 1 — exactly one size gate retained. Real Actions run confirmation is human_needed #4. |
| 4  | FileSystemWatcher Renamed-event + 300ms debounce → exactly one reload | ✓ VERIFIED (unit-tested — live Linux inotify is human_needed) | `ConfigFileWatcherService.cs`: only `watcher.Renamed` subscribed (no `watcher.Changed`). `Interlocked.Exchange` timer-reset debounce pattern. `ConfigFileWatcherServiceTests`: 3 rapid `SimulateRenamedEvent` calls at 50ms debounce → `Assert.Equal(1, liveCfg.SwapCount)` (9/9 tests pass). Live log-timestamp confirmation on Linux is human_needed #3. |
| 5  | DOCS.md Ingress UI section incl. ~4-min HST warm-up + corrupted-config recovery | ✓ VERIFIED | `argus/DOCS.md`: `## Using the Ingress UI` at line 57, after `## Installation` (line 40), before `## Configuration` (line 127). Subsections: Opening the UI, Selecting Entities, Assigning Detectors, Applying Changes (No Restart Required), Recovering a Corrupted Configuration — all present. HST warm-up states `window=250` at ~1 reading/s/entity ≈ 4 minutes. MAD/STL noted as batch detectors with no warm-up. Recovery documents deleting `/data/entities.yaml` AND `/data/.ui_config_present`. Existing YAML Configuration section preserved. |

**Score:** 5/5 truths verified (automated checks) — human_needed due to 4 non-inferable items

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `orchestrator/Argus.Orchestrator/Config/InputValidator.cs` | Static input validator (entity_id regex + per-type param ranges + unknown-detector allowlist) | ✓ VERIFIED | `public static class InputValidator` with `Validate(IEnumerable<string>, Dictionary<int, List<DetectorConfig>>)`. EntityIdRegex `^[a-z0-9_]+\.[a-z0-9_]+$` (Compiled). KnownDetectors `{"hst","mad","stl"}`. ValidateHst/ValidateMad/ValidateStl. `WebUtility.HtmlEncode` at 2 user-string interpolation sites (lines 52, 68). |
| `orchestrator/Argus.Orchestrator.Tests/InputValidatorTests.cs` | Unit coverage for every validation rule | ✓ VERIFIED | `public class InputValidatorTests` present. 38 test cases covering all 04-UI-SPEC rules including entity_id regex, HST int/range/cross-field, MAD, STL, unknown detector name, mixed-case acceptance, HTML-encode. |
| `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs` | BuildValidationBanner, BuildSuccessBanner(count,hasHst), error-msg spans, inline validation JS | ✓ VERIFIED | `public static string BuildValidationBanner(int errorCount)` present. `public static string BuildSuccessBanner(int count, bool hasHst = false)` with warm-up note conditional on hasHst. 12 param inputs migrated to `param-{ei}-{di}-{key}` id format with ARIA + error spans. Inline `<script>` with `var PR=` PARAM_RULES. |
| `orchestrator/Argus.Orchestrator/wwwroot/css/argus.css` | Phase 4 validation CSS classes | ✓ VERIFIED | All four required selectors present: `.argus-param-field__error-msg` (line 643), `.argus-param-field--error .argus-param-field__input` (line 652), `.argus-banner--validation` (line 665), `.argus-warmup-note` (line 670). |
| `orchestrator/Argus.Orchestrator/Workers/ConfigFileWatcherService.cs` | BackgroundService watching /data for Renamed(entities.yaml) with 300ms debounce | ✓ VERIFIED | `public sealed class ConfigFileWatcherService : BackgroundService`. 3 null-guarded injected dependencies. `Interlocked.Exchange` timer-reset debounce at `_debounceMs` (default 300). Only `watcher.Renamed` subscribed. Internal seams: `InternalReload`, `SimulateRenamedEvent`, `SetDebounceIntervalMs`. `Reload` wraps Load+Swap in try/catch. |
| `orchestrator/Argus.Orchestrator.Tests/ConfigFileWatcherServiceTests.cs` | Debounce coalescing + invalid-edit-ignored coverage | ✓ VERIFIED | 9 tests: valid reload → 1 Swap, invalid YAML → 0 Swap + Warning logged, missing file → 0 Swap + Warning logged, 3 rapid SimulateRenamedEvent → 1 Swap (debounce coalescing), wrong filename → 0 Swap, 3 null-guard constructor tests, valid reload logs Information. |
| `.github/workflows/build.yml` | CI assertion step: wwwroot assets present in publish output before docker build | ✓ VERIFIED | `Assert wwwroot assets present in publish output` step at lines 34–45, after `Publish orchestrator` (line 29) and before `Set up QEMU` / Docker build steps. `test -f` checks for both `htmx.min.js` and `argus.css` with `exit 1` on failure. |
| `argus/DOCS.md` | Ingress UI workflow section (open/select/assign/apply/recover) | ✓ VERIFIED | Section ordering: Installation (line 40) → Using the Ingress UI (line 57) → Configuration (line 127). All five subsections present with required content. |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `Program.cs` | `InputValidator.Validate` | Call in POST /api/sensors/save after `DetectorFieldParser.Parse`, before entity-list build | ✓ WIRED | Line 325: `var validationErrors = InputValidator.Validate(resolvedIds, parsedDetectors);`. Validation gate at line 326–333 returns before `snapshotById` (line 336) and `writer.WriteAsync` (line 388). |
| `Program.cs` | `ConfigFileWatcherService` | `builder.Services.AddHostedService<ConfigFileWatcherService>()` | ✓ WIRED | Lines 121–124: registered alongside other hosted services. |
| `ConfigFileWatcherService.cs` | `ILiveEntitiesConfig.Swap` | `Reload()` calls `EntitiesConfigLoader.Load` then `_liveCfg.Swap` | ✓ WIRED | Line 102: `_liveCfg.Swap(newConfig)` in `Reload(string path)` private method. |
| `EntityPickerPage.cs` | `#argus-picker-form` | Inline `<script>` event delegation (focusout/input/submit) toggling `.argus-param-field--error` and Save disabled | ✓ WIRED | `var PR=` (PARAM_RULES) confirmed present in inline script block. `#argus-picker-form` is the form element id (line 105 of EntityPickerPage.cs). No external `src=` attribute on script. |
| `.github/workflows/build.yml` | `orchestrator/publish/wwwroot` | `test -f` assertion step inserted after "Publish orchestrator" and before docker build | ✓ WIRED | Step `Assert wwwroot assets present in publish output` at lines 34–45, between `Publish orchestrator` (line 29) and `Set up QEMU` (line 50). |
| `Program.cs` | `EntityPickerPage.BuildSuccessBanner` | Call site upgraded to pass real hasHst argument | ✓ WIRED | Lines 413–416: `var hasHst = entities.Any(...)` computed then passed as second argument. Warm-up note now renders when HST detectors are present. |

---

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|--------------------|--------|
| `InputValidator.cs` | `resolvedIds`, `parsedDetectors` | POST /api/sensors/save form body via `DetectorFieldParser.Parse` | Yes — real form submission data | ✓ FLOWING |
| `ConfigFileWatcherService.cs` | `newConfig` | `EntitiesConfigLoader.Load(path, _logger)` | Yes — reads disk YAML | ✓ FLOWING |
| `EntityPickerPage.cs` (BuildValidationBanner) | `errorCount` | Server-computed `validationErrors.Count` | Yes — integer from real validation run | ✓ FLOWING |
| `EntityPickerPage.cs` (BuildSuccessBanner) | `hasHst` | `entities.Any(e => e.Detectors.Any(d => d.Name.Equals("hst", ...)))` — real entity list | Yes — derived from actual saved detectors | ✓ FLOWING |

---

### Behavioral Spot-Checks

Step 7b: SKIPPED for visual UI behavior (requires live HA Ingress browser session) and GitHub Actions CI run (requires external runner). The orchestrator's own build + 279-test run already confirmed: build clean, all tests pass.

| Behavior | Check | Status |
|----------|-------|--------|
| InputValidator rejects bad entity_id | `InputValidatorTests` 38/38 (per SUMMARY) | ✓ PASS (unit-test confirmed) |
| InputValidator rejects out-of-range params | `InputValidatorTests` 38/38 | ✓ PASS (unit-test confirmed) |
| ConfigFileWatcher 3 rapid events → 1 Swap | `SimulateRenamedEvent_ThreeRapidFires_CoalescesToOneSwap` | ✓ PASS (unit-test confirmed) |
| ConfigFileWatcher invalid YAML → 0 Swap | `Reload_InvalidConfig_LogsWarningAndDoesNotSwap` | ✓ PASS (unit-test confirmed) |
| Visual: red field + greyed Save in browser | Cannot check without live HA session | ? SKIP (human_needed #1) |
| CI wwwroot gate passing on tag push | Cannot check without Actions runner | ? SKIP (human_needed #4) |

---

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| UI-04 | 04-01, 04-02, 04-03 | UI inputs validated (entity_id format, parameter ranges) with clear error messages before save | ✓ SATISFIED | Server gate: InputValidator + wiring in Program.cs. Client gate: ARIA error spans + inline JS PARAM_RULES + CSS modifiers. FileSystemWatcher debounce. All test suites passing. |
| DOCS-02 | 04-04 | DOCS.md documents the Ingress UI; multi-arch image bundles UI assets and stays under 2 GB | ✓ SATISFIED | DOCS.md "Using the Ingress UI" section present with all required subsections. build.yml CI assertion guards wwwroot assets; single <2 GB size gate retained. |

No orphaned requirements: REQUIREMENTS.md maps UI-04 and DOCS-02 to Phase 4; both are claimed and satisfied.

---

### Anti-Patterns Found

| File | Pattern | Severity | Impact |
|------|---------|----------|--------|
| `EntityPickerPage.cs` (inline JS) | Script body ~2377 bytes — ~17% over the 2 KB budget specified in 04-UI-SPEC | ℹ️ Info | SUMMARY documents the deviation: verbatim spec-required error messages (two threshold strings alone account for 112 chars) make the budget impossible to meet while keeping the copywriting contract. No logic was cut. This is an accepted spec conflict, not a stub or blocker. |

No PLACEHOLDER, TODO, FIXME, `return null`, `return []`, or `return {}` patterns found in phase-modified files. No hardcoded empty data flowing to rendering. No disabled/no-op form handlers.

---

### Human Verification Required

#### 1. Visual Validation Error States in Live Browser

**Test:** Open a live HA Ingress session, navigate to the Argus UI, and enter an invalid value in a param field (e.g. set `window` to `-1` for an HST detector). Tab out of the field.
**Expected:** The field border turns red (`.argus-param-field--error` applied), the inline error message appears below the field (`.argus-param-field__error-msg` becomes visible), and the Save button is disabled (greyed out, `disabled` attribute present).
**Why human:** CSS modifier application and JS `disabled` attribute toggling are confirmed in code and unit tests (EntityPickerPageTests 37/37), but the visual rendering and interactive behaviour in a real browser routed through HA Ingress cannot be asserted programmatically.

#### 2. HST Warm-Up Disclosure in Live Save

**Test:** Configure at least one entity with an HST detector and click Save. Observe the flash banner.
**Expected:** The banner contains the text "HST detectors need ~4 minutes of readings to warm up (window=250 at ~1 reading/s). Anomaly scores will be low until warm-up completes."
**Why human:** `BuildSuccessBanner(count, hasHst=true)` warm-up note is unit-tested; the `hasHst` value is correctly computed and passed at the Program.cs call site. Confirming the note appears visibly in the live HA Ingress session requires human observation.

#### 3. Single Reload on Live Linux Host (inotify)

**Test:** On a live Linux/HA-OS host, perform a UI save and observe the add-on log. Note the timestamp of the Renamed event and the "External edit detected — reloaded" log line.
**Expected:** Exactly one "reloaded" log line appears approximately 300ms after the rename. No duplicate reload lines despite the temp-then-rename write pattern (which produces two filesystem events on some platforms).
**Why human:** inotify IN_MOVED_TO → Renamed mapping on real Linux cannot be reproduced in the Windows/test environment. Debounce coalescing is confirmed by unit test (3 rapid SimulateRenamedEvent → 1 Swap at 50ms interval), but log-timestamp proof of exactly-one-reload from a real atomic write requires human observation.

#### 4. GitHub Actions CI Run Passing on Tag Push

**Test:** Push a version tag (e.g. `v3.0.0`) to trigger the GitHub Actions workflow. Observe the run result.
**Expected:** (a) The "Assert wwwroot assets present in publish output" step is green. (b) The "Gate — compressed image size < 2 GB per arch" step is green.
**Why human:** The CI assertion step and size gate are correctly wired in `build.yml` (verified statically). A real Actions run against a tagged push is required to confirm that `dotnet publish` emits the `wwwroot` directory (requires the `.csproj` to have `<Content Include="wwwroot/**">` with `CopyToPublishDirectory`) and that the resulting multi-arch image stays within the 2 GB budget.

---

### Gaps Summary

No gaps. All five must-haves are satisfied by the actual codebase. The four human verification items are non-inferable by definition (visual browser behaviour, live Linux inotify, real GitHub Actions run) — they were explicitly classified as `non_inferable` in the plan frontmatter and do not represent implementation gaps.

---

_Verified: 2026-07-01T00:00:00Z_
_Verifier: Claude (gsd-verifier)_
