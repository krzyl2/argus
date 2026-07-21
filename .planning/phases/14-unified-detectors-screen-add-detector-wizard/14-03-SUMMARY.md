---
phase: 14-unified-detectors-screen-add-detector-wizard
plan: 03
subsystem: ui
tags: [preact, settings, pattern-filters, save-safety]

requires:
  - phase: 14-01-router-sidebar-detector-rows
    provides: "Removes the Sensors nav item, which is what orphans PatternFiltersPanel and motivates this relocation (Pitfall 4) — no direct code dependency, this plan's file is independent."
provides:
  - "SettingsPage.tsx hosts an editable Pattern Filters (auto-track) section bound to the existing includePatterns/excludePatterns signals — D-08b/DET-06"
  - "D-07 full-list-replace save-safety guard (loadSensors('') on SettingsPage mount), backed by a preservation regression test"
affects: [14-04]

tech-stack:
  added: []
  patterns:
    - "Route component mounts loadSensors('') (full set) before any save — D-07/Pitfall 1 guard, now used by SettingsPage in addition to GroupsPage/AddDetectorWizard/SingleDetectorEditorForm"

key-files:
  created:
    - orchestrator/ui/src/components/SettingsPage.test.tsx
  modified:
    - orchestrator/ui/src/components/SettingsPage.tsx

key-decisions:
  - "New section placed after the existing Appearance section (last, additive) rather than interleaved — keeps the three existing read-only sections' order/layout untouched exactly as written"
  - "Local saveState-derived patternsSaving/patternsResult variables added to SettingsPage rather than reusing a name that could shadow the existing s/settings.value read — keeps the read-only Connections/Batch code path visually unchanged"

patterns-established:
  - "Settings screen can now host functional, persisting sections (not just Appearance's local signal) as long as every save-path signal follows the D-07 full-set-load-on-mount discipline before any save() call"

requirements-completed: [DET-06]

coverage:
  - id: D1
    description: "SettingsPage renders a Pattern Filters (auto-track) section using PatternFiltersPanel, bound to includePatterns/excludePatterns, with its own SaveBar wired to save()"
    requirement: "DET-06"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/SettingsPage.test.tsx#D-08b: renders the relocated Pattern Filters section (include/exclude textareas)"
        status: pass
    human_judgment: false
  - id: D2
    description: "SettingsPage calls loadSensors('') on mount so its pattern-filter save cannot silently untrack the entire tracked set"
    requirement: "DET-06"
    verification:
      - kind: unit
        ref: "orchestrator/ui/src/components/SettingsPage.test.tsx#D-07: mounts with a full-set sensors fetch (api/sensors?q=) before any save is possible"
        status: pass
      - kind: unit
        ref: "orchestrator/ui/src/components/SettingsPage.test.tsx#WIZ-04 analog (CRITICAL, D-07): a pattern-filter-only save preserves the full previously-tracked set"
        status: pass
    human_judgment: false
  - id: D3
    description: "The relocation moves only the JSX mount + a full-set guard; include/exclude signals and POST /api/sensors/save are unchanged; no backend change (D-09)"
    requirement: "DET-06"
    verification:
      - kind: unit
        ref: "git diff --name-only against orchestrator/Argus.Orchestrator/ — zero files changed"
        status: pass
    human_judgment: false
  - id: D4
    description: "The relocated section is additive to Settings and does not disturb the existing read-only sections' layout/fidelity"
    verification: []
    human_judgment: true
    rationale: "Visual/layout fidelity backstop must_have — requires human visual review against the Design System reference, not automatable from unit tests alone. Connections/Batch/Appearance sections' JSX was left byte-for-byte untouched; only a new sibling <section> was appended."

duration: 6min
completed: 2026-07-21
status: complete
---

# Phase 14 Plan 03: Pattern Filters Relocation to Settings Summary

**Re-homed `PatternFiltersPanel`'s rendering from the removed Sensors browse screen into `SettingsPage.tsx` as a new "Auto-track patterns" section, with a mandatory D-07 `loadSensors('')` mount guard since its Save reuses the sensors full-list-replace `save()` path — proven safe by a preservation regression test.**

## Performance

