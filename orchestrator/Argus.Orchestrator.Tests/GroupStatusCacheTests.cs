using Argus.Orchestrator.Batch;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for GroupStatusCache — the in-memory last-verdict cache backing
/// GET /api/groups/{id}/status (GRP-09). Fully offline, no DI required.
/// </summary>
public class GroupStatusCacheTests
{
    [Fact]
    public void Get_UnknownGroupId_ReturnsNull()
    {
        var cache = new GroupStatusCache();

        var result = cache.Get("group.unknown");

        Assert.Null(result);
    }

    [Fact]
    public void Get_AfterSet_ReturnsSameEntry()
    {
        var cache = new GroupStatusCache();
        var entry = new GroupStatusEntry(
            GroupId: "group.living_room",
            Score: 0.83,
            IsAnomaly: true,
            Detector: "ecod",
            ScoredAtUtc: DateTimeOffset.UtcNow,
            Contributions:
            [
                new FeatureContributionDto("sensor.humidity", 0.6),
                new FeatureContributionDto("sensor.temp", 0.4),
            ]);

        cache.Set(entry);
        var result = cache.Get("group.living_room");

        Assert.NotNull(result);
        Assert.Same(entry, result);
    }

    [Fact]
    public void Get_IsCaseInsensitiveOnGroupId()
    {
        var cache = new GroupStatusCache();
        var entry = new GroupStatusEntry(
            "group.Living_Room", 0.1, false, "pca", DateTimeOffset.UtcNow, []);

        cache.Set(entry);
        var result = cache.Get("group.living_room");

        Assert.NotNull(result);
    }

    [Fact]
    public void Set_TwiceForSameGroupId_ReplacesPreviousEntry()
    {
        var cache = new GroupStatusCache();
        var first = new GroupStatusEntry("group.a", 0.1, false, "pca", DateTimeOffset.UtcNow, []);
        var second = new GroupStatusEntry("group.a", 0.9, true, "pca", DateTimeOffset.UtcNow, []);

        cache.Set(first);
        cache.Set(second);
        var result = cache.Get("group.a");

        Assert.Same(second, result);
    }

    [Fact]
    public void GroupStatusEntry_EmptyContributions_NeverNull()
    {
        // pca/iforest never produce attribution — the cache must store an empty list, not null.
        var entry = new GroupStatusEntry("group.a", 0.1, false, "pca", DateTimeOffset.UtcNow, []);

        Assert.NotNull(entry.Contributions);
        Assert.Empty(entry.Contributions);
    }
}
