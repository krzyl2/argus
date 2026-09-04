using Argus.Orchestrator.Batch;
using Argus.Orchestrator.Config;
using InfluxDB.Client.Core.Flux.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for GroupInfluxReader: config guards, Flux injection rejection, pivot-null-cell
/// exclusion semantics, and per-member freshness parsing. Uses hand-written fakes — no
/// live InfluxDB required (GRP-02).
/// </summary>
public class GroupInfluxReaderTests
{
    // ─── Fakes ───────────────────────────────────────────────────────────────

    /// <summary>Always returns empty table list — simulates InfluxDB with no data.</summary>
    private sealed class EmptyQueryApi : IInfluxQueryApi
    {
        public Task<List<FluxTable>> QueryAsync(string flux, string? org, CancellationToken ct)
            => Task.FromResult(new List<FluxTable>());
    }

    /// <summary>Throws on any call — should never be reached when config is invalid.</summary>
    private sealed class ThrowingQueryApi : IInfluxQueryApi
    {
        public Task<List<FluxTable>> QueryAsync(string flux, string? org, CancellationToken ct)
            => throw new InvalidOperationException("QueryApi should not be called when config is invalid");
    }

    /// <summary>
    /// Returns a distinct fixed table list per call, in order: first call gets the matrix
    /// fixture, second call gets the freshness fixture (GroupInfluxReader issues the matrix
    /// query first, then the freshness query).
    /// </summary>
    private sealed class SequencedQueryApi : IInfluxQueryApi
    {
        private readonly Queue<List<FluxTable>> _responses;

        public SequencedQueryApi(params List<FluxTable>[] responses)
            => _responses = new Queue<List<FluxTable>>(responses);

        public List<string> FluxQueries { get; } = new();

        public Task<List<FluxTable>> QueryAsync(string flux, string? org, CancellationToken ct)
        {
            FluxQueries.Add(flux);
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new List<FluxTable>());
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static ConnectionSettings ValidSettings() => new()
    {
        InfluxUrl = "http://localhost:8086",
        InfluxToken = "test-token",
        InfluxOrg = "test-org",
        InfluxBucket = "test-bucket",
        InfluxMeasurement = "homeassistant",
        InfluxValueField = "value",
    };

    private static ConnectionSettings NullUrlSettings() => new()
    {
        InfluxUrl = null,
        InfluxBucket = "test-bucket",
    };

    private static ConnectionSettings NullBucketSettings() => new()
    {
        InfluxUrl = "http://localhost:8086",
        InfluxBucket = null,
    };

    private static FluxRecord MakeRecord(int table, Instant time, Dictionary<string, object?> values)
    {
        var record = new FluxRecord(table);
        record.Values["_time"] = time;
        foreach (var (key, value) in values)
            record.Values[key] = value!;
        return record;
    }

    private static FluxTable MakeTable(params FluxRecord[] records)
    {
        var table = new FluxTable();
        table.Records.AddRange(records);
        return table;
    }

