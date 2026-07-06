---
phase: 08-group-config-ui-algorithm-chooser
verified: 2026-07-02T20:12:21Z
status: human_needed
score: 8/8 must-haves verified
behavior_unverified: 0
overrides_applied: 0
human_verification:
  - test: "Build + deploy the add-on image, install/update in real HA, open the add-on via HA 'Open Web UI' (never a direct port)."
    expected: "Navigate to Groups -> Create group: sensor search matches a Polish friendly_name (not just entity_id); sensor list browses by HA area with a domain/Ungrouped fallback."
    why_human: "Live HA Ingress round-trip (real Supervisor base-path proxying, real HA registry data) cannot be exercised by automated component/unit tests — carried forward from Phase 7 UI-02 as an explicit blocking checkpoint in 08-04-PLAN.md Task 3."
  - test: "Pick 3+ members, answer the guided 'What are you monitoring?' question, confirm the suggested algorithm is visibly labeled, then click a different card."
    expected: "The suggested card shows 'Suggested based on your answer' and clicking any other algorithm card overrides the selection in one click with zero friction/no confirm dialog."
    why_human: "Requires live visual confirmation of the guided-flow interaction under real Ingress asset loading; component tests exercise the state machine but not the rendered live experience."
  - test: "Select Low/Med/High (raw params hidden), open Advanced, override one field value."
    expected: "The 'customized' indicator appears next to the still-selected preset radio; saving persists the overridden value; hot-reload applies with no HA restart and entities: config remains untouched."
    why_human: "End-to-end save -> hot-reload -> config-file-untouched behavior requires a live orchestrator process and real entities.yaml on disk."
  - test: "Open an existing joint (ecod/copod) group with a recent verdict; then open a pca/iforest group."
    expected: "ecod/copod group shows ranked per-member contribution bars (largest first); pca/iforest group shows the honest 'This algorithm does not provide per-feature attribution' message, not an error state."
    why_human: "Requires a real nightly batch-scored group with live GroupStatusCache data — cannot be produced by static analysis or unit tests alone."
  - test: "On the Groups list, trigger an area-scoped suggestion banner (>=3 ungrouped sensors sharing an HA area) and click 'Review'."
    expected: "Banner text reads '{N} sensors share area \"{area}\" — group them?'; 'Review' pre-fills #/groups/new's member picker with those sensors but does not save anything; 'Not now' dismisses for the session only."
    why_human: "Requires real HA area-registry data with an actual area sharing >=3 ungrouped sensors — cannot be observed without a live HA instance."
---

# Phase 8: Group Config UI + Algorithm Chooser Verification Report