- **Duration:** ~6 min
- **Started:** 2026-07-21T20:38:30Z
- **Completed:** 2026-07-21T20:44:47Z
- **Tasks:** 2 completed
- **Files modified:** 2 (1 modified, 1 created)

## Accomplishments
- `SettingsPage.tsx`: new "Auto-track patterns" section (after the existing Connections / Batch & detection / Appearance sections) rendering `PatternFiltersPanel` bound verbatim to the `includePatterns`/`excludePatterns` signals from `state/sensors`, with its own `SaveBar` + `SaveResultBanner` wired to `state/sensors`' `save()`
- Added `loadSensors('')` to `SettingsPage`'s mount effect (D-07, CRITICAL) — the full tracked-sensor set is hydrated into `entityEdits` before this section's Save can ever post, so a pattern-filter-only edit can never silently truncate `entities.yaml`
- All three pre-existing sections (Connections, Batch & detection, Appearance) left byte-for-byte untouched — purely additive change
- Zero backend changes — confirmed via `git diff --name-only` against `orchestrator/Argus.Orchestrator/` (0 files)

## Task Commits

Each task was committed atomically:

1. **Task 1: Relocate PatternFiltersPanel into SettingsPage with a D-07 mount guard (D-08b, D-07)** - `f193073` (feat)
2. **Task 2: SettingsPage pattern-filter tests — render, D-07 mount guard, full-set preservation (D-08b, D-07)** - `96e0518` (test)

## Files Created/Modified
- `orchestrator/ui/src/components/SettingsPage.tsx` - added imports for `PatternFiltersPanel`/`SaveBar`/`SaveResultBanner` and the four `state/sensors` symbols (`includePatterns`, `excludePatterns`, `saveState`, `loadSensors`, `save`); added `loadSensors('')` to the mount effect; added a new "Auto-track patterns" `<section>` after Appearance
- `orchestrator/ui/src/components/SettingsPage.test.tsx` - new; 3 tests: (a) the relocated textareas render with their expected ids, (b) the mount effect fetches `api/sensors?q=` (D-07 proof), (c) a pattern-filter-only edit's `save()` POST body preserves every previously-tracked sensor (preservation regression, mirrors 14-02's WIZ-04)

## Decisions Made
- Placed the new section last (after Appearance) rather than interleaved with the read-only sections, to keep the diff purely additive and avoid any risk of disturbing the existing sections' order/layout fidelity.
- Introduced local `patternsSaving`/`patternsResult` derived variables in `SettingsPage` (mirroring the `saving`/`result` pattern already used in `SensorsPage.tsx`) rather than inlining the ternaries into JSX, for readability parity with the analog this was copied from.

## Deviations from Plan

None - plan executed exactly as written. Both tasks' acceptance criteria (grep checks, `tsc -b`, and the three `vitest` assertions) were verified directly against the final code.

## Issues Encountered
- First test-run attempt used `toBeInTheDocument()` (jest-dom matcher), which is not registered in this project's vitest setup (confirmed no other test file in the codebase uses it — they cast to the concrete HTML element type and assert a property instead). Fixed by asserting `.id` on the cast `HTMLTextAreaElement` returned from `getByLabelText`/`findByLabelText`, matching the existing convention (e.g. `AdvancedParamsDisclosure.test.tsx`). Test-authoring fix only, no production code affected, well under the 3-attempt auto-fix limit.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Pattern Filters are editable again (now on Settings) — no silent feature loss once 14-01's Sensors nav removal ships (Pitfall 4 closed).
- Full frontend suite (`npx vitest run`) passes: 30 test files, 186 tests, no regressions (Appearance theme toggle and the three read-only sections still behave as before).
- `npx tsc -b` type-checks clean.
- D-09 zero-backend-changes guard confirmed: no files under `orchestrator/Argus.Orchestrator/` touched in this plan's commits.
- Ready for 14-04's remaining route-table/cleanup work.

---
*Phase: 14-unified-detectors-screen-add-detector-wizard*
*Completed: 2026-07-21*

## Self-Check: PASSED

Both files (`SettingsPage.tsx` modified, `SettingsPage.test.tsx` created) confirmed present on disk;
both task commit hashes (`f193073`, `96e0518`) confirmed in `git log`.
