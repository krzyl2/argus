---
phase: 10-design-system-foundation
plan: 03
subsystem: ui
tags: [preact, components, display]

# Dependency graph
requires: [10-01]
provides:
  - "Card: generic flat surface wrapper over .argus-card / .argus-card--interactive"
  - "Badge: tone-driven pill wrapper over .argus-pill / .argus-pill--{tone} (tracked/member/neutral/ok/warn/error/accent)"
  - "StatusDot: tri-state status dot over .argus-status-dot / .status-{ok|warn|error|idle}, no emoji"
  - "KpiTile: dashboard KPI tile over .argus-kpi-tile family, renders StatusDot in place of value when status set (ready for Phase 11 Dashboard)"
  - "Disclosure: native <details>/<summary class=\"argus-disclosure-toggle\"> wrapper, CSS-driven marker/rotation"
  - "AttributionBar: verified already at spec in place (D-04 retrofit, no code change needed)"
affects: [11, 12, 13]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Display components are pure BEM-class compositions with zero owned state — Card/Badge/StatusDot/KpiTile/Disclosure all render straight from props to markup"
    - "KpiTile composes StatusDot directly (imports it) rather than duplicating the dot markup, when status is set"

key-files:
  created:
    - orchestrator/ui/src/components/Card.tsx
    - orchestrator/ui/src/components/Badge.tsx
    - orchestrator/ui/src/components/StatusDot.tsx
    - orchestrator/ui/src/components/KpiTile.tsx
    - orchestrator/ui/src/components/Disclosure.tsx
  modified: []

key-decisions:
  - "Card's `padding` prop is accepted but currently a documented no-op beyond default ('md') — Plan 10-01 did not add .argus-card--padding-none/sm modifier classes, and the plan explicitly permits keeping the default class and treating 'none'/'sm' as reserved rather than authoring new CSS in this plan (no new argus.css classes allowed per must_haves prohibition)"
  - "KpiTile renders value + unit as sibling spans (matching the Design System reference's flex-row layout) rather than nesting unit inside the value span"
  - "AttributionBar.tsx required no code change — read against the plan's acceptance criteria (composes .argus-attribution-bar* classes, top rank uses --fill--top, single dynamic width style) and it already satisfied every criterion from its Phase 8 authoring. Documented as a verified no-op per the plan's explicit instruction, not silently skipped"

requirements-completed: [COMP-01]

# Metrics
duration: 20min
completed: 2026-07-08
status: complete
---

# Phase 10 Plan 03: Display Components Summary

**Ported five display components (Card, Badge, StatusDot, KpiTile, Disclosure) to Preact as thin BEM-class wrappers over Plan 10-01's CSS, and verified the existing AttributionBar.tsx already satisfies the D-04 retrofit spec with no code changes required.**

## Performance

- **Duration:** ~20 min
- **Completed:** 2026-07-08T09:25:30Z
- **Tasks:** 3
- **Files modified:** 5 (all new)

## Accomplishments

- `Card` exports `Card`/`CardProps` (`padding`/`interactive`/`children`); renders `.argus-card` + `.argus-card--interactive` when interactive
- `Badge` exports `Badge`/`BadgeProps` (`tone`/`children`); renders `.argus-pill .argus-pill--{tone}`, defaulting to `neutral`, covering all 7 tones (tracked/member/neutral/ok/warn/error/accent)
- `StatusDot` exports `StatusDot`/`StatusDotProps` (`status`/`label`); renders `.argus-status-dot.status-{status}` as an `aria-hidden` decorative dot, wrapping dot+text in `.argus-status` when a label is given — contains no emoji or icon
- `KpiTile` exports `KpiTile`/`KpiTileProps` (`label`/`value`/`unit`/`accent`/`status`/`hint`); renders `.argus-kpi-tile` (+ `--accent` modifier), an uppercase `__label`, and either a `StatusDot` (when `status` is set) or a tabular-numeric `__value` + optional `__unit`/`__hint`
- `Disclosure` exports `Disclosure`/`DisclosureProps` (`summary`/`open`/`children`); renders a native `<details>` with `<summary class="argus-disclosure-toggle">` — no JS-controlled expand state, the ▶ marker and rotation are handled entirely by existing CSS
- `AttributionBar.tsx` verified against the plan's acceptance criteria — already composes only `.argus-attribution-bar*` classes (plus the shared `.argus-label` utility class, which predates this plan and is not attribution-bar-specific), the top-ranked row already uses `.argus-attribution-bar__fill--top` (accent fill), and the only inline style is the single data-driven `width` percentage explicitly permitted by the threat model. No changes made — this is a verified no-op, not a skipped task.

