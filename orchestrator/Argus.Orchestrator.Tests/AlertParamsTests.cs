using System.Globalization;
using Argus.Orchestrator.Config;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for AlertParams — the defaults table and its parsing contract.
/// </summary>
public class AlertParamsTests
{
    [Fact]
    public void From_EmptyDictionary_YieldsF13ValidatedDefaults()
    {
        // These fifteen numbers ARE the shipped gate. They were not picked for roundness: this
        // combination is the one measured to give 0 episodes on memory_use_percent and
        // zamrazarkapiwnica_power while keeping the two lodowka compressor runs (D-J). Changing
        // one of them here changes the field behaviour of every tracked sensor, so it has to
        // break a test.
        var p = AlertParams.From(new Dictionary<string, string>());

        Assert.Equal("adaptive", p.Mode);
        Assert.Equal("any", p.EvidenceMode);
        Assert.Equal(720, p.RankWindow);
        Assert.Equal(0.99, p.QFire);
        Assert.Equal(0.80, p.QClear);
        Assert.Equal(720, p.RawWindow);
        Assert.Equal(5.0, p.ZFire);
        Assert.Equal(3.0, p.ZClear);
        Assert.Equal(3, p.MinConsecutive);
        Assert.Equal(240, p.AlertMinSamples);
        Assert.Equal(120, p.MinDurationSec);
        Assert.Equal(600, p.RefractorySec);
        Assert.Equal(4, p.MaxEventsPerHour);
        Assert.Equal(21600, p.MaxEventDurationSec);
        Assert.Equal(3600, p.StormHoldSec);
    }

    [Fact]
    public void From_ReusesExistingMinConsecutiveKey_AndParsesInvariantCulture()
    {
        // min_consecutive is an EXISTING key shared with HstParams — the alert layer must read
        // the same one rather than introducing a second knob with the same meaning.
        //
        // The culture half is not theoretical: the operator's machine is pl-PL, where the
        // decimal separator is a comma. Parsing "0.995" under CurrentCulture yields 995, which
        // would sail past validation ((0,1) is checked in InputValidator, but a hand-edited
        // entities.yaml never goes through it) and silently disable the score channel.
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("pl-PL");

            var p = AlertParams.From(new Dictionary<string, string>
            {
                ["min_consecutive"] = "7",
                ["q_fire"] = "0.995",
                ["z_fire"] = "4.5",
            });

            Assert.Equal(7, p.MinConsecutive);
            Assert.Equal(0.995, p.QFire);
            Assert.Equal(4.5, p.ZFire);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void From_UnparsableValue_FallsBackToDefaultInsteadOfThrowing()
    {
        // A malformed params map must degrade, never throw: this map is read on the hot path
        // during entity-state construction, and an exception there takes the whole scoring
        // pipeline down over one bad character in a YAML file.
        var p = AlertParams.From(new Dictionary<string, string>
        {
            ["rank_window"] = "not-a-number",
            ["q_fire"] = "",
            ["alert_mode"] = "  LEGACY  ",
        });

        Assert.Equal(720, p.RankWindow);
        Assert.Equal(0.99, p.QFire);
        Assert.Equal("legacy", p.Mode);
    }
}
