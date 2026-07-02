---
phase: 08-group-config-ui-algorithm-chooser
plan: 04
subsystem: ui
tags: [preact, signals, vitest, algorithm-chooser, attribution, group-config]

# Dependency graph
requires:
  - phase: 08-group-config-ui-algorithm-chooser
    provides: "08-02 GET /api/detectors/catalog + GET /api/groups/{id}/status contracts; 08-03 GroupEditorForm #algorithm-chooser-slot mount point and draftDetector/draftParams signals"
provides:
  - "AlgorithmChooser: guided-flow 'What are you monitoring?' question that pre-selects + visibly labels + one-click overrides a detector (ALGO-04), backed by a catalog-sourced guided answer->detector map"
  - "SensitivityPresetPicker + AdvancedParamsDisclosure: Low/Med/High presets that expand catalog params into the group draft (ALGO-01), with an Advanced disclosure that overrides individual fields behind a 'Med, customized' indicator (ALGO-02); each AlgorithmCard shows the catalog's 'best for...' description (ALGO-03)"
  - "AttributionPanel + AttributionBar: polls GET api/groups/{id}/status on #/groups/:id, rendering ranked bars (ecod/copod), an honest no-attribution message (pca/iforest), or a no-verdict-yet state (GRP-09)"
  - "AreaSuggestionBanner: >=3-ungrouped-sensors-per-area proposal on #/groups that pre-fills (never auto-saves) the /groups/new member picker (SRCH-03)"
affects: []

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "state/groupEditor.ts owns the chooser's own state machine (chooserMode/selectedDetector/guidedRecommended, mirrors RESEARCH Pattern 4 verbatim); AlgorithmChooser is the single place that mirrors selectedDetector into state/groups.ts's draftDetector/draftParams (the actual save-time source of truth) via a useEffect — keeps the two modules independently testable without duplicating the persisted draft shape"
    - "draftPresetLabel (new signal in state/groups.ts) is UI-only bookkeeping for the 'customized' indicator — never sent to the server; SensitivityPresetPicker back-derives it from draftParams on mount so an existing group's saved params still show a sensible preset baseline instead of blank"
    - "pendingPrefillMembers (new signal in state/groups.ts) is a one-shot handoff consumed by resetDraft — AreaSuggestionBanner sets it then navigates via location.hash, never calls saveGroup itself (SRCH-03 approve-only guarantee)"

key-files:
  created:
    - orchestrator/ui/src/state/groupEditor.ts
    - orchestrator/ui/src/state/groupEditor.test.ts
    - orchestrator/ui/src/components/AlgorithmChooser.tsx
    - orchestrator/ui/src/components/AlgorithmChooser.test.tsx
    - orchestrator/ui/src/components/GuidedFlowStep.tsx
    - orchestrator/ui/src/components/AlgorithmCard.tsx
    - orchestrator/ui/src/components/SensitivityPresetPicker.tsx
    - orchestrator/ui/src/components/AdvancedParamsDisclosure.tsx
    - orchestrator/ui/src/components/AttributionPanel.tsx
    - orchestrator/ui/src/components/AttributionPanel.test.tsx
    - orchestrator/ui/src/components/AttributionBar.tsx
    - orchestrator/ui/src/components/AreaSuggestionBanner.tsx
  modified:
    - orchestrator/ui/src/components/GroupEditorForm.tsx
    - orchestrator/ui/src/components/GroupsPage.tsx
    - orchestrator/ui/src/state/groups.ts
    - orchestrator/ui/public/css/argus.css

key-decisions:
  - "Kept state/groupEditor.ts's selectedDetector signal separate from state/groups.ts's draftDetector rather than merging them — the chooser's state machine (guided-question/guided-pick-shown/manual transitions) is independently unit-testable per the plan's must_haves, while draftDetector/draftParams remain the single shape saveGroup() persists; AlgorithmChooser's useEffect is the one sync point"
  - "draftPresetLabel and pendingPrefillMembers added as new signals to state/groups.ts (not in the plan's stated files_modified list under a new filename, but within the plan's declared state/groups.ts file) — needed to implement the 'customized' indicator and the approve-only area-suggestion pre-fill without inventing a second draft-state module"
  - "AttributionPanel polls at a fixed 60s interval (CONTEXT.md: 'roughly the batch interval cadence, no SSE' — exact cadence left to this plan's discretion per RESEARCH's Assumptions Log) rather than reading the actual configured batch interval from the backend"
  - "Network/parse errors during attribution polling leave the panel in its last-known state (loaded=true, status unchanged) rather than surfacing an error banner — attribution is documented as a soft, best-effort display, and repeated polling will recover on the next successful tick"

