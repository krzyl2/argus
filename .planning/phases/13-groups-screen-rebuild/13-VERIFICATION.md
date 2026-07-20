---
phase: 13-groups-screen-rebuild
verified: 2026-07-20T07:13:31Z
status: human_needed
score: 3/3 must-haves verified
behavior_unverified: 0
overrides_applied: 0
human_verification:
  - test: "Toggle the theme switch on #/groups (list), #/groups/new and #/groups/:id (editor shell, member picker, wizard) and visually compare against Argus Design System/ui_kits/admin/Groups.jsx and HANDOFF_TO_CLAUDE_CODE.md in both light and dark themes."
    expected: "Card-wrapped list rows with two Badges, DS page-header + Back affordance, Input/Select/Checkbox/Badge member picker, GuidedFlowStep Card+Buttons, and AlgorithmCard 2px accent-border selection all render pixel-accurate to spec in both themes, with no unstyled/light-leaking regions."
    why_human: "Pixel-accuracy and theme parity require visual inspection in a live browser; cannot be asserted from unit tests or grep."
  - test: "Open a group with a joint verdict and confirm the AttributionBar ranked list (top row accent-filled, others neutral) renders correctly in both themes; open a peer/no-attribution group and confirm the unsupported empty state renders correctly in both themes."
    expected: "Top-ranked bar uses the accent fill, others neutral; unsupported state shows the two-line custom .argus-empty message naming the detector; both match the DS spec visually in light and dark."
    why_human: "Visual/theme parity requires human eyeballing against a live group with a real joint verdict — not something a unit test can assert (per 13-03-SUMMARY.md coverage D5)."
  - test: "Confirm whether ROADMAP SC2's literal wording — 'radio-card detector/preset selection showing the Phase 10 shared component's 2px accent-border state' — is satisfied by SensitivityPresetPicker's native `<input type=radio accent-color>` styling (no bordered card), or whether a literal reading requires SensitivityPresetPicker to become a bordered radio-card like AlgorithmCard."
    expected: "Human sign-off on the pre-existing Phase 10 interpretation (10-CONTEXT.md D-03, 10-PATTERNS.md, 10-VERIFICATION.md truth #4): the 2px-border rule is scoped to card-shaped selectors (AlgorithmCard) only; native radio inputs satisfy A11Y-02 via their own browser-rendered checked/unchecked affordance, not a border thicken. Phase 13 explicitly preserved SensitivityPresetPicker byte-identical (D-07) and did not introduce this ambiguity — it is inherited from Phase 10 and was never re-litigated there or here."
    why_human: "This is a documented, pre-planned scoping decision (not a Phase 13 gap) but the roadmap's SC2 prose could be read more literally than the shipped implementation; Phase 10's own verification already flagged it for human sign-off and it was never closed."
---

# Phase 13: Groups Screen Rebuild Verification Report

