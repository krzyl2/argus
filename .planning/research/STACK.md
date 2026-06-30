# Stack Research — Argus v3: Ingress Configuration UI

**Domain:** ASP.NET Core Minimal API web UI co-hosted inside an existing Generic Host worker, behind Home Assistant Supervisor Ingress
**Researched:** 2026-06-30
**Confidence:** HIGH — core .NET facts verified against official MS docs; HA Ingress headers verified against HA developer docs + supervisor source; htmx version confirmed on npm/CDN

---

## Context: What Already Exists (Do Not Change)

The orchestrator is a `Microsoft.NET.Sdk.Worker` project (`Host.CreateApplicationBuilder`), with six `BackgroundService` / `IHostedService` instances registered via `AddHostedService`. The Dockerfile base is `ghcr.io/home-assistant/base-debian:bookworm` with .NET 8 runtime installed via `dotnet-install.sh` and Python 3.11 via apt. The existing services must keep running without change.

---

## Decision A — Hosting model: co-host vs separate process

**Recommendation: Co-host ASP.NET Core inside the existing Generic Host process.**

Switch the project SDK from `Microsoft.NET.Sdk.Worker` to `Microsoft.NET.Sdk.Web` and replace `Host.CreateApplicationBuilder` with `WebApplication.CreateBuilder`. `WebApplication` is a superset of `IHost` — all existing `AddHostedService` / `AddSingleton` / `AddSingleton<IHostedService>` registrations work identically. `WebApplication` implements `IHost`, runs the Kestrel HTTP server as one of its own internal hosted services, and calls every `BackgroundService.ExecuteAsync` alongside it. No second process, no IPC, no additional s6 service, no image-size cost.

**Why not a separate process:**
- A separate .NET process adds ~50–80 MB to the published output and requires a new s6 service, inter-process communication (HTTP loopback or named pipe) to share in-memory state (`EntitiesConfig`, `ArgusHealthSignals`), and synchronised config-reload signalling. The shared-state coupling means the API reads the same singleton `EntitiesConfig` that the pipeline workers use — impossible across processes without serialisation.
- A second Python/Node process is even heavier and introduces a foreign runtime.

**Migration from Worker SDK to Web SDK is a one-line change in the .csproj:**

```xml
<!-- Before -->
<Project Sdk="Microsoft.NET.Sdk.Worker">
<!-- After -->
<Project Sdk="Microsoft.NET.Sdk.Web">
```

`Program.cs` changes:

```csharp
// Before
var builder = Host.CreateApplicationBuilder(args);
// ... service registrations ...
var host = builder.Build();
host.Run();

// After
var builder = WebApplication.CreateBuilder(args);
// ... same service registrations, unchanged ...
var app = builder.Build();
// Add static files + API routes (see Decision B)
app.UseStaticFiles();
app.MapGet("/api/config", ...);
app.Run();
```

The `Microsoft.Extensions.Hosting` explicit package reference becomes implicit (pulled in by the Web SDK); remove it to avoid version conflicts, or keep it pinned to 8.0.x.

---

## Decision B — UI rendering: server-rendered HTMX vs SPA

**Recommendation: Server-rendered minimal HTML + HTMX 2.x. No SPA, no build step, no Node toolchain in CI.**

### Why server-rendered beats a SPA here

| Concern | Server-rendered + HTMX | SPA (React/Vue/Svelte) |
|---------|----------------------|----------------------|
| Image size impact | Zero — htmx.min.js is 14 KB, copied into `wwwroot/` as a static file | +100–500 MB for Node.js in CI image or a multi-stage build that copies dist/ output |
| Build pipeline | No build step — HTML templates returned from C# endpoint methods | Requires Vite/Webpack, npm install in CI, separate build stage |
| Base-path handling | Server renders `<base href="{ingressPath}/">` from `X-Ingress-Path` header; all relative hrefs work automatically | SPA must be configured with `PUBLIC_URL` / `base` at build time; dynamic ingress path requires runtime injection hacks |
| Complexity | CRUD config form = ~5 HTMX-annotated HTML partials returned by Minimal API endpoints | Full SPA lifecycle: state management, routing, bundler, hot reload |
| Runtime dependency | None (htmx served from wwwroot/, no CDN) | None if bundled, but adds MB |
| License | htmx: BSD 0-Clause (permissive, passes project constraint) | React: MIT, Vue: MIT — both permissive; not the deciding factor |

HTMX 2.0.x (currently 2.0.10 as of June 2026) replaced `hx-sse` / `hx-ws` with separate extension files. For this UI — a config form, entity list, detector-assignment table — no SSE or WebSocket extension is needed. Plain `hx-get` / `hx-post` / `hx-swap` attributes are sufficient. The library is 14 KB minified.

