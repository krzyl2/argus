using InfluxDB.Client.Core.Flux.Domain;

namespace Argus.Orchestrator.Batch;

/// <summary>
/// Abstraction over InfluxDB.Client QueryApi to allow unit testing without a live InfluxDB instance.
/// Production implementation wraps InfluxDBClient.GetQueryApi().QueryAsync.
/// </summary>
public interface IInfluxQueryApi
{
    Task<List<FluxTable>> QueryAsync(string flux, string? org, CancellationToken ct);
}
