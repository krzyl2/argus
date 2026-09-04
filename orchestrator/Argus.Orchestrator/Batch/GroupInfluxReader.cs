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
    // T-06-03 (mirrors T-02-02-02): guard — reject values that contain double-quote, backslash,
    // or a line break (CR-02). All three would allow escaping the Flux string literal or
    // injecting an additional Flux pipeline stage across a newline. Member ids and config field
    // names are operator-controlled (accepted risk), but must not contain these characters.
    private static readonly Regex _safeFluxString =
        new(@"^[^""\\\r\n]+$", RegexOptions.Compiled);

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
        // Values containing ", \, or a line break would terminate the Flux string/statement
        // and inject operators (CR-02). WR-04: every/aggFn are the same risk class (operator-
        // controlled Flux fragment) as the fields below, so they go through the same guard.
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
        if (!_safeFluxString.IsMatch(every))
            throw new ArgumentException($"Unsafe 'every' value for Flux query: {every}", nameof(every));
        if (!_safeFluxString.IsMatch(aggFn))
            throw new ArgumentException($"Unsafe 'aggFn' value for Flux query: {aggFn}", nameof(aggFn));

        // HA writes the entity's OBJECT ID into the entity_id tag, so the filter, the pivot
        // column names and the freshness records all speak object ids while every caller
        // (and the returned dictionaries) speak full entity ids. Keep both directions.
        var tagOf = members.ToDictionary(m => m, InfluxFilter.EntityTag, StringComparer.Ordinal);

        // Fail loud rather than silently double-count: two members of one group that share an
        // object id (e.g. sensor.x + binary_sensor.x) collapse onto ONE pivot column, and
        // whichever member won the reverse lookup would receive the other's readings. HA's own
        // writer has the same ambiguity, so there is no honest answer here — skip the cycle.
        var fullByTag = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (full, tag) in tagOf)
        {
            if (fullByTag.TryGetValue(tag, out var clash))
            {
                _logger.LogError(LogEvents.GroupSchedulerError,
                    "Group members {A} and {B} share the InfluxDB entity_id tag '{Tag}' — cannot " +
                    "distinguish their series; skipping group query", clash, full, tag);
                return new GroupAlignedData(Array.Empty<GroupRow>(), new Dictionary<string, DateTime>());
            }
            fullByTag[tag] = full;
        }

        // T-06-04: use contains(value:..., set:[...]) array filter rather than an or-chain —
        // shorter and avoids parser edge cases with very long boolean expressions (RESEARCH Pitfall 4).
        var memberSet = string.Join(", ", members.Select(m => $"\"{tagOf[m]}\""));

        var filterClause = $"""
            {InfluxFilter.MeasurementClause(_settings.InfluxMeasurement)}contains(value: r["entity_id"], set: [{memberSet}])
                    and r["_field"] == "{_settings.InfluxValueField}"
            """;

        // Main query: aggregateWindow+pivot matrix. No fill() — gaps surface as Flux null (GRP-02).
        //
        // group() before pivot() is load-bearing, not tidying. pivot() splits its output by the
        // group key minus rowKey/columnKey, and _measurement/_field/domain are still group-key
        // columns after filter(). Since HA names the measurement after the entity's unit, a
        // mixed-unit group came back as ONE TABLE PER UNIT — solaredge_3_fazy (V+A+W) produced
        // three tables of three columns each, comfoairq (°C+%) two of four. Every row then
        // lacked the other units' members, BuildGroupMatrix's rectangular guard dropped all of
        // them, and the group was handed a set of empty series forever. Ungrouping first
        // collapses them into one wide table keyed only by _time, which is what the caller
        // needs and what a single-unit group was already getting by accident.
        var matrixFlux = $"""
            from(bucket: "{_settings.InfluxBucket}")
              |> range(start: -24h)
              |> filter(fn: (r) => {filterClause})
              |> aggregateWindow(every: {every}, fn: {aggFn}, createEmpty: true)
              |> group()
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
                    m => r.GetValueByKey(tagOf[m]) is null ? (double?)null : Convert.ToDouble(r.GetValueByKey(tagOf[m])))))
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

        // The freshness records carry the object-id tag; the staleness decision upstream is
        // keyed by full entity id. A tag outside fullByTag cannot happen (the query filters on
        // exactly this set) but is dropped rather than guessed at.
        var lastSeenUtc = freshnessTables
            .SelectMany(t => t.Records)
            .Where(r => r.GetValueByKey("entity_id") is not null && r.GetTime() is not null
                        && fullByTag.ContainsKey((string)r.GetValueByKey("entity_id")!))
            .ToDictionary(
                r => fullByTag[(string)r.GetValueByKey("entity_id")!],
                r => r.GetTime()!.Value.ToDateTimeUtc());

        return new GroupAlignedData(rows, lastSeenUtc);
    }
}