**Phase Goal:** Operators can author groups, pick detection algorithms through a transparent guided chooser instead of raw parameters, find sensors by friendly name/area, and see which member/feature drove a joint-multivariate anomaly instead of a flat boolean.
**Verified:** 2026-07-02T20:12:21Z
**Status:** human_needed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths (ROADMAP Success Criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Low/Med/High preset without raw params; Advanced reveals/overrides underlying values (ALGO-01/02) — AND presets genuinely change detection (Python honors params, incl. on nightly re-fit) | VERIFIED | `SensitivityPresetPicker.tsx` (radio group, no raw values shown by default) + `AdvancedParamsDisclosure.tsx` (native `<details>` reveals/overrides `draftParams`, preset radio stays selected). Server-side: `registry.py` `fit_one` (line 175-178) reconstructs joint detectors via `_create_detector(detector, params)` instead of `deepcopy` on re-fit — confirmed by passing test `test_fit_one_joint_detector_reapplies_changed_params_on_refit` which asserts `_model.contamination` changes across two fits. `peer_divergence.from_params` and `GroupMultivariateDetector.__init__(detector, params)` both param-aware. |
| 2 | Each algorithm shows "best for…" (ALGO-03); guided "what monitoring" pre-selects + visibly explains + one-click override (ALGO-04) | VERIFIED | `DetectorCatalog.cs` gives every one of 5 detectors a `BestFor` string, rendered via `AlgorithmCard.tsx`. `GuidedFlowStep.tsx` asks the 2-answer question; `state/groupEditor.ts` `guidedRecommended` signal + `AlgorithmCard`'s `guidedRecommended` prop render "Suggested based on your answer — you can pick a different algorithm below."; every card's `onClick` calls `onSelect` unconditionally (one click, no confirm). |
| 3 | Search by friendly_name; browse by HA area/domain; area-scoped suggestions approve-only (SRCH-01/02/03) | VERIFIED | Server: `HaSensorRegistry.GetFiltered` matches `EntityId` OR `FriendlyName` (case-insensitive substring). Client mirror: `sensorMatch.ts` `matchesSensorQuery`. `SensorList.tsx` `groupByArea` mode buckets by `entry.areaName` with a `domain`/"Ungrouped" fallback, sorted alphabetically with fallback sections last. `AreaSuggestionBanner.tsx` `findSuggestion` requires >=3 ungrouped same-area sensors; "Review" only sets `pendingPrefillMembers` + navigates to `#/groups/new` (never calls `saveGroup`); "Not now" is session-only dismiss. |
| 4 | Joint anomaly shows ranked per-feature/per-member contribution; honest "no attribution" for PCA/IForest (GRP-09) | VERIFIED | `BatchSchedulerWorker.cs` line 260 sorts `response.Contributions.OrderByDescending(c => c.Contribution)` before caching (`GroupStatusCache.Set`). `multivariate_detector.py` `_ATTRIBUTABLE = {"ecod", "copod"}` — `contributions` is `None`/empty for pca/iforest (verified at servicer.py lines 305-312, empty list built only `if contributions:`). `AttributionPanel.tsx` renders 4 honest states: loading, no-verdict-yet, ranked bars, and "This algorithm does not provide per-feature attribution." for empty contributions. |

**Score:** 4/4 roadmap Success Criteria verified. All 8 requirement-level must-haves (below) also verified.

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|---|---|---|---|---|
| ALGO-01 | 08-01, 08-04 | Low/Med/High preset without raw params | SATISFIED | `SensitivityPresetPicker.tsx` + catalog-sourced presets; params genuinely honored (`peer_divergence.from_params`, `GroupMultivariateDetector`) |
| ALGO-02 | 08-01, 08-04 | Advanced toggle reveals/overrides raw params | SATISFIED | `AdvancedParamsDisclosure.tsx`; preset radio stays selected + "customized" indicator (`isCustomized`) |
| ALGO-03 | 08-02, 08-04 | "best for…" description per algorithm | SATISFIED | `DetectorCatalog.cs` `BestFor` field, all 5 entries populated; rendered in `AlgorithmCard.tsx` |
| ALGO-04 | 08-04 | Guided chooser pre-selects + explains + one-click override | SATISFIED | `GuidedFlowStep.tsx` + `groupEditor.ts` `guidedRecommended`/`answerGuidedQuestion` + `AlgorithmCard.tsx` visible label + unconditional `onSelect` |
| SRCH-01 | 08-02, 08-03 | Search by friendly_name (not just entity_id) | SATISFIED | `HaSensorRegistry.GetFiltered` (server) + `sensorMatch.ts` `matchesSensorQuery` (client) both match entity_id OR friendly_name |
| SRCH-02 | 08-02, 08-03 | Browse by HA area/domain | SATISFIED | `HaSensorEntry.AreaName`/`Domain` enrichment (`NetDaemonHaEventSource.BuildEntityAreaNamesAsync`) + `SensorList.tsx` `groupByArea` rendering |
| SRCH-03 | 08-04 | Area-scoped suggestions, approve-only | SATISFIED | `AreaSuggestionBanner.tsx` — pre-fill only, `saveGroup()` never auto-invoked |
| GRP-09 | 08-02, 08-04 | Ranked per-feature/member contribution, not flat boolean | SATISFIED | `GroupStatusCache` sort-before-cache + `AttributionPanel.tsx` 4-state honest rendering |

No orphaned requirements — REQUIREMENTS.md lists exactly these 8 IDs mapped to Phase 8, all marked `[x] Complete`, all cross-referenced in at least one PLAN's frontmatter `requirements:` field.

### Required Artifacts

| Artifact | Expected | Status | Details |
|---|---|---|---|
| `detector/argus_detector/group/peer_divergence.py` | `from_params` + instance threshold | VERIFIED | `PeerDivergenceDetector.from_params(params)` used by `_create_detector` |
| `detector/argus_detector/group/multivariate_detector.py` | param-aware `__init__` | VERIFIED | `contamination` (all 4) + `n_estimators` (iforest) honored via `_cast_float`/`_cast_int` |
| `detector/argus_detector/registry.py` | `_create_detector` threads params; `fit_one` reconstructs joint detectors on re-fit | VERIFIED | CR-01 fix at lines 175-178; test passes |
| `detector/argus_detector/servicer.py` | `request.params` passed into group detector construction | VERIFIED | `fit_one(group_slug, detector, matrix, params=dict(request.params))` present |
| `orchestrator/Argus.Orchestrator/Web/DetectorCatalog.cs` | static catalog, no gRPC/Python call | VERIFIED | Pure C# static data; honest copy re: contamination shifting threshold not score |
| `orchestrator/Argus.Orchestrator/Batch/GroupStatusCache.cs` | `IGroupStatusCache` + `ConcurrentDictionary` | VERIFIED | present, wired into `Program.cs` and `BatchSchedulerWorker.cs` |
| `orchestrator/Argus.Orchestrator/Web/GroupInputValidator.cs` | mode/detector consistency, duplicate id, param bounds | VERIFIED | `IsModeDetectorConsistent`, duplicate `GroupBy(GroupId)` check, `ParamSchema` Min/Max enforcement all present |
| `orchestrator/ui/src/state/groups.ts` | signals + `saveGroup`/`deleteGroup` | VERIFIED | `saveGroup` refuses save when `draftDetector.value === null`; `deleteGroup` reuses full-list-replace POST |
| `orchestrator/ui/src/components/GroupEditorForm.tsx` | `hasErrors` includes `noAlgorithmError` | VERIFIED | line 62 `hasErrors = !!memberFloorError || !!unitMismatchError || !!nameError || !!noAlgorithmError` |
| `orchestrator/ui/src/components/AttributionPanel.tsx` | polling, 4 states, URL-encoded groupId | VERIFIED | `encodeURIComponent(groupId)` in fetch path (WR-03 fix) |
| `orchestrator/ui/src/components/AlgorithmChooser.tsx` | guided/manual orchestration | VERIFIED | mounts `GuidedFlowStep` or manual grid, both feed `selectedDetector` |
| `orchestrator/ui/src/components/AreaSuggestionBanner.tsx` | approve-only suggestions | VERIFIED | never calls `saveGroup`; pre-fills draft only |
| `orchestrator/ui/src/api/client.ts` | relative-fetch only | VERIFIED | `apiGet`/`apiPost` both throw on leading-slash path |

### Key Link Verification

| From | To | Via | Status | Details |
|---|---|---|---|---|
| `servicer.py` | `group/peer_divergence.py` | `PeerDivergenceDetector.from_params(dict(request.params))` | WIRED | confirmed in `_create_detector` |
| `servicer.py` | `registry.py` | `fit_one(..., params=dict(request.params))` | WIRED | confirmed |
| `Program.cs` | `Batch/GroupStatusCache.cs` | `GET /api/groups/{id}/status` reads `IGroupStatusCache.Get(id)` | WIRED | confirmed, behind `IsAuthorizedRequest` |
| `Batch/BatchSchedulerWorker.cs` | `Batch/GroupStatusCache.cs` | joint branch calls `_groupStatusCache.Set(sorted entry)` | WIRED | confirmed at line 262, sorted first |
| `Ha/NetDaemonHaEventSource.cs` | `Ha/HaWebSocketClient.cs` | `GetAreaRegistryAsync`/`GetEntityRegistryAsync` called once per connect | WIRED | confirmed |
| `state/groups.ts` | `api/client.ts` | `apiGet('api/groups')` / `apiPost('api/groups/save')` | WIRED | relative paths confirmed |
| `components/MemberPicker.tsx` | `validation/groupParams.ts` | `validateGroupMembers` + `validateUnitConsistency` | WIRED | confirmed |
| `components/AttributionPanel.tsx` | `api/client.ts` | polling `apiGet('api/groups/${id}/status')`, cleared on unmount | WIRED | `clearInterval` in cleanup confirmed |
| `components/SensitivityPresetPicker.tsx` | `api/types.ts` catalog | reads presets to expand label -> params | WIRED | confirmed |
| `BatchSchedulerWorker.cs` | `Web/GroupInputValidator.cs` | `IsModeDetectorConsistent` scheduler guard (defense in depth) | WIRED | confirmed at line 186, skips + logs on mismatch |

### Behavioral Spot-Checks / Test Suites (ground truth, not SUMMARY claims)

| Suite | Command | Result | Status |
|---|---|---|---|
| Python detector | `cd detector && python -m pytest -q` | 194 passed, 8 warnings | PASS (matches expected ~194) |
| .NET orchestrator | `cd orchestrator && dotnet test Argus.Orchestrator.sln -c Release` | 367 passed, 0 failed | PASS (matches expected ~367) |
| SPA vitest | `cd orchestrator/ui && npx vitest run` | 84 passed (11 files) | PASS (matches expected ~84) |
| SPA typecheck | `cd orchestrator/ui && npx tsc --noEmit` | exit 0, no output | PASS |
| SPA build | `cd orchestrator/ui && npx vite build` | built in 44ms, 45 modules | PASS |

Named CR-01 behavioral test individually inspected: `test_fit_one_joint_detector_reapplies_changed_params_on_refit` (detector/tests/test_registry.py) fits an `iforest` group detector twice with different `contamination` values and asserts `_model.contamination` changed on the second fit and the instance was replaced (`is not`) — this is the actual state-transition proof behind CR-01, not just presence of the reconstruction code path.

### Anti-Patterns Found

Scanned all files listed in the 4 plans' `files_modified` frontmatter for `TBD|FIXME|XXX|TODO|HACK|PLACEHOLDER`. One `TODO(plan06): real River HST scoring wired through registry.` found in `detector/argus_detector/servicer.py` line 46 — confirmed via `git log -S` that this marker predates Phase 8 (introduced in Phase 1's `8461428` commit, unrelated per-entity HST scoring path, not touched by any Phase 8 plan). Not a Phase 8 debt marker; no blocker.

