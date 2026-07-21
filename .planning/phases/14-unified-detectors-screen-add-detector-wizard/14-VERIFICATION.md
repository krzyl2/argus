---
phase: 14-unified-detectors-screen-add-detector-wizard
verified: 2026-07-21T18:54:54Z
status: human_needed
score: 17/17 truths verified (0 failed); 3 backstop items abstained to human review
behavior_unverified: 0
overrides_applied: 0
human_verification:
  - test: "Click through /detectors -> Add detector -> select exactly 1 sensor -> Configure detector"
    expected: "Lands on /detectors/sensor/<entityId> with the SingleDetectorEditorForm showing that sensor's detector-assignment UI; the sensor now appears as a tracked row back on /detectors after saving"
    why_human: "Real end-to-end route-switch + save round trip through main.tsx's App() conditional render; no dedicated main.tsx/App test exists (14-04-SUMMARY.md flags this itself), and visual/interaction correctness of the hand-off cannot be inferred from unit tests alone"
  - test: "Click through /detectors -> Add detector -> select >=2 sensors -> Create group"
    expected: "Lands on /groups/new with GroupEditorForm's member picker pre-filled with the selected entity ids, then the operator can proceed through the existing AlgorithmChooser -> GuidedFlowStep -> SensitivityPresetPicker flow unmodified"
    why_human: "Same end-to-end route-switch concern as above, for the second wizard exit"
  - test: "Visually compare the unified /detectors list (group + sensor rows) and the AddDetectorWizard against the Argus Design System reference (ui_kits/admin/index.html / HANDOFF_TO_CLAUDE_CODE.md)"
    expected: "Row spacing, badge tones, button labels, and section rhythm match the DS reference; the two row variants read as one consistent list"
    why_human: "Explicit `verification: backstop` must-haves in 14-02-PLAN.md (wizard layout/copy), 14-03-PLAN.md (Settings section additive/non-disruptive), and 14-04-PLAN.md (unified list visual consistency) — visual/Design-System fidelity cannot be confirmed from source code or unit tests; per honest-verifier discipline these abstain to human review rather than silently pass"
---

# Phase 14: Unified Detectors Screen + Add-Detector Wizard Verification Report

**Phase Goal:** Restructure the admin IA so operators manage all anomaly detection from one place
instead of two disconnected screens — one unified "Detectors" list (groups from `api/groups` +
tracked single sensors from `api/sensors`) editable via the existing editors (group →
`GroupEditorForm`; single sensor → a dedicated detector-edit view), plus a separate shared
Add-detector wizard (sensor search reveals results only after ≥3 chars; 1 sensor → single-sensor
path, ≥2 → group path), with the sidebar's Sensors+Groups items replaced by Detectors + Add-detector.

