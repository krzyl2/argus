---
phase: 02-live-sensor-discovery-entity-selection-ui
plan: "02"
subsystem: config-primitives
tags: [glob-expander, config-guard, tdd, combine-model, bash-guard]
dependency_graph:
  requires: [02-01]
  provides: [GlobExpander.Resolve, .ui_config_present guard]
  affects: [10-config-gen.sh, plan-03-save-endpoint]
tech_stack:
  added: []
  patterns: [FileSystemName.MatchesSimpleExpression, TDD RED-GREEN, bash-if-guard]
key_files:
  created:
    - orchestrator/Argus.Orchestrator/Config/GlobExpander.cs
    - orchestrator/Argus.Orchestrator.Tests/GlobExpanderTests.cs
  modified:
    - argus/rootfs/etc/cont-init.d/10-config-gen.sh
decisions:
  - "GlobExpander is public static (not internal) so Program.cs save handler can call it without reflection"
  - "Exclude step applies to allIds (not patternSelected) — ensures exclude always removes regardless of include coverage"
  - "manuallyUnchecked removal skips null/whitespace ids (defensive; form values are always trimmed entity_ids)"
  - "8th test added: manual-uncheck-beats-manual-check ordering guarantee (both applied to same id)"
metrics:
  duration: "8 minutes"
  completed: "2026-07-01"
  tasks: 2
  files: 3
---

# Phase 02 Plan 02: GlobExpander + Restart Guard Summary

**One-liner:** BCL `FileSystemName.MatchesSimpleExpression`-based glob resolver implementing the authoritative include/exclude/manual-override combine model, plus the `.ui_config_present` restart guard protecting UI-authored `entities.yaml` from regeneration on add-on restart.

## Tasks Completed

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 (RED) | GlobExpanderTests — failing tests | 7a8d02d | `GlobExpanderTests.cs` |
| 1 (GREEN) | GlobExpander implementation | a5782c7 | `GlobExpander.cs` |
| 2 | .ui_config_present restart guard | 7422bdb | `10-config-gen.sh` |

## What Was Built

### GlobExpander (orchestrator/Argus.Orchestrator/Config/GlobExpander.cs)

Pure static class implementing the authoritative CONTEXT combine model:

1. Build `allIds` from snapshot (OrdinalIgnoreCase HashSet)
2. Filter empty/whitespace patterns
3. No include patterns → all entities are the base candidate set; otherwise glob-filter
4. Remove entities matching any exclude pattern (applied to `allIds`, not just pattern-selected)
5. Add `manuallyChecked` ids (overrides exclusion)
6. Remove `manuallyUnchecked` ids **last** (final override — manual uncheck wins)

Glob matching: `FileSystemName.MatchesSimpleExpression(pattern, id, ignoreCase: true)` — BCL, net8.0, zero new NuGet dependencies, no ReDoS surface.

### GlobExpanderTests (orchestrator/Argus.Orchestrator.Tests/GlobExpanderTests.cs)

8 `[Fact]` tests covering every behavior listed in the plan:
- Include-only pattern selection
- Exclude pattern removal
- No-include → all-entities base set
- Manual-check overrides exclude (step 5 after step 4)
- Manual-uncheck overrides include (step 6, applied last)
- Case-insensitive matching (uppercase pattern matches lowercase entity_id)
- Empty/whitespace patterns ignored (treated as "no pattern")
- Manual-uncheck beats manual-check when both apply to same id (ordering guarantee)

### 10-config-gen.sh guard (argus/rootfs/etc/cont-init.d/10-config-gen.sh)

Added `if [ -f /data/.ui_config_present ]` guard around `gen-entities.py`:

- `ARGUS_ENTITIES_PATH` export remains **unconditional** (env var always set for orchestrator)
- Lock file present → skip gen-entities.py, emit `bashio::log.info` message
- Lock file absent → run gen-entities.py (byte-for-byte identical to previous behavior)
- Closes T-02-05 (Tampering: restart guard bypass / config erasure)
- Satisfies Wave 1 sequencing: lands BEFORE the Wave 2 save endpoint (plan 03)

## Verification Results

- `dotnet test --filter GlobExpanderTests`: 8/8 pass
- `dotnet test` (full suite): 138/138 pass (0 failures, 0 skipped)
- `dotnet build orchestrator/Argus.Orchestrator.sln`: 0 errors, 0 warnings
- `bash -n 10-config-gen.sh`: syntax OK
- grep `if \[ -f /data/.ui_config_present \]`: present
- grep `python3 /usr/local/bin/gen-entities.py`: present (else-branch)
- No NuGet packages added; no lock file written by this plan

## TDD Gate Compliance

| Gate | Commit | Status |
|------|--------|--------|
| RED — `test(02-02)` | 7a8d02d | PASSED (compile error confirmed) |
| GREEN — `feat(02-02)` | a5782c7 | PASSED (8/8 tests pass) |
| REFACTOR | N/A | Not needed |

## Deviations from Plan

### Auto-added

**[Rule 2 - Missing Critical Behavior] Added 8th test: manual-uncheck-beats-manual-check ordering**
- **Found during:** Task 1 implementation
- **Issue:** Plan listed 7 behavior scenarios; the "uncheck beats check when both apply to same id" case makes the step-6 ordering guarantee explicit and testable
- **Fix:** Added `Resolve_ManualUncheckBeatsManualCheck_WhenBothApply` [Fact]
- **Files modified:** `GlobExpanderTests.cs`
- **Commit:** 7a8d02d

**[Rule 2 - Defensive correctness] Skip null/whitespace ids in manuallyChecked/manuallyUnchecked**
- **Found during:** Task 1 implementation
- **Issue:** Form POST values could theoretically include empty strings; skipping ensures no blank entry is added to the result set
- **Fix:** Added `if (!string.IsNullOrWhiteSpace(id))` guard in steps 5 and 6
- **Files modified:** `GlobExpander.cs`
- **Commit:** a5782c7

## Known Stubs

None. This plan creates pure infrastructure (no UI rendering, no data source wiring). GlobExpander is a stateless utility; the guard only reads a lock file. No placeholder values or stubbed UI components.

## Threat Flags

None. No new network endpoints, auth paths, or trust boundary crossings introduced.
T-02-05 (Tampering: restart guard bypass) is explicitly MITIGATED by the 10-config-gen.sh guard landed in this plan.

## Self-Check

Files created/modified:
- orchestrator/Argus.Orchestrator/Config/GlobExpander.cs: FOUND
- orchestrator/Argus.Orchestrator.Tests/GlobExpanderTests.cs: FOUND
- argus/rootfs/etc/cont-init.d/10-config-gen.sh: FOUND (modified)

Commits:
- 7a8d02d (TDD RED): FOUND
- a5782c7 (TDD GREEN): FOUND
- 7422bdb (Task 2): FOUND

## Self-Check: PASSED
