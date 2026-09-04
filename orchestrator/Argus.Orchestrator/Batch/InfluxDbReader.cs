using System.Text.RegularExpressions;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Logging;
using InfluxDB.Client;
using Microsoft.Extensions.Logging;

namespace Argus.Orchestrator.Batch;

/// <summary>
/// Queries InfluxDB for a rolling 24-hour window of sensor readings per entity (BTCH-01).
/// Returns an empty list (never throws) when config is absent or InfluxDB returns no records.
/// </summary>
public sealed class InfluxDbReader : IInfluxDataSource
{
    // T-02-02-02: allowlist guard — reject values that contain double-quote or backslash
    // which would allow Flux string-literal injection. Entity IDs and config field names
    // are operator-controlled (accepted risk), but must not contain these characters.
    private static readonly Regex _safeFluxString =
        new(@"^[^""\\]+$", RegexOptions.Compiled);

    // D-13/T-15-03-02: validates a Flux duration literal (e.g. "30d", "24h") — one or
    // more digits followed by a single unit character (seconds/minutes/hours/days/weeks).
    // Applied IN ADDITION to _safeFluxString so a value like `30 days` (safe per
    // _safeFluxString but not a valid Flux duration) is still rejected.
    private static readonly Regex _safeFluxDuration =
        new(@"^\d+[smhdw]$", RegexOptions.Compiled);

    private readonly IInfluxQueryApi _queryApi;
    private readonly ConnectionSettings _settings;
    private readonly ILogger<InfluxDbReader> _logger;

    /// <inheritdoc/>
    public string SourceName => "InfluxDB";

    /// <summary>
    /// Production constructor: wraps the InfluxDBClient singleton.
    /// DI-resolved via AddSingleton — the client is a singleton, QueryApi obtained per-call.
    /// </summary>
    public InfluxDbReader(InfluxDBClient client, ConnectionSettings settings, ILogger<InfluxDbReader> logger)
        : this(new InfluxQueryApiAdapter(client), settings, logger)
    {
    }

