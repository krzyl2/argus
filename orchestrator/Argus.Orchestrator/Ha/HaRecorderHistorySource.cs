using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Argus.Orchestrator.Batch;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Logging;

namespace Argus.Orchestrator.Ha;

/// <summary>
/// <see cref="IInfluxDataSource"/> backed by the HA Recorder over WebSocket
/// (history/history_during_period) — the second implementor of that seam, and the only history
/// source that exists on a deployment with no InfluxDB configured (F11: influxUrl=null, F12: the
/// Recorder holds 7 days). Without it, backfill priming is dead code on this install and every
/// entity waits out its warm-up on live traffic alone (~6.5 h on a 225-readings-a-day sensor).
///
/// Three contracts are copied verbatim from <see cref="InfluxDbReader"/> so the two implementors
/// cannot drift apart:
///   - lookback shape is <c>^\d+[smhdw]$</c>, anything else throws ArgumentException;
///   - a non-positive limit throws ArgumentOutOfRangeException;
///   - results are the NEWEST <c>limit</c> points, returned in ASCENDING time order.
/// Everything after validation degrades to zero rows instead of throwing (D-15): a Recorder that
/// is slow, absent or answering in an unexpected shape must cost the entity its priming, never
/// its live stream.
///
/// Queries run on a short-lived, request/response-only connection (D-K) — see the ADR-4 note in
/// NetDaemonHaEventSource — serialized by a semaphore, one command per entity per 24 h slice.
/// </summary>
internal sealed class HaRecorderHistorySource : IInfluxDataSource
{
    // Verbatim from InfluxDbReader.cs:25-26 — the seam's lookback contract, not a local choice.
    private static readonly Regex _lookbackShape = new(@"^\d+[smhdw]$", RegexOptions.Compiled);

    /// <summary>
    /// History is fetched in 24 h slices walking backwards from now. One command for the whole
    /// lookback would put ~8 days of a 5000-readings-a-day sensor (~40 k rows) into a single
    /// frame, and HaWebSocketClient throws — killing the query — past 4 MB.
    /// </summary>
    internal const int SliceHours = 24;

    /// <summary>
    /// Stop after this many consecutive empty slices: the Recorder's retention edge produces
    /// empty slices for the rest of the window, and walking them costs one connect-free but
    /// still round-tripped command each.
    /// </summary>
    internal const int MaxConsecutiveEmptySlices = 2;

    private const int CacheCapacity = 32;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly ConnectionSettings _settings;
    private readonly ILogger<HaRecorderHistorySource> _logger;
    private readonly Func<IHaHistoryConnection> _connectionFactory;
    private readonly Func<DateTimeOffset> _now;

