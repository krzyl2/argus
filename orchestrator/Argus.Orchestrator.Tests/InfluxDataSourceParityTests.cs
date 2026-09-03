using System.Text.Json;
using Argus.Orchestrator.Batch;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Ha;
using InfluxDB.Client.Core.Flux.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// E3: the two implementors of <see cref="IInfluxDataSource"/> — InfluxDbReader and
/// HaRecorderHistorySource — must answer the SAME question the same way when fed the same
/// series.
///
/// This matters because the simulator's lookback is operator-chosen and forwarded verbatim
/// through the seam. If "8d" meant eight days on one deployment and was rejected (or silently
/// reinterpreted) on the other, every acceptance number in §5.6 would be deployment-dependent
/// and the F13 comparison would be meaningless. The existing coverage pins the REJECTION
/// parity (Lookback_BadShape_RejectedIdenticallyByBothImplementors); this file pins the
/// accepted side — same rows, same order, same count, from one shared fixture.
/// </summary>
public class InfluxDataSourceParityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The one series both implementors are asked about, newest last.</summary>
    private static List<(DateTimeOffset At, double Value)> SharedSeries(int count, int stepSeconds)
        => Enumerable.Range(1, count)
            .Select(i => (Now.AddSeconds(-(count - i + 1) * stepSeconds), 100.0 + (i % 7)))
            .ToList();

    // ─── Influx side ─────────────────────────────────────────────────────────

    private sealed class SeriesQueryApi : IInfluxQueryApi
    {
        private readonly List<(DateTimeOffset At, double Value)> _series;
        public SeriesQueryApi(List<(DateTimeOffset At, double Value)> series) => _series = series;

        public string? LastFlux { get; private set; }

        public Task<List<FluxTable>> QueryAsync(string flux, string? org, CancellationToken ct)
        {
            LastFlux = flux;

            // The Flux itself does sort(desc) -> limit -> sort(asc); this fake replays the
            // documented OUTCOME of that pipeline rather than parsing the query text.
            var limit = ExtractLimit(flux);
            var rows = _series.OrderBy(r => r.At).TakeLast(limit).ToList();

            var table = new FluxTable();
            foreach (var (at, value) in rows)
            {
                var record = new FluxRecord(0);
                record.Values["_time"] = Instant.FromDateTimeOffset(at);
                record.Values["_value"] = value;
                table.Records.Add(record);
            }
            return Task.FromResult(new List<FluxTable> { table });
        }

        private static int ExtractLimit(string flux)
        {
            var marker = "limit(n: ";
            var start = flux.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            var end = flux.IndexOf(')', start);
            return int.Parse(flux[start..end]);
        }
    }

    // ─── HA Recorder side ────────────────────────────────────────────────────

    private sealed class SeriesHistoryConnection : IHaHistoryConnection
    {
        private readonly List<(DateTimeOffset At, double Value)> _series;
        public SeriesHistoryConnection(List<(DateTimeOffset At, double Value)> series)
            => _series = series;

        public Task ConnectAndAuthAsync(Uri uri, string token, CancellationToken ct)
            => Task.CompletedTask;

        public Task<JsonElement> GetHistoryAsync(
            string entityId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
        {
            var rows = _series.Where(r => r.At >= start && r.At < end).ToList();
            var payload = new Dictionary<string, List<Dictionary<string, object>>>
            {
                [entityId] = rows
                    .Select(r => new Dictionary<string, object>
                    {
                        ["s"] = r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["lu"] = r.At.ToUnixTimeMilliseconds() / 1000.0,
                    })
                    .ToList(),
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(payload));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static InfluxDbReader MakeInflux(List<(DateTimeOffset At, double Value)> series)
        => new(
            new SeriesQueryApi(series),
            new ConnectionSettings
            {
                InfluxUrl = "http://localhost:8086",
                InfluxToken = "t",
                InfluxOrg = "o",
                InfluxBucket = "b",
                InfluxMeasurement = "homeassistant",
                InfluxValueField = "value",
            },
            NullLogger<InfluxDbReader>.Instance);

    private static HaRecorderHistorySource MakeRecorder(List<(DateTimeOffset At, double Value)> series)
        => new(
            new ConnectionSettings
            {
                HaUrl = "ws://supervisor/core/websocket",
                HaToken = "t",
                InfluxUrl = null,
            },
            NullLogger<HaRecorderHistorySource>.Instance,
            () => new SeriesHistoryConnection(series),
            () => Now);

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("8d")]
    [InlineData("24h")]
    public async Task Lookback_8d_YieldsIdenticalRowsAcrossImplementations(string lookback)
    {
        // 600 points one minute apart: inside 24 h, comfortably inside 8 d, and below the
        // limit, so neither implementor is truncating. Any difference in the answer is a
        // difference in MEANING, which is the thing the simulator cannot tolerate.
        var series = SharedSeries(600, stepSeconds: 60);

        var influxRows = await MakeInflux(series)
            .QueryHistoryAsync("sensor.parity", lookback, 2000, CancellationToken.None);
        var recorderRows = await MakeRecorder(series)
            .QueryHistoryAsync("sensor.parity", lookback, 2000, CancellationToken.None);

        Assert.Equal(600, influxRows.Count);
        Assert.Equal(influxRows.Count, recorderRows.Count);
        Assert.Equal(
            influxRows.Select(r => r.Timestamp).ToList(),
            recorderRows.Select(r => r.Timestamp).ToList());
        Assert.Equal(
            influxRows.Select(r => r.Value).ToList(),
            recorderRows.Select(r => r.Value).ToList());
    }

    [Fact]
    public async Task Limit_TakesTheNewestRowsAscending_OnBothImplementations()
    {
        // The simulator clamps to maxPoints and then charts the result against wall-clock
        // time. The OLDEST N rows, or the right N in descending order, would draw a chart of
        // a window the operator did not ask about — and, in the priming path that shares this
        // seam, would prime a detector with a fabricated past.
        var series = SharedSeries(600, stepSeconds: 60);
        var expected = series.OrderBy(r => r.At).TakeLast(100)
            .Select(r => r.At.UtcDateTime).ToList();

        var influxRows = await MakeInflux(series)
            .QueryHistoryAsync("sensor.parity", "24h", 100, CancellationToken.None);
        var recorderRows = await MakeRecorder(series)
            .QueryHistoryAsync("sensor.parity", "24h", 100, CancellationToken.None);

        Assert.Equal(expected, influxRows.Select(r => r.Timestamp).ToList());
        Assert.Equal(expected, recorderRows.Select(r => r.Timestamp).ToList());
    }
}