**Verified:** 2026-07-21T18:54:54Z
**Status:** human_needed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Bare `#/sensors`/`#/groups` and empty hash normalize to `/detectors`; `/groups/new`/`/groups/:id` unchanged | ✓ VERIFIED | `router.ts` lines 22-28; `router.test.ts` asserts all 6 cases; `parseGroupId` (lines 35-40) byte-identical to pre-Phase-14 idiom, untouched |
| 2 | `parseSensorEntityId` decodes `/detectors/sensor/:entityId`, returns null on malformed percent-encoding | ✓ VERIFIED | `router.ts` lines 49-57; `router.test.ts` covers decode-success + malformed-null + non-matching cases |
| 3 | Sidebar shows Detectors + Add detector, no Sensors/Groups; `/detectors/*` highlights Detectors | ✓ VERIFIED | `Sidebar.tsx` `NAV_ITEMS` (lines 16-22) + `isActive` (24-31); `Sidebar.test.tsx` asserts label presence/absence and active-route highlighting |
| 4 | `detectorRows` merges groups + tracked-only sensors, namespaced keys, discriminant | ✓ VERIFIED | `state/detectors.ts` (computed, pure derivation, no fetch); `state/detectors.test.ts` (not re-read line-by-line but full suite green covers it) |
| 5 | `MemberPicker` gets optional `minQueryLength` (default 2, Groups unaffected); wizard passes 3 | ✓ VERIFIED | `MemberPicker.tsx` lines 18, 36, 38, 51; `MemberPicker.test.tsx` regression test for the raised threshold; existing default-path tests untouched |
| 6 | `SingleDetectorEditorForm` edits one sensor via the existing `DetectorDisclosure` stack, loads full set on mount (D-07), never touches group draft (Pitfall 6) | ✓ VERIFIED | `SingleDetectorEditorForm.tsx` imports only from `../state/sensors`; `loadSensors('')` on mount (line 34); test proves a pre-set `draftDetector` survives mount unchanged |
| 7 | Untrack action lives only inside `SingleDetectorEditorForm`, calls `setTracked(id, false)`; not on any list row | ✓ VERIFIED | `SingleDetectorEditorForm.tsx` lines 70-76; `DetectorListRow.tsx` group/sensor variants render no delete/untrack control (asserted in `DetectorListRow.test.tsx`) |
| 8 | Selecting exactly 1 sensor in the wizard tracks it and navigates to `#/detectors/sensor/<encoded id>` (WIZ-02) | ✓ VERIFIED | `AddDetectorWizard.tsx` lines 34-39; `AddDetectorWizard.test.tsx` "WIZ-02" test asserts hash + `entityEdits.isTracked` |
| 9 | Selecting ≥2 sensors sets `pendingPrefillMembers` and navigates to `#/groups/new`, consumed by `GroupEditorForm`'s existing `resetDraft()` with zero receiving-end code (WIZ-03) | ✓ VERIFIED | `AddDetectorWizard.tsx` lines 29-33; `state/groups.ts` `resetDraft` (lines 40-44) consumes it; `GroupEditorForm.tsx` line 48 calls `resetDraft()` on mount — traced end-to-end; `AddDetectorWizard.test.tsx` "WIZ-03" test |
| 10 | Full-list-replace save safety: tracking a new sensor after the full set is hydrated preserves every previously-tracked sensor in the `POST /api/sensors/save` body (D-07/WIZ-04, CRITICAL) | ✓ VERIFIED | `AddDetectorWizard.test.tsx` "WIZ-04 (CRITICAL, D-07)" test — seeds 3 pre-tracked sensors, tracks a 4th, asserts all 4 survive the captured POST body |
| 11 | `SettingsPage` renders the relocated Pattern Filters section bound to `includePatterns`/`excludePatterns`, own SaveBar wired to `save()` (D-08b/DET-06) | ✓ VERIFIED | `SettingsPage.tsx` lines 186-198; `SettingsPage.test.tsx` renders assertion |
| 12 | `SettingsPage` calls `loadSensors('')` on mount so its pattern-filter save cannot silently untrack the full set (D-07) | ✓ VERIFIED | `SettingsPage.tsx` line 51; `SettingsPage.test.tsx` mount-fetch + preservation-regression tests |
| 13 | `/detectors` renders one unified list with both a group row and a tracked-sensor row, sourced from `detectorRows` (D-03/DET-01) | ✓ VERIFIED | `DetectorsPage.tsx` + `DetectorList.tsx`; `DetectorsPage.test.tsx` "renders one unified list containing both a group row and a tracked-sensor row" (2 `.argus-list-row`) |
| 14 | Group row Edit link → `#/groups/<encoded groupId>` (unchanged `GroupEditorForm`), no delete/untrack on the row (D-04/DET-02) | ✓ VERIFIED | `DetectorListRow.tsx` `GroupRow` (lines 20-41); `DetectorListRow.test.tsx` |
| 15 | Sensor row Edit link → `#/detectors/sensor/<encoded entityId>`, rows only navigate — no checkbox, no inline disclosure, no untrack/delete (D-03/DET-03/D-08a) | ✓ VERIFIED | `DetectorListRow.tsx` `SensorRow` (lines 43-60); `DetectorListRow.test.tsx` |
| 16 | `main.tsx` routes `/detectors`→`DetectorsPage`, `/detectors/add`→`AddDetectorWizard`, `/detectors/sensor/:id`→`SingleDetectorEditorForm`, fallback→`DetectorsPage` (not `SensorsPage`) (D-05/DET-05) | ✓ VERIFIED | `main.tsx` lines 4-34; `SensorsPage` import absent; `npx tsc -b` clean (integration gate — all 4 imports resolve). Note: no dedicated App/route-switch render test exists (flagged by the executor itself in 14-04-SUMMARY.md); the branch logic is simple string-equality, not a state-transition/cancellation invariant, so presence+`tsc -b`+per-page render tests are accepted as sufficient automated evidence, but the live click-through is still listed under Human Verification below out of caution |
| 17 | Zero backend changes across the whole phase (D-09) | ✓ VERIFIED | `git diff --name-only -- orchestrator/Argus.Orchestrator/` → 0 files; each plan's own SUMMARY records the same check |

