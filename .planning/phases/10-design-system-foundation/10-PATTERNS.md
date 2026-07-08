# Phase 10: Design System Foundation - Pattern Map

**Mapped:** 2026-07-08
**Files analyzed:** 21 (1 CSS token file, 1 shell retrofit, 16 new components, ~5 retrofit call sites, 1 bootstrap entry)
**Analogs found:** 21 / 21 (all have either an existing production analog or a Design System reference analog)

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `orchestrator/ui/public/css/argus.css` (dark tokens section) | config (CSS tokens) | transform | `Argus Design System/tokens/colors.css`, `spacing.css`, `typography.css`, `elevation.css` | exact (verbatim port) |
| `orchestrator/ui/src/main.tsx` (theme bootstrap) | provider/bootstrap | event-driven (one-shot on load) | existing `main.tsx` render bootstrap (same file) | exact (extend in place) |
| `orchestrator/ui/src/components/Sidebar.tsx` (new) | component/navigation | request-response (nav clicks) | `Argus Design System/components/navigation/Sidebar.jsx` (look/API ref) + existing `AppShell.tsx` (production structure to replace) | role-match (reference is React/inline-style; must reimplement in Preact/BEM) |
| `orchestrator/ui/src/components/AppShell.tsx` (modified) | component/layout | request-response | itself (existing file, D-01 retrofit) | exact |
| `orchestrator/ui/src/components/Button.tsx` (new) | component/forms | request-response | `.argus-btn`/`--primary`/`--destructive-ghost` in `argus.css` + `SaveBar.tsx`, `DetectorEntry.tsx` (real call sites) + `Argus Design System/components/forms/Button.jsx` (API ref) | exact (CSS classes already exist; call sites show usage) |
| `orchestrator/ui/src/components/Input.tsx` (new) | component/forms | request-response | `.argus-param-field__input` in `argus.css` + usage in `DetectorParamGrid.tsx` | role-match |
| `orchestrator/ui/src/components/Select.tsx` (new) | component/forms | request-response | `.argus-detector-select` in `argus.css` + `DetectorEntry.tsx` lines 28-37 | exact |
| `orchestrator/ui/src/components/Checkbox.tsx` (new) | component/forms | request-response | `.argus-checkbox` in `argus.css` + `SensorListRow.tsx` (uses raw checkbox) | exact |
| `orchestrator/ui/src/components/SearchInput.tsx` (new, replaces raw class use) | component/forms | request-response | existing `SensorSearchInput.tsx` (full pattern: debounce, `.argus-search`) | exact — wrap, don't rebuild debounce logic |
| `orchestrator/ui/src/components/Textarea.tsx` (new) | component/forms | request-response | `.argus-filters__textarea` in `argus.css` + `PatternFiltersPanel.tsx` | exact |
| `orchestrator/ui/src/components/Card.tsx` (new) | component/display | CRUD (container) | `.argus-detector-entry` / `.argus-list` container pattern in `argus.css` | role-match (generalizing an existing block pattern, no single 1:1 analog) |
| `orchestrator/ui/src/components/Badge.tsx` (new) | component/display | transform | `.argus-pill`/`--tracked` in `argus.css` + usage in `GroupListRow.tsx` lines 46, 53 | exact |
| `orchestrator/ui/src/components/StatusDot.tsx` (new) | component/display | transform | `.argus-status-dot`/`.status-ok`/`.status-error` in `argus.css` (no existing .tsx wrapper — used inline today) | role-match |
| `orchestrator/ui/src/components/KpiTile.tsx` (new) | component/display | transform | `Argus Design System/components/display/KpiTile.jsx` (no product analog — new UI, built for later Dashboard) | no analog (use DS reference + typography tokens) |
| `orchestrator/ui/src/components/AttributionBar.tsx` (modified) | component/display | transform | itself (existing file, D-04 retrofit — already close to spec) | exact |
| `orchestrator/ui/src/components/Disclosure.tsx` (new) | component/display | event-driven (toggle) | `.argus-disclosure-toggle` in `argus.css` + `DetectorDisclosure.tsx`, `AdvancedParamsDisclosure.tsx` (native `<details>` usage) | exact |
| `orchestrator/ui/src/components/Banner.tsx` (new) | component/feedback | event-driven | `.argus-banner`/`--success`/`--error`/`--reloading`/`--validation` + `SaveResultBanner.tsx`, `GroupSaveResultBanner.tsx`, `AreaSuggestionBanner.tsx` | exact — 3 existing analogs to consolidate |
| `orchestrator/ui/src/components/EmptyState.tsx` (modified) | component/feedback | transform | itself (existing file, D-04 retrofit) | exact |
| `orchestrator/ui/src/components/AlgorithmCard.tsx` (modified) | component/selection | event-driven (radio select) | itself (existing file, D-03 retrofit) | exact |
| `orchestrator/ui/src/components/SensitivityPresetPicker.tsx` (modified) | component/selection | event-driven (radio select) | itself (existing file, D-03 retrofit) | exact |
| `orchestrator/ui/src/components/SaveBar.tsx` (retrofit call site) | component/forms consumer | request-response | itself, adopts new `Button` | exact |
| `orchestrator/ui/src/components/DetectorEntry.tsx` (retrofit call site) | component consumer | request-response | itself, adopts new `Select`+`Button` | exact |
| `orchestrator/ui/src/components/GroupListRow.tsx` (retrofit call site) | component consumer | event-driven | itself, adopts new `Button` (destructive-ghost, arm/confirm) + `Badge` | exact |

