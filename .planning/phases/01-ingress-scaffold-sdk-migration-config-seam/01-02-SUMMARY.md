---
phase: 01-ingress-scaffold-sdk-migration-config-seam
plan: "02"
subsystem: orchestrator
tags: [sdk-migration, kestrel, ingress, placeholder-page, web-sdk, htmx, css-tokens]
dependency_graph:
  requires: [01-01]
  provides: [ingress-scaffold, placeholder-page, static-assets, kestrel-8099, ingress-manifest-keys]
  affects: [argus/config.yaml, orchestrator/Program.cs, orchestrator/Argus.Orchestrator.csproj]
tech_stack:
  added: [Microsoft.NET.Sdk.Web, ASP.NET Core Minimal API, Kestrel, UseStaticFiles, htmx-2.0.10]
  patterns: [X-Ingress-Path PathBase middleware, base-href dual-layer, server-rendered HTML, volatile bool health signal]
key_files:
  created:
    - orchestrator/Argus.Orchestrator/PlaceholderPage.cs
    - orchestrator/Argus.Orchestrator/wwwroot/js/htmx.min.js
    - orchestrator/Argus.Orchestrator/wwwroot/css/argus.css
  modified:
    - orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj
    - orchestrator/Argus.Orchestrator/Program.cs
    - orchestrator/Argus.Orchestrator/Health/ArgusHealthSignals.cs
    - orchestrator/Argus.Orchestrator/Workers/HealthPublisherWorker.cs
    - argus/config.yaml
decisions:
  - "Kestrel bound via ConfigureKestrel(IPAddress.Any, 8099) — not UseUrls or ASPNETCORE_URLS"
  - "Dual PathBase + base-href defense: correct for both supervisor-strips and supervisor-does-not-strip behaviors"
  - "DetectorConnected added to ArgusHealthSignals as volatile bool; set by HealthPublisherWorker every ~15s for zero-latency UI reads"
  - "PlaceholderPage HTML-encodes ingressPath before base href interpolation (T-01-08)"
  - "argus/Dockerfile unchanged: add-on uses base-debian:bookworm + dotnet-install.sh; Web SDK publish output carries ASP.NET DLLs"
metrics:
  duration: "3m 51s"
  completed_date: "2026-06-30"
  tasks_completed: 3
  tasks_deferred_live_ha: 1
  files_created: 3
  files_modified: 5
  tests_passing: 117
---

# Phase 01 Plan 02: SDK Migration + Kestrel + Ingress Scaffold Summary

**One-liner:** Worker SDK migrated to Web SDK; Kestrel co-hosted on 0.0.0.0:8099 with X-Ingress-Path PathBase middleware, server-rendered placeholder page (htmx 2.0.10, CSS token foundation), and config.yaml ingress manifest keys.

---

## Tasks Completed

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 | Commit htmx + author argus.css design foundation | 57391c4 | wwwroot/js/htmx.min.js, wwwroot/css/argus.css |
| 2 | SDK migration + Kestrel + Ingress middleware + placeholder page | 03dc10a | Argus.Orchestrator.csproj, Program.cs, PlaceholderPage.cs, ArgusHealthSignals.cs, HealthPublisherWorker.cs |
| 3 | Declare ingress keys in add-on manifest | f5a8c2c | argus/config.yaml |

---

## Verification (Local / Non-Live-HA)

- `dotnet build Argus.Orchestrator.csproj -v minimal` — PASS (0 errors, 0 warnings)
- `dotnet test Argus.Orchestrator.Tests.csproj -v minimal` — PASS (117/117 tests)
- `grep "IPAddress.Any, 8099" Program.cs` — FOUND at line 152
- `grep "base href" PlaceholderPage.cs` — FOUND at line 48
- `wc -c wwwroot/js/htmx.min.js` — 51238 bytes (non-empty, starts with `var htmx=...`)
- `wc -c wwwroot/css/argus.css` — 5580 bytes (non-empty, contains `--color-surface`, dark scheme `@media`)
- `grep -qE '^\s*ports:' argus/config.yaml` — NOT FOUND (correct — no port exposure)
- Middleware order confirmed: `app.Use` (PathBase) → `app.UseRouting()` → `app.UseStaticFiles()` → `app.MapGet("/")` → `app.Run()`

---

## Deviations from Plan

### Auto-added Issues

**1. [Rule 2 - Missing Critical Functionality] Added DetectorConnected to ArgusHealthSignals**

- **Found during:** Task 2 — reading ArgusHealthSignals.cs revealed only `HaConnected` existed; no `DetectorConnected` property.
- **Issue:** PlaceholderPage requires `health.DetectorConnected` for the status indicator ("Detector connected" / "Detector unreachable"), but the property did not exist in the singleton.
- **Fix:** Added `public volatile bool DetectorConnected;` to `ArgusHealthSignals.cs`; updated `HealthPublisherWorker.cs` to cache the gRPC serving result into `_signals.DetectorConnected = serving` after each health cycle (~15s cadence). This is zero-latency for UI reads (no gRPC call on page load).
- **Files modified:** `Health/ArgusHealthSignals.cs`, `Workers/HealthPublisherWorker.cs`
- **Commit:** 03dc10a

