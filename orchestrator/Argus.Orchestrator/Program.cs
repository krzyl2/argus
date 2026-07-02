using Argus.Orchestrator;
using Argus.Orchestrator.Batch;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Health;
using Argus.Orchestrator.Logging;
using Argus.Orchestrator.Mqtt;
using Argus.Orchestrator.Web;
using Argus.Orchestrator.Workers;
using Grpc.Net.Client;
using InfluxDB.Client;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

var builder = WebApplication.CreateBuilder(args);

// Load entities.yaml (CONF-01/CONF-02)
var entitiesPath = builder.Configuration["ARGUS_ENTITIES_PATH"] ?? "entities.yaml";
var entitiesLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var entitiesLogger = entitiesLoggerFactory.CreateLogger<EntitiesConfigLoader>();
var entitiesConfig = EntitiesConfigLoader.Load(entitiesPath, entitiesLogger);
// CFG-04: wrap raw EntitiesConfig in ILiveEntitiesConfig singleton so all consumers
// read the current reference and react to ConfigChanged (Plan 03-02 DI migration).
var liveConfig = new LiveEntitiesConfig(entitiesConfig);
builder.Services.AddSingleton<ILiveEntitiesConfig>(liveConfig);

// Build one authoritative ConnectionSettings instance from environment (CONF-03, WR-06).
// Single AddSingleton registration — DI consumers receive this instance directly.
// (Removed duplicate Configure<ConnectionSettings> that never reached constructor-injected consumers.)
var connectionSettings = new ConnectionSettings
{
    HaUrl = builder.Configuration["ARGUS_HA_URL"],
    HaToken = builder.Configuration["ARGUS_HA_TOKEN"],
    MqttHost = builder.Configuration["ARGUS_MQTT_HOST"],
    MqttPort = int.TryParse(builder.Configuration["ARGUS_MQTT_PORT"], out var mqttPort) ? mqttPort : 1883,
    MqttUser = builder.Configuration["ARGUS_MQTT_USER"],
    MqttPassword = builder.Configuration["ARGUS_MQTT_PASSWORD"],
    DetectorEndpoint = builder.Configuration["ARGUS_DETECTOR_ENDPOINT"],
    TlsCa = builder.Configuration["ARGUS_TLS_CA"],
    TlsCert = builder.Configuration["ARGUS_TLS_CERT"],
    TlsKey = builder.Configuration["ARGUS_TLS_KEY"],
    EntitiesPath = entitiesPath,
    InfluxUrl = builder.Configuration["ARGUS_INFLUX_URL"],
    InfluxToken = builder.Configuration["ARGUS_INFLUX_TOKEN"],
    InfluxOrg = builder.Configuration["ARGUS_INFLUX_ORG"],
    InfluxBucket = builder.Configuration["ARGUS_INFLUX_BUCKET"],
    InfluxMeasurement = builder.Configuration["ARGUS_INFLUX_MEASUREMENT"] ?? "homeassistant",
    InfluxValueField = builder.Configuration["ARGUS_INFLUX_VALUE_FIELD"] ?? "value",
    BatchIntervalMinutes = int.TryParse(builder.Configuration["ARGUS_BATCH_INTERVAL_MIN"], out var bim) ? bim : 10,
    NightlyFitHour = int.TryParse(builder.Configuration["ARGUS_NIGHTLY_FIT_HOUR"], out var nfh) ? nfh : 2,
};
// WR-04: validate BatchIntervalMinutes — zero or negative causes a tight spin loop or crash
if (connectionSettings.BatchIntervalMinutes <= 0)
    throw new InvalidOperationException(
        $"ARGUS_BATCH_INTERVAL_MIN must be > 0, got {connectionSettings.BatchIntervalMinutes}");

// WR-05: validate NightlyFitHour — out-of-range silently disables nightly fit
if (connectionSettings.NightlyFitHour < 0 || connectionSettings.NightlyFitHour > 23)
    throw new InvalidOperationException(
        $"ARGUS_NIGHTLY_FIT_HOUR must be in [0, 23], got {connectionSettings.NightlyFitHour}");

builder.Services.AddSingleton(connectionSettings);

// Register the single mTLS GrpcChannel as a singleton (D-18 — one channel per process)
// Channel construction is deferred to first resolution so the logger is available
builder.Services.AddSingleton<GrpcChannel>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<DetectionGateway>>();
    return DetectorChannelFactory.Create(connectionSettings, logger);
});

// Register DetectionGateway (holds channel + stubs; INFRA-07 health gate)
builder.Services.AddSingleton<DetectionGateway>();

