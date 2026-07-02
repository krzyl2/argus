# Phase 8 Plan Check - Group Config UI + Algorithm Chooser

**Verdict:** ISSUES FOUND
**Plans checked:** 08-01, 08-02, 08-03, 08-04
**Issues:** 1 blocker, 2 warnings

## Requirement Coverage

| Requirement | Plans | Status |
|---|---|---|
| GRP-09 | 08-02, 08-04 | Covered |
| ALGO-01 | 08-01, 08-02, 08-04 | Covered |
| ALGO-02 | 08-01, 08-02, 08-04 | Covered |
| ALGO-03 | 08-02, 08-04 | Covered |
| ALGO-04 | 08-02, 08-04 | Covered |
| SRCH-01 | 08-02, 08-03 | Covered |
| SRCH-02 | 08-02, 08-03 | Covered |
| SRCH-03 | 08-02, 08-03, 08-04 | Covered |

All 8 phase requirement IDs appear in at least one plan's requirements frontmatter.

## Dependency / Wave Coherence

08-01 (detector/) and 08-02 (orchestrator/Argus.Orchestrator/) are Wave 1, depends_on: [], fully disjoint directory trees. Correctly parallel.

08-03 (Wave 2, depends_on 08-02) and 08-04 (Wave 3, depends_on 08-03) both touch orchestrator/ui/ shared files (types.ts, GroupEditorForm.tsx, GroupsPage.tsx, state/groups.ts). Correctly sequenced, not parallel. No file-collision risk. Pass.

## Cosmetic-Preset Guard (ALGO-01/02 honesty)

08-01 prohibitions explicitly block cosmetic-only presets and forbid any test/docstring claiming contamination moves the continuous score for ecod/copod/pca/iforest. Task 1 behavior encodes the score-vs-threshold distinction as a test assertion (identical decision_function across two contamination values, differing is_anomaly). peer_divergence threshold correctly changes the flag boundary, not a faked score effect. Matches 08-02's catalog copy instruction. Pass.

08-02's catalog preset table matches 08-01's authoritative param-key contract (threshold, contamination, n_estimators) verbatim. Pass.

## Interface Handshake

Param key names fixed in 08-01, reused verbatim in 08-02's preset table. Pass.

08-02's AUTHORITATIVE JSON contracts block fully specifies all four endpoint shapes. 08-03 Task 1 explicitly instructs shapes MUST match 08-02-SUMMARY contracts exactly and reads that file in context. Pass (re-verify post-execution against actual SUMMARY).

## Sort Bug Fix (Pitfall 4)

08-02 Task 2 explicitly adds OrderByDescending(c => c.Contribution) before caching and switches the topContributor log to the sorted list. AttributionPanel (08-04) explicitly does not re-sort client-side. Pass.

## Attribution Honesty (GRP-09)

08-02 prohibits fabricated contributions for pca/iforest, cache stores empty list. 08-04 AttributionPanel renders an explicit no-attribution state distinct from ranked-bars state. Pass.

## Guided Flow / Suggestions Never Auto-Apply

08-04 groupEditor.ts state machine: pickAlgorithmManually is single-click zero-friction override, guided pick always visibly labeled. AreaSuggestionBanner pre-fills only, never saves, dismiss is session-only. Pass.

## Auth / Config-Write Integrity

08-02 Task 3 puts IsAuthorizedRequest as the first line of all 4 endpoint handlers. Save preserves entities:/_patterns: while replacing only groups:, reuses ConfigWriter/LiveEntitiesConfig.Swap byte-for-byte. Pass.

## HA Registry / Human Verification

HA registry field shapes flagged LOW-confidence in RESEARCH (A1); 08-02 Task 1 instructs defensive null-safe parsing. Live-HA Ingress round-trip is an explicit checkpoint:human-verify blocking task (08-04 Task 3), carried forward from Phase 7 UI-02. Pass.

## Scope Leak Check

No streaming/SSE. No ALGO-F1 univariate sensitivity. No custom HA dashboards. Pass.

## must_haves.prohibitions Presence

All 4 plans include a non-empty prohibitions list. Pass.

---

## BLOCKER

### 1. Delete group is a UI-SPEC-mandated feature with zero coverage in any plan

Dimension: requirement_coverage / key_links_planned
Plans: 08-02, 08-03 (missing from both)

Evidence: 08-UI-SPEC.md's Copywriting Contract specifies a "Delete group" button with an explicit second-click confirm pattern, and the Color contract reserves --color-destructive for "Delete group button only." Neither exists in any plan:
- 08-02's four endpoints are GET /api/groups, POST /api/groups/save, GET /api/detectors/catalog, GET /api/groups/id/status. No delete endpoint.
- 08-03's GroupListRow.tsx spec is "(name, mode badge, member count, status pill, edit link)" - no delete button, no confirm state machine, no deleteGroup mutator in state/groups.ts.

Why this blocks the goal: a phase whose stated goal is "operators author groups" ships no way to remove a group through the built UI. The only path (POST /api/groups/save full-list replace) is never exposed to the operator for deletion since the SPA never omits a group from the payload.

Fix hint: either (a) add a deleteGroup mutator plus a Delete group button/confirm-second-click affordance to GroupListRow.tsx or GroupEditorForm.tsx in plan 08-03, wired to the existing POST /api/groups/save full-list-replace endpoint (no new backend endpoint required), or (b) amend 08-UI-SPEC.md to explicitly defer deletion out of this phase's scope with a stated rationale, removing the contradiction. Do not leave the UI-SPEC and plans silently disagreeing.

## WARNINGS

### 1. 08-04 Task 1 spans 9 files

Plan: 08-04, Task 1. Exceeds the 5-8 file target, but the components are one tightly-coupled state machine plus its presentational views, and RESEARCH Pattern 4 supplies the state machine to copy verbatim. Not a blocker; flag for execution-time context budget awareness.

### 2. Preset numeric values are explicitly ASSUMED, not backtested

Plans: 08-02, 08-04. RESEARCH Assumption A2 and 08-02's own "FLAGGED [ASSUMED]" table both disclose that peer_divergence threshold (2.5/3.5/4.5) and contamination (0.05/0.1/0.2) values are PyOD-default-centered guesses. Appropriately disclosed, not hidden; Advanced override always available; the live human-verify checkpoint exercises Low/Med/High. Not a blocker, but confirm at UAT that High/Low feel operator-sensible.

---

## Recommendation

1 blocker requires resolution before execution: the "Delete group" UI-SPEC requirement has no implementing task in any plan. Either add the minimal delete affordance (reusing the existing save endpoint's full-replace semantics, likely a small addition to plan 08-03) or formally descope it from 08-UI-SPEC.md with an explicit rationale. Returning to planner for revision.
