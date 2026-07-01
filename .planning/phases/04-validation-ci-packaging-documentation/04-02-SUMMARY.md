---
phase: 04-validation-ci-packaging-documentation
plan: 02
subsystem: web-ui
tags: [validation, css, javascript, html, accessibility, unit-tests]
dependency_graph:
  requires: []
  provides:
    - "BuildValidationBanner(int errorCount) — public static on EntityPickerPage"
    - "BuildSuccessBanner(int count, bool hasHst = false) — warm-up-aware overload"
    - "argus-param-field__error-msg spans on all 12 param inputs"
    - "Phase 4 CSS modifiers in argus.css"
    - "Inline JS validation block in BuildFullPage"
  affects:
    - "orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs — BuildSuccessBanner call site upgrade (Plan 04-01 Task 2)"
tech_stack:
  added: []
  patterns:
    - "Inline JS IIFE with compact PR rules object + se/ce/vf helpers for client-side param validation"
    - "Non-interpolated C# raw string literal for JS block to avoid brace-escaping conflicts"
    - "Phase 4 CSS BEM modifier pattern (.argus-param-field--error, .argus-banner--validation, .argus-warmup-note)"
key_files:
  created: []
  modified:
    - orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs
    - orchestrator/Argus.Orchestrator/wwwroot/css/argus.css
    - orchestrator/Argus.Orchestrator.Tests/EntityPickerPageTests.cs
decisions:
  - "hasHst defaulted to false — existing single-arg BuildSuccessBanner(count) call site (Program.cs line 395) compiles unchanged; Plan 04-01 Task 2 wires the real hasHst value"
  - "JS variable names minified to PR/IC/EC/HI/LO/IN1/IN2 to reduce script size toward 2 KB budget"
  - "JS size budget not fully met — see Deviations section"
metrics:
  duration: "~9 minutes"
  completed: "2026-07-01"
  tasks: 2
  files: 3
---

# Phase 04 Plan 02: Validation Error States + Inline JS Summary

Client-side validation layer + server validation-rejection banner + HST warm-up disclosure added via ARIA-linked error spans on all param inputs, a compact inline JS mirror of server rules, four new CSS modifiers, and `BuildValidationBanner`/warm-up-aware `BuildSuccessBanner` builders with unit coverage.

---

## Tasks Completed

| Task | Name | Commit | Key Outputs |
|------|------|--------|-------------|
| 1 | Param error spans + ARIA + id-format migration + Phase 4 CSS | 150bbac | 12 param inputs migrated (p_ → param-), 12 aria-describedby + error spans, 4 new CSS modifiers |
| 2 | BuildValidationBanner + warm-up-aware BuildSuccessBanner + inline JS | 2ff97e0 | Public static builders, 12 new unit tests (37 total, all pass) |

---

## Deviations from Plan

### Spec Conflict: JS 2 KB Budget vs. Verbatim Error Messages

**Rule: auto-adapt (unavoidable spec conflict)**
- **Found during:** Task 2
- **Issue:** The 04-UI-SPEC §Inline JS requires the script body to stay under 2 KB minified AND specifies verbatim error messages including "Must be between 0 and 1, and greater than low threshold." (57 chars) and "Must be between 0 and 1, and less than high threshold." (55 chars). These messages alone, combined with the PARAM_RULES structure and DOM manipulation logic, bring the minified script to ~2377 bytes — ~17% over the 2048-byte 2 KB cap.
- **Fix:** Maximally compressed the script: variable names shortened to 1–3 characters (PR, IC, EC, HI, LO, IN1, IN2), logic deduplication, single-line IIFE format. The budget overage is entirely attributable to the verbatim spec-required error messages. No logic or message text was cut.
- **Files modified:** `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs`
- **Commit:** 2ff97e0
- **Recommendation for Plan 04-01:** If the 2 KB limit is strictly enforced, the two threshold messages could be shortened (e.g., "Between 0–1; must exceed low threshold.") at the cost of deviating from the copywriting contract. Alternatively, the budget could be raised to 2.5 KB in the UI-SPEC.

### id Format Migration

**Rule: auto-fix (planned, no deviation)**
- All three Build*ParamGrid methods migrated from `p_{ei}_{di}_{key}` to `param-{ei}-{di}-{key}` as specified. Verified: `grep -n 'id="p_\|for="p_'` returns no matches.

---

## Known Stubs

None. All banner methods produce real HTML. The `hasHst=false` default on `BuildSuccessBanner` is intentional — Plan 04-01 Task 2 wires the real value at the Program.cs call site.

---

## Threat Flags

None beyond the plan's registered threats. `BuildValidationBanner` interpolates only a server-computed integer (satisfying T-04-06). No new endpoints or auth paths introduced.

---

## Verification

- `dotnet build Argus.Orchestrator/Argus.Orchestrator.csproj -c Debug` — PASS (0 warnings, 0 errors)
- `dotnet test ... --filter "FullyQualifiedName~EntityPickerPageTests"` — PASS (37/37)
- No `id="p_` or `for="p_` occurrences remain in the three Build*ParamGrid methods
- All 12 param inputs carry `aria-describedby="param-..."` and `aria-invalid="false"`
- All 12 param inputs have a sibling `argus-param-field__error-msg` span with matching `-err` id
- CSS contains: `.argus-param-field__error-msg`, `.argus-param-field--error .argus-param-field__input`, `.argus-banner--validation`, `.argus-warmup-note`
- `BuildValidationBanner` is public static, returns `argus-banner--validation` fragment
- `BuildSuccessBanner` signature is `(int count, bool hasHst = false)`
- Inline JS embeds `var PR=` validation rules directly in page body (no `src=` attribute)

---

## Dependency Note

`BuildSuccessBanner(int count, bool hasHst = false)` — the existing single-argument call site at `Program.cs` line 395 compiles unchanged (default `hasHst=false`). Plan 04-01 Task 2 upgrades this call site to pass the real HST detection result (`entities.Any(e => e.Detectors.Any(d => d.Name.Equals("hst", StringComparison.OrdinalIgnoreCase)))`).

## Self-Check: PASSED

- `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs` — FOUND
- `orchestrator/Argus.Orchestrator/wwwroot/css/argus.css` — FOUND
- `orchestrator/Argus.Orchestrator.Tests/EntityPickerPageTests.cs` — FOUND
- Commit 150bbac — FOUND
- Commit 2ff97e0 — FOUND
