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

    // Phase 6 (GRP-02/GRP-04): group scoring/fit RPCs
    Task<GroupScoreResponse> ScoreGroupBatchAsync(GroupScoreRequest request, CancellationToken ct);
    Task<FitGroupResponse> FitGroupAsync(FitGroupRequest request, CancellationToken ct);

    // Phase 15-03 (BACKFILL-01..04): prime a cold streaming detector from InfluxDB history.
    Task<WarmupResponse> WarmupAsync(WarmupRequest request, CancellationToken ct);

    /// <summary>
    /// WS6: replays a stored history through a SANDBOXED detector instance and returns the
    /// raw per-point scores. The detector side never registers the instance (F14), so this
    /// cannot move the model that is scoring the live stream — which is exactly why it is a
    /// separate RPC rather than a reuse of <see cref="ScoreBatchAsync"/>.
    ///
    /// Takes plain arguments rather than a SimulateRequest so that the three hand-written
    /// fakes in the test project (and any future one) do not have to construct proto
    /// messages just to answer a canned score array.
    /// </summary>
    Task<SimulateResult> SimulateBatchAsync(
        string entityId,
        string detector,
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyList<HistoryPoint> history,
        CancellationToken ct);
}

/// <summary>
/// One historical reading, oldest-first, as the simulator consumes them.
///
/// The IInfluxDataSource seam speaks in <c>(DateTime Timestamp, double Value)</c> tuples;
/// this named record exists because the simulator's whole output is time-weighted
/// (on-time percent, spanHours, alertsPerDay) and a positional tuple gives the reader no
/// way to notice that two call sites disagree about which element is the timestamp.
/// </summary>
public sealed record HistoryPoint(DateTimeOffset Timestamp, double Value);

/// <summary>
/// Detector-side outcome of one simulation.
///
/// <paramref name="Ok"/> is false for every failure — an unknown detector name, an
/// exception in the detector, or an <c>Unimplemented</c> from a detector build that
/// predates the Simulate RPC. None of those may throw: the panel shows the message and
/// live scoring is untouched.
/// </summary>
/// <param name="Scores">One per history point, 1:1 by index.</param>
/// <param name="RobustZ">Same length as Scores for rmad; empty for detectors that compute
/// no deviation (hst scores rarity, F4).</param>
/// <param name="Window">Effective warm-up gate — hst: window, rmad: min_samples.</param>
/// <param name="WarmedUpFromIndex">First scorable index. Scores below it are a structural
/// 0.0 and MUST NOT be fed to the gate: doing so manufactures a release edge that never
/// happened on the sensor.</param>
public sealed record SimulateResult(
    bool Ok,
    string? Error,
    IReadOnlyList<double> Scores,
    IReadOnlyList<double> RobustZ,
    int Window,
    int WarmedUpFromIndex,
    string DetectorVersion);
