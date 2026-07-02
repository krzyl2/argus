---
phase: 07-spa-scaffolding
verified: 2026-07-02T18:40:00Z
status: human_needed
score: 4/4 must-haves verified
behavior_unverified: 0
overrides_applied: 0
human_verification:
  - test: "Open the add-on through HA Supervisor's 'Open Web UI' button (never a direct port) against a real running Home Assistant Supervisor Ingress proxy."
    expected: "SPA loads under the real dynamic Ingress base path; sensor search returns results; assigning a detector (HST/MAD/STL) + Save succeeds; hot-reload takes effect without restarting the add-on."
    why_human: "No running HA Supervisor Ingress instance is available in this verification environment. The SPA is robust-by-construction (vite.config.ts base:'./' + hash routing in router.ts + apiGet/apiPost in client.ts throwing on any leading-slash path) and all of those code-level guarantees were independently re-verified against source in this pass, but the live proxy round-trip itself (X-Ingress-Path rewriting, real base-path prefix injection) has not been exercised end-to-end against Supervisor in this session, matching the phase's own <human_verification> block in 07-03-PLAN.md."
---

# Phase 7: SPA Scaffolding Verification Report

**Phase Goal:** Rebuild the v3.0 Ingress config UI on a Preact+Vite SPA, built at Docker build-time as static assets (no Node in runtime image), verified against real HA Supervisor Ingress, with ZERO loss of v3.0 capability.
**Verified:** 2026-07-02T18:40:00Z
**Status:** human_needed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth (Roadmap Success Criterion) | Status | Evidence |
|---|------|--------|----------|
| 1 | UI-01 — SPA built at Docker build-time (Preact+Vite), static assets only, NO Node.js in runtime image | ✓ VERIFIED | `argus/Dockerfile` is 3-stage: `node:20-alpine AS ui-build` (npm ci + vite build) → `mcr.microsoft.com/dotnet/sdk:8.0 AS dotnet-build` (`COPY --from=ui-build` before `dotnet publish`) → runtime stage (`FROM ${BUILD_FROM}`, dotnet-install.sh `--runtime aspnetcore` only, no Node/npm/SDK). Independently re-ran `docker build --target dotnet-build` in this session: build succeeded, `/app/publish/wwwroot/index.html`, `assets/index-*.js`, `css/argus.css` all present. `.github/workflows/build.yml` and `deploy/build-push.ps1` no longer run host-side `dotnet publish`; both assert `index.html` + `assets/*.js`, not `htmx.min.js`. `htmx.min.js` confirmed absent from tracked source (only a stale gitignored `orchestrator/publish/` artifact directory contains a leftover copy). |
| 2 | UI-02 — Opening add-on Web UI via HA "Open Web UI" (never direct port) loads + fully functions under dynamic Ingress base path | ⚠️ PRESENT_BEHAVIOR_UNVERIFIED (code-level guarantees verified; live-Ingress round-trip requires a human) | `vite.config.ts`: `base: './'`. `router.ts`: hand-rolled hash router, no `preact-router`/`preact-iso` dependency (grep confirmed absent from `package.json`), redirects empty hash to `#/sensors`. `client.ts`: `apiGet`/`apiPost` both throw `Error` when `path.startsWith('/')` — grepped all of `src/` and found zero leading-slash `fetch()` calls outside `client.ts`. `index.html` has no `<base href>` tag. `Program.cs`'s `X-Ingress-Path` middleware sets `PathBase` before `UseRouting`/`UseStaticFiles`. All of this is present and wired; the actual live round-trip through a real HA Supervisor Ingress proxy cannot be exercised in this environment — routed to human verification per the phase's own `<human_verification>` block. |
| 3 | UI-03 — Every /api/* endpoint enforces the same Ingress auth as v3 | ✓ VERIFIED | `Program.cs`: `GET /api/sensors` (line 244), `GET /api/detectors/defaults` (line 274), `POST /api/sensors/save` (line 291) each call `IsAuthorizedRequest(req.HttpContext)` as their first statement, returning `Results.StatusCode(403)` on failure. `IsAuthorizedRequest` body unchanged (RemoteIp-based: loopback or 172.30.32.2, dev-only bypass gated by `ARGUS_DEV_TRUST_ALL_REQUESTS`). `MapFallbackToFile("index.html")` is registered last (line 451), after all explicit `/api/*` routes — cannot intercept them. No server-rendered HTML endpoints remain (`GET /`, `GET /sensors` removed). |
| 4 | UI-04 — All v3 capabilities (sensor discovery/selection, per-entity detector assignment, hot-reload without restart) work identically through the SPA | ✓ VERIFIED | Save pipeline order preserved verbatim in `Program.cs`: `GlobExpander.Resolve` → `InputValidator.Validate` (before any write, line 355) → YAML `SerializerBuilder` root-dict → `ConfigWriter.WriteAsync` → lock file → `EntitiesConfigLoader.Load` → `liveCfg.Swap` (hot-reload trigger, line 428). `SaveRequest.cs` DTO (`Entities`/`Include`/`Exclude`) matches `orchestrator/ui/src/api/types.ts`'s `SaveRequest` interface field-for-field (camelCase JSON via default System.Text.Json policy). All 3 endpoints return JSON per the UI-SPEC contract (`/api/sensors`, `/api/detectors/defaults`, `/api/sensors/save`). 14 non-test Preact components reproduce `EntityPickerPage.cs` behavior (search, detector assignment, validation, save, banners). `InputValidator.cs`'s CR-01 fix (client/server validation parity) is present in source: `TryGetInt`/`TryGetDouble`-backed checks now hard-fail on missing/blank/non-numeric params instead of silently skipping; cross-field high>low check only evaluates once both fields individually parse and range-check. `dotnet test` 321/321 passing; `npx vitest run` 41/41 passing — both re-run independently in this verification pass, not taken from SUMMARY claims. |

**Score:** 4/4 truths verified from a presence/wiring standpoint (3 fully VERIFIED, 1 PRESENT_BEHAVIOR_UNVERIFIED pending a human-run live-Ingress check, which is explicitly called out as a human_verification item by the phase's own plan, not a gap).

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `argus/Dockerfile` | Multi-stage: node → dotnet-sdk → runtime (no Node/SDK) | ✓ VERIFIED | 3 stages confirmed; live `docker build --target dotnet-build` re-run in this session succeeded and produced `wwwroot/index.html` + `assets/*.js` in `/app/publish` |
| `.github/workflows/build.yml` | No host publish; SPA asset assertion | ✓ VERIFIED | "Publish orchestrator" step absent; asserts `index.html` + `assets/*.js`; `dotnet test` step retained |
| `deploy/build-push.ps1` | No host publish; SPA asset assertion | ✓ VERIFIED | Host publish block removed; `-SkipPublish` kept as documented no-op |
| `orchestrator/Argus.Orchestrator/Web/SaveRequest.cs` | Nested JSON DTO matching types.ts | ✓ VERIFIED | `SaveRequest{Entities,Include,Exclude}` / `SaveEntity{EntityId,Detectors}` / `SaveDetector{Name,Params}` — field-for-field match with `types.ts` |
| `orchestrator/Argus.Orchestrator/Program.cs` | JSON endpoints + static/fallback wiring; server-render removed | ✓ VERIFIED | `MapFallbackToFile` present, registered last; no `Results.Content(html)` remains |
| `orchestrator/ui/vite.config.ts` | `base:'./'`, outDir → wwwroot, preact plugin | ✓ VERIFIED | Exact match; outDir resolves correctly (confirmed via live build) |
| `orchestrator/ui/src/api/client.ts` | Relative-fetch wrapper throwing on leading slash | ✓ VERIFIED | Both `apiGet`/`apiPost` throw on leading-slash path; `apiPost` also now handles non-JSON empty-body error responses (WR-01 fix) |
| `orchestrator/ui/src/router.ts` | Hand-rolled hash router | ✓ VERIFIED | ~20 lines, `@preact/signals`-based, no router library dependency |
| `orchestrator/ui/src/validation/detectorParams.ts` | TS port of v3 validation rules | ✓ VERIFIED | Present, Vitest-tested (41/41 passing incl. this module) |
| Server-render files (`EntityPickerPage.cs`, `PlaceholderPage.cs`, `DetectorFieldParser.cs`, `EntityPickerPageTests.cs`) | Deleted | ✓ VERIFIED | Confirmed absent from filesystem; only comment references remain (historical parity-spec pointers, not code dependencies) |
| `orchestrator/Argus.Orchestrator/wwwroot/js/htmx.min.js` | Removed | ✓ VERIFIED | Absent from tracked source; only a stale gitignored `orchestrator/publish/` artifact directory retains a leftover copy from a prior local build (not shipped, not tracked) |

### Key Link Verification

| From | To | Via | Status | Details |
|------|-----|-----|--------|---------|
| `orchestrator/ui/src/state/sensors.ts` | `orchestrator/ui/src/api/client.ts` | `apiGet('api/sensors?q=')` / `apiPost('api/sensors/save', body)` | ✓ WIRED | Confirmed in source |
| `orchestrator/ui/src/components/SaveResultBanner.tsx` | `orchestrator/ui/src/api/types.ts` | branches on `{ ok, kind }` discriminant | ✓ WIRED | Confirmed — no string-sniffing |
| `Program.cs` | `Config/InputValidator.cs` | `InputValidator.Validate` called before any write | ✓ WIRED | Line 355, before entity-list build (line 367) and `WriteAsync` (line 412) |
| `Program.cs` | `Config/ConfigWriter.cs` | `writer.WriteAsync` + `liveCfg.Swap` | ✓ WIRED | Lines 412 and 428, in order |
| `Program.cs` | `Web/SaveRequest.cs` | `ReadFromJsonAsync<SaveRequest>` | ✓ WIRED | Line 298 |
| `argus/Dockerfile ui-build` | `argus/Dockerfile dotnet-build` | `COPY --from=ui-build .../wwwroot/` before `dotnet publish` | ✓ WIRED | Confirmed by live build re-run: order correct, SPA present at publish time |
| `argus/Dockerfile dotnet-build` | `argus/Dockerfile runtime` | `COPY --from=dotnet-build /app/publish/` | ✓ WIRED | Confirmed — no leftover `COPY orchestrator/publish/` line |

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| .NET test suite passes | `cd orchestrator && dotnet test Argus.Orchestrator.sln -c Release` | 321/321 passed, 0 failures | ✓ PASS |
| SPA test suite passes | `cd orchestrator/ui && npx vitest run` | 41/41 passed (6 test files) | ✓ PASS |
| Docker multi-stage build produces SPA in publish output | `docker build -f argus/Dockerfile --target dotnet-build` + inspect `/app/publish/wwwroot/` | `index.html`, `assets/index-*.js`, `css/argus.css` all present; build succeeded | ✓ PASS |
| No leading-slash fetch outside client.ts wrapper | `grep -rn "fetch(['\"]\/" src/` (excluding client.ts) | 0 matches | ✓ PASS |
| No `<base href>` tag | `grep "base href" index.html` | 0 matches | ✓ PASS |
| No router library dependency | `grep "preact-router\|preact-iso" package.json` | 0 matches | ✓ PASS |
| No `confirm()` dialog on Remove detector | `grep -rn "confirm(" src/components/` | 0 matches | ✓ PASS |
| No dangling references to deleted server-render classes | `grep -rn "new EntityPickerPage\|new PlaceholderPage\|new DetectorFieldParser"` | 0 matches (only comments reference the names) | ✓ PASS |

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| UI-01 | 07-03-PLAN.md | SPA built at Docker build-time, no Node in runtime | ✓ SATISFIED | Multi-stage Dockerfile verified by live re-build in this session |
| UI-02 | 07-01-PLAN.md | SPA functions under dynamic Ingress base path | ? NEEDS HUMAN | Code-level guarantees (base:'./', hash routing, relative-fetch enforcement) all verified present; live Ingress round-trip needs a human with a running HA Supervisor |
| UI-03 | 07-02-PLAN.md | /api/* endpoints enforce Ingress auth | ✓ SATISFIED | `IsAuthorizedRequest` called first in all 3 handlers; verbatim auth logic preserved |
| UI-04 | 07-01-PLAN.md, 07-02-PLAN.md | v3.0 capabilities preserved (discovery, assignment, hot-reload) | ✓ SATISFIED | Save/hot-reload pipeline order preserved; DTO parity confirmed; validation parity fix (CR-01) confirmed in source; both test suites green |

No orphaned requirements — all 4 IDs (UI-01 through UI-04) declared across the phase's 3 plans and match REQUIREMENTS.md's Phase 7 traceability row exactly.

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| — | — | No TBD/FIXME/XXX/TODO/HACK/PLACEHOLDER markers found in any phase-modified file | — | None |

### Code Review Findings (07-REVIEW.md / 07-REVIEW-FIX.md)

The phase went through one code-review cycle: 1 critical + 4 warnings + 3 info findings. All 5 in-scope (critical + warnings) findings were fixed and independently confirmed present in source during this verification pass:

- **CR-01** (critical — server-side `InputValidator` silently accepted missing/blank/non-numeric params): fix confirmed in `InputValidator.cs` — `TryGetInt`/`TryGetDouble`-backed checks now hard-fail on parse failure; cross-field high>low logic restructured to only fire once both fields individually validate. 321 tests pass including new theory-driven coverage across all 7 HST fields.
- **WR-01** (`apiPost` didn't check `res.ok` for empty-body responses): fix confirmed in `client.ts` — reads body as text first, throws a clear error on non-ok empty body instead of a confusing `JSON.parse` `SyntaxError`.
- **WR-02** (duplicated detector-defaults tables, no shared source of truth): resolved by cross-reference comments (accepted as sufficient per review-fix rationale — endpoint intentionally kept unused by the SPA, values manually confirmed to match).
- **WR-03** (`SensorSearchInput` debounce timer not cleared on unmount): fix confirmed — `useEffect` cleanup added, new test file present (`SensorSearchInput.test.tsx`).
- **WR-04** (`loadSensors` race condition on out-of-order responses): fix confirmed — monotonic sequence counter guards stale response application.

Info findings (IN-01 dead ternary in `main.tsx`, IN-02 `@params` naming note, IN-03 `aria-label` uses numeric index) were explicitly out of the fix scope (info-level, not required) and remain — they do not affect the phase goal or any must-have.

### Human Verification Required

### 1. Live HA Supervisor Ingress round-trip (UI-02)

**Test:** After the next add-on release ships this image, open the add-on through Home Assistant's "Open Web UI" button (never a direct port). Confirm: (a) the SPA loads and renders under the real dynamic Ingress base path, (b) sensor search returns live results, (c) assigning a detector (HST/MAD/STL), editing params, and clicking "Save configuration" succeeds, (d) the hot-reload cycle completes without an add-on restart (subsequent GET /api/sensors reflects the new config).

**Expected:** All four sub-checks pass with no 404s on `/assets/*.js`, no fetch errors from a mis-resolved base path, and a visible success banner after save.

**Why human:** This verification environment has no running Home Assistant Supervisor instance to exercise the real `X-Ingress-Path` header injection and base-path proxying end-to-end. The code-level guarantees that make this robust by construction — `vite.config.ts`'s `base: './'`, `router.ts`'s hash-only routing, and `client.ts`'s throw-on-leading-slash `apiGet`/`apiPost` — were all independently re-verified against source in this pass. This is exactly the human_verification item the phase's own `07-03-PLAN.md` and `07-03-SUMMARY.md` flag as not satisfiable by unit/Docker-level testing alone.

### Gaps Summary

No gaps. All 4 roadmap success criteria (UI-01 through UI-04) have code-level evidence of being met. UI-01, UI-03, and UI-04 are fully verified against the live codebase (not SUMMARY claims) — including two independently re-run test suites (321/321 .NET, 41/41 Vitest) and one independently re-run Docker build proving the SPA lands in `wwwroot` before `dotnet publish` and the runtime stage never sees Node/npm/SDK. UI-02's code-level construction is fully verified; only the live Supervisor-Ingress round-trip itself remains a human-run check, which the phase correctly scoped as human_verification rather than claiming false completion.

---

_Verified: 2026-07-02T18:40:00Z_
_Verifier: Claude (gsd-verifier)_