// HA connection is handled by HaWebSocketClient (raw WebSocket with the Supervisor-proxy
// Bearer header) inside NetDaemonHaEventSource — no NetDaemon.Client DI needed.

// Register ReconnectCooldown (60s post-reconnect binary_sensor suppression — D-07)
builder.Services.AddSingleton<ReconnectCooldown>();

// Register ArgusHealthSignals singleton (HEALTH-01): shared liveness flag between
// NetDaemonHaEventSource (writer) and HealthPublisherWorker (reader).
builder.Services.AddSingleton<ArgusHealthSignals>();

// Register sensor registry singleton (Plan 02-01): caches live numeric-sensor snapshot.
// Written by NetDaemonHaEventSource on every HA connect; read by Wave 2 HTTP handlers.
builder.Services.AddSingleton<IHaSensorRegistry, HaSensorRegistry>();

// Register HA event source (NetDaemon.Client WebSocket subscription — STRM-01/STRM-02)
// ArgusHealthSignals + IHaSensorRegistry are resolved automatically from DI.
builder.Services.AddSingleton<IHaEventSource, NetDaemonHaEventSource>();

// Register HA listener worker (consumes IHaEventSource after health gate)
builder.Services.AddHostedService<HaListenerWorker>();

// Register MQTT credential source (Plan 03-02 / SUPV-03):
// SupervisorMqttCredentialSource fetches fresh credentials on every connect/reconnect attempt.
// Uses SUPERVISOR_TOKEN env var when running as HA add-on; falls back to ARGUS_MQTT_* env vars
// for docker-compose / remote-detector deployments.
builder.Services.AddSingleton<IMqttCredentialSource>(sp =>
    new SupervisorMqttCredentialSource(
        new HttpClient(),
        connectionSettings,
        sp.GetRequiredService<ILogger<SupervisorMqttCredentialSource>>()));

// Register MQTT stack (Plan 07): MqttConnection (LWT), StatePublisher, MqttPublisherWorker
// DiscoveryPublisher is static — no DI registration needed
builder.Services.AddSingleton<MqttConnection>(sp =>
    new MqttConnection(
        sp.GetRequiredService<IMqttCredentialSource>(),
        sp.GetRequiredService<ILogger<MqttConnection>>()));
builder.Services.AddSingleton<StatePublisher>();
// IStatePublisher resolves to the same singleton StatePublisher (for ScoreStreamPipeline injection)
builder.Services.AddSingleton<IStatePublisher>(sp => sp.GetRequiredService<StatePublisher>());
builder.Services.AddHostedService<MqttPublisherWorker>();

// Register HealthPublisherWorker (HEALTH-01): publishes composite health entity to HA via MQTT
builder.Services.AddHostedService<HealthPublisherWorker>();

// Register ConfigFileWatcherService (Plan 04-03 / SC4): watches entitiesPath for atomic renames
// (ConfigWriter temp→rename + external edits) and reloads live config with 300ms debounce.
// ILiveEntitiesConfig and ConnectionSettings are already registered singletons above.
builder.Services.AddHostedService<ConfigFileWatcherService>();

// Register ScoreStreamPipeline (Plan 08): bidi ScoreStream loop with hysteresis/frozen/MQTT
builder.Services.AddSingleton<ScoreStreamPipeline>();

// Register ConfigWriter (Plan 02): atomic /data/entities.yaml write seam (temp-then-rename + SemaphoreSlim)
builder.Services.AddSingleton<Argus.Orchestrator.Config.ConfigWriter>();

