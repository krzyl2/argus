namespace Argus.Orchestrator.Ha;

/// <summary>
/// Tracks post-reconnect binary_sensor suppression window (D-07).
/// After MarkReconnect(now), IsSuppressed returns true for 60 seconds.
/// Clock is injected via method parameter for deterministic testing — no Thread.Sleep needed.
/// </summary>
public class ReconnectCooldown
{
    /// <summary>Duration (seconds) for which binary_sensor publication is suppressed after reconnect (D-07).</summary>
    public const int SuppressionWindowSeconds = 60;

    private DateTimeOffset? _lastReconnect;

    /// <summary>Records a reconnect event at the given timestamp.</summary>
    public void MarkReconnect(DateTimeOffset now)
    {
        _lastReconnect = now;
    }

    /// <summary>
    /// Returns true if the given timestamp falls within the suppression window
    /// following the most recent call to MarkReconnect.
    /// Returns false if no reconnect has been recorded.
    /// </summary>
    public bool IsSuppressed(DateTimeOffset now)
    {
        if (_lastReconnect is null)
            return false;

        return now - _lastReconnect.Value < TimeSpan.FromSeconds(SuppressionWindowSeconds);
    }
}
