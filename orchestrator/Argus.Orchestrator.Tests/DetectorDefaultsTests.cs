using Argus.Orchestrator.Config;
using Argus.Orchestrator.Web;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Pins the rmad default table (D-A/D-B). These are not style checks: the whole premise of the
/// fix is that ONE dimensionless default table is arithmetically correct on every sensor, so
/// the two invariants that matter are (a) the thresholds invert to the robust-z pair that was
/// actually measured in F13, and (b) the table the API serves and the table the pipeline parses
/// are literally the same numbers — a drift between them means the operator tunes one thing in
/// the UI and the detector runs another.
/// </summary>
public class DetectorDefaultsTests
{
    /// <summary>z = z_scale * t / (1 - t) — the inverse of rmad's score squashing.</summary>
    private static double RobustZ(double threshold, double zScale)
        => zScale * threshold / (1.0 - threshold);

    [Fact]
    public void RmadDefaults_MapToRobustZ5AndZ3()
    {
        var defaults = DetectorDefaults.Get("rmad");
        Assert.NotNull(defaults);

        var p = RmadParams.From(defaults!);

        // "fire above z 5, release below z 3, 3 consecutive" — the exact variant measured in
        // F13. If someone retunes high_threshold without recomputing this, the default table
        // stops being the measured one and the acceptance numbers in D-J no longer apply.
        Assert.Equal(5.0, RobustZ(p.HighThreshold, p.ZScale), 2);
        Assert.Equal(3.0, RobustZ(p.LowThreshold, p.ZScale), 2);
        Assert.Equal(3, p.MinConsecutive);
    }

    [Fact]
    public void RmadDefaults_MatchRmadParamsFromFallbacks_KeyForKey()
    {
        var fromTable = RmadParams.From(DetectorDefaults.Get("rmad")!);
        // An EMPTY params map exercises every RmadParams.From fallback literal.
        var fromFallbacks = RmadParams.From(new Dictionary<string, string>());

        Assert.Equal(fromFallbacks.Window, fromTable.Window);
        Assert.Equal(fromFallbacks.MinSamples, fromTable.MinSamples);
        Assert.Equal(fromFallbacks.ZScale, fromTable.ZScale);
        Assert.Equal(fromFallbacks.ScaleFloor, fromTable.ScaleFloor);
        Assert.Equal(fromFallbacks.HighThreshold, fromTable.HighThreshold);
        Assert.Equal(fromFallbacks.LowThreshold, fromTable.LowThreshold);
        Assert.Equal(fromFallbacks.MinConsecutive, fromTable.MinConsecutive);
        Assert.Equal(fromFallbacks.FrozenWindow, fromTable.FrozenWindow);
        Assert.Equal(fromFallbacks.FrozenVarianceThreshold, fromTable.FrozenVarianceThreshold);
    }

    /// <summary>
    /// D-H: frozen is disabled by variance, never by the window. A frozen_window of "0" makes
    /// FrozenSensorDetector.AddReading dequeue an empty queue on the very first reading, and
    /// InputValidator rejects it outright — so the default table must never carry it.
    /// </summary>
    [Fact]
    public void RmadDefaults_DisableFrozenByVariance_NotByWindow()
    {
        var defaults = DetectorDefaults.Get("rmad")!;

        Assert.Equal("0.0", defaults["frozen_variance_threshold"]);
        Assert.Equal("10", defaults["frozen_window"]);
    }

    [Fact]
    public void All_ContainsEveryKnownDetector()
    {
        var all = DetectorDefaults.All();

        Assert.Equal(
            new[] { "hst", "mad", "rmad", "stl" },
            all.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(DetectorDefaults.Get("rmad"), all["rmad"]);
    }
}
