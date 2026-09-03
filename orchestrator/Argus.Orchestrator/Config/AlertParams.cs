using System.Globalization;

namespace Argus.Orchestrator.Config;

/// <summary>
/// Typed accessor for the alert-layer parameters (WS2 / D-C, D-D).
///
/// Reads the SAME <see cref="DetectorConfig.Params"/> map as <see cref="HstParams"/> — no new
/// YAML block, no new SaveRequest field — so an operator can flip a single entity back to the
/// old absolute-threshold gate by adding <c>alert_mode: legacy</c> to its params and reloading.
///
/// Every key is optional and a missing/unparsable key falls back to its default. WHY never an
/// error: the SPA does not send these keys at all, so a required key would turn every Save into
/// a validation failure. Range validation lives in InputValidator and only runs for keys that
/// are actually present.
/// </summary>
public sealed record AlertParams
{
    /// <summary>"adaptive" (rank + robust-z event layer) or "legacy" (HysteresisGate).</summary>
    public string Mode { get; init; } = "adaptive";

    /// <summary>"any" | "both" | "score_only" | "raw_only" — how the two evidence channels combine.</summary>
    public string EvidenceMode { get; init; } = "any";

    /// <summary>Number of recent scores the rank channel keeps per entity.</summary>
    public int RankWindow { get; init; } = 720;

    /// <summary>Rank at or above which the score channel votes fire.</summary>
    public double QFire { get; init; } = 0.99;

    /// <summary>Rank below which the score channel votes clear.</summary>
    public double QClear { get; init; } = 0.80;

    /// <summary>Number of recent raw values the robust-z channel keeps per entity.</summary>
    public int RawWindow { get; init; } = 720;

    /// <summary>Robust z at or above which the raw channel votes fire.</summary>
    public double ZFire { get; init; } = 5.0;

    /// <summary>Robust z below which the raw channel votes clear.</summary>
    public double ZClear { get; init; } = 3.0;

    /// <summary>Consecutive agreeing verdicts required to flip state (existing key, shared with HstParams).</summary>
    public int MinConsecutive { get; init; } = 3;

    /// <summary>Verdicts required before the rank channel is trusted at all.</summary>
    public int AlertMinSamples { get; init; } = 240;

    /// <summary>Minimum wall-clock lifetime of a raised event before it may clear.</summary>
    public int MinDurationSec { get; init; } = 120;

    /// <summary>Re-raising the flag within this window of the last close continues the same event.</summary>
    public int RefractorySec { get; init; } = 600;

    /// <summary>Event onsets per rolling hour above which a storm is raised instead of a new event.</summary>
    public int MaxEventsPerHour { get; init; } = 4;

    /// <summary>Watchdog: an event held longer than this is force-closed (F1's hard backstop).</summary>
    public int MaxEventDurationSec { get; init; } = 21600;

    /// <summary>How long a raised storm suppresses further onsets.</summary>
    public int StormHoldSec { get; init; } = 3600;

    /// <summary>Builds params from a detector's raw params map; absent keys take their default.</summary>
    public static AlertParams From(Dictionary<string, string> p)
    {
        return new AlertParams
        {
            Mode = GetString(p, "alert_mode", "adaptive"),
            EvidenceMode = GetString(p, "evidence_mode", "any"),
            RankWindow = GetInt(p, "rank_window", 720),
            QFire = GetDouble(p, "q_fire", 0.99),
            QClear = GetDouble(p, "q_clear", 0.80),
            RawWindow = GetInt(p, "raw_window", 720),
            ZFire = GetDouble(p, "z_fire", 5.0),
            ZClear = GetDouble(p, "z_clear", 3.0),
            MinConsecutive = GetInt(p, "min_consecutive", 3),
            AlertMinSamples = GetInt(p, "alert_min_samples", 240),
            MinDurationSec = GetInt(p, "min_duration_sec", 120),
            RefractorySec = GetInt(p, "refractory_sec", 600),
            MaxEventsPerHour = GetInt(p, "max_events_per_hour", 4),
            MaxEventDurationSec = GetInt(p, "max_event_duration_sec", 21600),
            StormHoldSec = GetInt(p, "storm_hold_sec", 3600),
        };
    }

    private static string GetString(Dictionary<string, string> p, string key, string def)
        => p.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim().ToLowerInvariant() : def;

    private static int GetInt(Dictionary<string, string> p, string key, int def)
        => p.TryGetValue(key, out var v) && int.TryParse(v, out var r) ? r : def;

    // InvariantCulture, matching HstParams.GetDouble — a Polish CurrentCulture must not
    // turn "0.995" into 995.
    private static double GetDouble(Dictionary<string, string> p, string key, double def)
        => p.TryGetValue(key, out var v) &&
           double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : def;
}
