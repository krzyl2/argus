using System.Globalization;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Web;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// The sensitivity presets are the ONLY place an operator retunes rmad without reading the
/// param table, so two things must hold or the picker is a trap: the three options must
/// actually be ordered by sensitivity (High fires earlier than Med, Med earlier than Low),
/// and every one of them must survive the same server-side validation a hand-typed value does.
/// </summary>
public class SensorPresetsTests
{
    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static Dictionary<int, List<DetectorConfig>> WithPreset(Dictionary<string, string> preset)
    {
        var p = new Dictionary<string, string>(DetectorDefaults.Get("rmad")!);
        foreach (var (k, v) in preset) p[k] = v;
        return new Dictionary<int, List<DetectorConfig>>
        {
            [0] = [new DetectorConfig { Name = "rmad", Params = p }],
        };
    }

    [Fact]
    public void RmadPresets_AreStrictlyOrdered_AndPassServerValidation()
    {
        var presets = SensorPresets.Get("rmad");
        Assert.NotNull(presets);
        Assert.Equal(new[] { "Low", "Med", "High" }, presets!.Select(x => x.Label).ToArray());

        double High(string label) => Num(presets.Single(x => x.Label == label).Params["high_threshold"]);
        double Low(string label) => Num(presets.Single(x => x.Label == label).Params["low_threshold"]);

        // A lower threshold means more sensitive, so both series decrease Low -> Med -> High.
        Assert.True(High("Low") > High("Med"));
        Assert.True(High("Med") > High("High"));
        Assert.True(Low("Low") > Low("Med"));
        Assert.True(Low("Med") > Low("High"));

        foreach (var preset in presets)
        {
            // Inverted thresholds are the one misconfiguration that looks valid and never
            // alarms — hysteresis would never release, or never fire.
            Assert.True(
                Num(preset.Params["high_threshold"]) > Num(preset.Params["low_threshold"]),
                $"preset {preset.Label} has high <= low");

            var errors = InputValidator.Validate(["sensor.load_5m"], WithPreset(preset.Params));
            Assert.Empty(errors);
        }
    }

    /// <summary>
    /// A preset must move the two threshold keys and NOTHING else: window, min_samples and
    /// scale_floor are in units the operator's sensor owns (samples, sensor units), and the
    /// measured cadences here span 15.3 s to 391 s per sample. Rewriting them from a
    /// sensitivity radio button would retune the sensor's memory, not its sensitivity.
    /// </summary>
    [Fact]
    public void RmadPresets_TouchOnlyThresholdKeys()
    {
        foreach (var preset in SensorPresets.Get("rmad")!)
        {
            Assert.Equal(
                new[] { "high_threshold", "low_threshold" },
                preset.Params.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        }
    }

    [Fact]
    public void Get_UnknownDetector_ReturnsNull()
    {
        Assert.Null(SensorPresets.Get("hst"));
        Assert.Null(SensorPresets.Get(""));
        Assert.Null(SensorPresets.Get(null));
    }

    /// <summary>
    /// The Med preset IS the default table (D-B). This matters because the picker adopts a
    /// matching preset's label instead of clobbering saved values: if Med drifted from the
    /// defaults, opening a freshly-migrated entity would show it as customized and the first
    /// click on Med would silently rewrite its thresholds.
    /// </summary>
    [Fact]
    public void MedPreset_EqualsTheDefaultThresholds()
    {
        var med = SensorPresets.Get("rmad")!.Single(p => p.Label == "Med");
        var defaults = DetectorDefaults.Get("rmad")!;

        Assert.Equal(defaults["high_threshold"], med.Params["high_threshold"]);
        Assert.Equal(defaults["low_threshold"], med.Params["low_threshold"]);
    }
}
