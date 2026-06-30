using System.Text.Json;
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
        Assert.Equal("argus_sensor_salon_temperatura_hst_anomaly", uniqueId);
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
        Assert.Equal("argus_sensor_salon_temperatura_hst_score", uniqueId);
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
}
