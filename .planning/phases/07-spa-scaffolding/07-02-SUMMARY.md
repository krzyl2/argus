---
phase: 07-spa-scaffolding
plan: 02
subsystem: orchestrator-api
tags: [aspnetcore, minimal-api, json, spa-hosting, hot-reload]

# Dependency graph
requires:
  - phase: 07-spa-scaffolding
    plan: 01
    provides: "orchestrator/ui/src/api/types.ts (authoritative SaveRequest shape) and the Vite build targeting Argus.Orchestrator/wwwroot"
provides:
  - "JSON /api/sensors, /api/detectors/defaults, /api/sensors/save endpoints (replaces v3.0 HTML-fragment htmx endpoints)"
  - "SaveRequest/SaveEntity/SaveDetector DTO — nested-array shape matching orchestrator/ui/src/api/types.ts exactly"
  - "DetectorDefaults static helper (HST/MAD/STL default-parameter tables)"
  - "app.MapFallbackToFile(\"index.html\") SPA static hosting"
affects: [07-03-dockerfile-ci-wiring, 08-group-config-ui]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "SaveRequest DTO deserialized via ReadFromJsonAsync<SaveRequest> — no form parsing, no DetectorFieldParser"
    - "JSON response discriminant: { ok:true, count, hasHst } | { ok:false, kind:'validation'|'error', ... }"
    - "DetectorDefaults.Get(name) — standalone testable static class backing GET /api/detectors/defaults"
    - "MapFallbackToFile registered last, after all MapGet/MapPost routes and UseStaticFiles — never intercepts /api/* or real static assets"

key-files:
  created:
    - orchestrator/Argus.Orchestrator/Web/SaveRequest.cs
    - orchestrator/Argus.Orchestrator/Web/DetectorDefaults.cs
    - orchestrator/Argus.Orchestrator.Tests/SensorsEndpointJsonTests.cs
    - orchestrator/Argus.Orchestrator.Tests/SaveEndpointJsonTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/Argus.Orchestrator.Tests/DetectorEntryEndpointTests.cs
    - orchestrator/Argus.Orchestrator.Tests/SaveEndpointDetectorParsingTests.cs
  deleted:
    - orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs
    - orchestrator/Argus.Orchestrator/Web/DetectorFieldParser.cs
    - orchestrator/Argus.Orchestrator/PlaceholderPage.cs
    - orchestrator/Argus.Orchestrator.Tests/EntityPickerPageTests.cs

key-decisions:
  - "SaveRequest.Include/Exclude are raw strings (not List<string>) — matches types.ts's string fields exactly (SPA sends the raw textarea content, split server-side, same as v3.0's form fields)"
  - "Detector default-parameter tables extracted into a standalone Web/DetectorDefaults.cs static class rather than left inline in Program.cs — directly unit-testable without an HTTP server, and Program.cs's endpoint body stays a thin wrapper"
  - "Save handler maps SaveRequest.Entities to the same Dictionary<int,List<DetectorConfig>> shape InputValidator.Validate already expects (keyed by position in sorted resolvedIds) — preserves the exact validation/build/serialize pipeline without touching InputValidator.cs"
  - "Fixed a locale bug surfaced during test-writing: CurrentValue.ToString(\"G\") had no InvariantCulture (same latent bug existed in the removed v3.0 EntityPickerPage.cs) — under non-US locales this produced comma-decimal JSON (\"18,5\") that would fail SPA JSON.parse of numeric-looking strings; fixed forward with explicit InvariantCulture (Rule 1)"

requirements-completed: [UI-03, UI-04]

# Metrics
duration: ~35min
completed: 2026-07-02
status: complete
---

# Phase 07 Plan 02: JSON API Conversion + SPA Fallback Summary

