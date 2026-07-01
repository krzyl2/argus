using Argus.Orchestrator.Ha;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Unit tests for HaSensorRegistry (thread-safe volatile snapshot, numeric filter, tracked flag).
/// Fully offline — no live HA connection required.
/// </summary>
public class HaSensorRegistryTests
{
    private static readonly HashSet<string> TrackedEntities = new(StringComparer.OrdinalIgnoreCase)
    {
        "sensor.outdoor_temp",
        "sensor.indoor_humidity",
    };

    private static HaStateDto MakeDto(
        string entityId, string? state,
        string? unit = null, string? friendlyName = null)
        => new(entityId, state, DateTime.UtcNow, unit, friendlyName);

    // -----------------------------------------------------------------------
    // UpdateSnapshot: numeric filter
    // -----------------------------------------------------------------------

    [Fact]
    public void UpdateSnapshot_NumericState_IsIncluded()
    {
        var registry = new HaSensorRegistry();

        registry.UpdateSnapshot(
            new[] { MakeDto("sensor.outdoor_temp", "21.5", "°C", "Outdoor Temp") },
            TrackedEntities);

        var all = registry.GetAll();
        Assert.Single(all);
        Assert.Equal("sensor.outdoor_temp", all[0].EntityId);
        Assert.Equal(21.5, all[0].CurrentValue, precision: 5);
        Assert.Equal("°C", all[0].UnitOfMeasurement);
        Assert.Equal("Outdoor Temp", all[0].FriendlyName);
    }

    [Fact]
    public void UpdateSnapshot_NonNumericStates_AreExcluded()
    {
        var registry = new HaSensorRegistry();

        registry.UpdateSnapshot(new[]
        {
            MakeDto("sensor.door", "on"),
            MakeDto("sensor.broken", "unavailable"),
            MakeDto("sensor.unknown_state", "unknown"),
            MakeDto("sensor.null_state", null),
        }, TrackedEntities);

        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public void UpdateSnapshot_MixedInput_ReturnsOnlyNumeric()
    {
        var registry = new HaSensorRegistry();

        registry.UpdateSnapshot(new[]
        {
            MakeDto("sensor.outdoor_temp", "21.5", "°C", "Outdoor Temp"),
            MakeDto("sensor.door", "on"),
            MakeDto("sensor.indoor_humidity", "55.0", "%", "Indoor Humidity"),
            MakeDto("sensor.motion", "unavailable"),
        }, TrackedEntities);

        var all = registry.GetAll();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void UpdateSnapshot_NegativeNumericValue_IsIncluded()
    {
        var registry = new HaSensorRegistry();

        registry.UpdateSnapshot(
            new[] { MakeDto("sensor.outdoor_temp_winter", "-15.3", "°C") },
            new HashSet<string>());

        var all = registry.GetAll();
        Assert.Single(all);
        Assert.Equal(-15.3, all[0].CurrentValue, precision: 5);
    }

    // -----------------------------------------------------------------------
    // GetFiltered: search
    // -----------------------------------------------------------------------

    [Fact]
    public void GetFiltered_EmptyQuery_ReturnsFullSnapshot()
    {
        var registry = new HaSensorRegistry();

        registry.UpdateSnapshot(new[]
        {
            MakeDto("sensor.outdoor_temp", "21.5"),
            MakeDto("sensor.indoor_humidity", "55.0"),
        }, TrackedEntities);

        var result = registry.GetFiltered("");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void GetFiltered_MatchingQuery_ReturnsFilteredSubset()
    {
        var registry = new HaSensorRegistry();

        registry.UpdateSnapshot(new[]
        {
            MakeDto("sensor.outdoor_temp", "21.5"),
            MakeDto("sensor.indoor_humidity", "55.0"),
            MakeDto("sensor.outdoor_pressure", "1013.0"),
        }, TrackedEntities);

        var result = registry.GetFiltered("outdoor");
        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Contains("outdoor", e.EntityId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetFiltered_CaseInsensitive_Matches()
    {
        var registry = new HaSensorRegistry();

        registry.UpdateSnapshot(
            new[] { MakeDto("sensor.TEMP_outdoor", "19.0") },
            new HashSet<string>());

        var result = registry.GetFiltered("temp");
        Assert.Single(result);
    }

    [Fact]
    public void GetFiltered_NoMatch_ReturnsEmpty()
    {
        var registry = new HaSensorRegistry();

        registry.UpdateSnapshot(
            new[] { MakeDto("sensor.outdoor_temp", "21.5") },
            new HashSet<string>());

        var result = registry.GetFiltered("zzz_no_match");
        Assert.Empty(result);
    }

    // -----------------------------------------------------------------------
    // IsTracked
    // -----------------------------------------------------------------------

    [Fact]
    public void UpdateSnapshot_TrackedEntity_HasIsTrackedTrue()
    {
        var registry = new HaSensorRegistry();

        registry.UpdateSnapshot(
            new[] { MakeDto("sensor.outdoor_temp", "21.5") },
            TrackedEntities);

        var all = registry.GetAll();
        Assert.True(all[0].IsTracked);
    }

    [Fact]
    public void UpdateSnapshot_UntrackedEntity_HasIsTrackedFalse()
    {
        var registry = new HaSensorRegistry();

        registry.UpdateSnapshot(
            new[] { MakeDto("sensor.wind_speed", "12.3") },
            TrackedEntities);

        var all = registry.GetAll();
        Assert.False(all[0].IsTracked);
    }

    // -----------------------------------------------------------------------
    // Ordering
    // -----------------------------------------------------------------------

    [Fact]
    public void UpdateSnapshot_EntriesOrderedByEntityIdOrdinalIgnoreCase()
    {
        var registry = new HaSensorRegistry();

        registry.UpdateSnapshot(new[]
        {
            MakeDto("sensor.zzz_last", "1.0"),
            MakeDto("sensor.aaa_first", "2.0"),
            MakeDto("sensor.mmm_middle", "3.0"),
        }, new HashSet<string>());

        var ids = registry.GetAll().Select(e => e.EntityId).ToList();
        Assert.Equal(new[] { "sensor.aaa_first", "sensor.mmm_middle", "sensor.zzz_last" }, ids);
    }

    // -----------------------------------------------------------------------
    // Thread safety
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentUpdateAndGetAll_DoesNotThrow()
    {
        var registry = new HaSensorRegistry();
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var states = Enumerable.Range(1, 50)
            .Select(i => MakeDto($"sensor.entity_{i}", $"{i}.0"))
            .ToList();

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var writerTask = Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                try { registry.UpdateSnapshot(states, tracked); }
                catch (Exception ex) { exceptions.Add(ex); }
            }
        });

        var readerTask = Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                try { _ = registry.GetAll(); }
                catch (Exception ex) { exceptions.Add(ex); }
            }
        });

        await Task.WhenAll(writerTask, readerTask);
        Assert.Empty(exceptions);
    }
}
