namespace Argus.Orchestrator.Ha;

/// <summary>
/// Stream of filtered, numeric HaReading events consumed by the scoring pipeline (Plan 07).
/// Implementations handle HA WebSocket auth, reconnect, entity filtering, and reconnect cooldown.
/// </summary>
public interface IHaEventSource
{
    IAsyncEnumerable<HaReading> ReadAllAsync(CancellationToken ct);
}
