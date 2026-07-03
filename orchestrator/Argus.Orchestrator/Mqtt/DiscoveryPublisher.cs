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
///
/// Also builds the composite add-on health binary_sensor (HEALTH-01):
/// device_class "problem", unique_id == object_id == argus_addon_health,
/// state_topic = argus/addon/health/state, device grouped under "Argus" with stable identifiers.
/// </summary>
public class DiscoveryPublisher
{
    private const string BridgeAvailabilityTopic = "argus/bridge/availability";
    private const string Manufacturer = "Argus";
    private const string Model = "Argus Anomaly Detector";

    // Health entity constants (HEALTH-01)
    public const string HealthObjectId = "argus_addon_health";
    public const string HealthStateTopic = "argus/addon/health/state";
    public const string HealthDiscoveryTopic = $"homeassistant/binary_sensor/{HealthObjectId}/config";

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
            // Per-entity availability list (HA 2022.9+): bridge-level + per-entity (CR-05)
            availability = new object[]
            {
                new { topic = BridgeAvailabilityTopic, payload_available = "online", payload_not_available = "offline" },
                new { topic = $"argus/{slug}/availability", payload_available = "online", payload_not_available = "offline" },
            },
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
            // Per-entity availability list (HA 2022.9+): bridge-level + per-entity (CR-05)
            availability = new object[]
            {
                new { topic = BridgeAvailabilityTopic, payload_available = "online", payload_not_available = "offline" },
                new { topic = $"argus/{slug}/availability", payload_available = "online", payload_not_available = "offline" },
            },
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
    public static Task PublishAllAsync(
        MqttConnection mqtt,
        IEnumerable<EntityConfig> entities,
        CancellationToken ct)
        => PublishAllAsync(
            (topic, payload, retain, token) => mqtt.PublishAsync(topic, payload, retain, token),
            entities,
            ct);

    /// <summary>
    /// Testable overload: accepts a publish delegate instead of a live MqttConnection.
    /// Production code uses the MqttConnection overload above.
    /// </summary>
    public static async Task PublishAllAsync(
        Func<string, string, bool, CancellationToken, Task> publish,
        IEnumerable<EntityConfig> entities,
        CancellationToken ct)
    {
        foreach (var entity in entities)
        {
            var detector = GetDetectorName(entity);
            var anomalyId = UniqueId.AnomalyId(entity.EntityId, detector);
            var scoreId   = UniqueId.ScoreId(entity.EntityId, detector);

            await publish(
                $"homeassistant/binary_sensor/{anomalyId}/config",
                BuildBinarySensorConfig(entity),
                true,
                ct);

            await publish(
                $"homeassistant/sensor/{scoreId}/config",
                BuildSensorConfig(entity),
                true,
                ct);
        }
    }

    /// <summary>
    /// Retracts discovery entities for removed entities by publishing empty retained payloads
    /// to their binary_sensor and sensor config topics (MQTT §3.3.1-7 retained-message deletion).
    ///
    /// Only the passed <paramref name="removedEntities"/> are retracted — no other topics are touched
    /// (T-03-01: retraction scope limited to the passed set; topic ids derived from server-controlled
    /// EntityConfig via UniqueId.Slug).
    /// </summary>
    public static Task RetractAsync(
        MqttConnection mqtt,
        IEnumerable<EntityConfig> removedEntities,
        CancellationToken ct)
        => RetractAsync(
            (topic, payload, retain, token) => mqtt.PublishAsync(topic, payload, retain, token),
            removedEntities,
            ct);

    /// <summary>
    /// Testable overload: accepts a publish delegate instead of a live MqttConnection.
    /// Production code uses the MqttConnection overload above.
    /// </summary>
    public static async Task RetractAsync(
        Func<string, string, bool, CancellationToken, Task> publish,
        IEnumerable<EntityConfig> removedEntities,
        CancellationToken ct)
    {
        foreach (var entity in removedEntities)
        {
            var detector  = GetDetectorName(entity);
            var anomalyId = UniqueId.AnomalyId(entity.EntityId, detector);
            var scoreId   = UniqueId.ScoreId(entity.EntityId, detector);

            await publish(
                $"homeassistant/binary_sensor/{anomalyId}/config",
                string.Empty, true, ct);

            await publish(
                $"homeassistant/sensor/{scoreId}/config",
                string.Empty, true, ct);
        }
    }

