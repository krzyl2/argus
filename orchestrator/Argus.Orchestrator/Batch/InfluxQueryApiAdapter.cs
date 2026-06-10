using InfluxDB.Client;
using InfluxDB.Client.Core.Flux.Domain;

namespace Argus.Orchestrator.Batch;

/// <summary>
/// Production adapter: wraps InfluxDBClient.GetQueryApi() for IInfluxQueryApi.
/// GetQueryApi() is called per-method as recommended by InfluxDB.Client docs.
/// </summary>
internal sealed class InfluxQueryApiAdapter : IInfluxQueryApi
{
    private readonly InfluxDBClient _client;

    public InfluxQueryApiAdapter(InfluxDBClient client)
    {
        _client = client;
    }

    public Task<List<FluxTable>> QueryAsync(string flux, string? org, CancellationToken ct)
        => _client.GetQueryApi().QueryAsync(flux, org, ct);
}