### Base-path handling under HA Ingress

HA Supervisor strips the ingress prefix from the URL before forwarding to the add-on container, so the container always sees requests arriving at `/{path}` (e.g., `/` or `/api/config`). The Supervisor injects one header:

- `X-Ingress-Path` — the full ingress prefix that HA uses externally (e.g., `/api/hassio_ingress/<token>`). The add-on uses this to construct absolute `<base href>` or redirect URLs if needed.

**Because the Supervisor strips the prefix**, the add-on's Kestrel server does **not** need `UsePathBase()`. The URL the server sees is already stripped. The only use of `X-Ingress-Path` is to emit a `<base href=".../">` tag in the HTML `<head>` so that static-file and HTMX form-action URLs resolve correctly in the browser, which uses the external (unstripped) path.

Implementation:

```csharp
app.MapGet("/", (HttpRequest req) =>
{
    var ingressPath = req.Headers["X-Ingress-Path"].FirstOrDefault() ?? "";
    // Emit base tag for browser-relative URL resolution
    return Results.Content($$"""
        <!DOCTYPE html>
        <html>
        <head>
          <base href="{{ingressPath}}/">
          <script src="htmx.min.js"></script>
        </head>
        <body>
          <!-- config UI -->
        </body>
        </html>
        """, "text/html");
});
```

All `hx-get="/api/config"` links in rendered HTML resolve against `<base href>` and are rewritten by the browser to `/api/hassio_ingress/<token>/api/config`, which the Supervisor forwards correctly.

### Static file serving (htmx.min.js + CSS)

Use `app.UseStaticFiles()` with a physical `wwwroot/` directory inside the published output. The SDK automatically includes files from `wwwroot/` in publish output when `Microsoft.NET.Sdk.Web` is the project SDK. No `ManifestEmbeddedFileProvider` is needed — embedded resources add build-manifest complexity with no benefit at this scale.

Copy `htmx.min.js` into `orchestrator/Argus.Orchestrator/wwwroot/` and add to the csproj:

```xml
<ItemGroup>
  <Content Include="wwwroot\**" CopyToPublishDirectory="Always" />
</ItemGroup>
```

The Web SDK does this automatically; the explicit item group is only needed if the Worker SDK path is retained (not recommended).

---

## Recommended Stack

### Core Technologies

| Technology | Version | Purpose | Why Recommended |
|------------|---------|---------|-----------------|
| `Microsoft.NET.Sdk.Web` (project SDK) | n/a (.NET 8) | Replaces `Microsoft.NET.Sdk.Worker`; enables `WebApplication.CreateBuilder`, Kestrel, static files, minimal API routing | Zero additional packages; `WebApplication` is a superset of `IHost` and runs all existing `BackgroundService` instances unchanged |
| ASP.NET Core Minimal API | .NET 8 (framework-included) | `MapGet`/`MapPost` handlers for config read/write endpoints | No controller overhead; 3–5 route handlers is the entire API surface; same DI container as workers |
| ASP.NET Core Static Files middleware | .NET 8 (framework-included) | Serves `wwwroot/` — htmx.min.js + any CSS | Single `app.UseStaticFiles()` call; no NuGet package |
| Kestrel HTTP server | .NET 8 (framework-included) | HTTP listener on `ingress_port` (8099) | Included in Web SDK; no nginx sidecar needed |

### Supporting Libraries

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| htmx | 2.0.10 | Browser-side progressive enhancement — partial HTML swaps on form submit and entity-list fetch | Copy `htmx.min.js` into `wwwroot/`; no npm required. Handles all UI interaction without a SPA framework. |
| `YamlDotNet` | 16.3.0 (already pinned) | Read/write `/data/entities.yaml` from the config API endpoints | Already a dependency; no version change needed |

### Development Tools

| Tool | Purpose | Notes |
|------|---------|-------|
| `dotnet publish` | Produces self-contained publish output including `wwwroot/` | CI already runs this; Web SDK automatically copies `wwwroot/**` into publish output |
| `dotnet watch` (dev only) | Hot-reload during local development | Standard .NET 8 SDK tool; not installed in the container |

---

## Installation

```xml
<!-- Argus.Orchestrator.csproj — two changes only -->
<!-- 1. Change SDK -->
<Project Sdk="Microsoft.NET.Sdk.Web">

<!-- 2. Remove explicit Microsoft.Extensions.Hosting (now implicit via Web SDK) -->
<!-- All other PackageReference entries are unchanged -->
```

