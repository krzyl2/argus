using Argus.Detector.V1;
using Argus.Orchestrator.Detection;

namespace Argus.Orchestrator.Batch;

/// <summary>
/// Wraps DetectorService.DetectorServiceClient (concrete gRPC stub) behind IBatchDetectorClient.
/// Allows BatchSchedulerWorker to accept IBatchDetectorClient in its constructor without
/// a direct reference to the generated gRPC stub type — enabling hand-written fakes in tests.
/// </summary>
public sealed class BatchDetectorClientAdapter : IBatchDetectorClient
{
    private readonly DetectionGateway _gateway;

    public BatchDetectorClientAdapter(DetectionGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    public async Task<ScoreBatchResponse> ScoreBatchAsync(ScoreBatchRequest request, CancellationToken ct)
    {
        var call = _gateway.DetectorClient.ScoreBatchAsync(request, cancellationToken: ct);
        return await call.ResponseAsync;
    }

    public async Task<FitResponse> FitAsync(FitRequest request, CancellationToken ct)
    {
        var call = _gateway.DetectorClient.FitAsync(request, cancellationToken: ct);
        return await call.ResponseAsync;
    }
}
