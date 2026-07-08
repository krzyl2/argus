---
phase: 11-new-standalone-screens-dashboard-algorithms-settings
plan: 01
subsystem: api
tags: [aspnet-core, minimal-api, redaction, settings, typescript]

# Dependency graph
requires:
  - phase: 10-design-system-foundation
    provides: Design system foundation (tokens, shared components) that the Settings screen (Plan 11-05) will consume
provides:
  - GET /api/settings read-only endpoint returning 6 non-sensitive orchestrator config fields
  - SettingsProjection.Build field-by-field allowlist projection (D-06/D-07)
  - SettingsResponse TS interface matching the endpoint JSON shape exactly
affects: [11-05-settings-screen]

# Tech tracking
tech-stack:
  added: []
  patterns: [field-by-field allowlist projection for secret redaction, IsAuthorizedRequest guard on every /api/* route]

key-files:
  created:
    - orchestrator/Argus.Orchestrator/Web/SettingsProjection.cs
    - orchestrator/Argus.Orchestrator.Tests/SettingsEndpointTests.cs
  modified:
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/ui/src/api/types.ts

key-decisions:
  - "logLevel is read directly from IConfiguration[\"Logging:LogLevel:Default\"], never from ConnectionSettings (which has no LogLevel field) — resolves Flagged Conflict #2; null when unset rather than a hardcoded guess"
  - "SettingsProjection constructs an anonymous object field-by-field rather than serializing ConnectionSettings as a whole, so any future field added to ConnectionSettings does not automatically leak into the API response"

patterns-established:
  - "Redacted config projection: new secret-bearing settings surfaces should follow the same field-by-field static Build() pattern (allowlist, not denylist) rather than object-wide serialization"

requirements-completed: [SET-01]

# Metrics
duration: ~6min
completed: 2026-07-08
status: complete
---

# Phase 11 Plan 01: Settings API Endpoint Summary

**Read-only `GET /api/settings` endpoint with a field-by-field allowlist projection that exposes 6 non-sensitive orchestrator config fields and provably redacts all secrets, plus the matching `SettingsResponse` TS type.**

## Performance

- **Duration:** ~6 min
- **Tasks:** 3 completed
- **Files modified:** 4 (2 created, 2 modified)

## Accomplishments
- `SettingsProjection.Build(ConnectionSettings, IConfiguration)` returns exactly `detectorEndpoint`, `influxUrl`, `influxBucket`, `batchIntervalMinutes`, `nightlyFitHour`, `logLevel` — never the whole `ConnectionSettings` object
- `GET /api/settings` registered in `Program.cs`, guarded by the same `IsAuthorizedRequest` check used by every other `/api/*` route (403 for unauthorized callers)
- 4 xUnit tests prove no sentinel secret value and no secret-shaped property name (`token|password|secret|key`, case-insensitive) ever appears in the serialized response, and that `logLevel` tracks `IConfiguration` (null when unset)
- `SettingsResponse` TS interface added to `api/types.ts`, matching the endpoint JSON exactly for Plan 11-05 (Settings screen) to consume

## Task Commits

Each task was committed atomically:

1. **Task 1: SettingsProjection + GET /api/settings endpoint (D-06/D-07)** - `0e67c7f` (feat)
2. **Task 2: Redaction + field-presence unit tests (D-07)** - `eaa8235` (test)
3. **Task 3: SettingsResponse frontend type** - `7b4e7cc` (feat)

## Files Created/Modified
- `orchestrator/Argus.Orchestrator/Web/SettingsProjection.cs` - Static `Build()` field-by-field allowlist projection
- `orchestrator/Argus.Orchestrator/Program.cs` - Registers `GET /api/settings` with the standard `IsAuthorizedRequest` guard
- `orchestrator/Argus.Orchestrator.Tests/SettingsEndpointTests.cs` - 4 redaction + field-presence xUnit tests against the real `SettingsProjection.Build`
- `orchestrator/ui/src/api/types.ts` - Adds `SettingsResponse` interface

## Decisions Made
- `logLevel` sourced from `IConfiguration` directly (not `ConnectionSettings`), null when unset — see key-decisions above.
- Field-by-field construction (not whole-object serialization) chosen as the redaction mechanism, per plan's explicit instruction and the threat register's T-11-01 mitigation.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Installed missing `orchestrator/ui` npm dependencies**
- **Found during:** Task 3 (SettingsResponse frontend type, verification step)
- **Issue:** `npm --prefix orchestrator/ui run build` failed with "This is not the tsc command you are looking for" — `node_modules` did not exist in this fresh worktree checkout (never installed, not project code).
- **Fix:** Ran `npm install` in `orchestrator/ui` to restore the existing `package.json`/`package-lock.json` dependency tree (no new packages added, no version changes).
- **Files modified:** None tracked (node_modules is gitignored).
- **Verification:** `npm --prefix orchestrator/ui run build` then succeeded (tsc type-check + vite build).
- **Committed in:** N/A (gitignored, no commit needed).

---

**Total deviations:** 1 auto-fixed (1 blocking — dependency install, not a new/unverified package)
**Impact on plan:** No scope creep; restored existing declared dependencies only.

## Issues Encountered
None beyond the dependency-install deviation above.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- `SettingsResponse` type and `GET /api/settings` endpoint are ready for Plan 11-05 (Settings screen) to consume.
- No blockers for Wave 1 sibling plans or Wave 2.

---
*Phase: 11-new-standalone-screens-dashboard-algorithms-settings*
*Completed: 2026-07-08*
