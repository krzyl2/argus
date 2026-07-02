---
phase: 06-batch-group-pipeline
plan: 01
subsystem: config
tags: [yamldotnet, config-validation, group-detection, degrade-not-crash]

# Dependency graph
requires:
  - phase: 05-group-detection-core-proto-python-detectors
    provides: GroupScoreRequest/Response proto contract, PeerDivergenceDetector (stateless), joint-multivariate detectors
provides:
  - GroupConfig schema (GroupId, FriendlyName, Members, Mode, Detector, Params, ResolvedUnits)
  - EntitiesConfig.Groups top-level list (GRP-01 operator-facing config surface)
  - EntitiesConfigLoader.ValidateGroups skip-and-warn validation (GRP-04 floor + peer-mode unit guard)
  - Nullable IHaSensorRegistry? threaded through Load() for cold-boot-safe unit resolution
affects: [06-02, 06-03, 06-04]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Group validation degrades (skip+warn) instead of throwing, opposite of entity Validate() convention"
    - "Nullable IHaSensorRegistry? parameter threaded through Load() for config-load-time HA lookups that may not be available yet (cold boot)"

key-files:
  created: []
  modified:
    - orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs
    - orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs
    - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
    - orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs

key-decisions:
  - "EntityConfig.Covariates/Groups placeholders removed entirely (not deprecated-in-place) — IgnoreUnmatchedProperties() makes this safe for any stray YAML on existing installs"
  - "IHaSensorRegistry threaded as an optional 3rd parameter (default null) rather than a new Load overload name, keeping all 16 existing 2-arg call sites unchanged"
  - "Peer-divergence unit check treats registry==null OR fewer than 2 members with a resolved unit as 'skip check, keep group' (cold boot); only rejects when 2+ distinct non-null units are actually observed"

requirements-completed: [GRP-01, GRP-04]

# Metrics
duration: 6min
completed: 2026-07-02
status: complete
---

# Phase 6 Plan 1: Group Config Schema + Validation Summary

**GroupConfig YAML schema with skip-and-warn config-load validation (3-member floor, peer-mode unit guard, nullable-registry cold-boot degrade) — dead per-entity Covariates/Groups placeholders retired.**

## Performance

- **Duration:** 6 min
- **Started:** 2026-07-02T13:19:00Z (approx, per STATE.md session start)
- **Completed:** 2026-07-02T13:25:35Z
- **Tasks:** 3 completed
- **Files modified:** 4

## Accomplishments
- `GroupConfig` type + `EntitiesConfig.Groups` list — operator can declare a top-level `groups:` key in entities.yaml (GRP-01)
- `ValidateGroups()` prunes invalid groups (below-floor, unknown mode/detector, mixed peer-mode units) with a warning, never throwing — valid groups and all entities still load (GRP-04)
- Cold-boot degrade: peer_divergence unit check skips (keeps the group) when `IHaSensorRegistry` is null or has no resolved units yet, per Pitfall 1
- Retired `EntityConfig.Covariates`/`EntityConfig.Groups` dead placeholders and the `WarnIgnoredKeys` method entirely

## Task Commits

Each task was committed atomically:

1. **Task 1: Add GroupConfig + Groups list; retire dead placeholders** - `71e93e2` (feat)
2. **Task 2: ValidateGroups() skip-and-warn + nullable-registry unit check; retire WarnIgnoredKeys** - `537d7e3` (feat)
3. **Task 3: EntitiesConfigTests — group parse, floor, mixed-unit, degrade-not-crash** - `8ec046b` (test)

## Files Created/Modified
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs` - Added `GroupConfig` class and `EntitiesConfig.Groups` list; removed `EntityConfig.Covariates`/`Groups` placeholders
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` - Added `ValidateGroups()` skip-and-warn validation with nullable `IHaSensorRegistry?` parameter on `Load()`; removed `WarnIgnoredKeys`
- `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` - Added `GroupConfigLoaded` (1004) and `GroupRejected` (1005) event IDs
- `orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs` - Added 6 group test cases; replaced obsolete covariates-warning test with a stray-key-ignored test; added `FakeHaSensorRegistry`

## Decisions Made
- `Load()` gained a third optional parameter (`IHaSensorRegistry? registry = null`) rather than a new overload — all 16 existing 2-arg call sites across `Program.cs`, `ConfigFileWatcherService.cs`, and test files compile unchanged
- `CovariatesIgnored` (EventId 1002) left defined in `LogEvents.cs` even though its only caller (`WarnIgnoredKeys`) was removed — kept for numbering stability; unused EventIds are harmless
- Peer-divergence unit rejection only fires when 2+ *distinct* non-null/non-whitespace units are observed across members; a registry that resolves fewer than 2 members' units (including all-null) is treated as "can't judge yet" and the group is kept, matching Pitfall 1's cold-boot guidance

## Deviations from Plan

None - plan executed exactly as written. The plan's own acceptance criteria for Task 1 explicitly anticipated the transient build break ("Project compiles... WarnIgnoredKeys reference from loader will be removed in Task 2") — this is expected sequencing, not a deviation.

## Issues Encountered
- The pre-existing test `Load_EntityWithCovariates_ParsesSuccessfullyAndLogsWarning` asserted on the now-removed `CovariatesIgnored` warning message. Replaced with `Load_EntityWithStrayCovariatesKey_ParsesSuccessfullyAndIgnoresIt`, which asserts the more relevant invariant post-retirement: a stray `covariates` YAML key under an entity is silently ignored (via `IgnoreUnmatchedProperties()`) and does not break parsing on upgrade for existing operator installs.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- `GroupConfig`/`EntitiesConfig.Groups`/`ValidateGroups` are ready for Plan 06-04 (scoring loop) to read `_liveConfig.Get().Groups` per cycle
- `IHaSensorRegistry?` threading pattern established for any future config-load-time HA lookups
- No blockers for Plans 06-02/06-03 (InfluxDB time-alignment, MQTT discovery) — they depend on this plan's schema, not on runtime behavior

---
*Phase: 06-batch-group-pipeline*
*Completed: 2026-07-02*

## Self-Check: PASSED

All created/modified files verified present on disk; all 3 task commits (71e93e2, 537d7e3, 8ec046b) verified present in git log.
