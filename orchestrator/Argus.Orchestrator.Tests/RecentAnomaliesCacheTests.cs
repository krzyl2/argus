using Argus.Orchestrator.Detection;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for RecentAnomaliesCache — the in-memory bounded ring buffer backing
/// GET /api/anomalies/recent (QUICK-dashboard-real-data). Fully offline, no DI required.
/// </summary>
public class RecentAnomaliesCacheTests
{
    [Fact]
    public void GetRecent_FreshCache_ReturnsEmpty()
    {
        var cache = new RecentAnomaliesCache();

        var result = cache.GetRecent();

        Assert.Empty(result);
    }

    [Fact]
    public void GetRecent_AfterRecordingThree_ReturnsNewestFirst()
    {
        var cache = new RecentAnomaliesCache();
        var first = new RecentAnomaly("sensor.a", null, 0.1, "hst", DateTimeOffset.UtcNow);
        var second = new RecentAnomaly("sensor.b", null, 0.2, "hst", DateTimeOffset.UtcNow);
        var third = new RecentAnomaly("sensor.c", null, 0.3, "hst", DateTimeOffset.UtcNow);

        cache.Record(first);
        cache.Record(second);
        cache.Record(third);
        var result = cache.GetRecent();

        Assert.Equal(3, result.Count);
        Assert.Equal("sensor.c", result[0].EntityId);
        Assert.Equal("sensor.b", result[1].EntityId);
        Assert.Equal("sensor.a", result[2].EntityId);
    }

    [Fact]
    public void Record_MoreThanCapacity_EvictsOldestAndKeepsExactlyCapacityEntries()
    {
        var cache = new RecentAnomaliesCache();

        for (var i = 0; i < 25; i++)
            cache.Record(new RecentAnomaly($"sensor.{i}", null, i, "hst", DateTimeOffset.UtcNow));

        var result = cache.GetRecent();

        Assert.Equal(20, result.Count);
        // Newest (index 24) recorded last, evicted-oldest are indices 0-4.
        Assert.Equal("sensor.24", result[0].EntityId);
        Assert.Equal("sensor.5", result[^1].EntityId);
        Assert.DoesNotContain(result, a => a.EntityId is "sensor.0" or "sensor.1" or "sensor.2" or "sensor.3" or "sensor.4");
    }

    [Fact]
    public void GetRecent_ReturnsSnapshot_NotMutatedByLaterRecord()
    {
        var cache = new RecentAnomaliesCache();
        cache.Record(new RecentAnomaly("sensor.a", null, 0.1, "hst", DateTimeOffset.UtcNow));

        var snapshot = cache.GetRecent();
        cache.Record(new RecentAnomaly("sensor.b", null, 0.2, "hst", DateTimeOffset.UtcNow));

        Assert.Single(snapshot);
        Assert.Equal("sensor.a", snapshot[0].EntityId);
    }
}
