namespace Argus.Orchestrator.Web;

/// <summary>
/// Low/Med/High sensitivity presets for SINGLE-SENSOR detectors, backing the presets half of
/// GET /api/detectors/defaults. Parallel to DetectorCatalog.cs (which covers GROUP detectors)
/// and deliberately reuses its <see cref="DetectorPreset"/> record rather than declaring a
/// second, near-identical type.
///
/// A preset moves EXACTLY the two threshold keys and nothing else. WHY: rmad's score is
/// dimensionless (z / (z + z_scale)), so sensitivity is entirely a threshold question — while
/// `window`, `min_samples` and `scale_floor` are all measured in units the operator's sensor
/// owns (samples, sensor units). Baking a guess about those into a preset would silently
/// retune cadence-dependent behaviour: the measured cadences on this installation span
/// 15.3 s/sample (memory_use_percent) to 391 s/sample (lodowkababcia_power), a factor of 25.
/// There is deliberately NO sensor-class table here — cadence is measured, never guessed
/// from the unit.
///
/// The z each threshold means is z = z_scale * t / (1 - t) with z_scale = 5:
///   Low  0.615 / 0.444 -> fire z 7.99, release z 3.99
///   Med  0.5   / 0.375 -> fire z 5.00, release z 3.00  (the D-B default, label per SPA)
///   High 0.444 / 0.286 -> fire z 3.99, release z 2.00
/// </summary>
public static class SensorPresets
{
    /// <summary>
    /// Returns the three presets for a single-sensor detector, or null when the detector has
    /// none defined. Only rmad has presets — hst/mad/stl are tuned by hand.
    /// </summary>
    public static List<DetectorPreset>? Get(string? name)
    {
        return (name ?? "").ToLowerInvariant() switch
        {
            "rmad" =>
            [
                new DetectorPreset("Low", new Dictionary<string, string>
                {
                    ["high_threshold"] = "0.615",
                    ["low_threshold"] = "0.444",
                }),
                new DetectorPreset("Med", new Dictionary<string, string>
                {
                    ["high_threshold"] = "0.5",
                    ["low_threshold"] = "0.375",
                }),
                new DetectorPreset("High", new Dictionary<string, string>
                {
                    ["high_threshold"] = "0.444",
                    ["low_threshold"] = "0.286",
                }),
            ],
            _ => null,
        };
    }
}
