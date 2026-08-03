namespace Argus.Orchestrator.Batch;

/// <summary>
/// Abstraction over InfluxDbReader for batch scheduler testability.
/// Implemented by InfluxDbReader (production) and hand-written fakes in tests.
/// </summary>
public interface IInfluxDataSource
{
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
