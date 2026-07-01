using System.Globalization;

namespace Argus.Orchestrator.Config;

/// <summary>Root deserialization type for entities.yaml.</summary>
public class EntitiesConfig
{
    public List<EntityConfig> Entities { get; set; } = new();
}

public class EntityConfig
{
    public string EntityId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public List<DetectorConfig> Detectors { get; set; } = new();

    /// <summary>Parsed but ignored in Phase 1 — see EntitiesConfigLoader for warning.</summary>
    public object? Covariates { get; set; }

    /// <summary>Parsed but ignored in Phase 1 — see EntitiesConfigLoader for warning.</summary>
    public object? Groups { get; set; }
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
