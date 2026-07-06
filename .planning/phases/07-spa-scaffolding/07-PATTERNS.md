# Phase 7: SPA Scaffolding - Pattern Map

**Mapped:** 2026-07-02
**Files analyzed:** 20 (new + modified, .NET/infra + SPA-side)
**Analogs found:** 8 in-repo / 12 "new stack — see RESEARCH.md"

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `orchestrator/ui/package.json`, `vite.config.ts`, `tsconfig.json`, `vitest.config.ts`, `index.html` | config | build | none in-repo | new stack — see RESEARCH.md Standard Stack + Code Examples |
| `orchestrator/ui/src/main.tsx`, `router.ts` | component/route | event-driven (hashchange) | none in-repo | new stack — see RESEARCH.md Pattern 2 |
| `orchestrator/ui/src/api/client.ts`, `types.ts` | service | request-response | none in-repo | new stack — see RESEARCH.md Pattern 3 |
| `orchestrator/ui/src/state/sensors.ts` | store | CRUD (client-side) | none in-repo | new stack — see RESEARCH.md (`@preact/signals`) |
| `orchestrator/ui/src/components/*.tsx` (13 components) | component | request-response / transform | none in-repo (Preact greenfield) | new stack — see UI-SPEC Component Inventory (1:1 mapping to `EntityPickerPage.cs` methods) for exact fields/classes/copy |
| `orchestrator/ui/src/validation/detectorParams.ts` | utility | transform | none in-repo (JS/TS side); logic ported from C# | new stack, but **logic source** is `EntityPickerPage.cs` `_validationScript` (see UI-SPEC validation table) — treat as a straight port, not new design |
| `orchestrator/ui/src/**/*.test.ts(x)` | test | — | none in-repo | new stack — see RESEARCH.md (Vitest + @testing-library/preact) |
| `orchestrator/Argus.Orchestrator/Program.cs` (MODIFIED: `/api/sensors`, `/api/sensors/save`, new `/api/detectors/defaults`, static file + fallback wiring) | route/controller (minimal API) | request-response (JSON) | **itself** (existing endpoints in same file) | exact — convert in place, same file |
| `orchestrator/Argus.Orchestrator/Web/DetectorFieldParser.cs` (MODIFY/REMOVE) | utility | transform | itself | exact — discretion item, likely deleted per CONTEXT.md |
| `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs` (REMOVE after parity) | controller/view-builder | request-response (HTML) | itself | exact — deletion target, its methods are the parity spec |
| `orchestrator/Argus.Orchestrator/Web/PlaceholderPage.cs` (REMOVE) | controller/view-builder | request-response (HTML) | itself | exact — deletion target |
| `orchestrator/Argus.Orchestrator/wwwroot/js/htmx.min.js` (REMOVE) | static asset | file-I/O | — | n/a — deletion target |
| `argus/Dockerfile` (MODIFIED: add Node stage + dotnet-publish stage) | config (build) | batch | itself | exact — modify in place |
| `.github/workflows/build.yml` (MODIFIED: remove host publish step, update asset-assertion) | config (CI) | batch | itself | exact — modify in place |
| `deploy/build-push.ps1` (MODIFIED: remove host publish block, update asset assertion) | config (CI) | batch | itself | exact — modify in place |
| `orchestrator/Argus.Orchestrator.Tests/*EndpointTests.cs` (new JSON-shape tests, alongside/replacing HTML-fragment assertions) | test | request-response | `DetectorEntryEndpointTests.cs`, `EntityPickerPageTests.cs` (existing) | exact — same test project, same xUnit conventions, assertions change from HTML string-contains to JSON property checks |

## Pattern Assignments

### `orchestrator/Argus.Orchestrator/Program.cs` (route, request-response) — JSON endpoint conversion

**Analog:** itself, lines 253–297 (existing `/sensors`, `/api/sensors`, `/api/detectors/new-entry`) and 301–441 (`/api/sensors/save`)

