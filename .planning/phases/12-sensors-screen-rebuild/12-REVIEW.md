---
phase: 12-sensors-screen-rebuild
reviewed: 2026-07-17T00:00:00Z
depth: standard
files_reviewed: 14
files_reviewed_list:
  - orchestrator/ui/public/css/argus.css
  - orchestrator/ui/src/components/AlgorithmCard.test.tsx
  - orchestrator/ui/src/components/AlgorithmCard.tsx
  - orchestrator/ui/src/components/AlgorithmChooser.tsx
  - orchestrator/ui/src/components/DetectorEntry.test.tsx
  - orchestrator/ui/src/components/DetectorEntry.tsx
  - orchestrator/ui/src/components/DetectorParamGrid.test.tsx
  - orchestrator/ui/src/components/DetectorParamGrid.tsx
  - orchestrator/ui/src/components/Input.tsx
  - orchestrator/ui/src/components/SensorList.test.tsx
  - orchestrator/ui/src/components/SensorList.tsx
  - orchestrator/ui/src/components/SensorListRow.test.tsx
  - orchestrator/ui/src/components/SensorListRow.tsx
  - orchestrator/ui/src/components/SensorsPage.tsx
findings:
  critical: 0
  warning: 5
  info: 3
  total: 8
status: issues_found
---

# Phase 12: Code Review Report

**Reviewed:** 2026-07-17T00:00:00Z
**Depth:** standard
**Files Reviewed:** 14
**Status:** issues_found

## Summary

Reviewed the Sensors screen rebuild (Preact/TS): the AlgorithmCard/AlgorithmChooser radio-card pattern, the DetectorEntry/DetectorParamGrid detector-editing grid, SensorList/SensorListRow's group-by-area rebuild, SensorsPage's top-level wiring, and the CSS token/utility file. No security issues and no crash-class bugs were found. Cross-checking the callers of these components against `state/sensors.ts`, `state/groupEditor.ts`, and `api/types.ts` did not surface data-loss or authorization concerns. The issues found are accessibility/robustness regressions and a couple of quality/doc-rot items — all fixable without redesign.

## Warnings

### WR-01: Per-detector radiogroup accessible names collide when an entity has more than one detector

**File:** `orchestrator/ui/src/components/DetectorEntry.tsx:38`
**Issue:** `DetectorEntry` receives `detIdx` as a prop but the `role="radiogroup"` wrapper's `aria-label` only interpolates `entityIdx`:
```tsx
aria-label={`Detector type for entity ${entityIdx}`}
```
`DetectorDisclosure.tsx` renders one `DetectorEntry` per element of `detectors[]`, each with the *same* `entityIdx` but a different `detIdx` (confirmed at `DetectorDisclosure.tsx:32-40`). When a tracked sensor has 2+ detectors, the DOM ends up with multiple `role="radiogroup"` elements sharing an identical accessible name ("Detector type for entity 3", "Detector type for entity 3", …). A screen-reader user tabbing through the panel cannot tell which radiogroup controls which detector. The existing test fixtures (`SensorList.test.tsx`, `DetectorEntry.test.tsx`) always use exactly one detector per entity, so this never surfaces in the test suite.
**Fix:**
```tsx
aria-label={`Detector ${detIdx + 1} type for entity ${entityIdx}`}
```

### WR-02: `role="radio"`/`role="radiogroup"` used without the expected keyboard interaction model

**File:** `orchestrator/ui/src/components/AlgorithmCard.tsx:20-26`, `orchestrator/ui/src/components/DetectorEntry.tsx:36-50`, `orchestrator/ui/src/components/AlgorithmChooser.tsx:76-88`
**Issue:** The ARIA radio/radiogroup pattern requires roving `tabindex` (only the checked — or first — radio is a Tab stop; Left/Right/Up/Down arrows move selection between siblings). Here every `AlgorithmCard` is a plain `<button>` with no `tabIndex` management and no keydown handler, so each card is its own Tab stop and arrow keys do nothing. Assistive tech that announces "radio button" keyboard affordances based on the role will mislead keyboard/screen-reader users. This is a new pattern introduced by this rebuild (previously a native `<select>`, per `DetectorEntry.test.tsx:75-88` "does not import/render the old Select element"), not a pre-existing one being preserved.
**Fix:** Either implement roving tabindex + arrow-key handling on the radiogroup container, or drop `role="radio"/"radiogroup"` in favor of a pattern (e.g. `aria-pressed` toggle buttons in a plain group) that matches the actual keyboard behavior implemented.

### WR-03: `.argus-sensor-list-grouped` wrapper class is used but never styled

