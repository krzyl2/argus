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
}
