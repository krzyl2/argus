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
// D-L: one-shot schema_version 2 migration, BEFORE the first Load. It runs here — ahead of DI,
// ahead of the HA snapshot — because every later reader must already see the migrated shape;
// the cost is that no unit_of_measurement is available for D-I, which the migrator says out loud.
//
// The pre-migration entity list is captured first and, if (and only if) a migration actually
// happened, handed to MqttPublisherWorker so it can retract the retained discovery configs
// those entities published under the OLD detector-scoped unique_id (D-G). Without that, HA
// keeps a second, orphaned entity per sensor fed by the same argus/{slug}/flag/state topic.
var preMigrationEntities = TryReadEntitiesQuietly(entitiesPath);
var didMigrate = EntitiesSchemaMigrator.MigrateIfNeeded(entitiesPath, entitiesLogger);
builder.Services.AddSingleton(didMigrate
    ? new LegacyDiscoveryRetraction(preMigrationEntities)
    : LegacyDiscoveryRetraction.None);

var entitiesConfig = EntitiesConfigLoader.Load(entitiesPath, entitiesLogger);

// Best-effort read used ONLY to reconstruct old discovery ids. A config too broken to load is
// not a startup failure here — EntitiesConfigLoader.Load below is the real gate, and it fails
// loud on its own terms rather than as a confusing error from a retraction helper.
static IReadOnlyList<EntityConfig> TryReadEntitiesQuietly(string path)
{
    try
    {
        if (!File.Exists(path)) return Array.Empty<EntityConfig>();
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<EntitiesConfig>(File.ReadAllText(path))?.Entities
            ?? (IReadOnlyList<EntityConfig>)Array.Empty<EntityConfig>();
    }
    catch
    {
        return Array.Empty<EntityConfig>();
    }
}
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
    // D-15: a bad/absent backfill value must degrade, not fail startup — no throw-on-invalid
    // guard like BatchIntervalMinutes/NightlyFitHour get below.
    BackfillEnabled = !bool.TryParse(builder.Configuration["ARGUS_BACKFILL_ENABLED"], out var backfillEnabled) || backfillEnabled,
    BackfillLookback = builder.Configuration["ARGUS_BACKFILL_LOOKBACK"] ?? "8d",
    // §7 #12: the gRPC receive limit is nowhere configured, so the clamp is the only thing
    // between an operator-raised cap and RESOURCE_EXHAUSTED on the Warmup call.
    BackfillRowCap = Math.Clamp(
        int.TryParse(builder.Configuration["ARGUS_BACKFILL_ROW_CAP"], out var brc) ? brc : 5000, 1, 20000),
    // WS4/F10: same D-15 rule — a garbage value degrades to the default instead of killing
    // startup, and the clamp keeps the post-connect settle delay bounded (0 = pre-WS4 behaviour).
    RegistrySettleSeconds = Math.Clamp(
        int.TryParse(builder.Configuration["ARGUS_REGISTRY_SETTLE_SEC"], out var rss) ? rss : 60, 0, 600),
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

// Register group status cache singleton (GRP-09/08-02): last joint-mode verdict + sorted
// contributions, written by BatchSchedulerWorker's joint branch, read by
// GET /api/groups/{id}/status.
builder.Services.AddSingleton<IGroupStatusCache, GroupStatusCache>();

// Register entity status cache singleton (QUICK-warmup-status): last per-entity warm-up
// snapshot, written by ScoreStreamPipeline's write loop, read by GET /api/sensors so the
// SPA can show live HST warm-up progress.
builder.Services.AddSingleton<IEntityStatusCache, EntityStatusCache>();

// Register recent-anomalies ring buffer + last-batch-run tracker (QUICK-dashboard-real-data):
// written by ScoreStreamPipeline (streaming) and BatchSchedulerWorker (joint-group batch),
// read by GET /api/anomalies/recent and GET /api/health. Registered unconditionally — the
// streaming path records anomalies regardless of InfluxDB, and IBatchRunStatus.LastRunUtc
// stays null when the batch worker never runs.
builder.Services.AddSingleton<IRecentAnomaliesCache, RecentAnomaliesCache>();
builder.Services.AddSingleton<IBatchRunStatus, BatchRunStatus>();

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

// WS2: process-lifetime home for per-entity AlertPolicy instances. Registered as a singleton
// (not owned by ScoreStreamPipeline) precisely because HaListenerWorker rebuilds every
// EntityRuntimeState on each config Save — without this, one unrelated Save would restart
// calibration on every entity.
builder.Services.AddSingleton<AlertStateStore>();