**Converted three htmx HTML-fragment endpoints (`/api/sensors`, `/api/detectors/new-entry`, `/api/sensors/save`) to clean JSON matching the SPA's `types.ts` contract, wired `MapFallbackToFile("index.html")` for SPA hosting, and deleted all server-rendered HTML code (`EntityPickerPage.cs`, `DetectorFieldParser.cs`, `PlaceholderPage.cs`) with zero regression to Ingress auth or the config hot-reload pipeline.**

## Performance

- **Duration:** ~35 min
- **Tasks:** 2
- **Files modified:** 11 (2 created, 3 modified in Task 1; 3 created/extracted, 6 modified/deleted in Task 2)

## Accomplishments

- `GET /api/sensors` now returns `{ entries: [{ entityId, friendlyName, currentValue, unitOfMeasurement, isTracked }] }` — `friendlyName` is `null` when empty or equal to `entityId` (exact v3.0 rule preserved)
- `GET /api/detectors/defaults?name=hst|mad|stl` replaces `/api/detectors/new-entry`; returns `{ name, params }` from the new `DetectorDefaults` static helper (unknown/empty name → 400)
- `POST /api/sensors/save` deserializes the natural nested `SaveRequest` DTO via `ReadFromJsonAsync` instead of `ReadFormAsync` + regex-based `DetectorFieldParser`; response is the `{ ok, count, hasHst }` / `{ ok:false, kind, ... }` discriminated JSON shape
- Every `/api/*` handler still calls `IsAuthorizedRequest` first, unchanged verbatim (UI-03 — zero auth regression)
- Save pipeline preserved exactly: `GlobExpander.Resolve` → `InputValidator.Validate` (before any write) → YAML `SerializerBuilder` root-dictionary → `ConfigWriter.WriteAsync` → lock file → `EntitiesConfigLoader.Load` → `liveCfg.Swap` (UI-04 hot-reload parity, verified by a new test asserting `ConfigChanged` fires)
- `app.MapFallbackToFile("index.html")` added after all explicit routes; `GET /` redirect and `GET /sensors` full-page HTML removed — the SPA hash router now owns both
- Deleted `EntityPickerPage.cs`, `DetectorFieldParser.cs`, `PlaceholderPage.cs` with no dead references remaining (verified by project-wide grep)
- Malformed/null JSON body on save returns 400 with a generic `"invalid request body"` reason — never raw exception text

## Task Commits

1. **Task 1: DTO + JSON endpoint conversion (auth + save pipeline preserved)** - `aaa98c3` (feat)
2. **Task 2: SPA fallback + server-render removal + JSON parity tests** - `006d68c` (feat)

## Files Created/Modified
- `orchestrator/Argus.Orchestrator/Web/SaveRequest.cs` (new) - `SaveRequest`/`SaveEntity`/`SaveDetector` DTO; `Include`/`Exclude` are raw strings matching `types.ts`
- `orchestrator/Argus.Orchestrator/Web/DetectorDefaults.cs` (new) - `DetectorDefaults.Get(name)` static helper, HST/MAD/STL default tables carried over verbatim from the deleted `EntityPickerPage.cs` constants
- `orchestrator/Argus.Orchestrator/Program.cs` (modified) - three endpoints converted to `Results.Json`; `MapFallbackToFile` added; `GET /`, `GET /sensors`, `lastIncludePatterns`/`lastExcludePatterns` removed
- `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs`, `Web/DetectorFieldParser.cs`, `PlaceholderPage.cs` (deleted) - server-render code, superseded by the SPA + JSON API
- `orchestrator/Argus.Orchestrator.Tests/SensorsEndpointJsonTests.cs` (new) - `/api/sensors` entries projection parity (tracked/untracked, friendly-name rule, value/unit, search filter, `liveCfg` freshness)
- `orchestrator/Argus.Orchestrator.Tests/SaveEndpointJsonTests.cs` (new) - `SaveRequest` JSON (de)serialization, full save pipeline success/validation/error paths, hot-reload `ConfigChanged` assertion
- `orchestrator/Argus.Orchestrator.Tests/DetectorEntryEndpointTests.cs` (rewritten) - now asserts `DetectorDefaults.Get` values instead of HTML string-contains
- `orchestrator/Argus.Orchestrator.Tests/SaveEndpointDetectorParsingTests.cs` (rewritten) - `DetectorFieldParser.Parse` tests replaced with a `SaveRequest`-to-`Dictionary<int,List<DetectorConfig>>` mapping test mirroring the new Program.cs logic; correlation/YAML round-trip/Swap tests preserved unchanged
- `orchestrator/Argus.Orchestrator.Tests/EntityPickerPageTests.cs` (deleted) - target class removed; no successor needed beyond the new JSON test files

