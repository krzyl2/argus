---
quick_id: 260723-oik
title: "UI: pokaż członków grupy w edytorze + status grup na liście Detectors"
status: complete
completed: 2026-07-23
code_commits:
  - 4d1a423
---

# Quick Task 260723-oik — Summary

Two frontend-only UX fixes in `orchestrator/ui` (Preact + `@preact/signals`), implemented
exactly per the plan. `MemberPicker.tsx` was not touched (shared with `AddDetectorWizard`).
No new CSS — reused existing `.argus-*` classes and the `Card`/`Badge`/`Button` components.

## What changed

### Task 1 — Members visible in the group editor
- `orchestrator/ui/src/components/GroupEditorForm.tsx`
  - Added an always-visible "Selected (N)" list between the `Members` section label and
    `<MemberPicker>`. Renders only when `draftMembers` resolves to ≥1 member.
  - Each row (`li.argus-list-row argus-list-row--tracked`) mirrors MemberPicker's selected-row
    markup: entity id, optional friendly name, unit of measurement, `Badge tone="member"`,
    and a `Button variant="destructive-ghost" size="xs"` "Remove" calling the existing
    `toggleMember(entityId, false)`.
  - Imported `Card` and `Badge`.

### Task 2 — Group status on the Detectors list
- `orchestrator/ui/src/state/groups.ts`
  - `groupStatuses = signal<Record<string, GroupStatus | null>>({})`.
  - `loadGroupStatuses()`: fetches `api/groups/{id}/status` for each loaded group in parallel
    (`Promise.all`, relative path via `apiGet`), tolerates per-group failures (skips failed
    ones, keeps previous value), merges onto the previous map before assigning.
- `orchestrator/ui/src/state/detectors.ts`
  - `DetectorRow.status?: GroupStatus | null`; group rows set `status: groupStatuses.value[id]`
    (read inside the computed body for reactivity — missing key ⇒ `undefined`).
- `orchestrator/ui/src/components/DetectorsPage.tsx`
  - Mount: `await loadGroups(); loadGroupStatuses();` (async IIFE, since `loadGroups` wasn't
    awaited before). Added a separate ~30s interval calling `loadGroups()` + `loadGroupStatuses()`.
    The existing 5s `loadSensors('')` poll is untouched; both intervals cleared on unmount.
- `orchestrator/ui/src/components/DetectorListRow.tsx`
  - `GroupRow` accepts `status`; renders a badge before the member count:
    `undefined` ⇒ nothing, `null` ⇒ `Badge tone="warn"` "Oczekuje",
    `isAnomaly === true` ⇒ `Badge tone="error"` "Anomalia", else `Badge tone="ok"` "Działa".

### Tests (intent-level, vitest)
- `GroupEditorForm.test.tsx`: members visible without searching; empty group renders no
  Selected section; Remove drops the member from `draftMembers`.
- `detectors.test.ts`: group row status is `undefined` unfetched, `null` when fetched-unscored,
  and carries the scored `GroupStatus` (isAnomaly preserved).
- `DetectorListRow.test.tsx`: null→"Oczekuje", scored→"Działa", anomaly→"Anomalia",
  undefined→no badge; member count still rendered alongside the status badge.
- `DetectorsPage.test.tsx`: routes the status URL in the shared mock; asserts each group's
  status is fetched after groups load on mount. Existing 5s-poll assertion preserved.

No existing assertions were weakened.

## Verification (run in `orchestrator/ui`)

`node_modules` was empty on this checkout; ran `npm ci` (added 230 packages) before verifying.

**`npm run build` (tsc -b && vite build) — PASS**
```
vite v8.1.3 building client environment for production...
✓ 67 modules transformed.
../Argus.Orchestrator/wwwroot/index.html                 0.34 kB │ gzip:  0.25 kB
../Argus.Orchestrator/wwwroot/assets/index-D2Mk_kMI.js  66.08 kB │ gzip: 20.57 kB
✓ built in 54ms
```

**`npm test` (vitest) — PASS**
```
 Test Files  34 passed (34)
      Tests  217 passed (217)
```
Targeted run of the 4 updated files: 35 tests passed.

## Deviations from Plan

None functional. One environmental note (Rule 3 — blocking issue): the `orchestrator/ui`
`node_modules` directory was present but empty, so `tsc`/`vite` were unavailable. Resolved by
running `npm ci` before build/test. No plan or code changes resulted.

## Code commit(s)

- `4d1a423` — fix(260723-oik): show group members in editor + group status on detectors list

## Self-Check

- Files verified present: GroupEditorForm.tsx, GroupEditorForm.test.tsx, groups.ts,
  detectors.ts, detectors.test.ts, DetectorsPage.tsx, DetectorsPage.test.tsx,
  DetectorListRow.tsx, DetectorListRow.test.tsx — all in commit 4d1a423.
- Commit `4d1a423` present in git log.
- Build + full test suite green.

## Self-Check: PASSED
