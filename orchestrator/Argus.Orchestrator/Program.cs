using Argus.Orchestrator.Batch;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Mqtt;
using Argus.Orchestrator.Workers;
using Grpc.Net.Client;
using InfluxDB.Client;
using NetDaemon.Client.Extensions;

var builder = Host.CreateApplicationBuilder(args);

// Load entities.yaml (CONF-01/CONF-02)
var entitiesPath = builder.Configuration["ARGUS_ENTITIES_PATH"] ?? "entities.yaml";
var entitiesLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var entitiesLogger = entitiesLoggerFactory.CreateLogger<EntitiesConfigLoader>();
var entitiesConfig = EntitiesConfigLoader.Load(entitiesPath, entitiesLogger);
builder.Services.AddSingleton(entitiesConfig);

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

// Register NetDaemon.Client DI (IHomeAssistantClient, IHomeAssistantRunner) — D-06
builder.Services.AddHomeAssistantClient();

// Register ReconnectCooldown (60s post-reconnect binary_sensor suppression — D-07)
builder.Services.AddSingleton<ReconnectCooldown>();

// Register HA event source (NetDaemon.Client WebSocket subscription — STRM-01/STRM-02)
builder.Services.AddSingleton<IHaEventSource, NetDaemonHaEventSource>();

// Register HA listener worker (consumes IHaEventSource after health gate)
builder.Services.AddHostedService<HaListenerWorker>();

// Register MQTT stack (Plan 07): MqttConnection (LWT), StatePublisher, MqttPublisherWorker
// DiscoveryPublisher is static — no DI registration needed
builder.Services.AddSingleton<MqttConnection>(sp =>
    new MqttConnection(connectionSettings, sp.GetRequiredService<ILogger<MqttConnection>>()));
builder.Services.AddSingleton<StatePublisher>();
// IStatePublisher resolves to the same singleton StatePublisher (for ScoreStreamPipeline injection)
builder.Services.AddSingleton<IStatePublisher>(sp => sp.GetRequiredService<StatePublisher>());
builder.Services.AddHostedService<MqttPublisherWorker>();

// Register ScoreStreamPipeline (Plan 08): bidi ScoreStream loop with hysteresis/frozen/MQTT
builder.Services.AddSingleton<ScoreStreamPipeline>();

// Register InfluxDB batch reader (Plan 02-02 / BTCH-01)
// InfluxDBClient is a singleton; QueryApi obtained per-call inside InfluxDbReader
builder.Services.AddSingleton<InfluxDBClient>(_ =>
    new InfluxDBClient(connectionSettings.InfluxUrl ?? string.Empty, connectionSettings.InfluxToken));
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
    sp.GetRequiredService<EntitiesConfig>(),
    sp.GetRequiredService<DetectionGateway>(),
    sp.GetRequiredService<ILogger<BatchSchedulerWorker>>()));

var host = builder.Build();
host.Run();
