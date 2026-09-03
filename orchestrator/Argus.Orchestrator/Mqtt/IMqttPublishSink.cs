namespace Argus.Orchestrator.Mqtt;

/// <summary>
/// Minimal publish surface shared by <see cref="MqttConnection"/> and test doubles.
///
/// Exists purely as an observability seam for the retain flag: MqttConnection is sealed and its
/// PublishAsync is non-virtual, so before this interface no test could assert that the flag topic
/// publishes retained while the score topic does not — and after WS2 that distinction is what
/// keeps HA from reading `unknown` for a flag that has not changed since the last restart.
/// </summary>
internal interface IMqttPublishSink
{
    Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct);
}
