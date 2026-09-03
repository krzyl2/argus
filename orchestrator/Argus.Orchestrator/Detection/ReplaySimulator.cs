using Argus.Orchestrator.Batch;

namespace Argus.Orchestrator.Detection;

/// <summary>
/// Gate configuration for one replay — the same three numbers HysteresisGate takes in
/// production (D-C). Passed as a value rather than read off EntityRuntimeState because the
/// whole point of the panel is to answer "what would THESE parameters have done?", which
/// includes parameters the operator has not saved yet.
/// </summary>
public readonly record struct GateParams(double HighThreshold, double LowThreshold, int MinConsecutive);

/// <summary>
/// Outcome of one replay, in the units an operator can act on.
/// </summary>
/// <param name="Episodes">OFF→ON transitions in the scorable region — "how many times would
/// this have fired".</param>
/// <param name="OnTimePercent">Percentage of WALL-CLOCK time spent ON, not percentage of
/// samples. Cadences in this installation range from 225 to 5082 samples/day, so a
/// sample-counted number is not comparable between two sensors, and the F1 baseline it has to
/// be compared against is itself time-weighted.</param>
/// <param name="SpanHours">Wall-clock span of the SCORABLE region only. The warm-up prefix is
/// excluded, otherwise a 720-sample warm-up on a slow sensor would dominate the denominator.</param>
/// <param name="AlertsPerDay">Episodes normalised to 24 h — the only rate that can be compared
/// across two lookbacks (B5).</param>
/// <param name="ScorablePoints">Points actually fed to the gate.</param>
/// <param name="Transitions">All flag flips, both directions. ON→OFF count is
/// <c>Transitions - Episodes</c>, which is what F2 is measured on: the legacy detector's
/// defining symptom is a flag that can never fall back.</param>
/// <param name="FirstScorableAt">Timestamp of the first gated point; the panel labels the
/// chart's live region with it.</param>
public sealed record SimulateSummary(
    int Episodes,
    double OnTimePercent,
    double SpanHours,
    double AlertsPerDay,
    int ScorablePoints,
    int Transitions,
    DateTimeOffset FirstScorableAt);

/// <summary>
/// Replays a detector's scores through the PRODUCTION hysteresis gate (D-C) and reduces the
/// result to the three numbers the operator is deciding on.
///
/// Pure function, no I/O, no state: the same history plus the same scores plus the same gate
/// parameters always produce the same summary. That is what makes it legitimate to describe
/// the panel's numbers as "what would have happened" rather than "what a second, similar
/// implementation thinks would have happened" — the gate class here is the same class the
/// live pipeline instantiates.
/// </summary>
public static class ReplaySimulator
{
    public static SimulateSummary Run(
        IReadOnlyList<HistoryPoint> history, SimulateResult sim, GateParams gate)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(sim);

        // The detector answers 1:1 with the history it was sent, but a truncated or failed
        // response must degrade to "nothing scorable" rather than index past the end.
        var count = Math.Min(history.Count, sim.Scores.Count);
        var start = Math.Clamp(sim.WarmedUpFromIndex, 0, count);

        if (start >= count)
        {
            return new SimulateSummary(0, 0.0, 0.0, 0.0, 0, 0, default);
        }

        var hysteresis = new HysteresisGate(
            gate.HighThreshold, gate.LowThreshold, gate.MinConsecutive);

        var firstAt = history[start].Timestamp;
        var lastAt = history[count - 1].Timestamp;
        var spanHours = (lastAt - firstAt).TotalHours;

        var episodes = 0;
        var transitions = 0;
        var onSeconds = 0.0;
        var previous = false;

        for (var i = start; i < count; i++)
        {
            var on = hysteresis.Apply(sim.Scores[i]);

            if (on != previous)
            {
                transitions++;
                if (on) episodes++;
                previous = on;
            }

            // Dwell is attributed to the state the gate is in AFTER this reading, and runs
            // until the next reading. The final point carries no dwell — there is no evidence
            // about what happened after the history ends, and inventing a tail would inflate
            // exactly the sensors that report least often.
            if (on && i + 1 < count)
            {
                var dwell = (history[i + 1].Timestamp - history[i].Timestamp).TotalSeconds;
                if (dwell > 0) onSeconds += dwell;
            }
        }

        var onTimePercent = spanHours > 0.0 ? 100.0 * onSeconds / (spanHours * 3600.0) : 0.0;
        var alertsPerDay = spanHours > 0.0 ? episodes * 24.0 / spanHours : 0.0;

        return new SimulateSummary(
            episodes, onTimePercent, spanHours, alertsPerDay, count - start, transitions, firstAt);
    }
}
