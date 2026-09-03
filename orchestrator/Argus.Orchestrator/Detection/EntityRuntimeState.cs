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
    ///
    /// For an rmad entity this holds the EQUIVALENT gate/window view of its rmad params
    /// (thresholds, min_consecutive, frozen pair, baseline window) so the hysteresis gate,
    /// the frozen detector and the backfill row request keep one code path. The rmad-only
    /// keys live on <see cref="RmadParams"/>; <see cref="DetectorName"/> says which is real.
    /// </summary>
    public HstParams HstParams { get; }

    /// <summary>
    /// Detector this entity actually runs — "rmad" (default, D-A) or "hst". Sent on the wire
    /// in Point.params/WarmupRequest.Detector so the Python side scores with the same
    /// algorithm the config names. A hardcoded literal here is exactly the defect that would
    /// let a migrated entities.yaml silently run the old detector (F0).
    /// </summary>
    public string DetectorName { get; }

    /// <summary>
    /// Resolved rmad params, or null for an hst entity. Non-null iff DetectorName == "rmad".
    /// </summary>
    public RmadParams? RmadParams { get; }

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
    /// Measured spacing between this entity's readings (F6-3). Fed from the write loop, which
    /// is the only place a real HaReading timestamp exists — the verdict read loop builds a
    /// synthetic HaReading stamped DateTimeOffset.UtcNow, so observing there would measure the
    /// detector's response time instead of the sensor's cadence.
    /// </summary>
    public ReadingCadence Cadence { get; } = new();

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
        DetectorName = "hst";
        AlertParams = alertParams ?? new AlertParams();
        Alert = alert ?? new AlertPolicy(AlertParams);
        WarmUpWindow = hstParams.Window;
    }

    /// <summary>
    /// Creates per-entity state for an rmad entity (D-A/D-B/D-M).
    ///
    /// The gate and the frozen detector are configured from the SAME numbers as the hst path —
    /// D-C keeps HysteresisGate untouched, because an rmad score is already dimensionless and a
    /// fixed threshold on it is correct per entity.
    ///
    /// D-M: WarmUpWindow is seeded from min_samples (60), NOT from the baseline window (720).
    /// The rolling median/MAD IS the calibration, recomputed every tick, so min_samples is the
    /// gate that actually decides whether a verdict counts — showing "n/720" would tell the
    /// operator to wait ~78 h on a slow sensor for a verdict that already arrived at 60.
    /// </summary>
    public EntityRuntimeState(RmadParams rmadParams, AlertParams? alertParams = null, AlertPolicy? alert = null)
    {
        Hysteresis = new HysteresisGate(
            rmadParams.HighThreshold,
            rmadParams.LowThreshold,
            rmadParams.MinConsecutive);

        FrozenDetector = new FrozenSensorDetector(
            rmadParams.FrozenWindow,
            rmadParams.FrozenVarianceThreshold);

        RmadParams = rmadParams;
        DetectorName = "rmad";
        HstParams = new HstParams
        {
            Window = rmadParams.Window,
            HighThreshold = rmadParams.HighThreshold,
            LowThreshold = rmadParams.LowThreshold,
            MinConsecutive = rmadParams.MinConsecutive,
            FrozenWindow = rmadParams.FrozenWindow,
            FrozenVarianceThreshold = rmadParams.FrozenVarianceThreshold,
        };
        AlertParams = alertParams ?? new AlertParams();
        Alert = alert ?? new AlertPolicy(AlertParams);
        WarmUpWindow = rmadParams.MinSamples;
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
