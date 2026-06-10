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
}
