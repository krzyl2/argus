namespace Argus.Orchestrator.Mqtt;

/// <summary>
/// Abstraction for per-attempt MQTT credential retrieval (SUPV-03).
/// Implementations MUST NOT cache credentials between calls — every call
/// must produce a fresh set so re-provisioning the broker survives a reconnect
/// without restarting the orchestrator.
/// </summary>
public interface IMqttCredentialSource
{
    /// <summary>Fetches MQTT credentials for a single connection attempt.</summary>
    Task<MqttCredentials> GetAsync(CancellationToken ct);
}
