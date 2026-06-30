namespace Argus.Orchestrator.Mqtt;

/// <summary>
/// Pure composite-health evaluator (HEALTH-01).
/// ON = problem/unavailable; OFF = healthy.
/// Healthy only when ALL three signals are true:
///   detector gRPC SERVING AND HA connected AND MQTT connected.
/// </summary>
public static class HealthEvaluator
{
    /// <summary>
    /// Returns "OFF" (healthy) when all three signals are true;
    /// returns "ON" (problem) if any signal is false.
    /// Matches HA binary_sensor device_class "problem" semantics.
    /// </summary>
    public static string Evaluate(bool detectorServing, bool haConnected, bool mqttConnected)
        => (detectorServing && haConnected && mqttConnected) ? "OFF" : "ON";
}
