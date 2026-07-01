using Argus.Orchestrator.Config;
using Argus.Orchestrator.Web;
using Microsoft.Extensions.Logging.Abstractions;
#pragma warning disable CS8602 // Tests explicitly construct valid objects
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for the indexed detector form field parser and extended save handler logic.
/// Validates CFG-03: indexed detector fields → EntityConfig.Detectors, multi-detector
/// round-trip, empty-list HST default, and entity-index correlation stability.
///
/// Fully offline — no HTTP server required. The parser logic is extracted to
/// DetectorFieldParser (internal static) for direct unit testing.
/// </summary>
public class SaveEndpointDetectorParsingTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string WriteTempYaml(string content)
    {
        var path = Path.GetTempFileName() + ".yaml";
        File.WriteAllText(path, content);
        return path;
    }

    private static string BuildCombinedYaml(
        IEnumerable<string> include,
        IEnumerable<string> exclude,
        IEnumerable<EntityConfig> entities)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var patternsMap = new Dictionary<string, object>
        {
            ["include"] = include.ToList(),
            ["exclude"] = exclude.ToList(),
        };

        var root = new Dictionary<string, object>
        {
            ["_patterns"] = patternsMap,
            ["entities"] = entities.ToList(),
        };

        return serializer.Serialize(root);
    }

    // -----------------------------------------------------------------------
    // DetectorFieldParser.Parse — indexed form field parsing
    // -----------------------------------------------------------------------

    [Fact]
    public void Parse_SingleDetectorSingleEntity_ReturnsSingleDetectorConfig()
    {
        // Arrange: form keys for entity 0, detector 0 → HST with defaults
        var formKeys = new Dictionary<string, string>
        {
            ["detectors[0][0][name]"] = "hst",
            ["detectors[0][0][params][window]"] = "250",
            ["detectors[0][0][params][n_trees]"] = "25",
        };

        // Act
        var result = DetectorFieldParser.Parse(formKeys);

        // Assert: entity index 0 → one detector
        Assert.True(result.ContainsKey(0), "Expected detector entry for entity index 0");
        Assert.Single(result[0]);
        Assert.Equal("hst", result[0][0].Name);
        Assert.Equal("250", result[0][0].Params["window"]);
        Assert.Equal("25", result[0][0].Params["n_trees"]);
    }

    [Fact]
    public void Parse_MultipleDetectorsSameEntity_ReturnsMultipleDetectorsInOrder()
    {
        // Arrange: entity 0 with HST (det 0) + MAD (det 1)
        var formKeys = new Dictionary<string, string>
        {
            ["detectors[0][0][name]"] = "hst",
            ["detectors[0][0][params][window]"] = "250",
            ["detectors[0][1][name]"] = "mad",
            ["detectors[0][1][params][threshold]"] = "3.5",
        };

        // Act
        var result = DetectorFieldParser.Parse(formKeys);

        // Assert
        Assert.Equal(2, result[0].Count);
        Assert.Equal("hst", result[0][0].Name);
        Assert.Equal("mad", result[0][1].Name);
        Assert.Equal("3.5", result[0][1].Params["threshold"]);
    }

    [Fact]
    public void Parse_TwoEntitiesWithDetectors_ReturnsTwoEntityEntries()
    {
        // Arrange: entity 0 → HST, entity 1 → STL
        var formKeys = new Dictionary<string, string>
        {
            ["detectors[0][0][name]"] = "hst",
            ["detectors[0][0][params][window]"] = "300",
            ["detectors[1][0][name]"] = "stl",
            ["detectors[1][0][params][period]"] = "24",
        };

        // Act
        var result = DetectorFieldParser.Parse(formKeys);

        Assert.Equal(2, result.Count);
        Assert.Equal("hst", result[0][0].Name);
        Assert.Equal("300", result[0][0].Params["window"]);
        Assert.Equal("stl", result[1][0].Name);
        Assert.Equal("24", result[1][0].Params["period"]);
    }

    [Fact]
    public void Parse_EmptyFormKeys_ReturnsEmptyDictionary()
    {
        // Arrange: no detector fields submitted
        var formKeys = new Dictionary<string, string>();

        // Act
        var result = DetectorFieldParser.Parse(formKeys);

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_NonDetectorKeys_AreIgnored()
    {
        // Arrange: form has entity checkboxes but no detector fields
        var formKeys = new Dictionary<string, string>
        {
            ["entities"] = "sensor.temp",
            ["include_patterns"] = "sensor.*",
        };

        // Act
        var result = DetectorFieldParser.Parse(formKeys);

        Assert.Empty(result);
    }

    // -----------------------------------------------------------------------
    // WR-03: int.TryParse — overflow / malformed index skip
    // -----------------------------------------------------------------------

    [Fact]
    public void Parse_OverflowingEntityIndex_IsSkippedNotThrown()
    {
        // WR-03: crafted input with ei > int.MaxValue must be silently skipped,
        // not throw OverflowException. Valid fields in the same POST must still parse.
        var formKeys = new Dictionary<string, string>
        {
            ["detectors[2147483648][0][name]"] = "hst",   // ei overflows int — skip
            ["detectors[0][0][name]"]          = "mad",   // valid — must survive
        };

        // Must not throw; overflow key silently ignored
        var result = DetectorFieldParser.Parse(formKeys);

        // The overflow key produces no entry; result should have exactly 1 entry (key=0)
        Assert.Single(result);
        Assert.True(result.ContainsKey(0), "Valid key must still parse");
        Assert.Equal("mad", result[0][0].Name);
    }

    [Fact]
    public void Parse_OverflowingDetectorIndex_IsSkippedNotThrown()
    {
        // WR-03: crafted input with di > int.MaxValue must be silently skipped.
        var formKeys = new Dictionary<string, string>
        {
            ["detectors[0][2147483648][name]"] = "hst",  // di overflows int — skip
            ["detectors[0][0][name]"]          = "stl",  // valid
        };

        var result = DetectorFieldParser.Parse(formKeys);

        Assert.True(result.ContainsKey(0));
        Assert.Single(result[0]);   // only the valid di=0 entry
        Assert.Equal("stl", result[0][0].Name);
    }

    // -----------------------------------------------------------------------
    // Entity index correlation (Pitfall 5 / CFG-03-critical)
    // -----------------------------------------------------------------------

    [Fact]
    public void Correlate_TwoEntitiesAlphabetical_DetectorIdx0MapsToFirstEntityAlpha()
    {
        // The canonical order is alphabetical by EntityId (same order as BuildFullPage renders).
        // detectors[0][...] must map to the FIRST entity alphabetically.
        // Arrange: two entity IDs submitted in reverse alpha order
        var submittedIds = new List<string> { "sensor.z_sensor", "sensor.a_sensor" };
        var sortedIds = submittedIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();

        // Detector fields indexed in the correlation order (NOT submission order)
        var parsedDetectors = new Dictionary<int, List<DetectorConfig>>
        {
            [0] = [new DetectorConfig { Name = "hst", Params = [] }],
            [1] = [new DetectorConfig { Name = "mad", Params = [] }],
        };

        // Act: correlate positionally with sorted entity IDs
        var entityConfigs = sortedIds
            .Select((id, i) => new EntityConfig
            {
                EntityId = id,
                FriendlyName = "",
                Detectors = parsedDetectors.TryGetValue(i, out var dets)
                    ? dets
                    : [new DetectorConfig { Name = "hst", Params = [] }],
            })
            .ToList();

        // Assert: first alphabetically (sensor.a_sensor) gets detectors[0] = hst
        var aEntity = entityConfigs.First(e => e.EntityId == "sensor.a_sensor");
        var zEntity = entityConfigs.First(e => e.EntityId == "sensor.z_sensor");
        Assert.Equal("hst", aEntity.Detectors[0].Name);
        Assert.Equal("mad", zEntity.Detectors[0].Name);
    }

    [Fact]
    public void Correlate_NonContiguousCheckedEntities_CorrelationIsStable()
    {
        // Pitfall 5: entity_idx must be stable regardless of which entities are checked.
        // If only entities B and D are checked (out of A, B, C, D), B maps to ei=0, D to ei=1.
        var submittedIds = new List<string> { "sensor.b_sensor", "sensor.d_sensor" };
        var sortedIds = submittedIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();

        var parsedDetectors = new Dictionary<int, List<DetectorConfig>>
        {
            [0] = [new DetectorConfig { Name = "hst", Params = new Dictionary<string, string> { ["window"] = "100" } }],
            [1] = [new DetectorConfig { Name = "stl", Params = [] }],
        };

        var entityConfigs = sortedIds
            .Select((id, i) => new EntityConfig
            {
                EntityId = id,
                FriendlyName = "",
                Detectors = parsedDetectors.TryGetValue(i, out var dets)
                    ? dets
                    : [new DetectorConfig { Name = "hst", Params = [] }],
            })
            .ToList();

        var bEntity = entityConfigs.First(e => e.EntityId == "sensor.b_sensor");
        var dEntity = entityConfigs.First(e => e.EntityId == "sensor.d_sensor");

        // sensor.b_sensor (first alphabetically) → detectors[0] → hst with window=100
        Assert.Equal("hst", bEntity.Detectors[0].Name);
        Assert.Equal("100", bEntity.Detectors[0].Params["window"]);
        // sensor.d_sensor → detectors[1] → stl
        Assert.Equal("stl", dEntity.Detectors[0].Name);
    }

    // -----------------------------------------------------------------------
    // Empty detector list → default HST (Pitfall 7 / CFG-03)
    // -----------------------------------------------------------------------

    [Fact]
    public void DefaultHst_EntityWithNoDetectorFields_GetsHstDefault()
    {
        // Arrange: entity is submitted (in selectedIds) but no detectors[0][...] keys present
        var parsedDetectors = new Dictionary<int, List<DetectorConfig>>();
        // Entity index 0 has no parsed detectors
        var entityId = "sensor.temp";

        // Act: apply the empty-list default logic (entityId used conceptually — suppressed)
        _ = entityId;
        var detectors = parsedDetectors.TryGetValue(0, out var dets) && dets.Count > 0
            ? dets
            : [new DetectorConfig { Name = "hst", Params = [] }];

        // Assert
        Assert.Single(detectors);
        Assert.Equal("hst", detectors[0].Name);
    }

    // -----------------------------------------------------------------------
    // Multi-detector YAML round-trip (CFG-03)
    // -----------------------------------------------------------------------

    [Fact]
    public void RoundTrip_MultipleDetectorsPerEntity_LoadsBackCorrectly()
    {
        // Arrange: build YAML with 2 detectors on one entity
        var entities = new List<EntityConfig>
        {
            new()
            {
                EntityId = "sensor.living_room_temp",
                FriendlyName = "Salon",
                Detectors =
                [
                    new DetectorConfig
                    {
                        Name = "hst",
                        Params = new Dictionary<string, string>
                        {
                            ["window"] = "300",
                            ["n_trees"] = "30",
                        }
                    },
                    new DetectorConfig
                    {
                        Name = "mad",
                        Params = new Dictionary<string, string>
                        {
                            ["threshold"] = "4.0",
                            ["window"] = "50",
                        }
                    },
                ]
            }
        };

        var yaml = BuildCombinedYaml([], [], entities);
        var path = WriteTempYaml(yaml);
        var logger = NullLogger<EntitiesConfigLoader>.Instance;

        // Act
        var config = EntitiesConfigLoader.Load(path, logger);

        // Assert
        Assert.Single(config.Entities);
        Assert.Equal(2, config.Entities[0].Detectors.Count);

        var hst = config.Entities[0].Detectors[0];
        Assert.Equal("hst", hst.Name);
        Assert.Equal("300", hst.Params["window"]);
        Assert.Equal("30", hst.Params["n_trees"]);

        var mad = config.Entities[0].Detectors[1];
        Assert.Equal("mad", mad.Name);
        Assert.Equal("4.0", mad.Params["threshold"]);
        Assert.Equal("50", mad.Params["window"]);
    }

    [Fact]
    public void RoundTrip_HstWithAllSevenParams_LoadsBackCorrectly()
    {
        // Arrange: all 7 HST params serialized
        var entities = new List<EntityConfig>
        {
            new()
            {
                EntityId = "sensor.outdoor_temp",
                FriendlyName = "",
                Detectors =
                [
                    new DetectorConfig
                    {
                        Name = "hst",
                        Params = new Dictionary<string, string>
                        {
                            ["window"] = "500",
                            ["n_trees"] = "50",
                            ["high_threshold"] = "0.8",
                            ["low_threshold"] = "0.2",
                            ["min_consecutive"] = "5",
                            ["frozen_window"] = "20",
                            ["frozen_variance_threshold"] = "0.002",
                        }
                    }
                ]
            }
        };

        var yaml = BuildCombinedYaml([], [], entities);
        var path = WriteTempYaml(yaml);
        var logger = NullLogger<EntitiesConfigLoader>.Instance;

        // Act
        var config = EntitiesConfigLoader.Load(path, logger);

        // Assert
        var hst = config.Entities[0].Detectors[0];
        Assert.Equal("500", hst.Params["window"]);
        Assert.Equal("0.002", hst.Params["frozen_variance_threshold"]);
    }

    // -----------------------------------------------------------------------
    // Swap called after write (ILiveEntitiesConfig)
    // -----------------------------------------------------------------------

    [Fact]
    public void SwapCalledAfterWrite_LiveConfigReflectsNewEntities()
    {
        // Simulates the save handler: write config → re-load → Swap → Get() returns new config
        var initial = new EntitiesConfig();
        var live = new LiveEntitiesConfig(initial);

        // Simulate what the save handler does after WriteAsync succeeds:
        var newEntities = new EntitiesConfig
        {
            Entities =
            [
                new EntityConfig
                {
                    EntityId = "sensor.new",
                    FriendlyName = "",
                    Detectors = [new DetectorConfig { Name = "hst", Params = [] }]
                }
            ]
        };

        // Act: call Swap with the reloaded config
        live.Swap(newEntities);

        // Assert: Get() returns the new config
        Assert.Same(newEntities, live.Get());
        Assert.Single(live.Get().Entities);
        Assert.Equal("sensor.new", live.Get().Entities[0].EntityId);
    }

    [Fact]
    public void SwapCalledAfterWrite_ConfigChangedEventFired()
    {
        // Verifies that Swap fires ConfigChanged (the reload trigger)
        var live = new LiveEntitiesConfig(new EntitiesConfig());
        var eventFired = false;
        live.ConfigChanged += (_, _) => eventFired = true;

        // Act
        live.Swap(new EntitiesConfig());

        Assert.True(eventFired, "ConfigChanged must fire after Swap — this is the reload trigger");
    }
}
