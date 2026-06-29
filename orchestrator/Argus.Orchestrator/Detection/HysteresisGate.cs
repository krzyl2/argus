namespace Argus.Orchestrator.Detection;

/// <summary>
/// Per-entity hysteresis state machine (STRM-05, D-11).
/// Requires min_consecutive high readings above high_threshold to flip ON,
/// and min_consecutive low readings below low_threshold to flip OFF.
/// Scores in the dead zone (low &lt;= score &lt;= high) reset both counters and hold state.
/// </summary>
public sealed class HysteresisGate
{
    private readonly double _highThreshold;
    private readonly double _lowThreshold;
    private readonly int _minConsecutive;

    private int _consecutiveHigh;
    private int _consecutiveLow;

    /// <summary>Current anomaly state. Starts false (OFF).</summary>
    public bool IsAnomalous { get; private set; }

    /// <summary>
    /// Initializes with D-11 defaults: high=0.7, low=0.3, min_consecutive=3.
    /// </summary>
    public HysteresisGate(double highThreshold = 0.7, double lowThreshold = 0.3, int minConsecutive = 3)
    {
        _highThreshold = highThreshold;
        _lowThreshold = lowThreshold;
        _minConsecutive = minConsecutive;
    }

    /// <summary>
    /// Applies a new score to the state machine.
    /// Returns the new IsAnomalous value for convenience.
    /// </summary>
    public bool Apply(double score)
    {
        if (score > _highThreshold)
        {
            _consecutiveHigh++;
            _consecutiveLow = 0;

            if (_consecutiveHigh >= _minConsecutive)
                IsAnomalous = true;
        }
        else if (score < _lowThreshold)
        {
            _consecutiveLow++;
            _consecutiveHigh = 0;

            if (_consecutiveLow >= _minConsecutive)
                IsAnomalous = false;
        }
        else
        {
            // Dead zone: reset both counters, hold current state
            _consecutiveHigh = 0;
            _consecutiveLow = 0;
        }

        return IsAnomalous;
    }
}
