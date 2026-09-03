using Argus.Orchestrator.Config;
using Argus.Orchestrator.Detection;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for the WS2 alert layer.
///
/// These tests encode WHY the layer exists, using the field measurements that motivated it as
/// regression fixtures:
///   F1 — five binary_sensors ON continuously for more than 24 h, on-time 100/100/99/91/25 %.
///   F2 — 24 h score minima of 0.480 / 0.830 / 0.562 / 0.492 / 0.497 against a release
///        threshold of 0.3: once lit, a flag could not go out by arithmetic.
///   F3 — share of samples at or above the fire threshold: memory 100 %, load 80 %.
///   F4 — sensor.zamrazarkapiwnica_power is a five-level quantized series (101×10, 103×41,
///        105×148, 107×230, 109×113), i.e. entirely normal but rare-valued.
///   F6 — one global threshold cannot be right on five differently-scaled score distributions.
///   F8 — the flag was republished on every verdict.
/// A test here that cannot fail when the gating rule changes is worthless; each one names the
/// measurement it protects.
/// </summary>
public class AlertPolicyTests
{
    // ─── Fixtures and drivers ────────────────────────────────────────────────

    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow;

    private static DateTimeOffset At(int tick, double secondsPerTick = 60.0)
        => T0.AddSeconds(tick * secondsPerTick);

    /// <summary>
    /// Params tuned for fast deterministic tests. Every knob that would only add wall-clock
    /// time to a unit test is neutralised; the gating rules under test are left at their
    /// production shape (mid-rank, min_consecutive, evidence composition).
    /// </summary>
    private static AlertParams FastParams(
        string evidenceMode = "score_only",
        int rankWindow = 200,
        int alertMinSamples = 100,
        int minConsecutive = 3,
        int minDurationSec = 0,
        int refractorySec = 600,
        int maxEventsPerHour = 4,
        int maxEventDurationSec = 21600,
        int stormHoldSec = 3600,
        double qFire = 0.99,
        double qClear = 0.80,
        double zFire = 5.0,
        double zClear = 3.0,
        int rawWindow = 720)
        => new()
        {
            EvidenceMode = evidenceMode,
            RankWindow = rankWindow,
            AlertMinSamples = alertMinSamples,
            MinConsecutive = minConsecutive,
            MinDurationSec = minDurationSec,
            RefractorySec = refractorySec,
            MaxEventsPerHour = maxEventsPerHour,
            MaxEventDurationSec = maxEventDurationSec,
            StormHoldSec = stormHoldSec,
            QFire = qFire,
            QClear = qClear,
            ZFire = zFire,
            ZClear = zClear,
            RawWindow = rawWindow,
        };

    /// <summary>
    /// The measured 24 h score band of sensor.memory_use_percent: 5653 samples, none below
    /// 0.830, i.e. 100 % of samples above the old fire threshold of 0.7 (F3/F6), saturating at
    /// 1.00. HST scores rarity, so this band is broad tick-to-tick scatter riding on a slow
    /// daily drift — not a smooth curve; a flat synthetic band would make the test pass for the
    /// wrong reason, and a band without the 1.00 ceiling would drop the measured saturation.
    /// </summary>
    private static double[] MemoryDriftBand()
    {
        var rnd = new Random(42);
        var band = new double[5653];
        for (int i = 0; i < band.Length; i++)
        {
            double level = 0.915 + 0.02 * Math.Sin(2 * Math.PI * i / band.Length);
            band[i] = Math.Clamp(level + (rnd.NextDouble() - 0.5) * 0.17, 0.830, 1.0);
        }
        return band;
    }

