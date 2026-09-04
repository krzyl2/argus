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

    [Fact]
    public void GetFiltered_MatchesFriendlyNameWhenEntityIdDoesNotMatch_SRCH01()
    {
        var registry = new HaSensorRegistry();

        registry.UpdateSnapshot(new[]
        {
            MakeDto("sensor.abc123", "21.5", friendlyName: "Living Room Temp"),
            MakeDto("sensor.def456", "55.0", friendlyName: "Bedroom Humidity"),
        }, new HashSet<string>());

        var result = registry.GetFiltered("Living Room");
        Assert.Single(result);
        Assert.Equal("sensor.abc123", result[0].EntityId);
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
    // Domain / AreaName enrichment (SRCH-02/03)
    // -----------------------------------------------------------------------

    [Fact]
    public void UpdateSnapshot_Domain_IsSubstringBeforeFirstDot()
    {
        var registry = new HaSensorRegistry();

        registry.UpdateSnapshot(
            new[] { MakeDto("sensor.outdoor_temp", "21.5") },
            TrackedEntities);

        Assert.Equal("sensor", registry.GetAll()[0].Domain);
    }

    [Fact]
    public void UpdateSnapshot_NoAreaMapProvided_AreaNameIsNull()
    {
        var registry = new HaSensorRegistry();

        registry.UpdateSnapshot(
            new[] { MakeDto("sensor.outdoor_temp", "21.5") },
            TrackedEntities);

        Assert.Null(registry.GetAll()[0].AreaName);
    }

    [Fact]
    public void UpdateSnapshot_EntityInAreaMap_AreaNameIsResolved()
    {
        var registry = new HaSensorRegistry();
        var areas = new Dictionary<string, string?> { ["sensor.outdoor_temp"] = "Garden" };

        registry.UpdateSnapshot(
            new[] { MakeDto("sensor.outdoor_temp", "21.5") },
            TrackedEntities,
            areas);

        Assert.Equal("Garden", registry.GetAll()[0].AreaName);
    }

    [Fact]
    public void UpdateSnapshot_EntityNotInAreaMap_AreaNameIsNull()
    {
        var registry = new HaSensorRegistry();
        var areas = new Dictionary<string, string?> { ["sensor.other"] = "Kitchen" };

        registry.UpdateSnapshot(
            new[] { MakeDto("sensor.outdoor_temp", "21.5") },
            TrackedEntities,
            areas);

        Assert.Null(registry.GetAll()[0].AreaName);
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
    // -----------------------------------------------------------------------
    // WS4/F10: the registry is no longer connect-only
    // -----------------------------------------------------------------------

    [Fact]
    public void Upsert_NonNumericState_DoesNotRemoveExistingEntry()
    {
        // WHY: an entity that blinks `unavailable`/`unknown` for one event (integration reload,
        // battery sensor waking up) must not vanish from the picker. Boot-time `unknown` being a
        // PERMANENT loss of the entity is hypothesis H1 for F10 — 403 entities in HA, 157 here.
        var registry = new HaSensorRegistry();
        registry.UpdateSnapshot(new[] { MakeDto("sensor.outdoor_temp", "21.5", "°C", "Outdoor") }, TrackedEntities);

        Assert.False(registry.Upsert(MakeDto("sensor.outdoor_temp", "unavailable"), isTracked: true));

        var all = registry.GetAll();
        Assert.Single(all);
        Assert.Equal(21.5, all[0].CurrentValue, precision: 5);
    }

    [Fact]
    public void Upsert_NewNumericEntity_AppearsInGetAllWithoutReconnect()
    {
        // WHY: this is the core of the F10 fix. state_changed is subscribed GLOBALLY, so an entity
        // absent from the connect-time get_states snapshot becomes selectable the moment it reports
        // a value — no second WebSocket (ADR-4) and no waiting for a reconnect.
        var registry = new HaSensorRegistry();
        registry.UpdateSnapshot(new[] { MakeDto("sensor.outdoor_temp", "21.5") }, TrackedEntities);

        Assert.True(registry.Upsert(MakeDto("sensor.expminimp", "412.0", "W", "Eksport min/imp"), isTracked: false));

        var added = Assert.Single(registry.GetAll(), e => e.EntityId == "sensor.expminimp");
        Assert.Equal(412.0, added.CurrentValue, precision: 5);
        Assert.Equal("sensor", added.Domain);
        Assert.False(added.IsTracked);
        Assert.True(added.KnownToHa);
        // Second event for a known entity is an update, not a discovery.
        Assert.False(registry.Upsert(MakeDto("sensor.expminimp", "413.0"), isTracked: false));
    }

    [Fact]
    public void UpdateSnapshot_MergesInsteadOfDropping_MarksStaleSince()
    {
        // WHY: a reconnect that catches HA mid-reload returns a SHORTER get_states. Replacing the
        // snapshot wholesale would empty the picker of everything HA happened not to list, and the
        // operator would read that as "Argus lost my sensors". Keep the row, mark it stale.
        var registry = new HaSensorRegistry();
        registry.UpdateSnapshot(new[]
        {
            MakeDto("sensor.outdoor_temp", "21.5"),
            MakeDto("sensor.indoor_humidity", "48.0"),
        }, TrackedEntities);

        registry.UpdateSnapshot(new[] { MakeDto("sensor.outdoor_temp", "22.0") }, TrackedEntities);

        var all = registry.GetAll();
        Assert.Equal(2, all.Count);

        var fresh = all.Single(e => e.EntityId == "sensor.outdoor_temp");
        Assert.Null(fresh.StaleSince);
        Assert.Equal(22.0, fresh.CurrentValue, precision: 5);

        var stale = all.Single(e => e.EntityId == "sensor.indoor_humidity");
        Assert.NotNull(stale.StaleSince);
        Assert.Equal(48.0, stale.CurrentValue, precision: 5);

        // StaleSince records the FIRST pass it went missing — later passes must not reset the clock,
        // otherwise "gone since" is unusable for deciding an entity really is gone.
        var firstStamp = stale.StaleSince;
        registry.UpdateSnapshot(new[] { MakeDto("sensor.outdoor_temp", "22.5") }, TrackedEntities);
        Assert.Equal(firstStamp, registry.GetAll().Single(e => e.EntityId == "sensor.indoor_humidity").StaleSince);

        // Reappearing clears the stamp — a stale row is a suspicion, not a verdict.
        registry.UpdateSnapshot(new[]
        {
            MakeDto("sensor.outdoor_temp", "22.5"),
            MakeDto("sensor.indoor_humidity", "50.0"),
        }, TrackedEntities);
        Assert.Null(registry.GetAll().Single(e => e.EntityId == "sensor.indoor_humidity").StaleSince);
    }

    [Fact]
    public void Upsert_NumberAndTextDomains_AreAccepted()
    {
        // WHY: F10's missing entities are not all `sensor.*` — the live instance is short 32
        // `number.*` and 6 `text.*`. The admission rule is double.TryParse and NOTHING else;
        // a domain allowlist would silently re-create the gap this workstream exists to close.
        var registry = new HaSensorRegistry();

        Assert.True(registry.Upsert(MakeDto("number.salon_termostat_algorithm_scale_factor", "5"), isTracked: false));
        Assert.True(registry.Upsert(MakeDto("text.kuchnia_notatka", "-12.5"), isTracked: false));
        Assert.False(registry.Upsert(MakeDto("text.kuchnia_opis", "ciepło"), isTracked: false));

        var ids = registry.GetAll().Select(e => e.EntityId).ToList();
        Assert.Contains("number.salon_termostat_algorithm_scale_factor", ids);
        Assert.Contains("text.kuchnia_notatka", ids);
        Assert.DoesNotContain("text.kuchnia_opis", ids);
        Assert.Equal("number", registry.GetAll().Single(e => e.EntityId.StartsWith("number.")).Domain);
    }

    [Fact]
    public void Upsert_KeepsAreaNameFromSnapshot()
    {
        // WHY: area names only arrive with the per-connect entity/area registry fetch. If an
        // ordinary state_changed wiped them, the area-grouped picker would degrade to "Ungrouped"
        // within seconds of connecting.
        var registry = new HaSensorRegistry();
        registry.UpdateSnapshot(
            new[] { MakeDto("sensor.salon_temp", "21.5") },
            TrackedEntities,
            new Dictionary<string, string?> { ["sensor.salon_temp"] = "Salon" });

        registry.Upsert(MakeDto("sensor.salon_temp", "21.9"), isTracked: false);

        Assert.Equal("Salon", registry.GetAll().Single().AreaName);
    }

    // -----------------------------------------------------------------------
    // Hot path: what Upsert is allowed to cost
    // -----------------------------------------------------------------------

    /// <summary>
    /// WHY: Upsert runs on the HA WebSocket receive loop — the same loop that has to get a
    /// reading into the scoring pipeline within the 2 s budget — and it fires on every numeric
    /// state_changed, tens per second on a real installation with a few hundred entities. The
    /// sorted projection it feeds is read only by GET /api/sensors, at human speed. So the rule
    /// is: a write may pay for recording the new value, and nothing that scales with the whole
    /// registry beyond that; the ordering is the reader's bill.
    ///
    /// The budget is calibrated inside the test against the unavoidable cost — copying the index
    /// — so it measures the RATIO, not a machine-specific byte count. Sorting on write pushed
    /// that ratio to roughly 2.4x (a buffer, a key array, an index map and the result list, all
    /// N-sized, on top of the copy); paying it on read leaves it at about 1x.
    /// </summary>
    /// <summary>
    /// WHY: moving the sort off the write loop only pays off if the READ side does not pay it
    /// over and over. GetAll() is not a one-per-request call — POST /api/sensors/save resolves
    /// the tracked set from it and then builds the friendly-name snapshot from it, and
    /// GroupInputValidator reads it per validation — so "each read re-sorts" would trade a cost
    /// on the HA receive loop for an unbounded one on the HTTP path.
    ///
    /// The rule: the sorted projection is built once per STATE VERSION, not once per read. A
    /// reader that changes nothing must be able to read again for free.
    /// </summary>
    [Fact]
    public void GetAll_OnAnUnchangedRegistry_DoesNotSortAgain()
    {
        const int entityCount = 4000;
        const int iterations = 20;

        var registry = new HaSensorRegistry();
        registry.UpdateSnapshot(
            Enumerable.Range(0, entityCount)
                .Select(i => MakeDto($"sensor.s{i:D5}", "1.0"))
                .ToList(),
            TrackedEntities);

        static long Measure(int times, Action body)
        {
            body();                                   // JIT + first-call allocations
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < times; i++)
                body();
            return (GC.GetAllocatedBytesForCurrentThread() - before) / times;
        }

        var hit = MakeDto("sensor.s02000", "2.0");

        // A NEW state version: whatever a first read of one costs, including the sort.
        var freshVersionCost = Measure(iterations, () =>
        {
            registry.Upsert(hit, isTracked: false);
            GC.KeepAlive(registry.GetAll());
        });

        // The same version, read again and again — the case an HTTP handler actually makes.
        var repeatReadCost = Measure(iterations, () => GC.KeepAlive(registry.GetAll()));

        Assert.True(repeatReadCost < freshVersionCost / 100,
            $"Re-reading an unchanged registry allocated {repeatReadCost} B against "
            + $"{freshVersionCost} B for a first read of a new version — the projection is being "
            + "rebuilt per read instead of per state version.");

        // Free, but not stale: the projection still has to be the current one.
        Assert.Equal(entityCount, registry.GetAll().Count);
    }

    [Fact]
    public void Upsert_DoesNotPayForOrderingTheWholeRegistry()
    {
        const int entityCount = 4000;
        const int iterations = 20;

        var registry = new HaSensorRegistry();
        registry.UpdateSnapshot(
            Enumerable.Range(0, entityCount)
                .Select(i => MakeDto($"sensor.s{i:D5}", "1.0"))
                .ToList(),
            TrackedEntities);

        // Force the projection once, so the measurement below is not paying for a cold cache.
        Assert.Equal(entityCount, registry.GetAll().Count);

        var index = registry.GetAll().ToDictionary(e => e.EntityId, StringComparer.OrdinalIgnoreCase);
        var hit = MakeDto("sensor.s02000", "2.0");

        static long Measure(int times, Action body)
        {
            body();                                   // JIT + first-call allocations
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < times; i++)
                body();
            return (GC.GetAllocatedBytesForCurrentThread() - before) / times;
        }

        var indexCopyCost = Measure(iterations, () =>
            GC.KeepAlive(new Dictionary<string, HaSensorEntry>(index, StringComparer.OrdinalIgnoreCase)));
        var upsertCost = Measure(iterations, () => registry.Upsert(hit, isTracked: false));

        Assert.True(upsertCost < indexCopyCost * 3 / 2,
            $"Upsert allocated {upsertCost} B against an index-copy floor of {indexCopyCost} B "
            + $"({upsertCost / (double)indexCopyCost:F2}x) — a write is doing work proportional to "
            + "the whole registry on the HA receive loop.");

        // The lazy projection must still be a correct one: a NEW entity has to appear, in order,
        // on the very next read.
        registry.Upsert(MakeDto("sensor.s00000_aaa", "3.0"), isTracked: false);
        var all = registry.GetAll();
        Assert.Equal(entityCount + 1, all.Count);
        Assert.Equal(
            all.Select(e => e.EntityId).OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
            all.Select(e => e.EntityId));
        Assert.Contains(all, e => e.EntityId == "sensor.s00000_aaa");
    }
}
