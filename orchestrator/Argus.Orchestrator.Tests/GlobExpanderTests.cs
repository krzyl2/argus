using Argus.Orchestrator.Config;
using Argus.Orchestrator.Ha;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for GlobExpander.Resolve — the authoritative CONTEXT combine model.
/// All tests are fully offline and pure-static (no DI, no HA connection).
/// </summary>
public class GlobExpanderTests
{
    private static IReadOnlyList<HaSensorEntry> MakeSnapshot(params string[] entityIds) =>
        entityIds.Select(id => new HaSensorEntry(id, 0, null, null, false)).ToList();

    [Fact]
    public void Resolve_IncludePattern_SelectsMatchingEntities()
    {
        // Only sensor.*temp* should be selected
        var snapshot = MakeSnapshot(
            "sensor.living_room_temp",
            "sensor.outdoor_humidity",
            "sensor.kitchen_temp");

        var result = GlobExpander.Resolve(
            snapshot,
            includePatterns: ["sensor.*temp*"],
            excludePatterns: [],
            manuallyChecked: [],
            manuallyUnchecked: []);

        Assert.Equal(2, result.Count);
        Assert.Contains("sensor.living_room_temp", result);
        Assert.Contains("sensor.kitchen_temp", result);
        Assert.DoesNotContain("sensor.outdoor_humidity", result);
    }

    [Fact]
    public void Resolve_ExcludePattern_RemovesMatchingEntities()
    {
        // All sensors minus those matching *test*
        var snapshot = MakeSnapshot(
            "sensor.living_room_temp",
            "sensor.test_device",
            "sensor.kitchen_humidity");

        var result = GlobExpander.Resolve(
            snapshot,
            includePatterns: [],
            excludePatterns: ["*test*"],
            manuallyChecked: [],
            manuallyUnchecked: []);

        Assert.Equal(2, result.Count);
        Assert.Contains("sensor.living_room_temp", result);
        Assert.Contains("sensor.kitchen_humidity", result);
        Assert.DoesNotContain("sensor.test_device", result);
    }

    [Fact]
    public void Resolve_NoIncludePatterns_AllEntitiesAreBase()
    {
        // When no include patterns: all snapshot entities form the base candidate set
        var snapshot = MakeSnapshot(
            "sensor.outdoor_temp",
            "sensor.indoor_humidity",
            "binary_sensor.motion");

        var result = GlobExpander.Resolve(
            snapshot,
            includePatterns: [],
            excludePatterns: [],
            manuallyChecked: [],
            manuallyUnchecked: []);

        Assert.Equal(3, result.Count);
        Assert.Contains("sensor.outdoor_temp", result);
        Assert.Contains("sensor.indoor_humidity", result);
        Assert.Contains("binary_sensor.motion", result);
    }

    [Fact]
    public void Resolve_ManualCheckOverridesExclude()
    {
        // An exclude pattern removes sensor.test_device, but manually checking it adds it back
        var snapshot = MakeSnapshot(
            "sensor.living_room_temp",
            "sensor.test_device");

        var result = GlobExpander.Resolve(
            snapshot,
            includePatterns: [],
            excludePatterns: ["*test*"],
            manuallyChecked: ["sensor.test_device"],
            manuallyUnchecked: []);

        Assert.Contains("sensor.test_device", result);
        Assert.Contains("sensor.living_room_temp", result);
    }

    [Fact]
    public void Resolve_ManualUncheckOverridesInclude()
    {
        // An include pattern selects sensor.living_room_temp, but manually unchecking removes it (applied last)
        var snapshot = MakeSnapshot(
            "sensor.living_room_temp",
            "sensor.outdoor_humidity");

        var result = GlobExpander.Resolve(
            snapshot,
            includePatterns: ["sensor.*temp*"],
            excludePatterns: [],
            manuallyChecked: [],
            manuallyUnchecked: ["sensor.living_room_temp"]);

        Assert.DoesNotContain("sensor.living_room_temp", result);
        Assert.DoesNotContain("sensor.outdoor_humidity", result);
    }

    [Fact]
    public void Resolve_CaseInsensitiveMatch()
    {
        // Pattern "sensor.*TEMP*" must match lowercase "sensor.living_room_temp"
        var snapshot = MakeSnapshot(
            "sensor.living_room_temp",
            "sensor.outdoor_humidity");

        var result = GlobExpander.Resolve(
            snapshot,
            includePatterns: ["sensor.*TEMP*"],
            excludePatterns: [],
            manuallyChecked: [],
            manuallyUnchecked: []);

        Assert.Single(result);
        Assert.Contains("sensor.living_room_temp", result);
    }

    [Fact]
    public void Resolve_EmptyWhitespacePatterns_AreIgnored()
    {
        // Empty and whitespace-only patterns must NOT be treated as match-all wildcards
        var snapshot = MakeSnapshot(
            "sensor.outdoor_temp",
            "sensor.indoor_humidity");

        // With only whitespace include patterns — treated as NO include patterns → all entities base
        var result = GlobExpander.Resolve(
            snapshot,
            includePatterns: ["", "   "],
            excludePatterns: ["", "  "],
            manuallyChecked: [],
            manuallyUnchecked: []);

        // Whitespace includes are ignored, so all entities are the base set
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Resolve_ManualUncheckBeatsManualCheck_WhenBothApply()
    {
        // If an id appears in both manuallyChecked and manuallyUnchecked,
        // manuallyUnchecked wins (it is applied LAST)
        var snapshot = MakeSnapshot("sensor.outdoor_temp");

        var result = GlobExpander.Resolve(
            snapshot,
            includePatterns: [],
            excludePatterns: [],
            manuallyChecked: ["sensor.outdoor_temp"],
            manuallyUnchecked: ["sensor.outdoor_temp"]);

        Assert.DoesNotContain("sensor.outdoor_temp", result);
    }
}