**Score:** 17/17 verified truths (0 failed, 0 present-behavior-unverified). 3 additional `verification: backstop` must-haves (wizard visual fidelity, Settings-section non-disruption, unified-list visual consistency) are explicitly non-inferable from code/tests and are routed to Human Verification below rather than silently passed.

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `orchestrator/ui/src/router.ts` | `/detectors` default + redirects + `parseSensorEntityId`/`routeSensorEntityId`; `parseGroupId` untouched | ✓ VERIFIED | Read in full; matches plan exactly |
| `orchestrator/ui/src/state/detectors.ts` | `DetectorRow` + `detectorRows` computed merge | ✓ VERIFIED | Pure computed, no fetch, namespaced keys |
| `orchestrator/ui/src/components/Sidebar.tsx` | Detectors + Add detector nav items, no Sensors/Groups | ✓ VERIFIED | `NAV_ITEMS`/`isActive` restructured |
| `orchestrator/ui/src/components/MemberPicker.tsx` | Optional `minQueryLength` prop, default 2 | ✓ VERIFIED | |
| `orchestrator/ui/src/components/SingleDetectorEditorForm.tsx` | New route component, `state/sensors`-only, D-07 guard, Untrack | ✓ VERIFIED | |
| `orchestrator/ui/src/components/AddDetectorWizard.tsx` | Thin hand-off, 1-vs-≥2 branch, D-07 guard | ✓ VERIFIED | |
| `orchestrator/ui/src/components/SettingsPage.tsx` | Relocated `PatternFiltersPanel` + D-07 guard | ✓ VERIFIED | Existing 3 sections untouched |
| `orchestrator/ui/src/components/DetectorsPage.tsx` | `/detectors` list screen, loads both sources | ✓ VERIFIED | |
| `orchestrator/ui/src/components/DetectorList.tsx` | Card-wrapped unified `<ul>`, empty-state branch | ✓ VERIFIED | |
| `orchestrator/ui/src/components/DetectorListRow.tsx` | Two navigate-only variants dispatched on `row.kind` | ✓ VERIFIED | |
| `orchestrator/ui/src/main.tsx` | Route table wired; `SensorsPage` import removed; fallback → `DetectorsPage` | ✓ VERIFIED | `SensorsPage.tsx` intentionally left on disk, unreferenced (per plan) |

### Key Link Verification