    /// <summary>
    /// Builds the health binary_sensor discovery JSON payload for the Argus add-on itself (HEALTH-01).
    /// device_class "problem" — ON means problem/unavailable, OFF means healthy.
    /// Stable unique_id == object_id == argus_addon_health (D-14, prevents HA mangling).
    /// Availability follows bridge-level only (no per-entity availability for the add-on health entity).
    /// Polish friendly name "Argus — status" (D8).
    /// </summary>
    public static string BuildHealthBinarySensorConfig()
    {
        var payload = new
        {
            unique_id = HealthObjectId,
            object_id = HealthObjectId,         // D-14: prevents HA mangling
            name = "Argus — status",        // D8: Polish friendly name "Argus — status"
            state_topic = HealthStateTopic,
            payload_on = "ON",
            payload_off = "OFF",
            device_class = "problem",
            availability_topic = BridgeAvailabilityTopic,
            payload_available = "online",
            payload_not_available = "offline",
            device = new
            {
                identifiers = new[] { "argus_addon" },
                name = "Argus",
                manufacturer = Manufacturer,
                model = Model,
            }
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string GetDetectorName(EntityConfig entity)
        => entity.Detectors.Count > 0 ? entity.Detectors[0].Name : "hst";

    private static bool IsPeerDivergence(GroupConfig group)
        => string.Equals(group.Mode, "peer_divergence", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Count-aware entity-shape predicate (Pitfall 3): a 2-member peer_divergence group's pairwise-
    /// delta score is a single derived value with no per-member attribution, so it must get ONE
    /// group-level entity pair (memberId=null), not two per-member entities. internal so
    /// MqttPublisherWorker can reuse it as the single source of truth (Pitfall 4).
    /// </summary>
    internal static bool UsesPerMemberEntities(GroupConfig group) => IsPeerDivergence(group) && group.Members.Count >= 3;

    /// <summary>
    /// Builds the group binary_sensor discovery JSON payload (GRP-08).
    /// peer_divergence (memberId set): per-member flag. joint (memberId null): single group-level flag.
    /// All group entities share ONE HA device (identifiers = argus_group_{groupSlug}) — never per-member.
    /// </summary>
    public static string BuildGroupBinarySensorConfig(GroupConfig group, string? memberId = null)
    {
        var groupSlug = UniqueId.Slug(group.GroupId);
        var uniqueId = UniqueId.GroupFlagId(group.GroupId, memberId);
        var isPeer = UsesPerMemberEntities(group);
        var name = isPeer
            ? $"{group.FriendlyName} {memberId} anomalia"
            : $"{group.FriendlyName} anomalia";
        var stateTopic = memberId is null
            ? $"argus/group/{groupSlug}/flag/state"
            : $"argus/group/{groupSlug}/{UniqueId.Slug(memberId)}/flag/state";

        var payload = new
        {
            unique_id = uniqueId,
            object_id = uniqueId,   // D-14: prevents HA mangling Polish chars
            name,
            state_topic = stateTopic,
            availability = new object[]
            {
                new { topic = BridgeAvailabilityTopic, payload_available = "online", payload_not_available = "offline" },
            },
            payload_on = "ON",
            payload_off = "OFF",
            device_class = "problem",
            device = new
            {
                identifiers = new[] { $"argus_group_{groupSlug}" },
                name = $"Argus grupa {group.FriendlyName}",
                model = Model,
                manufacturer = Manufacturer,
            }
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>
    /// Builds the group sensor discovery JSON payload for a group's score (GRP-08).
    /// Mirrors BuildGroupBinarySensorConfig's mode-branching and shared-device rule.
    /// </summary>
    public static string BuildGroupSensorConfig(GroupConfig group, string? memberId = null)
    {
        var groupSlug = UniqueId.Slug(group.GroupId);
        var uniqueId = UniqueId.GroupScoreId(group.GroupId, memberId);
        var isPeer = UsesPerMemberEntities(group);
        var name = isPeer
            ? $"{group.FriendlyName} {memberId} anomalia score"
            : $"{group.FriendlyName} anomalia score";
        var stateTopic = memberId is null
            ? $"argus/group/{groupSlug}/score/state"
            : $"argus/group/{groupSlug}/{UniqueId.Slug(memberId)}/score/state";

        var payload = new
        {
            unique_id = uniqueId,
            object_id = uniqueId,   // D-14
            name,
            state_topic = stateTopic,
            availability = new object[]
            {
                new { topic = BridgeAvailabilityTopic, payload_available = "online", payload_not_available = "offline" },
            },
            device = new
            {
                identifiers = new[] { $"argus_group_{groupSlug}" },
                name = $"Argus grupa {group.FriendlyName}",
                model = Model,
                manufacturer = Manufacturer,
            }
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>
    /// Publishes group discovery configs for the group's current members with retain=true (GRP-08).
    /// peer_divergence: one binary_sensor+sensor pair per member. joint: a single group-level pair.
    /// </summary>
    public static Task PublishGroupAsync(
        MqttConnection mqtt,
        GroupConfig group,
        CancellationToken ct)
        => PublishGroupAsync(
            (topic, payload, retain, token) => mqtt.PublishAsync(topic, payload, retain, token),
            group,
            ct);

    /// <summary>
    /// Testable overload: accepts a publish delegate instead of a live MqttConnection.
    /// Production code uses the MqttConnection overload above.
    /// </summary>
    public static async Task PublishGroupAsync(
        Func<string, string, bool, CancellationToken, Task> publish,
        GroupConfig group,
        CancellationToken ct)
    {
        var memberIds = UsesPerMemberEntities(group)
            ? group.Members.Cast<string?>()
            : [null];

        foreach (var memberId in memberIds)
        {
            var flagId = UniqueId.GroupFlagId(group.GroupId, memberId);
            var scoreId = UniqueId.GroupScoreId(group.GroupId, memberId);

            await publish(
                $"homeassistant/binary_sensor/{flagId}/config",
                BuildGroupBinarySensorConfig(group, memberId),
                true,
                ct);

            await publish(
                $"homeassistant/sensor/{scoreId}/config",
                BuildGroupSensorConfig(group, memberId),
                true,
                ct);
        }
    }

    /// <summary>
    /// Retracts discovery entities for a group's REMOVED members only, by publishing empty retained
    /// payloads to their binary_sensor and sensor config topics (GRP-08, T-06-07).
    ///
    /// For peer_divergence, pass exactly the removed member ids (oldMembers.Except(newMembers)) —
    /// surviving members are never touched. For joint groups (or when an entire group_id is removed),
    /// pass a single null entry to retract the group-level pair.
    /// </summary>
    public static Task RetractGroupAsync(
        MqttConnection mqtt,
        GroupConfig group,
        IEnumerable<string?> removedMembers,
        CancellationToken ct)
        => RetractGroupAsync(
            (topic, payload, retain, token) => mqtt.PublishAsync(topic, payload, retain, token),
            group,
            removedMembers,
            ct);

    /// <summary>
    /// Testable overload: accepts a publish delegate instead of a live MqttConnection.
    /// Production code uses the MqttConnection overload above.
    /// </summary>
    public static async Task RetractGroupAsync(
        Func<string, string, bool, CancellationToken, Task> publish,
        GroupConfig group,
        IEnumerable<string?> removedMembers,
        CancellationToken ct)
    {
        foreach (var memberId in removedMembers)
        {
            var flagId = UniqueId.GroupFlagId(group.GroupId, memberId);
            var scoreId = UniqueId.GroupScoreId(group.GroupId, memberId);

            await publish(
                $"homeassistant/binary_sensor/{flagId}/config",
                string.Empty, true, ct);

            await publish(
                $"homeassistant/sensor/{scoreId}/config",
                string.Empty, true, ct);
        }
    }
}
