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
    /// D-G: the id is detector-agnostic — see UniqueId.AnomalyId.
    /// </summary>
    public static string BuildBinarySensorConfig(EntityConfig entity)
    {
        var slug = UniqueId.Slug(entity.EntityId);
        var uniqueId = UniqueId.AnomalyId(entity.EntityId);
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
        var slug = UniqueId.Slug(entity.EntityId);
        var uniqueId = UniqueId.ScoreId(entity.EntityId);
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
            var anomalyId = UniqueId.AnomalyId(entity.EntityId);
            var scoreId   = UniqueId.ScoreId(entity.EntityId);

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
            var anomalyId = UniqueId.AnomalyId(entity.EntityId);
            var scoreId   = UniqueId.ScoreId(entity.EntityId);

            await publish(
                $"homeassistant/binary_sensor/{anomalyId}/config",
                string.Empty, true, ct);

            await publish(
                $"homeassistant/sensor/{scoreId}/config",
                string.Empty, true, ct);
        }
    }

    /// <summary>
    /// Retracts the retained discovery configs published under the PRE-D-G, detector-scoped
    /// id formula (argus_{slug}_{detector}_anomaly / _score), for every (entity, detector)
    /// pair present in the configuration as it was BEFORE the schema-2 migration.
    ///
    /// RetractAsync cannot do this: it only retracts entities that were REMOVED, and a migrated
    /// entity is still tracked. Its old retained config would therefore never expire, leaving a
    /// second HA entity fed by the very same argus/{slug}/flag/state topic.
    ///
    /// Every detector name is covered, not just "hst" — an operator may have set "mad" or "stl"
    /// by hand (InputValidator.KnownDetectors), and those published under the old formula too.
    /// Each (slug, detector) pair is retracted exactly once even if the pair repeats in config.
    /// Call once, at the first start after migration, BEFORE the first PublishAllAsync.
    /// </summary>
    /// <returns>
    /// True only when the broker took EVERY deletion. See the delegate overload: the caller
    /// records a durable "already retracted" marker off this answer, so a dropped publish must
    /// never read as a completed retraction.
    /// </returns>
    public static Task<bool> RetractLegacyDetectorScopedAsync(
        MqttConnection mqtt,
        IReadOnlyList<EntityConfig> preMigration,
        CancellationToken ct)
        => RetractLegacyDetectorScopedAsync(
            (topic, payload, retain, token) => mqtt.TryPublishAsync(topic, payload, retain, token),
            preMigration,
            ct);

    /// <summary>
    /// Testable overload: accepts a publish delegate instead of a live MqttConnection.
    ///
    /// The delegate reports DELIVERY, and this method reports it onwards, because
    /// MqttConnection.PublishAsync does not throw when the broker is unreachable — it logs and
    /// drops. A retraction built on a non-throwing sink would otherwise "succeed" having deleted
    /// nothing, the marker would be written, and the stale retained configs would survive every
    /// later boot: the exact outcome D-G exists to prevent, reached through the ordinary case
    /// (broker down) rather than the exotic one (process killed).
    ///
    /// Every deletion is still ATTEMPTED even after one is dropped — deleting an already-deleted
    /// retained message is a no-op, so a partial delivery costs nothing on the retry, while
    /// stopping early would leave topics untried for no gain.
    /// </summary>
    /// <returns>True when every publish was accepted by the broker.</returns>
    public static async Task<bool> RetractLegacyDetectorScopedAsync(
        Func<string, string, bool, CancellationToken, Task<bool>> publish,
        IReadOnlyList<EntityConfig> preMigration,
        CancellationToken ct)
    {
        var seen = new HashSet<(string EntityId, string Detector)>();
        var allDelivered = true;

        foreach (var entity in preMigration)
        {
            // An entity with no detector block still published under the "hst" fallback
            // GetDetectorName used to apply, so it must be retracted under that name.
            var detectors = entity.Detectors.Count > 0
                ? entity.Detectors.Select(d => d.Name)
                : ["hst"];

            foreach (var detector in detectors)
            {
                if (string.IsNullOrWhiteSpace(detector))
                    continue;
                if (!seen.Add((entity.EntityId, detector)))
                    continue;

                allDelivered &= await publish(
                    $"homeassistant/binary_sensor/{UniqueId.LegacyAnomalyId(entity.EntityId, detector)}/config",
                    string.Empty, true, ct);

                allDelivered &= await publish(
                    $"homeassistant/sensor/{UniqueId.LegacyScoreId(entity.EntityId, detector)}/config",
                    string.Empty, true, ct);
            }
        }

        return allDelivered;
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
    /// Computes which member ids (or a single null entry for the whole group) must be retracted
    /// from oldGroup's discovery entities on a ConfigChanged transition to newGroup (CR-02).
    /// newGroup null means the group_id was removed entirely. Pure decision logic — no MQTT I/O —
    /// so the shape-transition diff is unit-testable without a live broker. Returns null when
    /// nothing needs retracting (same shape, no members removed).
    /// </summary>
    internal static IEnumerable<string?>? ComputeRetractionEntities(GroupConfig oldGroup, GroupConfig? newGroup)
    {
        if (newGroup is null)
        {
            return UsesPerMemberEntities(oldGroup) ? oldGroup.Members.Cast<string?>() : [null];
        }

        var oldIsPeer = UsesPerMemberEntities(oldGroup);
        var newIsPeer = UsesPerMemberEntities(newGroup);

        if (oldIsPeer != newIsPeer)
        {
            // Entity shape changed (e.g. a peer_divergence group crossed the 2/3-member
            // boundary) — retract the OLD entity set entirely; the new shape's entities are
            // (re)published fresh by the caller.
            return oldIsPeer ? oldGroup.Members.Cast<string?>() : [null];
        }

        if (oldIsPeer)
        {
            var removed = oldGroup.Members
                .Except(newGroup.Members, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return removed.Count > 0 ? removed.Cast<string?>() : null;
        }

        // Joint groups (and same-shape peer groups) — no per-member diff needed.
        return null;
    }

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
