using Argus.Orchestrator.Web;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for GET /api/detectors/defaults JSON endpoint logic (replaces the v3.0
/// /api/detectors/new-entry htmx fragment). Validates the default-parameter table
/// returned by DetectorDefaults.Get for hst/mad/stl and the unknown-name 400 path.
/// Fully offline — no HTTP server needed.
/// </summary>
public class DetectorEntryEndpointTests
{
    // -----------------------------------------------------------------------
    // HST defaults
    // -----------------------------------------------------------------------

    [Fact]
    public void Get_Hst_ReturnsSevenParams()
    {
        var defaults = DetectorDefaults.Get("hst");

        Assert.NotNull(defaults);
        Assert.Equal(7, defaults!.Count);
        Assert.True(defaults.ContainsKey("window"));
        Assert.True(defaults.ContainsKey("n_trees"));
        Assert.True(defaults.ContainsKey("high_threshold"));
        Assert.True(defaults.ContainsKey("low_threshold"));
        Assert.True(defaults.ContainsKey("min_consecutive"));
        Assert.True(defaults.ContainsKey("frozen_window"));
        Assert.True(defaults.ContainsKey("frozen_variance_threshold"));
    }

    [Fact]
    public void Get_Hst_ReturnsExactDefaultValues()
    {
        var defaults = DetectorDefaults.Get("hst")!;

        Assert.Equal("250", defaults["window"]);
        Assert.Equal("25", defaults["n_trees"]);
        Assert.Equal("0.7", defaults["high_threshold"]);
        Assert.Equal("0.3", defaults["low_threshold"]);
        Assert.Equal("3", defaults["min_consecutive"]);
        Assert.Equal("10", defaults["frozen_window"]);
        Assert.Equal("0.001", defaults["frozen_variance_threshold"]);
    }

    [Fact]
    public void Get_HstUppercase_IsCaseInsensitive()
    {
        var defaults = DetectorDefaults.Get("HST");

        Assert.NotNull(defaults);
        Assert.Equal("250", defaults!["window"]);
    }

    // -----------------------------------------------------------------------
    // MAD defaults
    // -----------------------------------------------------------------------

    [Fact]
    public void Get_Mad_ReturnsTwoParams()
    {
        var defaults = DetectorDefaults.Get("mad");

        Assert.NotNull(defaults);
        Assert.Equal(2, defaults!.Count);
        Assert.Equal("3.5", defaults["threshold"]);
        Assert.Equal("20", defaults["window"]);
        Assert.False(defaults.ContainsKey("n_trees"));
    }

    // -----------------------------------------------------------------------
    // STL defaults
    // -----------------------------------------------------------------------

    [Fact]
    public void Get_Stl_ReturnsThreeParams()
    {
        var defaults = DetectorDefaults.Get("stl");

        Assert.NotNull(defaults);
        Assert.Equal(3, defaults!.Count);
        Assert.Equal("24", defaults["period"]);
        Assert.Equal("7", defaults["seasonal"]);
        Assert.Equal("3.0", defaults["threshold"]);
    }

    // -----------------------------------------------------------------------
    // Unknown / empty name
    // -----------------------------------------------------------------------

    [Fact]
    public void Get_UnknownName_ReturnsNull()
    {
        Assert.Null(DetectorDefaults.Get("bogus"));
    }

    [Fact]
    public void Get_EmptyOrNullName_ReturnsNull()
    {
        Assert.Null(DetectorDefaults.Get(""));
        Assert.Null(DetectorDefaults.Get(null));
    }
}