**Phase Goal:** The existing, functional Groups screen (`GroupsPage.tsx`, `GroupEditorForm.tsx`,
`MemberPicker.tsx`, `AlgorithmChooser.tsx`, `AttributionBar.tsx`) is rebuilt — markup and component
structure may be refactored, not just restyled — to the Argus Design System spec, with group CRUD,
the guided algorithm wizard, and attribution display fully preserved.
**Verified:** 2026-07-20T07:13:31Z
**Status:** human_needed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth (ROADMAP Success Criteria) | Status | Evidence |
|---|---|---|---|
| 1 | The group editor (list, member picker, save/delete) matches the DS spec in both themes | ✓ VERIFIED (structural + behavioral; visual theme parity → human item #1) | `GroupList.tsx` wraps rows in `Card padding="none"`, custom `.argus-empty` branch for zero groups (not `EmptyState`) — confirmed by direct read + `GroupList.test.tsx` (3/3 passing). `GroupListRow.tsx` renders mode `Badge tone="neutral"` + detector `Badge tone="accent"`, preserves the two-step delete (`armed`/`CONFIRM_WINDOW_MS`/`handleDeleteClick`) and status/no-status meta — exercised with fake timers in `GroupListRow.test.tsx` (arm → confirm → deleteGroup call, and arm → timeout → revert, both asserted). `GroupEditorForm.tsx` renders `.argus-page-header` + "Back to groups" (sets `location.hash`), name field via shared `Input` + `FieldValidationError`, mode via shared `Select` — asserted in `GroupEditorForm.test.tsx`. `MemberPicker.tsx` gates rows behind `MIN_QUERY_LENGTH = 2`, renders `Card`-wrapped rows via shared `Checkbox` + `Badge tone="member"` — both branches and toggle wiring asserted in `MemberPicker.test.tsx`. |
| 2 | The guided algorithm creation wizard's steps (chooser, sensitivity presets, Advanced override) match the DS spec, with radio-card detector/preset selection showing the Phase 10 shared component's 2px accent-border state | ✓ VERIFIED (structural + behavioral; visual theme parity + SC2 literal-wording scoping note → human items #1/#3) | `GuidedFlowStep.tsx` wraps content in `Card padding="sm"`, renders two `Button variant="secondary"` answers + a `Button variant="ghost"` skip, copy byte-identical — asserted in `GuidedFlowStep.test.tsx`. `AlgorithmChooser.tsx`'s catalog map has no `.filter()` keyed on `draftMode` (confirmed by direct read) and the guided step is reachable in both `peer_divergence` and `joint` modes with identical card counts — asserted by the new D-03 regression test in `AlgorithmChooser.test.tsx`. `AlgorithmCard.tsx` unchanged; `argus.css` lines 819-822 confirm `.argus-algorithm-card--selected { border-color: var(--color-accent); border-width: 2px; }`. `AdvancedParamsDisclosure.tsx`'s param fields render via shared `Input` with `updateParam`/`draftParams` wiring preserved — asserted in `AdvancedParamsDisclosure.test.tsx`. `SensitivityPresetPicker.tsx` is byte-unmodified (confirmed via `git log`); Med-default/`isCustomized` regression-covered by the new `SensitivityPresetPicker.test.tsx`. |
| 3 | The attribution panel (AttributionBar) renders ranked per-member/per-feature contribution bars matching the DS spec, in both themes | ✓ VERIFIED (structural + behavioral; visual theme parity → human item #2) | `AttributionPanel.tsx`'s poll `useEffect` (`POLL_INTERVAL_MS = 60_000`, `apiGet`, `setStatus`/`setLoaded`, interval cleanup) is unchanged and all 4 states (loading/no-score/unsupported/ranked) render `Card`-wrapped; the ranked branch adds `.argus-section-label` "Member attribution · last result, refreshes ~60s"; the unsupported branch renders custom `.argus-empty` (no `EmptyState` import in the file). `contributions.map` preserves server order with `topRank={idx === 0}` — no client `.sort()` added. All confirmed by direct read and exercised in `AttributionPanel.test.tsx` (ranked-order-preserved, unsupported-no-EmptyState, no-score, loading, poll-interval-and-cleanup, and a WR-03 URL-encoding regression case — 6/6 passing). `AttributionBar.tsx` unchanged; `argus.css` lines 899-908 confirm `__fill--top` uses `--color-accent`, base `__fill` is neutral — regression-tested in the new `AttributionBar.test.tsx` (accent toggle, width-percent calc incl. 0 and >100% clamp, label/value formatting — 6/6 passing). |

**Score:** 3/3 truths verified (0 present-but-behavior-unverified)

### Required Artifacts

| Artifact | Expected | Status | Details |
|---|---|---|---|
| `orchestrator/ui/src/components/GroupList.tsx` | Card-wrapped list, custom empty branch (GRP-12) | ✓ VERIFIED | Imports/uses `Card`; zero-groups branch is custom `.argus-empty`, no `EmptyState` reference |
| `orchestrator/ui/src/components/GroupListRow.tsx` | Mode + detector Badge, preserved delete/status (D-02) | ✓ VERIFIED | Two `<Badge>` present; `CONFIRM_WINDOW_MS`/`handleDeleteClick` unchanged |
| `orchestrator/ui/src/components/GroupEditorForm.tsx` | Shared Input + Select, DS header + Back (D-01/D-06) | ✓ VERIFIED | `Input`/`Select` imported and used; `.argus-page-header` + hash-setting Back button present; `state/groups.ts` untouched |
| `orchestrator/ui/src/components/MemberPicker.tsx` | Card + Checkbox + Badge behind MIN_QUERY_LENGTH gate (D-06/D-07) | ✓ VERIFIED | `MIN_QUERY_LENGTH = 2` + `queryTooShort` branch retained; `Checkbox`/`Badge`/`Card` used; no bare `<input type="checkbox">` |
| `orchestrator/ui/src/components/GuidedFlowStep.tsx` | DS Card + Buttons, verbatim copy (GRP-13/D-04) | ✓ VERIFIED | `Card`/`Button` used; copy strings byte-identical to pre-existing text |
| `orchestrator/ui/src/components/AdvancedParamsDisclosure.tsx` | Shared Input param fields (D-07) | ✓ VERIFIED | `Input` used per field; `<details>`/`<summary>`/`.argus-param-grid`/`updateParam` unchanged |
| `orchestrator/ui/src/components/AlgorithmChooser.tsx` | Unfiltered catalog + section label (D-03) | ✓ VERIFIED | No `.filter()` on `draftMode`; `.argus-section-label` "Algorithm" added; `useEffect` blocks unchanged |
| `orchestrator/ui/src/components/AttributionPanel.tsx` | Card + SectionLabel + custom empty wrap (GRP-14/D-05) | ✓ VERIFIED | All 4 branches `Card`-wrapped; unsupported branch has no `EmptyState` reference; poll logic unchanged |
| `orchestrator/ui/src/components/AttributionBar.tsx` | Accent-vs-neutral fill contract preserved | ✓ VERIFIED | No prop/logic change; `argus.css` confirms `--color-accent` on `__fill--top` |
| New/extended test files (10 total) | Unit coverage for each rebuild | ✓ VERIFIED | All present on disk; `npm run test -- --run` → 158/158 passing, 25 files (independently re-run, not trusted from SUMMARY) |

### Key Link Verification

| From | To | Via | Status | Details |
|---|---|---|---|---|
| `GroupList.tsx` | `GroupListRow.tsx` | Card-wrapped `<ul>` mapping `GroupConfig[]` to rows | ✓ WIRED | `groups.map(...)` renders one `GroupListRow` per group inside `Card` |
| `GroupEditorForm.tsx` | `Select.tsx` | Mode field | ✓ WIRED | `<Select value={draftMode.value} onChange=.../>` with 2 descriptive options |
| `MemberPicker.tsx` | `Checkbox.tsx` | Row toggle | ✓ WIRED | `onChange={(next) => onToggleMember(entry.entityId, next)}`, exercised in test with `fireEvent.click` |
| `GuidedFlowStep.tsx` | `Button.tsx` (→ `state/groupEditor.ts`) | Answer/skip callbacks | ✓ WIRED | `answerGuidedQuestion('together'|'diverges')` / `skipToManual` wired to `onClick`, unchanged imports |
| `AdvancedParamsDisclosure.tsx` | `Input.tsx` | Param field edit | ✓ WIRED | `onChange={(v) => updateParam(field.key, v)}`, confirmed by test asserting `draftParams` mutation |
| `AttributionPanel.tsx` | `AttributionBar.tsx` | Ranked contribution rows | ✓ WIRED | `status.contributions.map(...)` preserves server order, `topRank={idx === 0}`; no client `.sort()` (grep-confirmed absent) |

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|---|---|---|---|---|
| GRP-12 | 13-01 | Group editor (list, member picker, save/delete) rebuilt to DS spec | ✓ SATISFIED | `GroupList`/`GroupListRow`/`GroupEditorForm`/`MemberPicker` rebuilt with shared primitives; REQUIREMENTS.md marks `Complete` |
| GRP-13 | 13-02 | Algorithm creation wizard rebuilt to DS spec | ✓ SATISFIED | `GuidedFlowStep`/`AdvancedParamsDisclosure`/`AlgorithmChooser` rebuilt, D-03 no-filter guard regression-tested; REQUIREMENTS.md marks `Complete` |
| GRP-14 | 13-03 | Attribution panel / AttributionBar rebuilt to DS spec | ✓ SATISFIED | `AttributionPanel` Card/SectionLabel/empty wrap, `AttributionBar` confirmed already spec-compliant with new regression test; REQUIREMENTS.md marks `Complete` |

No orphaned requirements — all 3 IDs mapped in ROADMAP.md, REQUIREMENTS.md, and plan frontmatter agree.

### Regression Gate (D-07 / Phase-wide)

- `npm run build` (tsc -b && vite build): **PASS** (re-run independently, not trusted from SUMMARY)
- `npm run test -- --run`: **158/158 passing, 25 test files** (re-run independently)
- `git log` confirms zero phase-13 commits touch `state/groups.ts`, `state/groupEditor.ts`, `validation/groupParams.ts`, `AlgorithmCard.tsx`, or `Input.tsx` — the D-07 file set is untouched across all three plans
- `git status` confirms the previously-uncommitted `MemberPicker.tsx` diff (visible at session start) is now fully committed with no working-tree drift

### Anti-Patterns Found

None. Scanned all 10 rebuilt/created production files for `TBD`/`FIXME`/`XXX`/`TODO`/`HACK`/`PLACEHOLDER`/stub-return patterns — the only match (`AttributionPanel.tsx`: "Attribution not available.") is intentional user-facing copy, not a debt marker.

### Human Verification Required

1. **Theme parity — group editor family.** Toggle light/dark on `#/groups`, `#/groups/new`, `#/groups/:id`; confirm Card/Badge/Input/Select/Checkbox rendering is pixel-accurate to the DS spec with no unstyled regions.
2. **Theme parity — attribution panel.** Open a group with a real joint verdict and a group with no attribution support in both themes; confirm accent-vs-neutral bar fill and the custom empty state render correctly.
3. **SC2 literal-wording scoping note (inherited from Phase 10, not introduced here).** `SensitivityPresetPicker.tsx` uses native `<input type="radio" accent-color>` rather than a bordered "radio-card" like `AlgorithmCard`. Phase 10's own `10-CONTEXT.md` (D-03), `10-PATTERNS.md`, and `10-VERIFICATION.md` (truth #4) already documented this as an intentional scoping of the "2px border" rule to card-shaped selectors only — and flagged it for human sign-off, which appears not to have been closed. Phase 13 correctly preserved this component byte-identical (D-07) and did not re-litigate the interpretation; surfacing it here because ROADMAP SC2's prose for *this* phase repeats the "radio-card ... preset selection ... 2px accent-border" wording verbatim.

### Gaps Summary

No functional gaps found. All three ROADMAP success criteria are backed by both structural evidence (component code, CSS tokens, git history) and passing behavioral tests that were independently re-run (158/158, build green) rather than trusted from SUMMARY.md claims. The only open items are (a) visual/theme sign-off, which is inherently a human task and was correctly deferred to `/gsd-verify-work` by all three plans, and (b) a pre-existing, already-documented Phase 10 scoping ambiguity around SensitivityPresetPicker's radio style vs. the literal roadmap wording — inherited, not caused by this phase, but still open for a human decision.

---

*Verified: 2026-07-20T07:13:31Z*
*Verifier: Claude (gsd-verifier)*
