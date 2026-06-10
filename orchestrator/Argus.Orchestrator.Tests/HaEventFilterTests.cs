using Argus.Orchestrator.Ha;
using System;
using System.Collections.Generic;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for NetDaemonHaEventSource.TryMap — entity filter and numeric validation.
/// These tests operate against the static mapping function in isolation (no live HA required).
/// </summary>
public class HaEventFilterTests
{
    private static readonly HashSet<string> ConfiguredEntities = new(StringComparer.OrdinalIgnoreCase)
    {
        "sensor.salon_temperatura"
    };

    [Fact]
    public void TryMap_ConfiguredEntityWithNumericState_ReturnsHaReading()
    {
        var lastChanged = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var result = NetDaemonHaEventSource.TryMap(
            entityId: "sensor.salon_temperatura",
            stateValue: "21.5",
            lastChanged: lastChanged,
            configuredEntities: ConfiguredEntities,
            suppressBinarySensor: false,
            out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        Assert.Equal("sensor.salon_temperatura", reading!.EntityId);
        Assert.Equal(21.5, reading.Value, precision: 5);
        Assert.Equal(new DateTimeOffset(lastChanged), reading.LastChanged);
        Assert.False(reading.SuppressBinarySensor);
    }

    [Fact]
    public void TryMap_UnrelatedEntity_IsDropped()
    {
        var result = NetDaemonHaEventSource.TryMap(
            entityId: "sensor.unrelated",
            stateValue: "20.0",
            lastChanged: DateTime.UtcNow,
            configuredEntities: ConfiguredEntities,
            suppressBinarySensor: false,
            out var reading);

        Assert.False(result);
        Assert.Null(reading);
    }

    [Fact]
    public void TryMap_NonNumericState_IsDropped()
    {
        var result = NetDaemonHaEventSource.TryMap(
            entityId: "sensor.salon_temperatura",
            stateValue: "unavailable",
            lastChanged: DateTime.UtcNow,
            configuredEntities: ConfiguredEntities,
            suppressBinarySensor: false,
            out var reading);

        Assert.False(result);
        Assert.Null(reading);
    }

    [Fact]
    public void TryMap_ConfiguredEntityWithSuppressTrue_ReturnsReadingWithSuppressTrue()
    {
        var result = NetDaemonHaEventSource.TryMap(
            entityId: "sensor.salon_temperatura",
            stateValue: "22.0",
            lastChanged: DateTime.UtcNow,
            configuredEntities: ConfiguredEntities,
            suppressBinarySensor: true,
            out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        Assert.True(reading!.SuppressBinarySensor);
    }
}
