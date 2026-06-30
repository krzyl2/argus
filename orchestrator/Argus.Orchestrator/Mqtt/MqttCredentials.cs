namespace Argus.Orchestrator.Mqtt;

/// <summary>
/// Immutable value record for MQTT broker credentials fetched per-connection-attempt (SUPV-03).
/// Credentials must never be cached across attempts.
/// </summary>
public sealed record MqttCredentials(string? Host, int Port, string? User, string? Password);
