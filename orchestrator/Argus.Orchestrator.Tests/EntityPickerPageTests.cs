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
    public void BuildListFragment_TrackedEntry_RendersCheckedCheckboxAndTrackedPill()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.living_room_temp", isTracked: true));

        var html = EntityPickerPage.BuildListFragment(registry, q: "");

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

        var html = EntityPickerPage.BuildListFragment(registry, q: "");

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

        var html = EntityPickerPage.BuildListFragment(registry, q: "");

        Assert.Contains("argus-empty", html);
        Assert.Contains("No sensors found.", html);
    }

    [Fact]
    public void BuildListFragment_NoResultsForQuery_RendersNoResultsCopy()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.outdoor_temp"));

        var html = EntityPickerPage.BuildListFragment(registry, q: "zzz_no_match");

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

        var html = EntityPickerPage.BuildListFragment(registry, q: "");

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

        var html = EntityPickerPage.BuildListFragment(registry, q: "");

        Assert.DoesNotContain("<b>", html);
        Assert.Contains("&lt;b&gt;", html);
        Assert.Contains("&amp;", html);
    }

    [Fact]
    public void BuildListFragment_QueryWithHtml_IsHtmlEncodedInNoResultsCopy()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.outdoor_temp"));
        var maliciousQ = "<img src=x onerror=alert(1)>";

        var html = EntityPickerPage.BuildListFragment(registry, q: maliciousQ);

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

        var html = EntityPickerPage.BuildListFragment(registry, q: "");

        Assert.Contains("argus-row-friendly-name", html);
        Assert.Contains("Salon temperatura", html);
    }

    [Fact]
    public void BuildListFragment_FriendlyNameSameAsEntityId_IsNotRendered()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.temp", friendlyName: "sensor.temp"));

        var html = EntityPickerPage.BuildListFragment(registry, q: "");

        // When friendly_name == entity_id: friendly-name span must NOT appear
        Assert.DoesNotContain("argus-row-friendly-name", html);
    }

    [Fact]
    public void BuildListFragment_NullFriendlyName_IsNotRendered()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.outdoor_temp", friendlyName: null));

        var html = EntityPickerPage.BuildListFragment(registry, q: "");

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
        Assert.Contains("Changes take effect on the next pipeline cycle.", html);
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

        Assert.Contains("Configuration saved.", html);
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
}