**2. [Rule 3 - Blocking Fix] Added `using Argus.Orchestrator;` to Program.cs**

- **Found during:** Task 2 build — `PlaceholderPage` in `Argus.Orchestrator` namespace was not visible from top-level statements (global namespace) until the using was added.
- **Fix:** Added `using Argus.Orchestrator;` as the first using directive in Program.cs.
- **Files modified:** `Program.cs`
- **Commit:** 03dc10a (part of same task commit)

---

## Pending Live-HA Verification

**Status:** DEFERRED — this executor has no access to a live HA OS instance. All code changes are committed. Live verification must be performed by the operator after rebuilding and deploying the add-on image.

### Steps for the Operator

Rebuild and deploy the add-on image to the live HA OS instance (same flow used to ship v2.0), then perform the following checks:

1. **"Open Web UI" panel appears** — In HA, open the Argus add-on page. Confirm a button/panel labeled "Argus" (panel_title) with the `mdi:tune-variant` icon appears. This confirms `ingress: true` + `panel_icon` + `panel_title` in config.yaml were picked up by the Supervisor on restart.

2. **Placeholder page renders** — Click "Open Web UI" (NEVER access port 8099 directly). Confirm the page shows:
   - Display heading: "Argus is running"
   - Body: "Configuration UI coming soon. Sensor anomaly detection is active."
   - Status row: green dot + "Detector connected" (if detector is up) or red dot + "Detector unreachable"
   - Footer: `v{version}` (assembly version number)
   - Light/dark scheme adapts to HA theme (CSS `prefers-color-scheme` media query)

3. **Static assets return HTTP 200** — In browser DevTools → Network tab, confirm:
   - `css/argus.css` returns HTTP 200 (not 404)
   - `js/htmx.min.js` returns HTTP 200 (not 404)
   - Note the actual request URLs — specifically record whether the Supervisor **strips** the `/api/hassio_ingress/{token}` prefix before forwarding to Kestrel. This closes the STACK-vs-PITFALLS conflict documented in 01-RESEARCH.md (Open Question 1 / Assumption A1).

4. **Kestrel bind address** — In the add-on container shell, run:
   ```
   ss -tlnp | grep 8099
   ```
   Confirm output shows `0.0.0.0:8099` (NOT `127.0.0.1:8099`). Confirm no stray `:5000` or `:5001` is bound.

5. **v2.0 services regression** — Restart the add-on. After it comes back:
   - Confirm `binary_sensor.argus_addon_health` reads healthy (OFF/problem=false)
   - Confirm the add-on log shows streaming/MQTT/health workers started (and batch, if InfluxDB configured)
   - This proves the SDK migration did not break any BackgroundService

6. **Empty-entities first-boot (optional regression)** — With an empty `entities` options list, confirm the log shows EventId 1003 warning (not a crash) and the UI still loads.

### What to Record in Follow-Up

After live verification, record in a follow-up commit or STATE.md update:
- Observed X-Ingress-Path / prefix-strip behavior: does the asset URL in DevTools show the full ingress prefix or just `css/argus.css`?
- Output of `ss -tlnp` (ports section)
- `binary_sensor.argus_addon_health` state after restart

---

## Threat Surface Scan

All STRIDE mitigations from the plan's threat model are implemented:

| Threat ID | Mitigation Status |
|-----------|------------------|
| T-01-04 | IMPLEMENTED — no `ports:` entry in config.yaml verified |
| T-01-05 | ACCEPTED (documented) — PathBase from header; port not exposed |
| T-01-06 | DEFERRED to Phase 4 (validate_session) — documented accepted risk |
| T-01-07 | IMPLEMENTED — UseStaticFiles after UseRouting; wwwroot only |
| T-01-08 | IMPLEMENTED — `WebUtility.HtmlEncode(ingressPath)` in PlaceholderPage.Build |

No new threat surface beyond what the plan's threat model covers.

---

## Known Stubs

None. The placeholder page is the intentional Phase 1 deliverable. All content is static server-rendered HTML with live detector status from `ArgusHealthSignals.DetectorConnected`. No placeholder data, no hardcoded empty values flowing to UI rendering.

---

## Self-Check: PASSED

- `orchestrator/Argus.Orchestrator/wwwroot/js/htmx.min.js` — FOUND (57391c4)
- `orchestrator/Argus.Orchestrator/wwwroot/css/argus.css` — FOUND (57391c4)
- `orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj` (Sdk.Web) — FOUND (03dc10a)
- `orchestrator/Argus.Orchestrator/Program.cs` (WebApplication.CreateBuilder) — FOUND (03dc10a)
- `orchestrator/Argus.Orchestrator/PlaceholderPage.cs` — FOUND (03dc10a)
- `argus/config.yaml` (ingress: true) — FOUND (f5a8c2c)
- Build: PASS | Tests: 117/117 PASS
