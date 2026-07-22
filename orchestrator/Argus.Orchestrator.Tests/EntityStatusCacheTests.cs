using Argus.Orchestrator.Detection;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for EntityStatusCache — the in-memory last-warm-up-status cache backing
/// GET /api/sensors's per-entity warm-up projection (QUICK-warmup-status).
/// Mirrors GroupStatusCacheTests. Fully offline, no DI required.
/// </summary>
public class EntityStatusCacheTests
{
    [Fact]
    public void Get_UnknownEntityId_ReturnsNull()
    {
        var cache = new EntityStatusCache();

        var result = cache.Get("sensor.unknown");

        Assert.Null(result);
    }

    [Fact]
    public void Get_AfterSet_RoundTripsAllFields()
    {
        var cache = new EntityStatusCache();
        var entry = new EntityStatusEntry("sensor.living_room_temp", WarmedUp: false, ReadingCount: 100, WarmUpWindow: 250);

        cache.Set(entry);
        var result = cache.Get("sensor.living_room_temp");

        Assert.NotNull(result);
        Assert.Equal("sensor.living_room_temp", result!.EntityId);
        Assert.False(result.WarmedUp);
        Assert.Equal(100, result.ReadingCount);
        Assert.Equal(250, result.WarmUpWindow);
    }

    [Fact]
    public void Set_TwiceForSameEntityId_ReplacesPreviousEntry()
    {
        var cache = new EntityStatusCache();
        var first = new EntityStatusEntry("sensor.a", false, 10, 250);
        var second = new EntityStatusEntry("sensor.a", true, 250, 250);

        cache.Set(first);
        cache.Set(second);
        var result = cache.Get("sensor.a");

        Assert.Same(second, result);
    }

    [Fact]
    public void Get_IsCaseInsensitiveOnEntityId()
    {
        var cache = new EntityStatusCache();
        var entry = new EntityStatusEntry("sensor.X", true, 250, 250);

        cache.Set(entry);
        var result = cache.Get("SENSOR.X");

        Assert.NotNull(result);
        Assert.Same(entry, result);
    }
}
