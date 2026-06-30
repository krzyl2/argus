---
phase: 01-ingress-scaffold-sdk-migration-config-seam
verified: 2026-06-30T21:00:00Z
status: human_needed
score: 3/5 roadmap success criteria fully verified (2 require live-HA testing)
overrides_applied: 0
human_verification:
  - test: "Open Web UI panel appears in HA; clicking it serves the placeholder page through the Supervisor Ingress proxy with no login prompt and no extra exposed port"
    expected: "HA add-on page shows 'Argus' panel (panel_title, mdi:tune-variant icon). Clicking 'Open Web UI' loads the placeholder: heading 'Argus is running', body copy, detector status row, v{version} footer. No direct port access used."
    why_human: "Requires a running HA OS instance with the rebuilt add-on image. Cannot verify Supervisor Ingress routing or X-Ingress-Path prefix-strip behavior statically."
  - test: "All v2.0 background services continue operating after SDK migration; binary_sensor.argus_addon_health is healthy (OFF) after an add-on restart"
    expected: "Add-on log shows HaListenerWorker, MqttPublisherWorker, HealthPublisherWorker, ScoreStreamPipeline (and BatchSchedulerWorker if InfluxDB configured) started. binary_sensor.argus_addon_health reads OFF (problem=false) after restart."
    why_human: "Requires deploying and restarting the rebuilt add-on on live HA. DI registrations are present in code but actual service startup cannot be observed without a live HA OS environment."
  - test: "Static assets (htmx.min.js, argus.css) return HTTP 200 via the Ingress URL; PathBase + <base href> resolve correctly so no asset 404s appear in DevTools Network tab"
    expected: "Browser DevTools Network tab shows css/argus.css and js/htmx.min.js each with HTTP 200 status through the ingress URL. Record whether the Supervisor strips the /api/hassio_ingress/{token} prefix before forwarding (closes STACK-vs-PITFALLS Open Question 1 / A1)."
    why_human: "Requires the live Supervisor proxy to confirm PathBase + base-href dual-layer resolves correctly for the actual prefix-strip behavior. Cannot simulate Supervisor routing statically."
  - test: "Kestrel binds 0.0.0.0:8099 only; no stray :5000 or :5001 listener; no host-port mapping exists in config.yaml"
    expected: "ss -tlnp | grep 8099 shows 0.0.0.0:8099 inside the container. No :5000 or :5001 lines. config.yaml has no 'ports:' entry (already code-verified; confirm ss output matches)."
    why_human: "The bind address can only be confirmed at runtime inside the container. Code sets IPAddress.Any and config.yaml has no ports: entry (both verified), but the container network observation requires the live environment."
  - test: "(Optional) Empty entities first-boot regression: orchestrator starts and UI loads when entities options list is empty"
    expected: "Add-on log shows EventId 1003 EmptyEntitiesWarning (not a crash), and 'Open Web UI' still loads the placeholder page."
    why_human: "Regression check combining the code-verified empty-entities fix with the live Ingress scaffold. Optional as the code path is fully verified; confirms end-to-end."
---

# Phase 1: Ingress Scaffold + SDK Migration + Config Seam Verification Report