patterns-established:
  - "New argus.css classes for Phase 8 (algorithm-card, sensitivity-preset-picker, attribution-bar/-panel, area-suggestion-banner, guided-flow-step) reuse only existing tokens (--color-accent, --color-text-secondary, --space-*) per the UI-SPEC's 'no new tokens' constraint"

requirements-completed: [ALGO-01, ALGO-02, ALGO-03, ALGO-04, GRP-09, SRCH-03]

# Metrics
duration: 35min
completed: 2026-07-02
status: complete
---

# Phase 8 Plan 04: Algorithm chooser, attribution display, area suggestions Summary

**Guided-flow algorithm chooser with catalog-sourced presets/Advanced-override, ranked joint-anomaly attribution bars, and approve-only area-scoped group suggestions — completing the Phase 8 transparency crux (ALGO-01..04, GRP-09, SRCH-03); the final live-HA Ingress checkpoint is pending human execution.**

## Performance

- **Duration:** ~35 min
- **Started:** 2026-07-02T21:40:00Z
- **Completed:** 2026-07-02T22:15:00Z
- **Tasks:** 2 of 3 automated tasks complete (Task 3 is a `checkpoint:human-verify` requiring physical live-HA hardware access — cannot be executed by this agent; see "Pending Human Checkpoint" below)
- **Files modified:** 17 (12 created, 5 modified)

