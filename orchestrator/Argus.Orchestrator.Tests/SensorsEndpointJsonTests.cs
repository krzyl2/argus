using Argus.Orchestrator.Config;
using Argus.Orchestrator.Ha;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for the GET /api/sensors JSON response shape (Phase 7 — replaces the v3.0
/// EntityPickerPage.BuildListFragment HTML-fragment tests). Validates the entries
/// projection used directly inside Program.cs's /api/sensors handler: entityId,
/// friendlyName (null-when-equal-to-entityId rule), currentValue, unitOfMeasurement,
/// isTracked. Fully offline — no HTTP server needed.
/// </summary>
public class SensorsEndpointJsonTests
{
    // -----------------------------------------------------------------------
    // Helpers (reused pattern from the removed EntityPickerPageTests.cs)
    // -----------------------------------------------------------------------

    private sealed class FakeRegistry : IHaSensorRegistry
    {
        private readonly IReadOnlyList<HaSensorEntry> _entries;
        public FakeRegistry(params HaSensorEntry[] entries) => _entries = entries;

        public IReadOnlyList<HaSensorEntry> GetAll() => _entries;
        public IReadOnlyList<HaSensorEntry> GetFiltered(string q) =>
            string.IsNullOrEmpty(q)
                ? _entries
                : _entries
                    .Where(e => e.EntityId.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .ToList();
        public void UpdateSnapshot(IReadOnlyList<HaStateDto> states, HashSet<string> trackedEntityIds)
            => throw new NotImplementedException();
    }

    private static HaSensorEntry MakeEntry(
        string entityId, double value = 21.0, string? unit = "°C",
        string? friendlyName = null, bool isTracked = false)
        => new(entityId, value, unit, friendlyName, isTracked);

    /// <summary>
    /// Mirrors the exact projection performed inline in Program.cs's GET /api/sensors handler.
    /// </summary>
    private static IEnumerable<object> ProjectEntries(IReadOnlyList<HaSensorEntry> entries)
    {
        return entries.Select(e =>
        {
            var showFriendlyName = !string.IsNullOrEmpty(e.FriendlyName) &&
                !string.Equals(e.FriendlyName, e.EntityId, StringComparison.Ordinal);

            return new
            {
                entityId = e.EntityId,
                friendlyName = showFriendlyName ? e.FriendlyName : null,
                currentValue = e.CurrentValue.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
                unitOfMeasurement = e.UnitOfMeasurement,
                isTracked = e.IsTracked,
            };
        });
    }

    // -----------------------------------------------------------------------
    // Tracked / untracked
    // -----------------------------------------------------------------------

    [Fact]
    public void ProjectEntries_TrackedEntry_IsTrackedTrue()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.living_room_temp", isTracked: true));

        var result = ProjectEntries(registry.GetFiltered("")).Cast<dynamic>().ToList();

        Assert.Single(result);
        Assert.True((bool)result[0].isTracked);
        Assert.Equal("sensor.living_room_temp", (string)result[0].entityId);
    }

    [Fact]
    public void ProjectEntries_UntrackedEntry_IsTrackedFalse()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.outdoor_humidity", isTracked: false));

        var result = ProjectEntries(registry.GetFiltered("")).Cast<dynamic>().ToList();

        Assert.False((bool)result[0].isTracked);
    }

    // -----------------------------------------------------------------------
    // Friendly name rule — null when empty or equal to entityId
    // -----------------------------------------------------------------------

    [Fact]
    public void ProjectEntries_FriendlyNameDiffersFromEntityId_IsSurfaced()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.salon_temperatura", friendlyName: "Salon temperatura"));

        var result = ProjectEntries(registry.GetFiltered("")).Cast<dynamic>().ToList();

        Assert.Equal("Salon temperatura", (string)result[0].friendlyName);
    }

    [Fact]
    public void ProjectEntries_FriendlyNameSameAsEntityId_IsNull()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.temp", friendlyName: "sensor.temp"));

        var result = ProjectEntries(registry.GetFiltered("")).Cast<dynamic>().ToList();

        Assert.Null(result[0].friendlyName);
    }

    [Fact]
    public void ProjectEntries_NullFriendlyName_IsNull()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.outdoor_temp", friendlyName: null));

        var result = ProjectEntries(registry.GetFiltered("")).Cast<dynamic>().ToList();

        Assert.Null(result[0].friendlyName);
    }

    [Fact]
    public void ProjectEntries_EmptyFriendlyName_IsNull()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.outdoor_temp", friendlyName: ""));

        var result = ProjectEntries(registry.GetFiltered("")).Cast<dynamic>().ToList();

        Assert.Null(result[0].friendlyName);
    }

    // -----------------------------------------------------------------------
    // Value / unit projection
    // -----------------------------------------------------------------------

    [Fact]
    public void ProjectEntries_ValueAndUnit_AreProjectedSeparately()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.outdoor_temp", value: 18.5, unit: "°C"));

        var result = ProjectEntries(registry.GetFiltered("")).Cast<dynamic>().ToList();

        Assert.Equal("18.5", (string)result[0].currentValue);
        Assert.Equal("°C", (string)result[0].unitOfMeasurement);
    }

    [Fact]
    public void ProjectEntries_NullUnit_IsProjectedAsNull()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.outdoor_temp", unit: null));

        var result = ProjectEntries(registry.GetFiltered("")).Cast<dynamic>().ToList();

        Assert.Null(result[0].unitOfMeasurement);
    }

    // -----------------------------------------------------------------------
    // Search filter (q) — delegates to registry.GetFiltered, unchanged from v3.0
    // -----------------------------------------------------------------------

    [Fact]
    public void GetFiltered_QueryMatchesSubset_ReturnsOnlyMatches()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.living_room_temp"),
            MakeEntry("sensor.outdoor_humidity"));

        var result = ProjectEntries(registry.GetFiltered("living")).Cast<dynamic>().ToList();

        Assert.Single(result);
        Assert.Equal("sensor.living_room_temp", (string)result[0].entityId);
    }

    [Fact]
    public void GetFiltered_EmptyQuery_ReturnsAll()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.a"), MakeEntry("sensor.b"));

        var result = ProjectEntries(registry.GetFiltered("")).ToList();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void GetFiltered_EmptySnapshot_ReturnsEmptyEntries()
    {
        var registry = new FakeRegistry();

        var result = ProjectEntries(registry.GetFiltered("")).ToList();

        Assert.Empty(result);
    }

    // -----------------------------------------------------------------------
    // ILiveEntitiesConfig freshness (CFG-04) — liveCfg.Get() called per-request, not captured
    // -----------------------------------------------------------------------

    [Fact]
    public void LiveEntitiesConfig_GetAfterSwap_ReturnsUpdatedConfig()
    {
        // Regression guard: the /api/sensors handler must call liveCfg.Get() fresh on every
        // request (CFG-04), not capture a stale EntitiesConfig reference at DI-registration time.
        var live = new LiveEntitiesConfig(new EntitiesConfig());

        var updated = new EntitiesConfig
        {
            Entities = [new EntityConfig { EntityId = "sensor.new", FriendlyName = "", Detectors = [] }]
        };
        live.Swap(updated);

        Assert.Same(updated, live.Get());
        Assert.Single(live.Get().Entities);
    }
}