## Decisions Made
- `SaveRequest.Include`/`Exclude` kept as raw strings (not arrays) to match `orchestrator/ui/src/api/types.ts`'s locked `SaveRequest` interface exactly — the SPA sends the textarea's raw newline-separated content, split server-side exactly as v3.0's form fields were
- Detector defaults extracted to a standalone `DetectorDefaults` static class rather than inlined in the endpoint lambda — matches the plan's Task 1 read_first guidance to treat the default-values table as a first-class, testable module (mirrors the SPA-side `detectorParams.ts` treatment from Plan 07-01)
- Save handler's detector mapping is keyed by `entityId` first, then re-keyed positionally by index in the sorted `resolvedIds` list — this exactly reproduces the `Dictionary<int, List<DetectorConfig>>` shape `InputValidator.Validate` and the entity-build loop already expect, so neither of those two functions needed any change
- Fixed a pre-existing locale bug (present in the original v3.0 `EntityPickerPage.cs` too, just never surfaced because no automated test exercised it under a non-US culture): `CurrentValue.ToString("G")` without `InvariantCulture` — applied Rule 1 (auto-fix bug) since JSON output must be locale-independent for the SPA's `JSON.parse`/display logic

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] `CurrentValue.ToString("G")` locale-dependent decimal separator**
- **Found during:** Task 2, writing `SensorsEndpointJsonTests.ProjectEntries_ValueAndUnit_AreProjectedSeparately`
- **Issue:** `double.ToString("G")` uses the current thread culture; under a Polish (or any comma-decimal) locale this renders `18.5` as `"18,5"` in the JSON `currentValue` field. The same bug existed in the removed `EntityPickerPage.cs`'s equivalent code, just never caught by a test.
- **Fix:** Changed to `CurrentValue.ToString("G", CultureInfo.InvariantCulture)` in `Program.cs`'s `/api/sensors` projection (and the test helper mirroring it).
- **Files modified:** `orchestrator/Argus.Orchestrator/Program.cs`, `orchestrator/Argus.Orchestrator.Tests/SensorsEndpointJsonTests.cs`
- **Commit:** `006d68c`

## Issues Encountered
None beyond the locale bug documented above.

## User Setup Required
None — no external service configuration required.

## Next Phase Readiness
- `orchestrator/Argus.Orchestrator/Program.cs` is a pure JSON API + static-file host; ready for `orchestrator/ui`'s Vite build output to land in `wwwroot/` (Plan 07-03's Dockerfile/CI wiring)
- The `SaveRequest` DTO shape is locked and verified against `orchestrator/ui/src/api/types.ts` — Plan 07-03 does not need to touch either side of this contract
- Live-HA Ingress verification of the full SPA (base-path behavior, fetch resolution through the real Supervisor proxy) remains a human_verification item deferred to a later checkpoint, per 07-01's summary and 07-RESEARCH.md's UI-02 guidance

---
*Phase: 07-spa-scaffolding*
*Completed: 2026-07-02*

## Self-Check: PASSED

All created/modified files verified present on disk; commit hashes `aaa98c3` and `006d68c` verified in `git log`; `dotnet test orchestrator/Argus.Orchestrator.sln -c Release` — 292/292 passing.
