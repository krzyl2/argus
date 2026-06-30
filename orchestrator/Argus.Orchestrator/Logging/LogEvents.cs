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
    public static readonly EventId EmptyEntitiesWarning = new(1003, nameof(EmptyEntitiesWarning));

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
    public static readonly EventId DiscoveredSensorsLogged = new(3003, nameof(DiscoveredSensorsLogged));

    // MQTT publisher
    public static readonly EventId MqttConnected = new(4001, nameof(MqttConnected));
    public static readonly EventId MqttDisconnected = new(4002, nameof(MqttDisconnected));
    public static readonly EventId MqttBridgeOnline = new(4003, nameof(MqttBridgeOnline));
    public static readonly EventId MqttReconnecting = new(4004, nameof(MqttReconnecting));
    public static readonly EventId MqttDiscoveryPublished = new(4005, nameof(MqttDiscoveryPublished));
    public static readonly EventId MqttWorkerStarted = new(4006, nameof(MqttWorkerStarted));
    public static readonly EventId MqttWorkerReady = new(4007, nameof(MqttWorkerReady));
    public static readonly EventId MqttCredentialsRefreshed = new(4008, nameof(MqttCredentialsRefreshed));

    // Health publisher (6xxx)
    public static readonly EventId HealthEntityPublished = new(6001, nameof(HealthEntityPublished));

    // Batch scheduler (5xxx)
    public static readonly EventId BatchSchedulerStarted   = new(5001, nameof(BatchSchedulerStarted));
    public static readonly EventId BatchSchedulerStopped   = new(5002, nameof(BatchSchedulerStopped));
    public static readonly EventId BatchSchedulerError     = new(5003, nameof(BatchSchedulerError));
    public static readonly EventId BatchEntityNoData       = new(5004, nameof(BatchEntityNoData));
    public static readonly EventId BatchColdStartFit       = new(5005, nameof(BatchColdStartFit));
    public static readonly EventId BatchScoredEntity       = new(5006, nameof(BatchScoredEntity));
    public static readonly EventId NightlyFitStarted       = new(5007, nameof(NightlyFitStarted));
    public static readonly EventId NightlyFitCompleted     = new(5008, nameof(NightlyFitCompleted));
    public static readonly EventId ModelSaved              = new(5009, nameof(ModelSaved));
    public static readonly EventId ModelLoaded             = new(5010, nameof(ModelLoaded));
    public static readonly EventId ModelVersionMismatch    = new(5011, nameof(ModelVersionMismatch));
}
