---
phase: 260722-mbx
plan: 01
subsystem: ui
tags: [dashboard, health, preact, minimal-api, xunit, vitest]

requires: []
provides:
  - "RecentAnomaliesCache (20-entry bounded ring buffer) + BatchRunStatus singletons"
  - "GET /api/health composite liveness endpoint (HealthProjection allowlist)"
  - "GET /api/anomalies/recent endpoint"
  - "De-mocked DashboardPage rendering live health/recent-anomalies data"
affects: [dashboard, health-monitoring]

tech-stack:
  added: []
  patterns:
    - "In-memory bounded ring buffer (LinkedList + lock) for ordered/capped event history"
    - "HealthProjection allowlist boundary (D-07 pattern, mirrors SettingsProjection)"
    - "Independent per-area frontend loaders (Promise.all of isolated try/catch helpers)"

key-files:
  created:
    - orchestrator/Argus.Orchestrator/Detection/RecentAnomaliesCache.cs
    - orchestrator/Argus.Orchestrator/Batch/BatchRunStatus.cs
    - orchestrator/Argus.Orchestrator/Web/HealthProjection.cs
    - orchestrator/Argus.Orchestrator.Tests/RecentAnomaliesCacheTests.cs
    - orchestrator/Argus.Orchestrator.Tests/BatchRunStatusTests.cs
    - orchestrator/Argus.Orchestrator.Tests/HealthProjectionTests.cs
    - orchestrator/ui/src/state/dashboard.test.ts
  modified:
    - orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs
    - orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs
    - orchestrator/Argus.Orchestrator.Tests/GroupBatchSchedulerTests.cs
    - orchestrator/ui/src/api/types.ts
    - orchestrator/ui/src/state/dashboard.ts
    - orchestrator/ui/src/components/DashboardPage.tsx

key-decisions:
  - "HealthComponent/HomeAssistantHealth/HealthResponse records declared at namespace level (not nested in HealthProjection) to match RecentAnomaliesCache/GroupStatusCache codebase convention"
  - "MVP scope: only joint GroupVerdict anomalies recorded from the batch worker; PerMember peer-divergence anomalies intentionally not recorded (documented inline comment, already visible per-sensor)"

patterns-established:
  - "RecentAnomaliesCache: LinkedList + lock ring buffer (AddFirst/RemoveLast), newest-first snapshot returned as a new List"

requirements-completed: [QUICK-dashboard-real-data]

coverage:
  - id: D1
    description: "RecentAnomaliesCache (newest-first, 20-entry bounded) and BatchRunStatus are thread-safe in-memory singletons"
    requirement: "QUICK-dashboard-real-data"
    verification:
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/RecentAnomaliesCacheTests.cs (4 tests)"
        status: pass
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/BatchRunStatusTests.cs (3 tests)"
        status: pass
    human_judgment: false
  - id: D2
    description: "Streaming pipeline records single-sensor anomalies only when canPublishFlag && isAnomalous; batch worker records joint GroupVerdict anomalies (IsAnomaly=true) and stamps last-run time"
    requirement: "QUICK-dashboard-real-data"
    verification:
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs#OnVerdict_PublishedAndAnomalous_RecordsRecentAnomaly, #OnVerdict_Suppressed_DoesNotRecordRecentAnomaly"
        status: pass
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/GroupBatchSchedulerTests.cs#JointGroup_IsAnomalyTrue_RecordsRecentAnomalyWithGroupId"
        status: pass
    human_judgment: false
  - id: D3
    description: "GET /api/health returns HA connection+entity count + 5 allowlisted components (no secrets); GET /api/anomalies/recent returns the ring buffer newest-first"
    requirement: "QUICK-dashboard-real-data"
    verification:
      - kind: unit
        ref: "orchestrator/Argus.Orchestrator.Tests/HealthProjectionTests.cs (6 tests, incl. camelCase/no-secret contract test)"
        status: pass
    human_judgment: false
  - id: D4
    description: "Frontend health/recentAnomalies signals load independently per area (a failing endpoint does not blank the others)"
    requirement: "QUICK-dashboard-real-data"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/state/dashboard.test.ts (3 tests)"
        status: pass
    human_judgment: false
  - id: D5
    description: "DashboardPage renders live data for Home Assistant KPI, System health, and Recent anomalies with no mock arrays/banners remaining; SPA builds cleanly"
    verification:
      - kind: other
        ref: "npm run build (tsc -b && vite build) — no errors"
        status: pass
    human_judgment: true
    rationale: "Visual rendering under live HA Ingress (real health data, real anomaly events) requires a running instance to confirm — deferred to live-HA verification per project convention (see STATE.md Deferred Items)."

duration: 7min
completed: 2026-07-22
status: complete
---

# Quick Task 260722-mbx: Dashboard Real Data Summary

**Replaced the Dashboard's three mocked areas (Home Assistant KPI, System health, Recent anomalies) with live data via two new read-only endpoints (GET /api/health, GET /api/anomalies/recent) backed by a 20-entry anomaly ring buffer and a last-batch-run tracker.**

## Performance

- **Duration:** 7 min
- **Started:** 2026-07-22T14:15:59Z
- **Completed:** 2026-07-22T14:22:32Z
- **Tasks:** 5
- **Files modified:** 14 (7 created, 7 modified)

## Accomplishments

