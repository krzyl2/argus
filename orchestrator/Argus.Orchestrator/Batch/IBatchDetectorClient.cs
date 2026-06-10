using Argus.Detector.V1;

namespace Argus.Orchestrator.Batch;

/// <summary>
/// Abstraction over DetectorService.DetectorServiceClient for batch path testability.
/// Implemented by BatchDetectorClientAdapter (production) and hand-written fakes in tests.
/// Only the two methods used by BatchSchedulerWorker are included (BTCH-02/BTCH-04).
/// </summary>
public interface IBatchDetectorClient
{
    Task<ScoreBatchResponse> ScoreBatchAsync(ScoreBatchRequest request, CancellationToken ct);
    Task<FitResponse> FitAsync(FitRequest request, CancellationToken ct);
}