**Auth guard — preserve verbatim, reuse for every /api/* route** (lines 231–245):
```csharp
bool IsAuthorizedRequest(HttpContext ctx)
{
    if (devTrustAllRequests) return true;
    var remote = ctx.Connection.RemoteIpAddress;
    if (remote is null) return false;
    if (System.Net.IPAddress.IsLoopback(remote)) return true;
    if (remote.Equals(System.Net.IPAddress.Parse("172.30.32.2"))) return true;
    return false;
}
```
Every converted/new endpoint must open with `if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);` — do not touch this function.

**Existing GET endpoint to convert** (lines 266–277, `/api/sensors`):
```csharp
app.MapGet("/api/sensors", (HttpRequest req, IHaSensorRegistry registry, ILiveEntitiesConfig liveCfg) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);
    var q = req.Query["q"].FirstOrDefault() ?? "";
    return Results.Content(
        EntityPickerPage.BuildListFragment(registry, liveCfg.Get(), q),
        "text/html");
});
```
Convert to `Results.Json(...)` per UI-SPEC contract (`{ entries: [{ entityId, friendlyName, currentValue, unitOfMeasurement, isTracked }] }`), keeping the same DI params, same query-string read (`req.Query["q"]`), same `liveCfg.Get()` freshness pattern (CFG-04 — never capture a stale `EntitiesConfig` reference).

**Existing POST endpoint to convert** (lines 301–441, `/api/sensors/save`) — keep the structural skeleton (try/catch, `ConfigWriter.WriteAsync`, lock-file write, `EntitiesConfigLoader.Load` + `liveCfg.Swap`, logging with `LogEvents.*`), but:
- Replace `await req.ReadFormAsync(ct)` + `DetectorFieldParser.Parse(...)` with `await req.ReadFromJsonAsync<SaveRequest>(ct)` (new DTO, natural nested shape per RESEARCH Open Question 2: `entities: [{ entityId, detectors: [{ name, params }] }]`) — this removes the indexed-form parsing entirely.
- Replace `Results.Content(EntityPickerPage.BuildSuccessBanner(...), "text/html")` / `BuildValidationBanner` / `BuildErrorBanner` calls with `Results.Json(new { ok = true, count, hasHst })` / `Results.Json(new { ok = false, kind = "validation", errorCount })` / `Results.Json(new { ok = false, kind = "error", reason })` per UI-SPEC's `kind`-discriminated contract.
- Preserve verbatim: `GlobExpander.Resolve` call, `InputValidator.Validate` gate (validate BEFORE any write — this ordering is load-bearing, do not reorder), atomic `ConfigWriter.WriteAsync` + lock-file write, `liveCfg.Swap(newConfig)` hot-reload trigger, and the exception-to-generic-reason mapping (`ex is IOException ? "disk error" : "unexpected error"` — never leak raw exception text, T-02-11).

**New endpoint** `/api/detectors/defaults?name=hst|mad|stl` replaces `/api/detectors/new-entry` (lines 283–297) — same auth guard, same query-param read pattern, return `Results.Json(new { name, params })` instead of `EntityPickerPage.BuildDetectorEntry(...)` HTML fragment. Default values table lives in UI-SPEC (HST/MAD/STL params) — do not invent new defaults.

**Static file + SPA fallback wiring** — add after existing middleware, no reordering (per RESEARCH: `MapFallbackToFile` never intercepts `/api/*` or real static files):
```csharp
app.UseStaticFiles();      // already present at line 207 — unchanged position
// ... existing MapGet/MapPost endpoints (converted above) ...
app.MapFallbackToFile("index.html");
app.Run();
```
`GET /` (line 248, `Results.Redirect("sensors")`) and `GET /sensors` (lines 253–264, full HTML page) are removed once the SPA shell + hash router handle root/`#/sensors` client-side — the fallback route now serves `index.html` for both.

---

### `orchestrator/Argus.Orchestrator/Web/DetectorFieldParser.cs` — likely removal

**Analog:** itself (whole file, 109 lines)

Regex-based `detectors[{ei}][{di}][name]`/`[params][{key}]` parsing (lines 22–95) is obsolete once the SPA POSTs a natural JSON body (`entities: [{ entityId, detectors: [...] }]`) deserialized directly via `ReadFromJsonAsync`. Per CONTEXT.md "Claude's Discretion," the planner may fully delete this file or keep the `Dictionary<int, List<DetectorConfig>>`-shaped internal type if useful — but the regex/form-parsing logic itself has no successor pattern; do not port it into TypeScript.

---

### `argus/Dockerfile` (config, batch) — multi-stage Node + dotnet-publish

**Analog:** itself, lines 1–53 (current single-stage layout with `COPY orchestrator/publish/ /opt/argus/orchestrator/` at line 53)

Current structure: single `FROM ${BUILD_FROM}` stage; orchestrator is published by CI/host **before** `docker build` runs (line 50–53 comment confirms this explicitly). This is the exact assumption that breaks once a Node build stage is introduced (RESEARCH Pitfall 1).

**Required change** — insert two new build stages before the existing runtime stage, per RESEARCH Pattern 1:
```dockerfile
# ── Stage 1: build the SPA (Node, discarded) ──────────────────────────────────
FROM node:20-alpine AS ui-build
WORKDIR /src/ui
COPY orchestrator/ui/package.json orchestrator/ui/package-lock.json ./
RUN npm ci
COPY orchestrator/ui/ ./
RUN npm run build

# ── Stage 2: publish the .NET orchestrator (SDK, discarded) ──────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS dotnet-build
WORKDIR /src
COPY orchestrator/ ./orchestrator/
COPY proto/ ./proto/
COPY --from=ui-build /src/ui/dist/ ./orchestrator/Argus.Orchestrator/wwwroot/
RUN dotnet publish orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj \
    -c Release --self-contained false -o /app/publish

# ── Stage 3: runtime (existing ARG BUILD_FROM stage, unchanged except final COPY) ──
ARG BUILD_FROM=ghcr.io/home-assistant/base-debian:bookworm
FROM ${BUILD_FROM}
# ...existing dotnet-install.sh / python setup (lines 4-48), UNCHANGED...
COPY --from=dotnet-build /app/publish/ /opt/argus/orchestrator/
# ...existing COPY detector/, COPY argus/rootfs/, LABEL block (lines 55-86), UNCHANGED...
```
Line 53's current `COPY orchestrator/publish/ /opt/argus/orchestrator/` must be **deleted** and replaced with `COPY --from=dotnet-build /app/publish/ /opt/argus/orchestrator/` — keeping both would silently prefer whichever COPY runs last with stale/host-published content (RESEARCH Anti-Pattern, "two divergent publish paths").

---

### `.github/workflows/build.yml` — remove host publish, fix asset assertion

**Analog:** itself, lines 24–48

Current steps to **remove** (now redundant — Dockerfile does its own publish):
```yaml
      - name: Set up .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "8.0.x"

      - name: Publish orchestrator
        run: >-
          dotnet publish orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj
          -c Release --self-contained false -o orchestrator/publish/
```
Current asset-assertion step to **rewrite** (lines 34–45 — currently checks for the htmx-era paths against `orchestrator/publish/`, which no longer exists once publish moves into Docker):
```yaml
      - name: Assert wwwroot assets present in publish output
        shell: bash
        run: |
          test -f orchestrator/publish/wwwroot/js/htmx.min.js || {
            echo "FAIL: htmx.min.js not in publish output"
            exit 1
          }
          test -f orchestrator/publish/wwwroot/css/argus.css || {
            echo "FAIL: argus.css not in publish output"
            exit 1
          }
```
Replace with either (a) a check performed inside the Dockerfile build via a temporary `docker run` extraction, or (b) an equivalent assertion against a locally-built `orchestrator/ui/dist/` (`npm run build` step added to the workflow purely for CI-side verification) checking for `index.html` and at least one `assets/*.js` file — not `htmx.min.js`/hardcoded `argus.css` path. `dotnet test orchestrator/ -c Release` (line 48) stays unchanged — it still runs against source, independent of publish/Docker.

---

### `deploy/build-push.ps1` — remove host publish block, fix asset assertion

**Analog:** itself, lines 51–61 (`-SkipPublish` host-publish block)

Current block to **remove** (redundant once Dockerfile publishes internally):
```powershell
if (-not $SkipPublish) {
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    dotnet publish $csproj -c Release --self-contained false -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    foreach ($f in @("$publishDir/wwwroot/js/htmx.min.js", "$publishDir/wwwroot/css/argus.css")) {
        if (-not (Test-Path $f)) { throw "missing publish asset: $f (wwwroot not in publish output)" }
    }
    Write-Host "publish OK (wwwroot assets present)"
}
```
The `-SkipPublish` switch parameter and `$publishDir`/`$csproj` variables (lines 22-24, 40-41) become dead once this block is removed — planner should decide whether to delete the switch entirely or repurpose it as a no-op for backward CLI compatibility. `docker buildx build ... --push .` (lines 67–76) is unchanged — it already builds directly from `argus/Dockerfile` with no dependency on a pre-existing `orchestrator/publish/` directory once the Dockerfile self-publishes.

---

### `orchestrator/Argus.Orchestrator.Tests/*` — JSON-endpoint parity tests

**Analog:** `DetectorEntryEndpointTests.cs` (whole file, 80+ lines shown) and `EntityPickerPageTests.cs` (whole file)

**Existing xUnit conventions to reuse** (from `DetectorEntryEndpointTests.cs` lines 1–29):
```csharp
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Web;
using Xunit;

namespace Argus.Orchestrator.Tests;

public class DetectorEntryEndpointTests
{
    [Fact]
    public void BuildDetectorEntry_DefaultHstEntry_ContainsArgusDetectorEntryClass()
    {
        var detector = new DetectorConfig { Name = "hst", Params = [] };
        var html = EntityPickerPage.BuildDetectorEntry(0, 0, detector);
        Assert.Contains("argus-detector-entry", html);
    }
}
```
For the JSON conversion, the parity checklist is: every `Assert.Contains("...", html)` string-match test on HTML fragments becomes a `JsonSerializer.Deserialize<T>(json)` + property assertion (e.g. HST defaults test at lines 63–78 checking `value="250"`, `value="25"`, etc. becomes `Assert.Equal(250, result.Params["window"])`). `EntityPickerPageTests.cs`'s `FakeRegistry : IHaSensorRegistry` test double (lines 19–33) is directly reusable for endpoint-level tests exercising the converted `/api/sensors` handler — same DI fake, same `MakeEntry`/`MakeHealth` helper pattern (lines 35–45). Existing test file naming convention (`<Feature>Tests.cs`, one class per behavior area) should be followed for any new `SensorsEndpointJsonTests.cs` / `SaveEndpointJsonTests.cs`.

---

### SPA-side files (`orchestrator/ui/**`) — no in-repo analog (new stack)

Preact + Vite + TypeScript is greenfield in this repository — there is no existing JS/TS frontend to pattern-match against. For all files under `orchestrator/ui/`, use `07-RESEARCH.md` as the reference instead of an in-repo analog:

| File | Reference in RESEARCH.md |
|---|---|
| `vite.config.ts` | "Code Examples → Vite config (base + outDir + Preact plugin)" |
| `vitest.config.ts` | "Code Examples → Vitest config (jsdom environment)" |
| `src/router.ts`, `src/main.tsx` | "Architecture Patterns → Pattern 2: Hand-rolled hash router" |
| `src/api/client.ts` | "Architecture Patterns → Pattern 3: Relative-fetch API client" |
| `src/state/sensors.ts` | "Standard Stack → `@preact/signals`" + "Recommended Project Structure" |
| `src/components/*.tsx` | `07-UI-SPEC.md` → "Component Inventory" table (1:1 mapping to `EntityPickerPage.cs` methods, exact CSS classes, exact copy) |
| `src/validation/detectorParams.ts` | `07-UI-SPEC.md` → "Client-side field validation rules" table (ported from `EntityPickerPage.cs` `_validationScript`) — this is a straight logic port, not new design; treat the UI-SPEC table as the literal spec |
| `src/components/*.test.tsx` | RESEARCH.md "Don't Hand-Roll" (Vitest + @testing-library/preact is the standard, no custom harness) |

## Shared Patterns

### Ingress Auth Guard
**Source:** `orchestrator/Argus.Orchestrator/Program.cs` lines 231–245 (`IsAuthorizedRequest`)
**Apply to:** every converted/new `/api/*` endpoint (`/api/sensors`, `/api/sensors/save`, `/api/detectors/defaults`) — call unchanged, first line of every handler body.

### JSON Response Discriminant (`ok`/`kind`)
**Source:** `07-UI-SPEC.md` API Contract section
**Apply to:** `/api/sensors/save` — `{ ok: true, count, hasHst }` on success; `{ ok: false, kind: "validation"|"error", ... }` on failure. SPA's `SaveResultBanner` component branches on `ok`/`kind`, not string-sniffing (per RESEARCH Pattern 3 note).

### Config Hot-Reload (unchanged plumbing)
**Source:** `orchestrator/Argus.Orchestrator/Program.cs` lines 401–419 (`ConfigWriter.WriteAsync` → lock file → `EntitiesConfigLoader.Load` → `liveCfg.Swap`)
**Apply to:** the converted `/api/sensors/save` handler must trigger this exact same sequence — JSON body parsing changes, the write/swap pipeline does not.

### Relative-Fetch Enforcement
**Source:** `07-RESEARCH.md` Pattern 3 + Pitfall 2
**Apply to:** every SPA `fetch()` call — no leading slash, ever; enforced via the `apiGet`/`apiPost` wrapper functions, not ad hoc per-component `fetch()`.

### Docker Multi-Stage Build Ordering
**Source:** `07-RESEARCH.md` Pattern 1 + Pitfall 1
**Apply to:** `argus/Dockerfile`, `.github/workflows/build.yml`, `deploy/build-push.ps1` — all three must change together in the same commit/PR (Node stage → dotnet-publish stage → runtime stage; CI/local scripts drop their host-side `dotnet publish` step).

## No Analog Found

| File | Role | Data Flow | Reason |
|---|---|---|---|
| All `orchestrator/ui/src/**` files | component/service/store | various | Preact/Vite/TypeScript stack is entirely new to this repo; no prior JS framework code exists. Use RESEARCH.md Code Examples + UI-SPEC Component Inventory as the reference of record instead of an in-repo analog. |

## Metadata

**Analog search scope:** `orchestrator/Argus.Orchestrator/` (Program.cs, Web/), `orchestrator/Argus.Orchestrator.Tests/`, `argus/Dockerfile`, `.github/workflows/build.yml`, `deploy/build-push.ps1`
**Files scanned:** Program.cs (443 lines), EntityPickerPage.cs (594 lines, not fully re-read — parity spec already captured in UI-SPEC), DetectorFieldParser.cs (109 lines), Dockerfile (86 lines), build.yml (158 lines), build-push.ps1 (81 lines), DetectorEntryEndpointTests.cs (80+ lines), EntityPickerPageTests.cs (60 lines)
**Pattern extraction date:** 2026-07-02