## Task Commits

Each task was committed atomically:

1. **Task 1: Card + Badge** - `fb9f83d` (feat)
2. **Task 2: StatusDot + KpiTile** - `5abd32f` (feat)
3. **Task 3: Disclosure (new) + AttributionBar (retrofit verification)** - `7f181bb` (feat)

## Files Created/Modified

- `orchestrator/ui/src/components/Card.tsx` - `Card`/`CardProps`; wraps `.argus-card`
- `orchestrator/ui/src/components/Badge.tsx` - `Badge`/`BadgeProps`; wraps `.argus-pill`
- `orchestrator/ui/src/components/StatusDot.tsx` - `StatusDot`/`StatusDotProps`; wraps `.argus-status-dot`
- `orchestrator/ui/src/components/KpiTile.tsx` - `KpiTile`/`KpiTileProps`; wraps `.argus-kpi-tile` family, composes `StatusDot`
- `orchestrator/ui/src/components/Disclosure.tsx` - `Disclosure`/`DisclosureProps`; native `<details>`/`<summary>`
- `orchestrator/ui/src/components/AttributionBar.tsx` - unchanged (verified at spec)

## Decisions Made

- `Card`'s `padding` prop is part of the exported API (per the plan's artifact spec) but is currently a no-op beyond the default `.argus-card` padding — no `--padding-none`/`--padding-sm` modifier classes exist in `argus.css` and this plan is explicitly forbidden from authoring new classes (must_haves prohibition: "No new argus.css classes authored here")
- `KpiTile` lays out `value` + `unit` as sibling spans (matching the Design System reference's flex-row composition) rather than nesting `unit` inside `value`
- `AttributionBar.tsx` retrofit resolved as a verified no-op — read the file, checked it against every acceptance criterion, found it already compliant from its Phase 8 authoring, and committed no diff for it (documented here instead of silently regenerating an identical file)

## Deviations from Plan

None — plan executed exactly as written. All acceptance criteria met without needing Rule 1-4 fixes.

## Issues Encountered

- `orchestrator/ui/node_modules` was not present in this worktree (git worktrees do not carry gitignored directories from the main checkout) — ran `npm install` locally before running `npx tsc -b`, matching the same note from Plans 10-01 and 10-02. Dev-environment step only, nothing committed (`node_modules/` stays gitignored).

## Next Phase Readiness

- All 6 display-category components (Card, Badge, StatusDot, KpiTile, Disclosure, AttributionBar) now exist/verified as Preact + BEM wrappers
- `KpiTile` is ready for Phase 11's Dashboard screen to consume
- `AttributionBar` remains consumed as-is by the existing Groups attribution panel — no call-site changes needed
- Full `npx tsc -b` passes with zero regressions
- No blockers for the rest of Wave 2 (10-04, 10-05) or Wave 3 retrofit plans

## Self-Check: PASSED

- FOUND: orchestrator/ui/src/components/Card.tsx
- FOUND: orchestrator/ui/src/components/Badge.tsx
- FOUND: orchestrator/ui/src/components/StatusDot.tsx
- FOUND: orchestrator/ui/src/components/KpiTile.tsx
- FOUND: orchestrator/ui/src/components/Disclosure.tsx
- FOUND: fb9f83d (Task 1)
- FOUND: 5abd32f (Task 2)
- FOUND: 7f181bb (Task 3)

---
*Phase: 10-design-system-foundation*
*Completed: 2026-07-08*
