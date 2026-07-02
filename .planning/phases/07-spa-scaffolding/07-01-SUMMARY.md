---
phase: 07-spa-scaffolding
plan: 01
subsystem: ui
tags: [preact, vite, typescript, vitest, signals, spa, hash-router]

# Dependency graph
requires:
  - phase: 03-config-readwrite-detector-assignment-reload
    provides: EntityPickerPage.cs / InputValidator.cs as the v3.0 parity spec (validation rules, defaults, copy)
provides:
  - "orchestrator/ui/ Vite+Preact SPA project building to Argus.Orchestrator/wwwroot"
  - "Relative-fetch apiGet/apiPost client enforcing Ingress base-path safety"
  - "Hand-rolled hash router (#/sensors, root redirect)"
  - "detectorParams.ts validation module (Vitest-tested, InputValidator.cs parity)"
  - "13 Preact components reproducing EntityPickerPage.cs 1:1 (sensor list, detector assignment, save flow)"
affects: [07-02-api-endpoints, 08-group-config-ui]

# Tech tracking
tech-stack:
  added: [vite@8.1.3, preact@10.29.3, "@preact/signals@2.9.2", "@preact/preset-vite@2.10.5", typescript@6.0.3, vitest@4.1.9, "@testing-library/preact@3.2.4", jsdom@29.1.1]
  patterns:
    - "Relative-fetch wrapper (apiGet/apiPost) throws on leading-slash paths — sole legal way to call /api/* from components"
    - "Hand-rolled ~20-line @preact/signals hash router instead of a router library (single route this phase)"
    - "Signals store (state/sensors.ts) holds sensor list + per-entity edit state + computed validation-error map"
    - "Detector param validation centralized in validation/detectorParams.ts, ported field-for-field from InputValidator.cs"

key-files:
  created:
    - orchestrator/ui/vite.config.ts
    - orchestrator/ui/src/router.ts
    - orchestrator/ui/src/api/client.ts
    - orchestrator/ui/src/api/types.ts
    - orchestrator/ui/src/state/sensors.ts
    - orchestrator/ui/src/validation/detectorParams.ts
    - orchestrator/ui/src/components/SensorsPage.tsx
    - orchestrator/ui/public/css/argus.css
  modified:
    - .gitignore

key-decisions:
  - "argus.css moved (not duplicated) to orchestrator/ui/public/css/argus.css as the new canonical source; old wwwroot copy + dead htmx.min.js untracked and added to .gitignore since wwwroot/{index.html,assets/,css/} are now Vite build output"
  - "SaveRequest uses the natural nested entities:[{entityId, detectors:[{name, params}]}] shape (RESEARCH Open Q2 recommendation) — must match 07-02's C# DTO exactly"
  - "Task 1's main.tsx forward-references AppShell/SensorsPage; minimal stub components were written in Task 1 to keep npm run build green, then fully implemented in Task 2 (same file paths, incremental build-out within one plan)"
  - "Component tests avoid @testing-library/jest-dom (not in the locked package set) — use container.querySelector/textContent assertions instead of toBeInTheDocument"

requirements-completed: [UI-02, UI-04]

# Metrics
duration: 25min
completed: 2026-07-02
status: complete
---

# Phase 07 Plan 01: SPA Scaffolding Summary

**Vite+Preact SPA in orchestrator/ui/ with a relative-fetch client, hand-rolled hash router, and 13 components reproducing v3.0's htmx entity-picker UI 1:1 (search, detector assignment, validation, save) — builds to Argus.Orchestrator/wwwroot and passes Vitest.**

## Performance

- **Duration:** ~25 min
- **Tasks:** 2
- **Files modified:** 34 (new SPA project + .gitignore)

## Accomplishments
- New `orchestrator/ui/` Vite project (preact, @preact/signals, TypeScript, vitest+jsdom) building to `../Argus.Orchestrator/wwwroot` with `base:'./'` and `emptyOutDir:true` — outDir path verified to resolve exactly to `wwwroot`, not above it
- `apiGet`/`apiPost` fetch wrapper throws on any leading-slash path, enforced by tests — the sole legal way to reach `/api/*` from the SPA (UI-02)
- Hand-rolled hash router redirects `/` (no hash) to `#/sensors`, matching v3.0's server-side 302
- `detectorParams.ts` ports every validation rule from `InputValidator.cs`/`_validationScript` verbatim, including the high>low cross-field check, with matching error message strings
- 13 Preact components reproduce `EntityPickerPage.cs` behavior: debounced search, friendly-name display rule, native `<details>` disclosure, HST/MAD/STL param grids with exact field sets/defaults, add/remove detector with no `confirm()`, pattern filters panel, save bar disabled on validation error, and a save-result banner branching on `ok`/`kind`