    // ─── Guard tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryGroupAsync_NullInfluxUrl_ReturnsEmptyWithoutCallingApi()
    {
        var reader = new GroupInfluxReader(new ThrowingQueryApi(), NullUrlSettings(),
            NullLogger<GroupInfluxReader>.Instance);

        var result = await reader.QueryGroupAsync(
            new[] { "sensor.a", "sensor.b" }, "5m", "mean", TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Empty(result.Rows);
        Assert.Empty(result.LastSeenUtc);
    }

    [Fact]
    public async Task QueryGroupAsync_NullInfluxBucket_ReturnsEmptyWithoutCallingApi()
    {
        var reader = new GroupInfluxReader(new ThrowingQueryApi(), NullBucketSettings(),
            NullLogger<GroupInfluxReader>.Instance);

        var result = await reader.QueryGroupAsync(
            new[] { "sensor.a", "sensor.b" }, "5m", "mean", TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Empty(result.Rows);
        Assert.Empty(result.LastSeenUtc);
    }

    [Fact]
    public async Task QueryGroupAsync_InfluxReturnsNoRecords_ReturnsEmpty()
    {
        var reader = new GroupInfluxReader(new EmptyQueryApi(), ValidSettings(),
            NullLogger<GroupInfluxReader>.Instance);

        var result = await reader.QueryGroupAsync(
            new[] { "sensor.a", "sensor.b" }, "5m", "mean", TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Empty(result.Rows);
        Assert.Empty(result.LastSeenUtc);
    }

    // ─── Injection guard tests ───────────────────────────────────────────────

    [Fact]
    public async Task QueryGroupAsync_UnsafeMemberIdWithQuote_ThrowsArgumentException()
    {
        var reader = new GroupInfluxReader(new ThrowingQueryApi(), ValidSettings(),
            NullLogger<GroupInfluxReader>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => reader.QueryGroupAsync(
            new[] { "sensor.a", "sensor.\"evil" }, "5m", "mean", TimeSpan.FromMinutes(30), CancellationToken.None));
    }

    [Fact]
    public async Task QueryGroupAsync_UnsafeMemberIdWithBackslash_ThrowsArgumentException()
    {
        var reader = new GroupInfluxReader(new ThrowingQueryApi(), ValidSettings(),
            NullLogger<GroupInfluxReader>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => reader.QueryGroupAsync(
            new[] { "sensor.a\\evil" }, "5m", "mean", TimeSpan.FromMinutes(30), CancellationToken.None));
    }

    // CR-02: newline/carriage-return in an interpolated value must be rejected — the prior
    // regex ([^"\\]+) admitted \n/\r, allowing a member id or config field to inject an
    // additional Flux pipeline line across the query's line breaks.
    [Fact]
    public async Task QueryGroupAsync_UnsafeMemberIdWithNewline_ThrowsArgumentException()
    {
        var reader = new GroupInfluxReader(new ThrowingQueryApi(), ValidSettings(),
            NullLogger<GroupInfluxReader>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => reader.QueryGroupAsync(
            new[] { "sensor.a\n|> filter(fn: (r) => true)" }, "5m", "mean", TimeSpan.FromMinutes(30), CancellationToken.None));
    }

    [Fact]
    public async Task QueryGroupAsync_UnsafeMemberIdWithCarriageReturn_ThrowsArgumentException()
    {
        var reader = new GroupInfluxReader(new ThrowingQueryApi(), ValidSettings(),
            NullLogger<GroupInfluxReader>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => reader.QueryGroupAsync(
            new[] { "sensor.a\revil" }, "5m", "mean", TimeSpan.FromMinutes(30), CancellationToken.None));
    }

    // WR-04: every/aggFn now go through the same guard as members/bucket/measurement/field.
    [Fact]
    public async Task QueryGroupAsync_UnsafeEveryWithNewline_ThrowsArgumentException()
    {
        var reader = new GroupInfluxReader(new ThrowingQueryApi(), ValidSettings(),
            NullLogger<GroupInfluxReader>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => reader.QueryGroupAsync(
            new[] { "sensor.a" }, "5m\n|> filter(fn: (r) => true)", "mean", TimeSpan.FromMinutes(30), CancellationToken.None));
    }

    [Fact]
    public async Task QueryGroupAsync_UnsafeAggFnWithQuote_ThrowsArgumentException()
    {
        var reader = new GroupInfluxReader(new ThrowingQueryApi(), ValidSettings(),
            NullLogger<GroupInfluxReader>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => reader.QueryGroupAsync(
            new[] { "sensor.a" }, "5m", "mean\"evil", TimeSpan.FromMinutes(30), CancellationToken.None));
    }

    // ─── HA writer-shape tests (entity_id tag + optional measurement) ────────

    // BUG A: HA's influxdb: integration tags each point with the OBJECT ID
    // (domain=sensor, entity_id=salon_temperature), never the full entity id. Filtering on
    // "sensor.salon_temperature" matched zero series, so every group cycle returned no rows
    // and every group sat on a permanent "Oczekuje". This pins the tag shape, not just the
    // query text: if the filter ever carries a domain prefix again, live scoring is dead.
    [Fact]
    public async Task QueryGroupAsync_FiltersOnObjectIdNotFullEntityId()
    {
        var api = new SequencedQueryApi();
        var reader = new GroupInfluxReader(api, ValidSettings(), NullLogger<GroupInfluxReader>.Instance);

        await reader.QueryGroupAsync(
            new[] { "sensor.salon_temperature", "sensor.kuchnia_temperature" },
            "5m", "mean", TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Equal(2, api.FluxQueries.Count); // matrix + freshness
        foreach (var flux in api.FluxQueries)
        {
            Assert.Contains("\"salon_temperature\"", flux);
            Assert.Contains("\"kuchnia_temperature\"", flux);
            Assert.DoesNotContain("sensor.salon_temperature", flux);
            Assert.DoesNotContain("sensor.kuchnia_temperature", flux);
        }
    }

    // BUG A, other direction: callers speak full entity ids, so the returned matrix and
    // freshness dictionaries must still be keyed by them even though Influx answered in
    // object ids. Without the reverse mapping the group would score under keys no caller
    // recognises, which reads as "no data" one layer up.
    [Fact]
    public async Task QueryGroupAsync_ResultsAreKeyedByFullEntityId()
    {
        var t = Instant.FromUtc(2026, 9, 4, 10, 0, 0);

        var matrixTable = MakeTable(
            MakeRecord(0, t, new Dictionary<string, object?> { ["salon_temperature"] = 23.2 }));
        var freshnessTable = MakeTable(
            MakeRecord(0, t, new Dictionary<string, object?> { ["entity_id"] = "salon_temperature" }));

        var api = new SequencedQueryApi(
            new List<FluxTable> { matrixTable }, new List<FluxTable> { freshnessTable });
        var reader = new GroupInfluxReader(api, ValidSettings(), NullLogger<GroupInfluxReader>.Instance);

        var result = await reader.QueryGroupAsync(
            new[] { "sensor.salon_temperature" }, "5m", "mean", TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Equal(23.2, result.Rows[0].MemberValues["sensor.salon_temperature"]);
        Assert.Equal(t.ToDateTimeUtc(), result.LastSeenUtc["sensor.salon_temperature"]);
    }

    // BUG B: HA names the measurement after the entity's unit, so a mixed-unit group
    // (temperature + humidity) loses whole columns under a single _measurement equality —
    // and for joint mode a dropped column skips the group forever. Empty measurement must
    // therefore emit NO _measurement clause at all, not an equality against "".
    [Fact]
    public async Task QueryGroupAsync_EmptyMeasurement_OmitsMeasurementFilter()
    {
        var settings = ValidSettings();
        settings.InfluxMeasurement = "";

        var api = new SequencedQueryApi();
        var reader = new GroupInfluxReader(api, settings, NullLogger<GroupInfluxReader>.Instance);

        await reader.QueryGroupAsync(
            new[] { "sensor.a" }, "5m", "mean", TimeSpan.FromMinutes(30), CancellationToken.None);

        foreach (var flux in api.FluxQueries)
        {
            Assert.DoesNotContain("_measurement", flux);
            Assert.Contains("contains(value: r[\"entity_id\"]", flux);
        }
    }

    // The filter stays available for override_measurement setups — dropping it entirely
    // would break the deployments the old default was written for.
    [Fact]
    public async Task QueryGroupAsync_ConfiguredMeasurement_KeepsMeasurementFilter()
    {
        var settings = ValidSettings();
        settings.InfluxMeasurement = "homeassistant";

        var api = new SequencedQueryApi();
        var reader = new GroupInfluxReader(api, settings, NullLogger<GroupInfluxReader>.Instance);

        await reader.QueryGroupAsync(
            new[] { "sensor.a" }, "5m", "mean", TimeSpan.FromMinutes(30), CancellationToken.None);

        foreach (var flux in api.FluxQueries)
            Assert.Contains("r[\"_measurement\"] == \"homeassistant\"", flux);
    }

    // Two members that collapse onto one object id are genuinely indistinguishable in
    // InfluxDB, so one member would silently receive the other's readings. Fail loud
    // (skip the cycle) rather than score on data that belongs to a different sensor.
    [Fact]
    public async Task QueryGroupAsync_MembersSharingObjectId_SkipsCycleWithoutQuerying()
    {
        var reader = new GroupInfluxReader(new ThrowingQueryApi(), ValidSettings(),
            NullLogger<GroupInfluxReader>.Instance);

        var result = await reader.QueryGroupAsync(
            new[] { "sensor.x", "binary_sensor.x" }, "5m", "mean", TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Empty(result.Rows);
        Assert.Empty(result.LastSeenUtc);
    }

    // ─── Pivot null-cell exclusion tests ─────────────────────────────────────

    [Fact]
    public async Task QueryGroupAsync_PivotRowMissingMemberColumn_MemberValueIsNull()
    {
        var t1 = Instant.FromUtc(2026, 7, 2, 10, 0, 0);
        var t2 = Instant.FromUtc(2026, 7, 2, 10, 5, 0);

        // Row 1: both members present. Row 2: sensor.b column absent (genuine gap, no fill()).
        var matrixTable = MakeTable(
            MakeRecord(0, t1, new Dictionary<string, object?> { ["a"] = 21.5, ["b"] = 22.0 }),
            MakeRecord(0, t2, new Dictionary<string, object?> { ["a"] = 21.7 }));

        var freshnessTable = MakeTable(
            MakeRecord(0, t2, new Dictionary<string, object?> { ["entity_id"] = "a" }),
            MakeRecord(0, t1, new Dictionary<string, object?> { ["entity_id"] = "b" }));

        var api = new SequencedQueryApi(new List<FluxTable> { matrixTable }, new List<FluxTable> { freshnessTable });
        var reader = new GroupInfluxReader(api, ValidSettings(), NullLogger<GroupInfluxReader>.Instance);

        var result = await reader.QueryGroupAsync(
            new[] { "sensor.a", "sensor.b" }, "5m", "mean", TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(21.5, result.Rows[0].MemberValues["sensor.a"]);
        Assert.Equal(22.0, result.Rows[0].MemberValues["sensor.b"]);
        Assert.Equal(21.7, result.Rows[1].MemberValues["sensor.a"]);
        Assert.Null(result.Rows[1].MemberValues["sensor.b"]); // excluded — genuine gap, never coerced
    }

    // ─── Freshness tests ──────────────────────────────────────────────────────

    [Fact]
    public async Task QueryGroupAsync_FreshnessFixture_LastSeenUtcReflectsLastPerMember()
    {
        var tA = Instant.FromUtc(2026, 7, 2, 9, 58, 0);
        var tB = Instant.FromUtc(2026, 7, 2, 9, 30, 0);

        var matrixTable = MakeTable(
            MakeRecord(0, tA, new Dictionary<string, object?> { ["a"] = 20.0, ["b"] = 20.0 }));

        var freshnessTable = MakeTable(
            MakeRecord(0, tA, new Dictionary<string, object?> { ["entity_id"] = "a" }),
            MakeRecord(0, tB, new Dictionary<string, object?> { ["entity_id"] = "b" }));

        var api = new SequencedQueryApi(new List<FluxTable> { matrixTable }, new List<FluxTable> { freshnessTable });
        var reader = new GroupInfluxReader(api, ValidSettings(), NullLogger<GroupInfluxReader>.Instance);

        var result = await reader.QueryGroupAsync(
            new[] { "sensor.a", "sensor.b" }, "5m", "mean", TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Equal(tA.ToDateTimeUtc(), result.LastSeenUtc["sensor.a"]);
        Assert.Equal(tB.ToDateTimeUtc(), result.LastSeenUtc["sensor.b"]);
    }
}
