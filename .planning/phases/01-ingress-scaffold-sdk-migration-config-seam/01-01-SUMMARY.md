---
phase: 01-ingress-scaffold-sdk-migration-config-seam
plan: "01"
subsystem: config
tags: [dotnet, yaml, semaphoreslim, tdd, csharp]

# Dependency graph
requires: []
provides:
  - "LogEvents.EmptyEntitiesWarning (EventId 1003) in Argus.Orchestrator.Logging"
  - "EntitiesConfigLoader.Validate softened: empty/null entities logs warning instead of throwing"
  - "ConfigWriter.WriteAsync: atomic temp-then-rename YAML write serialized by SemaphoreSlim(1,1)"
affects:
  - "01-02: Program.cs SDK migration + DI registration of ConfigWriter"
  - "Phase 2+: any plan that calls ConfigWriter.WriteAsync or reads entities.yaml on first boot"
  - "Phase 3: FileSystemWatcher reload — relies on atomic rename guarantee"

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "SemaphoreSlim(1,1) async serialization for single-resource singletons (from MqttConnection.cs)"
    - "Sealed class for all single-responsibility singletons"
    - "EventId-tagged structured logging in 1xxx config range"
    - "TDD: RED (failing test commit) → GREEN (implementation commit) gate sequence"

key-files:
  created:
    - "orchestrator/Argus.Orchestrator/Config/ConfigWriter.cs"
    - "orchestrator/Argus.Orchestrator.Tests/ConfigWriterTests.cs"
  modified:
    - "orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs"
    - "orchestrator/Argus.Orchestrator/Logging/LogEvents.cs"
    - "orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs"

key-decisions:
  - "Null YAML deserialization result returns empty EntitiesConfig instead of throwing — maintains no-crash guarantee on first boot"
  - "ConfigWriter does not register itself in DI — Plan 02 owns Program.cs to avoid file conflicts between parallel plans"
  - "ConfigWriter writes strings verbatim; YAML serialization is a Phase 2+ caller concern"

patterns-established:
  - "SemaphoreSlim(1,1) field + WaitAsync/try/finally/Release pattern for concurrent write serialization"
  - "LogWarning + return (instead of throw) for non-fatal configuration states"

requirements-completed: [CFG-01]

# Metrics
duration: 2min
completed: "2026-06-30"
status: complete
---

# Phase 01 Plan 01: Config Seam Summary

**EntitiesConfigLoader softened (empty entities now warns + returns) and atomic ConfigWriter established via temp-then-rename + SemaphoreSlim(1,1) — orchestrator no longer crashes on first boot with no entities configured**

## Performance

- **Duration:** ~2 min
- **Started:** 2026-06-30T19:09:34Z
- **Completed:** 2026-06-30T19:11:32Z
- **Tasks:** 2 (each with RED + GREEN TDD commits)
- **Files modified:** 5

## Accomplishments

- `EntitiesConfigLoader.Validate()` no longer throws on empty/null entities — it logs `EmptyEntitiesWarning` (1003) and returns, allowing the orchestrator to start and the Ingress UI to load
- `ConfigWriter` provides an atomic write path via POSIX rename (`File.Move(overwrite:true)`) serialized by `SemaphoreSlim(1,1)` — future phases cannot introduce a non-atomic write
- All 117 tests pass with zero build warnings; 5 new tests added (2 for EntitiesConfigLoader, 3 for ConfigWriter)

## Task Commits

Each task was committed atomically via TDD RED → GREEN sequence:

1. **Task 1 RED: failing empty-entities tests** — `d2eb06e` (test)
2. **Task 1 GREEN: soften EntitiesConfigLoader + add EventId 1003** — `c420c0a` (feat)
3. **Task 2 RED: failing ConfigWriter tests** — `4c1f7d1` (test)
4. **Task 2 GREEN: add ConfigWriter.cs** — `240ad04` (feat)

_Note: TDD tasks have multiple commits (test → feat) per gate sequence._

## Files Created/Modified

- `orchestrator/Argus.Orchestrator/Config/ConfigWriter.cs` — new sealed class; WriteAsync with SemaphoreSlim(1,1) + temp-then-rename
- `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` — Validate() softened; WarnIgnoredKeys guarded; null-deserialization handled
- `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` — EmptyEntitiesWarning = new(1003, ...) added after CovariatesIgnored
- `orchestrator/Argus.Orchestrator.Tests/EntitiesConfigTests.cs` — two new tests: Load_EmptyEntities_LogsWarning_DoesNotThrow, Load_NullEntitiesKey_LogsWarning_DoesNotThrow
- `orchestrator/Argus.Orchestrator.Tests/ConfigWriterTests.cs` — three new tests: content, concurrency, no-orphan-temp-files

## Decisions Made

- Null YAML deserialization returns `new EntitiesConfig()` instead of throwing — maintains the no-crash guarantee for all "empty config" first-boot scenarios consistently
- ConfigWriter is NOT registered in DI here — Plan 02 owns `Program.cs` to avoid a parallel-wave file conflict; ConfigWriter must exist before Plan 02 compiles, hence wave 1 ordering
- ConfigWriter writes verbatim strings — YAML serialization deferred to Phase 2+ callers (keeps ConfigWriter focused and testable without YamlDotNet dependency)

## Deviations from Plan

None — plan executed exactly as written.

## Known Stubs

None — no placeholder values, hardcoded empty returns, or TODO markers in any created/modified file.

## Threat Flags

No new security surface introduced. T-01-01 (atomic write) and T-01-02 (no crash on empty entities) from the plan's threat register are both mitigated as specified.

## Issues Encountered

None.

## TDD Gate Compliance

RED/GREEN gate sequence verified:

1. `test(01-01)` commit `d2eb06e` — RED gate (EntitiesConfigTests failing)
2. `feat(01-01)` commit `c420c0a` — GREEN gate (EntitiesConfigLoader implemented)
3. `test(01-01)` commit `4c1f7d1` — RED gate (ConfigWriterTests failing to compile)
4. `feat(01-01)` commit `240ad04` — GREEN gate (ConfigWriter implemented)

Both TDD gate sequences complete. No REFACTOR commit needed (code is already clean per plan patterns).

## Next Phase Readiness

- `ConfigWriter` is ready for Plan 02 to register via `AddSingleton<ConfigWriter>()` in Program.cs
- `EntitiesConfigLoader` is safe to call from the Ingress web host startup before any entities are configured
- Phase 2+ save endpoints can call `ConfigWriter.WriteAsync(path, serializedYaml)` without risk of partial-write races

---
*Phase: 01-ingress-scaffold-sdk-migration-config-seam*
*Completed: 2026-06-30*
