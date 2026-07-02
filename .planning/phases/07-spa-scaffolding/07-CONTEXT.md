# Phase 7: SPA Scaffolding - Context

**Gathered:** 2026-07-02
**Status:** Ready for planning

<domain>
## Phase Boundary

Rebuild the v3.0 server-rendered (htmx) Ingress configuration UI on a Preact + Vite SPA foundation, built at Docker build-time and shipped as static assets (no Node in the runtime image), verified against real HA Supervisor Ingress — with ZERO loss of existing v3.0 capability (sensor discovery/selection, per-entity detector assignment, hot-reload). This is a scaffolding/migration phase: it replaces the delivery mechanism, not the feature set. NO new feature UI (group config, algorithm chooser, friendly-name search are Phase 8). Function-first — replicate v3.0 behavior, do not redesign visuals.

Covers requirements: UI-01 (SPA built at Docker build-time, static assets, no runtime Node), UI-02 (functions under Ingress dynamic base path), UI-03 (all /api/* enforce Ingress auth), UI-04 (v3.0 capabilities intact through SPA).
</domain>

<decisions>
## Implementation Decisions

### Build & Docker Integration (UI-01)
- SPA source lives in a new `orchestrator/ui/` Vite project.
- `argus/Dockerfile` gains a multi-stage `node:20-alpine` build stage: `npm ci && vite build` → copy `dist/` into the orchestrator `wwwroot/`. Runtime image contains NO Node — static assets only.
- Package manager: `npm` (committed `package-lock.json`).
- Vite `build.outDir` → orchestrator `wwwroot/`, `emptyOutDir: true` to clear the old htmx assets.

### Ingress Base-Path + Routing (UI-02)
- Vite `base: './'` — relative asset paths work under any dynamic Ingress prefix.
- Hash routing (`#/sensors`) — immune to the Ingress prefix, no server-side rewrite needed.
- Ingress path discovery: SPA uses relative fetch / `document.baseURI`; NO hardcoded absolute paths.
- Verification: a live-HA "Open Web UI" check (never a direct port) is captured as a human_verification item (the Ingress base-path behavior cannot be fully unit-tested).

### API Layer + Auth + Removal (UI-03/04)
- Convert `/api/sensors`, `/api/sensors/save`, `/api/detectors/new-entry` to return clean JSON (v3.0 returns HTML fragments for htmx in places); the SPA consumes JSON.
- Serve the SPA via `UseStaticFiles` + `MapFallbackToFile("index.html")` from `wwwroot`; `/` serves the SPA shell.
- The existing Ingress auth middleware (Program.cs) is unchanged and continues to cover `/api/*` and static assets — zero auth regression.
- After capability parity is confirmed, remove the v3.0 server-render code: `Web/EntityPickerPage.cs`, `PlaceholderPage.cs`, `Web/DetectorFieldParser.cs` (if only server-render), and `wwwroot/js/htmx.min.js`.

### Framework Stack (UI-01/04)
- TypeScript (typed API contracts).
- Preact hooks + `@preact/signals` for shared state (lightweight).
- Styling: carry the existing `wwwroot/css/argus.css` forward as a global stylesheet — preserve the v3.0 look (function-first, no redesign).
- Frontend tests: Vitest + @testing-library/preact (component/unit) verifying capability parity.
</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets
- `orchestrator/Argus.Orchestrator/Web/EntityPickerPage.cs` — v3.0 server-rendered entity picker (to be replaced by SPA; its logic/behavior is the parity spec). `BuildDetectorEntry` is public static — its output shape informs the JSON contract.
- `orchestrator/Argus.Orchestrator/Web/DetectorFieldParser.cs` — internal static param parser (accepts IEnumerable<KVP>); the SPA-side form maps to the same detector param shape.
- `orchestrator/Argus.Orchestrator/wwwroot/css/argus.css` — existing stylesheet to carry forward.
- `orchestrator/Argus.Orchestrator/Program.cs` — Kestrel bind 0.0.0.0:8099, Ingress auth middleware (X-Ingress-Path OR RemoteIp 172.30.32.2/loopback), PathBase + `<base href>` handling, static file serving. The SPA-serving (UseStaticFiles/MapFallbackToFile) and JSON endpoints wire in here.
- Existing endpoints: `MapGet("/")`, `MapGet("/sensors")`, `MapGet("/api/sensors")`, `MapPost("/api/sensors/save")`, `MapGet("/api/detectors/new-entry")`.
- `argus/Dockerfile` — add-on image (base-debian:bookworm + dotnet-install.sh); the Vite build stage prepends here.

### Established Patterns
- Ingress auth (interim v3): accept if `X-Ingress-Path` header present OR RemoteIpAddress is 172.30.32.2/loopback (T-02-09). Applies to the SPA + all /api/*.
- Dual PathBase + `<base href>` defense for Supervisor-strips-vs-not behavior — the SPA's relative `base: './'` + hash routing supersedes the `<base href>` approach but the PathBase middleware stays for /api/*.
- Config source of truth: `/data/entities.yaml` via `ILiveEntitiesConfig`; hot-reload via `Interlocked.Exchange` swap + `ConfigChanged`. The SPA save path must trigger the same reload (no restart).
- Tests: `EntityPickerPageTests.cs`, `DetectorEntryEndpointTests.cs` — the behaviors these assert are the parity checklist for UI-04.

### Integration Points
- Program.cs — add UseStaticFiles + MapFallbackToFile; convert/confirm /api endpoints return JSON; keep Ingress auth middleware ahead of them.
- `argus/Dockerfile` — multi-stage node build → wwwroot copy.
- `orchestrator/ui/` — new Vite project (package.json, vite.config, src/, tsconfig).
- Hot-reload: SPA POST /api/sensors/save → existing ConfigWriter + LiveEntitiesConfig swap path (UI-04 hot-reload parity).
</code_context>

<specifics>
## Specific Ideas

- Zero capability loss is the hard bar (UI-04): every behavior asserted by EntityPickerPageTests/DetectorEntryEndpointTests must work through the SPA — sensor discovery, per-entity detector assignment, save, hot-reload without restart.
- The Ingress base-path behavior (UI-02) is the highest-risk item and only truly verifiable live via "Open Web UI" — carry it as a human_verification item, and make the SPA robust by construction (relative base + hash routing).
- No new feature UI in this phase — resist adding group config / chooser / search (Phase 8). Scaffolding only.
- Runtime image must have NO Node (UI-01) — verify the final image contains only static assets, build happens in a discarded stage.
</specifics>

<deferred>
## Deferred Ideas

- Group config authoring UI, algorithm chooser (presets + Advanced + "best for"), friendly-name search, area-scoped suggestions, per-feature attribution display — all Phase 8 (ALGO-*, SRCH-*, GRP-09).
- Any visual redesign / UX polish beyond preserving the v3.0 look — out of scope (function-first migration).
- Completing the full Supervisor `validate_session` auth (interim auth from v3 remains) — not expanded in this phase unless a regression forces it.
</deferred>
