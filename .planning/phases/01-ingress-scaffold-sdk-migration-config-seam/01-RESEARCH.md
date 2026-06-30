# Phase 1: Ingress Scaffold + SDK Migration + Config Seam — Research

**Researched:** 2026-06-30
**Domain:** ASP.NET Core Minimal API + Kestrel co-hosted in an existing .NET 8 Worker process, behind Home Assistant Supervisor Ingress
**Confidence:** HIGH

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **SDK migration:** `Microsoft.NET.Sdk.Worker` → `Microsoft.NET.Sdk.Web`; `Host.CreateApplicationBuilder` → `WebApplication.CreateBuilder`. All existing `AddHostedService`/`AddSingleton` registrations remain identical under `WebApplication` — no service-registration changes.
- **Co-host in orchestrator process:** Kestrel + Minimal API inside the existing process; no second s6 service. UI reads the same singletons as the workers.
- **Kestrel bind:** `0.0.0.0:8099` (not loopback) — Supervisor connects from `172.30.32.2`. Verify with `ss -tlnp | grep 8099` inside the container.
- **UI tech:** server-rendered HTML + htmx 2.0.10 (~14 KB, BSD 0-Clause), committed to `wwwroot/`. No SPA, no Node.js build step, no CDN — air-gapped safe.
- **PathBase strategy (live-test item):** set `context.Request.PathBase` per-request from `X-Ingress-Path` AND emit `<base href="{ingressPath}/">` in the HTML head. Open the UI exclusively via "Open Web UI" in HA (never the direct port); confirm all assets + any `/api` calls return 200; record behavior and close the STACK-vs-PITFALLS conflict.
- **Docker base image:** `mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled` (replaces `runtime:8.0-jammy-chiseled`; ~10 MB larger; same distroless base).
- **config.yaml:** add `ingress: true`, `ingress_port: 8099`, `panel_icon`/`panel_title`. No `ports:` entry — Ingress-only; exposing the port would bypass HA auth.
- **Config source of truth:** `/data/entities.yaml` unchanged — read/write via YamlDotNet (already referenced). No new config format.

### Critical Pre-conditions (must land this phase)
- `EntitiesConfigLoader.Validate()` must change from `throw` to `LogWarning` on empty entities — otherwise first-boot with empty `options.json` crashes the orchestrator before the UI can load.
- Atomic config write (temp-then-rename + `SemaphoreSlim(1)`) in place from the start — no partial reads possible during a concurrent FileSystemWatcher event.

### Deferred Ideas (OUT OF SCOPE)
- Supervisor `validate_session` / `IngressAuthMiddleware` — deferred to Phase 4.
- Entity selection UI, detector-assignment UI, config read/write/reload — Phases 2–3.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| UI-01 | The add-on exposes an Ingress endpoint ("Open Web UI") served by the orchestrator (ASP.NET minimal API behind `ingress: true` / `ingress_port`), authenticated by HA Ingress, with no separately exposed port. | SDK migration, Kestrel bind to 0.0.0.0:8099, config.yaml ingress keys, PathBase middleware, placeholder page, static file serving. |
| CFG-01 | A single configuration source of truth under `/data` is read by both the UI and the orchestrator's `EntitiesConfigLoader`. | Atomic write path (temp-rename + SemaphoreSlim), EntitiesConfigLoader empty-entities fix, config seam. |
</phase_requirements>

---

## Summary

Phase 1 converts the Argus orchestrator from a headless Worker Service into a co-hosted Web + Worker process, wires it into the HA Supervisor Ingress, and establishes the infrastructure-level config write path — all without breaking any v2.0 BackgroundService functionality.

The migration is a four-part operation: (1) swap the project SDK and host builder in one csproj line + one Program.cs refactor; (2) configure Kestrel to bind `0.0.0.0:8099` programmatically so the Supervisor can reach it; (3) add the X-Ingress-Path middleware + placeholder page + static file serving; (4) harden the config path (empty-entities warning, atomic write with SemaphoreSlim). The add-on Dockerfile gets a base image swap from `aspnet:8.0-jammy-chiseled` replacing `runtime:8.0-jammy-chiseled` (the add-on itself uses `base-debian:bookworm` and installs .NET via `dotnet-install.sh` — see "Docker Base Image Clarification" below).

The most important open item is the X-Ingress-Path / PathBase behavior — prior research produced conflicting conclusions (STACK.md: "Supervisor strips prefix, no PathBase needed" vs. PITFALLS.md: "set PathBase from header"). The safe dual implementation (set PathBase AND emit `<base>` tag) satisfies both possible Supervisor behaviors and must be validated on a live HA instance. Both mechanisms cost nothing and have no downside.

**Primary recommendation:** Use `builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Any, 8099))` to set the bind address in code (not via env var), call `app.UseRouting()` explicitly after the X-Ingress-Path middleware, and serve the placeholder page from a single `app.MapGet("/", ...)` handler that reads `X-Ingress-Path` and emits the `<base>` tag.

