using Argus.Orchestrator.Config;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Health;
using Argus.Orchestrator.Web;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Unit tests for EntityPickerPage HTML builders.
/// All tests are fully offline — no HTTP server needed.
/// </summary>
public class EntityPickerPageTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private sealed class FakeRegistry : IHaSensorRegistry
    {
        private readonly IReadOnlyList<HaSensorEntry> _entries;
        public FakeRegistry(params HaSensorEntry[] entries) => _entries = entries;

        public IReadOnlyList<HaSensorEntry> GetAll() => _entries;
        public IReadOnlyList<HaSensorEntry> GetFiltered(string q) =>
            string.IsNullOrEmpty(q)
                ? _entries
                : _entries
                    .Where(e => e.EntityId.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .ToList();
        public void UpdateSnapshot(IReadOnlyList<HaStateDto> states, HashSet<string> trackedEntityIds)
            => throw new NotImplementedException();
    }

    private static HaSensorEntry MakeEntry(
        string entityId, double value = 21.0, string? unit = "°C",
        string? friendlyName = null, bool isTracked = false)
        => new(entityId, value, unit, friendlyName, isTracked);

    private static ArgusHealthSignals MakeHealth(bool detectorConnected = true)
    {
        var h = new ArgusHealthSignals();
        h.DetectorConnected = detectorConnected;
        return h;
    }

    // -----------------------------------------------------------------------
    // BuildListFragment: tracked pill
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildListFragment_WithRealConfig_PreservesDetectorDisclosurePanelsOnSearchRefresh()
    {
        // WR-01 regression: BuildListFragment must pass the real config so detector panels
        // are not lost when htmx does a search-refresh of #argus-sensor-list.
        var entry = MakeEntry("sensor.living_room_temp", isTracked: true);
        var registry = new FakeRegistry(entry);
        var config = new EntitiesConfig
        {
            Entities =
            [
                new EntityConfig
                {
                    EntityId = "sensor.living_room_temp",
                    FriendlyName = "",
                    Detectors = [new DetectorConfig { Name = "hst", Params = [] }]
                }
            ]
        };

        var html = EntityPickerPage.BuildListFragment(registry, config, q: "");

        // Detector disclosure section must be present (regression guard for WR-01)
        Assert.Contains("argus-detectors-details", html);
        Assert.Contains("argus-detector-entry", html);
        Assert.Contains("Detectors (1)", html);
    }

    [Fact]
    public void BuildListFragment_TrackedEntry_RendersCheckedCheckboxAndTrackedPill()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.living_room_temp", isTracked: true));

        var html = EntityPickerPage.BuildListFragment(registry, new EntitiesConfig(), q: "");

        // Checked checkbox
        Assert.Contains("checked", html);
        // Tracked pill
        Assert.Contains("argus-pill--tracked", html);
        Assert.Contains(">tracked<", html);
        // Row class
        Assert.Contains("argus-list-row--tracked", html);
    }

    [Fact]
    public void BuildListFragment_UntrackedEntry_RendersUncheckedCheckboxAndNoPill()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.outdoor_humidity", isTracked: false));

        var html = EntityPickerPage.BuildListFragment(registry, new EntitiesConfig(), q: "");

        // No checked attribute
        Assert.DoesNotContain(" checked", html);
        // No tracked pill
        Assert.DoesNotContain("argus-pill--tracked", html);
        Assert.DoesNotContain(">tracked<", html);
        // No tracked row class
        Assert.DoesNotContain("argus-list-row--tracked", html);
    }

    // -----------------------------------------------------------------------
    // BuildListFragment: empty state
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildListFragment_EmptySnapshot_RendersEmptyState()
    {
        var registry = new FakeRegistry();

        var html = EntityPickerPage.BuildListFragment(registry, new EntitiesConfig(), q: "");

        Assert.Contains("argus-empty", html);
        Assert.Contains("No sensors found.", html);
    }

    [Fact]
    public void BuildListFragment_NoResultsForQuery_RendersNoResultsCopy()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.outdoor_temp"));

        var html = EntityPickerPage.BuildListFragment(registry, new EntitiesConfig(), q: "zzz_no_match");

        Assert.Contains("argus-empty", html);
        Assert.Contains("zzz_no_match", html);
        Assert.Contains("Try a different search term", html);
    }

    // -----------------------------------------------------------------------
    // BuildListFragment: XSS encoding — T-02-07 regression
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildListFragment_EntityIdWithScriptTag_IsHtmlEncoded()
    {
        // T-02-07 stored-XSS regression: entity_id containing HTML/script must be encoded
        var maliciousId = "sensor.<script>alert(1)</script>";
        var registry = new FakeRegistry(MakeEntry(maliciousId));

        var html = EntityPickerPage.BuildListFragment(registry, new EntitiesConfig(), q: "");

        // The raw script tag MUST NOT appear in the output
        Assert.DoesNotContain("<script>", html);
        // The encoded form MUST appear instead
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void BuildListFragment_FriendlyNameWithHtml_IsHtmlEncoded()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.test", friendlyName: "<b>Bold & Name</b>"));

        var html = EntityPickerPage.BuildListFragment(registry, new EntitiesConfig(), q: "");

        Assert.DoesNotContain("<b>", html);
        Assert.Contains("&lt;b&gt;", html);
        Assert.Contains("&amp;", html);
    }

    [Fact]
    public void BuildListFragment_QueryWithHtml_IsHtmlEncodedInNoResultsCopy()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.outdoor_temp"));
        var maliciousQ = "<img src=x onerror=alert(1)>";

        var html = EntityPickerPage.BuildListFragment(registry, new EntitiesConfig(), q: maliciousQ);

        // No-results path with encoded query
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;img", html);
    }

    // -----------------------------------------------------------------------
    // BuildListFragment: friendly name rendering
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildListFragment_FriendlyNameDiffersFromEntityId_IsRendered()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.salon_temperatura", friendlyName: "Salon temperatura"));

        var html = EntityPickerPage.BuildListFragment(registry, new EntitiesConfig(), q: "");

        Assert.Contains("argus-row-friendly-name", html);
        Assert.Contains("Salon temperatura", html);
    }

    [Fact]
    public void BuildListFragment_FriendlyNameSameAsEntityId_IsNotRendered()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.temp", friendlyName: "sensor.temp"));

        var html = EntityPickerPage.BuildListFragment(registry, new EntitiesConfig(), q: "");

        // When friendly_name == entity_id: friendly-name span must NOT appear
        Assert.DoesNotContain("argus-row-friendly-name", html);
    }

    [Fact]
    public void BuildListFragment_NullFriendlyName_IsNotRendered()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.outdoor_temp", friendlyName: null));

        var html = EntityPickerPage.BuildListFragment(registry, new EntitiesConfig(), q: "");

        Assert.DoesNotContain("argus-row-friendly-name", html);
    }

    // -----------------------------------------------------------------------
    // BuildFullPage: structure and critical attributes
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildFullPage_ContainsExpectedPageStructure()
    {
        var registry = new FakeRegistry();
        var config = new EntitiesConfig();
        var health = MakeHealth();

        var html = EntityPickerPage.BuildFullPage("/ingress/abc", registry, config, health, "");

        Assert.Contains("Entity Selection", html);
        Assert.Contains("Select the sensors Argus monitors and assign detectors to each.", html);
        Assert.Contains("argus-picker-form", html);
        Assert.Contains("argus-sensor-list", html);
        Assert.Contains("argus-flash", html);
        Assert.Contains("argus-spinner", html);
        Assert.Contains("Save configuration", html);
        Assert.Contains("Pattern Filters", html);
        // htmx attributes
        Assert.Contains("hx-post", html);
        Assert.Contains("hx-indicator=\"#argus-spinner\"", html);
    }

    [Fact]
    public void BuildFullPage_IngressPathIsHtmlEncoded()
    {
        var registry = new FakeRegistry();
        var config = new EntitiesConfig();
        var health = MakeHealth();

        var html = EntityPickerPage.BuildFullPage("/ingress/<bad>", registry, config, health, "");

        Assert.DoesNotContain("/ingress/<bad>", html);
        Assert.Contains("/ingress/&lt;bad&gt;", html);
    }

    // -----------------------------------------------------------------------
    // BuildSuccessBanner / BuildErrorBanner
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildSuccessBanner_ContainsCount()
    {
        var html = EntityPickerPage.BuildSuccessBanner(5);

        Assert.Contains("Saved — pipeline active.", html);
        Assert.Contains("5", html);
        Assert.Contains("argus-banner--success", html);
        Assert.Contains("role=\"status\"", html);
        Assert.Contains("aria-live=\"polite\"", html);
    }

    [Fact]
    public void BuildErrorBanner_ReasonIsHtmlEncoded()
    {
        var html = EntityPickerPage.BuildErrorBanner("<disk full>");

        Assert.DoesNotContain("<disk full>", html);
        Assert.Contains("&lt;disk full&gt;", html);
        Assert.Contains("argus-banner--error", html);
        Assert.Contains("role=\"alert\"", html);
        Assert.Contains("aria-live=\"assertive\"", html);
    }

    // -----------------------------------------------------------------------
    // Phase 3: Detector disclosure rows — BuildFullPage + BuildDetectorEntry
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildFullPage_TrackedEntityWithHstDetector_RendersDisclosureSection()
    {
        // Arrange: tracked entity with one saved HST detector
        var entry = MakeEntry("sensor.living_room_temp", isTracked: true);
        var registry = new FakeRegistry(entry);
        var config = new EntitiesConfig
        {
            Entities =
            [
                new EntityConfig
                {
                    EntityId = "sensor.living_room_temp",
                    FriendlyName = "",
                    Detectors = [new DetectorConfig { Name = "hst", Params = [] }]
                }
            ]
        };
        var health = MakeHealth();

        var html = EntityPickerPage.BuildFullPage("/ingress/abc", registry, config, health, "");

        // Disclosure section present for tracked entity
        Assert.Contains("argus-detectors-details", html);
        Assert.Contains("argus-disclosure-toggle", html);
        // Summary shows count of 1
        Assert.Contains("Detectors (1)", html);
        // Detector entry
        Assert.Contains("argus-detector-entry", html);
    }

    [Fact]
    public void BuildFullPage_TrackedEntityWithTwoDetectors_RendersTwoEntriesWithCorrectIndices()
    {
        // Arrange: tracked entity with two detectors (HST + MAD)
        var entry = MakeEntry("sensor.outdoor_humidity", isTracked: true);
        var registry = new FakeRegistry(entry);
        var config = new EntitiesConfig
        {
            Entities =
            [
                new EntityConfig
                {
                    EntityId = "sensor.outdoor_humidity",
                    FriendlyName = "",
                    Detectors =
                    [
                        new DetectorConfig { Name = "hst", Params = [] },
                        new DetectorConfig { Name = "mad", Params = [] }
                    ]
                }
            ]
        };
        var health = MakeHealth();

        var html = EntityPickerPage.BuildFullPage("/ingress/abc", registry, config, health, "");

        // Summary shows count of 2
        Assert.Contains("Detectors (2)", html);
        // Two detector entries present
        var count = System.Text.RegularExpressions.Regex.Matches(html, "argus-detector-entry").Count;
        Assert.True(count >= 2, $"Expected at least 2 argus-detector-entry blocks but found {count}");
        // First detector: index [0][0], second: [0][1]
        Assert.Contains("detectors[0][0][name]", html);
        Assert.Contains("detectors[0][1][name]", html);
    }

    [Fact]
    public void BuildFullPage_UntrackedEntity_RendersNoDisclosureSection()
    {
        // Arrange: untracked entity — Phase 2 shape unchanged
        var entry = MakeEntry("sensor.test_sensor", isTracked: false);
        var registry = new FakeRegistry(entry);
        var config = new EntitiesConfig();
        var health = MakeHealth();

        var html = EntityPickerPage.BuildFullPage("/ingress/abc", registry, config, health, "");

        // No disclosure section for untracked rows
        Assert.DoesNotContain("argus-detectors-details", html);
        Assert.DoesNotContain("argus-detector-entry", html);
    }

    [Fact]
    public void BuildDetectorEntry_HstType_RendersSevenParamFields()
    {
        // Arrange: HST detector with default params
        var detector = new DetectorConfig { Name = "hst", Params = [] };

        var html = EntityPickerPage.BuildDetectorEntry(0, 0, detector);

        // HST must render 7 param fields
        Assert.Contains("detectors[0][0][params][window]", html);
        Assert.Contains("detectors[0][0][params][n_trees]", html);
        Assert.Contains("detectors[0][0][params][high_threshold]", html);
        Assert.Contains("detectors[0][0][params][low_threshold]", html);
        Assert.Contains("detectors[0][0][params][min_consecutive]", html);
        Assert.Contains("detectors[0][0][params][frozen_window]", html);
        Assert.Contains("detectors[0][0][params][frozen_variance_threshold]", html);
        // Timing caption
        Assert.Contains("streaming (live, ~2 s reload)", html);
        // Name select
        Assert.Contains("detectors[0][0][name]", html);
    }

    [Fact]
    public void BuildDetectorEntry_HstType_PreFillsDefaultsWhenParamsEmpty()
    {
        var detector = new DetectorConfig { Name = "hst", Params = [] };

        var html = EntityPickerPage.BuildDetectorEntry(0, 0, detector);

        // Default values rendered in inputs
        Assert.Contains("value=\"250\"", html);   // window
        Assert.Contains("value=\"25\"", html);    // n_trees
        Assert.Contains("value=\"0.7\"", html);   // high_threshold
        Assert.Contains("value=\"0.3\"", html);   // low_threshold
        Assert.Contains("value=\"3\"", html);     // min_consecutive
        Assert.Contains("value=\"10\"", html);    // frozen_window
        Assert.Contains("value=\"0.001\"", html); // frozen_variance_threshold
    }

    [Fact]
    public void BuildDetectorEntry_HstType_PreFillsStoredParamOverDefault()
    {
        // If a param value is stored in the entity config, it overrides the default
        var detector = new DetectorConfig
        {
            Name = "hst",
            Params = new Dictionary<string, string> { ["window"] = "500" }
        };

        var html = EntityPickerPage.BuildDetectorEntry(0, 0, detector);

        Assert.Contains("value=\"500\"", html);   // stored override
    }

    [Fact]
    public void BuildDetectorEntry_MadType_RendersTwoParamFields()
    {
        var detector = new DetectorConfig { Name = "mad", Params = [] };

        var html = EntityPickerPage.BuildDetectorEntry(1, 0, detector);

        Assert.Contains("detectors[1][0][params][threshold]", html);
        Assert.Contains("detectors[1][0][params][window]", html);
        Assert.Contains("batch (runs every N min)", html);
        // MAD should NOT contain HST-specific fields
        Assert.DoesNotContain("detectors[1][0][params][n_trees]", html);
    }

    [Fact]
    public void BuildDetectorEntry_StlType_RendersThreeParamFields()
    {
        var detector = new DetectorConfig { Name = "stl", Params = [] };

        var html = EntityPickerPage.BuildDetectorEntry(0, 2, detector);

        Assert.Contains("detectors[0][2][params][period]", html);
        Assert.Contains("detectors[0][2][params][seasonal]", html);
        Assert.Contains("detectors[0][2][params][threshold]", html);
        Assert.Contains("batch (runs every N min)", html);
    }

    [Fact]
    public void BuildDetectorEntry_ParamValueContainingScript_IsHtmlEncoded()
    {
        // T-03-11: stored XSS defense — param values must be HTML-encoded
        var detector = new DetectorConfig
        {
            Name = "hst",
            Params = new Dictionary<string, string> { ["window"] = "<script>alert(1)</script>" }
        };

        var html = EntityPickerPage.BuildDetectorEntry(0, 0, detector);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void BuildDetectorEntry_DetectorNameIsHtmlEncoded()
    {
        // T-03-11: detector Name in select element must be HTML-encoded
        var detector = new DetectorConfig { Name = "<evil>", Params = [] };

        var html = EntityPickerPage.BuildDetectorEntry(0, 0, detector);

        Assert.DoesNotContain("<evil>", html);
    }

    [Fact]
    public void BuildDetectorEntry_RendersRemoveButton()
    {
        var detector = new DetectorConfig { Name = "hst", Params = [] };

        var html = EntityPickerPage.BuildDetectorEntry(0, 0, detector);

        Assert.Contains("type=\"button\"", html);
        Assert.Contains("argus-btn--destructive-ghost", html);
        Assert.Contains(".argus-detector-entry", html); // inline onclick reference
    }

    [Fact]
    public void BuildFullPage_TrackedEntityWithNoSavedDetectors_RendersDefaultHstEntry()
    {
        // When an entity is tracked but has no detectors saved (first-time), render a default HST entry
        var entry = MakeEntry("sensor.new_entity", isTracked: true);
        var registry = new FakeRegistry(entry);
        // Entity in config with empty detectors list
        var config = new EntitiesConfig
        {
            Entities =
            [
                new EntityConfig
                {
                    EntityId = "sensor.new_entity",
                    FriendlyName = "",
                    Detectors = []
                }
            ]
        };
        var health = MakeHealth();

        var html = EntityPickerPage.BuildFullPage("/ingress/abc", registry, config, health, "");

        // Should still render a detector entry (default HST)
        Assert.Contains("argus-detector-entry", html);
        // HST timing caption should appear
        Assert.Contains("streaming (live, ~2 s reload)", html);
    }

    [Fact]
    public void BuildFullPage_TrackedEntityNotInConfig_RendersDefaultHstEntry()
    {
        // When a tracked entity has no entry in config at all, render default HST
        var entry = MakeEntry("sensor.brand_new", isTracked: true);
        var registry = new FakeRegistry(entry);
        var config = new EntitiesConfig(); // empty — no entities at all
        var health = MakeHealth();

        var html = EntityPickerPage.BuildFullPage("/ingress/abc", registry, config, health, "");

        Assert.Contains("argus-detector-entry", html);
        Assert.Contains("streaming (live, ~2 s reload)", html);
    }

    [Fact]
    public void BuildFullPage_PageSubheadingUpdated()
    {
        // Plan requires subheading to read "Select the sensors Argus monitors and assign detectors to each."
        var registry = new FakeRegistry();
        var config = new EntitiesConfig();
        var health = MakeHealth();

        var html = EntityPickerPage.BuildFullPage("/ingress/abc", registry, config, health, "");

        Assert.Contains("Select the sensors Argus monitors and assign detectors to each.", html);
    }

    [Fact]
    public void BuildReloadingBanner_ContainsExpectedCopy()
    {
        var html = EntityPickerPage.BuildReloadingBanner(3);

        Assert.Contains("argus-banner--reloading", html);
        Assert.Contains("role=\"status\"", html);
        Assert.Contains("aria-live=\"polite\"", html);
        Assert.Contains("Saved — pipeline reloading", html);
    }

    // -----------------------------------------------------------------------
    // Phase 4: BuildValidationBanner
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildValidationBanner_ContainsErrorCountAndBannerClass()
    {
        var html = EntityPickerPage.BuildValidationBanner(2);

        Assert.Contains("Save blocked: 2 field(s)", html);
        Assert.Contains("argus-banner--validation", html);
        Assert.Contains("role=\"alert\"", html);
        Assert.Contains("aria-live=\"assertive\"", html);
        Assert.Contains("Correct the highlighted fields and try again.", html);
    }

    [Fact]
    public void BuildValidationBanner_SingleError_ContainsCount1()
    {
        var html = EntityPickerPage.BuildValidationBanner(1);

        Assert.Contains("Save blocked: 1 field(s)", html);
        Assert.Contains("argus-banner--validation", html);
    }

    // -----------------------------------------------------------------------
    // Phase 4: BuildSuccessBanner — warm-up disclosure
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildSuccessBanner_HasHstFalse_DoesNotContainWarmupNote()
    {
        var html = EntityPickerPage.BuildSuccessBanner(3, hasHst: false);

        Assert.Contains("argus-banner--success", html);
        Assert.Contains("3", html);
        Assert.DoesNotContain("argus-warmup-note", html);
        Assert.DoesNotContain("warm up", html);
    }

    [Fact]
    public void BuildSuccessBanner_HasHstTrue_ContainsWarmupNote()
    {
        var html = EntityPickerPage.BuildSuccessBanner(3, hasHst: true);

        Assert.Contains("argus-banner--success", html);
        Assert.Contains("argus-warmup-note", html);
        Assert.Contains("HST detectors need ~4 minutes", html);
        Assert.Contains("window=250 at ~1 reading/s", html);
        Assert.Contains("warm-up completes", html);
    }

    [Fact]
    public void BuildSuccessBanner_DefaultHasHstIsFalse_NoWarmupNote()
    {
        // Existing single-arg call site still compiles and produces no warm-up note
        var html = EntityPickerPage.BuildSuccessBanner(5);

        Assert.Contains("argus-banner--success", html);
        Assert.DoesNotContain("argus-warmup-note", html);
    }

    // -----------------------------------------------------------------------
    // Phase 4: Inline validation JS in BuildFullPage
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildFullPage_ContainsInlineValidationScript()
    {
        var registry = new FakeRegistry();
        var config = new EntitiesConfig();
        var health = MakeHealth();

        var html = EntityPickerPage.BuildFullPage("/ingress/abc", registry, config, health, "");

        // Validation param rules must be present (PR = shorthand for PARAM_RULES in minified script)
        Assert.Contains("var PR=", html);
        // Event delegation on form
        Assert.Contains("argus-picker-form", html);
        Assert.Contains("focusout", html);
        Assert.Contains("submit", html);
        // Validation script must be inline — PR (param rules) is embedded directly in the page body
        // (htmx is loaded via src=, but the validation script must not have a src attribute)
        var prIdx = html.IndexOf("var PR=", StringComparison.Ordinal);
        Assert.True(prIdx > 0, "var PR= (validation rules) not found in page");
        // Ensure it is not in a script[src] element — look backwards for the opening script tag
        var scriptOpenBefore = html.LastIndexOf("<script", prIdx, StringComparison.Ordinal);
        Assert.True(scriptOpenBefore >= 0, "No preceding <script> tag found");
        var scriptTagContent = html.Substring(scriptOpenBefore, prIdx - scriptOpenBefore);
        Assert.DoesNotContain("src=", scriptTagContent);
    }

    [Fact]
    public void BuildFullPage_ParamInputs_HaveAriaDescribedByAndAriaInvalid()
    {
        var entry = MakeEntry("sensor.test_sensor", isTracked: true);
        var registry = new FakeRegistry(entry);
        var config = new EntitiesConfig();
        var health = MakeHealth();

        var html = EntityPickerPage.BuildFullPage("/ingress/abc", registry, config, health, "");

        // ARIA attributes must be present on param inputs (migrated id format)
        Assert.Contains("aria-describedby=\"param-", html);
        Assert.Contains("aria-invalid=\"false\"", html);
        // Error spans must be present
        Assert.Contains("argus-param-field__error-msg", html);
    }
}