| From | To | Via | Status | Details |
|------|-----|-----|--------|---------|
| `main.tsx` route switch | `DetectorsPage`/`AddDetectorWizard`/`SingleDetectorEditorForm`/`GroupsPage` | `route.value`/`routeSensorEntityId.value` conditionals | ✓ WIRED | All imports resolve (`tsc -b` clean); grep confirms all branches present |
| `AddDetectorWizard` (≥2 exit) | `GroupEditorForm` | `pendingPrefillMembers` signal + `resetDraft()` | ✓ WIRED | Traced end-to-end: wizard sets signal → `state/groups.ts` `resetDraft` consumes+clears it → `GroupEditorForm.tsx` line 48 calls `resetDraft()` on mount — genuinely zero receiving-end code, matches `AreaSuggestionBanner`'s existing idiom |
| `AddDetectorWizard` (1 exit) | `SingleDetectorEditorForm` | `setTracked(id, true)` + `location.hash` | ✓ WIRED | `entityEdits` updated before navigation; form reads `entityEdits.value[entityId]` on mount |
| `DetectorListRow` group variant | `GroupEditorForm` | `<a href="#/groups/:id">` | ✓ WIRED | Unchanged `/groups/:id` route + parser |
| `DetectorListRow` sensor variant | `SingleDetectorEditorForm` | `<a href="#/detectors/sensor/:id">` | ✓ WIRED | New route + `parseSensorEntityId` |
| `state/detectors.ts` `detectorRows` | `state/groups.ts` + `state/sensors.ts` | `computed()` over `groups`/`sensors`/`entityEdits` | ✓ WIRED | No new fetch path (confirmed by reading the file — no `apiGet`) |
| `SettingsPage`/`AddDetectorWizard`/`SingleDetectorEditorForm` mount | `state/sensors.ts` `save()` full-list-replace | `loadSensors('')` on every mount before any save | ✓ WIRED | Present in all three files; backed by 2 preservation regression tests (WIZ-04 + SettingsPage analog) |

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|---|---|---|---|---|
| DET-01 | 14-01, 14-04 | Unified list merging groups + tracked sensors | ✓ SATISFIED | `detectorRows` + `DetectorsPage`/`DetectorList` tests |
| DET-02 | 14-04 | Group row → unchanged `GroupEditorForm` | ✓ SATISFIED | `DetectorListRow.test.tsx` |
| DET-03 | 14-02, 14-04 | Single-sensor row → new dedicated editor route | ✓ SATISFIED | `SingleDetectorEditorForm` + `DetectorListRow.test.tsx` |
| DET-04 | 14-01 | Sidebar restructure, active-route highlighting | ✓ SATISFIED | `Sidebar.test.tsx` |
| DET-05 | 14-01, 14-04 | `/detectors` default route + legacy redirects + route table | ✓ SATISFIED | `router.test.ts` + `main.tsx` + `tsc -b` |
| DET-06 | 14-03 | Pattern Filters relocated to Settings, D-07 guarded | ✓ SATISFIED | `SettingsPage.test.tsx` |
| WIZ-01 | 14-02 | Wizard reveals rows only at ≥3 chars | ✓ SATISFIED | `MemberPicker.test.tsx` + `AddDetectorWizard.tsx` `minQueryLength={3}` |
| WIZ-02 | 14-02 | 1 sensor → track + single-sensor editor | ✓ SATISFIED | `AddDetectorWizard.test.tsx` "WIZ-02" |
| WIZ-03 | 14-02 | ≥2 sensors → group draft prefill hand-off | ✓ SATISFIED | `AddDetectorWizard.test.tsx` "WIZ-03" |
| WIZ-04 | 14-02, 14-03 | Full-list-replace save never drops tracked sensors | ✓ SATISFIED | CRITICAL regression tests in both `AddDetectorWizard.test.tsx` and `SettingsPage.test.tsx` |

