namespace Argus.Orchestrator.Web;

/// <summary>
/// Authoritative detector default-parameter tables (HST/MAD/STL), backing
/// GET /api/detectors/defaults. Values are the v3.0 parity spec — carried over verbatim
/// from the removed EntityPickerPage HST/MAD/STL default constants (07-UI-SPEC "Detector
/// default values"). Extracted as a standalone static class so it is directly unit-testable
/// without spinning up the Kestrel pipeline.
///
/// WR-02: intentionally mirrored in orchestrator/ui/src/state/sensors.ts (DETECTOR_DEFAULTS).
/// The client does not call GET /api/detectors/defaults — it constructs new detector entries
/// client-side to avoid a server round-trip. If either table changes, update BOTH and confirm
/// they still match exactly.
/// </summary>
public static class DetectorDefaults
{
    /// <summary>
    /// Returns the default parameter table for the given detector type ("hst"/"mad"/"stl",
    /// case-insensitive), or null when the name is unknown/empty.
    /// </summary>
    public static Dictionary<string, string>? Get(string? name)
    {
        return (name ?? "").ToLowerInvariant() switch
        {
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
}
