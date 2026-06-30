namespace Argus.Orchestrator.Health;

/// <summary>
/// Singleton liveness signals for composite health evaluation (HEALTH-01).
/// Shared between NetDaemonHaEventSource (writer) and HealthPublisherWorker (reader).
/// Fields are volatile to ensure cross-thread visibility without a lock.
/// </summary>
public sealed class ArgusHealthSignals
{
    /// <summary>
    /// True when the orchestrator has an active HA WebSocket connection.
    /// Set to true on successful ConnectAsync; cleared on connection loss.
    /// </summary>
    public volatile bool HaConnected;
}