- RecentAnomaliesCache (thread-safe, newest-first, 20-entry bounded ring buffer) and BatchRunStatus (Interlocked-backed last-run tracker) added as DI singletons
- ScoreStreamPipeline records a streaming anomaly only when the flag is actually published AND the reading is anomalous (mirrors the existing `canPublishFlag` gate — warm-up/cooldown-suppressed readings never appear)
- BatchSchedulerWorker records joint GroupVerdict anomalies on `IsAnomaly=true` and stamps `IBatchRunStatus.MarkRun` at the end of every batch cycle
- GET /api/health composes 5 allowlisted components (Home Assistant, Detector, MQTT broker, Last batch run, InfluxDB) via `HealthProjection` — never reads `HaToken`/`MqttUser`/`MqttPassword`/`InfluxToken`/TLS material
- GET /api/anomalies/recent exposes the ring buffer newest-first
- Frontend `dashboard.ts` loads counts/health/recentAnomalies independently — one failing endpoint degrades to `null` without blanking the others
- DashboardPage.tsx fully de-mocked: `MOCK_ANOMALIES`/`MOCK_HEALTH`/`MockAnomaly`/`MockHealthItem` and both "mocked — no endpoint yet" banners removed; all three areas render live signals with proper unavailable/empty states

## Task Commits

1. **Task 1: New in-memory singletons — RecentAnomaliesCache + BatchRunStatus + unit tests** - `0456e4e` (feat)
2. **Task 2: Wire caches into the streaming pipeline + batch worker + DI, with recording-gate tests** - `71d337a` (feat)
3. **Task 3: HealthProjection allowlist + GET /api/health + GET /api/anomalies/recent + backend tests** - `05c2a7e` (feat)
4. **Task 4: Frontend types + dashboard state loaders + state test** - `f9532a1` (feat)
5. **Task 5: DashboardPage renders real data — remove all mocks** - `7a013ec` (feat)

_Metadata commit (STATE.md/SUMMARY.md) applied separately by the orchestrator._

## Files Created/Modified

- `orchestrator/Argus.Orchestrator/Detection/RecentAnomaliesCache.cs` - RecentAnomaly record + IRecentAnomaliesCache + ring-buffer impl
- `orchestrator/Argus.Orchestrator/Batch/BatchRunStatus.cs` - IBatchRunStatus + Interlocked-backed impl
- `orchestrator/Argus.Orchestrator/Web/HealthProjection.cs` - allowlist projection + BuildBatchComponent overdue logic
- `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs` - optional IRecentAnomaliesCache ctor param + recording inside canPublishFlag gate
- `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` - optional IRecentAnomaliesCache/IBatchRunStatus ctor params + recording site + MarkRun
- `orchestrator/Argus.Orchestrator/Program.cs` - 2 new singleton registrations + GET /api/health + GET /api/anomalies/recent + factory arg wiring
- `orchestrator/Argus.Orchestrator.Tests/RecentAnomaliesCacheTests.cs` - 4 tests (empty, newest-first, capacity eviction, snapshot immutability)
- `orchestrator/Argus.Orchestrator.Tests/BatchRunStatusTests.cs` - 3 tests (null-before-run, round-trip, replace)
- `orchestrator/Argus.Orchestrator.Tests/HealthProjectionTests.cs` - 6 tests (batch-overdue logic + camelCase/no-secret contract)
- `orchestrator/Argus.Orchestrator.Tests/ScoreStreamPipelineTests.cs` - 2 new recording-gate tests
- `orchestrator/Argus.Orchestrator.Tests/GroupBatchSchedulerTests.cs` - 1 new recording test
- `orchestrator/ui/src/api/types.ts` - HealthResponse/HealthComponent/RecentAnomaly/RecentAnomaliesResponse types
- `orchestrator/ui/src/state/dashboard.ts` - health/recentAnomalies signals + 3 independent loaders
- `orchestrator/ui/src/state/dashboard.test.ts` - 3 tests (full success, health-only failure, counts-only failure decoupling)
- `orchestrator/ui/src/components/DashboardPage.tsx` - de-mocked; renders health/recentAnomalies signals

## Decisions Made

- `HealthComponent`/`HomeAssistantHealth`/`HealthResponse` records declared at namespace level (not nested inside the `HealthProjection` static class) — matches the existing `RecentAnomaliesCache`/`GroupStatusCache` convention of record-plus-interface-plus-impl in one file, and keeps call sites (`HealthComponent` not `HealthProjection.HealthComponent`) consistent with `types.ts`'s flat shape.
- MVP scope (per plan): only joint `GroupVerdict` anomalies are recorded from the batch worker; per-member `peer_divergence` anomalies are intentionally not recorded this pass (documented inline — already visible per-sensor, noisier). Not a silent omission.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## Known Stubs

None. All three Dashboard areas are wired to real backend data with honest null/empty states — no hardcoded placeholder values reach rendering.

## Threat Flags

None — the threat model in the plan (T-mbx-01/02/03) fully covers the new surface (GET /api/health, GET /api/anomalies/recent, in-memory cache concurrency); no additional surface was introduced beyond what was planned.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Both new endpoints and the DashboardPage are ready for live-HA verification (manual sanity check from the plan's `<verification>` section — open the Dashboard under live Ingress and confirm real connection state, entity count, and anomaly events). This falls under the same "deferred to live-HA verification" pattern already tracked in STATE.md Deferred Items for prior UI work.
- No blockers.

---
*Phase: 260722-mbx*
*Completed: 2026-07-22*

## Self-Check: PASSED

All 14 created/modified files verified present on disk; all 5 task commit hashes (0456e4e, 71d337a, 05c2a7e, f9532a1, 7a013ec) verified in git log. Backend: 420/420 tests pass, solution builds clean. Frontend: 203/203 tests pass, `npm run build` succeeds with no TypeScript errors.
