---
phase: 03-config-readwrite-detector-assignment-reload
plan: "03"
subsystem: Web / Config UI
tags: [ui, detector-assignment, htmx, yaml-serialization, live-reload, tdd]
dependency_graph:
  requires: ["03-02"]
  provides: [detector-disclosure-ui, detector-entry-endpoint, extended-save-with-swap]
  affects: [EntityPickerPage, Program, DetectorFieldParser, argus.css]
tech_stack:
  added: [DetectorFieldParser (internal static parser)]
  patterns:
    - TDD RED/GREEN/REFACTOR per task
    - BuildDetectorEntry — per-type param grids with pre-fill + HtmlEncode (T-03-11)
    - DetectorFieldParser.Parse — compiled GeneratedRegex, groups by (ei,di), ordered
    - ValidateBefore-Swap — EntitiesConfigLoader.Load + liveCfg.Swap after ConfigWriter.WriteAsync
key_files:
  created:
    - orchestrator/Argus.Orchestrator/Web/DetectorFieldParser.cs
    - orchestrator/Argus.Orchestrator.Tests/DetectorEntryEndpointTests.cs
    - orchestrator/Argus.Orchestrator.Tests/SaveEndpointDetectorParsingTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs
    - orchestrator/Argus.Orchestrator/wwwroot/css/argus.css
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/Argus.Orchestrator.Tests/EntityPickerPageTests.cs
decisions:
  - "BuildDetectorEntry is public static on EntityPickerPage (not a separate class) for direct test access and reuse by the /api/detectors/new-entry endpoint"
  - "DetectorFieldParser extracted as internal static class — directly unit-testable via InternalsVisibleTo; Parse() accepts IEnumerable<KVP> for offline testing without IFormCollection"
  - "Save handler defaults empty detector list to HST (Pitfall 7) before serialize, so EntitiesConfigLoader.Validate never throws after a UI save"
  - "Validate-before-Swap: Load() runs Validate() internally; a malformed config cannot reach Swap and crash the live pipeline"
  - "BuildSuccessBanner copy updated: 'Saved — pipeline active. N entities tracked.' (aligned with UI-SPEC)"
  - "BuildReloadingBanner added but not used in POST response — synchronous Swap approach means a single success banner is returned"
metrics:
  duration: "8m 43s"
  completed_date: "2026-07-01"
  tasks_completed: 2
  files_changed: 7
---

# Phase 03 Plan 03: Detector Assignment UI + Parser + Swap — Summary

One-liner: Expandable per-entity detector disclosure rows (HST/MAD/STL dropdown + param grids with pre-fill), GET /api/detectors/new-entry htmx fragment endpoint, and extended save handler that parses indexed detector fields, defaults empty lists to HST, serializes via YamlDotNet, and calls ILiveEntitiesConfig.Swap after write.

## Tasks Completed

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 (RED) | EntityPickerPage test skeletons | cdc9ce0 | EntityPickerPageTests.cs |
| 1 (GREEN+CSS) | Detector disclosure rows + BuildDetectorEntry + Phase-3 CSS | 09ec738 | EntityPickerPage.cs, argus.css, EntityPickerPageTests.cs |
| 2 (RED) | DetectorEntry + SaveParser test skeletons | 5c237e6 | DetectorEntryEndpointTests.cs, SaveEndpointDetectorParsingTests.cs |
| 2 (GREEN) | DetectorFieldParser + /api/detectors/new-entry + extended save + Swap | 9a087f4 | DetectorFieldParser.cs, Program.cs, SaveEndpointDetectorParsingTests.cs |

## What Was Built

### Task 1: EntityPickerPage detector disclosure rows + CSS

- `BuildDetectorEntry(int entityIdx, int detIdx, DetectorConfig)` — public static method returning one `.argus-detector-entry` fragment
  - HST: 7 param fields (window, n_trees, high/low threshold, min_consecutive, frozen_window, frozen_variance_threshold) in a 2-col grid
  - MAD: 2 fields (threshold, window)
  - STL: 3 fields (period, seasonal, threshold — last spans both cols)
  - Pre-fill: stored params override type defaults; defaults from HstParams constants
  - T-03-11: `WebUtility.HtmlEncode` on all detector.Name + every param value
  - Remove button: `type="button"` with inline `onclick="this.closest('.argus-detector-entry').remove()"`
  - Timing captions: "streaming (live, ~2 s reload)" for HST; "batch (runs every N min)" for MAD/STL

- `BuildDetectorDisclosure` (private) — wraps detector entries in `<details class="argus-detectors-details">` with `<summary>Detectors ({N})</summary>`, the detector panel, and the Add-detector button row (htmx `hx-get`/`hx-target`/`hx-swap`)

- `BuildReloadingBanner(int count)` — `argus-banner--reloading`, role=status, aria-live=polite

- `BuildFullPage` subheading updated: "Select the sensors Argus monitors and assign detectors to each."

- `BuildSuccessBanner` copy updated: "Saved — pipeline active. N entities tracked."