// Register the InfluxDB batch path (Plan 02-02/04, BTCH-01/03) ONLY when an
// InfluxDB URL is configured. InfluxDBClient's ctor throws on an empty URL, and
// BatchSchedulerWorker (a hosted service) resolves it at startup — so with no
// InfluxDB configured the add-on must skip the batch path and run streaming-only
// rather than crash. config-gen writes ARGUS_INFLUX_URL="" when influx_url is unset.
if (!string.IsNullOrWhiteSpace(connectionSettings.InfluxUrl))
{
    // InfluxDBClient is a singleton; QueryApi obtained per-call inside InfluxDbReader
    builder.Services.AddSingleton<InfluxDBClient>(_ =>
        new InfluxDBClient(connectionSettings.InfluxUrl, connectionSettings.InfluxToken));
    builder.Services.AddSingleton<InfluxDbReader>();
    // IInfluxDataSource resolves to the same singleton InfluxDbReader (for BatchSchedulerWorker injection)
    builder.Services.AddSingleton<IInfluxDataSource>(sp => sp.GetRequiredService<InfluxDbReader>());

    // Register batch detector client adapter (wraps DetectionGateway for IBatchDetectorClient)
    builder.Services.AddSingleton<IBatchDetectorClient, BatchDetectorClientAdapter>();

    // Register BatchSchedulerWorker as hosted service (Plan 02-04 / BTCH-03)
    // Uses factory to inject DetectionGateway directly for INFRA-07 health gate
    builder.Services.AddHostedService<BatchSchedulerWorker>(sp => new BatchSchedulerWorker(
        sp.GetRequiredService<ConnectionSettings>(),
        sp.GetRequiredService<IInfluxDataSource>(),
        sp.GetRequiredService<IBatchDetectorClient>(),
        sp.GetRequiredService<IStatePublisher>(),
        sp.GetRequiredService<ILiveEntitiesConfig>(),
        sp.GetRequiredService<DetectionGateway>(),
        sp.GetRequiredService<ILogger<BatchSchedulerWorker>>()));
}
else
{
    // Use the startup logger (same factory as entitiesLogger) so this message obeys
    // log-level filtering and appears in structured logs alongside other startup info.
    var startupLogger = entitiesLoggerFactory.CreateLogger<Program>();
    startupLogger.LogInformation(
        "InfluxDB not configured (influx_url empty) — batch path disabled; running streaming-only.");
}

// Kestrel: bind 0.0.0.0:8099 — Supervisor connects from 172.30.32.2 (not loopback).
// ConfigureKestrel replaces the default localhost:5000/5001 endpoints. Do NOT use UseUrls.
builder.WebHost.ConfigureKestrel(opts =>
    opts.Listen(System.Net.IPAddress.Any, 8099));

var app = builder.Build();

// [1] X-Ingress-Path middleware — set PathBase per-request BEFORE UseRouting.
// This ensures ASP.NET LinkGenerator and static-file middleware generate correct
// absolute URLs relative to the Supervisor Ingress prefix.
// T-01-05: PathBase derived from request header; port is not exposed (T-01-04).
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Headers.TryGetValue("X-Ingress-Path", out var ingressPath))
    {
        var raw = ingressPath.ToString();
        // Accept only non-empty strings that look like absolute paths (no query, fragment, or null byte)
        if (!string.IsNullOrEmpty(raw) && raw.StartsWith('/') &&
            !raw.Contains('?') && !raw.Contains('#') && !raw.Contains('\0'))
        {
            ctx.Request.PathBase = new Microsoft.AspNetCore.Http.PathString(raw);
        }
    }
    await next();
});

// [2] Explicit UseRouting() — must follow PathBase middleware (converts WebApplication's
// auto-placement into a no-op per official minimal-API middleware ordering rules).
app.UseRouting();

// [3] Static files — serves wwwroot/ (htmx.min.js, argus.css) under correct PathBase.
// T-01-07: only committed wwwroot/ tree; no directory listing; no /data exposure.
app.UseStaticFiles();

// ── Phase-2 in-memory patterns holder ─────────────────────────────────────
// Keeps the last-saved raw patterns in memory so GET /sensors can pre-fill
// the textareas without re-reading entities.yaml on every page load.
// Initialized empty; updated on each successful POST /api/sensors/save.
// A fresh restart shows empty pattern boxes — acceptable (resolved entities preserved).
var lastIncludePatterns = "";
var lastExcludePatterns = "";

// ── Interim auth helper (Phase 2 — full validate_session deferred to Phase 4) ──
// Authorizes only connections from the Supervisor IP (172.30.32.2) or loopback.
// Uses RemoteIpAddress (real TCP peer, not spoofable X-Forwarded-For) — T-02-09.
//
// NOTE: X-Ingress-Path is NOT treated as an auth credential — any LAN peer can
// fabricate the header. The header is read separately (above) only to set PathBase.
// Full validate_session cookie-based auth is scheduled for Phase 4.
//
// Dev-only bypass: docker-compose.dev.yml sets ARGUS_DEV_TRUST_ALL_REQUESTS=true so the
// UI is reachable from a host browser (which arrives via the Docker bridge gateway, not
// loopback). NEVER set this in the add-on/production — it disables the Supervisor-IP guard.
var devTrustAllRequests = string.Equals(
    builder.Configuration["ARGUS_DEV_TRUST_ALL_REQUESTS"], "true", StringComparison.OrdinalIgnoreCase);

bool IsAuthorizedRequest(HttpContext ctx)
{
    if (devTrustAllRequests) return true;

    var remote = ctx.Connection.RemoteIpAddress;
    if (remote is null) return false;

    // Loopback: 127.0.0.1 (IPv4) or ::1 (IPv6) — for local dev
    if (System.Net.IPAddress.IsLoopback(remote)) return true;

    // Supervisor IP: 172.30.32.2 (add-on container network)
    if (remote.Equals(System.Net.IPAddress.Parse("172.30.32.2"))) return true;

    return false;
}

