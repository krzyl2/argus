---
phase: 02-live-sensor-discovery-entity-selection-ui
plan: "03"
subsystem: web-ui
tags: [entity-picker, htmx, yaml-persistence, xss-defense, interim-auth]
dependency_graph:
  requires: [02-01, 02-02]
  provides: [entity-picker-ui, save-endpoint, lock-file-write]
  affects: [Program.cs, argus.css, entities.yaml persistence]
tech_stack:
  added: []
  patterns:
    - EntityPickerPage static page-builder (mirrors PlaceholderPage pattern)
    - Single YamlDotNet SerializerBuilder root-dict (_patterns + entities) — T-02-08
    - Interim IP/header auth — RemoteIpAddress check (T-02-09)
    - In-memory lastIncludePatterns/lastExcludePatterns holder (no disk re-read per page load)
key_files:
  created:
    - orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs
    - orchestrator/Argus.Orchestrator.Tests/EntityPickerPageTests.cs
    - orchestrator/Argus.Orchestrator.Tests/SaveEndpointPatternsTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/Argus.Orchestrator/wwwroot/css/argus.css
    - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
decisions:
  - "Root GET / redirects to /sensors — placeholder page fully replaced by entity picker"
  - "Combined YAML root dict (_patterns + entities) serialized in a single YamlDotNet call — never string-format YAML (T-02-08)"
  - "Empty checkbox selection writes entities: [] (valid state, no error) — Pitfall 5"
  - "In-memory patterns holder (not disk re-read) — acceptable for Phase 2; fresh restart shows empty boxes"
  - "Interim auth: X-Ingress-Path header OR RemoteIpAddress=172.30.32.2/loopback (Phase 4 completes validate_session)"
metrics:
  duration: "5 minutes"
  completed: "2026-07-01"
  tasks: 2
  files: 6
---

# Phase 02 Plan 03: Entity Picker UI + Save Endpoint Summary

**One-liner:** Server-rendered entity picker (GET /sensors + GET /api/sensors + POST /api/sensors/save) with htmx search, YamlDotNet combined-root YAML persistence (_patterns + entities), ConfigWriter atomic write, and .ui_config_present lock file activation.

---

## Tasks Completed

| # | Name | Commit | Files |
|---|------|--------|-------|
| 1 | EntityPickerPage + argus.css Phase-2 blocks | 23f6318 | EntityPickerPage.cs, argus.css, EntityPickerPageTests.cs |
| 2 | Register endpoints + save handler | 9484a01 | Program.cs, LogEvents.cs, SaveEndpointPatternsTests.cs |

---

## What Was Built

### Task 1: EntityPickerPage + CSS

`Web/EntityPickerPage.cs` — `public static class EntityPickerPage` with:
- `BuildFullPage(ingressPath, registry, config, health, q, includePatterns, excludePatterns)` — full HTML shell with `max-width:880px` inline override, search input (htmx GET /api/sensors), sensor list form (`#argus-picker-form`, hx-post=/api/sensors/save), Pattern Filters two-column panel, save bar with `#argus-spinner`, `#argus-flash` target div.
- `BuildListFragment(registry, q)` — bare `<li>` rows from `registry.GetFiltered(q)`; checked checkbox + `argus-pill--tracked` for tracked entries; empty-state and no-results copy when applicable.
- `BuildSuccessBanner(count)` and `BuildErrorBanner(reason)` — `role=status/alert` + `aria-live` fragments per UI-SPEC.
- All user-originated strings (entity_id, friendly_name, q, patterns, ingressPath) `WebUtility.HtmlEncode`'d — T-02-07.

`argus.css` — Phase-2 BEM blocks appended (no token redefinition): `.argus-search`, `.argus-list`, `.argus-list-row`, `.argus-checkbox`, `.argus-row-content`, `.argus-row-entity-id`, `.argus-row-friendly-name`, `.argus-row-meta`, `.argus-row-value`, `.argus-pill--tracked`, `.argus-filters`, `.argus-save-bar`, `.argus-btn--primary`, `#argus-spinner`, `.argus-banner`, `.argus-empty`.

