---
phase: 10-design-system-foundation
verified: 2026-07-08T12:00:00Z
status: human_needed
score: 4/4 must-haves verified (structurally); 0 behavior-unverified
behavior_unverified: 0
overrides_applied: 0
human_verification:
  - test: "Load the Sensors screen, click the Sidebar theme toggle, then the Groups screen — visually confirm every region (sidebar, main content, cards, banners, pills, inputs) swaps to dark values with no light-colored/unstyled region left behind, then reload the page and confirm the dark choice is restored before first paint."
    expected: "No light-leaking region on either live screen; theme persists across reload with no flash of the wrong theme."
    why_human: "Visual, full-page rendering — CSS token architecture and localStorage/matchMedia bootstrap are confirmed correct by static analysis (grep + tsc + tests), but 'no light-leaking region' is an emergent visual property across ~50 component/CSS-rule interactions that only a rendered browser can confirm."
  - test: "Visually compare each of the 17 shared components (Button, Input, Select, Checkbox, SearchInput, Textarea, Card, Badge, StatusDot, KpiTile, AttributionBar, Disclosure, Banner, EmptyState, AlgorithmCard, SensitivityPresetPicker, Sidebar) against its `Argus Design System/components/*` spec, in both light and dark themes."
    expected: "Pixel-accurate match (spacing, color, radius, typography) per the Design System handoff's fidelity statement."
    why_human: "Pixel-accuracy is a visual-appearance judgment; code inspection confirms correct class composition and token usage but not final rendered pixel fidelity."
  - test: "Tab through the Sensors screen (search input, checkboxes, detector select, param inputs, Save button) and the Groups screen (Delete/Edit buttons, AlgorithmCard/SensitivityPresetPicker in the group wizard) using only the keyboard."
    expected: "Every focused element shows a visible 2px accent outline with 2px offset; it is never invisible on any control."
    why_human: "Runtime keyboard-focus rendering; static analysis confirms the single global `:focus-visible` rule is never suppressed anywhere in argus.css (all 5 prior `outline: none` suppressions rescoped to `:focus:not(:focus-visible)`, and no component TSX adds a new suppression), but actual visible-outline confirmation requires a rendered browser."
  - test: "In the Groups wizard, select different AlgorithmCard options and confirm the selected card is distinguishable when viewed in grayscale/color-blindness simulation (2px border thickening, not just an accent color change)."
    expected: "Selected AlgorithmCard shows a visibly thicker (2px vs 1px) border regardless of color perception."
    why_human: "Accessibility visual confirmation (A11Y-02) beyond what a CSS rule inspection can prove; also flags a documented scoping note below re: SensitivityPresetPicker."
---

# Phase 10: Design System Foundation Verification Report

**Phase Goal:** The design system's visual and interaction foundation — dark-mode tokens, the
shared Preact component library, and the two cross-cutting accessibility rules — exists so every
later screen can be built pixel-accurate in both themes without re-deriving these primitives
per-screen.

