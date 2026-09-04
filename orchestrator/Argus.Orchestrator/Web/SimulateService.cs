using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Argus.Orchestrator.Batch;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Logging;
using Microsoft.Extensions.Logging;

namespace Argus.Orchestrator.Web;

/// <summary>
/// One replay's full answer: the detector's raw output plus the gate reduction plus the
/// series the panel draws.
///
/// Wider than the plan's <c>Task&lt;SimulateSummary&gt;</c>: the endpoint's documented
/// response carries scores, values and timestamps as well, and re-fetching or re-deriving
/// them outside the service would mean a second history read for the same request.
/// </summary>
public sealed record SimulateRunResult(
    bool Ok,
    string? Error,
    SimulateSummary Summary,
    IReadOnlyList<double> Scores,
    IReadOnlyList<double> Values,
    IReadOnlyList<DateTimeOffset> Timestamps,
    int WarmedUpFromIndex,
    int Window);

public interface ISimulateService
{
    Task<SimulateRunResult> RunAsync(
        string entityId,
        string detector,
        IReadOnlyDictionary<string, string> parameters,
        string lookback,
        int maxPoints,
        CancellationToken ct);
}

/// <summary>
/// Fetches an entity's history once, replays it through the sandboxed detector, and reduces
/// the result through the production gate.
///
/// The history cache is the load-bearing part (E2). The panel debounces parameter edits at
/// 400 ms, and every uncached fetch on an influx_url-less install is a fresh WebSocket
/// connect + auth to HA Core (blocker §7 #13). Keying the cache on (entityId, lookback) —
/// deliberately NOT on the detector params — is what turns "drag the threshold slider" from
/// N connections into one: the params change what is replayed, never what is fetched.
/// </summary>
public sealed class SimulateService : ISimulateService
{
    /// <summary>Default lookback (B5) — the window every F13 number is stated in.</summary>
    public const string DefaultLookback = "24h";

    public const int DefaultMaxPoints = 2000;
    public const int MinMaxPoints = 100;

    /// <summary>Hard ceiling on replayed points. Also the CPU brake in front of an endpoint
    /// whose only authentication is the TCP peer check (§7 #16).</summary>
    public const int MaxMaxPoints = 5000;

    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>Contract shared with InfluxDbReader's Flux duration guard — the same literal
    /// must be legal on both history implementations (E3).</summary>
    private static readonly Regex _lookbackShape =
        new(@"^\d+[smhdw]$", RegexOptions.Compiled);

    private readonly IInfluxDataSource _history;
    private readonly IBatchDetectorClient _detector;
    private readonly ILogger<SimulateService> _logger;
    private readonly Func<DateTimeOffset> _now;

    private readonly ConcurrentDictionary<(string EntityId, string Lookback),
        (DateTimeOffset FetchedAt, int Requested, IReadOnlyList<HistoryPoint> Rows)> _cache = new();

    public SimulateService(
        IInfluxDataSource history,
        IBatchDetectorClient detector,
        ILogger<SimulateService> logger,
        Func<DateTimeOffset>? now = null)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public static bool IsValidLookback(string? lookback)
        => !string.IsNullOrWhiteSpace(lookback) && _lookbackShape.IsMatch(lookback);

    public static int ClampMaxPoints(int? maxPoints)
        => Math.Clamp(maxPoints ?? DefaultMaxPoints, MinMaxPoints, MaxMaxPoints);

    /// <summary>
    /// Gate parameters for the replay, resolved from the SUBMITTED params — not from the
    /// saved config. The panel exists to answer "what would these do?", and a value the
    /// operator has typed but not saved is exactly the case that matters.
    ///
    /// The alert params come out of the SAME map (AlertParams shares DetectorConfig.Params with
    /// HstParams/RmadParams), so <c>alert_mode</c> typed in the editor selects the replayed
    /// decision path just as it selects the live one — and an entity that sends no alert keys
    /// at all replays through the adaptive default, which is what it actually runs.
    /// </summary>
    private static (GateParams Gate, AlertParams Alert) ResolveGate(
        string detector, IReadOnlyDictionary<string, string> parameters)
    {
        var dict = new Dictionary<string, string>(parameters, StringComparer.Ordinal);
        var alert = AlertParams.From(dict);

        if (string.Equals(detector, "rmad", StringComparison.OrdinalIgnoreCase))
        {
            var p = RmadParams.From(dict);
            return (new GateParams(
                p.HighThreshold, p.LowThreshold, p.MinConsecutive,
                p.FrozenWindow, p.FrozenVarianceThreshold), alert);
        }

        var hst = HstParams.From(dict);
        return (new GateParams(
            hst.HighThreshold, hst.LowThreshold, hst.MinConsecutive,
            hst.FrozenWindow, hst.FrozenVarianceThreshold), alert);
    }

