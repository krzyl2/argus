using System.Text.Json;
using System.Text.Json.Serialization;
using Argus.Orchestrator.Config;
using MQTTnet;
using MQTTnet.Protocol;

namespace Argus.Orchestrator.Mqtt;

/// <summary>
/// Builds and publishes retained MQTT discovery payloads for HA entities (MQTT-01, MQTT-03).
/// Each entity produces two HA entities (binary_sensor + sensor) under one HA device.
/// Idempotency (MQTT-04) is inherent: deterministic unique_id + retain=true; republish is safe.
/// </summary>
public class DiscoveryPublisher
{
    private const string BridgeAvailabilityTopic = "argus/bridge/availability";
    private const string Manufacturer = "Argus";
    private const string Model = "Argus Anomaly Detector";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>
    /// Builds the binary_sensor discovery JSON payload for an entity.
    /// Uses the first configured detector name, defaulting to "hst".
    /// </summary>
    public static string BuildBinarySensorConfig(EntityConfig entity)
    {
        var detector = GetDetectorName(entity);
        var slug = UniqueId.Slug(entity.EntityId);
        var uniqueId = UniqueId.AnomalyId(entity.EntityId, detector);
        var friendlyName = FriendlyName.ForAnomaly(entity.FriendlyName);

        var payload = new
        {
            unique_id = uniqueId,
            object_id = uniqueId,   // D-14: prevents HA mangling Polish chars
            name = friendlyName,
            state_topic = $"argus/{slug}/flag/state",
            availability_topic = BridgeAvailabilityTopic,  // bridge-level (D-15)
            payload_available = "online",
            payload_not_available = "offline",
            payload_on = "ON",
            payload_off = "OFF",
            device_class = "problem",
            device = new
            {
                identifiers = new[] { slug },
                name = $"Argus {slug}",
                model = Model,
                manufacturer = Manufacturer,
            }
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>
    /// Builds the sensor discovery JSON payload for an entity's score.
    /// </summary>
    public static string BuildSensorConfig(EntityConfig entity)
    {
        var detector = GetDetectorName(entity);
        var slug = UniqueId.Slug(entity.EntityId);
        var uniqueId = UniqueId.ScoreId(entity.EntityId, detector);
        var friendlyName = $"{FriendlyName.ForAnomaly(entity.FriendlyName)} score";

        var payload = new
        {
            unique_id = uniqueId,
            object_id = uniqueId,   // D-14
            name = friendlyName,
            state_topic = $"argus/{slug}/score/state",
            availability_topic = BridgeAvailabilityTopic,
            payload_available = "online",
            payload_not_available = "offline",
            device = new
            {
                identifiers = new[] { slug },
                name = $"Argus {slug}",
                model = Model,
                manufacturer = Manufacturer,
            }
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>
    /// Publishes discovery configs for all entities with retain=true and QoS AtLeastOnce (MQTT-01, MQTT-03).
    /// </summary>
    public static async Task PublishAllAsync(
        MqttConnection mqtt,
        IEnumerable<EntityConfig> entities,
        CancellationToken ct)
    {
        foreach (var entity in entities)
        {
            var detector = GetDetectorName(entity);
            var anomalyId = UniqueId.AnomalyId(entity.EntityId, detector);
            var scoreId   = UniqueId.ScoreId(entity.EntityId, detector);

            await mqtt.PublishAsync(
                $"homeassistant/binary_sensor/{anomalyId}/config",
                BuildBinarySensorConfig(entity),
                retain: true,
                ct);

            await mqtt.PublishAsync(
                $"homeassistant/sensor/{scoreId}/config",
                BuildSensorConfig(entity),
                retain: true,
                ct);
        }
    }

    private static string GetDetectorName(EntityConfig entity)
        => entity.Detectors.Count > 0 ? entity.Detectors[0].Name : "hst";
}