    // Serializes queries: one transient connect+auth at a time, so priming six entities at
    // startup is six sequential handshakes, not six concurrent ones against the Supervisor proxy.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // (entityId, lookback, limit) -> rows, 60 s TTL, 32 entries, LRU eviction. Guarded by _gate.
    // E2: the simulator's 400 ms debounce would otherwise turn parameter tweaking into a
    // connect+auth storm against HA Core.
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<(DateTime, double)> Rows)> _cache = new();
    private readonly LinkedList<string> _lru = new();

    public HaRecorderHistorySource(
        ConnectionSettings settings,
        ILogger<HaRecorderHistorySource> logger,
        Func<IHaHistoryConnection>? connectionFactory = null,
        Func<DateTimeOffset>? now = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionFactory = connectionFactory ?? (() => new HaWebSocketClient());
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Connections opened so far — Debug counter behind the 60 s cache criterion (E2).</summary>
    internal int ConnectionsOpened { get; private set; }

    /// <inheritdoc/>
    /// <remarks>
    /// F11's acceptance criterion is phrased "primed &lt;entity&gt; &lt;n&gt; points from HA
    /// Recorder": the operator must be able to read the SOURCE off the startup log, because on
    /// this install (influx_url empty) a priming line that names no source looks identical
    /// whether the Recorder answered or no history source was registered at all.
    /// </remarks>
    public string SourceName => "HA Recorder";

    /// <summary>
    /// The seam's rolling 24 h batch query. Same window as InfluxDbReader.QueryAsync; the row
    /// ceiling is the configured backfill cap because a WebSocket response, unlike a Flux result,
    /// has a hard frame limit.
    /// </summary>
    public Task<IReadOnlyList<(DateTime Timestamp, double Value)>> QueryAsync(
        string entityId, CancellationToken ct)
        => QueryHistoryAsync(entityId, "24h", RowCap, ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<(DateTime Timestamp, double Value)>> QueryHistoryAsync(
        string entityId, string lookback, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            throw new ArgumentException("entityId must not be empty", nameof(entityId));

        // Validation throws (contract parity with InfluxDbReader); everything below degrades.
        var span = ParseLookback(lookback);

        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit must be > 0");

        var key = $"{entityId}|{lookback}|{limit}";

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryGetCached(key, out var cached))
                return cached;

            var rows = await FetchAsync(entityId, lookback, span, limit, ct).ConfigureAwait(false);
            Store(key, rows);
            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    private int RowCap => Math.Clamp(_settings.BackfillRowCap, 1, 20000);

    /// <summary>
    /// Walks 24 h slices backwards from now on one transient connection, stopping at the row cap,
    /// the lookback edge, or <see cref="MaxConsecutiveEmptySlices"/> empty slices — then returns
    /// the newest <paramref name="limit"/> rows ascending.
    /// </summary>
    private async Task<IReadOnlyList<(DateTime Timestamp, double Value)>> FetchAsync(
        string entityId, string lookback, TimeSpan span, int limit, CancellationToken ct)
    {
        var now = _now();
        var windowStart = now - span;
        var rowCap = RowCap;

        var collected = new List<(DateTime Timestamp, double Value)>();
        var sliceEnd = now;
        var commands = 0;
        var emptyStreak = 0;

        try
        {
            await using var connection = _connectionFactory();
            await connection
                .ConnectAndAuthAsync(
                    NetDaemonHaEventSource.BuildWsUri(_settings.HaUrl),
                    _settings.HaToken ?? string.Empty,
                    ct)
                .ConfigureAwait(false);
            ConnectionsOpened++;

            // The E2 acceptance criterion ("200 queries inside 60 s open exactly ONE WS
            // connection") is only checkable if the counter reaches the log — an internal field
            // nobody can read proves nothing about a running add-on. Debug, because on the happy
            // path this fires once per entity per minute at most; if it starts repeating, the
            // cache is not holding and the operator sees it here first.
            _logger.LogDebug(LogEvents.HistoryConnectionOpened,
                "HA Recorder history connection opened for {EntityId} (lookback={Lookback}) — "
                + "connections opened this process: {ConnectionsOpened}",
                entityId, lookback, ConnectionsOpened);

            while (sliceEnd > windowStart
                   && collected.Count < rowCap
                   && emptyStreak < MaxConsecutiveEmptySlices)
            {
                var sliceStart = sliceEnd - TimeSpan.FromHours(SliceHours);
                if (sliceStart < windowStart)
                    sliceStart = windowStart;

                var result = await connection.GetHistoryAsync(entityId, sliceStart, sliceEnd, ct)
                    .ConfigureAwait(false);
                commands++;

                var parsed = ParseRows(entityId, result);
                if (parsed.Count == 0)
                {
                    emptyStreak++;
                }
                else
                {
                    emptyStreak = 0;
                    collected.AddRange(parsed);
                }

                sliceEnd = sliceStart;
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // A Recorder failure is a missing prime, never a broken stream: the live socket is a
            // different connection and must not learn about this at all.
            _logger.LogWarning(LogEvents.HistoryFetchFailed, ex,
                "HA Recorder history query failed for {EntityId} after {Commands} command(s) — "
                + "continuing with {Rows} row(s)", entityId, commands, collected.Count);
        }

        collected.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));

        // Same ordering contract as InfluxDbReader.cs:167-176 — newest `limit`, ascending.
        var rows = collected.Count > limit
            ? collected.GetRange(collected.Count - limit, limit)
            : collected;

        _logger.LogInformation(LogEvents.HistoryFetched,
            "HistoryFetched {EntityId}: {Rows} row(s) fetched, {Returned} returned "
            + "(lookback={Lookback} spanHours={SpanHours:F1} commands={Commands})",
            entityId, collected.Count, rows.Count, lookback, (now - sliceEnd).TotalHours, commands);

        return rows;
    }

    /// <summary>
    /// Parses the <c>result</c> element into (timestamp, value) rows.
    ///
    /// The response shape is the one thing in this class that was never observed against a live
    /// HA (the plan's open blocker #4), so every known spelling is accepted and anything
    /// unrecognised is skipped silently: a shape surprise must cost rows, not an exception.
    /// Accepted: <c>{entity_id: [row, ...]}</c> and a bare array (of rows, or of per-entity
    /// arrays); state under <c>s</c>/<c>state</c>; timestamp under <c>lu</c>/<c>last_updated</c>/
    /// <c>lc</c>/<c>last_changed</c>, as epoch seconds or an ISO-8601 string.
    ///
    /// Non-numeric states (unknown/unavailable/text) are dropped silently — that is the normal
    /// content of a Recorder series, not an error.
    /// </summary>
    internal static List<(DateTime Timestamp, double Value)> ParseRows(string entityId, JsonElement result)
    {
        var rows = new List<(DateTime, double)>();

        if (result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty(entityId, out var byId))
            {
                AppendRows(byId, rows);
            }
            else
            {
                foreach (var prop in result.EnumerateObject())
                    AppendRows(prop.Value, rows);
            }
        }
        else if (result.ValueKind == JsonValueKind.Array)
        {
            AppendRows(result, rows);
        }

        return rows;
    }

    private static void AppendRows(JsonElement element, List<(DateTime, double)> rows)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in element.EnumerateArray())
        {
            // REST-shaped payloads nest one array per entity; recurse one level for those.
            if (item.ValueKind == JsonValueKind.Array)
            {
                AppendRows(item, rows);
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            if (TryParseRow(item, out var row))
                rows.Add(row);
        }
    }

    private static bool TryParseRow(JsonElement item, out (DateTime Timestamp, double Value) row)
    {
        row = default;

        if (!TryGetState(item, out var value))
            return false;
        if (!TryGetTimestamp(item, out var timestamp))
            return false;

        row = (timestamp, value);
        return true;
    }

    private static bool TryGetState(JsonElement item, out double value)
    {
        value = 0;
        foreach (var name in new[] { "s", "state" })
        {
            if (!item.TryGetProperty(name, out var el))
                continue;
            if (el.ValueKind == JsonValueKind.Number)
            {
                value = el.GetDouble();
                return true;
            }
            // Same numeric contract as the live path (NetDaemonHaEventSource.TryMap):
            // "unknown"/"unavailable"/text is not a reading.
            if (el.ValueKind == JsonValueKind.String
                && double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
            return false;
        }
        return false;
    }

    private static bool TryGetTimestamp(JsonElement item, out DateTime timestamp)
    {
        timestamp = default;
        foreach (var name in new[] { "lu", "last_updated", "lc", "last_changed" })
        {
            if (!item.TryGetProperty(name, out var el))
                continue;
            if (el.ValueKind == JsonValueKind.Number)
            {
                // minimal_response reports epoch seconds as a float.
                timestamp = DateTimeOffset
                    .FromUnixTimeMilliseconds((long)Math.Round(el.GetDouble() * 1000)).UtcDateTime;
                return true;
            }
            if (el.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(el.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            {
                timestamp = dto.UtcDateTime;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Converts a validated lookback literal to a TimeSpan. Throws ArgumentException on any other
    /// shape — the seam's contract, so a typo in ARGUS_BACKFILL_LOOKBACK cannot silently become a
    /// different window on the HA path than it would be on the Influx path.
    /// </summary>
    internal static TimeSpan ParseLookback(string lookback)
    {
        if (string.IsNullOrEmpty(lookback) || !_lookbackShape.IsMatch(lookback))
            throw new ArgumentException($"Invalid lookback for HA history query: {lookback}", nameof(lookback));

        var quantity = int.Parse(lookback[..^1], CultureInfo.InvariantCulture);
        return lookback[^1] switch
        {
            's' => TimeSpan.FromSeconds(quantity),
            'm' => TimeSpan.FromMinutes(quantity),
            'h' => TimeSpan.FromHours(quantity),
            'd' => TimeSpan.FromDays(quantity),
            'w' => TimeSpan.FromDays(7 * quantity),
            _ => throw new ArgumentException($"Invalid lookback for HA history query: {lookback}", nameof(lookback)),
        };
    }

    private bool TryGetCached(string key, out IReadOnlyList<(DateTime Timestamp, double Value)> rows)
    {
        rows = Array.Empty<(DateTime, double)>();
        if (!_cache.TryGetValue(key, out var entry))
            return false;

        if (_now() - entry.At >= CacheTtl)
        {
            _cache.Remove(key);
            _lru.Remove(key);
            return false;
        }

        _lru.Remove(key);
        _lru.AddLast(key);
        rows = entry.Rows;
        _logger.LogDebug("HA Recorder history cache hit for {Key} ({Rows} row(s))", key, rows.Count);
        return true;
    }

    private void Store(string key, IReadOnlyList<(DateTime Timestamp, double Value)> rows)
    {
        if (_cache.ContainsKey(key))
            _lru.Remove(key);

        _cache[key] = (_now(), rows);
        _lru.AddLast(key);

        while (_lru.Count > CacheCapacity)
        {
            var evicted = _lru.First!.Value;
            _lru.RemoveFirst();
            _cache.Remove(evicted);
        }
    }
}
