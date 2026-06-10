using Argus.Orchestrator.Config;

namespace Argus.Orchestrator.Detection;

/// <summary>
/// Per-entity aggregate state for the scoring pipeline (Plan 08).
/// Holds hysteresis gate, frozen sensor detector, warm-up tracking,
/// and last-published flag value. Created once per (entity, detector) pair.
/// </summary>
public sealed class EntityRuntimeState
{
    /// <summary>Per-entity hysteresis state machine (D-11).</summary>
    public HysteresisGate Hysteresis { get; }

    /// <summary>Per-entity frozen sensor detector (D-12).</summary>
    public FrozenSensorDetector FrozenDetector { get; }

    /// <summary>
    /// True once this entity has received at least HstParams.Window readings —
    /// mirrors the HST warm-up period on the detector side (PITFALL 7/8).
    /// </summary>
    public bool WarmedUp => _readingCount >= _warmUpWindow;

    /// <summary>Last flag value published to MQTT (for change detection).</summary>
    public bool LastPublishedFlag { get; set; }

    /// <summary>
    /// Tracks whether the binary_sensor flag should be suppressed for the current reading
    /// (post-reconnect cooldown D-07 or warm-up PITFALL 8). Updated in the write loop so
    /// the verdict read loop can use it via a synthetic reading.
    /// </summary>
    public bool SuppressBinarySensor { get; set; }

    private int _readingCount;
    private readonly int _warmUpWindow;

    /// <summary>
    /// Creates per-entity state from resolved HST params.
    /// </summary>
    public EntityRuntimeState(HstParams hstParams)
    {
        Hysteresis = new HysteresisGate(
            hstParams.HighThreshold,
            hstParams.LowThreshold,
            hstParams.MinConsecutive);

        FrozenDetector = new FrozenSensorDetector(
            hstParams.FrozenWindow,
            hstParams.FrozenVarianceThreshold);

        _warmUpWindow = hstParams.Window;
    }

    /// <summary>
    /// Increments the reading counter (called for each reading forwarded to the detector).
    /// </summary>
    public void RecordReading() => _readingCount++;
}