No stub patterns (`return <div>Placeholder</div>`, empty `onClick={() => {}}`, hardcoded-empty renders feeding real UI) found in any Phase 8 component. Hardcoded empty-array/object literals found (e.g. `contributions.length === 0`, `draftParams.value = {}`) are legitimate state-machine defaults immediately overwritten by fetch/poll results, not stubs.

### Review-Fix Cross-Check (08-REVIEW.md -> 08-REVIEW-FIX.md claims verified against source)

| Fix | Claim | Source Verification |
|---|---|---|
| CR-01 | joint detectors reconstruct via `_create_detector`, not deepcopy, on re-fit | Confirmed at `registry.py:175-178`; behavioral test passes |
| CR-02 | `hasErrors` includes `noAlgorithmError`; `saveGroup` never silently defaults detector | Confirmed at `GroupEditorForm.tsx:62`, `groups.ts:113-118` |
| CR-03 | `IsModeDetectorConsistent` enforced both directions; scheduler guards fabricated verdicts | Confirmed at `GroupInputValidator.cs:34-41,119-127`, `BatchSchedulerWorker.cs:186-192` |
| WR-01 | duplicate group_id rejected | Confirmed at `GroupInputValidator.cs:62-71` |
| WR-02 | server-side param range validation against catalog | Confirmed at `GroupInputValidator.cs:73-169` |
| WR-03 | `encodeURIComponent(groupId)` in AttributionPanel poll path | Confirmed at `AttributionPanel.tsx:29` |
| WR-04 | covered by CR-03 (same root cause) | Confirmed — no separate code path needed |

