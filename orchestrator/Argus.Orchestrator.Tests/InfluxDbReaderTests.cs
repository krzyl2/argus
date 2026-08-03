using Argus.Orchestrator.Batch;
using Argus.Orchestrator.Config;
using InfluxDB.Client.Core.Flux.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for InfluxDbReader: null/empty config guard and empty query result handling.
/// Uses hand-written fakes — no live InfluxDB required (BTCH-01).
/// </summary>
public class InfluxDbReaderTests
{
    // ─── Fakes ───────────────────────────────────────────────────────────────

    /// <summary>Always returns empty table list — simulates InfluxDB with no data.</summary>
    private sealed class EmptyQueryApi : IInfluxQueryApi
    {
        public Task<List<FluxTable>> QueryAsync(string flux, string? org, CancellationToken ct)
            => Task.FromResult(new List<FluxTable>());
    }

    /// <summary>Throws on any call — should never be reached when config is null.</summary>
    private sealed class ThrowingQueryApi : IInfluxQueryApi
    {
        public Task<List<FluxTable>> QueryAsync(string flux, string? org, CancellationToken ct)
            => throw new InvalidOperationException("QueryApi should not be called when config is invalid");
    }

    /// <summary>Captures the last flux string passed to QueryAsync, for shape assertions.</summary>
    private sealed class CapturingQueryApi : IInfluxQueryApi
    {
        public string? LastFlux { get; private set; }

