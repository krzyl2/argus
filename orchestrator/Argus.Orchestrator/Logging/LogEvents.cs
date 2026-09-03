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
    public static readonly EventId GroupConfigLoaded = new(1004, nameof(GroupConfigLoaded));
    public static readonly EventId GroupRejected = new(1005, nameof(GroupRejected));

    // entities.yaml schema migration (D-L)
    public static readonly EventId EntityConfigMigrated = new(1006, nameof(EntityConfigMigrated));
    public static readonly EventId EntityConfigMigrationTuned = new(1007, nameof(EntityConfigMigrationTuned));
    public static readonly EventId EntityConfigMigrationRefused = new(1008, nameof(EntityConfigMigrationRefused));
    public static readonly EventId EntityConfigMigrationFailed = new(1009, nameof(EntityConfigMigrationFailed));

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
    public static readonly EventId MqttPublishDropped = new(4009, nameof(MqttPublishDropped));

    // Health publisher (6xxx)
    public static readonly EventId HealthEntityPublished = new(6001, nameof(HealthEntityPublished));
    public static readonly EventId HealthStatePublished  = new(6002, nameof(HealthStatePublished));
    public static readonly EventId HealthCycleFailed     = new(6003, nameof(HealthCycleFailed));

    // Phase 2 UI / Sensor Registry (7xxx)
    public static readonly EventId SensorRegistryUpdated = new(7001, nameof(SensorRegistryUpdated));
    public static readonly EventId UiSaveSuccess = new(7002, nameof(UiSaveSuccess));
    public static readonly EventId UiSaveFailed  = new(7003, nameof(UiSaveFailed));

    // Phase 3 Config Reload (7004–7006)
    public static readonly EventId ConfigReloadTriggered     = new(7004, nameof(ConfigReloadTriggered));
    public static readonly EventId ConfigReloadComplete      = new(7005, nameof(ConfigReloadComplete));
    public static readonly EventId MqttRetractionPublished   = new(7006, nameof(MqttRetractionPublished));

    // Phase 4 Input Validation (7007–7008)
    public static readonly EventId ConfigFileWatcherReloadFailed = new(7007, nameof(ConfigFileWatcherReloadFailed));
    public static readonly EventId UiValidationBlocked           = new(7008, nameof(UiValidationBlocked));

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

    // Group batch scheduler (GRP-02/GRP-08, Phase 6)
    public static readonly EventId GroupScored             = new(5012, nameof(GroupScored));
    public static readonly EventId GroupSkippedStale        = new(5013, nameof(GroupSkippedStale));
    public static readonly EventId GroupSchedulerError      = new(5014, nameof(GroupSchedulerError));
    public static readonly EventId GroupNoData              = new(5015, nameof(GroupNoData));
    public static readonly EventId GroupModeDetectorMismatch = new(5016, nameof(GroupModeDetectorMismatch));

    // InfluxDB history backfill (Phase 15-03, BACKFILL-01..04)
    public static readonly EventId WarmupPrimed             = new(5017, nameof(WarmupPrimed));
    public static readonly EventId WarmupSkipped            = new(5018, nameof(WarmupSkipped));
    public static readonly EventId WarmupFailed             = new(5019, nameof(WarmupFailed));

    // WS5 — HA Recorder as the history source (D-K). HistoryFetched is the per-entity receipt
    // that priming actually had data to work with; HistoryShort is the fail-loud line for an
    // entity the Recorder cannot fill the baseline window for (Rule 12).
    public static readonly EventId HistoryFetched           = new(5020, nameof(HistoryFetched));
    public static readonly EventId HistoryFetchFailed       = new(5021, nameof(HistoryFetchFailed));
    public static readonly EventId HistoryShort             = new(5022, nameof(HistoryShort));

    // HistoryConnectionOpened is the readable form of the E2 cache criterion: one line per
    // transient connect, carrying the running count, so "200 queries -> 1 connection" can be
    // grepped out of a Debug log instead of being asserted only in a unit test.
    public static readonly EventId HistoryConnectionOpened  = new(5023, nameof(HistoryConnectionOpened));

    // HistoryEmpty is the F11 negative case: the query succeeded and returned NOTHING for an
    // entity the operator asked to be watched. That is never normal — it is an HA-side visibility
    // or permission problem — and it must not be indistinguishable from "backfill is off", which
    // silence would make it (§5.3, case (e)).
    public static readonly EventId HistoryEmpty             = new(5024, nameof(HistoryEmpty));

    // WS4 — sensor registry (F9/F10). SensorRegistryUpserted is the receipt that an entity the
    // boot snapshot never showed us became pickable purely from state_changed; SensorRegistryGhost
    // is the fail-loud line for the opposite direction — an entity we are SCORING that HA does not
    // list (F9: sensor.zamrazarkapiwnica_power, scored 0.996 while invisible in the UI).
    // 5023/5024 are taken by WS5, so WS4's pair is 5025/5026.
    public static readonly EventId SensorRegistryUpserted   = new(5025, nameof(SensorRegistryUpserted));
    public static readonly EventId SensorRegistryGhost      = new(5026, nameof(SensorRegistryGhost));

    // Phase 8 UI — Group config UI + algorithm chooser (7009)
    public static readonly EventId GroupUiValidationBlocked = new(7009, nameof(GroupUiValidationBlocked));

    // WS2 alert layer — event onsets/ends and the fail-loud storm signal (D-D)
    public static readonly EventId AlertEventStarted        = new(7010, nameof(AlertEventStarted));
    public static readonly EventId AlertEventEnded          = new(7011, nameof(AlertEventEnded));
    public static readonly EventId AlertStormRaised         = new(7012, nameof(AlertStormRaised));
}
