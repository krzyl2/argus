using Argus.Orchestrator.Config;
using Argus.Orchestrator.Web;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for GET /api/detectors/new-entry fragment endpoint logic.
/// Validates the HTML fragment returned by EntityPickerPage.BuildDetectorEntry
/// with HST defaults and correct name= indices (entity_idx + det_idx).
/// Fully offline — no HTTP server needed.
/// </summary>
public class DetectorEntryEndpointTests
{
    // -----------------------------------------------------------------------
    // Fragment shape and HST defaults
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildDetectorEntry_DefaultHstEntry_ContainsArgusDetectorEntryClass()
    {
        // Arrange: new-entry endpoint returns HST defaults at given indices
        var detector = new DetectorConfig { Name = "hst", Params = [] };

        // Act
        var html = EntityPickerPage.BuildDetectorEntry(0, 0, detector);

        Assert.Contains("argus-detector-entry", html);
    }

    [Fact]
    public void BuildDetectorEntry_WithEntityIdx2DetIdx3_RendersCorrectNameIndices()
    {
        // Arrange: the htmx endpoint passes entity_idx and det_idx as query params
        var detector = new DetectorConfig { Name = "hst", Params = [] };

        // Act
        var html = EntityPickerPage.BuildDetectorEntry(2, 3, detector);

        // Name attribute for the select
        Assert.Contains("detectors[2][3][name]", html);
        // Param field names
        Assert.Contains("detectors[2][3][params][window]", html);
        Assert.Contains("detectors[2][3][params][n_trees]", html);
    }

    [Fact]
    public void BuildDetectorEntry_NewHstEntry_RendersHstSelected()
    {
        // New entries from the endpoint default to HST selected
        var detector = new DetectorConfig { Name = "hst", Params = [] };

        var html = EntityPickerPage.BuildDetectorEntry(0, 0, detector);

        // HST option should be selected
        Assert.Contains("<option value=\"hst\" selected>", html.Replace(" selected>", " selected>"));
        // or any whitespace variant
        Assert.True(
            html.Contains("value=\"hst\" selected") || html.Contains("value=\"hst\"  selected"),
            $"Expected HST to be selected in: {html[..Math.Min(500, html.Length)]}");
    }

    [Fact]
    public void BuildDetectorEntry_NewHstEntry_RendersHstDefaults()
    {
        var detector = new DetectorConfig { Name = "hst", Params = [] };

        var html = EntityPickerPage.BuildDetectorEntry(0, 0, detector);

        // All 7 HST default param values rendered
        Assert.Contains("value=\"250\"", html);
        Assert.Contains("value=\"25\"", html);
        Assert.Contains("value=\"0.7\"", html);
        Assert.Contains("value=\"0.3\"", html);
        Assert.Contains("value=\"3\"", html);
        Assert.Contains("value=\"10\"", html);
        Assert.Contains("value=\"0.001\"", html);
    }

    [Fact]
    public void BuildDetectorEntry_NewHstEntry_RendersTimingCaptionStreaming()
    {
        var detector = new DetectorConfig { Name = "hst", Params = [] };

        var html = EntityPickerPage.BuildDetectorEntry(0, 0, detector);

        Assert.Contains("streaming (live, ~2 s reload)", html);
    }

    [Fact]
    public void BuildDetectorEntry_NewHstEntry_RendersRemoveButton()
    {
        var detector = new DetectorConfig { Name = "hst", Params = [] };

        var html = EntityPickerPage.BuildDetectorEntry(0, 0, detector);

        // Remove button must be type="button" to not submit form
        Assert.Contains("type=\"button\"", html);
        Assert.Contains("argus-btn--destructive-ghost", html);
    }
}