        public Task<List<FluxTable>> QueryAsync(string flux, string? org, CancellationToken ct)
        {
            LastFlux = flux;
            return Task.FromResult(new List<FluxTable>());
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

    private static ConnectionSettings EmptyUrlSettings() => new()
    {
        InfluxUrl = "",
        InfluxBucket = "test-bucket",
    };

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_NullInfluxUrl_ReturnsEmptyListWithoutCallingApi()
    {
        var reader = new InfluxDbReader(new ThrowingQueryApi(), NullUrlSettings(),
            NullLogger<InfluxDbReader>.Instance);

        var result = await reader.QueryAsync("sensor.test", CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task QueryAsync_EmptyInfluxUrl_ReturnsEmptyListWithoutCallingApi()
    {
        var reader = new InfluxDbReader(new ThrowingQueryApi(), EmptyUrlSettings(),
            NullLogger<InfluxDbReader>.Instance);

        var result = await reader.QueryAsync("sensor.test", CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task QueryAsync_NullInfluxBucket_ReturnsEmptyListWithoutCallingApi()
    {
        var reader = new InfluxDbReader(new ThrowingQueryApi(), NullBucketSettings(),
            NullLogger<InfluxDbReader>.Instance);

        var result = await reader.QueryAsync("sensor.test", CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task QueryAsync_InfluxReturnsNoRecords_ReturnsEmptyList()
    {
        var reader = new InfluxDbReader(new EmptyQueryApi(), ValidSettings(),
            NullLogger<InfluxDbReader>.Instance);

        var result = await reader.QueryAsync("sensor.test", CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task QueryAsync_NullInfluxUrl_ReturnTypeIsIReadOnlyList()
    {
        var reader = new InfluxDbReader(new ThrowingQueryApi(), NullUrlSettings(),
            NullLogger<InfluxDbReader>.Instance);

        var result = await reader.QueryAsync("sensor.test", CancellationToken.None);

        // Verify return type contract — callers depend on IReadOnlyList<(DateTime, double)>
        Assert.IsAssignableFrom<IReadOnlyList<(DateTime Timestamp, double Value)>>(result);
    }

    // ─── QueryHistoryAsync tests (Phase 15-03, D-13) ──────────────────────────

    [Fact]
    public async Task QueryHistoryAsync_BuildsExpectedFluxShape()
    {
        var api = new CapturingQueryApi();
        var reader = new InfluxDbReader(api, ValidSettings(), NullLogger<InfluxDbReader>.Instance);

        await reader.QueryHistoryAsync("sensor.x", "30d", 250, CancellationToken.None);

        Assert.NotNull(api.LastFlux);
        var flux = api.LastFlux!;
        var rangeIdx = flux.IndexOf("range(start: -30d)", StringComparison.Ordinal);
        var descIdx = flux.IndexOf("sort(columns: [\"_time\"], desc: true)", StringComparison.Ordinal);
        var limitIdx = flux.IndexOf("limit(n: 250)", StringComparison.Ordinal);
        var ascIdx = flux.IndexOf("sort(columns: [\"_time\"], desc: false)", StringComparison.Ordinal);

        Assert.True(rangeIdx >= 0, "flux must contain range(start: -30d)");
        Assert.True(descIdx > rangeIdx, "descending sort must follow range");
        Assert.True(limitIdx > descIdx, "limit must follow descending sort");
        Assert.True(ascIdx > limitIdx, "ascending sort must follow limit");
    }

    [Fact]
    public async Task QueryAsync_FluxUnchanged_Has24hRangeAndExactlyOneSort()
    {
        // D-13/T-15-03: pins QueryAsync's existing flux so a future edit to the shared
        // guard block cannot silently change the batch path.
        var api = new CapturingQueryApi();
        var reader = new InfluxDbReader(api, ValidSettings(), NullLogger<InfluxDbReader>.Instance);

        await reader.QueryAsync("sensor.x", CancellationToken.None);

        Assert.NotNull(api.LastFlux);
        var flux = api.LastFlux!;
        Assert.Contains("range(start: -24h)", flux);
        var sortCount = System.Text.RegularExpressions.Regex.Matches(flux, "sort\\(").Count;
        Assert.Equal(1, sortCount);
    }

    [Theory]
    [InlineData("sensor.\"x")]
    public async Task QueryHistoryAsync_UnsafeEntityId_ThrowsArgumentException(string entityId)
    {
        var reader = new InfluxDbReader(new ThrowingQueryApi(), ValidSettings(), NullLogger<InfluxDbReader>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(
            () => reader.QueryHistoryAsync(entityId, "30d", 250, CancellationToken.None));
    }

    [Fact]
    public async Task QueryHistoryAsync_UnsafeBucket_ThrowsArgumentException()
    {
        var settings = ValidSettings();
        settings.InfluxBucket = "bucket\\injected";
        var reader = new InfluxDbReader(new ThrowingQueryApi(), settings, NullLogger<InfluxDbReader>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(
            () => reader.QueryHistoryAsync("sensor.x", "30d", 250, CancellationToken.None));
    }

    [Theory]
    [InlineData("30 days")]
    [InlineData("30d\"")]
    public async Task QueryHistoryAsync_InvalidLookback_ThrowsArgumentException(string lookback)
    {
        var reader = new InfluxDbReader(new ThrowingQueryApi(), ValidSettings(), NullLogger<InfluxDbReader>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(
            () => reader.QueryHistoryAsync("sensor.x", lookback, 250, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task QueryHistoryAsync_NonPositiveLimit_ThrowsArgumentOutOfRangeException(int limit)
    {
        var reader = new InfluxDbReader(new ThrowingQueryApi(), ValidSettings(), NullLogger<InfluxDbReader>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reader.QueryHistoryAsync("sensor.x", "30d", limit, CancellationToken.None));
    }

    [Fact]
    public async Task QueryHistoryAsync_NullInfluxUrl_ReturnsEmptyListWithoutCallingApi()
    {
        var reader = new InfluxDbReader(new ThrowingQueryApi(), NullUrlSettings(), NullLogger<InfluxDbReader>.Instance);

        var result = await reader.QueryHistoryAsync("sensor.x", "30d", 250, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task QueryHistoryAsync_NullInfluxBucket_ReturnsEmptyListWithoutCallingApi()
    {
        var reader = new InfluxDbReader(new ThrowingQueryApi(), NullBucketSettings(), NullLogger<InfluxDbReader>.Instance);

        var result = await reader.QueryHistoryAsync("sensor.x", "30d", 250, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task QueryHistoryAsync_InfluxReturnsNoRecords_ReturnsEmptyList()
    {
        var reader = new InfluxDbReader(new EmptyQueryApi(), ValidSettings(), NullLogger<InfluxDbReader>.Instance);

        var result = await reader.QueryHistoryAsync("sensor.x", "30d", 250, CancellationToken.None);

        Assert.Empty(result);
    }
}
