using Argus.Orchestrator.Config;
using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Workers;
using Grpc.Net.Client;

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

// Register HA listener worker (stub — Plan 05 fills the subscription body)
builder.Services.AddHostedService<HaListenerWorker>();

// TODO(plan06): register MqttPublisher
// builder.Services.AddSingleton<MqttPublisher>();
// builder.Services.AddHostedService<MqttPublisherWorker>();

var host = builder.Build();
host.Run();