```bash
# No new NuGet packages required — everything is in the Web SDK
# htmx: download once, commit to wwwroot/
curl -Lo orchestrator/Argus.Orchestrator/wwwroot/htmx.min.js \
     https://cdn.jsdelivr.net/npm/htmx.org@2.0.10/dist/htmx.min.js
```

---

## config.yaml Changes Required

Add three keys to `argus/config.yaml`:

```yaml
# NEW: enable Ingress ("Open Web UI" button in HA add-on panel)
ingress: true
ingress_port: 8099          # Kestrel listens on this port inside the container
panel_icon: mdi:tune-variant  # MDI icon shown in HA sidebar (optional; mdi:puzzle is default)
panel_title: "Argus Config"   # Sidebar label (optional; defaults to add-on name)
```

**Notes:**
- `ingress_port` must match the port Kestrel is configured to listen on. Default is 8099.
- Do **not** expose the port via `ports:` — Ingress is the only ingress path; no external port needed.
- `panel_admin: true` (default) is correct; this UI is for the owner only.
- `ingress_stream: false` (default) is correct; no chunked/SSE streaming from the config UI.
- `watchdog: "tcp://[HOST]:50051"` (existing gRPC port) is unaffected.

Configure Kestrel to listen on 0.0.0.0:8099 (not loopback — Supervisor connects from 172.30.32.2):

```json
// appsettings.json
{
  "Kestrel": {
    "Endpoints": {
      "Ingress": {
        "Url": "http://0.0.0.0:8099"
      }
    }
  }
}
```

Or via environment variable in the s6 run script:
```bash
ASPNETCORE_URLS=http://0.0.0.0:8099
```

---

## Alternatives Considered

| Recommended | Alternative | When to Use Alternative |
|-------------|-------------|-------------------------|
| `Microsoft.NET.Sdk.Web` (co-host) | Separate .NET process (second s6 service) | Never for this project — config UI needs access to the same singletons (`EntitiesConfig`, reload channel); separate process requires IPC and doubles .NET runtime overhead |
| `Microsoft.NET.Sdk.Web` (co-host) | Node.js/Python/nginx sidecar | Only if the UI technology was incompatible with .NET — not the case here |
| Server-rendered HTML + HTMX | React/Vue/Svelte SPA | If the UI required complex client-side state (e.g., real-time graph, drag-and-drop reordering with offline state). A config form does not. |
| `wwwroot/` static files | `ManifestEmbeddedFileProvider` (embedded resources) | If the assembly must ship as a single binary with no loose files. For a Docker add-on image this is unnecessary complexity. |
| htmx 2.0.10 | Alpine.js (7 KB) | Alpine is a fine alternative for purely declarative attribute-driven UI with no server interaction patterns. HTMX wins here because the entity-list needs server-fetched partial HTML swaps. |
| Kestrel (built-in) | nginx sidecar in front of Kestrel | Only if TLS termination or complex rewrite rules are needed. HA Ingress handles TLS; no nginx needed. |

---

## What NOT to Add

| Avoid | Why | Use Instead |
|-------|-----|-------------|
| React / Vue / Svelte | Requires Node.js in CI, adds 100–500 MB to CI image, complicates ingress base-path handling at build time | htmx 2.0.10 + server-rendered HTML |
| Blazor Server or Blazor WebAssembly | Blazor WASM adds ~6–10 MB to download; Blazor Server requires persistent SignalR WebSocket — fragile behind HA Supervisor proxy. Neither fits a 5-endpoint config form. | Minimal API + htmx |
| `UseForwardedHeaders` / `UsePathBase` middleware | HA Supervisor strips the ingress prefix before forwarding — the app sees requests at `/`, not at `/api/hassio_ingress/<token>/`. Calling `UsePathBase` with a static path would break routing; dynamic path from `X-Ingress-Path` is only needed for the `<base href>` tag. | Read `X-Ingress-Path` header directly in the root handler |
| Separate HTTPS / TLS in Kestrel | HA Supervisor Ingress handles TLS externally; Kestrel serves plain HTTP on 8099 inside the container | Plain HTTP Kestrel endpoint on 8099 |
| `aspnet:8.0` Docker base image | Project uses `base-debian:bookworm` + dotnet-install.sh; switching base image breaks s6-overlay and Python co-installation | Keep existing base image |
| OpenAPI / Swagger | This is an internal UI, not a public API. Zero consumers outside the htmx-driven UI. | None |
| SignalR | Config reload can be done with a simple POST + redirect. No real-time push to the browser is needed from the server side. | `hx-post` + `hx-swap` on form submit |
| ML.NET | Excluded by project constraint D2 | Python detector (no change) |
| CDN-referenced htmx | HA add-on operates offline/LAN; CDN fetch fails on air-gapped installs | Bundle `htmx.min.js` in `wwwroot/` |

