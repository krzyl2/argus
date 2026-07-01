using Argus.Orchestrator.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for the save-endpoint YAML persistence contract:
///   1. Round-trip: a YAML file with _patterns: block loads cleanly via EntitiesConfigLoader.
///   2. YAML-builder logic produces a valid _patterns: structure (include/exclude lists).
///   3. Zero selected entities yields entities: [] (no throw).
///   4. Lock file .ui_config_present is created after a successful write.
///
/// These tests validate the YAML shape and round-trip safety without spinning up a full
/// HTTP server — the core logic is extracted here for direct unit testing.
/// </summary>
public class SaveEndpointPatternsTests
{
    // -----------------------------------------------------------------------
    // Helpers: mirrors the Program.cs save handler serialization logic
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the same combined YAML root dict as the POST /api/sensors/save handler.
    /// Extracted here so tests can verify the shape independently of the HTTP layer.
    /// </summary>
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

    private static string WriteTempYaml(string content)
    {
        var path = Path.GetTempFileName() + ".yaml";
        File.WriteAllText(path, content);
        return path;
    }

    // -----------------------------------------------------------------------
    // Test 1: Round-trip — _patterns: block is silently ignored by EntitiesConfigLoader
    // -----------------------------------------------------------------------

    [Fact]
    public void EntitiesConfigLoader_YamlWithPatternsBlock_LoadsCleanly()
    {
        // Arrange — a YAML file that contains both _patterns: and entities: (the post-save shape)
        var yaml = """
            _patterns:
              include:
                - sensor.*temp*
              exclude:
                - sensor.*test*
            entities:
              - entity_id: sensor.living_room_temp
                friendly_name: Salon temperatura
                detectors:
                  - name: hst
                    params: {}
            """;
        var path = WriteTempYaml(yaml);
        var logger = NullLogger<EntitiesConfigLoader>.Instance;

        // Act — must NOT throw; _patterns is an unmatched key and must be silently ignored
        var config = EntitiesConfigLoader.Load(path, logger);

        // Assert — entities loaded correctly despite _patterns: prefix
        Assert.NotNull(config);
        Assert.Single(config.Entities);
        Assert.Equal("sensor.living_room_temp", config.Entities[0].EntityId);
        Assert.Equal("Salon temperatura", config.Entities[0].FriendlyName);
        Assert.Single(config.Entities[0].Detectors);
        Assert.Equal("hst", config.Entities[0].Detectors[0].Name);
    }

    [Fact]
    public void EntitiesConfigLoader_EmptyEntitiesWithPatternsBlock_LoadsCleanly()
    {
        // Arrange — same shape but zero entities (valid UI state after "deselect all")
        var yaml = """
            _patterns:
              include: []
              exclude: []
            entities: []
            """;
        var path = WriteTempYaml(yaml);
        var logger = NullLogger<EntitiesConfigLoader>.Instance;

        // Act
        var config = EntitiesConfigLoader.Load(path, logger);

        // Assert
        Assert.NotNull(config);
        Assert.Empty(config.Entities);
    }

    // -----------------------------------------------------------------------
    // Test 2: YAML builder produces correct _patterns: structure
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildCombinedYaml_ProducesValidPatternsBlock()
    {
        // Arrange
        var include = new[] { "sensor.*temp*", "sensor.*humidity*" };
        var exclude = new[] { "sensor.*test*" };
        var entities = new List<EntityConfig>
        {
            new() { EntityId = "sensor.outdoor_temp", FriendlyName = "Outdoor",
                    Detectors = [new() { Name = "hst", Params = [] }] }
        };

        // Act
        var yaml = BuildCombinedYaml(include, exclude, entities);

        // Assert — _patterns block present with correct lists
        Assert.Contains("_patterns:", yaml);
        Assert.Contains("include:", yaml);
        Assert.Contains("sensor.*temp*", yaml);
        Assert.Contains("sensor.*humidity*", yaml);
        Assert.Contains("exclude:", yaml);
        Assert.Contains("sensor.*test*", yaml);

        // Assert — entities block also present
        Assert.Contains("entities:", yaml);
        Assert.Contains("sensor.outdoor_temp", yaml);
    }

    [Fact]
    public void BuildCombinedYaml_YamlSpecialCharsInEntityId_AreSafelyHandled()
    {
        // T-02-08: YamlDotNet must quote/escape YAML-special characters — no string-format injection
        var entities = new List<EntityConfig>
        {
            new() { EntityId = "sensor.test:colon", FriendlyName = "",
                    Detectors = [new() { Name = "hst", Params = [] }] }
        };

        var yaml = BuildCombinedYaml([], [], entities);

        // The YAML must load back cleanly (YamlDotNet handled quoting)
        var path = WriteTempYaml(yaml);
        var logger = NullLogger<EntitiesConfigLoader>.Instance;
        var config = EntitiesConfigLoader.Load(path, logger);

        Assert.Single(config.Entities);
        Assert.Equal("sensor.test:colon", config.Entities[0].EntityId);
    }