---

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Ingress HTTP endpoint | Frontend Server (Kestrel, same process) | — | Minimal API in the orchestrator process — no second process, shares DI singletons |
| Static file serving (htmx.min.js, argus.css) | Frontend Server (Kestrel UseStaticFiles) | — | wwwroot/ files served by ASP.NET static files middleware; CDN excluded (air-gapped) |
| PathBase / Ingress proxy header handling | Frontend Server (ASP.NET middleware) | — | `X-Ingress-Path` is a server-to-server header; never reaches browser |
| Placeholder page HTML generation | Frontend Server (Minimal API handler) | — | Server-rendered `Results.Content(...)`, reads header in handler |
| Background workers (HA, MQTT, Health, Batch) | API / Backend (BackgroundService) | — | Registered via `AddHostedService`; continue running alongside Kestrel unchanged |
| EntitiesConfig loading | API / Backend (EntitiesConfigLoader) | — | Startup-time YAML read; must not crash on empty entities |
| Atomic config write seam | API / Backend (ConfigWriter service) | — | temp-rename pattern serialized by SemaphoreSlim; shared by UI (Phase 2+) and any future reload |
| config.yaml ingress declaration | CDN / Static (add-on manifest) | — | Supervisor reads this at add-on install/restart; Kestrel port must match `ingress_port` |
| Docker base image | CDN / Static (image layer) | — | `aspnet:8.0-jammy-chiseled` for standalone deploy image; add-on image uses `base-debian:bookworm` + dotnet-install.sh |

---

## Standard Stack

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `Microsoft.NET.Sdk.Web` (project SDK) | .NET 8 (n/a) | Replaces `Microsoft.NET.Sdk.Worker`; enables `WebApplication.CreateBuilder`, Kestrel, static files, Minimal API routing | Zero new NuGet packages; `WebApplication` is a superset of `IHost`; all existing `AddHostedService` calls work unchanged [VERIFIED: Context7 /dotnet/aspnetcore.docs] |
| ASP.NET Core Minimal API | .NET 8 (framework-included) | `MapGet` handlers for the placeholder page and future config endpoints | No controller overhead; single root endpoint in Phase 1; same DI container as workers [VERIFIED: Context7 /dotnet/aspnetcore.docs] |
| ASP.NET Core Static Files middleware | .NET 8 (framework-included) | Serves `wwwroot/` — htmx.min.js + argus.css | Single `app.UseStaticFiles()` call; no NuGet package [VERIFIED: Context7 /dotnet/aspnetcore.docs] |
| Kestrel HTTP server | .NET 8 (framework-included) | HTTP listener on `0.0.0.0:8099` | Included in Web SDK; no nginx sidecar; Supervisor can reach from `172.30.32.2` |
| YamlDotNet | 16.3.0 (already in csproj) | Serialize/deserialize `/data/entities.yaml` for the atomic write seam | Already referenced; `UnderscoredNamingConvention` already configured in `EntitiesConfigLoader` [VERIFIED: codebase grep] |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| htmx | 2.0.10 | Browser-side progressive enhancement; partial HTML swaps | Committed as `wwwroot/js/htmx.min.js`; no npm; all later phases add htmx-annotated partials on top of this file [CITED: htmx.org npm] |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Programmatic `ConfigureKestrel` | `ASPNETCORE_URLS` env var | Env var is easier to accidentally override in s6; programmatic is self-documenting and immune to env pollution. Use programmatic. |
| `app.UseStaticFiles()` default | `ManifestEmbeddedFileProvider` | Embedded resources add build-manifest complexity with no benefit in a Docker image. Stay with wwwroot/. |
| `File.Move(src, dst, overwrite: true)` | `File.Replace()` | Both call POSIX `rename()` under .NET 8 on Linux; `File.Move(overwrite: true)` is simpler API surface. Use `File.Move`. |

**Installation:**

```bash
# No new NuGet packages required — everything is in the Web SDK
# htmx: download once, commit to wwwroot/js/
curl -Lo orchestrator/Argus.Orchestrator/wwwroot/js/htmx.min.js \
     https://cdn.jsdelivr.net/npm/htmx.org@2.0.10/dist/htmx.min.js
```

```xml
<!-- Argus.Orchestrator.csproj — one line changes, one line removed -->
<!-- BEFORE: -->
<Project Sdk="Microsoft.NET.Sdk.Worker">
<!-- AFTER: -->
<Project Sdk="Microsoft.NET.Sdk.Web">

<!-- REMOVE: (now implicit via Web SDK) -->
<PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.1" />
```

**Version verification:** Confirmed via npm registry — htmx.org 2.0.10 published ~April 2026. [CITED: www.npmjs.com/package/htmx.org]

---

## Architecture Patterns

### System Architecture Diagram

```
HA Supervisor (172.30.32.2)
     │
     │  HTTP GET /api/hassio_ingress/{token}/{path}
     │  Header: X-Ingress-Path: /api/hassio_ingress/{token}
     │  (Supervisor may strip prefix before forwarding — live-test item)
     ▼
Kestrel 0.0.0.0:8099
     │
     ├─→ [1] X-Ingress-Path Middleware
     │       sets context.Request.PathBase = ingressPath
     │
     ├─→ [2] app.UseRouting()  (explicit, after middleware)
     │
     ├─→ [3] app.UseStaticFiles()
     │       serves wwwroot/js/htmx.min.js, wwwroot/css/argus.css
     │
     └─→ [4] app.MapGet("/", handler)
               reads X-Ingress-Path header → emits <base href="{ingressPath}/">
               renders placeholder HTML with detector status

DI Container (shared with BackgroundServices)
     ├── HaListenerWorker  ─────────────────┐
     ├── MqttPublisherWorker                │  all unchanged
     ├── HealthPublisherWorker  ────────────┤  BackgroundService.ExecuteAsync
     ├── BatchSchedulerWorker  ─────────────┘  runs alongside Kestrel
     ├── EntitiesConfig (singleton) ← Phase 1: empty-entities → LogWarning
     └── ConnectionSettings (singleton)

Config Seam (Phase 1 foundation, used by Phase 2+)
     ┌──────────────────────────────────────────┐
     │ ConfigWriter (new singleton)              │
     │  SemaphoreSlim(1,1)                       │
     │  WriteAsync(yaml):                        │
     │    1. write to /data/.entities.tmp.{guid} │
     │    2. File.Move(tmp, target, overwrite:   │
     │       true)  ← atomic POSIX rename        │
     └──────────────────────────────────────────┘
                  ↑
          used by Phase 2+ HTTP save handler
```

