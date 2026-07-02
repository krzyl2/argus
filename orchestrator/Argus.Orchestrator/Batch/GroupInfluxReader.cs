using System.Text.RegularExpressions;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Logging;
using InfluxDB.Client;
using Microsoft.Extensions.Logging;

namespace Argus.Orchestrator.Batch;

/// <summary>
/// Queries InfluxDB for a group's time-aligned matrix of member readings (GRP-02).
/// Issues one aggregateWindow+pivot query (no fill() — gaps surface as null cells) plus
/// one companion last()-per-member freshness query for the wall-clock staleness_cap decision.
/// Returns an empty result (never throws) when config is absent, mirroring InfluxDbReader.
/// The staleness_cap exclusion policy itself is NOT applied here — this reader only surfaces
/// null cells and LastSeenUtc; the caller (Plan 06-04) decides peer-drop-member vs joint-skip-group.
/// </summary>
public sealed class GroupInfluxReader : IGroupInfluxDataSource
{
    // T-06-03 (mirrors T-02-02-02): allowlist guard — reject values that contain double-quote
    // or backslash which would allow Flux string-literal injection. Member ids and config field
    // names are operator-controlled (accepted risk), but must not contain these characters.
    private static readonly Regex _safeFluxString =
        new(@"^[^""\\]+$", RegexOptions.Compiled);

    private readonly IInfluxQueryApi _queryApi;
    private readonly ConnectionSettings _settings;
    private readonly ILogger<GroupInfluxReader> _logger;

    /// <summary>
    /// Production constructor: wraps the InfluxDBClient singleton.
    /// </summary>
    public GroupInfluxReader(InfluxDBClient client, ConnectionSettings settings, ILogger<GroupInfluxReader> logger)
        : this(new InfluxQueryApiAdapter(client), settings, logger)
    {
    }

    /// <summary>
    /// Testable constructor: accepts IInfluxQueryApi directly (hand-written fake, no live InfluxDB needed).
    /// </summary>
    public GroupInfluxReader(IInfluxQueryApi queryApi, ConnectionSettings settings, ILogger<GroupInfluxReader> logger)
    {
        _queryApi = queryApi;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Queries the last 24 hours of readings for all group members, aligned onto a common
    /// grid via aggregateWindow+pivot (no fill()), plus each member's last-seen UTC timestamp
    /// via a companion last() freshness query. Returns empty result if InfluxDB config is absent.
    /// </summary>
    public async Task<GroupAlignedData> QueryGroupAsync(
        IReadOnlyList<string> members,
        string every,
        string aggFn,
        TimeSpan stalenessCap,
        CancellationToken ct)
    {
        // Guard: cannot query without InfluxUrl
        if (string.IsNullOrEmpty(_settings.InfluxUrl))
        {
            _logger.LogWarning(LogEvents.BatchEntityNoData,
                "InfluxUrl not configured — skipping group query for {MemberCount} members", members.Count);
            return new GroupAlignedData(Array.Empty<GroupRow>(), new Dictionary<string, DateTime>());
        }

        // Guard: cannot query without InfluxBucket
        if (string.IsNullOrEmpty(_settings.InfluxBucket))
        {
            _logger.LogWarning(LogEvents.BatchEntityNoData,
                "InfluxBucket not configured — skipping group query for {MemberCount} members", members.Count);
            return new GroupAlignedData(Array.Empty<GroupRow>(), new Dictionary<string, DateTime>());
        }

        // T-06-03: validate every interpolated value to prevent Flux string-literal injection.
        // Values containing " or \ would terminate the Flux string and inject operators.
        foreach (var memberId in members)
        {
            if (!_safeFluxString.IsMatch(memberId))
                throw new ArgumentException($"Unsafe member id for Flux query: {memberId}", nameof(members));
        }
        if (!_safeFluxString.IsMatch(_settings.InfluxBucket))
            throw new ArgumentException($"Unsafe InfluxBucket for Flux query: {_settings.InfluxBucket}");
        if (!string.IsNullOrEmpty(_settings.InfluxMeasurement) && !_safeFluxString.IsMatch(_settings.InfluxMeasurement))
            throw new ArgumentException($"Unsafe InfluxMeasurement for Flux query: {_settings.InfluxMeasurement}");
        if (!string.IsNullOrEmpty(_settings.InfluxValueField) && !_safeFluxString.IsMatch(_settings.InfluxValueField))
            throw new ArgumentException($"Unsafe InfluxValueField for Flux query: {_settings.InfluxValueField}");

        // T-06-04: use contains(value:..., set:[...]) array filter rather than an or-chain —
        // shorter and avoids parser edge cases with very long boolean expressions (RESEARCH Pitfall 4).
        var memberSet = string.Join(", ", members.Select(m => $"\"{m}\""));

        var filterClause = $"""
            r["_measurement"] == "{_settings.InfluxMeasurement}"
                    and contains(value: r["entity_id"], set: [{memberSet}])
                    and r["_field"] == "{_settings.InfluxValueField}"
            """;

        // Main query: aggregateWindow+pivot matrix. No fill() — gaps surface as Flux null (GRP-02).
        var matrixFlux = $"""
            from(bucket: "{_settings.InfluxBucket}")
              |> range(start: -24h)
              |> filter(fn: (r) => {filterClause})
              |> aggregateWindow(every: {every}, fn: {aggFn}, createEmpty: true)
              |> pivot(rowKey: ["_time"], columnKey: ["entity_id"], valueColumn: "_value")
              |> sort(columns: ["_time"])
            """;

        var matrixTables = await _queryApi.QueryAsync(matrixFlux, _settings.InfluxOrg, ct);

        var rows = matrixTables
            .SelectMany(t => t.Records)
            .Select(r => new GroupRow(
                r.GetTime()!.Value.ToDateTimeUtc(),
                members.ToDictionary(
                    m => m,
                    m => r.GetValueByKey(m) is null ? (double?)null : Convert.ToDouble(r.GetValueByKey(m)))))
            .ToList();

        // Companion freshness query: last()-per-member most-recent raw timestamp, for the
        // caller's wall-clock staleness_cap decision (GRP-02). Never fill()'d, never used
        // to fabricate a value — only a timestamp used for the exclusion decision.
        var freshnessFlux = $"""
            from(bucket: "{_settings.InfluxBucket}")
              |> range(start: -24h)
              |> filter(fn: (r) => {filterClause})
              |> group(columns: ["entity_id"])
              |> last()
            """;

        var freshnessTables = await _queryApi.QueryAsync(freshnessFlux, _settings.InfluxOrg, ct);

        var lastSeenUtc = freshnessTables
            .SelectMany(t => t.Records)
            .Where(r => r.GetValueByKey("entity_id") is not null && r.GetTime() is not null)
            .ToDictionary(
                r => (string)r.GetValueByKey("entity_id")!,
                r => r.GetTime()!.Value.ToDateTimeUtc());

        return new GroupAlignedData(rows, lastSeenUtc);
    }
}
