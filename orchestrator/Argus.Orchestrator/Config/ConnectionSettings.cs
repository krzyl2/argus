namespace Argus.Orchestrator.Config;

/// <summary>
/// Orchestrator connection settings bound from IConfiguration / environment variables.
/// CONF-03: No literal defaults for tokens or passwords. Null if unset; validated at startup.
///
/// Environment variable mapping:
///   ARGUS_HA_URL              -> HaUrl
///   ARGUS_HA_TOKEN            -> HaToken
///   ARGUS_MQTT_HOST           -> MqttHost
///   ARGUS_MQTT_PORT           -> MqttPort
///   ARGUS_MQTT_USER           -> MqttUser
///   ARGUS_MQTT_PASSWORD       -> MqttPassword
///   ARGUS_DETECTOR_ENDPOINT   -> DetectorEndpoint (e.g. https://gpu-host:50051)
///   ARGUS_TLS_CA              -> TlsCa (path to ca.crt)
///   ARGUS_TLS_CERT            -> TlsCert (path to client.crt)
///   ARGUS_TLS_KEY             -> TlsKey (path to client.key)
///   ARGUS_ENTITIES_PATH       -> EntitiesPath (default: entities.yaml)
///   ARGUS_INFLUX_URL          -> InfluxUrl
///   ARGUS_INFLUX_TOKEN        -> InfluxToken
///   ARGUS_INFLUX_ORG          -> InfluxOrg
///   ARGUS_INFLUX_BUCKET       -> InfluxBucket
///   ARGUS_INFLUX_MEASUREMENT  -> InfluxMeasurement (default: homeassistant)
///   ARGUS_INFLUX_VALUE_FIELD  -> InfluxValueField (default: value)
///   ARGUS_BATCH_INTERVAL_MIN  -> BatchIntervalMinutes (default: 10)
///   ARGUS_NIGHTLY_FIT_HOUR    -> NightlyFitHour (default: 2)
///   ARGUS_BACKFILL_ENABLED    -> BackfillEnabled (default: true) — D-16: orchestrator-only,
///                                deliberately absent from argus/config.yaml and 10-config-gen.sh
///   ARGUS_BACKFILL_LOOKBACK   -> BackfillLookback (default: "8d") — same as above
///   ARGUS_BACKFILL_ROW_CAP    -> BackfillRowCap (default: 5000, clamped 1..20000) — same as above
///   ARGUS_REGISTRY_SETTLE_SEC -> RegistrySettleSeconds (default: 60, clamped 0..600) — same as above
/// </summary>
public class ConnectionSettings
{
    // Home Assistant WebSocket
    public string? HaUrl { get; set; }
    public string? HaToken { get; set; }

    // MQTT broker (Zigbee2MQTT reuse — Q4 resolved: username/password)
    public string? MqttHost { get; set; }
    public int MqttPort { get; set; } = 1883;
    public string? MqttUser { get; set; }
    public string? MqttPassword { get; set; }

    // Detector gRPC endpoint
    public string? DetectorEndpoint { get; set; }

    // mTLS cert paths (ARGUS_TLS_*)
    public string? TlsCa { get; set; }
    public string? TlsCert { get; set; }
    public string? TlsKey { get; set; }

    // entities.yaml path
    public string EntitiesPath { get; set; } = "entities.yaml";

    // InfluxDB v2 (BTCH-01 / CONF-03)
    public string? InfluxUrl { get; set; }
    public string? InfluxToken { get; set; }
    public string? InfluxOrg { get; set; }
    public string? InfluxBucket { get; set; }

    // Configurable measurement/field names (A4 mitigation — HA InfluxDB defaults may vary)
    public string InfluxMeasurement { get; set; } = "homeassistant";
    public string InfluxValueField { get; set; } = "value";

    // Batch scheduler (BTCH-03)
    public int BatchIntervalMinutes { get; set; } = 10;
    public int NightlyFitHour { get; set; } = 2;

    // InfluxDB history backfill (D-13/D-15/D-16, BACKFILL-01..04). Orchestrator-side only —
    // the Python detector has no InfluxDB client (RESEARCH.md Pitfall 3). Defaults are
    // correct for the operator's deployment and are NOT surfaced in the add-on options UI.
    public bool BackfillEnabled { get; set; } = true;

    /// <summary>
    /// WS5/D-K: 8 days, because the HA Recorder on this deployment keeps 7 (F12) and 8 covers the
    /// boundary — asking for 30d returns the same 1546 rows for the reference sensor, at the cost
    /// of walking 22 days of empty slices.
    /// </summary>
    public string BackfillLookback { get; set; } = "8d";

    /// <summary>
    /// WS5/D-K: ceiling on rows pulled per history query. Bounds both the gRPC Warmup message
    /// (~57 B/Point, so 5000 rows is ~285 kB against an unconfigured 4 MB receive limit) and the
    /// number of 24 h slices the Recorder walk issues. Consumers clamp to 1..20000.
    /// </summary>
    public int BackfillRowCap { get; set; } = 5000;

    /// <summary>
    /// WS4/F10: seconds to wait after the FIRST connect before taking a second get_states
    /// snapshot. At add-on boot some HA integrations are still loading, so their entities report
    /// <c>unknown</c>/<c>unavailable</c>, fail the numeric filter, and — with a connect-only
    /// snapshot — stay invisible until the next reconnect. Orchestrator-only knob
    /// (<c>ARGUS_REGISTRY_SETTLE_SEC</c>, D-13/D-16): deliberately absent from argus/config.yaml
    /// and the translations. 0 disables the second pass (pre-WS4 behaviour). Consumers clamp
    /// to 0..600 and never throw (D-15).
    /// </summary>
    public int RegistrySettleSeconds { get; set; } = 60;
}
