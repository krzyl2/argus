using Argus.Orchestrator.Config;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Health;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Generic;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Regression tests for NetDaemonHaEventSource CFG-04 live-filter behaviour (GAP 2).
///
/// Verifies that after ILiveEntitiesConfig.Swap() adds a new entity, the internal
/// _configuredEntities HashSet is rebuilt and TryMap accepts state_changed events
/// for the newly-added entity without requiring an event-source restart.
/// </summary>
public class NetDaemonHaEventSourceLiveFilterTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static EntitiesConfig MakeConfig(params string[] entityIds)
    {
        var cfg = new EntitiesConfig();
        foreach (var id in entityIds)
            cfg.Entities.Add(new EntityConfig
            {
                EntityId = id,
                Detectors = [new DetectorConfig { Name = "hst" }],
            });
        return cfg;
    }

    /// <summary>No-op IHaSensorRegistry — only needed to satisfy the ctor.</summary>
    private sealed class NullSensorRegistry : IHaSensorRegistry
    {
        public IReadOnlyList<HaSensorEntry> GetAll() => [];
        public IReadOnlyList<HaSensorEntry> GetFiltered(string q) => [];
        public void UpdateSnapshot(IReadOnlyList<HaStateDto> states, HashSet<string> trackedEntityIds) { }
    }

    private static NetDaemonHaEventSource MakeEventSource(ILiveEntitiesConfig liveConfig) =>
        new(
            new ConnectionSettings(),
            liveConfig,
            new ReconnectCooldown(),
            new ArgusHealthSignals(),
            new NullSensorRegistry(),
            NullLogger<NetDaemonHaEventSource>.Instance);

    // ─── Tests ───────────────────────────────────────────────────────────────

    /// <summary>
    /// CFG-04 / GAP 2: After Swap() adds a new entity, InternalConfiguredEntities must
    /// contain it — i.e. the ConfigChanged handler rebuilds the set.
    /// </summary>
    [Fact]
    public void AfterSwap_NewEntityIsAcceptedByFilter()
    {
        // Arrange
        var liveConfig = new LiveEntitiesConfig(MakeConfig("sensor.existing"));
        var src = MakeEventSource(liveConfig);

        var ts = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Before swap: sensor.new is NOT accepted
        var beforeSwap = NetDaemonHaEventSource.TryMap(
            "sensor.new", "25.0", ts,
            src.InternalConfiguredEntities,
            suppressBinarySensor: false, out _);

        Assert.False(beforeSwap,
            "sensor.new should be filtered out before Swap()");

        // Act: swap to config that includes the new entity
        liveConfig.Swap(MakeConfig("sensor.existing", "sensor.new"));

        // Assert: sensor.new is now accepted
        var afterSwap = NetDaemonHaEventSource.TryMap(
            "sensor.new", "25.0", ts,
            src.InternalConfiguredEntities,
            suppressBinarySensor: false, out var reading);

        Assert.True(afterSwap,
            "sensor.new should be accepted after Swap() adds it to the config");
        Assert.NotNull(reading);
        Assert.Equal("sensor.new", reading!.EntityId);
    }

    /// <summary>
    /// CFG-04 / GAP 2: After Swap() removes an entity, InternalConfiguredEntities must
    /// no longer contain it — i.e. the filter shrinks correctly.
    /// </summary>
    [Fact]
    public void AfterSwap_RemovedEntityIsRejectedByFilter()
    {
        // Arrange
        var liveConfig = new LiveEntitiesConfig(MakeConfig("sensor.a", "sensor.b"));
        var src = MakeEventSource(liveConfig);

        var ts = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Before swap: sensor.b IS accepted
        var beforeSwap = NetDaemonHaEventSource.TryMap(
            "sensor.b", "10.0", ts,
            src.InternalConfiguredEntities,
            suppressBinarySensor: false, out _);

        Assert.True(beforeSwap, "sensor.b should be accepted before Swap()");

        // Act: swap to config that removes sensor.b
        liveConfig.Swap(MakeConfig("sensor.a"));

        // Assert: sensor.b is now rejected
        var afterSwap = NetDaemonHaEventSource.TryMap(
            "sensor.b", "10.0", ts,
            src.InternalConfiguredEntities,
            suppressBinarySensor: false, out _);

        Assert.False(afterSwap,
            "sensor.b should be filtered out after Swap() removes it from the config");
    }

    /// <summary>
    /// GAP 1 regression: DI graph is resolvable — ILiveEntitiesConfig is accepted by the ctor
    /// (previously the ctor required raw EntitiesConfig which was removed from DI in Plan 03-02).
    /// </summary>
    [Fact]
    public void Constructor_AcceptsILiveEntitiesConfig_DoesNotThrow()
    {
        var liveConfig = new LiveEntitiesConfig(MakeConfig("sensor.test"));

        // Should not throw InvalidOperationException (old EntitiesConfig injection gap)
        var src = MakeEventSource(liveConfig);

        Assert.NotNull(src);
    }
}
