using Argus.Detector.V1;
using Argus.Orchestrator.Batch;
using Argus.Orchestrator.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for SimulateService — specifically the history cache (E2).
///
/// The cache is not a performance nicety. On the operator's install influx_url is empty
/// (F11), so every history read is a fresh WebSocket connect + auth to HA Core on a
/// short-lived socket (§7 #13). The panel re-runs on every parameter edit behind a 400 ms
/// debounce, and the acceptance recipe (B6) fires 200 replays in a loop — uncached, that is
/// 200 authentications against Core for data that did not change. The cache key is
/// deliberately (entityId, lookback) and NOT the detector params: params change what is
/// replayed, never what is fetched, which is the whole reason one fetch can serve a slider
/// drag.
/// </summary>
public class SimulateServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);

    private sealed class CountingHistorySource : IInfluxDataSource
    {
        public int CallCount { get; private set; }
        public List<(string EntityId, string Lookback, int Limit)> Calls { get; } = new();
        public int Rows { get; init; } = 300;

        public Task<IReadOnlyList<(DateTime Timestamp, double Value)>> QueryAsync(
            string entityId, CancellationToken ct)
            => QueryHistoryAsync(entityId, "24h", Rows, ct);

        public Task<IReadOnlyList<(DateTime Timestamp, double Value)>> QueryHistoryAsync(
            string entityId, string lookback, int limit, CancellationToken ct)
        {
            CallCount++;
            Calls.Add((entityId, lookback, limit));

            IReadOnlyList<(DateTime, double)> rows = Enumerable.Range(0, Rows)
                .Select(i => (T0.UtcDateTime.AddMinutes(i), 100.0 + (i % 3)))
                .ToList();
            return Task.FromResult(rows);
        }
    }

    private sealed class ZeroScoreDetectorClient : IBatchDetectorClient
    {
        public int SimulateCallCount { get; private set; }
        public IReadOnlyDictionary<string, string>? LastParams { get; private set; }

        public Task<ScoreBatchResponse> ScoreBatchAsync(ScoreBatchRequest request, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<FitResponse> FitAsync(FitRequest request, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<GroupScoreResponse> ScoreGroupBatchAsync(GroupScoreRequest request, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<FitGroupResponse> FitGroupAsync(FitGroupRequest request, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<WarmupResponse> WarmupAsync(WarmupRequest request, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<SimulateResult> SimulateBatchAsync(
            string entityId, string detector,
            IReadOnlyDictionary<string, string> parameters,
            IReadOnlyList<HistoryPoint> history, CancellationToken ct)
        {
            SimulateCallCount++;
            LastParams = parameters;
            return Task.FromResult(new SimulateResult(
                true, null, new double[history.Count], Array.Empty<double>(), 60, 60, "test"));
        }
    }

    private static (SimulateService Service, CountingHistorySource History, ZeroScoreDetectorClient Detector, Func<DateTimeOffset> _)
        Build(Func<DateTimeOffset> clock, int rows = 300)
    {
        var history = new CountingHistorySource { Rows = rows };
        var detector = new ZeroScoreDetectorClient();
        var service = new SimulateService(
            history, detector, NullLogger<SimulateService>.Instance, clock);
        return (service, history, detector, clock);
    }

    private static readonly Dictionary<string, string> NoParams = new();

    [Fact]
    public async Task HistoryCached_60s_SingleFetchForRepeatedRuns()
    {
        var now = T0;
        var (service, history, detector, _) = Build(() => now);

        // 10 replays inside one second — a slider drag past the 400 ms debounce.
        for (var i = 0; i < 10; i++)
        {
            now = T0.AddMilliseconds(i * 100);
            await service.RunAsync(
                "sensor.load_5m", "rmad", NoParams, "24h", 2000, CancellationToken.None);
        }

        Assert.Equal(1, history.CallCount);
        // The DETECTOR still ran ten times: the cache must not short-circuit the replay
        // itself, only the fetch. A cached summary would silently ignore the new parameters.
        Assert.Equal(10, detector.SimulateCallCount);

        // Past the TTL the data is refetched — an operator watching a live sensor must not be
        // shown a snapshot that has quietly gone stale.
        now = T0.AddSeconds(61);
        await service.RunAsync(
            "sensor.load_5m", "rmad", NoParams, "24h", 2000, CancellationToken.None);

        Assert.Equal(2, history.CallCount);
    }

    [Fact]
    public async Task ParamChanges_DoNotInvalidateTheHistoryCache()
    {
        var now = T0;
        var (service, history, _, _) = Build(() => now);

        await service.RunAsync("sensor.load_5m", "rmad",
            new Dictionary<string, string> { ["high_threshold"] = "0.5" }, "24h", 2000, default);
        await service.RunAsync("sensor.load_5m", "rmad",
            new Dictionary<string, string> { ["high_threshold"] = "0.6" }, "24h", 2000, default);

        Assert.Equal(1, history.CallCount);
    }

    [Fact]
    public async Task DifferentLookback_UsesSeparateCacheKey()
    {
        var now = T0;
        var (service, history, _, _) = Build(() => now);

        await service.RunAsync("sensor.load_5m", "rmad", NoParams, "24h", 2000, default);
        await service.RunAsync("sensor.load_5m", "rmad", NoParams, "8d", 2000, default);
        await service.RunAsync("sensor.load_5m", "rmad", NoParams, "24h", 2000, default);

        // Two fetches, not one and not three: "24h" and "8d" are different questions, and the
        // second "24h" must still be served from cache.
        Assert.Equal(2, history.CallCount);
        Assert.Equal(new[] { "24h", "8d" }, history.Calls.Select(c => c.Lookback).ToArray());
    }

    [Fact]
    public async Task DifferentEntity_UsesSeparateCacheKey()
    {
        var now = T0;
        var (service, history, _, _) = Build(() => now);

        await service.RunAsync("sensor.a", "rmad", NoParams, "24h", 2000, default);
        await service.RunAsync("sensor.b", "rmad", NoParams, "24h", 2000, default);

        Assert.Equal(2, history.CallCount);
    }

    [Fact]
    public async Task MaxPoints_IsClampedBeforeItReachesTheHistorySource()
    {
        var now = T0;
        var (service, history, _, _) = Build(() => now);

        await service.RunAsync("sensor.a", "rmad", NoParams, "24h", 999_999, default);

        // §7 #16: the clamp is one of only four brakes in front of an endpoint whose sole
        // authentication is the TCP peer check, so it has to bite before the query, not after.
        Assert.Equal(SimulateService.MaxMaxPoints, history.Calls[0].Limit);
    }

    [Fact]
    public async Task EmptyHistory_ReportsNotOkInsteadOfAnEmptyChart()
    {
        var now = T0;
        var (service, _, detector, _) = Build(() => now, rows: 0);

        var result = await service.RunAsync("sensor.a", "rmad", NoParams, "24h", 2000, default);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrEmpty(result.Error));
        // And the detector was never asked — a zero-point Simulate is a guaranteed ok=false.
        Assert.Equal(0, detector.SimulateCallCount);
    }

    [Theory]
    [InlineData("24h", true)]
    [InlineData("8d", true)]
    [InlineData("300s", true)]
    [InlineData("30", false)]
    [InlineData("24 h", false)]
    [InlineData("-1h", false)]
    [InlineData("24y", false)]
    [InlineData("", false)]
    public void IsValidLookback_MatchesTheInfluxDurationContract(string lookback, bool expected)
    {
        // The literal is forwarded verbatim into InfluxDbReader's Flux guard, which THROWS on
        // a bad shape. Rejecting it at the boundary is what turns an operator typo into a 400
        // instead of a 500 (and keeps the guard's injection check from being the last line of
        // defence).
        Assert.Equal(expected, SimulateService.IsValidLookback(lookback));
    }

    [Theory]
    [InlineData(null, SimulateService.DefaultMaxPoints)]
    [InlineData(1, SimulateService.MinMaxPoints)]
    [InlineData(20000, SimulateService.MaxMaxPoints)]
    [InlineData(2500, 2500)]
    public void ClampMaxPoints_HonoursTheDocumentedBounds(int? requested, int expected)
        => Assert.Equal(expected, SimulateService.ClampMaxPoints(requested));
}
