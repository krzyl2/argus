# Phase 10: Design System Foundation - Context

**Gathered:** 2026-07-08
**Status:** Ready for planning

<domain>
## Phase Boundary

Dark-mode tokens, the shared Preact component library (Button/Input/Select/Checkbox/
SearchInput/Textarea/Card/Badge/StatusDot/KpiTile/AttributionBar/Disclosure/Banner/
EmptyState/AlgorithmCard/SensitivityPreset/Sidebar), and the two cross-cutting a11y rules
(focus-visible, radio-card border-not-color) that every later screen (Phases 11-13) depends
on. No new screens are built here — only the foundation and the retrofit of the two screens
that exist today (Sensors, Groups).

</domain>

<decisions>
## Implementation Decisions

### Sidebar / AppShell rollout
- **D-01:** `AppShell.tsx`'s current top-header nav is replaced by the new `Sidebar`
  component in Phase 10 itself — not deferred to per-screen phases. This is the only way
  Success Criteria #1 ("theme toggle in the sidebar, consistent across all 5 screens") is
  even verifiable, since the sidebar must exist in the running app.
- **D-02:** Sidebar renders nav items for all 5 screens (Dashboard, Algorithms, Sensors,
  Groups, Settings) now, in final order/look. Dashboard, Algorithms, and Settings are
  disabled/placeholder ("coming soon") until Phase 11 adds their routes — avoids reshaping
  Sidebar again when those routes land.

### Component library scope
- **D-03:** `AlgorithmCard.tsx` and `SensitivityPresetPicker.tsx` already exist with partial
  CSS (`.argus-algorithm-card`, `.argus-sensitivity-preset-picker`). They are modified in
  place to match the Design System spec exactly (2px selection border, dark mode) — required
  now because Success Criteria #2 and #4 name them explicitly; not deferred to Phase 13.
- **D-04:** For the remaining ~14 components, Phase 10 does full retrofit now: every existing
  call site that currently uses raw `.argus-*` classes directly (`SaveBar`, `DetectorEntry`,
  `SensorSearchInput`, `EmptyState`, etc.) is switched to the new shared components in this
  phase, not left for Phase 12/13's screen rebuilds. **User explicitly chose this over
  Claude's recommendation** (which was library-only, no retrofit) — larger footprint,
  touches files that Phase 12/13 will also touch, but is the locked decision.

### Success Criteria #1 verification scope
- **D-05:** With D-01/D-02/D-04 locked, Success Criteria #1 is verified on the 2 live,
  retrofitted screens (Sensors, Groups) plus the 3 sidebar placeholders — Phase 10 does
  **not** build placeholder routes/pages for Dashboard/Algorithms/Settings. Their consistency
  is a structural consequence of shared tokens/components existing, not something Phase 10
  needs to additionally verify against live routes.

### UI-SPEC checker gate resolutions (2026-07-08, post /gsd-ui-phase)
- **D-06:** The retrofitted primary CTA label is renamed away from the bare generic
  "Save" (flagged by the UI-SPEC checker as a blocklisted generic label) to a specific
  verb+noun label (e.g. "Save changes" / "Save group", exact wording left to the
  component/screen it lives on). This is a narrow, explicit exception to the "retrofit =
  swap implementation, not rewrite copy" framing in D-04 — the label text itself is in
  scope for Phase 10 wherever the shared Button component is adopted.
- **D-07:** The 8-size typography scale (micro/label/body/lead/heading/title/display/kpi)
  from `Argus Design System/tokens/typography.css` is locked as-is, exceeding the UI-SPEC
  template's default 4-size guideline intentionally. It is a verbatim port of the
  already-approved Design System token package (project source of truth), not a new
  design decision — the template limit does not apply to an inherited, locked scale.

