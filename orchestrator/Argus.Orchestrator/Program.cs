using Argus.Orchestrator.Config;
using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Mqtt;
using Argus.Orchestrator.Workers;
using Grpc.Net.Client;
using NetDaemon.Client.Extensions;

var builder = Host.CreateApplicationBuilder(args);

// Bind ConnectionSettings from environment variables (CONF-03 — no credentials in source)
builder.Services.Configure<ConnectionSettings>(o =>
{
    o.HaUrl = builder.Configuration["ARGUS_HA_URL"];
    o.HaToken = builder.Configuration["ARGUS_HA_TOKEN"];
    o.MqttHost = builder.Configuration["ARGUS_MQTT_HOST"];
    if (int.TryParse(builder.Configuration["ARGUS_MQTT_PORT"], out var port)) o.MqttPort = port;
    o.MqttUser = builder.Configuration["ARGUS_MQTT_USER"];
    o.MqttPassword = builder.Configuration["ARGUS_MQTT_PASSWORD"];
    o.DetectorEndpoint = builder.Configuration["ARGUS_DETECTOR_ENDPOINT"];
    o.TlsCa = builder.Configuration["ARGUS_TLS_CA"];
    o.TlsCert = builder.Configuration["ARGUS_TLS_CERT"];
    o.TlsKey = builder.Configuration["ARGUS_TLS_KEY"];
    o.EntitiesPath = builder.Configuration["ARGUS_ENTITIES_PATH"] ?? "entities.yaml";
});

// Load entities.yaml (CONF-01/CONF-02)
var entitiesPath = builder.Configuration["ARGUS_ENTITIES_PATH"] ?? "entities.yaml";
var entitiesLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var entitiesLogger = entitiesLoggerFactory.CreateLogger<EntitiesConfigLoader>();
var entitiesConfig = EntitiesConfigLoader.Load(entitiesPath, entitiesLogger);
builder.Services.AddSingleton(entitiesConfig);

// Build mTLS ConnectionSettings eagerly for singleton registration
var connectionSettings = new ConnectionSettings
{
    HaUrl = builder.Configuration["ARGUS_HA_URL"],
    HaToken = builder.Configuration["ARGUS_HA_TOKEN"],
    MqttHost = builder.Configuration["ARGUS_MQTT_HOST"],
    MqttUser = builder.Configuration["ARGUS_MQTT_USER"],
    MqttPassword = builder.Configuration["ARGUS_MQTT_PASSWORD"],
    DetectorEndpoint = builder.Configuration["ARGUS_DETECTOR_ENDPOINT"],
    TlsCa = builder.Configuration["ARGUS_TLS_CA"],
    TlsCert = builder.Configuration["ARGUS_TLS_CERT"],
    TlsKey = builder.Configuration["ARGUS_TLS_KEY"],
    EntitiesPath = entitiesPath,
};
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
builder.Services.AddHostedService<MqttPublisherWorker>();

var host = builder.Build();
host.Run();