### Recommended Project Structure

```
orchestrator/Argus.Orchestrator/
├── Program.cs               (SDK migration + Kestrel + middleware wiring)
├── Argus.Orchestrator.csproj (Sdk="Microsoft.NET.Sdk.Web")
├── Config/
│   ├── EntitiesConfigLoader.cs  (Validate() → LogWarning on empty)
│   ├── EntitiesConfig.cs        (unchanged)
│   └── ConfigWriter.cs          (NEW: atomic write + SemaphoreSlim)
└── wwwroot/
    ├── js/
    │   └── htmx.min.js          (committed, 2.0.10, BSD 0-Clause)
    └── css/
        └── argus.css            (new: CSS custom properties from UI-SPEC)

argus/
├── config.yaml                  (add ingress/panel keys)
└── Dockerfile                   (no change for add-on; see note below)

orchestrator/
└── Dockerfile                   (standalone deploy image: aspnet→runtime swap)
```

### Pattern 1: Worker → Web SDK Migration

**What:** Convert `Host.CreateApplicationBuilder` to `WebApplication.CreateBuilder`. All DI registrations are identical — only the builder type and the final `app.Run()` call change.

**When to use:** Whenever a Worker Service needs to serve HTTP endpoints.

**Key insight:** `WebApplication` implements `IHost`. BackgroundServices registered via `AddHostedService<T>` run alongside Kestrel as additional IHostedServices. Kestrel is itself implemented as an internal IHostedService (`GenericWebHostService`). [VERIFIED: Context7 /dotnet/aspnetcore.docs]

```csharp
// Source: Context7 /dotnet/aspnetcore.docs (hosted-services5.md)
// BEFORE:
var builder = Host.CreateApplicationBuilder(args);
// ... AddHostedService / AddSingleton calls (unchanged) ...
var host = builder.Build();
host.Run();

// AFTER:
var builder = WebApplication.CreateBuilder(args);
// ... same AddHostedService / AddSingleton calls, verbatim ...
var app = builder.Build();

// Configure Kestrel — must be before Build():
builder.WebHost.ConfigureKestrel(opts =>
    opts.Listen(System.Net.IPAddress.Any, 8099));

// Middleware pipeline (after Build()):
app.Use(async (ctx, next) =>         // [1] X-Ingress-Path PathBase
{
    if (ctx.Request.Headers.TryGetValue("X-Ingress-Path", out var ip))
        ctx.Request.PathBase = new PathString(ip.ToString());
    await next();
});
app.UseRouting();                    // [2] explicit — must be AFTER [1]
app.UseStaticFiles();                // [3]
app.MapGet("/", (HttpRequest req) => // [4] placeholder handler
{
    var ingressPath = req.Headers["X-Ingress-Path"].FirstOrDefault() ?? "";
    return Results.Content(BuildPlaceholderHtml(ingressPath), "text/html");
});

app.Run();
```

### Pattern 2: X-Ingress-Path Dual-Layer Defense

**What:** Handle Supervisor Ingress path in two layers: server-side PathBase + browser-side `<base>` tag.

**When to use:** All Phase 1–4 HTML responses.

**Why both layers:**
- PathBase: ensures ASP.NET's `LinkGenerator`, redirect helpers, and static-file middleware generate correct absolute URLs. [VERIFIED: andrewlock.net/using-pathbase-with-dotnet-6-webapplicationbuilder — MEDIUM confidence]
- `<base>` tag: ensures browser resolves relative `href`s and htmx `hx-get` calls against the correct ingress prefix. [CITED: HA developer docs, community.home-assistant.io/t/276905]

**Middleware ordering critical rule (from official docs):**
> "If middleware should be run before route matching occurs, `UseRouting` should be called and the middleware should be placed **before** the call to `UseRouting`."
> [VERIFIED: learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/middleware, aspnetcore-8.0 moniker]

```csharp
// Source: learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/middleware
// X-Ingress-Path middleware must be placed BEFORE the explicit UseRouting() call.
// WebApplication auto-adds UseRouting() AFTER all user middleware by default —
// BUT if you call UseRouting() yourself, that overrides the auto-placement.

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Headers.TryGetValue("X-Ingress-Path", out var ingressPath))
        ctx.Request.PathBase = new PathString(ingressPath.ToString());
    await next();
});
app.UseRouting(); // explicit call converts auto-placement into a no-op
app.UseStaticFiles();
app.MapGet("/", (HttpRequest req) =>
{
    var ingressPath = req.Headers["X-Ingress-Path"].FirstOrDefault() ?? "";
    return Results.Content($$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="UTF-8">
          <title>Argus</title>
          <base href="{{ingressPath}}/">
          <link rel="stylesheet" href="css/argus.css">
          <script src="js/htmx.min.js" defer></script>
        </head>
        <body>...</body>
        </html>
        """, "text/html");
});
```

