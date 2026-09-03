using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Mqtt;
using Xunit;

namespace Argus.Orchestrator.Tests;

public class DiscoveryPayloadTests
{
    private static EntityConfig MakeEntity() => new()
    {
        EntityId = "sensor.salon_temperatura",
        FriendlyName = "Salon temperatura",
        Detectors = [new DetectorConfig { Name = "hst", Params = [] }]
    };

    [Fact]
    public void BinarySensorPayload_ContainsCorrectUniqueId()
    {
        var entity = MakeEntity();
        var json = DiscoveryPublisher.BuildBinarySensorConfig(entity);
        var doc = JsonDocument.Parse(json);

        var uniqueId = doc.RootElement.GetProperty("unique_id").GetString();
        Assert.Equal("argus_sensor_salon_temperatura_anomaly", uniqueId);
    }

    [Fact]
    public void BinarySensorPayload_UniqueIdEqualsObjectId()
    {
        var entity = MakeEntity();
        var json = DiscoveryPublisher.BuildBinarySensorConfig(entity);
        var doc = JsonDocument.Parse(json);

        var uniqueId = doc.RootElement.GetProperty("unique_id").GetString();
        var objectId = doc.RootElement.GetProperty("object_id").GetString();
        Assert.Equal(uniqueId, objectId);
    }

    [Fact]
    public void BinarySensorPayload_DeviceIdentifiersContainsSlug()
    {
        var entity = MakeEntity();
        var json = DiscoveryPublisher.BuildBinarySensorConfig(entity);
        var doc = JsonDocument.Parse(json);

        var identifiers = doc.RootElement.GetProperty("device").GetProperty("identifiers");
        Assert.Equal(JsonValueKind.Array, identifiers.ValueKind);
        Assert.Contains(identifiers.EnumerateArray(), el => el.GetString() == "sensor_salon_temperatura");
    }

    [Fact]
    public void BinarySensorPayload_DeviceClassIsProblem()
    {
        var entity = MakeEntity();
        var json = DiscoveryPublisher.BuildBinarySensorConfig(entity);
        var doc = JsonDocument.Parse(json);

        var deviceClass = doc.RootElement.GetProperty("device_class").GetString();
        Assert.Equal("problem", deviceClass);
    }

    [Fact]
    public void BinarySensorPayload_AvailabilityTopicIsBridgeLevel()
    {
        var entity = MakeEntity();
        var json = DiscoveryPublisher.BuildBinarySensorConfig(entity);
        var doc = JsonDocument.Parse(json);

        // CR-05: per-entity availability list (bridge-level + per-entity), not a single availability_topic.
        var availability = doc.RootElement.GetProperty("availability");
        Assert.Equal(JsonValueKind.Array, availability.ValueKind);
        Assert.Contains(
            availability.EnumerateArray(),
            el => el.GetProperty("topic").GetString() == "argus/bridge/availability");
    }

    [Fact]
    public void BinarySensorPayload_PayloadAvailableOnline()
    {
        var entity = MakeEntity();
        var json = DiscoveryPublisher.BuildBinarySensorConfig(entity);
        var doc = JsonDocument.Parse(json);

        // CR-05: online/offline payloads are carried on each availability list entry.
        var availability = doc.RootElement.GetProperty("availability");
        Assert.All(availability.EnumerateArray(), el =>
        {
            Assert.Equal("online",  el.GetProperty("payload_available").GetString());
            Assert.Equal("offline", el.GetProperty("payload_not_available").GetString());
        });
    }

    [Fact]
    public void BinarySensorPayload_StateTopicCorrect()
    {
        var entity = MakeEntity();
        var json = DiscoveryPublisher.BuildBinarySensorConfig(entity);
        var doc = JsonDocument.Parse(json);

        var stateTopic = doc.RootElement.GetProperty("state_topic").GetString();
        Assert.Equal("argus/sensor_salon_temperatura/flag/state", stateTopic);
    }

    [Fact]
    public void SensorPayload_UniqueIdAndObjectIdMatch()
    {
        var entity = MakeEntity();
        var json = DiscoveryPublisher.BuildSensorConfig(entity);
        var doc = JsonDocument.Parse(json);

        var uniqueId = doc.RootElement.GetProperty("unique_id").GetString();
        var objectId = doc.RootElement.GetProperty("object_id").GetString();
        Assert.Equal("argus_sensor_salon_temperatura_score", uniqueId);
        Assert.Equal(uniqueId, objectId);
    }

