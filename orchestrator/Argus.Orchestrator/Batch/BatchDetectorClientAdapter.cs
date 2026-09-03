using Argus.Detector.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
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

    public async Task<GroupScoreResponse> ScoreGroupBatchAsync(GroupScoreRequest request, CancellationToken ct)
    {
        var call = _gateway.DetectorClient.ScoreGroupBatchAsync(request, cancellationToken: ct);
        return await call.ResponseAsync;
    }

    public async Task<FitGroupResponse> FitGroupAsync(FitGroupRequest request, CancellationToken ct)
    {
        var call = _gateway.DetectorClient.FitGroupAsync(request, cancellationToken: ct);
        return await call.ResponseAsync;
    }

    public async Task<WarmupResponse> WarmupAsync(WarmupRequest request, CancellationToken ct)
    {
        var call = _gateway.DetectorClient.WarmupAsync(request, cancellationToken: ct);
        return await call.ResponseAsync;
    }

    /// <summary>
    /// WS6: one sandboxed replay. Never throws on an RPC failure — a detector build that
    /// predates the Simulate RPC answers Unimplemented, and a panel that threw there would
    /// surface as a 500 next to a perfectly healthy scoring path.
    /// </summary>
    public async Task<SimulateResult> SimulateBatchAsync(
        string entityId,
        string detector,
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyList<HistoryPoint> history,
        CancellationToken ct)
    {
        var request = new SimulateRequest
        {
            EntityId = entityId,
            Detector = detector,
            RequestId = Guid.NewGuid().ToString("N"),
        };
        foreach (var kv in parameters) request.Params[kv.Key] = kv.Value;
        foreach (var point in history)
        {
            request.History.Add(new Point
            {
                EntityId = entityId,
                Value = point.Value,
                Timestamp = Timestamp.FromDateTimeOffset(point.Timestamp),
            });
        }

        try
        {
            var call = _gateway.DetectorClient.SimulateAsync(
                request, deadline: DateTime.UtcNow.AddSeconds(30), cancellationToken: ct);
            var response = await call.ResponseAsync;

            return new SimulateResult(
                response.Ok,
                string.IsNullOrEmpty(response.Error) ? null : response.Error,
                response.Scores,
                response.RobustZ,
                (int)response.Window,
                (int)response.WarmedUpFromIndex,
                response.DetectorVersion);
        }
        catch (RpcException ex)
        {
            return new SimulateResult(
                false, ex.Status.Detail, Array.Empty<double>(), Array.Empty<double>(), 0, 0, "");
        }
    }
}
