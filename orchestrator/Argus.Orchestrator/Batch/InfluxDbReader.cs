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

    private readonly IInfluxQueryApi _queryApi;
    private readonly ConnectionSettings _settings;
    private readonly ILogger<InfluxDbReader> _logger;

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
}