## Accomplishments
- `state/groupEditor.ts` implements the guided-flow state machine (`chooserMode`/`selectedDetector`/`guidedRecommended`) verbatim from 08-RESEARCH.md Pattern 4, extended to load `GET api/detectors/catalog` once and derive the guided answer->detector map from the catalog's `guided` block rather than hardcoding `together->ecod`/`diverges->peer_divergence`
- `AlgorithmChooser` orchestrates `GuidedFlowStep` (question + "Skip — choose manually", always visible) vs the `AlgorithmCard` grid; answering the guided question shows the recommended card pre-selected AND visibly labeled ("Suggested based on your answer…") while the full grid stays clickable; one click on any other card overrides with zero friction (no confirm), clearing the guided label in the same synchronous update
- `SensitivityPresetPicker` expands a selected Low/Med/High preset's catalog params into the group draft immediately (client-side, no round-trip); `AdvancedParamsDisclosure` reuses the exact `.argus-param-grid`/`.argus-param-field` classes to reveal/override individual fields, with a "{Preset}, customized" indicator that stays visible next to the preset radio (not just inside the collapsed disclosure) whenever >=1 field diverges
- `AlgorithmCard` renders each detector's catalog-sourced `bestFor` text (ALGO-03) — never hardcoded client copy
- `AttributionPanel` polls `GET api/groups/{id}/status` every 60s while `#/groups/:id` is open (cleared on unmount via the `SensorSearchInput` debounce-cleanup discipline), rendering ranked `AttributionBar` rows in received order (never re-sorted — server already sorts per 08-02's Pitfall-4 fix), an honest "This algorithm does not provide per-feature attribution." line for pca/iforest (not an error state), or "No anomaly score yet…" when no verdict exists yet
- `AreaSuggestionBanner` surfaces "{N} sensors share area "{area}" — group them?" on `#/groups` for any area with >=3 ungrouped sensors; "Review" sets a one-shot `pendingPrefillMembers` signal and navigates to `#/groups/new` (operator still edits mode/algorithm and explicitly saves — never auto-groups); "Not now" dismisses for the session only (component-local state, not persisted)

## Task Commits

Each automated task was committed atomically:

1. **Task 1: Guided-flow state machine + algorithm chooser (ALGO-01..04)** - `e6e9e90` (feat)
2. **Task 2: Attribution panel + bar (GRP-09) + area suggestion banner (SRCH-03)** - `2afbb2b` (feat)

**Task 3 (checkpoint:human-verify, live-HA Ingress round-trip): NOT executed by this agent — see "Pending Human Checkpoint" below.**

**Plan metadata:** (this commit)

## Files Created/Modified
- `orchestrator/ui/src/state/groupEditor.ts` - guided-flow chooser state machine + catalog loader
- `orchestrator/ui/src/state/groupEditor.test.ts` - 11 tests: guided answer mapping, override semantics, skip, reset, catalog load + stale-response guard
- `orchestrator/ui/src/components/AlgorithmChooser.tsx` - orchestrates guided/manual chooser, mirrors selection into the draft
- `orchestrator/ui/src/components/AlgorithmChooser.test.tsx` - 7 tests: guided pick shown + labeled, override, bestFor render, preset expansion, Advanced override + customized indicator, existing-detector skip-to-manual
- `orchestrator/ui/src/components/GuidedFlowStep.tsx` - "What are you monitoring?" question + 2 answers + skip link
- `orchestrator/ui/src/components/AlgorithmCard.tsx` - selectable algorithm card (name, best-for, selected/guided-recommended states)
- `orchestrator/ui/src/components/SensitivityPresetPicker.tsx` - Low/Med/High radio group, preset->params expansion, customized-indicator logic
- `orchestrator/ui/src/components/AdvancedParamsDisclosure.tsx` - native details/summary param grid override
- `orchestrator/ui/src/components/AttributionPanel.tsx` - polling panel, 4 states
- `orchestrator/ui/src/components/AttributionPanel.test.tsx` - 4 tests: ranked bars/no-resort/top-accent, no-attribution, no-verdict, poll+cleanup
- `orchestrator/ui/src/components/AttributionBar.tsx` - one ranked row, CSS div-width bar
- `orchestrator/ui/src/components/AreaSuggestionBanner.tsx` - area-scoped suggestion, approve-only pre-fill
- `orchestrator/ui/src/components/GroupEditorForm.tsx` - mounts AlgorithmChooser + AttributionPanel (edit mode only)
- `orchestrator/ui/src/components/GroupsPage.tsx` - mounts AreaSuggestionBanner above the group list
- `orchestrator/ui/src/state/groups.ts` - adds `draftPresetLabel` + `pendingPrefillMembers` signals
- `orchestrator/ui/public/css/argus.css` - new Phase 8 BEM classes (no new tokens)

## Decisions Made
- Kept `state/groupEditor.ts`'s chooser state machine independent from `state/groups.ts`'s draft signals (see key-decisions above) — satisfies the plan's requirement that `groupEditor.test.ts` exercise the state machine directly while `saveGroup()` continues to persist exactly the `draftDetector`/`draftParams` shape it always has
- `SensitivityPresetPicker` back-derives an existing group's preset label by matching its saved params against the catalog's preset table on mount, rather than leaving the "customized" indicator permanently blank for previously-saved groups
- Attribution polling swallows network/parse errors into a "last known state" render rather than an error banner — matches the UI-SPEC's framing of attribution as a soft, best-effort display, not a hard dependency of the save flow

## Deviations from Plan

None - plan executed exactly as written for the two automated tasks. Task 3 (`checkpoint:human-verify`) is inherently non-automatable (requires a real HA Supervisor instance and manual "Open Web UI" navigation) and is documented below rather than skipped silently.

## Issues Encountered
None beyond the expected Task 3 checkpoint.

## Pending Human Checkpoint

**Task 3 — Live-HA Ingress round-trip verification (carried forward from Phase 7 UI-02, per this plan's checkpoint):** not executed. This requires:
1. Build + deploy the add-on image, install/update in a real HA instance.
2. Open the add-on via HA's "Open Web UI" (never a direct port).
3. Navigate to Groups -> Create group; confirm sensor search matches a Polish friendly_name and area/domain browse works.
4. Pick 3+ members, answer the guided question, confirm the suggested algorithm is visibly labeled and one-click overridable.
5. Select Low/Med/High (raw params hidden), open Advanced, override one value, confirm the "customized" indicator; save and confirm hot-reload with no restart.
6. For an existing ecod/copod group with a recent verdict, confirm ranked attribution bars; for pca/iforest confirm the honest no-attribution message.
7. On the Groups list, confirm any area-scoped suggestion banner pre-fills (never auto-saves) on "Review".

Resume signal: "approved" or a description of issues found.

## User Setup Required

Live-HA deployment + verification per the "Pending Human Checkpoint" section above — no other external service configuration required.

## Next Phase Readiness
- All 6 requirements this plan targets (ALGO-01..04, GRP-09, SRCH-03) are implemented and covered by 22 new automated tests; `npx vitest run` (81/81 total), `npx tsc --noEmit`, and `npx vite build` all pass clean
- Phase 8 has no further plans after this one — the milestone's remaining work is the human checkpoint above
- No blockers beyond the pending live-HA verification

---
*Phase: 08-group-config-ui-algorithm-chooser*
*Completed: 2026-07-02*

## Self-Check: PASSED

All 12 created/referenced files verified present on disk; all 3 commit hashes (e6e9e90, 2afbb2b, bc5c21f) verified in git log.