**File:** `orchestrator/ui/src/components/SensorList.tsx:101`, `orchestrator/ui/public/css/argus.css`
**Issue:** The group-by-area rebuild wraps its `<details>` sections in `<div class="argus-sensor-list-grouped">`, and both the component and `SensorList.test.tsx:87/132` assert on this class, but `argus.css` has no `.argus-sensor-list-grouped` rule anywhere in the file (confirmed via full-file search). Section `<details>` elements render with no gap/margin between them and no spacing above/below the wrapper, so grouped mode currently renders header-hugging-header/list-hugging-header with none of the visual separation the rest of `argus.css`'s Phase 2/3 rules provide for the flat list.
**Fix:** Add a rule providing vertical rhythm between sections, e.g.:
```css
.argus-sensor-list-grouped {
  display: flex;
  flex-direction: column;
  gap: var(--space-lg);
}
```

### WR-04: Unvalidated cast of `existingDetector` to `GroupDetectorName` masks corrupt/unknown data

**File:** `orchestrator/ui/src/components/AlgorithmChooser.tsx:43`
**Issue:**
```tsx
loadChooserFromDetector(existingDetector as Parameters<typeof loadChooserFromDetector>[0]);
```
`existingDetector` is typed `string | null` at the component boundary (`AlgorithmChooserProps.existingDetector`), not `GroupDetectorName | null`. The cast performs no runtime check. If a saved group's `detector` field is ever a value outside the current `GroupDetectorName` union (schema drift, manual edit, future detector removed from the catalog), `cat.detectors.find((d) => d.name === selected)` at line 69 silently returns `undefined`: no card shows as selected, `SensitivityPresetPicker`/`AdvancedParamsDisclosure` don't render, and the user editing that group gets no indication why — they'd have to notice the missing UI rather than see an error.
**Fix:** Validate against the known detector names before casting, and fall back to `resetChooser()` (or show an inline warning) when the value doesn't match:
```tsx
const known = new Set(catalog.value?.detectors.map((d) => d.name));
if (existingDetector && known.has(existingDetector)) {
  loadChooserFromDetector(existingDetector as GroupDetectorName);
} else {
  resetChooser();
}
```

### WR-05: Section labels use non-semantic `<p class="argus-heading">` instead of heading elements

**File:** `orchestrator/ui/src/components/SensorsPage.tsx:53,68`
**Issue:** `<p class="argus-heading">Sensors</p>` and `<p class="argus-heading">Pattern Filters</p>` are styled to look like section headings but are `<p>` tags, not `<h2>`/`<h3>`. Screen-reader users who navigate a page by its heading outline (a very common workflow, e.g. NVDA/JAWS "H" key) will not see these two sections listed at all, even though the page has a single `<h1>Sensors</h1>` above them implying a document structure that isn't actually there.
**Fix:**
```tsx
<h2 class="argus-heading">Sensors</h2>
...
<h2 class="argus-heading">Pattern Filters</h2>
```

## Info

### IN-01: Inconsistent field label for `frozen_variance_threshold`

**File:** `orchestrator/ui/src/components/DetectorParamGrid.tsx:28`
**Issue:** Every other `FieldSpec` in `HST_FIELDS`/`MAD_FIELDS`/`STL_FIELDS` uses `label === key`. `frozen_variance_threshold` is the only field truncated to `label: 'frozen_variance'`, which also becomes its `aria-label` (per `DetectorParamGrid.test.tsx:17-26`'s pattern). This is inconsistent with the file's own convention and could confuse anyone correlating the visible label with the underlying param key.
**Fix:** Use `label: 'frozen_variance_threshold'` for consistency, or note explicitly in a comment why this one field is intentionally shortened.

### IN-02: Stale comment referencing a save-correlation mechanism that no longer exists

**File:** `orchestrator/ui/src/components/SensorList.tsx:43-44`
**Issue:** The comment states `trackedEntityIdx` matches "the save-handler's alphabetical-sort correlation (see EntityPickerPage.cs BuildListRows)". In this codebase, `save()` (`state/sensors.ts:161-169`) builds `SaveEntity` objects keyed by `entityId` string (`Object.entries(entityEdits.value)...map(([entityId, edit]) => ({ entityId, detectors: edit.detectors }))`), not by array index — there is no positional correlation to preserve any more. `entityIdx` today is used purely to build unique DOM ids (`param-${entityIdx}-${detIdx}-...`). The comment describes a v3.0/server-rendered-era invariant that a future reader could mistakenly treat as a live constraint on entries ordering.
**Fix:** Update the comment to describe the actual current purpose (unique DOM id generation only), or remove the stale cross-reference to `EntityPickerPage.cs`.

### IN-03: Empty, intentionally-blank CSS ruleset

**File:** `orchestrator/ui/public/css/argus.css:301-302`
**Issue:** `.argus-list-row--tracked { }` is an empty rule kept only for the class to exist as a documented hook (comment above it explains the pill is the sole visual distinction). It's harmless but is dead CSS that a linter/build step may flag, and offers no functional value beyond documentation.
**Fix:** Either remove the empty ruleset (keep the class applied in JSX purely for a potential future/test hook without a matching CSS rule) or fold the explanation into a comment without an empty block.

---

_Reviewed: 2026-07-17T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