## Pattern Assignments

### `orchestrator/ui/public/css/argus.css` — dark-mode token block (config)

**Analog:** `Argus Design System/tokens/colors.css` (full file), `tokens/spacing.css`, `tokens/typography.css`, `tokens/elevation.css`

**Current (to be replaced) — `argus.css` lines 41-54:**
```css
@media (prefers-color-scheme: dark) {
  :root {
    --color-surface:        #1c1c1e;
    --color-element:        #2c2c2e;
    --color-accent:         #3b9eff;
    --color-border:         #3a3a3c;
    --color-text-primary:   #f2f2f7;
    --color-text-secondary: #aeaeb2;
    --color-destructive:    #ff453a;
    --color-status-ok:      #32d74b;
    --color-status-error:   #ff453a;
  }
}
```

**Replacement pattern — `[data-theme="dark"]` attribute selector, from `tokens/colors.css` lines 44-71:**
```css
:root {
  /* ...existing light tokens, extended with brand-navy, status-warn, soft tints, row-hover, accent-hover per tokens/colors.css lines 7-41... */
  color-scheme: light;
}

[data-theme="dark"] {
  --color-surface:        #1c1c1e;
  --color-element:        #2c2c2e;
  --color-border:         #3a3a3c;
  --color-accent:         #3b9eff;
  --color-brand-navy:     #14213d;
  --color-brand-navy-2:   #1e2f52;
  --color-on-brand:       #cddcf5;
  --color-text-primary:   #f2f2f7;
  --color-text-secondary: #aeaeb2;
  --color-destructive:    #ff453a;
  --color-status-ok:      #32d74b;
  --color-status-warn:    #ffd60a;
  --color-status-error:   #ff453a;
  --color-ok-soft:    color-mix(in srgb, var(--color-status-ok) 18%, var(--color-element));
  --color-warn-soft:  color-mix(in srgb, var(--color-status-warn) 18%, var(--color-element));
  --color-error-soft: color-mix(in srgb, var(--color-status-error) 18%, var(--color-element));
  --color-accent-soft:color-mix(in srgb, var(--color-accent) 16%, var(--color-element));
  --color-row-hover:  color-mix(in srgb, #ffffff 6%, transparent);
  --color-accent-hover: #5cb0ff;
  color-scheme: dark;
}
```