    /// <summary>
    /// The measured value histogram of sensor.zamrazarkapiwnica_power (F4): five levels, modal
    /// value 107 W. Shuffled with a fixed seed so the series is realistic but reproducible.
    /// </summary>
    private static double[] ZamrazarkaLevels()
    {
        var values = new List<double>();
        void Add(double v, int n) { for (int i = 0; i < n; i++) values.Add(v); }
        Add(101, 10); Add(103, 41); Add(105, 148); Add(107, 230); Add(109, 113);

        var rnd = new Random(42);
        for (int i = values.Count - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
        return values.ToArray();
    }

    /// <summary>
    /// sensor.lodowkababcia_power over 24 h: 1546 readings, mostly 0 W, with two compressor
    /// runs of ~90 readings at 984 W. This is the one sensor in the whole measured set that
    /// carries real events (F3, 83 % episode precision), so it is the one series the layer is
    /// not allowed to go quiet on.
    /// </summary>
    private static double[] LodowkaCompressorCycle()
    {
        var series = new double[1546];
        for (int i = 300; i < 390; i++) series[i] = 984.0;
        for (int i = 1300; i < 1390; i++) series[i] = 984.0;
        return series;
    }

    // ─── F2: a flag must be able to go out ───────────────────────────────────

    [Fact]
    public void RankGate_ScoreStreamNeverBelow048_StillReleasesWithinOneWindow()
    {
        // F2: sensor.load_5m's 24 h score minimum was 0.480 against low_threshold 0.3. The old
        // gate released on `score < 0.3`, so a lit flag could not go out — ever. The rank gate
        // compares a score with the entity's own recent scores, so release depends on the
        // score returning to its own normal band, not on it crossing a global constant.
        var policy = new AlertPolicy(FastParams());
        var rnd = new Random(7);
        double minScore = double.MaxValue;
        int tick = 0;

        double Baseline() => 0.480 + rnd.NextDouble() * 0.14; // load_5m band: [0.480, 0.620]

        for (int i = 0; i < 200; i++)
        {
            double s = Baseline();
            minScore = Math.Min(minScore, s);
            policy.OnVerdict(s, warmedUp: true, suppressed: false, frozen: false, At(tick++));
        }

        // Excursion far above the band's own history, but still nowhere near 0.3 on the way out.
        bool fired = false;
        for (int i = 0; i < 5; i++)
        {
            var d = policy.OnVerdict(0.99, true, false, false, At(tick++));
            fired |= d.FlagOn;
        }
        Assert.True(fired, "An excursion above the entity's own band must raise the flag");

        int releasedAtTick = -1;
        for (int i = 0; i < 200 && releasedAtTick < 0; i++)
        {
            double s = Baseline();
            minScore = Math.Min(minScore, s);
            var d = policy.OnVerdict(s, true, false, false, At(tick++));
            if (!d.FlagOn) releasedAtTick = i;
        }

        Assert.True(minScore >= 0.480,
            "Fixture must reproduce F2: no score in this stream may drop below the measured 0.480 minimum");
        Assert.InRange(releasedAtTick, 0, 199);
    }

    // ─── F3/F6: an always-high band is not an alarm ──────────────────────────

    [Fact]
    public void RankGate_MemoryBand_100PercentAbove07_ProducesZeroEvents()
    {
        // F3/F6: sensor.memory_use_percent had 100.0 % of its samples at or above the global
        // fire threshold of 0.7 and a 24 h minimum of 0.830 — the flag was ON for 100 % of the
        // day at a measured precision of 1.2 %. Nothing in this band is an event, and a gate
        // that is relative to the entity's own distribution must say so.
        var policy = new AlertPolicy(FastParams(rankWindow: 720, alertMinSamples: 240));
        var band = MemoryDriftBand();

        int events = 0, onTicks = 0;
        for (int i = 0; i < band.Length; i++)
        {
            var d = policy.OnVerdict(band[i], true, false, false, At(i, secondsPerTick: 15.3));
            if (d.EventStarted) events++;
            if (d.FlagOn) onTicks++;
        }

        Assert.True(band.Min() >= 0.830, "Fixture must reproduce F3: the whole band sits above 0.830");
        Assert.Equal(0, events);
        Assert.Equal(0, onTicks);
    }

    // ─── F1: a raised flag cannot own the day ────────────────────────────────

    [Fact]
    public void SustainedExcursion_SelfClears_OnTimeUnderTwentyFivePercent()
    {
        // F1: the measured on-times were 100 / 100 / 99 / 91 / 25 %. Under a rank gate a level
        // that persists stops being rare — the window fills with it and its own rank collapses
        // below q_clear. A permanently-high input therefore cannot produce a permanently-high
        // flag, whatever the detector believes.
        var policy = new AlertPolicy(FastParams());
        int tick = 0, onTicks = 0, total = 0;

        void Feed(double s)
        {
            var d = policy.OnVerdict(s, true, false, false, At(tick++));
            total++;
            if (d.FlagOn) onTicks++;
        }

        var rnd = new Random(11);
        for (int i = 0; i < 200; i++) Feed(0.50 + rnd.NextDouble() * 0.05);
        for (int i = 0; i < 400; i++) Feed(0.95);              // sustained excursion
        for (int i = 0; i < 200; i++) Feed(0.50 + rnd.NextDouble() * 0.05);

        double onTime = (double)onTicks / total;
        Assert.True(onTime < 0.25,
            $"Sustained excursion must not hold the flag: on-time {onTime:P1} (F1 measured 91–100 %)");
        Assert.True(onTime > 0.0, "…but it must still raise the flag at the onset");
    }

    // ─── F4: rare is not anomalous ───────────────────────────────────────────

    [Theory]
    [InlineData("any")]
    [InlineData("both")]
    [InlineData("score_only")]
    [InlineData("raw_only")]
    public void RawZ_ZamrazarkaLevelHistogram_NeverExceedsFireThreshold(string evidenceMode)
    {
        // F4 is the measurement that killed the rarity-based scorer: on this five-level series
        // the rare-but-normal 101 W scored 0.997 while the modal 107 W scored 0.560. A robust-z
        // channel measures deviation instead: 101 W is 6 W from the median against a MAD-derived
        // scale of ~2.97, i.e. about 2σ — normal, in every evidence mode.
        var levels = ZamrazarkaLevels();
        var policy = new AlertPolicy(FastParams(evidenceMode: evidenceMode, alertMinSamples: 100));

        // Prime the raw window from history first, exactly as backfill does before a stream
        // opens — a z-score taken against a half-empty window measures the window, not the value.
        foreach (var v in levels) policy.SeedValue(v);

        double maxZ = 0.0;
        int events = 0;
        for (int i = 0; i < levels.Length; i++)
        {
            policy.ObserveValue(levels[i]);
            maxZ = Math.Max(maxZ, policy.LastRawZ);
            // Flat score: this test isolates the raw channel, so the score channel must
            // contribute nothing (a constant always sits at mid-rank 0.5).
            var d = policy.OnVerdict(0.6, true, false, false, At(i, secondsPerTick: 384.0));
            if (d.EventStarted) events++;
        }

        Assert.True(maxZ <= 2.1, $"Max robust-z on the F4 histogram was {maxZ:F3}, expected ≤ 2.1");
        Assert.Equal(0, events);
    }

    // ─── F3: the one sensor with real events must keep them ──────────────────

    [Fact]
    public void RawZ_LodowkaCompressorCycle_FiresExactlyTwice()
    {
        // F3: sensor.lodowkababcia_power is the only sensor in the measured set whose alarms
        // were mostly real (83 % episode precision) — two compressor runs a day between 0 W and
        // 984 W. Reducing the design to a rank-on-score gate would silence this sensor outright
        // (its score is flat), so this test fails the moment the raw channel is dropped.
        var series = LodowkaCompressorCycle();
        var policy = new AlertPolicy(FastParams(evidenceMode: "any", alertMinSamples: 100));

        int events = 0, onTicks = 0;
        for (int i = 0; i < series.Length; i++)
        {
            policy.ObserveValue(series[i]);
            var d = policy.OnVerdict(0.5, true, false, false, At(i, secondsPerTick: 391.0));
            if (d.EventStarted) events++;
            if (d.FlagOn) onTicks++;
        }

        Assert.Equal(2, events);
        double onTime = (double)onTicks / series.Length;
        Assert.InRange(onTime, 0.02, 0.15);
    }

    // ─── Calibration floor ───────────────────────────────────────────────────

    [Fact]
    public void Uncalibrated_BelowAlertMinSamples_NeverFires()
    {
        // Two independent floors, both required. alert_min_samples is the operator-facing one;
        // the hard 50-sample floor on the rank window is arithmetic — with mid-ranks the largest
        // attainable rank is 1 − 0.5/Count, so q_fire = 0.99 is unreachable below 50 samples and
        // an entity configured under it would look calm rather than uncalibrated.
        var policy = new AlertPolicy(FastParams(alertMinSamples: 100));
        for (int i = 0; i < 99; i++)
        {
            var d = policy.OnVerdict(i, true, false, false, At(i)); // strictly increasing → rank 1.0
            Assert.False(d.FlagOn);
            Assert.False(d.EventStarted);
        }
        Assert.False(policy.Calibrated);

        var lowTarget = new AlertPolicy(FastParams(alertMinSamples: 10));
        for (int i = 0; i < 49; i++)
            Assert.False(lowTarget.OnVerdict(i, true, false, false, At(i)).FlagOn);
        Assert.False(lowTarget.Calibrated);
    }

    // ─── Watchdog / storm ────────────────────────────────────────────────────

    [Fact]
    public void FirstEventEverInsideRefractoryOfCalibration_DoesNotTripWatchdog()
    {
        // Two coupled rules, both invisible until they break: an event is closed only when one
        // is actually running (otherwise every calibration tick stamps the last-ended time), and
        // the very first event ever is started regardless of the refractory window (otherwise
        // _eventStartedAt stays at MinValue and the watchdog force-closes the first alarm after
        // every process start — a false storm plus an hour of blindness on each restart).
        var policy = new AlertPolicy(FastParams(refractorySec: 3600, maxEventDurationSec: 21600));
        int tick = 0;
        for (int i = 0; i < 150; i++)
            policy.OnVerdict(0.5, true, false, false, At(tick++));

        AlertDecision? onset = null;
        for (int i = 0; i < 3; i++)
            onset = policy.OnVerdict(1.0 + i, true, false, false, At(tick++));

        Assert.True(onset!.EventStarted, "The first event ever must start even inside the refractory window");
        Assert.False(onset.Storm);

        for (int i = 0; i < 5; i++)
        {
            var d = policy.OnVerdict(10.0 + i, true, false, false, At(tick++));
            Assert.False(d.Storm, "A freshly started event must not be force-closed by the watchdog");
            Assert.True(d.FlagOn);
        }
    }

    [Fact]
    public void MaxEventDuration_EvidenceHeldTrueForever_ForceClosesAndRaisesStorm()
    {
        // F1's only backstop that does not depend on the scorer being right: no flag stays ON
        // past max_event_duration_sec, whatever the evidence says. Rule 12 — the suppression is
        // reported (storm), never silent.
        var policy = new AlertPolicy(FastParams(maxEventDurationSec: 600, stormHoldSec: 3600));
        int tick = 0;
        for (int i = 0; i < 150; i++)
            policy.OnVerdict(0.5, true, false, false, At(tick++));

        for (int i = 0; i < 3; i++)
            policy.OnVerdict(1.0 + i, true, false, false, At(tick++));

        AlertDecision? closing = null;
        for (int i = 0; i < 30 && closing is null; i++)
        {
            var d = policy.OnVerdict(100.0 + i, true, false, false, At(tick++));
            if (d.EventEnded) closing = d;
        }

        Assert.NotNull(closing);
        Assert.True(closing!.Storm, "A watchdog force-close must raise a storm, not close quietly");
        Assert.False(closing.FlagOn);
        Assert.Equal("storm", policy.State);
    }

    [Fact]
    public void RateCap_FiveOnsetsInOneHour_RaisesStormAndCapsAtFour()
    {
        // A flapping entity must not become an alert firehose. The cap is fail-loud: the fifth
        // onset produces a storm signal instead of an event, never nothing at all.
        var policy = new AlertPolicy(FastParams(refractorySec: 0, maxEventsPerHour: 4, stormHoldSec: 3600));
        int tick = 0;
        for (int i = 0; i < 150; i++)
            policy.OnVerdict(0.5, true, false, false, At(tick++, 10));

        int events = 0;
        bool storm = false;
        for (int cycle = 0; cycle < 5; cycle++)
        {
            for (int i = 0; i < 3; i++)
            {
                var d = policy.OnVerdict(1000.0 + cycle, true, false, false, At(tick++, 10));
                if (d.EventStarted) events++;
                storm |= d.Storm;
            }
            for (int i = 0; i < 3; i++)
                policy.OnVerdict(0.5, true, false, false, At(tick++, 10));
        }

        Assert.Equal(4, events);
        Assert.True(storm, "The capped fifth onset must be reported as a storm");
    }

    [Fact]
    public void Refractory_ReFireInsideWindow_ReRaisesFlagButCountsNoNewEvent()
    {
        // An episode that blinks off and on again is one episode. Counting it twice would make
        // the acceptance criteria (5–15 event starts per day) unreadable, and would let a single
        // flapping sensor exhaust the hourly cap on its own.
        var policy = new AlertPolicy(FastParams(refractorySec: 3600));
        int tick = 0;
        for (int i = 0; i < 150; i++)
            policy.OnVerdict(0.5, true, false, false, At(tick++));

        int events = 0;
        for (int i = 0; i < 3; i++)
            if (policy.OnVerdict(1000.0 + i, true, false, false, At(tick++)).EventStarted) events++;
        Assert.Equal(1, events);

        for (int i = 0; i < 3; i++)
            policy.OnVerdict(0.5, true, false, false, At(tick++));

        AlertDecision? reFire = null;
        for (int i = 0; i < 3; i++)
            reFire = policy.OnVerdict(2000.0 + i, true, false, false, At(tick++));

        Assert.True(reFire!.FlagOn, "The flag must go back ON — the operator still needs to see it");
        Assert.False(reFire.EventStarted, "…but it is the same episode, not a new one");
    }

    [Fact]
    public void MinDuration_SingleTickSpike_HoldsFlagForMinDurationSec()
    {
        // Without a floor on episode length a one-tick spike produces a flag that HA may never
        // render and no automation can act on.
        var policy = new AlertPolicy(FastParams(minDurationSec: 300));
        int tick = 0;
        for (int i = 0; i < 150; i++)
            policy.OnVerdict(0.5, true, false, false, At(tick++, 10));

        DateTimeOffset firedAt = default;
        for (int i = 0; i < 3; i++)
        {
            var d = policy.OnVerdict(1000.0 + i, true, false, false, At(tick++, 10));
            if (d.FlagOn && firedAt == default) firedAt = At(tick - 1, 10);
        }
        Assert.NotEqual(default, firedAt);

        // Clear evidence 120 s in — well past min_consecutive, well short of min_duration.
        for (int i = 0; i < 6; i++)
        {
            var d = policy.OnVerdict(0.5, true, false, false, firedAt.AddSeconds(20 * i));
            Assert.True(d.FlagOn, "min_duration_sec must hold the flag even once the evidence is gone");
        }

        var released = policy.OnVerdict(0.5, true, false, false, firedAt.AddSeconds(301));
        Assert.False(released.FlagOn);
        Assert.True(released.EventEnded);
    }

    // ─── D-07 asymmetry ──────────────────────────────────────────────────────

    [Fact]
    public void ReconnectSuppression_BlocksOnTransition_ButAllowsOffTransition()
    {
        // D-07: the 60 s post-reconnect cooldown exists because a reconnect replays a burst of
        // stale states. It must block a NEW alarm, and it must NOT block an existing one from
        // ending — a symmetric rule would turn every reconnect into a flag that cannot go out,
        // which is exactly the F1 shape this workstream removes.
        var policy = new AlertPolicy(FastParams());
        int tick = 0;
        for (int i = 0; i < 150; i++)
            policy.OnVerdict(0.5, true, false, false, At(tick++));

        for (int i = 0; i < 10; i++)
        {
            var d = policy.OnVerdict(1000.0 + i, true, suppressed: true, frozen: false, At(tick++));
            Assert.False(d.FlagOn, "A cooldown reading may never start an event");
            Assert.False(d.EventStarted);
        }

        AlertDecision? onset = null;
        for (int i = 0; i < 3; i++)
            onset = policy.OnVerdict(5000.0 + i, true, suppressed: false, frozen: false, At(tick++));
        Assert.True(onset!.FlagOn);

        AlertDecision? closed = null;
        for (int i = 0; i < 3; i++)
            closed = policy.OnVerdict(0.5, true, suppressed: true, frozen: false, At(tick++));
        Assert.False(closed!.FlagOn, "A cooldown reading must still be allowed to end a running event");
        Assert.True(closed.EventEnded);
    }

    // ─── Frozen as evidence, not as an override ──────────────────────────────

    [Fact]
    public void FrozenEvidence_BypassesCalibration_ButStillClearsWhenUnfrozen()
    {
        // Before WS2 the frozen branch forced the flag ON from the write loop, bypassing
        // warm-up, cooldown and hysteresis — and only three scores below 0.3 could put it out,
        // which F2 proves never happen. Frozen is now a premise fed into the same gate: it can
        // still raise a flag on an uncalibrated entity, but the flag can go out again.
        var policy = new AlertPolicy(FastParams(alertMinSamples: 100));
        int tick = 0;

        AlertDecision? frozenOnset = null;
        for (int i = 0; i < 3; i++)
            frozenOnset = policy.OnVerdict(0.5, warmedUp: false, suppressed: false, frozen: true, At(tick++));

        Assert.False(policy.Calibrated);
        Assert.True(frozenOnset!.FlagOn, "Frozen must still be able to raise a flag before calibration");
        Assert.True(frozenOnset.EventStarted);
        Assert.Equal("frozen", frozenOnset.Channel);

        var thawed = policy.OnVerdict(0.5, warmedUp: false, suppressed: false, frozen: false, At(tick++));
        Assert.False(thawed.FlagOn, "An unfrozen sensor must be able to clear even while uncalibrated");
        Assert.True(thawed.EventEnded);
    }

    // ─── Scale ladder ────────────────────────────────────────────────────────

    [Fact]
    public void ScaleLadder_TwelveIdenticalThenOutlier_PicksFiniteNonDegenerateScale()
    {
        // Both rungs below MAD exist for a reason. MAD and the IQR are both zero when most of
        // the window is one value, so without the StdDev rung the channel would abstain on a
        // series that plainly contains an outlier. StdDev must see only the live slots: on a
        // 0/984 W duty-cycle sensor the unfilled tail of the array is zeros, and counting them
        // deflates the scale into permanent alarm.
        var z = new RollingRobustZ(720);
        for (int i = 0; i < 12; i++) z.Push(50.0);
        z.Push(500.0);

        double outlierZ = z.ZOf(500.0);
        double normalZ = z.ZOf(50.0);

        Assert.True(double.IsFinite(outlierZ), "Scale must never degenerate to zero while a spread exists");
        Assert.True(outlierZ > normalZ, "The outlier must score above the modal value");
        Assert.True(outlierZ > 1.0 && outlierZ < 100.0, $"z was {outlierZ:F3} — degenerate scale");
    }

    // ─── Evidence composition ────────────────────────────────────────────────

    [Fact]
    public void EvidenceModeBoth_ScoreHighAlone_DoesNotFire()
    {
        // evidence_mode is the documented retreat if the score ever becomes deviation-shaped:
        // "both" must genuinely require both channels, otherwise the knob is decorative.
        var policy = new AlertPolicy(FastParams(evidenceMode: "both"));
        int tick = 0;
        for (int i = 0; i < 150; i++)
            policy.OnVerdict(0.5, true, false, false, At(tick++));

        for (int i = 0; i < 20; i++)
        {
            var d = policy.OnVerdict(1000.0 + i, true, false, false, At(tick++));
            Assert.False(d.FlagOn, "Rank evidence alone must not fire in evidence_mode=both");
            Assert.False(d.EventStarted);
        }
    }
}