- **argus.css Phase-3 section** — all 14 new BEM blocks from 03-UI-SPEC "New Component Classes":
  `.argus-detectors-details`, `.argus-disclosure-toggle` (+ `::before`, `details[open]>`, `:hover`), `.argus-detectors-panel`, `.argus-detector-entry`, `.argus-detector-header`, `.argus-detector-select` (+ `:focus`), `.argus-timing-caption`, `.argus-param-grid` (+ `@media 480px`, `--span2`), `.argus-param-field` (+ `__label`, `__input`, `__input:focus`), `.argus-btn--destructive-ghost` (+ `:hover`, `:active`), `.argus-btn--add-detector` (+ `:hover`, `:active`), `.argus-add-detector-row`, `.argus-banner--reloading`
  - **No new CSS custom properties** — all tokens reference existing `--color-*`, `--space-*`, `--font-*` declarations from Phase 1

### Task 2: /api/detectors/new-entry + extended save + Swap

- `DetectorFieldParser` (internal static) in `Argus.Orchestrator.Web` namespace
  - Compiled `[GeneratedRegex]` pattern: `^detectors\[(\d+)\]\[(\d+)\]\[(.+?)\](?:\[(.+?)\])?$`
  - `Parse(IEnumerable<KVP<string,string>>)` → `Dictionary<int, List<DetectorConfig>>`
  - Groups by (ei, di), orders detectors within entity by di
  - Accessible to tests via existing `InternalsVisibleTo("Argus.Orchestrator.Tests")`

- `GET /api/detectors/new-entry`:
  - `IsAuthorizedRequest` guard → 403 (T-03-12)
  - `int.TryParse` on entity_idx/det_idx (T-03-14: bad input → fallback 0, no file/DB access)
  - Returns `EntityPickerPage.BuildDetectorEntry(ei, dj, new DetectorConfig { Name="hst" })` as `text/html`

- `POST /api/sensors/save` extended:
  - Added `ILiveEntitiesConfig liveCfg` to handler params
  - `DetectorFieldParser.Parse(form.Keys → KVP)` parses all `detectors[...]` fields
  - Entities sorted alphabetically before correlation (canonical order = render order)
  - `ei` index correlates to sorted entity position → stable even for non-contiguous checked sets
  - Empty detector list → `new DetectorConfig { Name="hst", Params=[] }` (Pitfall 7, T-03-13)
  - Serialize via same single `SerializerBuilder` root-dict call (T-02-08, never string-format)
  - After `ConfigWriter.WriteAsync`: `EntitiesConfigLoader.Load(entitiesPath, logger)` (re-read + validate), then `liveCfg.Swap(newConfig)` (triggers ConfigChanged → HaListenerWorker restart)

## Verification

- `dotnet build` — 0 errors, 0 warnings
- `dotnet test` — 215/215 pass (197 baseline + 18 new Phase-3 tests)
- CSS Phase-3 section: `grep "^\s*--"` returns 0 matches (no new custom properties)
- All key_links from plan frontmatter verified:
  - `\.Swap\(` present at Program.cs line 374 (after ConfigWriter.WriteAsync)
  - `BuildDetectorEntry` present in EntityPickerPage.cs (line 177) and called in BuildDetectorDisclosure
  - `new-entry` present in Program.cs (line 255) and in disclosure HTML

## Deviations from Plan

### Auto-fixed Issues

None — plan executed exactly as written.

### Test Adjustments (not deviations)

- Existing `EntityPickerPageTests.BuildFullPage_ContainsExpectedPageStructure` asserted old subheading "Changes take effect on the next pipeline cycle." — updated to the new Phase-3 subheading per plan requirement.
- Existing `BuildSuccessBanner_ContainsCount` asserted "Configuration saved." — updated to "Saved — pipeline active." per UI-SPEC copywriting contract.
- Both updates are consistent with the plan's explicit subheading and banner copy requirements, not deviations.

## Known Stubs

None. All detector assignments are read from `config.Entities` (live `ILiveEntitiesConfig.Get()`) and rendered directly — no hardcoded placeholder values that flow to UI.

## Threat Flags

All STRIDE threats in the plan's `<threat_model>` were mitigated:

| Threat ID | Mitigation Applied |
|-----------|-------------------|
| T-03-10 | YamlDotNet SerializerBuilder (never string-format) for all param values |
| T-03-11 | `WebUtility.HtmlEncode` on detector.Name + all param values in BuildDetectorEntry |
| T-03-12 | `IsAuthorizedRequest` guard on both `/api/detectors/new-entry` and extended save |
| T-03-13 | Empty detector list defaulted to HST before serialize; Validate-before-Swap prevents bad config reaching pipeline |
| T-03-14 | entity_idx/det_idx are `int.TryParse`'d; only used as name= indices |

No new trust boundaries introduced beyond the plan's threat register.

## Self-Check: PASSED

- EntityPickerPage.cs exists: FOUND
- DetectorFieldParser.cs exists: FOUND
- argus.css Phase-3 section (argus-detector-entry class): FOUND
- DetectorEntryEndpointTests.cs exists: FOUND
- SaveEndpointDetectorParsingTests.cs exists: FOUND
- Commits cdc9ce0, 09ec738, 5c237e6, 9a087f4: all verified in git log
- 215/215 tests green
- 0 build errors, 0 warnings