// Register ScoreStreamPipeline (Plan 08; Phase 15-03 backfill deps): bidi ScoreStream loop
// with hysteresis/frozen/MQTT. Explicit factory (not a bare AddSingleton<T>()) because
// IInfluxDataSource is registered in one of the two branches below (InfluxDbReader when
// influx_url is set, HaRecorderHistorySource otherwise), THIS registration runs before both,
// and the class has two constructors — an explicit factory removes all constructor-selection
// ambiguity. GetService (not GetRequiredService) still guards the optional deps, but WS5
// changed what null means: IInfluxDataSource is now ALWAYS registered, so it resolves to null
// only when neither branch ran. D-15's degrade path is unchanged and still exercised by
// BackfillEnabled=false and by a source that returns zero rows.
builder.Services.AddSingleton<ScoreStreamPipeline>(sp => new ScoreStreamPipeline(
    sp.GetRequiredService<IStatePublisher>(),
    sp.GetRequiredService<ILogger<ScoreStreamPipeline>>(),
    sp.GetRequiredService<ILiveEntitiesConfig>(),
    sp.GetRequiredService<DetectionGateway>(),
    sp.GetService<IEntityStatusCache>(),
    sp.GetService<IRecentAnomaliesCache>(),
    sp.GetService<IInfluxDataSource>(),
    sp.GetService<IBatchDetectorClient>(),
    sp.GetRequiredService<ConnectionSettings>(),
    sp.GetRequiredService<AlertStateStore>()));

