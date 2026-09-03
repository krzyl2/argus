namespace Argus.Orchestrator.Batch;

/// <summary>
/// Abstraction over InfluxDbReader for batch scheduler testability.
/// Implemented by InfluxDbReader (production) and hand-written fakes in tests.
/// </summary>
public interface IInfluxDataSource
{
    /// <summary>
    /// Operator-facing name of the store behind this seam, used in the priming log line.
    /// F11's acceptance criterion is that the startup log says an entity was primed
    /// <em>from HA Recorder</em>: on a deployment with influx_url empty, "primed 720 points" alone
    /// cannot distinguish a working Recorder backfill from an InfluxDB that was never configured,
    /// which is precisely the failure WS5 exists to make visible. Defaulted so the two production
    /// implementors are the only places that must name themselves, and test fakes stay untouched.
    /// </summary>
    string SourceName => GetType().Name;

    Task<IReadOnlyList<(DateTime Timestamp, double Value)>> QueryAsync(
        string entityId, CancellationToken ct);

    /// <summary>
    /// Queries a bounded, chronologically ascending window of history for backfill
    /// priming (D-13/BACKFILL-01). Sibling of <see cref="QueryAsync"/> — that method's
    /// hardcoded 24h batch behavior is unchanged.
    /// </summary>
    Task<IReadOnlyList<(DateTime Timestamp, double Value)>> QueryHistoryAsync(
        string entityId, string lookback, int limit, CancellationToken ct);
}