Names are 1:1 identical to existing `argus.css` custom properties — this is a verbatim value port, not a rename. Remove the `@media` block outright (locked, per CONTEXT.md Claude's Discretion resolution — no parallel source of truth).

---

### `orchestrator/ui/src/main.tsx` — theme bootstrap (provider)

**Analog:** existing `main.tsx` itself (extend, don't replace render logic)

**Current — full file (17 lines):**
```typescript
import { render } from 'preact';
import './router';
import { route } from './router';
import { AppShell } from './components/AppShell';
import { SensorsPage } from './components/SensorsPage';
import { GroupsPage } from './components/GroupsPage';

function App() {
  const isGroupsRoute = route.value === '/groups' || route.value.startsWith('/groups/');
  return <AppShell>{isGroupsRoute ? <GroupsPage /> : <SensorsPage />}</AppShell>;
}

const mountEl = document.getElementById('app');
if (mountEl) {
  render(<App />, mountEl);
}
```

**Pattern to add — before `render(...)` call, run synchronously so no flash-of-wrong-theme:**
```typescript
const storedTheme = localStorage.getItem('argus-theme');
if (storedTheme) {
  document.documentElement.setAttribute('data-theme', storedTheme);
} else if (window.matchMedia('(prefers-color-scheme: dark)').matches) {
  document.documentElement.setAttribute('data-theme', 'dark');
}
```
Toggle handler (lives in Sidebar) always writes `localStorage.setItem('argus-theme', next)` and sets the attribute — this wins over the OS preference from then on, per CONTEXT.md Claude's Discretion.

---

### `orchestrator/ui/src/components/AppShell.tsx` → Sidebar layout (D-01)

**Analog:** itself (existing 33-line file, full content already read above) + `Argus Design System/components/navigation/Sidebar.jsx` (API/look reference)

**Current structure to replace** — `<header class="argus-header">` + nav `<a>` tags + `<main class="argus-main">` + `<footer class="argus-footer">`. New structure: `<div class="argus-shell">` (flex row) containing `<Sidebar />` (navy ground, fixed width) + `<main class="argus-main">{children}</main>`. Drop the footer (no footer in Sidebar-based layout per Design System `index.html` composition).

**Core nav-item pattern from `Sidebar.jsx` lines 43-77** (reimplement using BEM classes, not inline styles — per handoff doc, production code must not carry the reference's inline-style approach):
```jsx
{items.map((item) => {
  const active = item.id === activeId;
  return (
    <button
      type="button"
      class={`argus-sidebar__item${active ? ' argus-sidebar__item--active' : ''}${item.disabled ? ' argus-sidebar__item--disabled' : ''}`}
      disabled={item.disabled}
      onClick={() => !item.disabled && onNavigate(item.id)}
    >
      <span aria-hidden="true" class="argus-sidebar__icon">{item.icon}</span>
      <span class="argus-sidebar__label">{item.label}</span>
    </button>
  );
})}
```
Active state: accent left bar (`::before` on `--active`) + `--color-brand-navy-2` background — never color alone is not required here (nav is not a radio-card), but keep for parity with reference. Nav items list (D-02): Dashboard (`▦`, disabled), Algorithms (`⚙`, disabled), Sensors (`◎`, `#/sensors`), Groups (`⧉`, `#/groups`), Settings (`⚙`, disabled). Theme toggle (`☀`/`☾`) rendered in Sidebar footer slot, writes `localStorage` per main.tsx pattern above.

No new CSS block exists yet for `.argus-sidebar*` — author it in `argus.css` using `--color-brand-navy`, `--sidebar-width` (240px), `--sidebar-width-collapsed` (60px) tokens ported alongside the color tokens.

---

### `orchestrator/ui/src/components/Button.tsx` (new)

**Analog:** `.argus-btn` family in `argus.css` lines 353-389, 590-626 (real production CSS) + `SaveBar.tsx` (real call site, full file above) + `DetectorEntry.tsx` lines 39-46 (destructive-ghost call site) + `Argus Design System/components/forms/Button.jsx` (variant/size API reference)

**Existing call-site pattern to preserve (`SaveBar.tsx` lines 9-23):**
```tsx
<button
  type="button"
  class="argus-btn argus-btn--primary"
  disabled={disabled}
  onClick={onSave}
>
  Save configuration
</button>
```

**Existing destructive-ghost + arm/confirm pattern (`GroupListRow.tsx` lines 31-40, 62-68)** — the new `Button` component must support this label-swap-on-click without owning the arm/confirm state itself (state stays in the call site; Button just renders whatever label/variant it's given):
```tsx
<button type="button" class="argus-btn argus-btn--destructive-ghost" onClick={handleDeleteClick}>
  {armed ? 'Confirm delete' : 'Delete group'}
</button>
```

**New variant/size API surface (from DS reference `Button.jsx` lines 9-35, adapted to BEM, no inline styles):**
```tsx
interface ButtonProps {
  variant?: 'primary' | 'secondary' | 'ghost' | 'destructive-ghost';
  size?: 'md' | 'sm' | 'xs';
  disabled?: boolean;
  loading?: boolean;
  type?: 'button' | 'submit';
  onClick?: () => void;
  children: ComponentChildren;
}
// class = `argus-btn argus-btn--${variant} argus-btn--${size}`
```
`--secondary` and `--ghost` variants and size modifiers (`--sm`, `--xs`) do not exist in `argus.css` yet — add them following the existing `--primary`/`--destructive-ghost` declaration pattern (flat color, no shadow, `min-height` per control-height token).

**D-06 copy note:** `SaveBar.tsx` already renders "Save configuration" (not generic "Save") — this label is preserved verbatim when porting to the new `Button`, no new wording introduced.

---

### `orchestrator/ui/src/components/Select.tsx` (new)

**Analog:** `.argus-detector-select` in `argus.css` lines 512-529 + `DetectorEntry.tsx` lines 28-37

```tsx
<select
  class="argus-detector-select"
  aria-label={ariaLabel}
  value={value}
  onChange={(e) => onChange((e.target as HTMLSelectElement).value)}
>
  {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
</select>
```
Focus pattern already present (`argus.css` lines 526-529: `border-color: var(--color-accent); outline: none;`) — audit against A11Y-01 (global `:focus-visible` might be suppressed by this `outline: none`; verify a focus-visible border-thicken or keep the global outline in addition to border-color change).

---

### `orchestrator/ui/src/components/SearchInput.tsx` (new — wraps existing `SensorSearchInput`)

**Analog:** existing `SensorSearchInput.tsx` (full file, 39 lines, read above) — this is the strongest analog in the whole phase; the new shared component should extract this exact debounce+ref pattern and let `SensorSearchInput` become a thin instantiation (or be merged/renamed), not rebuilt from scratch.

```tsx
const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
useEffect(() => () => { if (timerRef.current) clearTimeout(timerRef.current); }, []);
function handleInput(e: Event) {
  const next = (e.target as HTMLInputElement).value;
  if (timerRef.current) clearTimeout(timerRef.current);
  timerRef.current = setTimeout(() => onChange(next), DEBOUNCE_MS);
}
```
CSS: `.argus-search` / `.argus-search__input` already includes the `⌕` glyph expectation per UI-SPEC — check current markup doesn't already need a glyph span added (today's CSS has no `::before` glyph; add one or an inline `<span aria-hidden>⌕</span>` inside `.argus-search`).

---

### `orchestrator/ui/src/components/AlgorithmCard.tsx` (modified, D-03)

**Analog:** itself (existing file, full content read above) — 2px border already implemented via `.argus-algorithm-card--selected` in `argus.css` lines 725-728 (`border-color` + `border-width: 2px`). This already satisfies A11Y-02 (border thickens, not just recolors) — verify dark-mode token values apply correctly once `[data-theme="dark"]` lands; no structural change needed to the `.tsx`, only CSS token propagation.

---

### `orchestrator/ui/src/components/SensitivityPresetPicker.tsx` (modified, D-03)

**Analog:** itself (existing file, full content read above). Native `<input type="radio">` with `accent-color: var(--color-accent)` (argus.css lines 763-767) — already spec-compliant; only dark-mode token wiring needed.

---

### `orchestrator/ui/src/components/Banner.tsx` (new — consolidates 3 existing banner components)

**Analog:** `.argus-banner` family in `argus.css` lines 410-428, 636-639, 665-668 + existing `SaveResultBanner.tsx`, `GroupSaveResultBanner.tsx`, `AreaSuggestionBanner.tsx` (all render a `<div class="argus-banner argus-banner--{tone}">` variant — read one for the exact prop shape before consolidating, they are structurally near-identical per the CSS modifier list).

```css
.argus-banner--success { background-color: var(--color-status-ok); color: #fff; }
.argus-banner--error   { background-color: var(--color-status-error); color: #fff; }
.argus-banner--reloading { background-color: var(--color-accent); color: #fff; }
.argus-banner--validation { background-color: var(--color-status-error); color: #fff; }
```
New `info` tone (dismissable) has no existing CSS — add using `--color-accent-soft`/`--color-text-primary` (subtle fill, not full-strength accent) plus a dismiss `Button` (variant `ghost`, size `xs`).

---

## Shared Patterns

### BEM class convention
**Source:** `argus.css` (50+ existing `.argus-*` / `.argus-*--*` selectors throughout)
**Apply to:** every new component — never inline styles (unlike the `Argus Design System/components/*.jsx` reference files, which use inline styles deliberately as an API/look spec only, per `HANDOFF_TO_CLAUDE_CODE.md`).

### Focus visibility (A11Y-01)
**Source:** `argus.css` lines 75-78
```css
:focus-visible {
  outline: 2px solid var(--color-accent);
  outline-offset: 2px;
}
```
**Apply to:** audit every component with a custom `:focus { outline: none; ... }` override (Select line 528, Input line 586, SearchInput, Textarea line 341, DetectorParamGrid inputs) — these replace outline with border-color today; verify `:focus-visible` still fires visibly (border-color change is not necessarily sufficient alone if outline is suppressed). Recommend keeping `outline: none` only for `:focus` (mouse) while preserving default `:focus-visible` outline (keyboard) — do not add `outline: none` inside a `:focus-visible` rule anywhere.

### Radio-card border-not-color (A11Y-02)
**Source:** `argus.css` lines 725-728 (`.argus-algorithm-card--selected`)
```css
.argus-algorithm-card--selected {
  border-color: var(--color-accent);
  border-width: 2px;
}
```
**Apply to:** `AlgorithmCard.tsx` (already compliant), `SensitivityPresetPicker.tsx` radio inputs (native radios, N/A — border rule applies to card-shaped selectors only), and any future radio-card component built on the shared library.

### Two-step destructive confirm (arm/confirm, no `window.confirm`)
**Source:** `GroupListRow.tsx` lines 10, 21-40 (full pattern above)
**Apply to:** any `Button` variant=`destructive-ghost` usage that performs a destructive action (Remove detector in `DetectorEntry.tsx`, Delete group in `GroupListRow.tsx`) — state (`armed`, timer) stays in the call site, not in the shared `Button`.

### Dark-mode token verbatim port
**Source:** `Argus Design System/tokens/colors.css`, `spacing.css`, `typography.css`, `elevation.css`
**Apply to:** `argus.css` root/`[data-theme="dark"]` blocks — names match 1:1, this is a value copy not a naming exercise (see CONTEXT.md code_context "Established Patterns").

## No Analog Found

| File | Role | Data Flow | Reason |
|---|---|---|---|
| `orchestrator/ui/src/components/KpiTile.tsx` | component/display | transform | No KPI/dashboard surface exists yet in production (Dashboard is Phase 11+); use `Argus Design System/components/display/KpiTile.jsx` as the sole reference, reimplemented in BEM/Preact with `--font-size-kpi` (34px) token from typography.css |
| `orchestrator/ui/src/components/Card.tsx` | component/display | CRUD (generic container) | No single existing "generic card" component — `.argus-detector-entry` and `.argus-list` are the closest structural precedents but are role-specific; Card is a new generalization, not a retrofit of one file |

## Metadata

**Analog search scope:** `orchestrator/ui/src/components/`, `orchestrator/ui/public/css/argus.css`, `orchestrator/ui/src/main.tsx`, `Argus Design System/components/**`, `Argus Design System/tokens/**`
**Files scanned:** 21 production `.tsx` files, 1 CSS file (835 lines), 4 token CSS files, 3 component reference files (Sidebar, Button, colors.css read in full; others by category listing)
**Pattern extraction date:** 2026-07-08
