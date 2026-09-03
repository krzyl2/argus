using Argus.Orchestrator.Config;

namespace Argus.Orchestrator.Detection;

/// <summary>
/// Per-entity aggregate state for the scoring pipeline (Plan 08).
/// Holds hysteresis gate, frozen sensor detector, warm-up tracking,
/// and last-published flag value. Created once per (entity, detector) pair.
///
/// Warm-up ownership (D-01, Phase 15-02/WARM-01): the detector is the single
/// source of truth for warm-up. WarmedUp/ReadingCount/WarmUpWindow are set
/// exclusively by ApplyVerdictWarmup from the Verdict's warmed_up/n_seen/window
/// fields — this class no longer counts readings itself. A second independent
/// counter here (alongside the detector's own n_seen) is precisely the defect
/// this phase fixes (D2); do not reintroduce a local fallback counter.
///
/// D-11: HysteresisGate state (_consecutiveHigh/Low, IsAnomalous) is
/// deliberately NOT persisted across restarts. It derives from scores, not
/// raw readings, so InfluxDB backfill (Phase 15-03) cannot rebuild it, and
/// persisting it would require a new .NET-side state-file layer for a benefit
/// bounded by MinConsecutive (at most a few readings of post-restart flag
/// latency). Out of scope by design — see 15-CONTEXT.md D-11.
/// </summary>
public sealed class EntityRuntimeState
{
    /// <summary>Per-entity hysteresis state machine (D-11 — not persisted, see class remarks).</summary>
    public HysteresisGate Hysteresis { get; }

    /// <summary>Per-entity frozen sensor detector (D-12).</summary>
    public FrozenSensorDetector FrozenDetector { get; }

    /// <summary>
    /// Resolved HST params this entity was configured with. Threaded through so
    /// ScoreStreamPipeline.ToPoint can populate Point.params (WARM-02) without
    /// re-parsing config in the write loop.
    /// </summary>
    public HstParams HstParams { get; }

    /// <summary>
    /// True once the detector reports warmed_up on a Verdict (D-01) — set by
    /// ApplyVerdictWarmup, never self-computed from a local reading count.
    /// </summary>
    public bool WarmedUp { get; private set; }

    /// <summary>Detector-reported reading count (Verdict.n_seen) — warm-up progress numerator.</summary>
    public int ReadingCount { get; private set; }

    /// <summary>
    /// The window the detector is actually using (Verdict.window). Seeded from the
    /// configured HstParams.Window at construction so GET /api/sensors can render a
    /// sensible x/N before the first verdict arrives; overwritten by ApplyVerdictWarmup
    /// once a verdict reports a positive window.
    /// </summary>
    public int WarmUpWindow { get; private set; }

    /// <summary>Resolved alert-layer params for this entity (WS2/D-C, D-D).</summary>
    public AlertParams AlertParams { get; }

    /// <summary>
    /// Per-entity event layer used on the adaptive path (WS2). Survives config rebuilds when
    /// supplied by AlertStateStore; owns LastPublishedFlag, so an unchanged flag is never
    /// republished (F8).
    /// </summary>
    public AlertPolicy Alert { get; }

    /// <summary>Last raw reading value seen by the write loop (WS2 raw evidence channel).</summary>
    public double LastValue { get; set; }

    /// <summary>
    /// FrozenSensorDetector verdict for the latest reading, latched in the write loop so the
    /// verdict read loop can feed it into the alert layer as evidence instead of the write loop
    /// forcing the flag ON behind the gate's back.
    /// </summary>
    public bool FrozenNow { get; set; }

    /// <summary>
    /// Tracks whether the binary_sensor flag should be suppressed for the current reading
    /// (post-reconnect cooldown D-07 or warm-up PITFALL 8). Updated in the write loop so
    /// the verdict read loop can use it via a synthetic reading.
    /// </summary>
    public bool SuppressBinarySensor { get; set; }

    /// <summary>
    /// Creates per-entity state from resolved HST params.
    /// <paramref name="alertParams"/> and <paramref name="alert"/> are optional so the 37 existing
    /// construction sites keep compiling; production passes the store-owned policy so calibration
    /// survives a config reload.
    /// </summary>
    public EntityRuntimeState(HstParams hstParams, AlertParams? alertParams = null, AlertPolicy? alert = null)
    {
        Hysteresis = new HysteresisGate(
            hstParams.HighThreshold,
            hstParams.LowThreshold,
            hstParams.MinConsecutive);

        FrozenDetector = new FrozenSensorDetector(
            hstParams.FrozenWindow,
            hstParams.FrozenVarianceThreshold);

        HstParams = hstParams;
        AlertParams = alertParams ?? new AlertParams();
        Alert = alert ?? new AlertPolicy(AlertParams);
        WarmUpWindow = hstParams.Window;
    }

    /// <summary>
    /// Applies the detector's own warm-up numbers from a Verdict (D-01/WARM-01).
    /// A non-positive <paramref name="window"/> — the tuple a detector returns for an
    /// entity it has no entry for yet — is ignored so the UI never renders a zero
    /// denominator; WarmUpWindow keeps its constructor-seeded value in that case.
    /// </summary>
    public void ApplyVerdictWarmup(bool warmedUp, int nSeen, int window)
    {
        WarmedUp = warmedUp;
        ReadingCount = nSeen;
        if (window > 0)
            WarmUpWindow = window;
    }
}
