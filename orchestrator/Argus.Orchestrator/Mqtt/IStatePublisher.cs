namespace Argus.Orchestrator.Mqtt;

/// <summary>
/// Testable interface for MQTT state publishing operations.
/// Implemented by StatePublisher (real) and fakes in tests.
/// </summary>
public interface IStatePublisher
{
    Task PublishFlagAsync(string entityId, bool on, CancellationToken ct);
    Task PublishScoreAsync(string entityId, double score, CancellationToken ct);
    Task PublishAvailabilityAsync(string entityId, bool online, CancellationToken ct);
}
