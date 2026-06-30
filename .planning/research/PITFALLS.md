# Pitfalls Research

**Domain:** HA Ingress web UI added to an existing .NET 8 Worker-Service add-on (Argus v3.0)
**Researched:** 2026-06-30
**Confidence:** HIGH (Ingress mechanics, ASP.NET path handling); MEDIUM (reload race — no direct HA add-on .NET precedent found; pattern-derived from gRPC streaming design + .NET hosted-service literature)

---

## Critical Pitfalls

### Pitfall 1: X-Ingress-Path Breaks All Absolute Asset and API URLs

**What goes wrong:**
The HA Supervisor Ingress proxy routes requests through a dynamic, per-session URL of the form `/api/hassio_ingress/{token}/{path}`. The exact prefix changes every session. If any HTML page, JavaScript bundle, or API `fetch()` call uses an absolute URL starting with `/` (e.g., `<script src="/app.js">` or `fetch('/api/config')`), the browser resolves it against the HA host root — not against the ingress prefix — and gets a 404 or is refused by HA's own auth layer. The page loads but shows a blank UI, missing styles, or silent API failures.

This applies to:
- `<script src="/...">`, `<link href="/...">` in HTML
- `fetch('/api/...')` or `axios.get('/api/...')` calls that hardcode a leading `/`
- CSS `url('/images/...')` references
- Any redirect response from Kestrel that writes an absolute `Location:` header