    [Fact]
    public void FriendlyName_AppendAnomalia()
    {
        Assert.Equal("Salon temperatura anomalia", FriendlyName.ForAnomaly("Salon temperatura"));
    }

    [Fact]
    public void FriendlyName_PreservesPolishCharacters()
    {
        Assert.Equal("Zewnątrz temperatura anomalia", FriendlyName.ForAnomaly("Zewnątrz temperatura"));
    }

    // ─── Group entity shape (Pitfall 3 — UsesPerMemberEntities count-awareness) ────

    private static GroupConfig MakePeerGroup(string groupId, params string[] members) => new()
    {
        GroupId = groupId,
        FriendlyName = groupId,
        Mode = "peer_divergence",
        Detector = "peer_divergence",
        Members = [.. members],
    };

    [Fact]
    public async Task PublishGroupAsync_TwoMemberPeerGroup_PublishesOneGroupLevelPairNotPerMember()
    {
        // A 2-member peer_divergence group's pairwise-delta score is a single derived value with
        // no per-member attribution (Rule 9: encode WHY) — it must publish ONE group-level
        // binary_sensor + sensor pair (memberId=null), not two per-member entities.
        var calls = new List<(string Topic, string Payload, bool Retain)>();
        Task Publish(string topic, string payload, bool retain, CancellationToken _)
        {
            calls.Add((topic, payload, retain));
            return Task.CompletedTask;
        }

        var group = MakePeerGroup("water_pressure_pair", "sensor.pressure_a", "sensor.pressure_b");

        await DiscoveryPublisher.PublishGroupAsync(Publish, group, CancellationToken.None);

        // 2 config topics total (one binary_sensor + one sensor), not 4 (per-member would be 2x2)
        Assert.Equal(2, calls.Count);
        var flagId = UniqueId.GroupFlagId(group.GroupId);
        var scoreId = UniqueId.GroupScoreId(group.GroupId);
        Assert.Contains(calls, c => c.Topic == $"homeassistant/binary_sensor/{flagId}/config");
        Assert.Contains(calls, c => c.Topic == $"homeassistant/sensor/{scoreId}/config");
    }

    [Fact]
    public void BuildGroupBinarySensorConfig_TwoMemberPeerGroup_UsesNullMemberScoping()
    {
        var group = MakePeerGroup("water_pressure_pair", "sensor.pressure_a", "sensor.pressure_b");

        var json = DiscoveryPublisher.BuildGroupBinarySensorConfig(group, memberId: null);
        var doc = JsonDocument.Parse(json);

        var uniqueId = doc.RootElement.GetProperty("unique_id").GetString();
        Assert.Equal(UniqueId.GroupFlagId(group.GroupId), uniqueId);
    }

    [Fact]
    public async Task PublishGroupAsync_ThreeMemberPeerGroup_StillPublishesPerMemberPairs()
    {
        // N>=3 classic peer_divergence behavior is unchanged — one pair per member.
        var calls = new List<(string Topic, string Payload, bool Retain)>();
        Task Publish(string topic, string payload, bool retain, CancellationToken _)
        {
            calls.Add((topic, payload, retain));
            return Task.CompletedTask;
        }

        var group = MakePeerGroup("garden_tires", "sensor.tire_fl", "sensor.tire_fr", "sensor.tire_rl");

        await DiscoveryPublisher.PublishGroupAsync(Publish, group, CancellationToken.None);

        // 3 members x 2 topics each = 6
        Assert.Equal(6, calls.Count);
        foreach (var member in new[] { "sensor.tire_fl", "sensor.tire_fr", "sensor.tire_rl" })
        {
            var flagId = UniqueId.GroupFlagId(group.GroupId, member);
            var scoreId = UniqueId.GroupScoreId(group.GroupId, member);
            Assert.Contains(calls, c => c.Topic == $"homeassistant/binary_sensor/{flagId}/config");
            Assert.Contains(calls, c => c.Topic == $"homeassistant/sensor/{scoreId}/config");
        }
    }
}