    /// <summary>
    /// Testable constructor: accepts IInfluxQueryApi directly (hand-written fake, no live InfluxDB needed).
    /// </summary>
    public InfluxDbReader(IInfluxQueryApi queryApi, ConnectionSettings settings, ILogger<InfluxDbReader> logger)
    {
        _queryApi = queryApi;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Queries the last 24 hours of sensor readings for the given entity.
    /// Returns empty list if InfluxDB config is absent or no records exist in the window.
    /// Uses Convert.ToDouble for GetValue() to handle both long and double InfluxDB field types (PITFALL 6).
    /// </summary>
    public async Task<IReadOnlyList<(DateTime Timestamp, double Value)>> QueryAsync(
        string entityId, CancellationToken ct)
    {
        // Guard: cannot query without InfluxUrl
        if (string.IsNullOrEmpty(_settings.InfluxUrl))
        {
            _logger.LogWarning(LogEvents.BatchEntityNoData,
                "InfluxUrl not configured — skipping query for {EntityId}", entityId);
            return Array.Empty<(DateTime, double)>();
        }

        // Guard: cannot query without InfluxBucket
        if (string.IsNullOrEmpty(_settings.InfluxBucket))
        {
            _logger.LogWarning(LogEvents.BatchEntityNoData,
                "InfluxBucket not configured — skipping query for {EntityId}", entityId);
            return Array.Empty<(DateTime, double)>();
        }

        // T-02-02-02: validate interpolated values to prevent Flux string-literal injection.
        // Values containing " or \ would terminate the Flux string and inject operators.
        if (!_safeFluxString.IsMatch(entityId))
            throw new ArgumentException($"Unsafe entityId for Flux query: {entityId}", nameof(entityId));
        if (!_safeFluxString.IsMatch(_settings.InfluxBucket))
            throw new ArgumentException($"Unsafe InfluxBucket for Flux query: {_settings.InfluxBucket}");
        if (!string.IsNullOrEmpty(_settings.InfluxMeasurement) && !_safeFluxString.IsMatch(_settings.InfluxMeasurement))
            throw new ArgumentException($"Unsafe InfluxMeasurement for Flux query: {_settings.InfluxMeasurement}");
        if (!string.IsNullOrEmpty(_settings.InfluxValueField) && !_safeFluxString.IsMatch(_settings.InfluxValueField))
            throw new ArgumentException($"Unsafe InfluxValueField for Flux query: {_settings.InfluxValueField}");

        var flux = $"""
            from(bucket: "{_settings.InfluxBucket}")
              |> range(start: -24h)
              |> filter(fn: (r) => r["_measurement"] == "{_settings.InfluxMeasurement}"
                    and r["entity_id"] == "{entityId}"
                    and r["_field"] == "{_settings.InfluxValueField}")
              |> sort(columns: ["_time"])
            """;

        var tables = await _queryApi.QueryAsync(flux, _settings.InfluxOrg, ct);

        var points = tables
            .SelectMany(t => t.Records)
            .Select(r => (
                Timestamp: r.GetTime()!.Value.ToDateTimeUtc(),
                // PITFALL 6: use Convert.ToDouble, not (double)r.GetValue() — HA may write integer fields
                Value: Convert.ToDouble(r.GetValue())))
            .ToList();

        if (points.Count == 0)
        {
            _logger.LogWarning(LogEvents.BatchEntityNoData,
                "No readings in 24h window for {EntityId} — skipping", entityId);
            return Array.Empty<(DateTime, double)>();
        }

        return points;
    }

    /// <summary>
    /// Queries a bounded, chronologically ascending window of history for backfill priming
    /// (D-13/BACKFILL-01). Sibling of <see cref="QueryAsync"/> — that method's hardcoded
    /// 24h range is untouched by this method. Returns empty (never throws) on missing config
    /// or zero records, mirroring QueryAsync's degrade-safe shape (D-15).
    /// </summary>
    /// <param name="entityId">HA entity ID.</param>
    /// <param name="lookback">Flux duration literal (e.g. "30d") — validated, never raw-interpolated
    /// without the duration-shape check.</param>
    /// <param name="limit">Maximum number of rows to return. Must be a positive parsed int —
    /// T-15-03-03/D-13: a caller-supplied limit string is NEVER interpolated raw.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<(DateTime Timestamp, double Value)>> QueryHistoryAsync(
        string entityId, string lookback, int limit, CancellationToken ct)
    {
        // Guard: cannot query without InfluxUrl
        if (string.IsNullOrEmpty(_settings.InfluxUrl))
        {
            _logger.LogWarning(LogEvents.BatchEntityNoData,
                "InfluxUrl not configured — skipping history query for {EntityId}", entityId);
            return Array.Empty<(DateTime, double)>();
        }

        // Guard: cannot query without InfluxBucket
        if (string.IsNullOrEmpty(_settings.InfluxBucket))
        {
            _logger.LogWarning(LogEvents.BatchEntityNoData,
                "InfluxBucket not configured — skipping history query for {EntityId}", entityId);
            return Array.Empty<(DateTime, double)>();
        }

        // T-15-03-01: same injection guards as QueryAsync, applied verbatim.
        if (!_safeFluxString.IsMatch(entityId))
            throw new ArgumentException($"Unsafe entityId for Flux query: {entityId}", nameof(entityId));
        if (!_safeFluxString.IsMatch(_settings.InfluxBucket))
            throw new ArgumentException($"Unsafe InfluxBucket for Flux query: {_settings.InfluxBucket}");
        if (!string.IsNullOrEmpty(_settings.InfluxMeasurement) && !_safeFluxString.IsMatch(_settings.InfluxMeasurement))
            throw new ArgumentException($"Unsafe InfluxMeasurement for Flux query: {_settings.InfluxMeasurement}");
        if (!string.IsNullOrEmpty(_settings.InfluxValueField) && !_safeFluxString.IsMatch(_settings.InfluxValueField))
            throw new ArgumentException($"Unsafe InfluxValueField for Flux query: {_settings.InfluxValueField}");

        // T-15-03-02: lookback must be both injection-safe AND a valid Flux duration shape.
        if (!_safeFluxString.IsMatch(lookback) || !_safeFluxDuration.IsMatch(lookback))
            throw new ArgumentException($"Invalid lookback for Flux query: {lookback}", nameof(lookback));

        // T-15-03-03/D-13: limit is validated BEFORE interpolation, and interpolated only as
        // a formatted integer — never a raw caller-controlled string.
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit must be > 0");

        // Pattern 5: explicit sort(desc) -> limit -> sort(asc) — do not rely on tail()'s
        // implicit ordering guarantee (RESEARCH.md Assumption A3).
        var flux = $"""
            from(bucket: "{_settings.InfluxBucket}")
              |> range(start: -{lookback})
              |> filter(fn: (r) => r["_measurement"] == "{_settings.InfluxMeasurement}"
                    and r["entity_id"] == "{entityId}"
                    and r["_field"] == "{_settings.InfluxValueField}")
              |> sort(columns: ["_time"], desc: true)
              |> limit(n: {limit})
              |> sort(columns: ["_time"], desc: false)
            """;

        var tables = await _queryApi.QueryAsync(flux, _settings.InfluxOrg, ct);

        var points = tables
            .SelectMany(t => t.Records)
            .Select(r => (
                Timestamp: r.GetTime()!.Value.ToDateTimeUtc(),
                // PITFALL 6: use Convert.ToDouble, not (double)r.GetValue() — HA may write integer fields
                Value: Convert.ToDouble(r.GetValue())))
            .ToList();

        if (points.Count == 0)
        {
            _logger.LogWarning(LogEvents.BatchEntityNoData,
                "No history in {Lookback} window for {EntityId} — skipping", lookback, entityId);
            return Array.Empty<(DateTime, double)>();
        }

        return points;
    }
}
