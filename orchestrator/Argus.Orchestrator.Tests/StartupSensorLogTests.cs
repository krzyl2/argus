using Argus.Orchestrator.Ha;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for NetDaemonHaEventSource.SelectDiscoverableSensors (UICFG-05).
/// Fully offline — verifies filtering logic without a live HA connection.
/// </summary>
public class StartupSensorLogTests
{
    private static readonly HashSet<string> ConfiguredEntities = new(StringComparer.OrdinalIgnoreCase)
    {
        "sensor.configured_temp",
        "sensor.configured_humidity",
    };

    [Fact]
    public void SelectDiscoverableSensors_NumericUnconfigured_IsIncluded()
    {
        var states = new[]
        {
            ("sensor.outdoor_temp", (string?)"18.5"),
        };

        var result = NetDaemonHaEventSource.SelectDiscoverableSensors(states, ConfiguredEntities);

        Assert.Single(result);
        Assert.Equal("sensor.outdoor_temp", result[0].EntityId);
        Assert.Equal(18.5, result[0].Value, precision: 5);
    }

    [Fact]
    public void SelectDiscoverableSensors_ConfiguredEntity_IsExcluded()
    {
        var states = new[]
        {
            ("sensor.configured_temp", (string?)"21.0"),
        };

        var result = NetDaemonHaEventSource.SelectDiscoverableSensors(states, ConfiguredEntities);

        Assert.Empty(result);
    }

    [Fact]
    public void SelectDiscoverableSensors_NonNumericState_IsExcluded()
    {
        var states = new[]
        {
            ("sensor.broken", (string?)"unavailable"),
            ("sensor.unknown_state", (string?)"unknown"),
            ("sensor.null_state", (string?)null),
        };

        var result = NetDaemonHaEventSource.SelectDiscoverableSensors(states, ConfiguredEntities);

        Assert.Empty(result);
    }

    [Fact]
    public void SelectDiscoverableSensors_MixedInput_ReturnsOnlyUnconfiguredNumeric()
    {
        // Mix of: configured numeric, unconfigured numeric, non-numeric, configured non-numeric
        var states = new[]
        {
            ("sensor.configured_temp", (string?)"21.5"),       // configured → excluded
            ("sensor.outdoor_wind", (string?)"12.3"),          // unconfigured numeric → included
            ("sensor.indoor_co2", (string?)"450.0"),           // unconfigured numeric → included
            ("sensor.door_contact", (string?)"on"),            // non-numeric → excluded
            ("sensor.configured_humidity", (string?)"55.0"),   // configured → excluded
            ("sensor.motion_hallway", (string?)"unavailable"), // non-numeric → excluded
        };

        var result = NetDaemonHaEventSource.SelectDiscoverableSensors(states, ConfiguredEntities);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.EntityId == "sensor.outdoor_wind" && Math.Abs(r.Value - 12.3) < 0.001);
        Assert.Contains(result, r => r.EntityId == "sensor.indoor_co2" && Math.Abs(r.Value - 450.0) < 0.001);
    }

    [Fact]
    public void SelectDiscoverableSensors_EmptyInput_ReturnsEmpty()
    {
        var result = NetDaemonHaEventSource.SelectDiscoverableSensors(
            Enumerable.Empty<(string, string?)>(),
            ConfiguredEntities);

        Assert.Empty(result);
    }

    [Fact]
    public void SelectDiscoverableSensors_NegativeNumericValue_IsIncluded()
    {
        // Negative values (e.g. outdoor temp in winter) must be included
        var states = new[]
        {
            ("sensor.outdoor_temp_winter", (string?)"-15.3"),
        };

        var result = NetDaemonHaEventSource.SelectDiscoverableSensors(states, ConfiguredEntities);

        Assert.Single(result);
        Assert.Equal(-15.3, result[0].Value, precision: 5);
    }

    [Fact]
    public void SelectDiscoverableSensors_ConfiguredEntityCaseInsensitive_IsExcluded()
    {
        // Entity IDs in HA are lowercase, but guard against case mismatches
        var states = new[]
        {
            ("Sensor.Configured_Temp", (string?)"22.0"),
        };

        var result = NetDaemonHaEventSource.SelectDiscoverableSensors(states, ConfiguredEntities);

        Assert.Empty(result);
    }
}
