using Argus.Orchestrator.Config;
using Argus.Orchestrator.Health;
using Argus.Orchestrator.Web;
using System.Text.Json;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for HealthProjection — the allowlist boundary backing GET /api/health
/// (QUICK-dashboard-real-data). Targets the batch-overdue logic and the
/// camelCase/no-secret wire contract the frontend depends on. Fully offline.
/// </summary>
public class HealthProjectionTests
{
    // ─── BuildBatchComponent ───────────────────────────────────────────────

    [Fact]
    public void BuildBatchComponent_NullInfluxUrl_IsIdleStreamingOnly()
    {
        var component = HealthProjection.BuildBatchComponent(
            influxUrl: null, lastRunUtc: null, intervalMinutes: 10, now: DateTimeOffset.UtcNow);

        Assert.Equal("idle", component.Status);
        Assert.Contains("streaming-only", component.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildBatchComponent_ConfiguredInflux_NullLastRunUtc_IsWarnNotRunYet()
    {
        var component = HealthProjection.BuildBatchComponent(
            influxUrl: "http://influx:8086", lastRunUtc: null, intervalMinutes: 10, now: DateTimeOffset.UtcNow);

        Assert.Equal("warn", component.Status);
        Assert.Equal("Not run yet", component.Detail);
    }

    [Fact]
    public void BuildBatchComponent_RecentRun_WithinInterval_IsOk()
    {
        var now = DateTimeOffset.UtcNow;
        var component = HealthProjection.BuildBatchComponent(
            influxUrl: "http://influx:8086", lastRunUtc: now.AddMinutes(-3), intervalMinutes: 10, now: now);

        Assert.Equal("ok", component.Status);
    }

    [Fact]
    public void BuildBatchComponent_OverdueRun_IsWarnWithOverdueDetail()
    {
        var now = DateTimeOffset.UtcNow;
        var component = HealthProjection.BuildBatchComponent(
            influxUrl: "http://influx:8086", lastRunUtc: now.AddMinutes(-20), intervalMinutes: 10, now: now);

        Assert.Equal("warn", component.Status);
        Assert.Contains("Overdue", component.Detail);
    }

    // ─── Build (full composition) ──────────────────────────────────────────

    [Fact]
    public void Build_HaDisconnected_YieldsErrorComponentAndFalseConnected()
    {
        var signals = new ArgusHealthSignals { HaConnected = false, DetectorConnected = true };
        var settings = new ConnectionSettings();

        var result = HealthProjection.Build(
            signals, mqttConnected: true, haEntityCount: 42, settings, lastBatchRunUtc: null, now: DateTimeOffset.UtcNow);

        var haComponent = Assert.Single(result.Components, c => c.Key == "homeAssistant");
        Assert.Equal("error", haComponent.Status);
        Assert.False(result.HomeAssistant.Connected);
        Assert.Equal(42, result.HomeAssistant.EntityCount);
    }

    // ─── Camel-case + no-secret wire contract ──────────────────────────────

    [Fact]
    public void Build_SerializedCamelCase_ContainsExpectedKeysAndNoSecrets()
    {
        var signals = new ArgusHealthSignals { HaConnected = true, DetectorConnected = true };
        var settings = new ConnectionSettings
        {
            HaToken = "super-secret-ha-token",
            MqttUser = "mqttuser",
            MqttPassword = "super-secret-mqtt-password",
            InfluxToken = "super-secret-influx-token",
            DetectorEndpoint = "https://gpu-host:50051",
            InfluxUrl = "http://influx:8086",
            InfluxBucket = "homeassistant",
        };

        var result = HealthProjection.Build(
            signals, mqttConnected: true, haEntityCount: 5, settings, lastBatchRunUtc: DateTimeOffset.UtcNow, now: DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("homeAssistant", json);
        Assert.Contains("entityCount", json);
        Assert.Contains("components", json);

        Assert.DoesNotContain("HaToken", json);
        Assert.DoesNotContain(settings.HaToken, json);
        Assert.DoesNotContain(settings.MqttPassword, json);
        Assert.DoesNotContain("InfluxToken", json);
        Assert.DoesNotContain(settings.InfluxToken, json);
    }
}
