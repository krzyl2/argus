using System.Globalization;

namespace Argus.Orchestrator.Config;

/// <summary>Root deserialization type for entities.yaml.</summary>
public class EntitiesConfig
{
    public List<EntityConfig> Entities { get; set; } = new();
    public List<GroupConfig> Groups { get; set; } = new();
}

public class EntityConfig
{
    public string EntityId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public List<DetectorConfig> Detectors { get; set; } = new();
}

/// <summary>
/// Operator-declared group of sensor members for group/multivariate anomaly detection (GRP-01).
/// Deserialized from the top-level `groups:` key in entities.yaml.
/// </summary>
public class GroupConfig
{
    public string GroupId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public List<string> Members { get; set; } = new();

    /// <summary>"peer_divergence" | "joint"</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>"peer_divergence" | "ecod" | "copod" | "pca" | "iforest"</summary>
    public string Detector { get; set; } = string.Empty;
    public Dictionary<string, string> Params { get; set; } = new();

    /// <summary>
    /// Populated at config-load time from IHaSensorRegistry — NOT deserialized from YAML.
    /// Consumed by EntitiesConfigLoader.ValidateGroups for the peer-divergence shared-unit check.
    /// </summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public Dictionary<string, string?> ResolvedUnits { get; set; } = new();
}

public class DetectorConfig
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Params { get; set; } = new();
}

/// <summary>
/// Typed HST parameter accessor with D-09/D-11/D-12 defaults.
/// Consumes the DetectorConfig.Params dictionary; defaults apply when keys are absent.
/// </summary>
public class HstParams
{
    // D-09 defaults
    public int Window { get; init; } = 250;
    public int NTrees { get; init; } = 25;

    // D-11 defaults
    public double HighThreshold { get; init; } = 0.7;
    public double LowThreshold { get; init; } = 0.3;
    public int MinConsecutive { get; init; } = 3;

    // D-12 defaults
    public int FrozenWindow { get; init; } = 10;
    public double FrozenVarianceThreshold { get; init; } = 0.001;

    public static HstParams From(Dictionary<string, string> p)
    {
        return new HstParams
        {
            Window = GetInt(p, "window", 250),
            NTrees = GetInt(p, "n_trees", 25),
            HighThreshold = GetDouble(p, "high_threshold", 0.7),
            LowThreshold = GetDouble(p, "low_threshold", 0.3),
            MinConsecutive = GetInt(p, "min_consecutive", 3),
            FrozenWindow = GetInt(p, "frozen_window", 10),
            FrozenVarianceThreshold = GetDouble(p, "frozen_variance_threshold", 0.001),
        };
    }

    private static int GetInt(Dictionary<string, string> p, string key, int def)
        => p.TryGetValue(key, out var v) && int.TryParse(v, out var r) ? r : def;

    private static double GetDouble(Dictionary<string, string> p, string key, double def)
        => p.TryGetValue(key, out var v) &&
           double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : def;
}

/// <summary>
/// Typed rmad parameter accessor (D-A/D-B/D-M) — the default streaming detector.
///
/// The thresholds are DIMENSIONLESS. rmad publishes score = z / (z + z_scale) where z is the
/// robust deviation |x - median| / (1.4826 * MAD), so with z_scale = 5 the defaults invert to
/// exactly "fire above z 5, release below z 3, 3 consecutive" — the variant measured in F13.
/// That is why ONE default table is arithmetically correct on every sensor regardless of unit
/// or range (this is the actual fix for F6), and why z_scale is a module constant on the Python
/// side rather than a knob here: z_scale and high_threshold are the same degree of freedom.
///
/// Inverse, for UI copy: z = z_scale * t / (1 - t)  =>  0.5 -> 5.0, 0.375 -> 3.0.
///
/// Frozen detection is disabled by VARIANCE, never by window (D-H): FrozenSensorDetector.cs
/// dequeues from an empty queue when the window is 0, and InputValidator requires >= 1.
/// </summary>
public class RmadParams
{
    /// <summary>Rolling baseline window, in SAMPLES (see §7 #14 — not wall-clock).</summary>
    public int Window { get; init; } = 720;

    /// <summary>
    /// Samples needed before a verdict is trusted. D-M: this — not Window — is the warm-up
    /// gate that actually applies, so it is what Verdict.window reports and what the UI shows.
    /// </summary>
    public int MinSamples { get; init; } = 60;

    /// <summary>Score-squashing constant; 0.5 &lt;=&gt; z 5, 0.8 &lt;=&gt; z 20 (D-E).</summary>
    public double ZScale { get; init; } = 5.0;

    /// <summary>
    /// Floor on the scale estimate, in the SENSOR'S OWN UNITS (D-I). 0.0 by default; the
    /// migration writes 0.3 for percent-unit sensors, where a quantized MAD of 0.1 otherwise
    /// turns a benign 1.1 pp move into z = 7.4.
    /// </summary>
    public double ScaleFloor { get; init; } = 0.0;

    public double HighThreshold { get; init; } = 0.5;
    public double LowThreshold { get; init; } = 0.375;
    public int MinConsecutive { get; init; } = 3;

    /// <summary>D-H: carried verbatim; 0 is FORBIDDEN (FrozenSensorDetector dequeues empty).</summary>
    public int FrozenWindow { get; init; } = 10;

    /// <summary>D-H: 0.0 disables frozen arithmetically — variance is never negative.</summary>
    public double FrozenVarianceThreshold { get; init; } = 0.0;

    public static RmadParams From(Dictionary<string, string> p)
    {
        return new RmadParams
        {
            Window = GetInt(p, "window", 720),
            MinSamples = GetInt(p, "min_samples", 60),
            ZScale = GetDouble(p, "z_scale", 5.0),
            ScaleFloor = GetDouble(p, "scale_floor", 0.0),
            HighThreshold = GetDouble(p, "high_threshold", 0.5),
            LowThreshold = GetDouble(p, "low_threshold", 0.375),
            MinConsecutive = GetInt(p, "min_consecutive", 3),
            FrozenWindow = GetInt(p, "frozen_window", 10),
            FrozenVarianceThreshold = GetDouble(p, "frozen_variance_threshold", 0.0),
        };
    }

    private static int GetInt(Dictionary<string, string> p, string key, int def)
        => p.TryGetValue(key, out var v) && int.TryParse(v, out var r) ? r : def;

    private static double GetDouble(Dictionary<string, string> p, string key, double def)
        => p.TryGetValue(key, out var v) &&
           double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : def;
}