**Why it happens:**
Ingress was designed for apps that already use relative paths (like ESPHome's web UI). The HA Supervisor provides the `X-Ingress-Path` header so the backend can reconstruct the base path, but many web frameworks default to generating absolute paths. A server-rendered HTML page using `<base href="/">` will break; one using `<base href="{{ ingress_path }}/">` (where `ingress_path` comes from the request header) will work. Developers who test with a direct port hit (not through ingress) never encounter the problem.

**How to avoid:**
Two layers of defence are required:

1. **ASP.NET/Kestrel layer — set PathBase from the header.** In the minimal-API setup, read `X-Ingress-Path` from the request and call `app.Use` to set `context.Request.PathBase` before routing. The correct middleware ordering is: `UsePathBase` middleware → `UseRouting()` (explicitly called, to override the automatic placement). Example:
   ```csharp
   app.Use(async (ctx, next) =>
   {
       if (ctx.Request.Headers.TryGetValue("X-Ingress-Path", out var ingressPath))
           ctx.Request.PathBase = new PathString(ingressPath.ToString());
       await next();
   });
   app.UseRouting(); // must come after the above, not before
   app.UseStaticFiles();
   app.MapGet("/api/config", ...);
   ```
   With PathBase set, `Link.GetPathByName()`, redirect helpers, and static-file middleware all prepend the correct prefix automatically. **Do not call `UsePathBase(staticValue)` — the prefix is dynamic per session.**

2. **HTML/frontend layer — use relative paths everywhere.** All `<script>`, `<link>`, `<img>`, and `fetch` calls must use relative paths (no leading `/`). Inject a `<base href="./"></base>` tag into every HTML page. If using a JS bundler, set `publicPath: './'` (Vite: `base: './'`).

Do not attempt to read `X-Ingress-Path` only in JavaScript; the header is not sent to the browser — it is a server-to-server header added by the Ingress proxy.

**Warning signs:**
- Browser DevTools Network tab shows 404 for `GET /app.js` where the URL lacks the `/api/hassio_ingress/...` prefix.
- The page renders but no CSS or JS loads.
- `fetch('/api/config')` returns a 401 from HA's own auth (not from Kestrel).
- Works fine when hitting the add-on port directly (`http://addon-hostname:8099/`) but breaks when opened via "Open Web UI" in HA.

**Phase to address:**
v3 Phase 1 (Ingress scaffolding / Kestrel wiring). The `X-Ingress-Path` middleware must be in place before any HTML or API endpoint is added. Test by opening the UI exclusively through the HA Ingress panel, never by direct port access.

---

### Pitfall 2: Kestrel Bound to Wrong Interface — Ingress Gets "502: Bad Gateway"

**What goes wrong:**
The HA Supervisor Ingress proxy, running at `172.30.32.2`, makes HTTP connections to the add-on container's internal IP (the container side of the add-on network bridge) on the `ingress_port`. If Kestrel binds to `127.0.0.1` (loopback only), the Supervisor cannot reach it from `172.30.32.2`. The HA UI shows a permanent "502: Bad Gateway" when clicking "Open Web UI". No error appears in the orchestrator logs because Kestrel never receives the connection.

The mirror mistake is to also open a `ports:` mapping in `config.yaml`, which would expose the web UI on the host network — bypassing auth entirely and creating an unauthenticated public endpoint on the add-on's host port.

**Why it happens:**
ASP.NET's `WebApplication.CreateBuilder` defaults to `http://localhost:5000` (loopback). Developers test locally with direct browser access (which works on loopback) and do not discover the Supervisor-reach problem until the add-on is installed on real HA OS. Adding a `ports:` entry to `config.yaml` is the quickest workaround but is the wrong fix — it creates a port visible to everything on the LAN.

Argus already has the correct pattern for the gRPC watchdog: binding the Python detector to `0.0.0.0` so the Supervisor TCP watchdog (`tcp://[HOST]:50051`) can probe it. The same principle applies to Kestrel.

**How to avoid:**
- Bind Kestrel to `0.0.0.0` on the `ingress_port` (e.g. 8099 or another chosen port):
  ```csharp
  builder.WebHost.ConfigureKestrel(opts =>
      opts.Listen(System.Net.IPAddress.Any, 8099));
  ```
  Or via environment: `ASPNETCORE_URLS=http://0.0.0.0:8099`.
- In `config.yaml`: set `ingress: true`, `ingress_port: 8099`. Do NOT add a `ports:` entry for 8099.
- Restrict to the Supervisor's IP at the application layer if security tightening is needed (allow only `172.30.32.2` in the middleware), but do not use OS-level bind restriction that would also block the Supervisor.

**Warning signs:**
- "Open Web UI" in HA shows a full-page "502: Bad Gateway" immediately.
- Orchestrator logs show no incoming HTTP request at all when the UI is opened.
- `ss -tlnp | grep 8099` inside the container shows `127.0.0.1:8099` (loopback-only).
- The gRPC detector watchdog passes (it binds `0.0.0.0`) but the UI fails.

**Phase to address:**
v3 Phase 1 (Ingress scaffolding). The bind address must be set and tested as part of the Kestrel startup, before any UI content is served. Acceptance criterion: "Open Web UI" must reach Kestrel in a real HA Supervisor environment.

---

### Pitfall 3: Host Builder Migration — `Host.CreateApplicationBuilder` Cannot Add Kestrel; Requires Switching to `WebApplication.CreateBuilder`

**What goes wrong:**
The current `Program.cs` uses `Host.CreateApplicationBuilder(args)` (a Generic Host, no web server). Adding ASP.NET minimal-API and Kestrel requires switching to `WebApplication.CreateBuilder(args)`. This is not additive — the two builder types have different APIs, different default service registrations, and produce different host types. Attempting to add `IWebHostBuilder` configuration to a Generic Host fails at compile time or produces an `InvalidOperationException` at startup.

If the migration is done naively (wholesale replacement), existing DI registrations, `AddHostedService<>` calls, and `AddSingleton<>` entries all carry over correctly, but ordering and startup sequencing change:

- `WebApplication` starts the HTTP server before calling `StartAsync` on hosted services. This means the `/api/config` endpoint is reachable before `HaListenerWorker` has connected to HA — API calls can arrive before the system is ready.
- The default Kestrel URLs (`http://localhost:5000` and `https://localhost:5001`) conflict if an unrelated port is already in use on the container.
- `ASPNETCORE_URLS` environment variable can override Kestrel endpoints system-wide, which is easy to accidentally set in the s6 environment and hard to debug.

**Why it happens:**
Generic Host (`Host.CreateApplicationBuilder`) is the right host for worker services with no HTTP. Moving to `WebApplication.CreateBuilder` is required to get Kestrel. The .NET template for "Worker Service" does not include web hosting; the "Web API" template does not include `AddHostedService`. Developers merging the two find that the builder swap is straightforward but that the application lifecycle ordering subtleties are not.

**How to avoid:**
- Replace `Host.CreateApplicationBuilder(args)` with `WebApplication.CreateBuilder(args)`. All `builder.Services.AddSingleton<>` and `builder.Services.AddHostedService<>` calls are identical in both builders — no changes to the DI registrations.
- Use `var app = builder.Build(); ... app.Run();` (instead of `var host = builder.Build(); host.Run();`). The `WebApplication` type is also an `IHost`.
- Explicitly configure Kestrel (see Pitfall 2) so the default `localhost:5000` is never used.
- Add a readiness gate to the API: the `/api/config` endpoint should return `503 Service Unavailable` (with a `Retry-After` header) until `HaListenerWorker` has emitted its first "healthy" signal. This prevents the UI from displaying a partially-initialized state.
- Do not set `ASPNETCORE_URLS` in the s6 environment — control the port only through `builder.WebHost.ConfigureKestrel(...)`.

**Warning signs:**
- Compile error: `'Host' does not contain a definition for 'ConfigureWebHostDefaults'`.
- Runtime error: `System.InvalidOperationException: Unable to resolve service for type 'IWebHostEnvironment'`.
- API endpoint returns data before `HaListenerWorker` has finished its HA health gate, leading to empty entity lists in the UI.
- Port 5000 conflict at startup when another service already binds that port.

**Phase to address:**
v3 Phase 1 (Kestrel / host builder migration). The builder swap is Phase 1 work item 1; the readiness gate is Phase 1 work item 2. Both must be completed before any UI feature work begins.

---

### Pitfall 4: Config Write Integrity — Corrupt `entities.yaml` and Schema Drift

**What goes wrong:**
Three sub-problems compose this pitfall:

**4a. Non-atomic writes corrupting the config file.** If the UI save handler does `File.WriteAllText("/data/entities.yaml", yaml)` and the orchestrator's `FileSystemWatcher` fires a reload exactly when the file is half-written, `EntitiesConfigLoader.Load()` reads a truncated YAML document, throws an exception, and the running configuration is lost. The pipeline may crash or silently revert to an empty entity list.

**4b. Concurrent access — UI save vs orchestrator read.** There is no locking between the HTTP handler writing the file and the `FileSystemWatcher` event reading it. On a dual-core Pi 4, these race on file handles. `YamlDotNet` deserialization does not tolerate partial reads.

**4c. Schema drift between the UI, config-gen, and `EntitiesConfigLoader`.** `gen-entities.py` generates YAML in the shape `EntitiesConfigLoader` expects. If the UI writes a richer JSON/YAML format that `EntitiesConfigLoader` does not understand (e.g., a new `threshold` field not in `DetectorConfig.Params`), and `EntitiesConfigLoader` uses `IgnoreUnmatchedProperties()`, the data is silently dropped. The UI shows the user's saved parameters; the orchestrator ignores them. This is a hard-to-detect regression.

**Why it happens:**
`EntitiesConfigLoader.Load()` currently reads the file once at startup as a static call. There is no existing reload path, no file lock, and no schema versioning. The `gen-entities.py` path and the planned UI save path are two independent writers to the same file. YamlDotNet's `IgnoreUnmatchedProperties()` makes it easy for the loader to silently swallow fields the UI adds.

**How to avoid:**

For write integrity (4a + 4b):
- Write to a temp file in `/data` (same filesystem), then `File.Move(tempPath, targetPath, overwrite: true)`. This is atomic on POSIX filesystems (rename syscall). The orchestrator always reads a complete file.
  ```csharp
  var tmp = Path.Combine(Path.GetDirectoryName(configPath)!, $".entities.tmp.{Guid.NewGuid():N}.yaml");
  await File.WriteAllTextAsync(tmp, yaml, ct);
  File.Move(tmp, configPath, overwrite: true); // atomic rename
  ```
- Use a `SemaphoreSlim(1)` in the API handler to serialize concurrent UI save requests.
- In the reload path, open the file with `FileShare.Read` and handle `IOException` (file locked) with a 200ms retry before re-throwing.

For schema drift (4c):
- Remove `IgnoreUnmatchedProperties()` from `EntitiesConfigLoader` once the schema is stable, or replace it with `StrictMode` and maintain a versioned schema.
- Add a schema version field to `entities.yaml` (e.g., `schema_version: 2`). The loader checks the version at startup and rejects files written by a newer UI than the orchestrator understands.
- The UI's YAML serializer must use the same property naming convention as `EntitiesConfigLoader` (`UnderscoredNamingConvention`). A mismatch (camelCase from a JS client-serialized object) will cause silent field drops.

**Warning signs:**
- Orchestrator logs show `YamlDotNet.Core.YamlException: mapping values are not allowed` after a UI save — classic partial-read.
- All entities disappear from MQTT after a UI save (config read as empty).
- UI shows detector parameters the user saved, but log shows default parameters being used.
- `ARGUS_ENTITIES_PATH` points to `/data/entities.yaml`; `gen-entities.py` also writes the same path at startup — if both run at container start during a reload, one clobbers the other.

**Phase to address:**
v3 Phase 1 (config model and write path) for the atomic write and semaphore. v3 Phase 3 (per-entity detector/parameter UI) for the schema-drift risk — that is when the YAML schema first gains new fields beyond the `gen-entities.py` baseline.

---

### Pitfall 5: Reload Races — Config Applied to Live ScoreStreamPipeline Drops or Duplicates Detectors

**What goes wrong:**
`ScoreStreamPipeline.RunAsync()` runs a long-lived fan-out loop keyed by `entity_id`. Each entity has its own bidirectional gRPC stream, per-entity channel (`Channel.CreateBounded<HaReading>(500)`), and `EntityRuntimeState` (hysteresis gate, warm-up counter, `FrozenSensorDetector`). A config reload must:

1. Discover added entities (open new streams, create new channels and states).
2. Discover removed entities (drain the channel, close the stream gracefully, unpublish MQTT discovery for orphaned entities).
3. Update parameters for unchanged entities (e.g., threshold changes) without resetting the warm-up counter or discarding the in-memory model state.

Problems that arise without explicit reload coordination:
- **Duplicate detectors**: If the reload naively restarts the entire `RunAsync` loop (by cancelling and restarting `HaListenerWorker`), all entities rebuild from scratch, resetting warm-up counters and losing the in-memory HST sliding window state.
- **Dropped readings**: Between cancelling the old pipeline and starting the new one, `HaListenerWorker` is still receiving WebSocket events. If those events are not buffered, they are lost. A `Channel<HaReading>` buffer of 500 at the worker level would help, but there is currently no such buffer between `NetDaemonHaEventSource` and the pipeline.
- **Orphaned MQTT entities**: If an entity is removed from config but its MQTT discovery topic is not retracted (by publishing an empty payload to the discovery topic), the HA entity persists indefinitely in "unavailable" state. Users see stale entities they cannot delete without restarting HA.
- **Model state loss**: `ScoreStreamPipeline.BuildEntityStates()` creates a fresh `EntityRuntimeState` for each entity. River's HST window state lives only in the Python detector process — it survives a reload because the detector is not restarted. But `HysteresisGate` and `FrozenSensorDetector` state live in `EntityRuntimeState` on the .NET side and are reset on every pipeline rebuild.

**Why it happens:**
The current pipeline is designed for startup-only configuration (config read once, pipeline runs until shutdown). There is no partial-reload path. The simplest reload implementation (cancel + restart `HaListenerWorker`) is safe for correctness but breaks warm-up and hysteresis continuity.

**How to avoid:**

For the v3.0 milestone, recommend the **restart-on-save** strategy as the minimal viable approach:
- On UI save, write the new `entities.yaml`, then post a reload signal (e.g., send `SIGHUP` to the orchestrator process, or set a shared `CancellationTokenSource`).
- The orchestrator restarts only `HaListenerWorker` and `ScoreStreamPipeline`. All other services (MQTT, HealthPublisher, BatchScheduler) continue uninterrupted.
- Accept the model state reset cost. For HST with `window=250`, the warm-up period is typically 250 readings ≈ 4 minutes at one reading/second per entity. Document this in the UI as "changes apply within ~5 minutes".
- Retract MQTT discovery for removed entities: before restarting the pipeline, compare the old and new entity lists and publish empty payloads to the discovery topics of removed entities.

Defer the fully incremental reload (add/remove entities without resetting state) to a later phase. It requires refactoring `ScoreStreamPipeline` to accept a live `IDelta<EntityConfig>` and is significantly more complex.

**Warning signs:**
- After a UI save adding a new entity, the HA entity for the new sensor never appears (pipeline not reloaded).
- After a UI save removing an entity, its `binary_sensor` stays "unavailable" in HA forever (MQTT discovery not retracted).
- After any reload, all sensors report normal for ~4 minutes (warm-up reset) and anomaly detection resumes — operator mistakes this for a bug.
- Two pipeline instances running simultaneously, producing duplicate MQTT publishes for the same entity.

**Phase to address:**
v3 Phase 4 (reload-without-restart / CFG-04). The restart-on-save approach is acceptable for Phase 4; incremental reload is a future milestone item. MQTT discovery retraction for removed entities must be implemented as part of Phase 4.

---

## High-Risk Pitfalls

### Pitfall 6: Image Bloat from Node.js Build Steps Pushing Past 2 GB

**What goes wrong:**
Adding a JavaScript frontend (Vite, a small React/Preact SPA, or even vanilla JS bundled with esbuild) introduces a Node.js build step. Common mistakes:

- Including `node_modules` in the final Docker layer (typically 200–500 MB of dev dependencies that are not needed at runtime).
- Using a single-stage Dockerfile where `npm install` (all deps including devDeps) is run in the same stage as the `dotnet publish` and Python pip install steps — all layers end up in the final image.
- Caching `npm install` in a layer above `COPY detector/requirements.txt` — the pip layer is large, and if the npm layer invalidates it, CI rebuild times double.
- Using `node:latest` (1+ GB) as the build stage base instead of `node:20-slim` (~200 MB).

The current add-on image already contains `.NET 8 runtime` (~120 MB) + `Python 3.11 + ML deps` (~900 MB uncompressed). Adding an unbundled Node.js build environment pushes past 2 GB, violating the DOCS-02 budget.

**How to avoid:**
Use a multi-stage Dockerfile. The Node.js build stage produces static assets only; those assets are `COPY --from=builder` into the final image as plain files (no Node.js runtime needed at runtime):

```dockerfile
# Stage 1: JS build (never reaches the final image)
FROM node:20-slim AS ui-builder
WORKDIR /ui
COPY ui/package.json ui/package-lock.json ./
RUN npm ci --prefer-offline
COPY ui/ ./
RUN npm run build  # produces /ui/dist/

# Stage 2: final image (existing Dockerfile content)
FROM ${BUILD_FROM}
...
COPY --from=ui-builder /ui/dist/ /opt/argus/orchestrator/wwwroot/
```

This keeps Node.js and `node_modules` entirely out of the final image. The `wwwroot/` folder is then served by Kestrel's `UseStaticFiles()` middleware.

Alternative: generate server-rendered HTML at compile time using a lightweight static site generator or a simple Python script — no JavaScript framework at all. For a config UI with ~3 pages, plain HTML + a few hundred lines of vanilla JS (no bundler) may be the better tradeoff: no Node.js build step, no bundle size concerns, no framework upgrade churn.

**Warning signs:**
- `docker build` output shows a `RUN npm install` or `RUN npm ci` step that takes >2 minutes in the final stage (not a builder stage).
- `docker image inspect <image> | jq '.[0].Size'` returns more than 2 GB.
- `docker history <image>` shows a layer of >200 MB from an npm step.
- CI build for aarch64 exceeds 30 minutes.

**Phase to address:**
v3 Phase 1 (UI technology decision — open question Q1 in REQUIREMENTS.md). If a JS framework is chosen, the multi-stage Dockerfile must be established before any feature content is added. The CI image-size gate (fail if >2 GB) must be added in the same phase.

---

### Pitfall 7: Kestrel Running Alongside s6 BackgroundServices — Port Binding and Graceful Shutdown

**What goes wrong:**
Two specific problems arise from Kestrel coexisting with s6 and existing `BackgroundService` workers:

**7a. Port conflict with gRPC watchdog.** The existing `watchdog: "tcp://[HOST]:50051"` in `config.yaml` monitors the gRPC port. Adding an HTTP port (8099) does not conflict with 50051, but if Kestrel's default ports (5000, 5001) are not explicitly suppressed, they bind in addition to 8099. Two of these ports may collide with other add-ons or the HA Supervisor's own services.

**7b. Graceful shutdown ordering under s6.** When s6 sends `SIGTERM` to the orchestrator process, the .NET Generic Host (and `WebApplication`) registers a SIGTERM handler that calls `IHost.StopAsync()`. The shutdown sequence is: Kestrel stops accepting new requests → BackgroundService.StopAsync() is called for each hosted service in reverse registration order → host exits. The default shutdown timeout is 30 seconds in .NET 8.

Problems:
- If `HaListenerWorker.ExecuteAsync` does not observe `stoppingToken` promptly (it is blocked on `_scoreStreamPipeline.RunAsync()` which is awaiting gRPC reads), the worker hangs until the gRPC call is cancelled by the underlying cancellation. If the gRPC stream cancellation takes >30s (network partition), .NET forcefully aborts and s6 may log a false "service crashed" exit.
- s6's `S6_KILL_GRACETIME` (default 5000ms) may fire before .NET's shutdown completes, sending SIGKILL. Set `S6_KILL_GRACETIME` high enough (e.g., 10000ms / 10 seconds) to let .NET drain gracefully.
- The s6 `finish` script for the orchestrator service currently calls `/run/s6/basedir/bin/halt` (correct for v3). No change is needed for the Kestrel addition — the halt call terminates the entire container, not just the process.

**How to avoid:**
- Suppress all default Kestrel endpoints: use `builder.WebHost.ConfigureKestrel(opts => opts.Listen(IPAddress.Any, 8099))` and set `ASPNETCORE_URLS` to an empty string or explicitly override it to prevent the default localhost:5000 from also binding.
- Set `builder.WebHost.UseUrls(string.Empty)` before `ConfigureKestrel` to clear the default URL list.
- Ensure `HaListenerWorker.ExecuteAsync` propagates `stoppingToken` all the way into the gRPC call. In `ScoreStreamPipeline.RunAsync`, the fan-out task and per-entity tasks all receive `ct` — this is already correct. Verify `NetDaemonHaEventSource.ReadAllAsync(stoppingToken)` also propagates cancellation (it should end the channel on cancellation).
- Add `ShutdownTimeout = TimeSpan.FromSeconds(15)` to the host options to give BackgroundServices enough time to drain.
- Set `ENV S6_KILL_GRACETIME=10000` in the Dockerfile.

**Warning signs:**
- `netstat -tlnp` inside the container shows both `0.0.0.0:5000` and `0.0.0.0:8099` bound (double binding).
- s6 logs show orchestrator exit code 137 (SIGKILL) rather than a clean exit during add-on stop.
- "Open Web UI" causes a 502 immediately after add-on start (Kestrel not yet bound when the UI is clicked).
- HaListenerWorker takes >10 seconds to stop after SIGTERM.

**Phase to address:**
v3 Phase 1 (host builder migration and Kestrel wiring). The shutdown timeout and `S6_KILL_GRACETIME` settings should be validated in Phase 1 as part of the "start/stop add-on cleanly" acceptance criterion.

---

### Pitfall 8: `gen-entities.py` Startup Path Collides With UI-Written Config

**What goes wrong:**
Currently, `10-config-gen.sh` runs `gen-entities.py` at every container start, unconditionally overwriting `/data/entities.yaml` with content derived from `options.json` (the HA add-on options form). After v3.0 ships, the UI will be the authoritative writer of `/data/entities.yaml`. On the next add-on restart or update, `gen-entities.py` runs again and overwrites the UI-saved config with the dumb `options.json` entity list (all with `hst` defaults, no per-entity detector parameters). All user configuration from the UI is silently erased.

**Why it happens:**
`gen-entities.py` was designed as the only config source. It does not know whether the user has ever opened the UI. The two config-writing paths (startup script and UI save) have no coordination.

**How to avoid:**
Make `gen-entities.py` conditional on whether a UI-authored config already exists:
```python
if os.path.exists("/data/entities.yaml") and is_ui_authored("/data/entities.yaml"):
    # UI config present — skip overwrite; only validate it is readable
    sys.exit(0)
else:
    # First boot or no UI config — generate from options.json
    write_from_options()
```
Add a marker field to UI-authored YAML (e.g., `_source: ui`) that `gen-entities.py` checks. Alternatively, write a separate lock file (`/data/.ui_config_present`) on first UI save.

Also: the v3.0 UI should allow importing from `options.json` on first open (so the user does not have to re-enter entities already configured in the add-on options), but after the first UI save, `options.json` entity list is treated as a migration source only.

**Warning signs:**
- User saves complex per-entity detector parameters in the UI; after an add-on restart or OTA update, all entities revert to `hst` with default params.
- Log at startup shows `Config-gen complete` (which means gen-entities.py ran) followed by the orchestrator loading fewer detectors than the user configured.
- `git diff /data/entities.yaml` (mentally) between pre-restart and post-restart shows all `params:` blocks reverted to `{}`.

**Phase to address:**
v3 Phase 1 or Phase 2. The conditional gen-entities.py check must be in place before any user can save config via the UI (Phase 2). If it is not present at Phase 2, the first user to save config and then restart will lose their work.

---

## Moderate Pitfalls

### Pitfall 9: Auth Assumption — Ingress Does NOT Re-Verify HA Session for Individual API Requests

**What goes wrong:**
HA Ingress authenticates at the WebSocket/HTTP session level using a token embedded in the `X-Ingress-Path` URL. Individual subsequent requests within that session are proxied without re-authentication. This means:
- An API endpoint like `POST /api/config/save` is reachable by anyone who holds the ingress session token.
- The token is per-user and tied to a HA session, so it is not publicly guessable. However, if a logged-in HA user is malicious or the token leaks, the API is exploitable.
- Argus is single-operator (no multi-user concern), but this is still a design assumption to document: the UI does not need to implement its own auth, but it also cannot rely on per-request auth from the proxy.

**How to avoid:**
For Argus (single-operator, self-hosted), no additional auth is needed beyond the HA session. Do NOT add a separate auth layer — it would require session management outside HA and defeats the purpose of Ingress.

Do NOT expose the ingress port via `ports:` in `config.yaml`. The "no separately exposed port" constraint in UI-01 is the correct security boundary.

Document this assumption explicitly in the phase acceptance criteria so future contributors do not add unnecessary API keys or JWT middleware.

**Warning signs:**
- `ports:` entry added to `config.yaml` for the ingress port — this is a security regression.
- Middleware added to Kestrel that returns 401 for missing `Authorization` header — breaks the ingress flow since the proxy does not add that header.

**Phase to address:**
v3 Phase 1 (Kestrel wiring). Document the auth model in the code as a comment. Verify the `ports:` entry is absent from `config.yaml`.

---

### Pitfall 10: Static File Serving — `UseStaticFiles` Ignores PathBase Without Explicit Configuration

**What goes wrong:**
ASP.NET's `UseStaticFiles()` middleware serves files from `wwwroot/` by default, using the request path after stripping `PathBase`. If `PathBase` is set correctly from `X-Ingress-Path` (Pitfall 1), static file URLs like `./app.js` (relative) resolve correctly. However, if the HTML page uses a `<base href="./"></base>` tag and the JS code then attempts to `fetch('api/config')` (relative, no leading slash), the browser resolves it relative to the page URL which already includes the ingress prefix — this works. But if JS uses `fetch('./api/config')`, the browser may strip the last path segment — this is a subtle difference that causes 404s only for one-segment-deep routes.

Additionally, `UseStaticFiles()` must be called AFTER the PathBase middleware but BEFORE `UseRouting()` if static files should not go through route matching.

**How to avoid:**
- Use `app.UseStaticFiles(new StaticFileOptions { RequestPath = "" })` after the PathBase middleware.
- Standardize all JS `fetch` calls to use `fetch('api/config')` (no leading dot-slash) when the page is at the root of the ingress base path.
- Add a smoke test that loads the HTML page and confirms CSS and JS assets return HTTP 200 through the Ingress URL (not direct port access).

**Phase to address:**
v3 Phase 1 (Kestrel + static files wiring).

---

### Pitfall 11: `FileSystemWatcher` Double-Fire on YAML Write

**What goes wrong:**
When the UI save handler writes `/data/entities.yaml`, many editors and file-write implementations emit multiple `Changed` events for a single logical write (e.g., one for the truncate, one for the content write, one for the metadata flush). If the reload handler triggers on each event, `EntitiesConfigLoader.Load()` is called three times within 50ms. If the atomic-rename pattern (Pitfall 4) is used correctly, only one `Renamed` event fires (for the rename of the temp file to the target), but `FileSystemWatcher` can still emit spurious duplicates.

**How to avoid:**
- Debounce the watcher callback: use a `System.Threading.Timer` that resets on each event and fires the actual reload 300ms after the last event.
- Use `FileSystemWatcher` on the `Renamed` event type (from the temp file to target) rather than `Changed` — this fires once per atomic write.
- Track a sequence number or file modification timestamp; skip reload if the file has not actually changed since last reload.

**Phase to address:**
v3 Phase 4 (reload-without-restart).

---

## Technical Debt Patterns

| Shortcut | Immediate Benefit | Long-term Cost | When Acceptable |
|----------|-------------------|----------------|-----------------|
| Restart entire add-on on config save | Simple reload logic | 4–5 min detection gap per reload; user loses warm-up state | Acceptable for Phase 4 MVP; document in UI |
| `IgnoreUnmatchedProperties()` on YAML loader | No loader churn when adding fields | Schema drift: UI writes fields loader silently drops | Only during initial schema exploration; remove before ship |
| Non-atomic `File.WriteAllText` for config save | Two lines of code | Corrupt config on partial write during concurrent reload | Never — always use atomic rename |
| Single-stage Dockerfile with Node.js build tools | Simpler Dockerfile | +500 MB–1 GB image bloat from `node_modules` | Never if image budget is 2 GB |
| `UseStaticFiles()` before PathBase middleware | Default middleware order | Static file 404s through Ingress path | Never — PathBase must come first |
| `ports:` entry in config.yaml alongside `ingress: true` | Direct browser debug access | Unauthenticated public endpoint on host network | Never in production; use only in local dev override |

---

## Integration Gotchas

| Integration | Common Mistake | Correct Approach |
|-------------|----------------|------------------|
| HA Ingress proxy | Absolute URLs in HTML/JS (`/api/config`) | All URLs relative; PathBase middleware strips prefix server-side |
| HA Ingress proxy | Reading `X-Ingress-Path` in JS (client-side) | Header is server-to-server; read it in Kestrel middleware only |
| ASP.NET PathBase + WebApplication | Call `UsePathBase` after `UseRouting` | Call explicit `UseRouting()` after `UsePathBase` middleware; do not rely on automatic placement |
| Kestrel default URLs | `ASPNETCORE_URLS=http://localhost:5000` | Override to `http://0.0.0.0:8099`; suppress the default |
| `gen-entities.py` + UI save | gen-entities.py overwrites UI config on restart | Guard gen-entities.py with `_source: ui` marker or lock file |
| `/data/entities.yaml` writes | `File.WriteAllText` directly to target path | Atomic rename: write to `.tmp` then `File.Move(tmp, target, overwrite: true)` |
| FileSystemWatcher for reload | Trigger on every `Changed` event | Debounce 300ms; watch `Renamed` event for atomic renames |
| ScoreStreamPipeline reload | Cancel + restart entire pipeline | Restart only `HaListenerWorker`; retract MQTT discovery for removed entities |

---

## Performance Traps

| Trap | Symptoms | Prevention | When It Breaks |
|------|----------|------------|----------------|
| Node.js dev deps in final image | Image >2 GB; 20+ min pull on RPi | Multi-stage build; `COPY --from=ui-builder /dist/ /wwwroot/` | Day 1 of first JS build |
| No debounce on FileSystemWatcher | Three rapid config reloads per UI save | 300ms debounce timer | Any time UI save fires (every save) |
| Kestrel on default port 5000 + 8099 | Two ports bound; unexpected port conflict | Suppress defaults; explicit `ConfigureKestrel` | At first `WebApplication.CreateBuilder` migration |
| Config save resets all EntityRuntimeState | 4 min anomaly-detection gap after every UI save | Document; defer incremental reload to future milestone | Every config save by user |
| Restart-on-save drops in-flight readings | Readings from HA during restart window lost | Buffer at HaListenerWorker level (Channel<HaReading>); tolerate a few missed events | During a save at high event rate (>1 event/sec per entity) |

---

## "Looks Done But Isn't" Checklist

- [ ] **X-Ingress-Path middleware**: Open the UI exclusively via "Open Web UI" in HA, never via direct port hit. Confirm all CSS, JS, and API calls return 200 with correct content.
- [ ] **Kestrel bind address**: `ss -tlnp | grep 8099` inside the container shows `0.0.0.0:8099`, not `127.0.0.1:8099`. No port 5000 or 5001 binding visible.
- [ ] **No `ports:` entry**: `config.yaml` has `ingress: true` and `ingress_port: 8099` but no `ports:` entry for 8099.
- [ ] **Atomic write**: Simultaneously trigger a UI save and a `FileSystemWatcher` reload event; verify the config file is never read in a partial state.
- [ ] **gen-entities.py conditional**: Save complex per-entity config in UI; restart the add-on; confirm the UI-authored config survives intact.
- [ ] **MQTT discovery retraction**: Remove an entity via the UI; confirm its `binary_sensor` is removed from HA (not left "unavailable") within 30 seconds.
- [ ] **Shutdown timing**: Stop the add-on from HA UI; confirm s6 logs a clean exit (not exit code 137 / SIGKILL).
- [ ] **Image size**: `docker image inspect <image> | jq '.[0].Size'` confirms total size < 2 GB after adding UI assets and JS build stage.
- [ ] **Schema round-trip**: Save an entity with non-default detector parameters via the UI; confirm the orchestrator logs those exact parameters at next startup.
- [ ] **Readiness gate**: Open the UI within 5 seconds of add-on start (before `HaListenerWorker` connects to HA); confirm the API returns a clear "not ready" response, not stale or empty data.

---

## Recovery Strategies

| Pitfall | Recovery Cost | Recovery Steps |
|---------|---------------|----------------|
| Absolute URLs breaking Ingress (Pitfall 1) | MEDIUM | Fix PathBase middleware + relative paths in HTML/JS; rebuild image; no data loss |
| 502 Bad Gateway / wrong bind address (Pitfall 2) | LOW | Change Kestrel bind to `0.0.0.0`; rebuild image; no data loss |
| Host builder migration breaks DI (Pitfall 3) | MEDIUM | Migrate to `WebApplication.CreateBuilder`; re-run all existing integration tests |
| Corrupt entities.yaml from non-atomic write (Pitfall 4a) | LOW | Restore from backup in `/data`; implement atomic rename before next release |
| Schema drift: UI fields silently dropped (Pitfall 4c) | MEDIUM | Add schema version; align `EntitiesConfigLoader` and UI serializer; users must re-save config |
| Pipeline duplicate detectors after reload (Pitfall 5) | MEDIUM | Implement pipeline restart gate; no data loss but 4-min detection gap on correction |
| gen-entities.py wipes UI config on restart (Pitfall 8) | HIGH (user data) | Add conditional check immediately; users must re-enter config manually for this occurrence |
| Image >2 GB (Pitfall 6) | LOW | Add multi-stage build; rebuild; re-push; no functional change |

---

## Pitfall-to-Phase Mapping

| Pitfall | Prevention Phase | Verification |
|---------|------------------|--------------|
| X-Ingress-Path / absolute URLs (P1) | v3 Phase 1 | Open UI only via HA Ingress panel; verify all assets load |
| Kestrel wrong bind address (P2) | v3 Phase 1 | `ss -tlnp` inside container; HA Supervisor "Open Web UI" succeeds |
| Host builder migration (P3) | v3 Phase 1 | All existing BackgroundService integration tests pass after builder swap |
| Config write integrity / schema drift (P4) | v3 Phase 1 (atomic write), v3 Phase 3 (schema) | Concurrent write+read test; schema round-trip test |
| Reload race / pipeline restart (P5) | v3 Phase 4 | Save config; verify pipeline restarts; MQTT retraction for removed entities |
| Image bloat / Node.js build (P6) | v3 Phase 1 (UI tech decision) | CI image-size gate: fail if >2 GB |
| Kestrel + s6 shutdown ordering (P7) | v3 Phase 1 | Stop add-on from HA UI; confirm clean exit; no SIGKILL |
| gen-entities.py overwrite (P8) | v3 Phase 2 (before first UI save) | Restart after UI save; UI config survives |
| Auth assumption / no extra ports (P9) | v3 Phase 1 | `config.yaml` has no `ports:` for ingress port; Ingress auth verified |
| UseStaticFiles + PathBase ordering (P10) | v3 Phase 1 | Static assets load through Ingress path |
| FileSystemWatcher double-fire (P11) | v3 Phase 4 | Confirm single reload per UI save via log timestamps |

---

## Sources

- [HA Add-on Presentation / Ingress — developers.home-assistant.io](https://developers.home-assistant.io/docs/add-ons/presentation)
- [HA Add-on Configuration — ingress, ingress_port, ingress_stream — developers.home-assistant.io](https://developers.home-assistant.io/docs/add-ons/configuration)
- [HA Supervisor Ingress Proxy mechanics — deepwiki.com/home-assistant/supervisor/6.3-proxy-and-ingress](https://deepwiki.com/home-assistant/supervisor/6.3-proxy-and-ingress)
- [How to use X-Ingress-Path in an add-on — community.home-assistant.io](https://community.home-assistant.io/t/how-to-use-x-ingress-path-in-an-add-on/276905)
- [How to handle absolute paths with HA Ingress — community.home-assistant.io](https://community.home-assistant.io/t/how-to-handle-absolute-paths-with-ha-ingress/370572)
- [Using PathBase with .NET 6 WebApplicationBuilder — andrewlock.net](https://andrewlock.net/using-pathbase-with-dotnet-6-webapplicationbuilder/)
- [Understanding PathBase in ASP.NET Core — andrewlock.net](https://andrewlock.net/understanding-pathbase-in-aspnetcore/)
- [Configure ASP.NET Core for proxy servers and load balancers — learn.microsoft.com](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer)
- [Add Web API Controllers to a Worker Service — medium.com/@adinas](https://medium.com/@adinas/add-webapi-controllers-to-a-worker-service-baabd838dac2)
- [Extending graceful shutdown timeout for IHostedService — andrewlock.net](https://andrewlock.net/extending-the-shutdown-timeout-setting-to-ensure-graceful-ihostedservice-shutdown/)
- [Concurrent hosted service start/stop in .NET 8 — stevejgordon.co.uk](https://www.stevejgordon.co.uk/concurrent-hosted-service-start-and-stop-in-dotnet-8)
- [Addon Ingress community discussion — community.home-assistant.io](https://community.home-assistant.io/t/addon-ingress/936226)
- [502 Bad Gateway Ingress error pattern — community.home-assistant.io (multiple threads)](https://community.home-assistant.io/t/502-bad-gateway-ingress-error/265775)
- [Docker Multi-Stage Builds — iximiuz.com](https://labs.iximiuz.com/tutorials/docker-multi-stage-builds)
- [FileSystemWatcher debounce — gist.github.com/cocowalla](https://gist.github.com/cocowalla/5d181b82b9a986c6761585000901d1b8)
- [Avoiding file concurrency with FileSystemWatcher — intertech.com](https://www.intertech.com/avoiding-file-concurrency-using-system-io-filesystemwatcher/)
- [s6-overlay graceful shutdown issues — github.com/just-containers/s6-overlay/issues/337](https://github.com/just-containers/s6-overlay/issues/337)

---
*Pitfalls research for: HA Ingress web UI + .NET Kestrel + live config reload (Argus v3.0)*
*Researched: 2026-06-30*