### Pattern 3: Atomic Config Write

**What:** Write YAML to a temp file on the same filesystem, then atomically rename over the target.

**When to use:** Any code path that writes `/data/entities.yaml`.

**Why atomic rename:** `File.Move(src, dst, overwrite: true)` calls the POSIX `rename()` syscall on Linux. POSIX specifies `rename()` is atomic — a reader will always see either the old or the new complete file, never a partial write. [VERIFIED: learn.microsoft.com/en-us/dotnet/api/system.io.file.move — Move(String, String, Boolean) available since .NET Core 3.0+]

**SemaphoreSlim:** Serializes concurrent write requests so no two writers call `rename` simultaneously (which is safe but wastes one write).

```csharp
// Source: verified from System.IO.File.Move .NET 8 docs + POSIX rename semantics
public sealed class ConfigWriter
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task WriteAsync(string targetPath, string yaml,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var dir = Path.GetDirectoryName(targetPath)!;
            var tmp = Path.Combine(dir, $".entities.tmp.{Guid.NewGuid():N}.yaml");
            await File.WriteAllTextAsync(tmp, yaml, ct);
            File.Move(tmp, targetPath, overwrite: true); // atomic POSIX rename
        }
        finally
        {
            _lock.Release();
        }
    }
}
```

### Pattern 4: EntitiesConfigLoader Empty-Entities Fix

**What:** Change `Validate()` from `throw` to `LogWarning` when entities are null or empty.

**When to use:** Phase 1 — must land before any first-boot scenario where the UI has not yet been used to configure entities.

**Current code (confirmed by reading the file):**
```csharp
// Current — orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs line 43
private static void Validate(EntitiesConfig config, string path)
{
    if (config.Entities == null || config.Entities.Count == 0)
        throw new InvalidOperationException(  // ← CRASH on empty entities
            $"entities.yaml at '{path}' contains no entities");
    ...
}
```

**Target behavior:**
```csharp
// After fix — Validate() receives an ILogger parameter (or restructure as instance method)
private static void Validate(EntitiesConfig config, string path, ILogger logger)
{
    if (config.Entities == null || config.Entities.Count == 0)
    {
        logger.LogWarning("entities.yaml at '{Path}' contains no entities " +
            "— orchestrator running with empty pipeline; configure via UI.", path);
        return; // ← no throw; BackgroundServices start; UI can load
    }
    foreach (var entity in config.Entities)
    {
        if (string.IsNullOrWhiteSpace(entity.EntityId))
            throw new InvalidOperationException(
                "An entity in entities.yaml is missing 'entity_id'");
        if (entity.Detectors == null || entity.Detectors.Count == 0)
            throw new InvalidOperationException(
                $"Entity '{entity.EntityId}' has no detectors configured");
    }
}
```

**Load() call site** already passes `logger` — the signature change propagates to one call.

**Existing test** `Load_OneEntityWithHstParams_ParsesCorrectly` passes unchanged. A new test `Load_EmptyEntities_LogsWarning_DoesNotThrow` must be added to `EntitiesConfigTests.cs`.

### Anti-Patterns to Avoid

- **Bind Kestrel to loopback:** `builder.WebHost.UseUrls("http://localhost:8099")` or `http://127.0.0.1:8099` — Supervisor at `172.30.32.2` cannot reach loopback. Always use `IPAddress.Any` (0.0.0.0).
- **ASPNETCORE_URLS in s6 env:** Setting `ASPNETCORE_URLS` in `10-config-gen.sh` or a service run file risks override by downstream configuration. Control the port exclusively in `Program.cs` via `ConfigureKestrel`.
- **Non-atomic write:** `File.WriteAllText("/data/entities.yaml", yaml)` directly — races with FileSystemWatcher. Always use temp-then-rename.
- **Add `ports:` to config.yaml for port 8099:** Creates an unauthenticated endpoint reachable from the LAN, bypassing HA Ingress auth.
- **UseStaticFiles before PathBase middleware:** Static file 404s through Ingress. UseStaticFiles must come after the PathBase middleware and the explicit `UseRouting()` call.
- **Call UseRouting() before the X-Ingress-Path middleware:** WebApplication auto-places UseRouting() after user middleware by default, but if you call it manually, placement matters. Calling `app.UseRouting()` before the custom PathBase middleware means routing runs before PathBase is set — routes with PathBase-relative patterns fail.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Atomic file write | Custom `FileStream` with explicit flush+close | `File.WriteAllTextAsync(tmp) + File.Move(tmp, dst, overwrite: true)` | POSIX `rename()` is guaranteed atomic by the OS; custom stream logic can still produce partial reads on process kill |
| Serialization of concurrent writes | Mutex / lock() | `SemaphoreSlim(1,1)` with `WaitAsync` | Async-compatible; `lock()` cannot be used with `await` |
| Kestrel port configuration | Custom `IHostedService` that calls Socket.Bind | `builder.WebHost.ConfigureKestrel(opts => opts.Listen(...))` | ConfigureKestrel is the documented API; it configures before Kestrel starts |
| CSS framework | Write custom grid system from scratch | Single `argus.css` with CSS custom properties per UI-SPEC | Phase 1 is placeholder only — full CSS token system already defined in UI-SPEC.md |

**Key insight:** The atomic rename pattern is the only correct solution for concurrent file write + FileSystemWatcher read. Any custom buffering or FileStream locking can still be interrupted by process kill; rename is atomic at the kernel level.

