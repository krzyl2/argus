using Argus.Orchestrator.Batch;
using Argus.Orchestrator.Detection;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for ReplaySimulator — the reduction of a replayed score array to the three numbers
/// the operator decides on.
///
/// Two invariants matter more than the arithmetic:
///   B5 — a rate must be normalised to 24 h. The panel's lookback is operator-chosen, so a
///        raw episode count from a 12 h window is not comparable to the F13 targets, which
///        are all stated per day. Reporting the raw count would let a WS pass by shortening
///        the window.
///   Warm-up — scores before warmed_up_from_index are a structural 0.0, not a measurement.
///        Feeding them to the gate manufactures a release edge (three sub-threshold readings)
///        that never happened on the sensor, which would make every replay look like it had
///        healthy ON→OFF behaviour — the exact property F2 exists to test.
/// </summary>
public class ReplaySimulatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);

    private static readonly GateParams Defaults = new(0.5, 0.375, 3);

    private static IReadOnlyList<HistoryPoint> History(int count, TimeSpan step)
        => Enumerable.Range(0, count)
            .Select(i => new HistoryPoint(T0 + step * i, 100.0))
            .ToList();

    private static SimulateResult Sim(IReadOnlyList<double> scores, int warmedFrom)
        => new(true, null, scores, Array.Empty<double>(), warmedFrom, warmedFrom, "test");

    /// <summary>
    /// Drives the gate to ON for <paramref name="onLength"/> readings and back OFF, repeated
    /// <paramref name="episodes"/> times, padding to <paramref name="total"/> with releases.
    /// min_consecutive is 3, so each block is comfortably longer than the gate needs.
    /// </summary>
    private static List<double> Pulses(int total, int episodes, int onLength, int offLength)
    {
        var scores = new List<double>(total);
        for (var e = 0; e < episodes; e++)
        {
            scores.AddRange(Enumerable.Repeat(0.9, onLength));
            scores.AddRange(Enumerable.Repeat(0.0, offLength));
        }
        while (scores.Count < total) scores.Add(0.0);
        return scores.Take(total).ToList();
    }

    [Fact]
    public void AlertsPerDay_NormalizesToSpanHours()
    {
        // 145 points, 5 minutes apart => exactly 12 h of scorable span, no warm-up prefix.
        var history = History(145, TimeSpan.FromMinutes(5));
        var scores = Pulses(145, episodes: 2, onLength: 6, offLength: 30);

        var summary = ReplaySimulator.Run(history, Sim(scores, 0), Defaults);

        Assert.Equal(2, summary.Episodes);
        Assert.Equal(12.0, summary.SpanHours, 6);
        // B5: 2 episodes in 12 h is 4 alerts/day, and that is the number the F13 targets
        // ("load_5m <= 6/dobe") are stated in.
        Assert.Equal(4.0, summary.AlertsPerDay, 6);
    }

    [Fact]
    public void NoTransitionsBeforeWarmedUpFromIndex()
    {
        // The first 60 scores are the detector's structural 0.0 (rmad's min_samples gate).
        // Read literally they are three-in-a-row releases and would flip the gate OFF, then
        // the following 0.9s would count as a fresh episode with a release edge in front of
        // it — a clean ON→OFF story the sensor never told.
        var history = History(120, TimeSpan.FromMinutes(1));
        var scores = Enumerable.Repeat(0.0, 60).Concat(Enumerable.Repeat(0.9, 60)).ToList();

        var gated = ReplaySimulator.Run(history, Sim(scores, 60), Defaults);
        var ungated = ReplaySimulator.Run(history, Sim(scores, 0), Defaults);

        Assert.Equal(60, gated.ScorablePoints);
        Assert.Equal(history[60].Timestamp, gated.FirstScorableAt);
        // One rise, no fall: the warm-up prefix contributed nothing.
        Assert.Equal(1, gated.Episodes);
        Assert.Equal(1, gated.Transitions);
        // And the span is measured from the first SCORABLE point, not from the warm-up start.
        Assert.Equal(59.0 / 60.0, gated.SpanHours, 6);
        Assert.True(ungated.SpanHours > gated.SpanHours);
    }

    [Fact]
    public void OnTimeIsWeightedByWallClock_NotBySampleCount()
    {
        // Two readings per hour for 10 h, then a burst of 10 readings one minute apart.
        // The burst is where the flag fires: 10 of 30 samples, but only ~9 minutes of the
        // ~10 h span. A sample-counted on-time would report ~33%; the truth is well under 2%.
        var timestamps = new List<DateTimeOffset>();
        for (var i = 0; i < 20; i++) timestamps.Add(T0 + TimeSpan.FromMinutes(30 * i));
        var burstStart = timestamps[^1];
        for (var i = 1; i <= 10; i++) timestamps.Add(burstStart + TimeSpan.FromMinutes(i));

        var history = timestamps.Select(t => new HistoryPoint(t, 1.0)).ToList();
        var scores = Enumerable.Repeat(0.0, 20).Concat(Enumerable.Repeat(0.9, 10)).ToList();

        var summary = ReplaySimulator.Run(history, Sim(scores, 0), Defaults);

        Assert.Equal(1, summary.Episodes);
        Assert.True(summary.OnTimePercent < 2.0,
            $"on-time must be time-weighted, got {summary.OnTimePercent:F2}%");
    }

    [Fact]
    public void LatchedFlag_ReportsZeroReleases()
    {
        // F2's signature: a score series whose minimum never drops below low_threshold. The
        // summary must show it — one rise, no fall — instead of quietly reporting one tidy
        // episode indistinguishable from a real, closed one.
        var history = History(100, TimeSpan.FromMinutes(1));
        var scores = Enumerable.Repeat(0.9, 100).ToList();

        var summary = ReplaySimulator.Run(history, Sim(scores, 0), Defaults);

        Assert.Equal(1, summary.Episodes);
        Assert.Equal(1, summary.Transitions);
        // ON->OFF count = Transitions - Episodes; zero here is the defect, not a rounding
        // artefact.
        Assert.Equal(0, summary.Transitions - summary.Episodes);
        Assert.True(summary.OnTimePercent > 95.0);
    }

    [Fact]
    public void FailedSimulation_YieldsEmptySummaryInsteadOfThrowing()
    {
        // An Unimplemented from an old detector build arrives as ok=false with no scores.
        // The panel must render "no result", never a 500 next to a healthy scoring path.
        var history = History(50, TimeSpan.FromMinutes(1));
        var failed = new SimulateResult(
            false, "Unimplemented", Array.Empty<double>(), Array.Empty<double>(), 0, 0, "");

        var summary = ReplaySimulator.Run(history, failed, Defaults);

        Assert.Equal(0, summary.ScorablePoints);
        Assert.Equal(0, summary.Episodes);
        Assert.Equal(0.0, summary.AlertsPerDay);
    }

    [Fact]
    public void ProductionGateSemanticsAreReused_MinConsecutiveStillApplies()
    {
        // Two highs in a row must NOT fire — min_consecutive is 3. This is asserted here (and
        // not only in HysteresisGateTests) because the whole claim of the panel is that it
        // uses the production gate; a replay that re-implemented the state machine would
        // answer a different question than the one the operator asked.
        var history = History(30, TimeSpan.FromMinutes(1));
        var scores = new List<double>();
        for (var i = 0; i < 15; i++) { scores.Add(0.9); scores.Add(0.9); scores.Add(0.0); }

        var summary = ReplaySimulator.Run(history, Sim(scores, 0), Defaults);

        Assert.Equal(0, summary.Episodes);
    }
}
