---
phase: 10
slug: design-system-foundation
status: verified
threats_open: 0
asvs_level: 1
created: 2026-07-08
---

# Phase 10 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.
> Register authored at plan time (all 7 PLAN.md files carried a `<threat_model>` block);
> `threats_open: 0` with every threat dispositioned `accept` and documented — short-circuit
> verification applied (no new-threat scan required per secure-phase Step 3).

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| localStorage → DOM attribute | `argus-theme` value read at boot and written to `<html data-theme>` | Two-value UI theme string (`light`/`dark`), non-sensitive |
| user keystrokes → onChange props | Input/Search/Textarea forward raw string values to parent handlers | Free-text filter/pattern input |
| props → DOM text | All display/feedback components render caller-provided strings as text nodes only | Catalog copy, error strings, group names |
| toggle → localStorage + DOM | Theme toggle writes `argus-theme` and sets `<html data-theme>` | UI theme string |
| `location.hash` navigation | Sidebar sets hash to hard-coded internal routes only | Internal route (`#/sensors`, `#/groups`) |
| server result → banner text | Save/group results + area-suggestion copy render as text; delete triggers existing endpoint | Server-originated result strings |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-10-01 | Tampering | localStorage `argus-theme` | accept | Value only interpolated into a DOM attribute matched by CSS `[data-theme="dark"]`; arbitrary value falls back to light theme, no code/HTML/query path | closed |
| T-10-02 | Information Disclosure | theme preference | accept | Non-sensitive UI state, no PII | closed |
| T-10-SC (10-01) | Tampering | npm/pip/cargo installs | accept | No new packages; deps already pinned | closed |
| T-10-02-01 | Injection (XSS) | Input/Textarea/SearchInput | accept | Text rendered via Preact text-node binding only; no `dangerouslySetInnerHTML` | closed |
| T-10-02-SC | Tampering | npm installs | accept | No new dependencies | closed |
| T-10-03-01 | Injection (XSS) | Card/Badge/KpiTile/AttributionBar | accept | Children/labels rendered as auto-escaped Preact text nodes; only dynamic style is a clamped numeric width `Math.min(100,…)` | closed |
| T-10-03-SC | Tampering | npm installs | accept | No new dependencies | closed |
| T-10-04-01 | Injection (XSS) | Banner/AlgorithmCard | accept | Error reasons + catalog copy render as text nodes; catalog copy originates server-side (`DetectorCatalog.cs`), not free user input | closed |
| T-10-04-SC | Tampering | npm installs | accept | No new dependencies | closed |
| T-10-05-01 | Tampering | localStorage `argus-theme` | accept | Same disposition as T-10-01; tampered value yields light theme, no execution path | closed |
| T-10-05-02 | Tampering | `location.hash` navigation | accept | Sidebar sets only two hard-coded internal routes; no caller/URL-supplied destination, no open-redirect surface; parsing via existing hardened `router.ts` | closed |
| T-10-05-SC | Tampering | npm installs | accept | No router library added; reuse existing signal router | closed |
| T-10-06-01 | Tampering | detector-type / search values | accept | Implementation swap only; same values flow to same handlers; server-side validation (`DetectorDefaults.cs`, save endpoint) unchanged and remains authority | closed |
| T-10-06-SC | Tampering | npm installs | accept | No new dependencies | closed |
| T-10-07-01 | Injection (XSS) | banners | accept | Save reasons/error strings/group names render as Preact text nodes; retrofit changes wrapper only, not rendered data | closed |
| T-10-07-02 | Tampering | delete action | accept | Two-step arm/confirm calls existing `deleteGroup` path; server validation unchanged; UI confirm is a UX guard, not the security control | closed |
| T-10-07-SC | Tampering | npm installs | accept | No new dependencies | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-10-01 | T-10-01, T-10-05-01 | `argus-theme` localStorage key is a two-value UI string only ever consumed as a DOM attribute matched by a CSS selector; a tampered value degrades to light theme with no code/HTML/injection path. | Krzysztof Krawczyk | 2026-07-08 |
| AR-10-02 | T-10-02 | Theme preference is non-sensitive UI state with no PII. | Krzysztof Krawczyk | 2026-07-08 |
| AR-10-03 | T-10-05-02 | Sidebar navigation only sets `location.hash` to hard-coded internal routes; no open-redirect surface. | Krzysztof Krawczyk | 2026-07-08 |
| AR-10-04 | T-10-*-01 (XSS across all components) | Every component renders caller/server strings as Preact auto-escaped text nodes; no `dangerouslySetInnerHTML` anywhere in the phase. | Krzysztof Krawczyk | 2026-07-08 |
| AR-10-05 | T-10-06-01, T-10-07-02 | Retrofits are behavior-preserving wrapper swaps; existing server-side validation on save/delete endpoints is unchanged and remains the authority. | Krzysztof Krawczyk | 2026-07-08 |
| AR-10-06 | T-10-*-SC (supply chain) | Phase adds zero new npm dependencies; all imports already pinned in `orchestrator/ui/package.json`. | Krzysztof Krawczyk | 2026-07-08 |

*Accepted risks do not resurface in future audit runs.*

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-07-08 | 17 | 17 | 0 | Claude (gsd-secure-phase, short-circuit: register authored at plan time, all dispositions accepted) |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-07-08
