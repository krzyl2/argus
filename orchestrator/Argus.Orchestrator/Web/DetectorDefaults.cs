namespace Argus.Orchestrator.Web;

/// <summary>
/// Authoritative detector default-parameter tables (HST/MAD/STL), backing
/// GET /api/detectors/defaults. Values are the v3.0 parity spec — carried over verbatim
/// from the removed EntityPickerPage HST/MAD/STL default constants (07-UI-SPEC "Detector
/// default values"). Extracted as a standalone static class so it is directly unit-testable
/// without spinning up the Kestrel pipeline.
///
/// WR-02 is WITHDRAWN (WS3): the client no longer keeps a mirrored DETECTOR_DEFAULTS table.
/// orchestrator/ui/src/state/detectorDefaults.ts fetches GET /api/detectors/defaults, so this
/// class is the single source of truth — changing a number here and rebuilding only the .NET
/// side changes the UI too. Do not reintroduce a client-side copy.
/// </summary>
public static class DetectorDefaults
{
    /// <summary>
    /// Returns the default parameter table for the given detector type
    /// ("rmad"/"hst"/"mad"/"stl", case-insensitive), or null when the name is unknown/empty.
    /// </summary>
    public static Dictionary<string, string>? Get(string? name)
    {
        return (name ?? "").ToLowerInvariant() switch
        {
            // rmad first — it is the default detector (D-A). Every literal here must equal the
            // corresponding fallback in RmadParams.From; DetectorDefaultsTests pins that pairing.
            "rmad" => new Dictionary<string, string>
            {
                ["window"] = "720",
                ["min_samples"] = "60",
                ["z_scale"] = "5.0",
                ["scale_floor"] = "0.0",
                ["high_threshold"] = "0.5",
                ["low_threshold"] = "0.375",
                ["min_consecutive"] = "3",
                ["frozen_window"] = "10",
                ["frozen_variance_threshold"] = "0.0",
            },
            "hst" => new Dictionary<string, string>
            {
                ["window"] = "250",
                ["n_trees"] = "25",
                ["high_threshold"] = "0.7",
                ["low_threshold"] = "0.3",
                ["min_consecutive"] = "3",
                ["frozen_window"] = "10",
                ["frozen_variance_threshold"] = "0.001",
            },
            "mad" => new Dictionary<string, string>
            {
                ["threshold"] = "3.5",
                ["window"] = "20",
            },
            "stl" => new Dictionary<string, string>
            {
                ["period"] = "24",
                ["seasonal"] = "7",
                ["threshold"] = "3.0",
            },
            _ => null,
        };
    }

    /// <summary>
    /// Every known detector's default table, keyed by detector name. Backs the no-name variant
    /// of GET /api/detectors/defaults so the SPA can load the whole table in one request at
    /// startup instead of one round-trip per detector type.
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>> All()
    {
        var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var name in new[] { "rmad", "hst", "mad", "stl" })
            all[name] = Get(name)!;
        return all;
    }
}