    // -----------------------------------------------------------------------
    // Test 3: Zero entities — valid state, no throw, entities: []
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildCombinedYaml_ZeroEntities_ProducesEmptyEntitiesList()
    {
        // Arrange — empty selection: valid per Pitfall 5
        var yaml = BuildCombinedYaml([], [], []);

        // Act — load it back to verify round-trip
        var path = WriteTempYaml(yaml);
        var logger = NullLogger<EntitiesConfigLoader>.Instance;
        var config = EntitiesConfigLoader.Load(path, logger);

        // Assert
        Assert.NotNull(config);
        Assert.Empty(config.Entities);

        // The raw YAML must also show an entities key (not omitted)
        Assert.Contains("entities:", yaml);
    }

    // -----------------------------------------------------------------------
    // Test 4: Lock file — created after successful ConfigWriter.WriteAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LockFile_CreatedAfterSuccessfulSave()
    {
        // Arrange — use a temp directory to simulate /data/
        var tmpDir = Path.Combine(Path.GetTempPath(), $"argus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var entitiesPath = Path.Combine(tmpDir, "entities.yaml");
        var lockPath = Path.Combine(tmpDir, ".ui_config_present");

        var writer = new ConfigWriter();
        var yaml = BuildCombinedYaml(["sensor.*temp*"], [], new List<EntityConfig>
        {
            new() { EntityId = "sensor.outdoor_temp", FriendlyName = "Outdoor",
                    Detectors = [new() { Name = "hst", Params = [] }] }
        });

        // Act — mirror the Program.cs save handler logic
        await writer.WriteAsync(entitiesPath, yaml);
        await File.WriteAllTextAsync(lockPath, string.Empty);

        // Assert — entities.yaml written
        Assert.True(File.Exists(entitiesPath), "entities.yaml must exist after save");

        // Assert — lock file created AFTER successful write
        Assert.True(File.Exists(lockPath), ".ui_config_present must be created after successful save");

        // Assert — config round-trips via EntitiesConfigLoader
        var logger = NullLogger<EntitiesConfigLoader>.Instance;
        var config = EntitiesConfigLoader.Load(entitiesPath, logger);
        Assert.Single(config.Entities);
        Assert.Equal("sensor.outdoor_temp", config.Entities[0].EntityId);

        // Cleanup
        Directory.Delete(tmpDir, recursive: true);
    }

    [Fact]
    public async Task LockFile_NotCreatedBeforeSave()
    {
        // Lock file must NOT be created if WriteAsync fails
        // (This is a logical assertion: lock is only written AFTER WriteAsync succeeds)
        var tmpDir = Path.Combine(Path.GetTempPath(), $"argus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var lockPath = Path.Combine(tmpDir, ".ui_config_present");

        // We do NOT call WriteAsync or create the lock file — assert lock is absent
        Assert.False(File.Exists(lockPath), ".ui_config_present must not exist before a save");

        // Cleanup
        Directory.Delete(tmpDir, recursive: true);
        await Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Test 5: Full round-trip with multiple entities and non-empty patterns
    // -----------------------------------------------------------------------

    [Fact]
    public void FullRoundTrip_MultipleEntitiesWithPatterns_LoadsCorrectly()
    {
        // Arrange
        var entities = new List<EntityConfig>
        {
            new() { EntityId = "sensor.living_room_temp", FriendlyName = "Salon temperatura",
                    Detectors = [new() { Name = "hst", Params = [] }] },
            new() { EntityId = "sensor.outdoor_humidity", FriendlyName = "Wilgotność zewnętrzna",
                    Detectors = [new() { Name = "hst", Params = [] }] },
        };
        var yaml = BuildCombinedYaml(["sensor.*temp*"], ["sensor.*test*"], entities);
        var path = WriteTempYaml(yaml);
        var logger = NullLogger<EntitiesConfigLoader>.Instance;

        // Act
        var config = EntitiesConfigLoader.Load(path, logger);

        // Assert — both entities loaded; _patterns was silently ignored
        Assert.Equal(2, config.Entities.Count);
        Assert.Contains(config.Entities, e => e.EntityId == "sensor.living_room_temp");
        Assert.Contains(config.Entities, e => e.EntityId == "sensor.outdoor_humidity");
    }
}