---

## Common Pitfalls

### Pitfall 1: X-Ingress-Path Breaks All Absolute Asset URLs
**What goes wrong:** Static assets served with absolute paths (`<script src="/js/htmx.min.js">`) resolve against the HA host root, not the Ingress prefix. All CSS/JS returns 404; page renders blank. [CITED: .planning/research/PITFALLS.md — Pitfall 1; MEDIUM confidence from prior research]

**Why it happens:** Browser resolves `/` relative to the HA URL, which is `https://homeassistant.local:8123`, not the ingress prefix.

**How to avoid:** (a) All asset `href`/`src` attributes must use relative paths (no leading `/`). (b) Every HTML response emits `<base href="{ingressPath}/">` in `<head>`.

**Warning signs:** DevTools shows `GET https://homeassistant.local:8123/js/htmx.min.js` returning 404. Page loads but all styles missing. Works fine on direct port but fails via "Open Web UI".

### Pitfall 2: Kestrel Bound to Loopback → 502 Bad Gateway
**What goes wrong:** `WebApplication.CreateBuilder` defaults to `http://localhost:5000`. Supervisor at `172.30.32.2` cannot reach loopback; HA UI shows "502: Bad Gateway". [CITED: .planning/research/PITFALLS.md — Pitfall 2]

**Why it happens:** `localhost` resolves to `127.0.0.1` inside the container. Supervisor connects from `172.30.32.2`, which is not on the loopback interface.

**How to avoid:** `builder.WebHost.ConfigureKestrel(opts => opts.Listen(IPAddress.Any, 8099))`. Verify with `ss -tlnp | grep 8099` showing `0.0.0.0:8099`.

**Warning signs:** `ss -tlnp | grep 8099` shows `127.0.0.1:8099`. No HTTP request logged by Kestrel when UI is opened. gRPC watchdog passes (detector binds `0.0.0.0:50051`) but UI fails.

### Pitfall 3: Default Kestrel URL 5000 Binds in Addition to 8099
**What goes wrong:** `WebApplication.CreateBuilder` binds `http://localhost:5000` AND `https://localhost:5001` by default in addition to any programmatic listen configuration. Two extra ports bind inside the container; port 5000 may conflict with another add-on. [CITED: .planning/research/PITFALLS.md — Pitfall 7]

**How to avoid:** `ConfigureKestrel` with only `opts.Listen(IPAddress.Any, 8099)` replaces the default endpoint list. Do not use `UseUrls` alongside `ConfigureKestrel` — they conflict. Verify with `ss -tlnp` showing only `0.0.0.0:8099` (plus the gRPC 50051).

### Pitfall 4: UseRouting Auto-Placement Runs Before PathBase Middleware
**What goes wrong:** `WebApplication` auto-adds `UseRouting()` between `UseDeveloperExceptionPage` and user middleware. If no explicit `UseRouting()` call is made, user middleware runs AFTER routing — PathBase is set too late for route matching. [VERIFIED: learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/middleware]

**How to avoid:** Call `app.UseRouting()` explicitly, positioned AFTER the PathBase middleware. The official docs confirm: when `UseRouting` is called explicitly, `WebApplication` converts its automatic placement into a no-op. Pattern:
```csharp
app.Use(/* PathBase middleware */);   // sets PathBase from header
app.UseRouting();                     // explicit — after PathBase
app.UseStaticFiles();                 // after routing
app.MapGet("/", ...);
```

### Pitfall 5: aspnet vs runtime Docker Base Image Confusion (Add-On vs Standalone)
**What goes wrong:** The CONTEXT.md locked decision references `aspnet:8.0-jammy-chiseled` replacing `runtime:8.0-jammy-chiseled`. However, the **add-on Dockerfile** (`argus/Dockerfile`) does NOT use either of those Microsoft images — it uses `ghcr.io/home-assistant/base-debian:bookworm` and installs .NET via `dotnet-install.sh`. Swapping the add-on Dockerfile base image to `aspnet:8.0-jammy-chiseled` would break the s6-overlay entrypoint, Python 3.11 co-installation, and the HA-required label system.

**Root cause:** There is a separate standalone deploy image (for docker-compose deployments) that does use Microsoft base images. STATE.md "v3.0 architecture decisions" identifies the base image swap as relevant — this applies only to the standalone deploy image (`orchestrator/Dockerfile` or `deploy/`), NOT to the add-on image (`argus/Dockerfile`). [VERIFIED: reading argus/Dockerfile directly]

**How to avoid:**
- Add-on Dockerfile (`argus/Dockerfile`): **no change** to the base image. The .NET 8 runtime installed via `dotnet-install.sh` is already `dotnet --runtime` only (not `aspnet`). The Web SDK and Kestrel are self-contained in the published output — no additional runtime package needed.
- Standalone deploy Dockerfile (if it exists): swap `mcr.microsoft.com/dotnet/runtime:8.0-jammy-chiseled` → `mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled` to include ASP.NET Core runtime libraries.
- Check whether `orchestrator/publish/` contains `Microsoft.AspNetCore.*.dll` entries — if the published output is self-contained, no base image change is needed even for the standalone image.

**Warning signs:** After building the add-on image with an Microsoft base, the s6 entrypoint (`/init`) is missing; container exits immediately with "exec /init: no such file or directory".

