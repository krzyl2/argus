using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Argus.Orchestrator.Batch;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// WS5: the HA Recorder is the second implementor of <see cref="IInfluxDataSource"/>, and on the
/// operator's deployment (influx_url empty — F11) it is the ONLY one. Every test here pins a
/// property that, if it broke, would either silently return the wrong history (a detector primed
/// with the wrong baseline scores wrongly for hours) or take the live scoring stream down with it.
///
/// No live HA is required: the WebSocket is behind <see cref="IHaHistoryConnection"/>.
/// </summary>
public class HaRecorderHistorySourceTests
{
    // ─── Fakes ───────────────────────────────────────────────────────────────

    private sealed record FakeRow(DateTimeOffset At, string State);

    /// <summary>
    /// Stands in for HA: holds a per-entity series, answers only the rows inside the requested
    /// [start, end) slice, and counts connections and commands so the tests can assert the two
    /// things that cost real money against a live Core — handshakes and round-trips.
    /// </summary>
    private sealed class FakeHaHistory
    {
        public readonly Dictionary<string, List<FakeRow>> Series = new();
        public readonly List<(string EntityId, DateTimeOffset Start, DateTimeOffset End)> Commands = new();
        public int Connects { get; private set; }
        public Exception? ThrowOnGetHistory { get; set; }

        public void MarkConnect() => Connects++;

        public JsonElement Handle(string entityId, DateTimeOffset start, DateTimeOffset end)
        {
            Commands.Add((entityId, start, end));
            if (ThrowOnGetHistory is not null)
                throw ThrowOnGetHistory;

            var rows = Series.TryGetValue(entityId, out var series)
                ? series.Where(r => r.At >= start && r.At < end).ToList()
                : new List<FakeRow>();

            // Shape mirrors history/history_during_period with minimal_response/no_attributes:
            // { "<entity_id>": [ { "s": "<state>", "lu": <epoch seconds> }, ... ] }
            var payload = new Dictionary<string, List<Dictionary<string, object>>>
            {
                [entityId] = rows
                    .Select(r => new Dictionary<string, object>
                    {
                        ["s"] = r.State,
                        ["lu"] = r.At.ToUnixTimeMilliseconds() / 1000.0,
                    })
                    .ToList(),
            };
            return JsonSerializer.SerializeToElement(payload);
        }
    }

    private sealed class FakeHistoryConnection : IHaHistoryConnection
    {
        private readonly FakeHaHistory _ha;
        public FakeHistoryConnection(FakeHaHistory ha) => _ha = ha;

        public Task ConnectAndAuthAsync(Uri uri, string token, CancellationToken ct)
        {
            _ha.MarkConnect();
            return Task.CompletedTask;
        }