All 10 requirement IDs declared in ROADMAP.md's Phase 14 section and REQUIREMENTS.md are covered by
at least one plan and at least one automated test. No orphaned requirements found (REQUIREMENTS.md's
Phase 14 checklist and the plans' `requirements:` frontmatter are in exact 1:1 correspondence).

### Anti-Patterns Found

None. Scanned all 11 phase-modified/created source files (`router.ts`, `state/detectors.ts`,
`Sidebar.tsx`, `MemberPicker.tsx`, `SingleDetectorEditorForm.tsx`, `AddDetectorWizard.tsx`,
`DetectorsPage.tsx`, `DetectorList.tsx`, `DetectorListRow.tsx`, `SettingsPage.tsx`, `main.tsx`) for
`TBD`/`FIXME`/`XXX`/`TODO`/`HACK`/`PLACEHOLDER`/"not yet implemented" markers — zero matches.
`SensorsPage.tsx` is intentionally left on disk unreferenced (documented, surgical, non-blocking
per 14-04's plan and summary — not a stub, just dead code awaiting a later cleanup pass).

### Behavioral / Regression Spot-Checks

| Check | Command | Result | Status |
|---|---|---|---|
| Full frontend test suite | `cd orchestrator/ui && npx vitest run` | 33 test files, 195 tests, all pass | ✓ PASS |
| Type-check (integration gate) | `cd orchestrator/ui && npx tsc -b` | Clean, no errors | ✓ PASS |
| D-07 CRITICAL preservation (wizard) | `AddDetectorWizard.test.tsx` "WIZ-04" (read in full) | 4 tracked ids survive `save()` POST body | ✓ PASS |
| D-07 CRITICAL preservation (Settings) | `SettingsPage.test.tsx` preservation test (per 14-03-SUMMARY, confirmed via full suite pass) | Full tracked set survives a pattern-filter-only save | ✓ PASS |
| Zero backend changes | `git diff --name-only -- orchestrator/Argus.Orchestrator/` | 0 files | ✓ PASS |
| `pendingPrefillMembers` hand-off end-to-end trace | Manual code trace: `AddDetectorWizard.tsx` → `state/groups.ts` `resetDraft` → `GroupEditorForm.tsx` line 48 | Signal set → consumed → cleared, exactly as `AreaSuggestionBanner` already does | ✓ PASS |

### Human Verification Required

1. **Wizard end-to-end click-through — 1-sensor exit**
   **Test:** From `/detectors`, click "Add detector", search a sensor, select exactly one, click "Configure detector".
   **Expected:** Navigates to `/detectors/sensor/<entityId>`; the sensor's detector-assignment UI renders; saving makes it appear as a tracked row back on `/detectors`.
   **Why human:** No dedicated `main.tsx`/App route-switch render test exists (the executor itself flagged this in 14-04-SUMMARY.md); the underlying units are each tested in isolation, but the live hash-driven hand-off has not been exercised end-to-end in a browser.

2. **Wizard end-to-end click-through — ≥2-sensor exit**
   **Test:** From `/detectors`, click "Add detector", select two or more sensors, click "Create group".
   **Expected:** Navigates to `/groups/new` with the member picker pre-filled with the selected sensors; the existing guided algorithm/sensitivity flow proceeds unmodified.
   **Why human:** Same end-to-end concern as above.

3. **Visual/Design-System fidelity of the unified list and the wizard**
   **Test:** Compare `/detectors` (group + sensor rows in one list) and `/detectors/add` against the Argus Design System reference (`Argus Design System/HANDOFF_TO_CLAUDE_CODE.md`, `ui_kits/admin/index.html`).
   **Expected:** Row spacing/rhythm, badge tones, and button copy are visually consistent with the DS reference; the two row variants read as one list, not two stitched-together lists; the new Settings "Auto-track patterns" section does not disturb the existing three read-only sections.
   **Why human:** Explicitly declared `verification: backstop` must-haves in 14-02-PLAN.md, 14-03-PLAN.md, and 14-04-PLAN.md — pure visual/layout fidelity claims that cannot be confirmed by reading source or running unit tests. Per honest-verifier discipline these abstain to human review rather than being silently marked passed.

### Gaps Summary

No blocking gaps. Every truth derivable from ROADMAP.md's Phase 14 goal, the 10 REQ-IDs, and the
four plans' `must_haves.truths` blocks is backed by source code that matches the plan's description
and, where the truth is testable, a passing automated test (full suite: 195/195 green; `tsc -b`
clean; zero backend diff). The three `verification: backstop` items are not gaps — they are
deliberately non-code-inferable visual/interaction claims that the plans themselves flagged for
human sign-off, and this verifier is honoring that flag rather than rubber-stamping them. The one
process note worth carrying forward (not a gap, not blocking): 14-04 has no dedicated
`main.tsx`/App render-switch test — the executor's own summary already recommends a follow-up
`main.test.tsx` or a manual click-through, which is folded into Human Verification item 1/2 above.

---

*Verified: 2026-07-21T18:54:54Z*
*Verifier: Claude (gsd-verifier)*