// D-K: the Warmup client is NOT part of the InfluxDB branch. Backfill priming needs it on every
// deployment — registering it inside the Influx branch is what made HaRecorderHistorySource
// insufficient on its own: PrimeFromHistoryAsync no-ops when the detector client is null, so an
// influx_url-less install would have had a history source and still never primed anything.
builder.Services.AddSingleton<IBatchDetectorClient, BatchDetectorClientAdapter>();

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

    // Register the group Influx source (Phase 6 / GRP-02) — reuses the already-registered
    // singleton InfluxDBClient via GroupInfluxReader's production ctor; no second client.
    builder.Services.AddSingleton<GroupInfluxReader>();
    builder.Services.AddSingleton<IGroupInfluxDataSource>(sp => sp.GetRequiredService<GroupInfluxReader>());

    // Register BatchSchedulerWorker as hosted service (Plan 02-04 / BTCH-03)
    // Uses factory to inject DetectionGateway directly for INFRA-07 health gate
    builder.Services.AddHostedService<BatchSchedulerWorker>(sp => new BatchSchedulerWorker(
        sp.GetRequiredService<ConnectionSettings>(),
        sp.GetRequiredService<IInfluxDataSource>(),
        sp.GetRequiredService<IBatchDetectorClient>(),
        sp.GetRequiredService<IStatePublisher>(),
        sp.GetRequiredService<ILiveEntitiesConfig>(),
        sp.GetRequiredService<IGroupInfluxDataSource>(),
        sp.GetRequiredService<DetectionGateway>(),
        sp.GetRequiredService<ILogger<BatchSchedulerWorker>>(),
        sp.GetRequiredService<IGroupStatusCache>(),
        sp.GetRequiredService<IRecentAnomaliesCache>(),
        sp.GetRequiredService<IBatchRunStatus>()));
}
else
{
    // Use the startup logger (same factory as entitiesLogger) so this message obeys
    // log-level filtering and appears in structured logs alongside other startup info.
    var startupLogger = entitiesLoggerFactory.CreateLogger<Program>();
    startupLogger.LogInformation(
        "InfluxDB not configured (influx_url empty) — batch path disabled; running streaming-only.");

    // WS5/D-K/F11: the HA Recorder is the only history source on this deployment. Registering it
    // here is what makes backfill priming reachable at all when influx_url is empty — before this,
    // GetService<IInfluxDataSource>() returned null and PrimeFromHistoryAsync was dead code.
    builder.Services.AddHaRecorderHistorySource();
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

// [3] Static files — serves wwwroot/ (Vite SPA build output) under correct PathBase.
// T-01-07: only committed wwwroot/ tree; no directory listing; no /data exposure.
app.UseStaticFiles();

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

// [4] GET /api/sensors — JSON sensor list (SPA fetch target, replaces htmx HTML fragment)
// CFG-04: pass liveCfg.Get() so friendlyName/isTracked reflect the current config, not a
// captured stale EntitiesConfig reference (WR-01 fix carried forward).
app.MapGet("/api/sensors", (HttpRequest req, IHaSensorRegistry registry, ILiveEntitiesConfig liveCfg, IEntityStatusCache statusCache) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);

    var q = req.Query["q"].FirstOrDefault() ?? "";
    var entries = registry.GetFiltered(q);

    // G-14-1 fix #2: derive isTracked from the live config (always fresh after a save's Swap),
    // not the HA registry snapshot (e.IsTracked), which only refreshes on an HA WebSocket
    // reconnect and is not reconciled by Swap — see SensorTracking.cs.
    var trackedIds = SensorTracking.TrackedIds(liveCfg.Get());

    // D-N: the saved detector list must round-trip to the editor. Built ONCE, outside the
    // Select — a per-row lookup over liveCfg.Entities would be O(n*m) across ~400 entities.
    // Without this the editor seeds a fresh default block on every load and the first Save
    // from ANY screen (including the pattern textareas in Settings) writes those defaults back
    // over every tracked sensor — i.e. it silently undoes the migration.
    var configuredById = liveCfg.Get().Entities
        .GroupBy(x => x.EntityId, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    var payload = entries.Select(e =>
    {
        // Friendly name: only surfaced when present and differs from entity_id (exact v3.0 rule)
        var showFriendlyName = !string.IsNullOrEmpty(e.FriendlyName) &&
            !string.Equals(e.FriendlyName, e.EntityId, StringComparison.Ordinal);

        var tracked = trackedIds.Contains(e.EntityId);
        // QUICK-warmup-status: warm-up status is surfaced for tracked entities only — null
        // (never populated) for untracked, and null for a tracked entity that has not yet
        // received its first reading (pipeline hasn't scored it — acceptable MVP behavior).
        var status = tracked ? statusCache.Get(e.EntityId) : null;

        configuredById.TryGetValue(e.EntityId, out var configured);

        return new
        {
            entityId = e.EntityId,
            friendlyName = showFriendlyName ? e.FriendlyName : null,
            currentValue = e.CurrentValue.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
            unitOfMeasurement = e.UnitOfMeasurement,
            isTracked = tracked,
            areaName = e.AreaName,
            domain = e.Domain,
            // D-N: name + params exactly as stored, so the editor hydrates from disk.
            detectors = tracked && configured is not null
                ? configured.Detectors.Select(d => new { name = d.Name, @params = d.Params }).ToList()
                : null,
            // D-E/F6-2: the calibrated band in the SENSOR'S OWN units, so one dimensionless
            // threshold reads differently (and correctly) on every sensor. Null until the first
            // verdict — the UI must degrade to "calibrating", never invent a band.
            calibratedExpected = status?.CalibratedExpected,
            calibratedLower = status?.CalibratedLower,
            calibratedUpper = status?.CalibratedUpper,
            medianIntervalSec = status?.MedianIntervalSec,
            warmedUp = status?.WarmedUp,
            readingCount = status?.ReadingCount,
            warmUpWindow = status?.WarmUpWindow,
            // WS2 (A14): alert-layer calibration/state. Additive JSON — the SPA's types.ts
            // ignores unknown fields, so no client change is required to ship this.
            calibrated = status?.Calibrated,
            calibrationCount = status?.CalibrationCount,
            calibrationTarget = status?.CalibrationTarget,
            alertState = status?.AlertState,
        };
    });

    return Results.Json(new { entries = payload });
});

// [4b] GET /api/detectors/defaults — JSON detector default params (replaces
// /api/detectors/new-entry htmx fragment). Default values table is the authoritative
// v3.0 spec (DetectorDefaults / 07-UI-SPEC "Detector default values") — do not
// invent new defaults here.
app.MapGet("/api/detectors/defaults", (HttpRequest req) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);

    var name = (req.Query["name"].FirstOrDefault() ?? "").ToLowerInvariant();

    // No ?name= — the whole table plus the single-sensor sensitivity presets, in one request.
    // WR-02 is withdrawn: the SPA no longer mirrors these numbers, it fetches them, so
    // DetectorDefaults.cs is the single source of truth for both sides.
    if (name.Length == 0)
    {
        return Results.Json(new
        {
            defaults = DetectorDefaults.All(),
            presets = new { rmad = SensorPresets.Get("rmad") },
        });
    }

    var defaults = DetectorDefaults.Get(name);

    if (defaults is null) return Results.StatusCode(400);

    return Results.Json(new { name, @params = defaults });
});

