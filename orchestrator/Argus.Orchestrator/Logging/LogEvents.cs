using Microsoft.Extensions.Logging;

namespace Argus.Orchestrator.Logging;

/// <summary>
/// Structured log event ID definitions for OBS-01 scaffolding.
/// Stable event IDs enable log filtering and alerting by ID.
/// </summary>
public static class LogEvents
{
    // Config loading
    public static readonly EventId EntityConfigLoaded = new(1001, nameof(EntityConfigLoaded));
    public static readonly EventId CovariatesIgnored = new(1002, nameof(CovariatesIgnored));

    // gRPC channel
    public static readonly EventId ChannelEstablished = new(2001, nameof(ChannelEstablished));
    public static readonly EventId ChannelFailed = new(2002, nameof(ChannelFailed));

    // Health check gate (INFRA-07)
    public static readonly EventId StartupHealthCheck = new(2010, nameof(StartupHealthCheck));
    public static readonly EventId StartupHealthCheckServing = new(2011, nameof(StartupHealthCheckServing));
    public static readonly EventId StartupHealthCheckNotServing = new(2012, nameof(StartupHealthCheckNotServing));
    public static readonly EventId StartupHealthCheckRetry = new(2013, nameof(StartupHealthCheckRetry));

    // HA listener
    public static readonly EventId HaListenerStarting = new(3001, nameof(HaListenerStarting));
    public static readonly EventId HaListenerDetectorHealthy = new(3002, nameof(HaListenerDetectorHealthy));

    // MQTT publisher
    public static readonly EventId MqttConnected = new(4001, nameof(MqttConnected));
    public static readonly EventId MqttDisconnected = new(4002, nameof(MqttDisconnected));
    public static readonly EventId MqttBridgeOnline = new(4003, nameof(MqttBridgeOnline));
    public static readonly EventId MqttReconnecting = new(4004, nameof(MqttReconnecting));
    public static readonly EventId MqttDiscoveryPublished = new(4005, nameof(MqttDiscoveryPublished));
    public static readonly EventId MqttWorkerStarted = new(4006, nameof(MqttWorkerStarted));
}