// [4] Root redirect — Phase 2 replaces placeholder page with entity picker
app.MapGet("/", () => Results.Redirect("sensors"));

// [5] GET /sensors — full entity picker page (UI-02 SC1)
// CFG-04: resolve ILiveEntitiesConfig and pass .Get() so the page always
// reflects the current entity set (not a ctor-captured stale reference).
app.MapGet("/sensors", (HttpRequest req, IHaSensorRegistry registry,
    ILiveEntitiesConfig liveCfg, ArgusHealthSignals health) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);

    var ip = req.Headers["X-Ingress-Path"].FirstOrDefault() ?? "";
    var q  = req.Query["q"].FirstOrDefault() ?? "";
    var html = EntityPickerPage.BuildFullPage(
        ip, registry, liveCfg.Get(), health, q,
        lastIncludePatterns, lastExcludePatterns);
    return Results.Content(html, "text/html");
});

// [6] GET /api/sensors — htmx list fragment (search refresh target)
// CFG-04: pass liveCfg.Get() so tracked entities keep their detector disclosure panels
// on htmx search refresh (not a captured stale EntitiesConfig — WR-01 fix).
app.MapGet("/api/sensors", (HttpRequest req, IHaSensorRegistry registry, ILiveEntitiesConfig liveCfg) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);

    var q = req.Query["q"].FirstOrDefault() ?? "";
    return Results.Content(
        EntityPickerPage.BuildListFragment(registry, liveCfg.Get(), q),
        "text/html");
});

// [6b] GET /api/detectors/new-entry — htmx fragment for "Add detector" button
// Returns a new .argus-detector-entry block with HST defaults at the given indices.
// T-03-12: same IsAuthorizedRequest guard as all endpoints (Phase 2 interim auth).
// T-03-14: entity_idx/det_idx are int.Parse'd; used only as name= indices — no file/DB access.
app.MapGet("/api/detectors/new-entry", (HttpRequest req) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);

    var entityIdxStr = req.Query["entity_idx"].FirstOrDefault() ?? "0";
    var detIdxStr    = req.Query["det_idx"].FirstOrDefault() ?? "0";

    if (!int.TryParse(entityIdxStr, out var ei)) ei = 0;
    if (!int.TryParse(detIdxStr, out var dj)) dj = 0;

    var fragment = EntityPickerPage.BuildDetectorEntry(
        ei, dj, new DetectorConfig { Name = "hst", Params = [] });

    return Results.Content(fragment, "text/html");
});

