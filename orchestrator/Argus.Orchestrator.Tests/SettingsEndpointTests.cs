using System.Text.Json;
using System.Text.RegularExpressions;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Web;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Redaction + field-presence tests for GET /api/settings (D-06/D-07). Exercises the real
/// SettingsProjection.Build against sentinel secret values, asserting the JSON response
/// never leaks secret content or secret-shaped property names. Fully offline — no HTTP
/// server needed.
/// </summary>
public class SettingsEndpointTests
{
    private static ConnectionSettings MakeSettingsWithSentinelSecrets()
    {
        return new ConnectionSettings
        {
            HaUrl = "http://ha.local:8123",
            HaToken = "SENTINEL_HA",
            MqttHost = "mqtt.local",
            MqttUser = "SENTINEL_USER",
            MqttPassword = "SENTINEL_MQTT",
            DetectorEndpoint = "https://gpu-host:50051",
            TlsCa = "SENTINEL_CA",
            TlsCert = "SENTINEL_CERT",
            TlsKey = "SENTINEL_KEY",
            InfluxUrl = "http://influx:8086",
            InfluxToken = "SENTINEL_INFLUX",
            InfluxOrg = "home",
            InfluxBucket = "ha",
            BatchIntervalMinutes = 10,
            NightlyFitHour = 2,
        };
    }

    private static IConfiguration MakeConfig(string? logLevel)
    {
        var data = new Dictionary<string, string?>();
        if (logLevel is not null) data["Logging:LogLevel:Default"] = logLevel;
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void Build_ExposesOnlyNonSensitiveFields()
    {
        var settings = MakeSettingsWithSentinelSecrets();
        var config = MakeConfig("Information");

        var json = JsonSerializer.Serialize(SettingsProjection.Build(settings, config));

        Assert.DoesNotContain("SENTINEL_", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_NoSecretPropertyKeys()
    {
        var settings = MakeSettingsWithSentinelSecrets();
        var config = MakeConfig("Information");

        var json = JsonSerializer.Serialize(SettingsProjection.Build(settings, config));
        using var doc = JsonDocument.Parse(json);

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            Assert.False(
                Regex.IsMatch(property.Name, "token|password|secret|key", RegexOptions.IgnoreCase),
                $"Property '{property.Name}' matches a secret-shaped name.");
        }
    }

    [Fact]
    public void Build_IncludesAllSixNonSensitiveFields()
    {
        var settings = MakeSettingsWithSentinelSecrets();
        var config = MakeConfig("Information");

        var json = JsonSerializer.Serialize(SettingsProjection.Build(settings, config));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("detectorEndpoint", out _));
        Assert.True(root.TryGetProperty("influxUrl", out _));
        Assert.True(root.TryGetProperty("influxBucket", out _));
        Assert.True(root.TryGetProperty("batchIntervalMinutes", out _));
        Assert.True(root.TryGetProperty("nightlyFitHour", out _));
        Assert.True(root.TryGetProperty("logLevel", out _));
    }

    [Fact]
    public void Build_LogLevelFromConfiguration()
    {
        var settings = MakeSettingsWithSentinelSecrets();

        var withLogLevel = MakeConfig("Debug");
        var jsonWith = JsonSerializer.Serialize(SettingsProjection.Build(settings, withLogLevel));
        using var docWith = JsonDocument.Parse(jsonWith);
        Assert.Equal("Debug", docWith.RootElement.GetProperty("logLevel").GetString());

        var withoutLogLevel = MakeConfig(null);
        var jsonWithout = JsonSerializer.Serialize(SettingsProjection.Build(settings, withoutLogLevel));
        using var docWithout = JsonDocument.Parse(jsonWithout);
        Assert.Equal(JsonValueKind.Null, docWithout.RootElement.GetProperty("logLevel").ValueKind);
    }
}