### Pitfall 6: gen-entities.py Overwrites UI Config on Restart (Pre-Condition for Phase 2)
**What goes wrong:** `10-config-gen.sh` runs `gen-entities.py` unconditionally at every container start, overwriting `/data/entities.yaml`. Once Phase 2 lets users save config via the UI, the next add-on restart silently erases it. [CITED: .planning/research/PITFALLS.md — Pitfall 8]

**Phase relevance:** Phase 1 must NOT introduce this guard yet (Phase 1 ships no UI save endpoint). The guard must land at the START of Phase 2, before the first save endpoint is wired. This is a Phase 2 pre-condition, noted here so the planner puts it first in Phase 2 Wave 0.

### Pitfall 7: BatchSchedulerWorker Uses EntitiesConfig at Construction vs Per-Cycle
**What goes wrong:** `BatchSchedulerWorker` captures `EntitiesConfig entities` as a constructor parameter (confirmed by reading the code). `_entities` is set once at construction time. If Phase 3 hot-reloads `EntitiesConfig`, `BatchSchedulerWorker` continues using the stale snapshot. [VERIFIED: reading BatchSchedulerWorker.cs line 36 `private readonly EntitiesConfig _entities`]

**Phase relevance:** This is a Phase 3 planning concern, NOT Phase 1. Documented here because STATE.md identifies it as a "before Phase 3 planning" item. Phase 1 does not change `BatchSchedulerWorker` — the constructor capture pattern is unchanged.

---

## Code Examples

### Worker → Web SDK: Program.cs Scaffold

```csharp
// Source: Context7 /dotnet/aspnetcore.docs + verified pattern
// Replace Host.CreateApplicationBuilder with WebApplication.CreateBuilder.
// ConfigureKestrel must be called on builder BEFORE builder.Build().

var builder = WebApplication.CreateBuilder(args);

// === All existing DI registrations go here — verbatim, unchanged ===
// builder.Services.AddSingleton<EntitiesConfig>(entitiesConfig);
// builder.Services.AddSingleton<ConnectionSettings>(connectionSettings);
// builder.Services.AddSingleton<GrpcChannel>(...);
// builder.Services.AddSingleton<DetectionGateway>();
// builder.Services.AddSingleton<ReconnectCooldown>();
// builder.Services.AddSingleton<ArgusHealthSignals>();
// builder.Services.AddSingleton<IHaEventSource, NetDaemonHaEventSource>();
// builder.Services.AddHostedService<HaListenerWorker>();
// builder.Services.AddSingleton<IMqttCredentialSource>(...);
// builder.Services.AddSingleton<MqttConnection>(...);
// builder.Services.AddSingleton<StatePublisher>();
// builder.Services.AddSingleton<IStatePublisher>(...);
// builder.Services.AddHostedService<MqttPublisherWorker>();
// builder.Services.AddHostedService<HealthPublisherWorker>();
// builder.Services.AddSingleton<ScoreStreamPipeline>();
// builder.Services.AddSingleton<ConfigWriter>(); // NEW
// if InfluxDB path: ... unchanged ...

// NEW: Kestrel on 0.0.0.0:8099 — must be before Build()
builder.WebHost.ConfigureKestrel(opts =>
    opts.Listen(System.Net.IPAddress.Any, 8099));

var app = builder.Build();

// NEW: middleware pipeline
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Headers.TryGetValue("X-Ingress-Path", out var ingressPath))
        ctx.Request.PathBase = new PathString(ingressPath.ToString());
    await next();
});
app.UseRouting();    // explicit — must follow PathBase middleware
app.UseStaticFiles();
app.MapGet("/", (HttpRequest req) =>
{
    var ip = req.Headers["X-Ingress-Path"].FirstOrDefault() ?? "";
    return Results.Content(PlaceholderPage.Build(ip), "text/html");
});

// CHANGED: host → app
app.Run();
```

### config.yaml Ingress Keys

```yaml
# Source: developers.home-assistant.io/docs/add-ons/configuration
# Add these keys to argus/config.yaml (after existing keys):
ingress: true
ingress_port: 8099
panel_icon: "mdi:tune-variant"
panel_title: "Argus"

# NOTE: do NOT add a `ports:` entry for 8099.
# Ingress is the only permitted access path (UI-01 constraint).
# ingress_stream: false  (default — not needed; no chunked/SSE in Phase 1)
# panel_admin: true      (default — owner-only UI)
```

### Placeholder Page HTML (UI-SPEC-compliant)

```html
<!-- Matches UI-SPEC.md layout shell, typography, color tokens, and copywriting -->
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Argus</title>
  <base href="{ingressPath}/">
  <link rel="stylesheet" href="css/argus.css">
  <script src="js/htmx.min.js" defer></script>
</head>
<body>
  <header class="argus-header"><span class="argus-heading">Argus</span></header>
  <main class="argus-main">
    <p class="argus-display">Argus is running</p>
    <p class="argus-body">Configuration UI coming soon. Sensor anomaly detection is active.</p>
    <div class="argus-status">
      <!-- server-side: inject status-ok or status-error class + label -->
      <span class="argus-status-dot {statusClass}"></span>
      <span class="argus-label">{statusLabel}</span>
    </div>
  </main>
  <footer class="argus-footer">
    <span class="argus-label">v{version}</span>
  </footer>
</body>
</html>
```

---

## Docker Base Image Clarification

This is the most important correctness item in this research. The locked decision references `aspnet:8.0-jammy-chiseled` but requires careful application:

