using Argus.Orchestrator.Batch;
using Argus.Orchestrator.Config;

namespace Argus.Orchestrator.Detection;

/// <summary>
/// Detector-side gate configuration for one replay. Passed as a value rather than read off
/// EntityRuntimeState because the whole point of the panel is to answer "what would THESE
/// parameters have done?", which includes parameters the operator has not saved yet.
///
/// The three threshold numbers drive <see cref="HysteresisGate"/> on the legacy path; the
/// frozen pair drives <see cref="FrozenSensorDetector"/>, which is a PREMISE of the adaptive
/// gate (D-H) and therefore has to be replayed too. Nothing here has a default: every field
/// comes from the entity's own params, and a silently defaulted frozen threshold would let the
/// panel report episodes the live entity cannot have.
/// </summary>
public readonly record struct GateParams(
    double HighThreshold,
    double LowThreshold,
    int MinConsecutive,
    int FrozenWindow,
    double FrozenVarianceThreshold);

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
/// Replays a detector's scores through the SAME decision path the live pipeline runs for the
/// entity's current <c>alert_mode</c>, and reduces the result to the three numbers the operator
/// is deciding on.
///
/// WHY the mode matters here and not only in the pipeline: <c>alert_mode</c> defaults to
/// "adaptive", so on a stock install <c>ScoreStreamPipeline.ProcessVerdictAsync</c> decides
/// through <see cref="AlertPolicy"/> — rank inside the entity's own score window, robust z on
/// the raw value, min_duration, refractory, the rate cap and the watchdog. Replaying every
/// entity through <see cref="HysteresisGate"/> (which only "legacy" entities ever reach) made
/// the panel compare each score against an absolute 0.5 and report an episode count and an
/// on-time percent produced by a gate the entity does not run. Those two numbers are what the
/// alertsPerDay/on-time acceptance of WS6 is read off, so measuring the wrong gate is not a
/// cosmetic defect: it invalidates the measurement.
///
/// Pure function, no I/O, no shared state: the same history plus the same scores plus the same
/// parameters always produce the same summary. Every stateful participant (the policy, the
/// hysteresis gate, the frozen detector) is constructed inside the call and dropped on return,
/// so a replay can never move a live entity's calibration.
/// </summary>
public static class ReplaySimulator
{
    public static SimulateSummary Run(
        IReadOnlyList<HistoryPoint> history,
        SimulateResult sim,
        GateParams gate,
        AlertParams alertParams)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(sim);
        ArgumentNullException.ThrowIfNull(alertParams);

        // The detector answers 1:1 with the history it was sent, but a truncated or failed
        // response must degrade to "nothing scorable" rather than index past the end.
        var count = Math.Min(history.Count, sim.Scores.Count);
        var start = Math.Clamp(sim.WarmedUpFromIndex, 0, count);

        if (start >= count)
        {
            return new SimulateSummary(0, 0.0, 0.0, 0.0, 0, 0, default);
        }

        // Exactly the switch ProcessVerdictAsync makes, on exactly the same key.
        var flags = string.Equals(alertParams.Mode, "legacy", StringComparison.OrdinalIgnoreCase)
            ? ReplayLegacy(sim, gate, start, count)
            : ReplayAdaptive(history, sim, gate, alertParams, start, count);

        var firstAt = history[start].Timestamp;
        var lastAt = history[count - 1].Timestamp;
        var spanHours = (lastAt - firstAt).TotalHours;

        var episodes = 0;
        var transitions = 0;
        var onSeconds = 0.0;
        var previous = false;

        for (var i = start; i < count; i++)
        {
            var on = flags[i - start];

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

    /// <summary>
    /// alert_mode: legacy — the absolute-threshold gate, byte-for-byte the class
    /// <c>ProcessVerdictLegacyAsync</c> applies.
    /// </summary>
    private static bool[] ReplayLegacy(SimulateResult sim, GateParams gate, int start, int count)
    {
        var hysteresis = new HysteresisGate(
            gate.HighThreshold, gate.LowThreshold, gate.MinConsecutive);

        var flags = new bool[count - start];
        for (var i = start; i < count; i++)
            flags[i - start] = hysteresis.Apply(sim.Scores[i]);

        return flags;
    }

    /// <summary>
    /// alert_mode: adaptive (the default) — <see cref="AlertPolicy"/>, fed the same three
    /// inputs the live loops feed it: the detector's score, the raw reading, and the frozen
    /// detector's verdict for that reading.
    /// </summary>
    private static bool[] ReplayAdaptive(
        IReadOnlyList<HistoryPoint> history,
        SimulateResult sim,
        GateParams gate,
        AlertParams alertParams,
        int start,
        int count)
    {
        var policy = new AlertPolicy(alertParams);
        var frozen = new FrozenSensorDetector(gate.FrozenWindow, gate.FrozenVarianceThreshold);

        // The warm-up prefix is history, and history is what primes the raw channel live:
        // ScoreStreamPipeline backfills it into AlertPolicy.SeedHistory before the first
        // verdict arrives. Dropping it here instead would start the raw channel from an empty
        // window and report "no evidence" for the first ten readings of every replay — a
        // silence the live entity does not have. SeedValue, not ObserveValue, for the same
        // reason production uses it: a historical value must build the baseline, never be
        // scored against a half-filled one.
        for (var i = 0; i < start; i++)
        {
            policy.SeedValue(history[i].Value);
            frozen.AddReading(history[i].Value);
        }

        var flags = new bool[count - start];

        for (var i = start; i < count; i++)
        {
            var value = history[i].Value;

            // Write-loop order, preserved: the frozen detector and the raw channel both see
            // the reading before the verdict it produced reaches the gate.
            frozen.AddReading(value);
            policy.ObserveValue(value);

            // now = the reading's OWN timestamp, never UtcNow. min_duration_sec,
            // refractory_sec, max_events_per_hour and the watchdog are all wall-clock, so
            // stamping a replay with the request time would collapse a day of history into
            // milliseconds — every episode inside one refractory window, and a storm on the
            // fifth. Taking `now` as a parameter is exactly why AlertPolicy can be replayed.
            var decision = policy.OnVerdict(
                sim.Scores[i],
                warmedUp: true,
                suppressed: false,
                frozen: frozen.IsFrozen,
                now: history[i].Timestamp);

            flags[i - start] = decision.FlagOn;
        }

        return flags;
    }
}