// [5] POST /api/sensors/save — expand patterns, write entities.yaml, create lock file,
//     call ILiveEntitiesConfig.Swap. JSON body (SaveRequest) replaces form-encoded body;
//     ReadFromJsonAsync's natural nested DTO eliminates DetectorFieldParser entirely.
app.MapPost("/api/sensors/save", async (HttpRequest req, IHaSensorRegistry registry,
    Argus.Orchestrator.Config.ConfigWriter writer, ConnectionSettings settings,
    ILiveEntitiesConfig liveCfg, ILogger<Program> logger, CancellationToken ct) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);

    try
    {
        SaveRequest? body;
        try
        {
            body = await req.ReadFromJsonAsync<SaveRequest>(ct);
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed JSON body — 400 with a generic reason, never raw exception text.
            return Results.Json(new { ok = false, kind = "error", reason = "invalid request body" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (body is null)
        {
            return Results.Json(new { ok = false, kind = "error", reason = "invalid request body" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Selected entity ids (may be empty — valid per Pitfall 5)
        var selectedIds = body.Entities
            .Select(e => e.EntityId)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        // Split include/exclude textarea content by newline (same shape as v3.0 form fields)
        var includeRaw = body.Include ?? "";
        var excludeRaw = body.Exclude ?? "";
        var include = includeRaw.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var exclude = excludeRaw.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Resolve: GlobExpander.Resolve with selectedIds as manuallyChecked, [] as manuallyUnchecked
        // (the UI model: checkboxes ARE the manual selection — patterns feed the base set)
        var resolvedIds = GlobExpander.Resolve(
            registry.GetAll(), include, exclude, selectedIds, []);

        // Build parsedDetectors keyed by entity index — index = position in the sorted
        // (alphabetical EntityId) resolvedIds list, exactly like the v3.0 form-parsing path.
        // Source is now the JSON body's per-entity detectors array, keyed by entityId.
        var detectorsByEntityId = body.Entities
            .Where(e => !string.IsNullOrEmpty(e.EntityId))
            .ToDictionary(
                e => e.EntityId,
                e => e.Detectors
                    .Select(d => new DetectorConfig { Name = d.Name, Params = d.Params })
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        var sortedIds = resolvedIds
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var parsedDetectors = sortedIds
            .Select((id, ei) => (ei, dets: detectorsByEntityId.TryGetValue(id, out var d) ? d : new List<DetectorConfig>()))
            .ToDictionary(x => x.ei, x => x.dets);

        // Phase 4 input validation gate (UI-04 / T-04-01–T-04-05):
        // Validate raw parsedDetectors BEFORE defaulting and BEFORE any write.
        // A tampered or malformed POST body must never reach disk or the live pipeline.
        var validationErrors = InputValidator.Validate(resolvedIds, parsedDetectors);
        if (validationErrors.Count > 0)
        {
            logger.LogWarning(LogEvents.UiValidationBlocked,
                "UI save blocked: {ErrorCount} validation error(s)", validationErrors.Count);
            return Results.Json(new { ok = false, kind = "validation", errorCount = validationErrors.Count });
        }

        // Build EntityConfig list: sorted alphabetically by EntityId so ei=0 → first alpha
        var snapshotById = registry.GetAll()
            .ToDictionary(e => e.EntityId, StringComparer.OrdinalIgnoreCase);

        var entities = sortedIds
            .Select((id, ei) =>
            {
                snapshotById.TryGetValue(id, out var entry);

                // Get detector list for this entity index; default to HST if empty (Pitfall 7 / CFG-03)
                // D-A: rmad is the default detector for a newly tracked entity. Empty params
                // means "use all defaults", which RmadParams.From and DetectorDefaults agree on.
                var detectors = parsedDetectors.TryGetValue(ei, out var dets) && dets.Count > 0
                    ? dets
                    : [new DetectorConfig { Name = "rmad", Params = [] }];

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

        // Use an ordered dictionary to ensure _patterns appears before entities.
        // T-14-01 / G-14-1 fix: preserve pre-existing groups via read-modify-write — liveCfg
        // still holds the pre-save config here (Swap happens below), so its Groups are the
        // current on-disk groups. Symmetric with /api/groups/save (Program.cs:521-556), which
        // preserves entities:/_patterns: the same way.
        // D-L: schema_version FIRST and on BOTH writers. If either writer omitted it, the next
        // save would strip the stamp and the migrator would rewrite the file on every boot —
        // and every rewrite is a rename, i.e. a ConfigFileWatcherService Swap that resets every
        // entity's alert gate.
        var root = new Dictionary<string, object>
        {
            ["schema_version"] = EntitiesSchemaMigrator.TargetSchemaVersion,
            ["_patterns"] = patternsMap,
            ["entities"] = entities,
            ["groups"] = liveCfg.Get().Groups,
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

        // SC5: the warm-up note renders for any STREAMING detector, not hst alone — rmad warms
        // up too (min_samples), and after the migration hst is the exception, not the rule.
        var hasStreaming = entities.Any(e => e.Detectors.Any(
            d => d.Name.Equals("rmad", StringComparison.OrdinalIgnoreCase) ||
                 d.Name.Equals("hst", StringComparison.OrdinalIgnoreCase)));
        return Results.Json(new { ok = true, count = entities.Count, hasStreaming });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "UI save failed");  // Full exception to add-on log only (T-02-11)

        // Generic reason exposed to browser — no internal exception detail (T-02-11)
        var reason = ex is IOException ? "disk error" : "unexpected error";
        return Results.Json(new { ok = false, kind = "error", reason });
    }
});

// [7] GET /api/groups — JSON group list (08-02 SPA #/groups). CFG-04: liveCfg.Get() read
// fresh per-request, never a captured stale reference.
app.MapGet("/api/groups", (HttpRequest req, ILiveEntitiesConfig liveCfg) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);

    var groups = liveCfg.Get().Groups.Select(g => new
    {
        groupId = g.GroupId,
        friendlyName = g.FriendlyName,
        members = g.Members,
        mode = g.Mode,
        detector = g.Detector,
        @params = g.Params,
    });

    return Results.Json(new { groups });
});

// [8] POST /api/groups/save — validate (floor 3, peer unit consistency, member cap), then
// full-list-replace the top-level groups: key via ConfigWriter + LiveEntitiesConfig.Swap,
// WITHOUT disturbing entities:/_patterns: (byte-for-byte the /api/sensors/save pipeline).
app.MapPost("/api/groups/save", async (HttpRequest req, IHaSensorRegistry registry,
    Argus.Orchestrator.Config.ConfigWriter writer, ConnectionSettings settings,
    ILiveEntitiesConfig liveCfg, ILogger<Program> logger, CancellationToken ct) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);

    try
    {
        GroupSaveRequest? body;
        try
        {
            body = await req.ReadFromJsonAsync<GroupSaveRequest>(ct);
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.Json(new { ok = false, kind = "error", reason = "invalid request body" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (body is null)
        {
            return Results.Json(new { ok = false, kind = "error", reason = "invalid request body" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var validationErrors = GroupInputValidator.Validate(body.Groups, registry);
        if (validationErrors.Count > 0)
        {
            logger.LogWarning(LogEvents.GroupUiValidationBlocked,
                "Group UI save blocked: {ErrorCount} validation error(s)", validationErrors.Count);
            return Results.Json(new { ok = false, kind = "validation", errorCount = validationErrors.Count });
        }

        var groups = body.Groups.Select(g => new GroupConfig
        {
            GroupId = g.GroupId,
            FriendlyName = g.FriendlyName,
            Members = g.Members,
            Mode = g.Mode,
            Detector = g.Detector,
            Params = g.Params,
        }).ToList();

        // Read the CURRENT entities.yaml so entities:/_patterns: are preserved untouched;
        // replace ONLY the top-level groups: key (same single-root-dict discipline as
        // /api/sensors/save — never string-format YAML, T-02-08). EntitiesConfig does not
        // model _patterns (it's write-only UI state dropped by IgnoreUnmatchedProperties on
        // load), so it is re-derived here from the raw on-disk YAML rather than lost on a
        // groups-only save.
        var entitiesPath = settings.EntitiesPath ?? "/data/entities.yaml";
        var currentConfig = liveCfg.Get();

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        object existingPatterns = new Dictionary<string, object>
        {
            ["include"] = new List<string>(),
            ["exclude"] = new List<string>(),
        };
        if (File.Exists(entitiesPath))
        {
            var existingYaml = await File.ReadAllTextAsync(entitiesPath, ct);
            var rawDeserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var rawRoot = rawDeserializer.Deserialize<Dictionary<object, object>>(existingYaml);
            if (rawRoot is not null && rawRoot.TryGetValue("_patterns", out var patternsObj))
                existingPatterns = patternsObj;
        }

        // D-L: the groups writer stamps schema_version too — see /api/sensors/save above.
        var root = new Dictionary<string, object>
        {
            ["schema_version"] = EntitiesSchemaMigrator.TargetSchemaVersion,
            ["_patterns"] = existingPatterns,
            ["entities"] = currentConfig.Entities,
            ["groups"] = groups,
        };

        var fullYaml = serializer.Serialize(root);

        await writer.WriteAsync(entitiesPath, fullYaml, ct);

        var newConfig = EntitiesConfigLoader.Load(entitiesPath, logger, registry);
        liveCfg.Swap(newConfig); // fires ConfigChanged → hot-reload, no restart

        logger.LogInformation(LogEvents.UiSaveSuccess,
            "Group UI save succeeded: {GroupCount} groups written to {Path}", groups.Count, entitiesPath);

        return Results.Json(new { ok = true, count = groups.Count });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Group UI save failed"); // Full exception to add-on log only (T-02-11)

        var reason = ex is IOException ? "disk error" : "unexpected error";
        return Results.Json(new { ok = false, kind = "error", reason });
    }
});

// [9] GET /api/detectors/catalog — static group-detector catalog (ALGO-01..04). Purely
// descriptive C#, never calls gRPC/Python — must render even when the detector is down.
app.MapGet("/api/detectors/catalog", (HttpRequest req) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);

    return Results.Json(new { detectors = DetectorCatalog.All(), guided = DetectorCatalog.Guided() });
});

// [9b] GET /api/settings — read-only orchestrator configuration for the Settings screen
// (D-06). SettingsProjection is the sole allowlist boundary (D-07) — it never serializes
// ConnectionSettings as a whole, so credentials and connection secrets cannot leak here.
app.MapGet("/api/settings", (HttpRequest req, ConnectionSettings settings, IConfiguration config) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);

    return Results.Json(SettingsProjection.Build(settings, config));
});

// [10] GET /api/groups/{id}/status — last cached joint-mode verdict (GRP-09). Returns
// 200-with-null for an unknown/never-scored id (T-08-05: no existence oracle via 404).
app.MapGet("/api/groups/{id}/status", (HttpRequest req, string id, IGroupStatusCache statusCache) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);

    var entry = statusCache.Get(id);
    if (entry is null) return Results.Json(new { status = (object?)null });

    return Results.Json(new
    {
        status = new
        {
            groupId = entry.GroupId,
            score = entry.Score,
            isAnomaly = entry.IsAnomaly,
            detector = entry.Detector,
            scoredAtUtc = entry.ScoredAtUtc,
            contributions = entry.Contributions.Select(c => new { memberId = c.MemberId, contribution = c.Contribution }),
        },
    });
});

// [10b] GET /api/health — composite liveness + HA entity count (QUICK-dashboard-real-data).
// HealthProjection is the sole allowlist boundary (D-07) — see HealthProjection.cs.
app.MapGet("/api/health", (
    HttpRequest req, ArgusHealthSignals signals, MqttConnection mqtt, IHaSensorRegistry registry,
    ConnectionSettings settings, IBatchRunStatus batchRunStatus) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);

    return Results.Json(HealthProjection.Build(
        signals, mqtt.IsConnected, registry.GetAll().Count, settings, batchRunStatus.LastRunUtc, DateTimeOffset.UtcNow));
});

// [10c] GET /api/anomalies/recent — last N anomalies newest-first (QUICK-dashboard-real-data).
app.MapGet("/api/anomalies/recent", (HttpRequest req, IRecentAnomaliesCache cache) =>
{
    if (!IsAuthorizedRequest(req.HttpContext)) return Results.StatusCode(403);

    return Results.Json(new
    {
        anomalies = cache.GetRecent().Select(a => new
        {
            entityId = a.EntityId,
            groupId = a.GroupId,
            score = a.Score,
            detector = a.Detector,
            detectedAtUtc = a.DetectedAtUtc,
        }),
    });
});

// [11] SPA fallback — serves index.html for any unmatched, extensionless path (root and any
// client-side hash routes). Never intercepts /api/* (explicit routes win) or real static
// files (UseStaticFiles already served above). Must be registered last.
app.MapFallbackToFile("index.html");

app.Run();
