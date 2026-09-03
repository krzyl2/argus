using Argus.Orchestrator.Config;
using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Web;
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
        public void UpdateSnapshot(
            IReadOnlyList<HaStateDto> states, HashSet<string> trackedEntityIds,
            IReadOnlyDictionary<string, string?>? entityAreaNames = null)
            => throw new NotImplementedException();
        public bool Upsert(HaStateDto state, bool isTracked) => throw new NotImplementedException();
    }

    private static HaSensorEntry MakeEntry(
        string entityId, double value = 21.0, string? unit = "°C",
        string? friendlyName = null, bool isTracked = false)
        => new(entityId, value, unit, friendlyName, isTracked, null, "sensor");

    /// <summary>
    /// Mirrors the exact projection performed inline in Program.cs's GET /api/sensors handler.
    /// G-14-1 fix #2: isTracked is now config-sourced via SensorTracking.TrackedIds (not
    /// e.IsTracked, the stale HA registry snapshot) — pass the EntitiesConfig liveCfg.Get()
    /// would return, exactly like the handler does.
    /// QUICK-warmup-status: cache is optional (defaults null) so existing 2-arg call sites
    /// keep compiling; warm-up status is looked up for tracked entities only.
    /// </summary>
    private static IEnumerable<object> ProjectEntries(
        IReadOnlyList<HaSensorEntry> entries, EntitiesConfig config, IEntityStatusCache? cache = null)
    {
        var trackedIds = SensorTracking.TrackedIds(config);

        // D-N: same single-pass dictionary the handler builds outside the Select.
        var configuredById = config.Entities
            .GroupBy(x => x.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        return entries.Select(e =>
        {
            var showFriendlyName = !string.IsNullOrEmpty(e.FriendlyName) &&
                !string.Equals(e.FriendlyName, e.EntityId, StringComparison.Ordinal);

            var tracked = trackedIds.Contains(e.EntityId);
            var status = tracked ? cache?.Get(e.EntityId) : null;
            configuredById.TryGetValue(e.EntityId, out var configured);

            return new
            {
                entityId = e.EntityId,
                friendlyName = showFriendlyName ? e.FriendlyName : null,
                currentValue = e.CurrentValue.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
                unitOfMeasurement = e.UnitOfMeasurement,
                isTracked = tracked,
                detectors = tracked && configured is not null
                    ? configured.Detectors.Select(d => new { name = d.Name, @params = d.Params }).ToList()
                    : null,
                calibratedExpected = status?.CalibratedExpected,
                calibratedLower = status?.CalibratedLower,
                calibratedUpper = status?.CalibratedUpper,
                medianIntervalSec = status?.MedianIntervalSec,
                warmedUp = status?.WarmedUp,
                readingCount = status?.ReadingCount,
                warmUpWindow = status?.WarmUpWindow,
            };
        });
    }

    // -----------------------------------------------------------------------
    // Tracked / untracked
    // -----------------------------------------------------------------------

    [Fact]
    public void ProjectEntries_TrackedInConfig_IsTrackedTrue()
    {
        // G-14-1 regression: registry snapshot is deliberately stale (isTracked: false) — proves
        // the projection is config-sourced, not the HA registry snapshot (fix #2).
        var registry = new FakeRegistry(MakeEntry("sensor.living_room_temp", isTracked: false));
        var config = new EntitiesConfig
        {
            Entities = [new EntityConfig { EntityId = "sensor.living_room_temp", FriendlyName = "", Detectors = [] }],
        };

        var result = ProjectEntries(registry.GetFiltered(""), config).Cast<dynamic>().ToList();

        Assert.Single(result);
        Assert.True((bool)result[0].isTracked);
        Assert.Equal("sensor.living_room_temp", (string)result[0].entityId);
    }

    [Fact]
    public void ProjectEntries_NotInConfig_IsTrackedFalse()
    {
        // Registry snapshot says tracked, but the entity is absent from the live config —
        // config is authoritative, so isTracked must be false.
        var registry = new FakeRegistry(MakeEntry("sensor.outdoor_humidity", isTracked: true));

        var result = ProjectEntries(registry.GetFiltered(""), new EntitiesConfig()).Cast<dynamic>().ToList();

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

        var result = ProjectEntries(registry.GetFiltered(""), new EntitiesConfig()).Cast<dynamic>().ToList();

        Assert.Equal("Salon temperatura", (string)result[0].friendlyName);
    }

    [Fact]
    public void ProjectEntries_FriendlyNameSameAsEntityId_IsNull()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.temp", friendlyName: "sensor.temp"));

        var result = ProjectEntries(registry.GetFiltered(""), new EntitiesConfig()).Cast<dynamic>().ToList();

        Assert.Null(result[0].friendlyName);
    }

    [Fact]
    public void ProjectEntries_NullFriendlyName_IsNull()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.outdoor_temp", friendlyName: null));

        var result = ProjectEntries(registry.GetFiltered(""), new EntitiesConfig()).Cast<dynamic>().ToList();

        Assert.Null(result[0].friendlyName);
    }

    [Fact]
    public void ProjectEntries_EmptyFriendlyName_IsNull()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.outdoor_temp", friendlyName: ""));

        var result = ProjectEntries(registry.GetFiltered(""), new EntitiesConfig()).Cast<dynamic>().ToList();

        Assert.Null(result[0].friendlyName);
    }

    // -----------------------------------------------------------------------
    // Value / unit projection
    // -----------------------------------------------------------------------

    [Fact]
    public void ProjectEntries_ValueAndUnit_AreProjectedSeparately()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.outdoor_temp", value: 18.5, unit: "°C"));

        var result = ProjectEntries(registry.GetFiltered(""), new EntitiesConfig()).Cast<dynamic>().ToList();

        Assert.Equal("18.5", (string)result[0].currentValue);
        Assert.Equal("°C", (string)result[0].unitOfMeasurement);
    }

    [Fact]
    public void ProjectEntries_NullUnit_IsProjectedAsNull()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.outdoor_temp", unit: null));

        var result = ProjectEntries(registry.GetFiltered(""), new EntitiesConfig()).Cast<dynamic>().ToList();

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

        var result = ProjectEntries(registry.GetFiltered("living"), new EntitiesConfig()).Cast<dynamic>().ToList();

        Assert.Single(result);
        Assert.Equal("sensor.living_room_temp", (string)result[0].entityId);
    }

    [Fact]
    public void GetFiltered_EmptyQuery_ReturnsAll()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.a"), MakeEntry("sensor.b"));

        var result = ProjectEntries(registry.GetFiltered(""), new EntitiesConfig()).ToList();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void GetFiltered_EmptySnapshot_ReturnsEmptyEntries()
    {
        var registry = new FakeRegistry();

        var result = ProjectEntries(registry.GetFiltered(""), new EntitiesConfig()).ToList();

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

    // -----------------------------------------------------------------------
    // Warm-up status projection (QUICK-warmup-status)
    // -----------------------------------------------------------------------

    [Fact]
    public void ProjectEntries_TrackedWithCachedWarmingStatus_ProjectsWarmUpFields()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.living_room_temp"));
        var config = new EntitiesConfig
        {
            Entities = [new EntityConfig { EntityId = "sensor.living_room_temp", FriendlyName = "", Detectors = [] }],
        };
        var cache = new EntityStatusCache();
        cache.Set(new EntityStatusEntry("sensor.living_room_temp", WarmedUp: false, ReadingCount: 100, WarmUpWindow: 250));

        var result = ProjectEntries(registry.GetFiltered(""), config, cache).Cast<dynamic>().ToList();

        Assert.False((bool)result[0].warmedUp);
        Assert.Equal(100, (int)result[0].readingCount);
        Assert.Equal(250, (int)result[0].warmUpWindow);
    }

    [Fact]
    public void ProjectEntries_TrackedWithCachedWarmedUpStatus_ProjectsWarmedUpTrue()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.living_room_temp"));
        var config = new EntitiesConfig
        {
            Entities = [new EntityConfig { EntityId = "sensor.living_room_temp", FriendlyName = "", Detectors = [] }],
        };
        var cache = new EntityStatusCache();
        cache.Set(new EntityStatusEntry("sensor.living_room_temp", WarmedUp: true, ReadingCount: 250, WarmUpWindow: 250));

        var result = ProjectEntries(registry.GetFiltered(""), config, cache).Cast<dynamic>().ToList();

        Assert.True((bool)result[0].warmedUp);
    }

    [Fact]
    public void ProjectEntries_UntrackedWithCachedEntry_WarmUpFieldsAreNull()
    {
        // Entity absent from config (untracked) — warm-up fields must be null even when
        // a cache entry exists for it (status is only surfaced for tracked entities).
        var registry = new FakeRegistry(MakeEntry("sensor.outdoor_humidity"));
        var cache = new EntityStatusCache();
        cache.Set(new EntityStatusEntry("sensor.outdoor_humidity", WarmedUp: true, ReadingCount: 250, WarmUpWindow: 250));

        var result = ProjectEntries(registry.GetFiltered(""), new EntitiesConfig(), cache).Cast<dynamic>().ToList();

        Assert.Null(result[0].warmedUp);
        Assert.Null(result[0].readingCount);
        Assert.Null(result[0].warmUpWindow);
    }

    [Fact]
    public void ProjectEntries_TrackedWithEmptyCache_WarmUpFieldsAreNull()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.living_room_temp"));
        var config = new EntitiesConfig
        {
            Entities = [new EntityConfig { EntityId = "sensor.living_room_temp", FriendlyName = "", Detectors = [] }],
        };
        var cache = new EntityStatusCache();

        var result = ProjectEntries(registry.GetFiltered(""), config, cache).Cast<dynamic>().ToList();

        Assert.Null(result[0].warmedUp);
        Assert.Null(result[0].readingCount);
        Assert.Null(result[0].warmUpWindow);
    }

    // -----------------------------------------------------------------------
    // D-N: saved detectors round-trip, and the calibrated band
    // -----------------------------------------------------------------------

    /// <summary>
    /// Without this projection the editor has nothing to hydrate from and seeds a fresh default
    /// block instead. Because save() replaces the ENTIRE entities list, the first Save from any
    /// screen -- including the pattern textareas in Settings -- would then write those defaults
    /// back over every tracked sensor. That is how a one-way migration silently reverts on the
    /// first click, so this projection is a prerequisite of the migration, not a nicety.
    /// </summary>
    [Fact]
    public void TrackedEntity_Projection_ReturnsSavedDetectorsAndParams()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.load_5m", isTracked: true));
        var tuned = new Dictionary<string, string>(DetectorDefaults.Get("rmad")!)
        {
            ["window"] = "240",
            ["high_threshold"] = "0.615",
        };
        var config = new EntitiesConfig
        {
            Entities =
            [
                new EntityConfig
                {
                    EntityId = "sensor.load_5m",
                    FriendlyName = "",
                    Detectors = [new DetectorConfig { Name = "rmad", Params = tuned }],
                }
            ],
        };

        var result = ProjectEntries(registry.GetFiltered(""), config).Cast<dynamic>().ToList();

        var detectors = result[0].detectors;
        Assert.NotNull(detectors);
        Assert.Single(detectors);
        Assert.Equal("rmad", (string)detectors[0].name);
        // The TUNED values, not the defaults -- that is the whole point.
        Assert.Equal("240", (string)detectors[0].@params["window"]);
        Assert.Equal("0.615", (string)detectors[0].@params["high_threshold"]);
    }

    [Fact]
    public void UntrackedEntity_Projection_ReturnsNullDetectors()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.load_5m", isTracked: true));

        var result = ProjectEntries(registry.GetFiltered(""), new EntitiesConfig()).Cast<dynamic>().ToList();

        Assert.Null(result[0].detectors);
    }

    /// <summary>
    /// F6-2: the same dimensionless threshold must read as a DIFFERENT band in each sensor own
    /// units, or the operator has no way to judge whether 0.5 is right there. The numbers are
    /// the worked example: median 107 W, MAD 2 W, sigma 1.4826*2 = 2.965, z = 5 gives 92..122 W.
    /// </summary>
    [Fact]
    public void CalibratedBand_IsProjectedFromStatusCache()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.zamrazarkapiwnica_power", isTracked: true));
        var config = new EntitiesConfig
        {
            Entities =
            [
                new EntityConfig
                {
                    EntityId = "sensor.zamrazarkapiwnica_power",
                    FriendlyName = "",
                    Detectors = [new DetectorConfig { Name = "rmad", Params = [] }],
                }
            ],
        };
        var cache = new EntityStatusCache();
        cache.Set(new EntityStatusEntry(
            "sensor.zamrazarkapiwnica_power", WarmedUp: true, ReadingCount: 720, WarmUpWindow: 60,
            CalibratedExpected: 107.0, CalibratedLower: 92.0, CalibratedUpper: 122.0,
            MedianIntervalSec: 384.0));

        var result = ProjectEntries(registry.GetFiltered(""), config, cache).Cast<dynamic>().ToList();

        Assert.Equal(107.0, (double)result[0].calibratedExpected);
        Assert.Equal(92.0, (double)result[0].calibratedLower);
        Assert.Equal(122.0, (double)result[0].calibratedUpper);
        Assert.Equal(384.0, (double)result[0].medianIntervalSec);
    }

    /// <summary>
    /// Before the first verdict there is no band. The projection must pass the nulls through
    /// unchanged so the UI can say "calibrating" -- a zero or an invented band would read as a
    /// measured statement about a sensor nothing has measured yet.
    /// </summary>
    [Fact]
    public void CalibratedBand_BeforeFirstVerdict_IsNull()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.load_5m", isTracked: true));
        var config = new EntitiesConfig
        {
            Entities =
            [
                new EntityConfig
                {
                    EntityId = "sensor.load_5m", FriendlyName = "",
                    Detectors = [new DetectorConfig { Name = "rmad", Params = [] }],
                }
            ],
        };

        var result = ProjectEntries(registry.GetFiltered(""), config, new EntityStatusCache())
            .Cast<dynamic>().ToList();

        Assert.Null(result[0].calibratedExpected);
        Assert.Null(result[0].calibratedLower);
        Assert.Null(result[0].calibratedUpper);
    }
}