| Image | File | Current Base | Applies to Lock? | Action |
|-------|------|-------------|-----------------|--------|
| Add-on image | `argus/Dockerfile` | `ghcr.io/home-assistant/base-debian:bookworm` | NO — this is HA's own base with s6-overlay + Python 3.11 | **No change to base image** |
| Standalone deploy | `orchestrator/Dockerfile` (if it exists) or `deploy/` | `mcr.microsoft.com/dotnet/runtime:8.0-jammy-chiseled` | YES | Swap to `aspnet:8.0-jammy-chiseled` |

**For the add-on image:** The add-on installs .NET via `dotnet-install.sh --runtime dotnet`. The Web SDK publishes `Microsoft.AspNetCore.*.dll` files alongside the application output — these are already in `orchestrator/publish/` and are copied into the image via `COPY orchestrator/publish/ /opt/argus/orchestrator/`. No base image change is needed. Kestrel works because ASP.NET Core runtime libraries ship in the publish output.

**Why `aspnet` is larger than `runtime`:** The `aspnet` image includes the ASP.NET Core runtime shared framework in the image layer. The `runtime` image does not. When publishing self-contained or including ASP.NET Core DLLs in the publish output (which dotnet publish does by default for Kestrel-based apps), both images work. The `aspnet` image is the correct base for a standalone deploy that expects shared framework libraries NOT to be in the publish output (smaller publish output, larger image). [CITED: github.com/dotnet/dotnet-docker/blob/main/README.aspnet.md]

**Recommendation for Phase 1:** Focus on the add-on image only (no Dockerfile change required for the add-on). If a standalone deploy Dockerfile exists, perform the swap there as a low-risk ~10 MB size reduction.

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `Microsoft.NET.Sdk.Worker` + `Host.CreateApplicationBuilder` | `Microsoft.NET.Sdk.Web` + `WebApplication.CreateBuilder` | .NET 6 (2021) — now standard | BackgroundServices and Kestrel coexist in one process |
| `ManagedClient` (MQTTnet v4) | `MqttClientFactory` (MQTTnet v5) | MQTTnet v5 (2023) — already migrated in v2.0 | No impact on Phase 1 |
| `File.Move(src, dst)` throws if dst exists | `File.Move(src, dst, overwrite: true)` atomic | .NET Core 3.0 (2019) | Use the three-argument overload |
| `UsePathBase("/static-path")` | Per-request PathBase from request header in `app.Use` | Inherent to dynamic ingress scenarios | Must call `UseRouting()` explicitly after |

---

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | HA Supervisor strips the ingress prefix before forwarding to the container (`/api/hassio_ingress/{token}/foo` → `/foo`) | Pitfalls (Pitfall 1), STACK.md claim | If Supervisor does NOT strip, PathBase middleware must map the full prefix to the routing path; asset 404s and route mismatches occur. The dual implementation (PathBase + `<base>` tag) handles both cases, so this assumption only affects whether PathBase is strictly necessary — not whether the dual implementation works. |
| A2 | `dotnet-install.sh --runtime dotnet` installs only the runtime, not the ASP.NET shared framework, requiring the publish output to include ASP.NET Core DLLs | Docker Base Image Clarification | If `dotnet-install.sh` already installs the ASP.NET shared framework, the publish output may need `--no-self-contained` tuning. Verify by checking `orchestrator/publish/` for `Microsoft.AspNetCore.*.dll` files (they are present — confirmed in Bash check). |
| A3 | `File.Move(src, dst, overwrite: true)` calls POSIX `rename()` on Linux (atomic) | Pattern 3 (Atomic Config Write) | If it calls `CopyFile + Delete` instead, a partial copy + crash produces corrupt destination. This would be a .NET runtime bug; highly unlikely. Cross-volume moves do NOT use rename (but `/data/` is a single volume in Docker — no cross-volume risk). |
| A4 | htmx 2.0.10 is a published, stable release | Standard Stack | If 2.0.10 is not the latest stable, the UI-SPEC.md version pin may need updating. Non-blocking for planning — download the file before committing. |

**A1 is the only consequential assumption.** The dual implementation neutralizes it.

---

## Open Questions

1. **X-Ingress-Path / PathBase conflict (Supervisor strip behavior)**
   - What we know: STACK.md claims Supervisor strips the prefix; PITFALLS.md says set PathBase from header. Both documents are from the same research session (2026-06-30) and contradict each other.
   - What's unclear: Whether `/api/hassio_ingress/{token}/foo` arrives at Kestrel as `/foo` (stripped) or `/api/hassio_ingress/{token}/foo` (not stripped).
   - Recommendation: Implement the dual layer (PathBase middleware + `<base>` tag) — it is correct for both behaviors. Validate by opening "Open Web UI" on the live HA instance and checking the URL the browser shows and the path Kestrel logs.

2. **Assembly version for `v{version}` footer**
   - What we know: UI-SPEC.md requires `v{version}` in the footer, populated server-side.
   - What's unclear: Whether to read from `Assembly.GetExecutingAssembly().GetName().Version` or from an environment variable / file (the Docker label `BUILD_VERSION` is set at image build time, not available at runtime as an env var automatically).
   - Recommendation: Use `Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown"` — this is populated from the csproj `<Version>` element or CI build number. [ASSUMED]

