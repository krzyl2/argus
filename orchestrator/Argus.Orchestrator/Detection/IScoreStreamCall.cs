using Argus.Detector.V1;

namespace Argus.Orchestrator.Detection;

/// <summary>
/// Abstraction over AsyncDuplexStreamingCall for ScoreStream, enabling unit testing
/// without a live gRPC channel (PITFALL 3 ordering test).
/// </summary>
public interface IScoreStreamCall
{
    Task WriteAsync(Point point, CancellationToken ct);
    Task CompleteAsync();
    IAsyncEnumerable<Verdict> ReadAllVerdictsAsync(CancellationToken ct);
}
