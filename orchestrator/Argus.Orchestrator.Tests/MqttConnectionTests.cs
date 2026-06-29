using Argus.Orchestrator.Config;
using Argus.Orchestrator.Mqtt;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.Orchestrator.Tests;

public class MqttConnectionTests
{
    private static ConnectionSettings MakeSettings() => new()
    {
        MqttHost = "localhost",
        MqttPort = 1883,
        MqttUser = "testuser",
        MqttPassword = "testpass",
    };

    [Fact]
    public void ConnectOptions_WillTopic_EndsWithAvailability()
    {
        var conn = new MqttConnection(MakeSettings(), NullLogger<MqttConnection>.Instance);
        Assert.EndsWith("/availability", conn.ConnectOptions.WillTopic);
    }

    [Fact]
    public void ConnectOptions_WillPayload_IsOffline()
    {
        var conn = new MqttConnection(MakeSettings(), NullLogger<MqttConnection>.Instance);
        var payload = System.Text.Encoding.UTF8.GetString(conn.ConnectOptions.WillPayload!);
        Assert.Equal("offline", payload);
    }

    [Fact]
    public void ConnectOptions_WillRetain_IsTrue()
    {
        var conn = new MqttConnection(MakeSettings(), NullLogger<MqttConnection>.Instance);
        Assert.True(conn.ConnectOptions.WillRetain);
    }

    [Fact]
    public void ConnectOptions_WillIsConfiguredBeforeConnect_NoLiveConnection()
    {
        // This test verifies that ConnectOptions is populated (LWT is configured in the
        // constructor, before any ConnectAsync call — PITFALL 6 mitigation).
        // No live broker needed: we only inspect the options object.
        var conn = new MqttConnection(MakeSettings(), NullLogger<MqttConnection>.Instance);
        Assert.NotNull(conn.ConnectOptions.WillTopic);
        Assert.NotEmpty(conn.ConnectOptions.WillTopic);
    }

    [Fact]
    public void StatePublisher_FlagTopic_Correct()
    {
        var publisher = new StatePublisher();
        Assert.Equal("argus/sensor_salon_temperatura/flag/state",
            publisher.FlagTopic("sensor.salon_temperatura"));
    }

    [Fact]
    public void StatePublisher_ScoreTopic_Correct()
    {
        var publisher = new StatePublisher();
        Assert.Equal("argus/sensor_salon_temperatura/score/state",
            publisher.ScoreTopic("sensor.salon_temperatura"));
    }

    [Fact]
    public void StatePublisher_BridgeAvailabilityTopic_Constant()
    {
        Assert.Equal("argus/bridge/availability", StatePublisher.BridgeAvailabilityTopic);
    }
}
