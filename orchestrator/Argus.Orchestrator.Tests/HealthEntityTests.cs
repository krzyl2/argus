using System.Text.Json;
using Argus.Orchestrator.Mqtt;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for HealthEvaluator truth table and BuildHealthBinarySensorConfig discovery payload (HEALTH-01).
/// All tests are fully offline — no MQTT broker or HA required.
/// </summary>
public class HealthEntityTests
{
    // ── HealthEvaluator truth table ──────────────────────────────────────────

    [Fact]
    public void Evaluate_AllTrue_ReturnsOff()
    {
        // All three signals healthy → add-on is healthy → "OFF" (device_class problem: OFF = no problem)
        Assert.Equal("OFF", HealthEvaluator.Evaluate(detectorServing: true, haConnected: true, mqttConnected: true));
    }

    [Fact]
    public void Evaluate_DetectorNotServing_ReturnsOn()
    {
        Assert.Equal("ON", HealthEvaluator.Evaluate(detectorServing: false, haConnected: true, mqttConnected: true));
    }

    [Fact]
    public void Evaluate_HaNotConnected_ReturnsOn()
    {
        Assert.Equal("ON", HealthEvaluator.Evaluate(detectorServing: true, haConnected: false, mqttConnected: true));
    }

    [Fact]
    public void Evaluate_MqttNotConnected_ReturnsOn()
    {
        Assert.Equal("ON", HealthEvaluator.Evaluate(detectorServing: true, haConnected: true, mqttConnected: false));
    }

    [Fact]
    public void Evaluate_AllFalse_ReturnsOn()
    {
        Assert.Equal("ON", HealthEvaluator.Evaluate(detectorServing: false, haConnected: false, mqttConnected: false));
    }

    // ── Health discovery payload ─────────────────────────────────────────────

    private static JsonDocument ParseHealthPayload()
        => JsonDocument.Parse(DiscoveryPublisher.BuildHealthBinarySensorConfig());

    [Fact]
    public void HealthDiscoveryPayload_DeviceClassIsProblem()
    {
        using var doc = ParseHealthPayload();
        Assert.Equal("problem", doc.RootElement.GetProperty("device_class").GetString());
    }

    [Fact]
    public void HealthDiscoveryPayload_UniqueIdIsArgusAddonHealth()
    {
        using var doc = ParseHealthPayload();
        Assert.Equal("argus_addon_health", doc.RootElement.GetProperty("unique_id").GetString());
    }

    [Fact]
    public void HealthDiscoveryPayload_UniqueIdEqualsObjectId()
    {
        using var doc = ParseHealthPayload();
        var uniqueId = doc.RootElement.GetProperty("unique_id").GetString();
        var objectId = doc.RootElement.GetProperty("object_id").GetString();
        Assert.Equal(uniqueId, objectId);
    }

    [Fact]
    public void HealthDiscoveryPayload_NameIsPolish()
    {
        // D8: friendly name must be "Argus — status" (Polish em-dash variant)
        using var doc = ParseHealthPayload();
        Assert.Equal("Argus — status", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void HealthDiscoveryPayload_PayloadOnOff()
    {
        using var doc = ParseHealthPayload();
        Assert.Equal("ON", doc.RootElement.GetProperty("payload_on").GetString());
        Assert.Equal("OFF", doc.RootElement.GetProperty("payload_off").GetString());
    }

    [Fact]
    public void HealthDiscoveryPayload_StateTopic()
    {
        using var doc = ParseHealthPayload();
        Assert.Equal("argus/addon/health/state", doc.RootElement.GetProperty("state_topic").GetString());
    }

    [Fact]
    public void HealthDiscoveryPayload_AvailabilityTopicIsBridge()
    {
        using var doc = ParseHealthPayload();
        Assert.Equal("argus/bridge/availability", doc.RootElement.GetProperty("availability_topic").GetString());
    }

    [Fact]
    public void HealthDiscoveryPayload_DeviceIdentifiersContainArgusAddon()
    {
        using var doc = ParseHealthPayload();
        var identifiers = doc.RootElement.GetProperty("device").GetProperty("identifiers");
        Assert.Equal(JsonValueKind.Array, identifiers.ValueKind);
        Assert.Contains(identifiers.EnumerateArray(), el => el.GetString() == "argus_addon");
    }

    [Fact]
    public void HealthDiscoveryPayload_DeviceNameIsArgus()
    {
        using var doc = ParseHealthPayload();
        Assert.Equal("Argus", doc.RootElement.GetProperty("device").GetProperty("name").GetString());
    }
}