        public Task<JsonElement> GetHistoryAsync(
            string entityId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
            => Task.FromResult(_ha.Handle(entityId, start, end));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Captures log entries so a criterion phrased as "readable from the log" can be asserted as
    /// exactly that, and not as an internal field only a test can reach.
    /// </summary>
    private sealed class RecordingLogger : ILogger<HaRecorderHistorySource>
    {
        public List<(LogLevel Level, EventId Event, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, eventId, formatter(state, exception)));
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static ConnectionSettings Settings() => new()
    {
        HaUrl = "ws://supervisor/core/websocket",
        HaToken = "test-token",
        InfluxUrl = null,
    };

    private static HaRecorderHistorySource MakeSource(
        FakeHaHistory ha, Func<DateTimeOffset>? clock = null, ConnectionSettings? settings = null,
        ILogger<HaRecorderHistorySource>? logger = null)
        => new(
            settings ?? Settings(),
            logger ?? NullLogger<HaRecorderHistorySource>.Instance,
            () => new FakeHistoryConnection(ha),
            clock ?? (() => Now));

    /// <summary>Appends <paramref name="count"/> rows spaced <paramref name="stepSeconds"/> apart, ending just before <see cref="Now"/>.</summary>
    private static void SeedSeries(FakeHaHistory ha, string entityId, int count, int stepSeconds = 60)
    {
        var rows = new List<FakeRow>();
        for (int i = count; i >= 1; i--)
            rows.Add(new FakeRow(Now.AddSeconds(-i * stepSeconds), (100 + i).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        ha.Series[entityId] = rows;
    }

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lookback_RejectsBadShape_AcceptsCanonical()
    {
        // The lookback contract belongs to the SEAM (InfluxDbReader.cs:25-26), not to either
        // implementor: if the HA path silently accepted "7 days" as something, the same operator
        // config would mean two different windows depending on whether InfluxDB is configured.
        var rejecting = MakeSource(new FakeHaHistory());
        foreach (var bad in new[] { "7 days", "d7", "8", "8x", "" })
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => rejecting.QueryHistoryAsync("sensor.x", bad, 720, CancellationToken.None));
        }

        foreach (var good in new[] { "8d", "24h", "600s" })
        {
            var ha = new FakeHaHistory();
            SeedSeries(ha, "sensor.x", 5, stepSeconds: 10);

            var rows = await MakeSource(ha).QueryHistoryAsync("sensor.x", good, 720, CancellationToken.None);

            Assert.Equal(5, rows.Count);
        }
    }

    [Fact]
    public async Task Lookback_BadShape_RejectedIdenticallyByBothImplementors()
    {
        // Parity of the rejection itself, not just of the accepted set: both implementors must
        // fail the same input the same way, or a lookback typo becomes an implementor-dependent
        // bug that only reproduces on one deployment.
        var haSource = MakeSource(new FakeHaHistory());
        var influx = new InfluxDbReader(
            new ThrowingQueryApi(),
            new ConnectionSettings { InfluxUrl = "http://localhost:8086", InfluxBucket = "b" },
            NullLogger<InfluxDbReader>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(
            () => haSource.QueryHistoryAsync("sensor.x", "7 days", 720, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => influx.QueryHistoryAsync("sensor.x", "7 days", 720, CancellationToken.None));
    }

    private sealed class ThrowingQueryApi : IInfluxQueryApi
    {
        public Task<List<InfluxDB.Client.Core.Flux.Domain.FluxTable>> QueryAsync(
            string flux, string? org, CancellationToken ct)
            => throw new InvalidOperationException("must not be reached — lookback is invalid");
    }

    [Fact]
    public async Task SeamParity_SameLookback_YieldsSameOrderAndCount_AsInfluxReader()
    {
        // InfluxDbReader.cs:167-176 is sort(desc) -> limit -> sort(asc): the NEWEST `limit` rows,
        // ascending. ScoreStreamPipeline feeds those rows straight into WarmupRequest.History and
        // the frozen window, both of which are order-sensitive — the oldest 720 of 1000 points, or
        // the right 720 in descending order, would prime a detector with a fabricated past.
        var ha = new FakeHaHistory();
        SeedSeries(ha, "sensor.parity", 1000, stepSeconds: 60);
        var source = MakeSource(ha);

        var rows = await source.QueryHistoryAsync("sensor.parity", "8d", 720, CancellationToken.None);

        Assert.Equal(720, rows.Count);

        var timestamps = rows.Select(r => r.Timestamp).ToList();
        Assert.Equal(timestamps.OrderBy(t => t).ToList(), timestamps);

        var expectedNewest = ha.Series["sensor.parity"]
            .OrderBy(r => r.At)
            .Skip(1000 - 720)
            .Select(r => r.At.UtcDateTime)
            .ToList();
        Assert.Equal(expectedNewest, timestamps);
    }

    [Fact]
    public async Task NonNumericStatesAreDropped_NotFatal()
    {
        // "unknown"/"unavailable" are the normal content of a Recorder series (a restart writes
        // both). Treating them as an error would mean no priming at all for any sensor that has
        // ever been unavailable; treating them as a value would inject a 0.0 outlier into the
        // median/MAD baseline. They are dropped, and the surrounding rows survive.
        var ha = new FakeHaHistory();
        ha.Series["sensor.mixed"] = new List<FakeRow>
        {
            new(Now.AddMinutes(-5), "21.0"),
            new(Now.AddMinutes(-4), "unknown"),
            new(Now.AddMinutes(-3), "22.5"),
            new(Now.AddMinutes(-2), "unavailable"),
            new(Now.AddMinutes(-1), "23.0"),
        };
        var source = MakeSource(ha);

        var rows = await source.QueryHistoryAsync("sensor.mixed", "24h", 720, CancellationToken.None);

        Assert.Equal(new[] { 21.0, 22.5, 23.0 }, rows.Select(r => r.Value));
    }

    [Fact]
    public async Task CachedWithin60s_OpensOneConnection()
    {
        // E2/§7 #13: the simulator debounces at 400 ms, so an operator dragging a parameter slider
        // would otherwise mean one connect+auth against HA Core per keystroke. The cache is what
        // makes repeated identical replays free; the TTL is what keeps them from going stale.
        var ha = new FakeHaHistory();
        SeedSeries(ha, "sensor.cached", 10);
        var clock = Now;
        var source = MakeSource(ha, () => clock);

        for (int i = 0; i < 5; i++)
            await source.QueryHistoryAsync("sensor.cached", "24h", 720, CancellationToken.None);

        Assert.Equal(1, ha.Connects);

        clock = Now.AddSeconds(61);
        await source.QueryHistoryAsync("sensor.cached", "24h", 720, CancellationToken.None);

        Assert.Equal(2, ha.Connects);
    }

    [Fact]
    public async Task WebSocketFailure_ReturnsEmpty_NeverThrows()
    {
        // The whole reason history runs on its own transient socket: a Recorder failure (including
        // the 4 MB frame guard in HaWebSocketClient.ReceiveMessageAsync) must cost this entity its
        // priming and nothing else. An exception escaping here reaches ScoreStreamPipeline's
        // stream-open path, and the pipeline's degrade branch is the only thing between it and a
        // scoring outage across every entity.
        var ha = new FakeHaHistory
        {
            ThrowOnGetHistory = new InvalidOperationException(
                "HA WebSocket message exceeded 4194304 bytes — dropping connection."),
        };
        SeedSeries(ha, "sensor.boom", 100);
        var source = MakeSource(ha);

        var rows = await source.QueryHistoryAsync("sensor.boom", "8d", 720, CancellationToken.None);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task OneCommandPerEntity()
    {
        // D-K: never one batched command for several entities. HaWebSocketClient throws past 4 MB
        // in a single frame, so batching trades N bounded responses for one that can exceed the
        // cap — and the failure mode of exceeding it is losing the whole query, not truncation.
        var ha = new FakeHaHistory();
        foreach (var id in new[] { "sensor.a", "sensor.b", "sensor.c" })
            SeedSeries(ha, id, 10);
        var source = MakeSource(ha);

        foreach (var id in new[] { "sensor.a", "sensor.b", "sensor.c" })
            await source.QueryHistoryAsync(id, "24h", 720, CancellationToken.None);

        Assert.Equal(3, ha.Commands.Count);
        Assert.Equal(new[] { "sensor.a", "sensor.b", "sensor.c" }, ha.Commands.Select(c => c.EntityId));
    }

    [Fact]
    public void HistoryRequest_DisablesStartTimeState_AndAsksForOneEntity()
    {
        // D-K pins the wire payload, and include_start_time_state is the one flag whose DEFAULT is
        // wrong for us: HA defaults it to true and then prepends a synthetic row stamped at
        // start_time carrying whatever the entity held before the window opened. History is walked
        // in 24 h slices (SliceHours), so on the default every slice boundary would hand back one
        // such row - a copy of the neighbouring slice's reading - and an 8 d backfill would prime
        // the baseline with ~8 fabricated points. They sit inside the F12 tolerance (+/-20), so
        // nothing fails loudly; the median/MAD just quietly gets data HA invented.
        var request = HaWebSocketClient.BuildHistoryRequest(
            7, "sensor.lodowkababcia_power",
            new DateTimeOffset(2026, 8, 27, 5, 18, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(request));
        var root = doc.RootElement;

        Assert.False(root.GetProperty("include_start_time_state").GetBoolean());
        Assert.Equal("history/history_during_period", root.GetProperty("type").GetString());
        Assert.True(root.GetProperty("minimal_response").GetBoolean());
        Assert.True(root.GetProperty("no_attributes").GetBoolean());
        Assert.False(root.GetProperty("significant_changes_only").GetBoolean());
        Assert.Equal(7, root.GetProperty("id").GetInt32());

        // One entity per command, asserted in the payload itself - the 4 MB frame cap turns a
        // batched request into a whole-query loss, not a truncation (OneCommandPerEntity covers
        // the calling side).
        var ids = root.GetProperty("entity_ids");
        Assert.Equal(1, ids.GetArrayLength());
        Assert.Equal("sensor.lodowkababcia_power", ids[0].GetString());
    }

    [Fact]
    public async Task ConnectionCount_IsReadableFromTheDebugLog()
    {
        // E2's acceptance criterion is "200 queries in 60 s -> exactly ONE WS connection, counter
        // in the Debug log". A counter that never reaches the log leaves the criterion
        // unfalsifiable on a running add-on: the operator cannot tell a working cache from a
        // broken one. So this asserts the LOG LINE, not ConnectionsOpened.
        var ha = new FakeHaHistory();
        SeedSeries(ha, "sensor.load_5m", 50);
        var log = new RecordingLogger();
        var source = MakeSource(ha, logger: log);

        for (int i = 0; i < 200; i++)
            await source.QueryHistoryAsync("sensor.load_5m", "8d", 720, CancellationToken.None);

        var opens = log.Entries.Where(e => e.Event == LogEvents.HistoryConnectionOpened).ToList();

        Assert.Single(opens);
        Assert.Equal(LogLevel.Debug, opens[0].Level);
        Assert.Contains("sensor.load_5m", opens[0].Message);
        Assert.Contains("connections opened this process: 1", opens[0].Message);
        Assert.Equal(1, ha.Connects);   // and the line does not lie about it
    }

    [Fact]
    public void HistorySourceRegistered_WhenInfluxUrlNull()
    {
        // F11: influx_url is empty on the operator's install, so before WS5 the container resolved
        // IInfluxDataSource to null and backfill priming was unreachable dead code.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ConnectionSettings { InfluxUrl = null, HaUrl = "ws://supervisor/core/websocket" });

        services.AddHaRecorderHistorySource();

        using var provider = services.BuildServiceProvider();
        var source = provider.GetService<IInfluxDataSource>();

        Assert.NotNull(source);
        Assert.IsType<HaRecorderHistorySource>(source);
    }

    [Fact]
    public void CompositionRoot_CallsTheRegistration_InTheNoInfluxBranch()
    {
        // The test above proves the registration WORKS; it cannot prove Program.cs still calls it.
        // Delete that one line from the else arm and every other test here stays green while the
        // shipped add-on reverts to "influx_url empty == no history source == dead backfill" -
        // exactly the F11 failure WS5 exists to undo. Program.cs is top-level statements and
        // cannot be invoked from a test, so the composition root is pinned as source.
        var program = File.ReadAllText(FindRepoFile("orchestrator/Argus.Orchestrator/Program.cs"));

        var branch = program.IndexOf(
            "if (!string.IsNullOrWhiteSpace(connectionSettings.InfluxUrl))", StringComparison.Ordinal);
        Assert.True(branch >= 0, "Program.cs no longer branches on connectionSettings.InfluxUrl - "
            + "re-point this test at whatever replaced that branch.");

        var elseAt = program.IndexOf("\nelse", branch, StringComparison.Ordinal);
        var buildAt = program.IndexOf("var app = builder.Build();", branch, StringComparison.Ordinal);
        Assert.True(elseAt > branch && buildAt > elseAt, "the influx_url branch lost its else arm");

        var callAt = program.IndexOf("AddHaRecorderHistorySource()", StringComparison.Ordinal);
        Assert.True(callAt > elseAt && callAt < buildAt,
            "Program.cs must register the HA Recorder history source in the else (no-InfluxDB) arm "
            + "of the influx_url branch - F11 depends on it.");
    }

    /// <summary>Resolves a repo-relative path by walking up from the test binary.</summary>
    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"could not find {relativePath} above {AppContext.BaseDirectory}");
    }
}
