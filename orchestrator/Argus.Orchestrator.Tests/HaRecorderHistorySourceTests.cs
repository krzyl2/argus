using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Argus.Detector.V1;
using Argus.Orchestrator.Batch;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Logging;
using Grpc.Net.Client;
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

        public int Closes { get; private set; }

        /// <summary>Highest number of simultaneously open connections seen during the run.</summary>
        public int MaxConcurrentlyOpen { get; private set; }

        private int _open;

        public void MarkConnect()
        {
            Connects++;
            _open++;
            if (_open > MaxConcurrentlyOpen)
                MaxConcurrentlyOpen = _open;
        }

        public void MarkClose()
        {
            Closes++;
            _open--;
        }

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

        public async Task ConnectAndAuthAsync(Uri uri, string token, CancellationToken ct)
        {
            // Yield before recording the connect: without it this fake is fast enough to run
            // every query to completion synchronously, which would hide a missing semaphore from
            // HistoryConnections_AreTransientAndNeverOverlap.
            await Task.Yield();
            _ha.MarkConnect();
        }

        public Task<JsonElement> GetHistoryAsync(
            string entityId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
            => Task.FromResult(_ha.Handle(entityId, start, end));

        public ValueTask DisposeAsync()
        {
            _ha.MarkClose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Captures log entries so a criterion phrased as "readable from the log" can be asserted as
    /// exactly that, and not as an internal field only a test can reach.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
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
        var log = new RecordingLogger<HaRecorderHistorySource>();
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


    // --- F11/F12 acceptance criteria, pinned offline -------------------------

    /// <summary>
    /// F12's measured baseline: 1546 rows for sensor.lodowkababcia_power over an 8 d lookback,
    /// oldest stamp 2026-08-27T05:18Z, and the SAME count when asked for 30 d because the
    /// Recorder only keeps 7 days.
    /// </summary>
    private const int F12RowCount = 1546;

    private static readonly DateTimeOffset F12OldestStamp =
        new(2026, 8, 27, 5, 18, 0, TimeSpan.Zero);

    /// <summary>F12's measurement was taken exactly 7 d (the Recorder's retention) after the oldest row.</summary>
    private static readonly DateTimeOffset F12Now = F12OldestStamp.AddDays(7);

    /// <summary>Rebuilds the F12 series: 1546 rows evenly spread across the Recorder's 7 day window.</summary>
    private static void SeedF12Series(FakeHaHistory ha, string entityId)
    {
        var step = TimeSpan.FromTicks(TimeSpan.FromDays(7).Ticks / F12RowCount);
        var rows = new List<FakeRow>();
        for (int i = 0; i < F12RowCount; i++)
        {
            rows.Add(new FakeRow(
                F12OldestStamp + step * i,
                (40 + (i % 7)).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        ha.Series[entityId] = rows;
    }

    [Fact]
    public async Task F12_EightDayLookback_ReturnsTheMeasuredWindow_AndThirtyDaysReturnsNoMore()
    {
        // F12 is a measurement off the live install, and the reason it belongs here as a
        // REGRESSION fixture is that both of its halves fail silently. Half one: 8 d must reach
        // past the 7 d retention edge so the whole window arrives - a slice walk that gave up at
        // the first thin slice would return "plenty of rows" and nothing would say otherwise.
        // Half two: 30 d must cost no extra rows, because the Recorder has nothing older; a wider
        // lookback that suddenly returns MORE means rows are being duplicated across slice
        // boundaries, which is precisely what include_start_time_state used to do.
        var ha = new FakeHaHistory();
        SeedF12Series(ha, "sensor.lodowkababcia_power");
        var source = MakeSource(ha, clock: () => F12Now);

        var eightDays = await source.QueryHistoryAsync(
            "sensor.lodowkababcia_power", "8d", 2000, CancellationToken.None);
        var commandsAt8d = ha.Commands.Count;

        var thirtyDays = await source.QueryHistoryAsync(
            "sensor.lodowkababcia_power", "30d", 2000, CancellationToken.None);
        var commandsAt30d = ha.Commands.Count - commandsAt8d;

        Assert.InRange(eightDays.Count, F12RowCount - 20, F12RowCount + 20);
        Assert.Equal(eightDays.Count, thirtyDays.Count);

        // The oldest row must not be younger than the measured retention edge: an early stop
        // still returns a lot of rows, just not the ones from seven days ago.
        Assert.True(eightDays[0].Timestamp <= F12OldestStamp.UtcDateTime.AddMinutes(1),
            $"oldest row {eightDays[0].Timestamp:O} is newer than the measured edge {F12OldestStamp:O}");

        // 5.3: commands per entity stay <= 10 even at a 30 d lookback against a 7 d Recorder -
        // the empty-slice stop is what keeps a wide lookback from costing 30 round trips of
        // nothing on every restart.
        Assert.True(commandsAt30d <= 10, $"30d lookback issued {commandsAt30d} commands");
    }

    [Fact]
    public async Task PrimedLogLine_NamesHaRecorderAsTheSource()
    {
        // F11's acceptance criterion is a startup line reading "primed <entity> <n> points from
        // HA Recorder". The source name is the load-bearing part: on this install influx_url is
        // empty, so a line saying only "Primed sensor.x with 720 history points" looks the same
        // whether the Recorder answered or whether IInfluxDataSource resolved to null and priming
        // never ran at all - and telling those two apart IS the F11 check.
        var ha = new FakeHaHistory();
        SeedF12Series(ha, "sensor.lodowkababcia_power");
        var source = MakeSource(ha, clock: () => F12Now);
        var log = new RecordingLogger<ScoreStreamPipeline>();

        var pipeline = MakePipeline(source, log, out var detectorClient);
        await pipeline.PrimeFromHistoryAsync(
            "sensor.lodowkababcia_power", NewEntityState(), CancellationToken.None);

        var primed = Assert.Single(log.Entries, e => e.Event == LogEvents.WarmupPrimed);
        Assert.Equal(LogLevel.Information, primed.Level);
        Assert.Contains("sensor.lodowkababcia_power", primed.Message);
        Assert.Contains("from HA Recorder", primed.Message);

        // ...and n > 0, the other half of the criterion.
        var pointCount = detectorClient.LastWarmupRequest!.History.Count;
        Assert.True(pointCount > 0);
        Assert.Contains($"{pointCount} history points", primed.Message);
    }

    [Fact]
    public async Task EmptyRecorderResult_IsAWarningNamingTheEntity_NotSilence()
    {
        // 5.3 case (e): success == true with an empty result for a watched entity is an HA-side
        // visibility/permission problem, not a normal outcome - and it is the one path that emits
        // no "Primed ..." line at all. Without this warning "backfill is off", "the entity is
        // invisible to the Supervisor token" and "the Recorder is empty" are all just missing
        // output, and F11's "n > 0 for all five entities" would have to be checked by noticing
        // what is NOT in the log.
        var ha = new FakeHaHistory();
        ha.Series["sensor.invisible"] = new List<FakeRow>();
        var source = MakeSource(ha, clock: () => F12Now);
        var log = new RecordingLogger<ScoreStreamPipeline>();

        var pipeline = MakePipeline(source, log, out var detectorClient);
        await pipeline.PrimeFromHistoryAsync("sensor.invisible", NewEntityState(), CancellationToken.None);

        var empty = Assert.Single(log.Entries, e => e.Event == LogEvents.HistoryEmpty);
        Assert.Equal(LogLevel.Warning, empty.Level);
        Assert.Contains("sensor.invisible", empty.Message);
        Assert.Contains("HA Recorder", empty.Message);
        Assert.Equal(0, detectorClient.WarmupCallCount);
    }

    [Fact]
    public async Task HistoryConnections_AreTransientAndNeverOverlap()
    {
        // Non-invasiveness towards the live stream. Two failure shapes are pinned here because
        // neither shows up in the returned rows.
        // (1) A connection kept alive between queries would show fewer connects than fetches -
        //     that is the second persistent socket ADR-4 forbids, and the state in which a
        //     history response starts consuming state_changed frames.
        // (2) Two open at once would mean the semaphore stopped serializing, turning startup
        //     priming of six entities into six simultaneous connect+auth handshakes against the
        //     Supervisor proxy - the reconnect storm the criterion rules out.
        var ha = new FakeHaHistory();
        SeedSeries(ha, "sensor.load_5m", 30);
        var source = MakeSource(ha);

        // Distinct lookbacks so the 60 s cache cannot absorb the calls and hide the answer.
        await Task.WhenAll(
            source.QueryHistoryAsync("sensor.load_5m", "1h", 720, CancellationToken.None),
            source.QueryHistoryAsync("sensor.load_5m", "2h", 720, CancellationToken.None),
            source.QueryHistoryAsync("sensor.load_5m", "3h", 720, CancellationToken.None));

        Assert.Equal(3, ha.Connects);
        Assert.Equal(3, ha.Closes);            // each one closed again: transient, not pooled
        Assert.Equal(1, ha.MaxConcurrentlyOpen);
    }

    // --- Pipeline wiring for the two log-line tests --------------------------

    private static EntityRuntimeState NewEntityState()
        => new(HstParams.From(new Dictionary<string, string> { ["window"] = "250" }));

    /// <summary>
    /// A ScoreStreamPipeline wired to the REAL HaRecorderHistorySource: the log-line criteria are
    /// about what the two components print together, so faking the seam here would assert the
    /// test's own string instead of the shipped one.
    /// </summary>
    private static ScoreStreamPipeline MakePipeline(
        HaRecorderHistorySource source,
        ILogger<ScoreStreamPipeline> logger,
        out FakeWarmupDetectorClient detectorClient)
    {
        var cfg = new EntitiesConfig();
        cfg.Entities.Add(new EntityConfig
        {
            EntityId = "sensor.lodowkababcia_power",
            FriendlyName = "Lodowka babcia - moc",
            Detectors = new List<DetectorConfig>
            {
                new DetectorConfig { Name = "hst", Params = new Dictionary<string, string> { ["window"] = "250" } },
            },
        });

        detectorClient = new FakeWarmupDetectorClient
        {
            WarmupResponse = new WarmupResponse { Ok = true, NSeen = 720, WarmedUp = true },
        };

        return new ScoreStreamPipeline(
            new FakeStatePublisher(),
            logger,
            new LiveEntitiesConfig(cfg),
            // Never dialled: PrimeFromHistoryAsync does not touch the gateway.
            new DetectionGateway(GrpcChannel.ForAddress("http://localhost:1"), NullLogger<DetectionGateway>.Instance),
            historySource: source,
            detectorClient: detectorClient,
            connectionSettings: new ConnectionSettings { BackfillEnabled = true, BackfillLookback = "8d" });
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