**Verified:** 2026-07-08
**Status:** human_needed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths (ROADMAP Success Criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Theme toggle in sidebar instantly swaps every token-driven color between light/dark, no light-leaking region, persists in localStorage, restored on reload, consistent across all 5 screens (2 live + 3 placeholders per D-05) | ✓ VERIFIED (structural) | `[data-theme="dark"]` block in `argus.css` (lines 96-126) carries a full, verbatim-ported dark value set matching `Argus Design System/tokens/colors.css` byte-for-byte; `Sidebar.tsx` toggle writes `data-theme` + `localStorage['argus-theme']` and is unit-tested (`Sidebar.test.tsx`, round-trips dark↔light, passing); `main.tsx` reads `localStorage.getItem('argus-theme')` synchronously before `render()`, falling back to `matchMedia`. No hardcoded hex colors found in any component `.tsx` file (grep across all `src/components/*.tsx`) that would defeat theme-swapping. Visual "no light-leaking region" confirmation → human item #1. |
| 2 | Every design-system component (Button, Input, Select, Checkbox, SearchInput, Textarea, Card, Badge, StatusDot, KpiTile, AttributionBar, Disclosure, Banner, EmptyState, AlgorithmCard, SensitivityPreset, Sidebar) exists as a Preact component matching its spec, in both themes | ✓ VERIFIED (structural) | All 17 components confirmed present in `orchestrator/ui/src/components/`, each composing only `.argus-*` BEM classes backed by CSS custom properties (no inline color styles except the one documented dynamic-width exception in `AttributionBar.tsx`). `npx tsc -b` exits 0; full `npx vitest run` passes 92/92 across 13 files. Pre-existing components (EmptyState, AttributionBar, AlgorithmCard, SensitivityPresetPicker) independently re-read and confirmed already spec-compliant — not rubber-stamped from the SUMMARY claims. Pixel-accuracy vs. DS spec → human item #2. |
| 3 | Tabbing through any interactive element shows a visible 2px accent outline with 2px offset — focus never invisible (A11Y-01) | ✓ VERIFIED (structural) | Single global rule in `argus.css`: `:focus-visible { outline: 2px solid var(--color-accent); outline-offset: 2px; }` (lines 147-150). Exhaustive grep for `outline:\s*none` across the whole stylesheet finds exactly 5 occurrences, all scoped `:focus:not(:focus-visible)` (search input, filters textarea, detector select, param field input, error-state param field) — none inside a `:focus-visible` block. No new component `.tsx` file introduces an `outline: none`. Runtime visible-outline confirmation → human item #3. |
| 4 | Selecting an AlgorithmCard or SensitivityPreset radio-card shows a 2px accent border on the selected option, distinguishable without color alone (A11Y-02) | ✓ VERIFIED (structural, with a documented scoping note) | `AlgorithmCard.tsx` toggles `.argus-algorithm-card--selected` which sets `border-color: var(--color-accent); border-width: 2px` (confirmed in both the component and `argus.css` lines 814-817). `SensitivityPresetPicker.tsx` uses native `<input type="radio">` elements (not a bordered "card"); Phase 10's own planning artifacts (`10-CONTEXT.md` D-03, `10-PATTERNS.md` line 286) explicitly scope the "2px border" rule to card-shaped selectors only, treating native radios as compliant via their own browser-rendered filled/unfilled shape affordance rather than a border thicken. This is a documented, pre-planned interpretation — not a silent gap — but is flagged for human sign-off since the roadmap's literal wording ("SensitivityPreset radio-card ... 2px accent border") could be read more literally than the implementation delivers. Visual confirmation → human item #4. |

**Score:** 4/4 truths structurally verified; 0 behavior-unverified; 4 items routed to human visual verification (Step 8 — visual appearance always needs human confirmation, this does not indicate a code defect).

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `orchestrator/ui/public/css/argus.css` | Full light+dark token set + new BEM classes + A11Y-01 fix | ✓ VERIFIED | All 4 token families ported verbatim from `Argus Design System/tokens/*.css` (colors/spacing/typography/elevation) confirmed byte-identical; `[data-theme="dark"]` present, no `@media (prefers-color-scheme: dark)` remains; 8-size typography scale present (micro/label/body/lead/heading/title/display/kpi); only 2 font weights (400/600) |
| `orchestrator/ui/src/main.tsx` | Pre-render theme bootstrap | ✓ VERIFIED | `localStorage.getItem('argus-theme')` read + `matchMedia` fallback, both execute before `render()` |
| `Button.tsx`, `Input.tsx`, `Select.tsx`, `Textarea.tsx`, `Checkbox.tsx`, `SearchInput.tsx` | Form controls | ✓ VERIFIED | All exist, exported, compose correct BEM classes, no inline styles, no focus suppression; `Button.test.tsx` (5/5 passing) |
| `Card.tsx`, `Badge.tsx`, `StatusDot.tsx`, `KpiTile.tsx`, `Disclosure.tsx`, `AttributionBar.tsx` | Display components | ✓ VERIFIED | All exist/verified; StatusDot contains no emoji; AttributionBar top-rank uses `--fill--top` accent class |
| `Banner.tsx`, `EmptyState.tsx`, `AlgorithmCard.tsx`, `SensitivityPresetPicker.tsx` | Feedback + selection | ✓ VERIFIED | Banner covers 5 tones + dismiss; EmptyState/AlgorithmCard/SensitivityPresetPicker independently re-confirmed spec-compliant (not rubber-stamped) |
| `Sidebar.tsx`, `AppShell.tsx` | Navigation shell + theme toggle | ✓ VERIFIED | 5 nav items in D-02 order, 3 disabled placeholders, active-state via `route` signal; `AppShell` renders `.argus-shell` + `Sidebar` + `.argus-main`, no `argus-header`/`argus-footer` remnants anywhere in `src/`; `Sidebar.test.tsx` (2/2 passing) |
| Retrofitted call sites (`SaveBar`, `AddDetectorButton`, `DetectorEntry`, `SensorListRow`, `SensorSearchInput`, `GroupListRow`, `SaveResultBanner`, `GroupSaveResultBanner`, `AreaSuggestionBanner`) | Consume shared components (D-04) | ✓ VERIFIED | All 9 files import and render the shared components; "Save configuration" label preserved verbatim (D-06); two-step arm/confirm delete preserved in `GroupListRow` (no `window.confirm`); no raw `.argus-btn`/`.argus-detector-select`/`.argus-checkbox`/destructive `.argus-btn` markup remains in these files |

### Key Link Verification

| From | To | Via | Status | Details |
|------|-----|-----|--------|---------|
| `main.tsx` | `argus.css` | sets `data-theme` attribute consumed by `[data-theme="dark"]` | ✓ WIRED | Confirmed by code read |
| `Sidebar.tsx` | `router.ts` | reads `route` signal for active nav item | ✓ WIRED | `import { route } from '../router'`, used in `isActive()` |
| `AppShell.tsx` | `Sidebar.tsx` | renders `<Sidebar>` inside `.argus-shell` | ✓ WIRED | Confirmed |
| `Button.tsx` | `argus.css` | class string `argus-btn argus-btn--{variant} argus-btn--{size}` | ✓ WIRED | Confirmed, classes exist in CSS |
| `Badge.tsx` | `argus.css` | class `argus-pill argus-pill--{tone}` | ✓ WIRED | All 7 tones present in CSS |
| `AlgorithmCard.tsx` | `argus.css` | `.argus-algorithm-card--selected` (A11Y-02) | ✓ WIRED | Confirmed |
| `SaveBar.tsx` | `Button.tsx` | imports and renders `<Button variant="primary">Save configuration</Button>` | ✓ WIRED | Confirmed |

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Full TS build compiles | `cd orchestrator/ui && npx tsc -b` | exit 0, no errors | ✓ PASS |
| Full test suite passes | `cd orchestrator/ui && npx vitest run` | 92/92 tests, 13/13 files passed | ✓ PASS |
| Production build succeeds | `cd orchestrator/ui && npm run build` | built in 47ms, output emitted | ✓ PASS |
| Sidebar theme-toggle state transition | `Sidebar.test.tsx` (named test, already in the pre-existing suite) | passes — asserts `data-theme` and `localStorage['argus-theme']` round-trip dark↔light | ✓ PASS |
| Button variant/size/loading/label behaviors | `Button.test.tsx` (5 named tests) | all pass | ✓ PASS |
| No `@media (prefers-color-scheme: dark)` remains | `grep -c prefers-color-scheme argus.css` | 0 | ✓ PASS |
| No unscoped `outline: none` suppresses `:focus-visible` | manual grep audit (5 occurrences, all `:focus:not(:focus-visible)`) | 0 unsafe occurrences | ✓ PASS |

### Requirements Coverage

| Requirement | Source Plan(s) | Description | Status | Evidence |
|-------------|-----------------|--------------|--------|----------|
| THEME-01 | 10-01 | Dark-mode token set matching DS tokens | ✓ SATISFIED | Verbatim token port confirmed byte-identical to `Argus Design System/tokens/colors.css` + `elevation.css` |
| THEME-02 | 10-01, 10-05 | Theme toggle in sidebar, persists, consistent across screens | ✓ SATISFIED | Write half (`Sidebar.tsx`, tested) + restore half (`main.tsx`) both confirmed |
| COMP-01 | 10-02 through 10-07 | Full component set ported to Preact | ✓ SATISFIED | All 17 components exist, wired, and 9 call sites retrofitted |
| A11Y-01 | 10-01, 10-02, 10-06 | Focus always visible (2px accent, 2px offset) | ✓ SATISFIED | Global rule intact, no suppression found anywhere |
| A11Y-02 | 10-04 | Radio-card selection = 2px border, never color alone | ✓ SATISFIED (with documented scoping note re: SensitivityPresetPicker native radios — see Truth #4 and human item #4) | `AlgorithmCard` confirmed; `SensitivityPresetPicker` scoping decision pre-documented in `10-CONTEXT.md`/`10-PATTERNS.md` |

No orphaned requirements: REQUIREMENTS.md maps exactly THEME-01, THEME-02, COMP-01, A11Y-01, A11Y-02 to Phase 10, and all 5 appear in at least one plan's `requirements` frontmatter.

### Anti-Patterns Found

None. Grep for `TBD|FIXME|XXX|TODO|HACK|PLACEHOLDER|coming soon|not yet implemented` across all Phase 10 modified/created files returned zero real hits (the only "placeholder" matches were the `placeholder` prop name/attribute on Input/Textarea/SearchInput, and one code comment documenting the intentionally-disabled D-02 nav items, which is a locked design decision, not a stub). No hardcoded hex colors found in any component `.tsx` file. No inline styles beyond the two documented, permitted exceptions (`AttributionBar`'s dynamic width, and `display: contents` layout wrappers in `SensorListRow`/`MemberPicker`, which are layout-only and carry no color/token values).

### Human Verification Required

See frontmatter `human_verification` — 4 items, all visual/rendering confirmations that cannot be
proven by static analysis alone (theme-swap completeness across live screens, pixel-accuracy vs.
the Design System spec, keyboard focus-ring visibility, and AlgorithmCard/SensitivityPresetPicker
selection-cue distinguishability). None of these indicate a known defect — every underlying
mechanism (tokens, wiring, tests) is independently confirmed correct in the codebase; these are
the class of check that requires a rendered browser and human eyes per the verification
methodology's "visual appearance always needs human" rule.

### Gaps Summary

No gaps found. All must-haves from ROADMAP.md and all 7 plans' frontmatter are structurally
verified in the codebase: `npx tsc -b` exits 0, the full test suite passes 92/92 across 13 files,
`npm run build` succeeds, all 17 design-system components exist and are wired, both cross-cutting
a11y rules are enforced globally with no per-component suppression, and dark-mode tokens are a
verbatim, verified port of the Design System source. The phase is blocked only on human visual
sign-off (Step 8), which is a normal completion gate for a phase whose deliverable is visual/UI,
not a code defect.

---

_Verified: 2026-07-08_
_Verifier: Claude (gsd-verifier)_
