namespace Argus.Orchestrator.Ha;

/// <summary>
/// A filtered, numeric-valued HA state_changed reading ready for the scoring pipeline.
/// SuppressBinarySensor=true while within the 60s post-reconnect cooldown (D-07) or during warm-up.
/// </summary>
public record HaReading(
    string EntityId,
    double Value,
    DateTimeOffset LastChanged,
    bool SuppressBinarySensor);