---

## Stack Patterns

**If Kestrel port conflicts with another service:**
- Set `ingress_port` in `config.yaml` to a different value (e.g., 8080) and set `ASPNETCORE_URLS=http://0.0.0.0:8080` in the s6 run script.

**If config reload requires notifying background workers:**
- Inject `IHostApplicationLifetime` or a custom `IConfigReloadSignal` singleton into both the API handler and the affected worker. The API POST writes new YAML to `/data/entities.yaml`, then signals the singleton; the worker's `ExecuteAsync` loop checks the signal. No process restart needed.

**If the entity list is large (>200 sensors):**
- Use `hx-get="/api/entities?q={search}"` with HTMX search input debounce (`hx-trigger="keyup changed delay:300ms"`) to filter server-side. The server calls the already-wired `SelectDiscoverableSensors` and returns an HTML partial — same pattern as the streaming path uses today.

---

## Version Compatibility

| Component | Version | Notes |
|-----------|---------|-------|
| `Microsoft.NET.Sdk.Web` | .NET 8 | Compatible with all existing NuGet packages; Web SDK is a superset of Worker SDK |
| `Microsoft.Extensions.Hosting` | Remove explicit reference | Now implicit via Web SDK; keeping it at 8.0.1 is safe but redundant |
| htmx | 2.0.10 | No IE11 support (htmx 2.x dropped it); HA frontend runs Chromium — fine |
| `YamlDotNet` | 16.3.0 (existing) | No change needed |
| Kestrel | .NET 8 built-in | Listens on 0.0.0.0:8099; plain HTTP only |
| HA Supervisor Ingress proxy | n/a | Connects from 172.30.32.2 to container IP:8099; strips ingress path prefix from URL |

---

## Sources

- [ASP.NET Core hosted services (.NET 8 official docs)](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-8.0) — confirms `AddHostedService` + `WebApplication.CreateBuilder` work together; verified June 2026
- [.NET Worker Services docs](https://learn.microsoft.com/en-us/dotnet/core/extensions/workers) — SDK difference (`Microsoft.NET.Sdk.Worker` vs `Microsoft.NET.Sdk.Web`); confirmed BackgroundService works under both
- [ASP.NET Core .NET 5 → 6 migration guide](https://learn.microsoft.com/en-us/aspnet/core/migration/50-to-60?view=aspnetcore-8.0) — confirms Generic Host (`IHost`) is NOT deprecated; `WebApplication` is the preferred host going forward but Generic Host is still fully supported
- [HA Developer Docs — Presenting your app (Ingress)](https://developers.home-assistant.io/docs/apps/presentation/) — `ingress`, `ingress_port`, `ingress_entry`, `panel_icon`, `panel_title`, `ingress_stream` keys; `X-Ingress-Path` header; 172.30.32.2 source IP restriction; default port 8099
- [home-assistant/supervisor ingress.py source](https://github.com/home-assistant/supervisor/blob/main/supervisor/api/ingress.py) — confirms Supervisor strips URL prefix before forwarding to container; sets `X-Remote-User-ID/Name/Display-Name`; `X-Ingress-Path` is set by the Supervisor HTTP client (separate from the API proxy handler)
- [HA Community — X-Ingress-Path usage](https://community.home-assistant.io/t/how-to-use-x-ingress-path-in-an-add-on/276905) — practical pattern: use header value in `<base href>` tag; confirmed approach used by multiple add-on authors
- [HA Community — absolute path handling with HA Ingress](https://community.home-assistant.io/t/how-to-handle-absolute-paths-with-ha-ingress/370572) — confirms relative paths work through proxy without rewriting; `X-Ingress-Path` needed only for `<base href>` generation
- [htmx.org npm](https://www.npmjs.com/package/htmx.org) — version 2.0.10, last published ~2 months ago (April 2026); BSD 0-Clause license (confirmed permissive, passes project constraint)
- [htmx 2.0.0 release announcement](https://htmx.org/posts/2024-06-17-htmx-2-0-0-is-released/) — htmx 2.x change summary; extensions separated; DELETE now uses URL params
- [ASP.NET Core static files docs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/static-files?view=aspnetcore-8.0) — `UseStaticFiles()` with `wwwroot/` default path; `ManifestEmbeddedFileProvider` for embedded-resource alternative
- [ASP.NET Core proxy/load balancer docs](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-8.0) — `UsePathBase`, `UseForwardedHeaders`, `X-Forwarded-Prefix` — confirmed NOT needed here because Supervisor strips prefix before forwarding

---
*Stack research for: Argus v3.0 Ingress Configuration UI*
*Researched: 2026-06-30*