All 7 review findings verified fixed in source, not merely claimed in 08-REVIEW-FIX.md.

### Human Verification Required

The phase plan (`08-04-PLAN.md` Task 3, `type="checkpoint:human-verify" gate="blocking"`) explicitly defers the live-HA Ingress round-trip to a human checkpoint — this was NOT executed by the executor agent (confirmed in `08-04-SUMMARY.md`: "Task 3 ... NOT executed by this agent"). This is a documented, intentional deferral, not a gap. See frontmatter `human_verification` list above for the 5 specific checks (search by Polish friendly_name under real Ingress, guided-flow one-click override live, preset/Advanced save + hot-reload + entities.yaml untouched, ranked attribution vs. honest no-attribution on real batch-scored groups, area-suggestion banner pre-fill on real HA area data).

Per plan note: if the live area browse mostly shows "Ungrouped" during human verification, this is the documented device-inherited-area fast-follow (device_registry area resolution deferred, entity-only `area_id` + domain fallback shipped this phase) — NOT a gap.

### Gaps Summary

No gaps found. All 8 requirement IDs (GRP-09, ALGO-01/02/03/04, SRCH-01/02/03) are satisfied in source with passing tests across all three layers (Python 194/194, .NET 367/367, SPA 84/84), clean `tsc --noEmit`, and a successful `vite build`. All 7 code-review findings from `08-REVIEW.md` are verified fixed in source (not just claimed in `08-REVIEW-FIX.md`). The only outstanding item is the explicitly-planned, blocking live-HA human-verify checkpoint (Task 3 of 08-04-PLAN.md), which routes this verification to `human_needed` rather than `passed` per the decision tree — this is expected and by design, mirroring the Phase 7 UI-02 precedent.

---

_Verified: 2026-07-02T20:12:21Z_
_Verifier: Claude (gsd-verifier)_
