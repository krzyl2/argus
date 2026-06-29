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
}