3. **Placeholder page: detector status indicator**
   - What we know: UI-SPEC.md placeholder shows "Detector connected" / "Detector unreachable" status.
   - What's unclear: Whether the placeholder handler should call `DetectionGateway.CheckAsync()` on every page load (adds latency on each UI open) or read a cached value from `ArgusHealthSignals`.
   - Recommendation: Read `ArgusHealthSignals` (already a DI singleton) for a zero-latency status display. Inject `ArgusHealthSignals` into the handler or read it from a `IServiceProvider` factory.

---

## Environment Availability

> Phase 1 is a code + configuration change. All dependencies are within the existing process or committed assets.

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | `dotnet build` / `dotnet publish` | ✓ | 8.x (project already builds) | — |
| xunit (test framework) | Unit tests for empty-entities + atomic write | ✓ | 2.9.3 (in test csproj) | — |
| htmx 2.0.10 | wwwroot/js/htmx.min.js | Must be downloaded once | 2.0.10 | — download from cdn.jsdelivr.net before committing |
| Live HA OS instance | PathBase / Ingress live-test item | ✓ (user has live HA) | — | Cannot be automated; manual verification step |

---

## Validation Architecture

> `workflow.nyquist_validation` is `false` in `.planning/config.json`. Validation section skipped per config.

---

## Security Domain

> Note: `security_enforcement` key is not set in `.planning/config.json` — treated as enabled.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | Indirect — delegated to HA Ingress session auth | No additional auth layer in Phase 1; document as intentional (see CONTEXT.md "Deferred: validate_session") |
| V3 Session Management | No — HA Ingress manages sessions | n/a |
| V4 Access Control | Partial — no `ports:` entry in config.yaml is the access control | Enforced by config.yaml schema: `ingress: true`, no `ports:` entry |
| V5 Input Validation | No user inputs in Phase 1 | n/a (placeholder page only) |
| V6 Cryptography | No new crypto | Plain HTTP on LAN; HA Supervisor handles TLS externally |

### Known Threat Patterns for This Stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Exposed Ingress port on host network | Elevation of Privilege | No `ports:` entry in config.yaml; verified by code review of config.yaml |
| Kestrel binding to loopback — 502 "fixes" via ports: | Elevation of Privilege | Bind `0.0.0.0:8099`; never add `ports:` |
| YAML partial-read via non-atomic write | Tampering | Atomic rename (temp-file + File.Move overwrite); SemaphoreSlim serialization |

---

## Sources

### Primary (HIGH confidence)
- Context7 `/dotnet/aspnetcore.docs` — hosted services, UseStaticFiles, WebApplication.CreateBuilder, UseRouting, middleware ordering
- [learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/middleware?view=aspnetcore-8.0](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/middleware?view=aspnetcore-8.0) — definitive middleware ordering rules for WebApplication
- [learn.microsoft.com/en-us/dotnet/api/system.io.file.move?view=net-8.0](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move?view=net-8.0) — File.Move(String, String, Boolean) overwrite semantics, .NET 8
- Codebase: `orchestrator/Argus.Orchestrator/Program.cs`, `Config/EntitiesConfigLoader.cs`, `Config/EntitiesConfig.cs`, `Workers/BatchSchedulerWorker.cs`, `argus/config.yaml`, `argus/Dockerfile`, `argus/rootfs/etc/cont-init.d/10-config-gen.sh`
- `.planning/research/STACK.md` — v3.0 stack research (verified June 2026)
- `.planning/research/PITFALLS.md` — v3.0 pitfalls research (verified June 2026)

### Secondary (MEDIUM confidence)
- [andrewlock.net/using-pathbase-with-dotnet-6-webapplicationbuilder](https://andrewlock.net/using-pathbase-with-dotnet-6-webapplicationbuilder/) — PathBase ordering trap with WebApplication
- [andrewlock.net/understanding-pathbase-in-aspnetcore](https://andrewlock.net/understanding-pathbase-in-aspnetcore/) — PathBase mechanics
- [developers.home-assistant.io/docs/add-ons/configuration](https://developers.home-assistant.io/docs/add-ons/configuration) — ingress, ingress_port, panel_icon, panel_title keys confirmed
- [community.home-assistant.io/t/how-to-use-x-ingress-path-in-an-add-on/276905](https://community.home-assistant.io/t/how-to-use-x-ingress-path-in-an-add-on/276905) — practical X-Ingress-Path usage patterns
- [github.com/dotnet/dotnet-docker/blob/main/README.aspnet.md](https://github.com/dotnet/dotnet-docker/blob/main/README.aspnet.md) — aspnet vs runtime base image difference

### Tertiary (LOW confidence)
- WebSearch results on Supervisor ingress prefix-stripping behavior — conflicting; superseded by dual-implementation approach that works regardless

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — confirmed in codebase; all libraries already present except htmx (must be downloaded)
- SDK migration pattern: HIGH — verified in official MS docs via Context7
- Kestrel bind address: HIGH — confirmed by reading PITFALLS.md + official Kestrel docs
- PathBase ordering: HIGH — confirmed by official minimal-API middleware docs
- X-Ingress-Path behavior (strip vs not strip): LOW — conflicting sources; dual implementation required
- Atomic write semantics: HIGH — confirmed by File.Move .NET 8 API docs + POSIX rename spec
- Empty-entities fix: HIGH — confirmed by reading EntitiesConfigLoader.cs source directly
- Docker base image clarification: HIGH — confirmed by reading argus/Dockerfile directly

**Research date:** 2026-06-30
**Valid until:** 2026-07-30 (stable .NET 8 + HA add-on ecosystem; 30 days)
