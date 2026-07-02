using Argus.Orchestrator.Config;
using Argus.Orchestrator.Web;
using Microsoft.Extensions.Logging.Abstractions;
#pragma warning disable CS8602 // Tests explicitly construct valid objects
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for the JSON SaveRequest -> Dictionary&lt;int, List&lt;DetectorConfig&gt;&gt; mapping
/// used by the POST /api/sensors/save handler (Phase 7 — replaces the removed
/// DetectorFieldParser regex-based form parsing with a direct JSON body mapping).
///
/// Validates CFG-03: SaveRequest.Entities[].Detectors -> EntityConfig.Detectors, multi-detector
/// round-trip, empty-list HST default, and entity-index correlation stability.
///
/// Fully offline — no HTTP server required. Mirrors the exact mapping logic in Program.cs's
/// save handler (entityId-keyed lookup, positional index = position in sorted resolvedIds).
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

    /// <summary>
    /// Mirrors Program.cs's save-handler mapping: SaveRequest.Entities keyed by entityId,
    /// then positionally re-keyed by index in the sorted resolvedIds list.
    /// </summary>
    private static Dictionary<int, List<DetectorConfig>> MapToParsedDetectors(
        SaveRequest body, IEnumerable<string> sortedIds)
    {
        var detectorsByEntityId = body.Entities
            .Where(e => !string.IsNullOrEmpty(e.EntityId))
            .ToDictionary(
                e => e.EntityId,
                e => e.Detectors.Select(d => new DetectorConfig { Name = d.Name, Params = d.Params }).ToList(),
                StringComparer.OrdinalIgnoreCase);

        return sortedIds
            .Select((id, ei) => (ei, dets: detectorsByEntityId.TryGetValue(id, out var d) ? d : new List<DetectorConfig>()))
            .ToDictionary(x => x.ei, x => x.dets);
    }

    // -----------------------------------------------------------------------
    // SaveRequest -> parsedDetectors mapping
    // -----------------------------------------------------------------------

    [Fact]
    public void Map_SingleDetectorSingleEntity_ReturnsSingleDetectorConfig()
    {
        var body = new SaveRequest
        {
            Entities =
            [
                new SaveEntity
                {
                    EntityId = "sensor.a",
                    Detectors = [new SaveDetector { Name = "hst", Params = new() { ["window"] = "250", ["n_trees"] = "25" } }]
                }
            ]
        };

        var result = MapToParsedDetectors(body, ["sensor.a"]);

        Assert.True(result.ContainsKey(0), "Expected detector entry for entity index 0");
        Assert.Single(result[0]);
        Assert.Equal("hst", result[0][0].Name);
        Assert.Equal("250", result[0][0].Params["window"]);
        Assert.Equal("25", result[0][0].Params["n_trees"]);
    }

    [Fact]
    public void Map_MultipleDetectorsSameEntity_ReturnsMultipleDetectorsInOrder()
    {
        var body = new SaveRequest
        {
            Entities =
            [
                new SaveEntity
                {
                    EntityId = "sensor.a",
                    Detectors =
                    [
                        new SaveDetector { Name = "hst", Params = new() { ["window"] = "250" } },
                        new SaveDetector { Name = "mad", Params = new() { ["threshold"] = "3.5" } },
                    ]
                }
            ]
        };

        var result = MapToParsedDetectors(body, ["sensor.a"]);

        Assert.Equal(2, result[0].Count);
        Assert.Equal("hst", result[0][0].Name);
        Assert.Equal("mad", result[0][1].Name);
        Assert.Equal("3.5", result[0][1].Params["threshold"]);
    }

    [Fact]
    public void Map_TwoEntitiesWithDetectors_ReturnsTwoEntityEntries()
    {
        var body = new SaveRequest
        {
            Entities =
            [
                new SaveEntity { EntityId = "sensor.a", Detectors = [new SaveDetector { Name = "hst", Params = new() { ["window"] = "300" } }] },
                new SaveEntity { EntityId = "sensor.b", Detectors = [new SaveDetector { Name = "stl", Params = new() { ["period"] = "24" } }] },
            ]
        };

        var result = MapToParsedDetectors(body, ["sensor.a", "sensor.b"]);

        Assert.Equal(2, result.Count);
        Assert.Equal("hst", result[0][0].Name);
        Assert.Equal("300", result[0][0].Params["window"]);
        Assert.Equal("stl", result[1][0].Name);
        Assert.Equal("24", result[1][0].Params["period"]);
    }

    [Fact]
    public void Map_EmptyEntities_ReturnsEmptyDictionary()
    {
        var body = new SaveRequest { Entities = [] };

        var result = MapToParsedDetectors(body, []);

        Assert.Empty(result);
    }

    [Fact]
    public void Map_EntityNotInSortedIds_IsIgnored()
    {
        // Entity present in body but not in the resolved/sorted id set (e.g. filtered out
        // by GlobExpander) must not appear in the result.
        var body = new SaveRequest
        {
            Entities = [new SaveEntity { EntityId = "sensor.excluded", Detectors = [new SaveDetector { Name = "hst" }] }]
        };

        var result = MapToParsedDetectors(body, []);

        Assert.Empty(result);
    }

    // -----------------------------------------------------------------------
    // Entity index correlation (Pitfall 5 / CFG-03-critical)
    // -----------------------------------------------------------------------

    [Fact]
    public void Correlate_TwoEntitiesAlphabetical_DetectorIdx0MapsToFirstEntityAlpha()
    {
        // The canonical order is alphabetical by EntityId (same order the SPA renders).
        // detectors[0] must map to the FIRST entity alphabetically.
        var submittedIds = new List<string> { "sensor.z_sensor", "sensor.a_sensor" };
        var sortedIds = submittedIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();

        var parsedDetectors = new Dictionary<int, List<DetectorConfig>>
        {
            [0] = [new DetectorConfig { Name = "hst", Params = [] }],
            [1] = [new DetectorConfig { Name = "mad", Params = [] }],
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

        var aEntity = entityConfigs.First(e => e.EntityId == "sensor.a_sensor");
        var zEntity = entityConfigs.First(e => e.EntityId == "sensor.z_sensor");
        Assert.Equal("hst", aEntity.Detectors[0].Name);
        Assert.Equal("mad", zEntity.Detectors[0].Name);
    }

    [Fact]
    public void Correlate_NonContiguousCheckedEntities_CorrelationIsStable()
    {
        // Pitfall 5: entity index must be stable regardless of which entities are checked.
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

        Assert.Equal("hst", bEntity.Detectors[0].Name);
        Assert.Equal("100", bEntity.Detectors[0].Params["window"]);
        Assert.Equal("stl", dEntity.Detectors[0].Name);
    }

    // -----------------------------------------------------------------------
    // Empty detector list -> default HST (Pitfall 7 / CFG-03)
    // -----------------------------------------------------------------------

    [Fact]
    public void DefaultHst_EntityWithNoDetectors_GetsHstDefault()
    {
        var body = new SaveRequest
        {
            Entities = [new SaveEntity { EntityId = "sensor.temp", Detectors = [] }]
        };

        var parsedDetectors = MapToParsedDetectors(body, ["sensor.temp"]);

        var detectors = parsedDetectors.TryGetValue(0, out var dets) && dets.Count > 0
            ? dets
            : [new DetectorConfig { Name = "hst", Params = [] }];

        Assert.Single(detectors);
        Assert.Equal("hst", detectors[0].Name);
    }

    // -----------------------------------------------------------------------
    // Multi-detector YAML round-trip (CFG-03)
    // -----------------------------------------------------------------------

    [Fact]
    public void RoundTrip_MultipleDetectorsPerEntity_LoadsBackCorrectly()
    {
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

        var config = EntitiesConfigLoader.Load(path, logger);

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

        var config = EntitiesConfigLoader.Load(path, logger);

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
        var initial = new EntitiesConfig();
        var live = new LiveEntitiesConfig(initial);

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

        live.Swap(newEntities);

        Assert.Same(newEntities, live.Get());
        Assert.Single(live.Get().Entities);
        Assert.Equal("sensor.new", live.Get().Entities[0].EntityId);
    }

    [Fact]
    public void SwapCalledAfterWrite_ConfigChangedEventFired()
    {
        var live = new LiveEntitiesConfig(new EntitiesConfig());
        var eventFired = false;
        live.ConfigChanged += (_, _) => eventFired = true;

        live.Swap(new EntitiesConfig());

        Assert.True(eventFired, "ConfigChanged must fire after Swap — this is the reload trigger");
    }
}