### Claude's Discretion
- **Dark-mode activation mechanism** — user did not select this gray area for discussion.
  Flagging a real conflict found during codebase scouting, resolved at Claude's discretion:
  - **Conflict:** `orchestrator/ui/public/css/argus.css` today has dark values ONLY inside
    `@media (prefers-color-scheme: dark)` (OS-driven, no manual toggle possible). This
    contradicts STATE.md's note that "0 dark-mode CSS rules exist" — that note is stale/wrong.
    The Design System's `tokens/colors.css` uses `[data-theme="dark"]` attribute selector
    (manual toggle + localStorage per THEME-02), with a fuller token set (brand-navy,
    status-warn, soft-tint variants, hover states) than the existing media-query block.
  - **Resolution:** Replace the `@media (prefers-color-scheme: dark)` block with a
    `[data-theme="dark"]` attribute block, porting the full token set from
    `Argus Design System/tokens/colors.css` verbatim (names already match 1:1). Add a small
    bootstrap: on first load with no `localStorage` theme key, read
    `matchMedia('(prefers-color-scheme: dark)')` once and set the `data-theme` attribute
    accordingly; after that, an explicit toggle writes to `localStorage` and always wins.
    The old media-query block is removed outright, not kept as a parallel path (per project
    convention of not carrying two competing sources of truth for the same tokens).
  - Flagged here per Rule 7 (surface conflicts, don't average them) — planner/researcher
    should treat this as locked unless the user overrides it later.
- Exact new-component file location/structure inside `orchestrator/ui/src/components/`
  (e.g., flat vs. a `ds/` subfolder) — left to planner, not a user-facing decision.
- Whether new shared components get their own `.test.tsx` files — left to planner per
  existing repo testing conventions (many current components have matching test files).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Design System source of truth
- `Argus Design System/HANDOFF_TO_CLAUDE_CODE.md` — handoff brief: what's reference vs.
  production code, fidelity expectations, rules that must not be broken (typographic brand,
  no emoji/icons beyond Unicode placeholders, radio-card border rule, focus rule, dark-mode
  first-class)
- `Argus Design System/readme.md` — full spec: voice/content, visual foundations,
  iconography, manifest — "source of truth", read first per the handoff doc
- `Argus Design System/tokens/colors.css` — color tokens, light on `:root` / dark on
  `[data-theme="dark"]`, names match `argus.css` 1:1 (verbatim-portable)
- `Argus Design System/tokens/elevation.css`, `tokens/spacing.css`, `tokens/typography.css`
  — remaining token sets to port
- `Argus Design System/ui_kits/admin/index.html` — full interactive 5-screen composition
  reference (nav, theme toggle, filtering, detector assignment, group editor/wizard/
  attribution)
- `Argus Design System/components/*/*.jsx` + `*.d.ts` + `*.prompt.md` — per-component API +
  look spec (16 components across `forms/`, `display/`, `feedback/`, `navigation/`,
  `selection/`). Treat as API+look reference only — these are React with inline styles;
  production code must be Preact using `argus.css` BEM classes (`.argus-*` convention),
  per the handoff doc's explicit instruction.
- `Argus Design System/templates/admin-page/` — empty screen skeleton (sidebar + content
  area) template for new screens

### Existing production code (retrofit targets, D-04)
- `orchestrator/ui/src/components/AppShell.tsx` — current top-header shell, replaced by
  Sidebar layout (D-01)
- `orchestrator/ui/src/components/AlgorithmCard.tsx`,
  `orchestrator/ui/src/components/SensitivityPresetPicker.tsx` — modified in place (D-03)
- `orchestrator/ui/public/css/argus.css` — canonical CSS source (835 lines today); already
  has partial BEM classes for several components (`.argus-btn`/`.argus-btn--*`,
  `.argus-status-dot`, `.argus-empty`, `.argus-banner`/`--*`, `.argus-checkbox`,
  `.argus-search`, `.argus-disclosure-toggle`, `.argus-algorithm-card`/`--selected`,
  `.argus-sensitivity-preset-picker`, `.argus-attribution-bar`) — extend, don't duplicate
- `Web/DetectorCatalog.cs`, `Web/DetectorDefaults.cs`, `orchestrator/ui/src/api/types.ts` —
  data-source references named in REQUIREMENTS.md (used by later phases, not Phase 10, but
  confirm no shape assumptions in shared components conflict with these)

### Requirements / roadmap
- `.planning/ROADMAP.md` §Phase 10 — success criteria (verbatim goal + 4 criteria)
- `.planning/REQUIREMENTS.md` — THEME-01, THEME-02, COMP-01, A11Y-01, A11Y-02

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `.argus-btn` / `.argus-btn--primary` / `.argus-btn--destructive-ghost` — Button variants
  already styled in `argus.css`; new `Button.tsx` should wrap these classes, not reinvent
- `.argus-status-dot`, `.argus-empty`, `.argus-banner`/`--success`/`--error`/`--reloading`/
  `--validation`, `.argus-checkbox`, `.argus-search`, `.argus-disclosure-toggle` — same
  pattern: CSS exists, component extraction is the Phase 10 work
- `AlgorithmCard.tsx`, `SensitivityPresetPicker.tsx` — existing TSX components to bring to
  spec (D-03), not build from scratch

### Established Patterns
- BEM class convention: `.argus-{block}`, `.argus-{block}--{modifier}` — confirmed
  throughout `argus.css` (50+ existing selectors). All new components must follow this,
  per the handoff doc's explicit instruction (not inline styles like the reference `.jsx`).
- `:focus-visible { outline: 2px solid var(--color-accent); outline-offset: 2px; }` already
  exists globally in `argus.css` (line 75-78) — A11Y-01 may already be substantially met at
  the base-element level; verify it isn't overridden/suppressed by any custom component
  states (buttons, radio-cards) rather than assuming it needs to be built from zero.
- Token names are 1:1 identical between `argus.css` and `Argus Design System/tokens/*.css`
  — dark-mode port is verbatim value copy, not a naming reconciliation exercise.

### Integration Points
- `orchestrator/ui/src/router.ts` — hash router; only `/sensors` (default), `/groups`,
  `/groups/new`, `/groups/:id` exist. Sidebar nav items for Dashboard/Algorithms/Settings
  point nowhere yet (D-02 handles as disabled/placeholder).
- `orchestrator/ui/src/main.tsx` — app entry, where `data-theme` bootstrap logic
  (Claude's Discretion decision above) should attach before first paint to avoid a flash of
  wrong theme.

</code_context>

<specifics>
## Specific Ideas

No specific visual references beyond the Design System package itself — it is the
source of truth and should be followed pixel-accurately per the handoff doc's fidelity
statement ("High-fidelity... reproduce pixel-perfect").

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope. Icon-set adoption (Lucide/Material Symbols)
is already out of scope for this milestone per REQUIREMENTS.md (UI-10, deferred).

</deferred>

---

*Phase: 10-design-system-foundation*
*Context gathered: 2026-07-08*