    public async Task<SimulateRunResult> RunAsync(
        string entityId,
        string detector,
        IReadOnlyDictionary<string, string> parameters,
        string lookback,
        int maxPoints,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var points = Math.Clamp(maxPoints, MinMaxPoints, MaxMaxPoints);

        var history = await GetHistoryAsync(entityId, lookback, points, ct).ConfigureAwait(false);

        if (history.Count == 0)
        {
            return Empty("no history for this entity in the requested window");
        }

        var sim = await _detector
            .SimulateBatchAsync(entityId, detector, parameters, history, ct)
            .ConfigureAwait(false);

        if (!sim.Ok)
        {
            return Empty(sim.Error ?? "detector returned no result");
        }

        var (gate, alert) = ResolveGate(detector, parameters);
        var summary = ReplaySimulator.Run(history, sim, gate, alert);

        // B8 — the ONLY source for simulator response time. The verdict-latency field
        // (latency_ms, ScoreStreamPipeline) measures a different quantity and must not be
        // mixed into this measurement.
        _logger.LogInformation(LogEvents.SimulateCompleted,
            "Simulate {EntityId} detector={Detector} points={Points} episodes={Episodes} " +
            "alertsPerDay={AlertsPerDay} durationMs={DurationMs}",
            entityId, detector, history.Count, summary.Episodes,
            summary.AlertsPerDay, sw.Elapsed.TotalMilliseconds);

        return new SimulateRunResult(
            true, null, summary,
            sim.Scores,
            history.Select(h => h.Value).ToList(),
            history.Select(h => h.Timestamp).ToList(),
            sim.WarmedUpFromIndex,
            sim.Window);

        static SimulateRunResult Empty(string error) => new(
            false, error,
            new SimulateSummary(0, 0.0, 0.0, 0.0, 0, 0, default),
            Array.Empty<double>(), Array.Empty<double>(), Array.Empty<DateTimeOffset>(), 0, 0);
    }

    private async Task<IReadOnlyList<HistoryPoint>> GetHistoryAsync(
        string entityId, string lookback, int maxPoints, CancellationToken ct)
    {
        var key = (entityId, lookback);
        var now = _now();

        if (_cache.TryGetValue(key, out var entry))
        {
            // Reusable while inside the TTL AND fetched with at least as wide a limit. The
            // limit comparison is against what was REQUESTED, not against how many rows came
            // back: a sensor with 300 rows in the window answers a 2000-point request with
            // 300 rows, and comparing counts would make every such entity permanently
            // uncacheable — which is exactly the slow-sensor case the cache exists for.
            if (now - entry.FetchedAt < CacheTtl && entry.Requested >= maxPoints)
                return Trim(entry.Rows, maxPoints);

            // Expired, or too narrow for a widened maxPoints: drop it rather than leave a
            // stale row set behind a key that will never be read again.
            _cache.TryRemove(key, out _);
        }

        var rows = await _history.QueryHistoryAsync(entityId, lookback, maxPoints, ct)
            .ConfigureAwait(false);

        var mapped = rows
            .Select(r => new HistoryPoint(ToOffset(r.Timestamp), r.Value))
            .ToList();

        _cache[key] = (now, maxPoints, mapped);

        // The seam contract says "newest `limit` rows, ascending", but a fake or a future
        // implementation returning more must not silently blow past the CPU ceiling.
        return Trim(mapped, maxPoints);
    }

    private static IReadOnlyList<HistoryPoint> Trim(IReadOnlyList<HistoryPoint> rows, int maxPoints)
        => rows.Count <= maxPoints ? rows : rows.Skip(rows.Count - maxPoints).ToList();

    private static DateTimeOffset ToOffset(DateTime timestamp)
        => timestamp.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc))
            : new DateTimeOffset(timestamp.ToUniversalTime());
}