## Task Commits

1. **Task 1: Scaffold Vite/Preact project + config + fetch wrapper + hash router + argus.css** - `2ffd46e` (feat)
2. **Task 2: Validation + 13 component parity + signals store** - `1cc2697` (feat)

## Files Created/Modified
- `orchestrator/ui/package.json`, `package-lock.json` - npm project manifest (preact/vite/signals/vitest stack)
- `orchestrator/ui/vite.config.ts` - `base:'./'`, `outDir` -> `../Argus.Orchestrator/wwwroot`, `emptyOutDir`, preact plugin
- `orchestrator/ui/tsconfig.json`, `vitest.config.ts` - TypeScript strict config, jsdom test environment
- `orchestrator/ui/index.html` - relative CSS link, no `<base href>` tag
- `orchestrator/ui/public/css/argus.css` - moved verbatim from `Argus.Orchestrator/wwwroot/css/` (visual source of truth)
- `orchestrator/ui/src/router.ts` - hand-rolled hash-signal router
- `orchestrator/ui/src/api/client.ts`, `types.ts` - relative-fetch wrapper + API contract types
- `orchestrator/ui/src/state/sensors.ts` - signals store: sensor list, edit state, detector defaults table, save assembly, aggregate validation errors
- `orchestrator/ui/src/validation/detectorParams.ts` - field + cross-field validation ported from `InputValidator.cs`
- `orchestrator/ui/src/components/*.tsx` (13 files) - AppShell, SensorsPage, SensorSearchInput, SensorList, SensorListRow, DetectorDisclosure, DetectorEntry, DetectorParamGrid, AddDetectorButton, PatternFiltersPanel, SaveBar, SaveResultBanner, EmptyState, FieldValidationError
- `orchestrator/ui/src/**/*.test.ts(x)` (4 files) - client, detectorParams, SensorListRow, SaveResultBanner
- `.gitignore` - excludes `orchestrator/ui/node_modules/`, `*.tsbuildinfo`, and the now-generated `wwwroot/{index.html,assets/,css/}`; untracked the old hand-authored `wwwroot/css/argus.css` and dead `wwwroot/js/htmx.min.js`

## Decisions Made
- argus.css moved (not duplicated) to `orchestrator/ui/public/css/` as the new canonical source — old wwwroot copy removed from git tracking since Vite's `emptyOutDir` regenerates it every build
- SaveRequest DTO uses the natural nested-array shape per RESEARCH's Open Question 2 recommendation; this is now locked and must match 07-02's C# `SaveRequest`
- Component tests use plain `container.querySelector`/`textContent` assertions rather than `@testing-library/jest-dom` matchers, since jest-dom is not in the CONTEXT.md-locked package list
- Task 1 wrote minimal `AppShell`/`SensorsPage` stubs (not full implementations) so `npm run build` would pass at that task's own verification gate; Task 2 fully implemented both in the same files — this is incremental build-out within a single plan, not a new file or scope change

## Deviations from Plan

None - plan executed exactly as written. The Task 1 stub-then-fill approach for `AppShell.tsx`/`SensorsPage.tsx` follows directly from the plan's own task ordering (main.tsx references them in Task 1; full implementations are Task 2's explicit scope) and is not a deviation from the file list or behavior spec.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required. Live-HA Ingress verification (base-path behavior under a real dynamic prefix) remains a human_verification item per CONTEXT.md, deferred to a later checkpoint/plan that wires the SPA into the running add-on.

## Next Phase Readiness
- `orchestrator/ui/` is ready for 07-02 to wire the ASP.NET Core `/api/*` JSON endpoints (SaveRequest shape is locked and must match `src/api/types.ts` exactly)
- Dockerfile multi-stage build (Node stage -> dotnet publish stage) is not yet wired — still needed before this SPA ships in the add-on image
- `npm run build` and `npm test` both pass cleanly from a clean `wwwroot/`; 34 Vitest tests green across 4 test files

---
*Phase: 07-spa-scaffolding*
*Completed: 2026-07-02*

## Self-Check: PASSED

All created files verified present on disk; all task/summary commit hashes (2ffd46e, 1cc2697, 585d593) verified in git log.