// [7] POST /api/sensors/save — expand patterns, write entities.yaml, create lock file,
//     parse detector fields, call ILiveEntitiesConfig.Swap (Phase 3 extension).
app.MapPost("/api/sensors/save", async (HttpRequest req, IHaSensorRegistry registry,
    Argus.Orchestrator.Config.ConfigWriter writer, ConnectionSettings settings,
    ILiveEntitiesConfig liveCfg, ILogger<Program> logger, CancellationToken ct) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);

    try
    {
        var form = await req.ReadFormAsync(ct);

        // Selected entity ids from checkboxes (may be empty — valid per Pitfall 5)
        var selectedIds = form["entities"]
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        // Split include/exclude textarea content by newline
        var includeRaw = form["include_patterns"].FirstOrDefault() ?? "";
        var excludeRaw = form["exclude_patterns"].FirstOrDefault() ?? "";
        var include = includeRaw.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var exclude = excludeRaw.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Resolve: GlobExpander.Resolve with selectedIds as manuallyChecked, [] as manuallyUnchecked
        // (the UI model: checkboxes ARE the manual selection — patterns feed the base set)
        var resolvedIds = GlobExpander.Resolve(
            registry.GetAll(), include, exclude,
            selectedIds.Where(s => s is not null).Select(s => s!), []);

        // Parse indexed detector form fields (Phase 3 — CFG-03)
        // detectors[{ei}][{di}][name] and detectors[{ei}][{di}][params][{key}]
        // {ei} correlates positionally to the sorted (alphabetical EntityId) resolved entity list.
        var formPairs = form.Keys
            .Select(k => new KeyValuePair<string, string>(k, form[k].FirstOrDefault() ?? string.Empty));
        var parsedDetectors = DetectorFieldParser.Parse(formPairs);

        // Phase 4 input validation gate (UI-04 / T-04-01–T-04-05):
        // Validate raw parsedDetectors BEFORE defaulting and BEFORE any write.
        // A tampered or malformed POST body must never reach disk or the live pipeline.
        var validationErrors = InputValidator.Validate(resolvedIds, parsedDetectors);
        if (validationErrors.Count > 0)
        {
            logger.LogWarning(LogEvents.UiValidationBlocked,
                "UI save blocked: {ErrorCount} validation error(s)", validationErrors.Count);
            return Results.Content(
                EntityPickerPage.BuildValidationBanner(validationErrors.Count),
                "text/html");
        }

        // Build EntityConfig list: sorted alphabetically by EntityId so ei=0 → first alpha
        var snapshotById = registry.GetAll()
            .ToDictionary(e => e.EntityId, StringComparer.OrdinalIgnoreCase);

        var sortedIds = resolvedIds
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var entities = sortedIds
            .Select((id, ei) =>
            {
                snapshotById.TryGetValue(id, out var entry);

                // Get detector list for this entity index; default to HST if empty (Pitfall 7 / CFG-03)
                var detectors = parsedDetectors.TryGetValue(ei, out var dets) && dets.Count > 0
                    ? dets
                    : [new DetectorConfig { Name = "hst", Params = [] }];

                return new EntityConfig
                {
                    EntityId = id,
                    FriendlyName = entry?.FriendlyName ?? "",
                    Detectors = detectors,
                };
            })
            .ToList();

        // Serialize BOTH entities and _patterns via a single YamlDotNet SerializerBuilder
        // (never string-format YAML — T-02-08 / CLAUDE.md rule)
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        // Build a single root dictionary: { _patterns: {...}, entities: [...] }
        // The _patterns key name starts with underscore — bypasses UnderscoredNamingConvention
        // conversion which only applies to PascalCase property names; use explicit key.
        var patternsMap = new Dictionary<string, object>
        {
            ["include"] = include.ToList(),
            ["exclude"] = exclude.ToList(),
        };

        // Use an ordered dictionary to ensure _patterns appears before entities
        var root = new Dictionary<string, object>
        {
            ["_patterns"] = patternsMap,
            ["entities"] = entities,
        };

        var fullYaml = serializer.Serialize(root);

        // Write atomically via ConfigWriter (temp-then-rename + SemaphoreSlim — T-02-10)
        var entitiesPath = settings.EntitiesPath ?? "/data/entities.yaml";
        await writer.WriteAsync(entitiesPath, fullYaml, ct);

        // Write lock file ONLY after a successful config write — guard for gen-entities.py (CFG-02).
        // Synchronous write: if WriteAsync succeeded, the lock must also be durable before we return.
        // Using async here would introduce a crash window between the two writes (WR-02).
        // Path.GetFullPath converts bare filenames (e.g. "entities.yaml") to absolute paths using
        // CWD, so GetDirectoryName never returns null or "" — lock lands in the same dir as the YAML.
        var entitiesDir = Path.GetDirectoryName(Path.GetFullPath(entitiesPath))
            ?? Path.GetTempPath(); // absolute fallback; GetFullPath never returns ""
        var lockPath = Path.Combine(entitiesDir, ".ui_config_present");
        File.WriteAllText(lockPath, string.Empty);

        // Phase 3: Re-read the written config and call ILiveEntitiesConfig.Swap.
        // Validate-before-Swap: EntitiesConfigLoader.Validate runs during Load; empty detector
        // lists are never written (defaulted to HST above) so Validate never throws — T-03-13.
        var newConfig = EntitiesConfigLoader.Load(entitiesPath, logger);
        liveCfg.Swap(newConfig);  // fires ConfigChanged → HaListenerWorker restart

        logger.LogInformation(LogEvents.UiSaveSuccess,
            "UI save succeeded: {EntityCount} entities written to {Path}", entities.Count, entitiesPath);

        // Update in-memory patterns holder for next GET /sensors pre-fill
        lastIncludePatterns = includeRaw;
        lastExcludePatterns = excludeRaw;

        // SC5: pass real hasHst so the ~4-min warm-up note renders when HST detectors are present.
        var hasHst = entities.Any(e => e.Detectors.Any(
            d => d.Name.Equals("hst", StringComparison.OrdinalIgnoreCase)));
        return Results.Content(EntityPickerPage.BuildSuccessBanner(entities.Count, hasHst), "text/html");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "UI save failed");  // Full exception to add-on log only (T-02-11)

        // Generic reason exposed to browser — no internal exception detail (T-02-11)
        var reason = ex is IOException ? "disk error" : "unexpected error";
        return Results.Content(EntityPickerPage.BuildErrorBanner(reason), "text/html");
    }
});

app.Run();
