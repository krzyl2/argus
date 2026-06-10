namespace Argus.Orchestrator.Batch;

/// <summary>
/// Abstraction over InfluxDbReader for batch scheduler testability.
/// Implemented by InfluxDbReader (production) and hand-written fakes in tests.
/// </summary>
public interface IInfluxDataSource
{
    Task<IReadOnlyList<(DateTime Timestamp, double Value)>> QueryAsync(
        string entityId, CancellationToken ct);
}