Spinner CSS: `#argus-spinner.htmx-request { display: inline-block; }` — class added TO the indicator element (Pitfall 3 addressed).

### Task 2: Endpoints + Save Handler

`Program.cs` changes:
- `GET /` now redirects to `sensors` (relative, works under ingress PathBase).
- `GET /sensors` — interim auth guard → `EntityPickerPage.BuildFullPage(...)`.
- `GET /api/sensors` — interim auth guard → `EntityPickerPage.BuildListFragment(registry, q)`.
- `POST /api/sensors/save` — form read → GlobExpander.Resolve → EntityConfig list with `hst` default → single YamlDotNet Serialize of `Dictionary<string,object>` root (`_patterns` + `entities`) → `ConfigWriter.WriteAsync` → `.ui_config_present` lock file → success banner. IOException maps to "disk error"; others to "unexpected error" (T-02-11).
- Interim auth: `IsAuthorizedRequest` helper checks `X-Ingress-Path` header OR `RemoteIpAddress` is 172.30.32.2 or loopback (T-02-09).
- In-memory `lastIncludePatterns` / `lastExcludePatterns` updated on each successful save.

`LogEvents.cs` — added `UiSaveSuccess = new(7002, ...)` and `UiSaveFailed = new(7003, ...)`.

---

## Test Coverage

| Test Class | Tests | Coverage |
|------------|-------|----------|
| EntityPickerPageTests | 14 | Tracked pill, untracked, empty state, no-results, XSS encoding (T-02-07), friendly name rendering, full-page structure, banner attributes |
| SaveEndpointPatternsTests | 8 | Round-trip (_patterns ignored by EntitiesConfigLoader), YAML builder shape, YAML-special chars in entity_id, zero entities valid, lock file created, full multi-entity round-trip |

Full suite: **160/160 pass** (138 pre-existing + 22 new).

---

## Deviations from Plan

### Auto-fixed Issues

None.

### Minor Implementation Notes

1. **Nullable CS8620 warning (auto-fixed):** `form["entities"]` returns `StringValues` with nullable strings; added `.Where(s => s is not null).Select(s => s!)` before passing to `GlobExpander.Resolve`. Result: 0 build warnings.
2. **"Pattern Filters" heading missing from initial HTML:** Test revealed the section heading was absent from `BuildFullPage`. Added `<p class="argus-heading">Pattern Filters</p>` before the `.argus-filters` panel per UI-SPEC copywriting. Fixed within Task 1 before commit.
3. **`label style="display:contents"`**: The `.argus-list-row` is a `<li>` (for list semantics); the `<label>` inside uses `display:contents` so the flex layout of the row is maintained by the `<li>` not the `<label>`. This allows the entire row to be a click target while preserving the `<ul><li>` structure per accessibility contract.

---

## Known Stubs

None — all required functionality is wired. The entity picker renders live data from `IHaSensorRegistry`, the save handler writes real YAML, and the lock file is created.

---

## Threat Flags

All T-02-07 through T-02-11 mitigations applied:
- T-02-07: `WebUtility.HtmlEncode` on all user strings in EntityPickerPage — verified by tests.
- T-02-08: YamlDotNet SerializerBuilder single-root serialization — never string-format YAML.
- T-02-09: Interim IP/header auth — `RemoteIpAddress` (not `X-Forwarded-For`).
- T-02-10: ConfigWriter atomic temp+rename+SemaphoreSlim (unchanged from Phase 1).
- T-02-11: Generic error reason in banner; full exception to add-on log only.

No new security surfaces beyond those in the plan's threat register.

---

## Self-Check: PASSED

Files created/modified:
- FOUND: orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs
- FOUND: orchestrator/Argus.Orchestrator/wwwroot/css/argus.css
- FOUND: orchestrator/Argus.Orchestrator.Tests/EntityPickerPageTests.cs
- FOUND: orchestrator/Argus.Orchestrator/Program.cs
- FOUND: orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
- FOUND: orchestrator/Argus.Orchestrator.Tests/SaveEndpointPatternsTests.cs

Commits verified:
- FOUND: 23f6318 (Task 1)
- FOUND: 9484a01 (Task 2)

Tests: 160/160 pass.
Build: 0 errors, 0 warnings.
