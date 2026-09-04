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
        entityIds.Select(id => new HaSensorEntry(id, 0, null, null, false, null, "sensor")).ToList();

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
        // Include "*" makes the base set all sensors; exclude *test* then removes matches.
        // (An explicit include is required now that empty include = empty base set.)
        var snapshot = MakeSnapshot(
            "sensor.living_room_temp",
            "sensor.test_device",
            "sensor.kitchen_humidity");

        var result = GlobExpander.Resolve(
            snapshot,
            includePatterns: ["*"],
            excludePatterns: ["*test*"],
            manuallyChecked: [],
            manuallyUnchecked: []);

        Assert.Equal(2, result.Count);
        Assert.Contains("sensor.living_room_temp", result);
        Assert.Contains("sensor.kitchen_humidity", result);
        Assert.DoesNotContain("sensor.test_device", result);
    }

    [Fact]
    public void Resolve_NoIncludePatterns_NoCheckboxes_SelectsNothing()
    {
        // WHY: empty include patterns must NOT track every discovered sensor — that flooded HA
        // with hundreds of auto-created entities. With no patterns and no checkboxes, track nothing.
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

        Assert.Empty(result);
    }

    [Fact]
    public void Resolve_NoIncludePatterns_WithCheckboxes_SelectsOnlyChecked()
    {
        // WHY: checkbox-driven selection — with no include patterns, ONLY the manually-checked
        // entities are tracked (not the whole snapshot). This is the primary UI workflow.
        var snapshot = MakeSnapshot(
            "sensor.outdoor_temp",
            "sensor.indoor_humidity",
            "binary_sensor.motion");

        var result = GlobExpander.Resolve(
            snapshot,
            includePatterns: [],
            excludePatterns: [],
            manuallyChecked: ["sensor.outdoor_temp"],
            manuallyUnchecked: []);

        Assert.Single(result);
        Assert.Contains("sensor.outdoor_temp", result);
    }

    [Fact]
    public void Resolve_ManualCheckOverridesExclude()
    {
        // Include "*" selects both; exclude *test* removes sensor.test_device; manually
        // checking it adds it back (manual check overrides an exclusion).
        var snapshot = MakeSnapshot(
            "sensor.living_room_temp",
            "sensor.test_device");

        var result = GlobExpander.Resolve(
            snapshot,
            includePatterns: ["*"],
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
        // WHY: whitespace-only patterns must NOT be treated as literal patterns NOR as match-all.
        // They are ignored → empty base set, so only the manually-checked entity survives. If
        // whitespace were mistakenly a wildcard, indoor_humidity would leak in too.
        var snapshot = MakeSnapshot(
            "sensor.outdoor_temp",
            "sensor.indoor_humidity");

        var result = GlobExpander.Resolve(
            snapshot,
            includePatterns: ["", "   "],
            excludePatterns: ["", "  "],
            manuallyChecked: ["sensor.outdoor_temp"],
            manuallyUnchecked: []);

        Assert.Single(result);
        Assert.Contains("sensor.outdoor_temp", result);
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

    [Fact]
    public void Resolve_ManuallyChecked_ArbitraryIdNotInSnapshot_IsRejected()
    {
        // WR-03: an id submitted via form that does not exist in the live snapshot
        // must NOT be injected into entities.yaml
        var snapshot = MakeSnapshot("sensor.outdoor_temp", "sensor.indoor_humidity");

        // Check one real id alongside the fake so the assertion isn't vacuously true on an
        // empty result: the real id must survive, the fake must be dropped.
        var result = GlobExpander.Resolve(
            snapshot,
            includePatterns: [],
            excludePatterns: [],
            manuallyChecked: ["sensor.outdoor_temp", "sensor.injected_fake_entity"],
            manuallyUnchecked: []);

        Assert.DoesNotContain("sensor.injected_fake_entity", result);
        Assert.Contains("sensor.outdoor_temp", result);
    }
    // -----------------------------------------------------------------------
    // WS4/F9 — an already-tracked entity absent from the HA snapshot
    // -----------------------------------------------------------------------

    [Fact]
    public void Expand_TrackedGhostEntity_SurvivesSave()
    {
        // WHY (F9, close relative of G-14-1): sensor.zamrazarkapiwnica_power was in entities.yaml
        // and being scored, but absent from the HA snapshot — so `allIds.Contains(id)` rejected it
        // and EVERY save (including one triggered from the pattern textareas in Settings) silently
        // deleted it from the config. Data loss must not be a side effect of an unrelated save.
        var snapshot = MakeSnapshot("sensor.lodowkababcia_power");

        var result = GlobExpander.Resolve(
            snapshot,
            includePatterns: [],
            excludePatterns: [],
            manuallyChecked: ["sensor.lodowkababcia_power", "sensor.zamrazarkapiwnica_power"],
            manuallyUnchecked: [],
            currentlyTrackedIds: ["sensor.lodowkababcia_power", "sensor.zamrazarkapiwnica_power"]);

        Assert.Contains("sensor.zamrazarkapiwnica_power", result);
        Assert.Contains("sensor.lodowkababcia_power", result);

        // ...and unticking it must still work — the row is fully interactive, not merely visible.
        var afterUntick = GlobExpander.Resolve(
            snapshot,
            includePatterns: [],
            excludePatterns: [],
            manuallyChecked: ["sensor.lodowkababcia_power"],
            manuallyUnchecked: ["sensor.zamrazarkapiwnica_power"],
            currentlyTrackedIds: ["sensor.lodowkababcia_power", "sensor.zamrazarkapiwnica_power"]);

        Assert.DoesNotContain("sensor.zamrazarkapiwnica_power", afterUntick);
    }

    [Fact]
    public void Expand_ArbitraryUnknownId_StillRejected()
    {
        // WHY (WR-03): the F9 widening must not become "accept anything the form posts". Only ids
        // the orchestrator ALREADY tracks are added — an id that is in neither the snapshot nor the
        // live config is still refused, so a tampered POST cannot create entities.
        var snapshot = MakeSnapshot("sensor.lodowkababcia_power");

        var result = GlobExpander.Resolve(
            snapshot,
            includePatterns: [],
            excludePatterns: [],
            manuallyChecked: ["sensor.lodowkababcia_power", "sensor.not_a_real_entity", "'; DROP TABLE"],
            manuallyUnchecked: [],
            currentlyTrackedIds: ["sensor.lodowkababcia_power"]);

        Assert.Equal(["sensor.lodowkababcia_power"], result.ToList());
    }
}