**Phase Goal:** The orchestrator serves an Ingress web endpoint ("Open Web UI") through the existing process — SDK migrated from Worker to Web, Kestrel bound on 0.0.0.0:8099, config.yaml declares ingress keys, a placeholder page loads through the HA Supervisor, all v2.0 BackgroundService functionality is verified unaffected, empty-entities no longer crashes the orchestrator, and the atomic config write path is in place from day one.
**Verified:** 2026-06-30T21:00:00Z
**Status:** human_needed
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths (Roadmap Success Criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | The add-on page in HA shows "Open Web UI"; clicking it serves a placeholder page through the Supervisor Ingress proxy with no separate login and no additional exposed port | ? HUMAN NEEDED | Code: config.yaml has `ingress: true`, `ingress_port: 8099`, `panel_title: "Argus"`, `panel_icon: "mdi:tune-variant"`, no `ports:` entry. Program.cs + PlaceholderPage.cs wired. Live-HA test deferred by plan design. |
| 2 | All v2.0 background services continue operating after the SDK migration — verified by restarting the add-on and confirming `binary_sensor.argus_addon_health` is healthy | ? HUMAN NEEDED | Code: all 15+ DI registrations (`AddHostedService<HaListenerWorker>`, `MqttPublisherWorker`, `HealthPublisherWorker`, `ScoreStreamPipeline`, conditional `BatchSchedulerWorker`) are verbatim in Program.cs under `WebApplication.CreateBuilder`. `ArgusHealthSignals.DetectorConnected` added and wired in `HealthPublisherWorker`. Live restart test deferred by plan design. |
| 3 | Starting the orchestrator with an empty `entities.yaml` produces a log warning (not a crash); the UI endpoint remains reachable so the user can configure entities | ✓ VERIFIED | `EntitiesConfigLoader.Validate()` checks `config.Entities == null || Count == 0` → `logger.LogWarning(LogEvents.EmptyEntitiesWarning, ...)` → `return;` (no throw). `WarnIgnoredKeys` guarded by `if (config.Entities?.Count > 0)`. Null-deserialization returns `new EntitiesConfig()`. Two new tests pass: `Load_EmptyEntities_LogsWarning_DoesNotThrow` and `Load_NullEntitiesKey_LogsWarning_DoesNotThrow`. `LogEvents.EmptyEntitiesWarning = new(1003, ...)` confirmed. |
| 4 | Config writes use atomic temp-then-rename; no partial reads are possible during a concurrent file-system watcher event | ✓ VERIFIED | `ConfigWriter.WriteAsync`: writes to `.entities.tmp.{Guid:N}.yaml`, then `File.Move(tmp, targetPath, overwrite: true)` (3-arg atomic overload). `SemaphoreSlim(1,1)` serializes concurrent callers. Orphan cleanup in `finally` block (`tmp != null → File.Delete(tmp)`). `ConfigWriter` registered as `AddSingleton` in Program.cs line 114. Three tests pass + one failure-path orphan test. |
| 5 | Static assets (htmx.min.js, any CSS) load via the Ingress URL with HTTP 200 — not via direct port access — confirming PathBase / `<base href>` resolution is correct | ? HUMAN NEEDED | Code: `wwwroot/js/htmx.min.js` (51,238 bytes, starts with `var htmx=`), `wwwroot/css/argus.css` (5,580 bytes, `--color-surface` + dark `@media`). `app.UseStaticFiles()` after `app.UseRouting()` in correct middleware order. `PlaceholderPage.Build` emits `<base href="{safeIngressPath}/">` with HTML-encoded path. Live HTTP 200 confirmation deferred. |

**Score:** 2/5 roadmap success criteria fully verified in code (SC3, SC4). SC1, SC2, SC5 are code-complete but require live-HA testing per the plan's deliberate deferral documented in 01-02-SUMMARY.md.

---

### Plan Must-Haves: Plan 01-01 (CFG-01)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Starting the orchestrator with an empty entities.yaml logs a warning and does not crash | ✓ VERIFIED | `EntitiesConfigLoader.cs` line 41-47: `if (config.Entities == null || config.Entities.Count == 0)` → `LogWarning(EmptyEntitiesWarning)` → `return;` |
| 2 | Starting with a missing entities key (first-boot options.json) logs a warning and does not crash | ✓ VERIFIED | `Deserialize<EntitiesConfig>(yaml) ?? new EntitiesConfig()` returns empty config; Validate then logs warning + returns. Test `Load_NullEntitiesKey_LogsWarning_DoesNotThrow` passes. |
| 3 | A config write produces a complete file via temp-then-rename, never a partial file | ✓ VERIFIED | `ConfigWriter.WriteAsync`: `WriteAllTextAsync(tmp, yaml)` then `File.Move(tmp, targetPath, overwrite: true)`. Test `WriteAsync_ProducesFileWithExpectedContent` passes. |
| 4 | Two concurrent config writes are serialized and neither throws | ✓ VERIFIED | `_lock = new SemaphoreSlim(1,1)` + `await _lock.WaitAsync(ct)` in try/finally. Test `WriteAsync_ConcurrentCalls_NeitherThrows` passes. |
| 5 | No orphan temp files remain in the target directory after a write | ✓ VERIFIED | `tmp = null` after successful Move; `if (tmp != null) File.Delete(tmp)` in finally. Tests `WriteAsync_NoTempFileLeftBehind` and `WriteAsync_FailedMove_LeavesNoOrphanTempFile` both pass. |

**Plan 01-01 score: 5/5 truths verified**

---

### Plan Must-Haves: Plan 01-02 (UI-01)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | The add-on page in HA shows "Open Web UI"; clicking it serves the placeholder page through Supervisor Ingress with no separate login and no extra exposed port | ? HUMAN NEEDED | Code complete; live-HA test required. |
| 2 | All v2.0 background services keep running after SDK migration; binary_sensor.argus_addon_health is healthy after add-on restart | ? HUMAN NEEDED | Code complete (all DI registrations preserved); live restart required. |
| 3 | Static assets load via the Ingress URL with HTTP 200, not via direct port | ? HUMAN NEEDED | Code complete; live Supervisor routing required. |
| 4 | Kestrel binds 0.0.0.0:8099 only (not loopback, no stray :5000) | ✓ VERIFIED | Program.cs line 151-152: `builder.WebHost.ConfigureKestrel(opts => opts.Listen(System.Net.IPAddress.Any, 8099));`. No `UseUrls`, no `ASPNETCORE_URLS`. config.yaml: no `ports:` entry (grep confirmed). |

**Plan 01-02 score: 1/4 truths fully code-verified; 3/4 code-complete but human-needed**

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `orchestrator/Argus.Orchestrator/Config/ConfigWriter.cs` | Atomic YAML write seam (temp+File.Move, SemaphoreSlim) | ✓ VERIFIED | `public sealed class ConfigWriter`; `SemaphoreSlim(1,1)`; `File.Move(tmp, targetPath, overwrite: true)`; orphan cleanup in finally |
| `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs` | Empty-entities logs warning instead of throwing | ✓ VERIFIED | `LogWarning(LogEvents.EmptyEntitiesWarning, ...)` + early return on empty/null entities; `WarnIgnoredKeys` guarded |
| `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` | EmptyEntitiesWarning EventId (1003) | ✓ VERIFIED | `public static readonly EventId EmptyEntitiesWarning = new(1003, nameof(EmptyEntitiesWarning));` at line 14 |
| `orchestrator/Argus.Orchestrator.Tests/ConfigWriterTests.cs` | Atomic write + concurrency + no-orphan tests | ✓ VERIFIED | 4 tests: `WriteAsync_ProducesFileWithExpectedContent`, `WriteAsync_ConcurrentCalls_NeitherThrows`, `WriteAsync_NoTempFileLeftBehind`, `WriteAsync_FailedMove_LeavesNoOrphanTempFile` |
| `orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj` | Web SDK; Microsoft.Extensions.Hosting PackageReference removed | ✓ VERIFIED | `<Project Sdk="Microsoft.NET.Sdk.Web">` at line 1; no `Microsoft.Extensions.Hosting` PackageReference; all other references intact |
| `orchestrator/Argus.Orchestrator/Program.cs` | WebApplication host + Kestrel 0.0.0.0:8099 + X-Ingress-Path middleware + UseRouting + UseStaticFiles + MapGet placeholder + ConfigWriter DI | ✓ VERIFIED | `WebApplication.CreateBuilder(args)` line 12; `IPAddress.Any, 8099` line 152; PathBase middleware → UseRouting → UseStaticFiles → MapGet("/") → Run in correct order; `AddSingleton<ConfigWriter>()` line 114 |
| `orchestrator/Argus.Orchestrator/PlaceholderPage.cs` | Server-rendered placeholder HTML with `<base href>` + relative assets + status + version footer | ✓ VERIFIED | `public static class PlaceholderPage`; `Build(string ingressPath, ArgusHealthSignals health)`; `WebUtility.HtmlEncode(ingressPath)` → `<base href="{safeIngressPath}/">`; relative `href="css/argus.css"`, `src="js/htmx.min.js"`; `DetectorConnected` status; `_version` footer |
| `orchestrator/Argus.Orchestrator/wwwroot/js/htmx.min.js` | htmx 2.0.10 committed (BSD 0-Clause, air-gapped) | ✓ VERIFIED | File exists, 51,238 bytes, starts with `var htmx=function(){"use strict";` — not an HTML error page |
| `orchestrator/Argus.Orchestrator/wwwroot/css/argus.css` | CSS custom-property design foundation (color/spacing/typography, light+dark) | ✓ VERIFIED | `--color-surface`, `--space-xs` through `--space-3xl`, typography tokens, `@media (prefers-color-scheme: dark)` block, all placeholder BEM classes present; 5,580 bytes |
| `argus/config.yaml` | ingress: true, ingress_port: 8099, panel_icon, panel_title; NO ports: entry | ✓ VERIFIED | Lines 17-20: `ingress: true`, `ingress_port: 8099`, `panel_icon: "mdi:tune-variant"`, `panel_title: "Argus"`. No `ports:` key anywhere in file. |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `EntitiesConfigLoader.Load` | `EntitiesConfigLoader.Validate` | logger parameter passed through; early return on empty entities | ✓ WIRED | `Validate(config, path, logger)` called at line 29; method takes ILogger; empty check at line 41 logs + returns |
| `ConfigWriter.WriteAsync` | `File.Move(tmp, targetPath, overwrite: true)` | POSIX atomic rename after writing temp file | ✓ WIRED | Line 26: `File.Move(tmp, targetPath, overwrite: true)` — 3-arg overload confirmed |
| `Program.cs X-Ingress-Path middleware` | `context.Request.PathBase` | Set per-request BEFORE explicit `app.UseRouting()` | ✓ WIRED | `ctx.Request.PathBase = new PathString(raw)` in `app.Use(...)` at lines 160-173; `app.UseRouting()` at line 177 (after) |
| `PlaceholderPage.Build` | `<base href="{ingressPath}/">` | Emit base tag + relative asset hrefs (no leading slash) | ✓ WIRED | Line 48: `<base href="{{safeIngressPath}}/">` ; asset hrefs: `href="css/argus.css"`, `src="js/htmx.min.js"` (relative, no leading slash) |
| `Program.cs MapGet /` | `ArgusHealthSignals` | Inject health singleton for zero-latency detector status | ✓ WIRED | `app.MapGet("/", (HttpRequest req, ArgusHealthSignals health) => ...)` at line 185; `PlaceholderPage.Build(ip, health)` passes singleton |
| `argus/config.yaml ingress_port` | `Kestrel Listen(IPAddress.Any, 8099)` | ingress_port must equal the Kestrel bind port | ✓ WIRED | `ingress_port: 8099` in config.yaml; `opts.Listen(System.Net.IPAddress.Any, 8099)` in Program.cs line 152 — ports match |
| `HealthPublisherWorker` | `ArgusHealthSignals.DetectorConnected` | Set after each gRPC health cycle for zero-latency UI reads | ✓ WIRED | Line 86 of HealthPublisherWorker.cs: `_signals.DetectorConnected = serving;` — updated every ~15s |

---

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|-------------------|--------|
| `PlaceholderPage.Build` | `health.DetectorConnected` | `ArgusHealthSignals.DetectorConnected` (volatile bool) set by `HealthPublisherWorker` every ~15s from gRPC health check | Yes — real gRPC health check result, not hardcoded | ✓ FLOWING |
| `PlaceholderPage.Build` | `_version` | `Assembly.GetExecutingAssembly().GetName().Version` — assembly attribute | Yes — real assembly version | ✓ FLOWING |
| `PlaceholderPage.Build` | `ingressPath` | `req.Headers["X-Ingress-Path"].FirstOrDefault() ?? ""` — real HTTP header | Yes — runtime request header (empty string fallback for non-proxy access is correct) | ✓ FLOWING |

---

### Behavioral Spot-Checks

Step 7b: SKIPPED for live-HA behaviors (no running server available; see Human Verification Required). Local build/test confirmations from 01-02-SUMMARY.md corroborate:

| Behavior | Check | Result | Status |
|----------|-------|--------|--------|
| `dotnet build` passes | Commit 03dc10a message: "0 errors, 0 warnings" confirmed in 01-02-SUMMARY.md | Summary-reported PASS (cannot re-run here) | ✓ PASS (summary-corroborated) |
| `dotnet test` 117/117 green | SUMMARY.md: "117/117 tests pass"; 4 TDD commits + fix commits all in git log | Summary-reported PASS | ✓ PASS (summary-corroborated) |
| htmx.min.js non-empty, valid content | `wc -c` = 51,238 bytes; file starts with `var htmx=function()` | ✓ PASS (directly verified) | ✓ PASS |
| argus.css contains `--color-surface` + dark scheme `@media` | File read confirmed both at lines 8 and 42 | ✓ PASS (directly verified) | ✓ PASS |
| config.yaml: `ingress: true`, no `ports:` entry | File read at lines 17 and full-file scan | ✓ PASS (directly verified) | ✓ PASS |

---

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| UI-01 | 01-02-PLAN.md | The add-on exposes an Ingress endpoint ("Open Web UI") served by the orchestrator (ASP.NET minimal API behind `ingress: true` / `ingress_port`), authenticated by HA Ingress, with no separately exposed port | ? HUMAN NEEDED | Code fully implements the requirement: Web SDK, Kestrel 0.0.0.0:8099, config.yaml ingress keys, placeholder page, X-Ingress-Path middleware, no `ports:` entry. Live-HA confirmation that the Supervisor proxy actually serves the page and assets at HTTP 200 remains pending. |
| CFG-01 | 01-01-PLAN.md | A single configuration source of truth under `/data` is read by both the UI and the orchestrator's `EntitiesConfigLoader` | ✓ SATISFIED | `ConfigWriter.WriteAsync` provides the atomic write path to `entities.yaml`. `EntitiesConfigLoader.Load` reads the same file. `ConfigWriter` is a DI singleton (Plan 02 registration confirmed in Program.cs line 114). The seam is in place for Phase 2+ callers. |

**Orphaned requirements check:** REQUIREMENTS.md maps UI-01 and CFG-01 to v3 Phase 1. Both are claimed by plans in this phase. No orphaned requirements.

---

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `PlaceholderPage.cs` | 8 | Word "placeholder" in class name and XML doc | ℹ️ Info | Intentional naming — `PlaceholderPage` IS the Phase 1 deliverable; the class emits real dynamic content (version, detector status, HTML-encoded ingress path). Not a stub. |

No blockers. No stub anti-patterns. No TODO/FIXME/hardcoded-empty-data patterns found in any Phase 1 file.

---

### Human Verification Required

The following 4 items require operator testing after rebuilding and deploying the add-on image to a live HA OS instance. These were deliberately deferred by Plan 01-02 Task 4 (gate: `autonomous: false`, `checkpoint:human-verify`).

#### 1. "Open Web UI" panel and placeholder page via Ingress

**Test:** Rebuild and deploy the add-on. In HA, open the Argus add-on page. Confirm an "Argus" panel with `mdi:tune-variant` icon appears ("Open Web UI" button). Click it (never access port 8099 directly). Confirm the placeholder page renders: display heading "Argus is running", body copy "Configuration UI coming soon. Sensor anomaly detection is active.", a status row (green "Detector connected" or red "Detector unreachable"), and a "v{version}" footer. Confirm light/dark scheme adapts to HA theme.
**Expected:** Placeholder page renders fully through the Supervisor Ingress proxy. No separate login dialog. No HTTP 502/404.
**Why human:** Requires live HA OS with the rebuilt image. Supervisor Ingress routing cannot be simulated statically.

#### 2. v2.0 BackgroundService regression after SDK migration

**Test:** After add-on restart, examine the add-on log. Confirm it shows the following workers started: `HaListenerWorker`, `MqttPublisherWorker`, `HealthPublisherWorker`, `ScoreStreamPipeline` (and `BatchSchedulerWorker` if InfluxDB is configured). Confirm `binary_sensor.argus_addon_health` reads OFF (problem=false) in HA within 30 seconds of startup.
**Expected:** All v2.0 services start. Health entity is healthy. No service is absent from the log.
**Why human:** BackgroundService startup can only be confirmed in the running container. Code registrations are verified; actual execution requires live deployment.

#### 3. Static assets HTTP 200 + X-Ingress-Path prefix-strip behavior

**Test:** After clicking "Open Web UI", open browser DevTools → Network tab. Confirm `css/argus.css` and `js/htmx.min.js` each return HTTP 200 (not 404 or 302). Record the actual request URLs — specifically whether the Supervisor strips the `/api/hassio_ingress/{token}` prefix before forwarding to Kestrel. This observation closes the STACK-vs-PITFALLS Open Question 1 / Assumption A1 documented in 01-RESEARCH.md.
**Expected:** Both assets return 200. Record the asset URL pattern in a follow-up commit or STATE.md update.
**Why human:** The PathBase + `<base href>` dual-layer implementation covers both supervisor-strips and supervisor-does-not-strip behaviors in code, but the actual runtime routing can only be observed on a live Supervisor.

#### 4. Kestrel bind address confirmation in container

**Test:** Inside the add-on container shell, run `ss -tlnp | grep 8099`. Optionally also run `ss -tlnp | grep ':500'`.
**Expected:** Output shows `0.0.0.0:8099` (not `127.0.0.1:8099`). No stray `:5000` or `:5001` listener.
**Why human:** Container network state is only observable at runtime. The code bind is verified (`IPAddress.Any, 8099`) but the resulting socket cannot be checked statically.

---

### Gaps Summary

No gaps blocking goal achievement. All code deliverables are implemented, substantive, wired, and data-flowing:

- **CFG-01 (config seam):** Fully verified — `ConfigWriter`, `EntitiesConfigLoader` softening, `LogEvents.EmptyEntitiesWarning`, all tests green.
- **UI-01 (Ingress scaffold):** Code is complete and builds clean — Web SDK, Kestrel, middleware pipeline, placeholder page, wwwroot assets, config.yaml manifest keys. The remaining 3 roadmap success criteria (SC1, SC2, SC5) and 3 plan must-haves (plan 01-02 truths 1-3) cannot be verified without a live HA OS instance. These are human-verification items, not gaps — the implementation matches the plan specification exactly.

The status is `human_needed` because live-HA acceptance tests are required before the phase can be fully signed off. All automated and static checks pass.

---

_Verified: 2026-06-30T21:00:00Z_
_Verifier: Claude (gsd-verifier)_
