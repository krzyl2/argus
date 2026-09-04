using System.Linq;
using Microsoft.Extensions.Logging;
using Xunit;
using Argus.Orchestrator.Config;

namespace Argus.Orchestrator.Tests;

public class EntitiesConfigTests
{
    // Helper: create an in-memory capturing logger
    private static (ILogger<EntitiesConfigLoader> logger, List<string> messages) CreateCapturingLogger()
    {
        var messages = new List<string>();
        var factory = LoggerFactory.Create(b =>
            b.AddProvider(new CapturingLoggerProvider(messages)));
        return (factory.CreateLogger<EntitiesConfigLoader>(), messages);
    }

    [Fact]
    public void Load_OneEntityWithHstParams_ParsesCorrectly()
    {
        // Arrange
        var yaml = @"
entities:
  - entity_id: sensor.salon_temperatura
    friendly_name: Salon temperatura
    detectors:
      - name: hst
        params:
          window: '250'
          n_trees: '25'
          high_threshold: '0.7'
          low_threshold: '0.3'
          min_consecutive: '3'
";
        var path = WriteTempYaml(yaml);
        var (logger, _) = CreateCapturingLogger();

        // Act
        var config = EntitiesConfigLoader.Load(path, logger);

        // Assert
        Assert.Single(config.Entities);
        var entity = config.Entities[0];
        Assert.Equal("sensor.salon_temperatura", entity.EntityId);
        Assert.Single(entity.Detectors);
        var det = entity.Detectors[0];
        Assert.Equal("hst", det.Name);
        var hst = HstParams.From(det.Params);
        Assert.Equal(250, hst.Window);
        Assert.Equal(0.7, hst.HighThreshold, precision: 6);
    }

    [Fact]
    public void Load_EntityWithStrayCovariatesKey_ParsesSuccessfullyAndIgnoresIt()
    {
        // Arrange — retired per-entity covariates/groups placeholders no longer exist as C#
        // properties; IgnoreUnmatchedProperties() must still let stray YAML keys through
        // without throwing (existing operator YAML with these keys must not break on upgrade).
        var yaml = @"
entities:
  - entity_id: sensor.salon_temperatura
    friendly_name: Salon temperatura
    covariates:
      - sensor.outdoor_temperature
    detectors:
      - name: hst
        params: {}
";
        var path = WriteTempYaml(yaml);
        var (logger, _) = CreateCapturingLogger();

        // Act — must not throw
        var config = EntitiesConfigLoader.Load(path, logger);

        // Assert: parse succeeded, stray key silently ignored
        Assert.Single(config.Entities);
        Assert.Equal("sensor.salon_temperatura", config.Entities[0].EntityId);
    }

    [Fact]
    public void Load_EntityWithEmptyParams_AppliesDefaults()
    {
        // Arrange
        var yaml = @"
entities:
  - entity_id: sensor.outdoor_temperature
    friendly_name: Zewnatrz temperatura
    detectors:
      - name: hst
        params: {}
";
        var path = WriteTempYaml(yaml);
        var (logger, _) = CreateCapturingLogger();

        // Act
        var config = EntitiesConfigLoader.Load(path, logger);

        // Assert: defaults applied per D-09/D-11/D-12
        var det = config.Entities[0].Detectors[0];
        var hst = HstParams.From(det.Params);
        Assert.Equal(250, hst.Window);
        Assert.Equal(25, hst.NTrees);
        Assert.Equal(0.7, hst.HighThreshold, precision: 6);
        Assert.Equal(0.3, hst.LowThreshold, precision: 6);
        Assert.Equal(3, hst.MinConsecutive);
        Assert.Equal(10, hst.FrozenWindow);
        Assert.Equal(0.001, hst.FrozenVarianceThreshold, precision: 6);
    }

    [Fact]
    public void Load_EmptyEntities_LogsWarning_DoesNotThrow()
    {
        // Arrange
        var yaml = "entities: []";
        var path = WriteTempYaml(yaml);
        var (logger, messages) = CreateCapturingLogger();

        // Act — must NOT throw
        var config = EntitiesConfigLoader.Load(path, logger);

        // Assert: returned config has empty entities (not null)
        Assert.NotNull(config);
        Assert.Empty(config.Entities);

        // Assert: warning logged mentioning empty pipeline / UI
        Assert.Contains(messages, m =>
            m.Contains("no entities") || m.Contains("empty pipeline") || m.Contains("configure via UI"));
    }

    [Fact]
    public void Load_NullEntitiesKey_LogsWarning_DoesNotThrow()
    {
        // Arrange — YAML with no `entities:` key at all (options.json first-boot scenario)
        var yaml = "# empty config";
        var path = WriteTempYaml(yaml);
        var (logger, messages) = CreateCapturingLogger();

        // Act — must NOT throw
        var config = EntitiesConfigLoader.Load(path, logger);

        Assert.NotNull(config);
        Assert.Contains(messages, m => m.Contains("no entities") || m.Contains("empty pipeline"));
    }

    [Fact]
    public void Load_ValidJointGroup_Survives()
    {
        var yaml = @"
entities: []
groups:
  - group_id: living_room_climate
    friendly_name: Salon klimat
    members: [sensor.a, sensor.b, sensor.c]
    mode: joint
    detector: pca
";
        var path = WriteTempYaml(yaml);
        var (logger, _) = CreateCapturingLogger();

        var config = EntitiesConfigLoader.Load(path, logger);

        Assert.Single(config.Groups);
        Assert.Equal("living_room_climate", config.Groups[0].GroupId);
    }

    [Fact]
    public void Load_GroupBelowFloor_IsPrunedAndWarns_DoesNotThrow()
    {
        // Floor is now 2 (GRP-10/GRP-12) — a 1-member group is the below-floor case.
        var yaml = @"
entities: []
groups:
  - group_id: too_small
    friendly_name: Too small
    members: [sensor.a]
    mode: joint
    detector: pca
";
        var path = WriteTempYaml(yaml);
        var (logger, messages) = CreateCapturingLogger();

        // Act — must not throw
        var config = EntitiesConfigLoader.Load(path, logger);

        Assert.Empty(config.Groups);
        Assert.Contains(messages, m => m.Contains("too_small") || m.Contains("minimum"));
    }

    [Fact]
    public void Load_TwoMemberJointGroup_Survives()
    {
        // GRP-10: a 2-member joint group is now a valid paired comparison, not below-floor.
        var yaml = @"
entities: []
groups:
  - group_id: two_member_joint
    friendly_name: Two member joint
    members: [sensor.a, sensor.b]
    mode: joint
    detector: pca
";
        var path = WriteTempYaml(yaml);
        var (logger, _) = CreateCapturingLogger();

        var config = EntitiesConfigLoader.Load(path, logger);

        Assert.Single(config.Groups);
        Assert.Equal("two_member_joint", config.Groups[0].GroupId);
    }

    [Fact]
    public void Load_TwoMemberPeerDivergenceGroup_SameUnits_Survives()
    {
        // GRP-11/GRP-12: a 2-member peer_divergence group must survive config-load validation
        // so it can route to the pairwise-delta path (Plan 09-02/09-03).
        var yaml = @"
entities: []
groups:
  - group_id: two_member_peer
    friendly_name: Two member peer
    members: [sensor.a, sensor.b]
    mode: peer_divergence
    detector: peer_divergence
";
        var path = WriteTempYaml(yaml);
        var (logger, _) = CreateCapturingLogger();
        var registry = new FakeHaSensorRegistry(new Dictionary<string, string?>
        {
            ["sensor.a"] = "°C",
            ["sensor.b"] = "°C",
        });

        var config = EntitiesConfigLoader.Load(path, logger, registry);

        Assert.Single(config.Groups);
        Assert.Equal("two_member_peer", config.Groups[0].GroupId);
    }

    [Fact]
    public void Load_PeerDivergenceGroupWithMixedUnits_IsPrunedAndWarns()
    {
        var yaml = @"
entities: []
groups:
  - group_id: mixed_units
    friendly_name: Mixed units
    members: [sensor.a, sensor.b, sensor.c]
    mode: peer_divergence
    detector: peer_divergence
";
        var path = WriteTempYaml(yaml);
        var (logger, messages) = CreateCapturingLogger();
        var registry = new FakeHaSensorRegistry(new Dictionary<string, string?>
        {
            ["sensor.a"] = "°C",
            ["sensor.b"] = "°C",
            ["sensor.c"] = "%",
        });

        var config = EntitiesConfigLoader.Load(path, logger, registry);

        Assert.Empty(config.Groups);
        Assert.Contains(messages, m => m.Contains("mixed_units"));
    }

    [Fact]
    public void Load_PeerDivergenceGroupWithNullRegistry_IsKept_ColdBootDegrade()
    {
        var yaml = @"
entities: []
groups:
  - group_id: cold_boot
    friendly_name: Cold boot
    members: [sensor.a, sensor.b, sensor.c]
    mode: peer_divergence
    detector: peer_divergence
";
        var path = WriteTempYaml(yaml);
        var (logger, _) = CreateCapturingLogger();

        // Act — no registry passed (defaults to null), must not throw and must KEEP the group
        var config = EntitiesConfigLoader.Load(path, logger);

        Assert.Single(config.Groups);
        Assert.Equal("cold_boot", config.Groups[0].GroupId);
    }

    [Fact]
    public void Load_MixedValidAndInvalidGroups_KeepsOnlyValid()
    {
        var yaml = @"
entities: []
groups:
  - group_id: valid_group
    friendly_name: Valid
    members: [sensor.a, sensor.b, sensor.c]
    mode: joint
    detector: pca
  - group_id: invalid_group
    friendly_name: Invalid
    members: [sensor.x]
    mode: joint
    detector: pca
";
        var path = WriteTempYaml(yaml);
        var (logger, _) = CreateCapturingLogger();

        var config = EntitiesConfigLoader.Load(path, logger);

        Assert.Single(config.Groups);
        Assert.Equal("valid_group", config.Groups[0].GroupId);
    }

    [Fact]
    public void Load_NoGroupsKey_YieldsEmptyGroupsList()
    {
        var yaml = @"
entities: []
";
        var path = WriteTempYaml(yaml);
        var (logger, _) = CreateCapturingLogger();

        var config = EntitiesConfigLoader.Load(path, logger);

        Assert.NotNull(config.Groups);
        Assert.Empty(config.Groups);
    }

    // --- Null LIST ITEMS: the bare "-" an operator leaves behind mid-edit -----

    [Fact]
    public void Load_BareDashInDetectors_ThrowsReadableValidationError()
    {
        // A bare "-" under `detectors:` is VALID YAML for a null list item, and YamlDotNet puts
        // that null straight into List<DetectorConfig> — so `Detectors.Count` is 1 and the
        // existing "has no detectors configured" check waves it through. Every reader then
        // dereferences null, and the first one on the boot path is EntitiesSchemaMigrator, which
        // logs and RETHROWS by design into a Program.cs call that does not catch: the add-on
        // does not start at all. Same operator typo, same operational outcome as the `params:`
        // null this loader already normalizes.
        //
        // The guarantee pinned here is what the file already promises for a null ENTITY: the
        // config is REJECTED with a message naming the shape, never a NullReferenceException.
        var yaml = @"
entities:
  - entity_id: sensor.salon_temperatura
    friendly_name: Salon temperatura
    detectors:
      -
";
        var path = WriteTempYaml(yaml);
        var (logger, _) = CreateCapturingLogger();

        var ex = Assert.Throws<InvalidOperationException>(
            () => EntitiesConfigLoader.Load(path, logger));

        Assert.Contains("sensor.salon_temperatura", ex.Message);
        Assert.Contains("-", ex.Message);
    }

    [Fact]
    public void Load_BareDashInEntities_ThrowsReadableValidationError()
    {
        // The entity-level counterpart, pinned so the two list levels cannot drift apart again:
        // both are rejected, and both name the bare '-' the operator has to go and find.
        var yaml = @"
entities:
  -
";
        var path = WriteTempYaml(yaml);
        var (logger, _) = CreateCapturingLogger();

        var ex = Assert.Throws<InvalidOperationException>(
            () => EntitiesConfigLoader.Load(path, logger));

        Assert.Contains("-", ex.Message);
    }

    [Fact]
    public void Load_BareDashInGroups_SkipsGroupAndKeepsTheRest()
    {
        // Groups follow the OTHER convention of this file (degrade-not-crash, D-14): a bad group
        // is pruned with a warning so the valid ones — and every entity — still load.
        var yaml = @"
entities: []
groups:
  -
  - group_id: valid_group
    friendly_name: Valid
    members: [sensor.a, sensor.b]
    mode: joint
    detector: pca
";
        var path = WriteTempYaml(yaml);
        var (logger, messages) = CreateCapturingLogger();

        var config = EntitiesConfigLoader.Load(path, logger);

        Assert.Equal("valid_group", Assert.Single(config.Groups).GroupId);
        Assert.Contains(messages, m => m.Contains("null group entry"));
    }

    [Fact]
    public void Load_BareDashInGroupMembers_SkipsGroupAndKeepsTheRest()
    {
        // The list one level down. A null member survives the count and the duplicate check, and
        // then peer-divergence unit resolution does `ResolvedUnits[member] = ...` with a null key
        // — an ArgumentNullException thrown out of ValidateGroups, breaking the "NEVER throws"
        // contract that method is built on and that the /api reload path (which is the caller
        // that passes a registry) depends on. Pruned with a warning, like every other malformed
        // group.
        var yaml = @"
entities: []
groups:
  - group_id: holey_members
    friendly_name: Holey
    members:
      - sensor.a
      -
      - sensor.b
    mode: peer_divergence
    detector: peer_divergence
  - group_id: valid_group
    friendly_name: Valid
    members: [sensor.a, sensor.b]
    mode: peer_divergence
    detector: peer_divergence
";
        var path = WriteTempYaml(yaml);
        var (logger, messages) = CreateCapturingLogger();
        var registry = new FakeHaSensorRegistry(new Dictionary<string, string?>
        {
            ["sensor.a"] = "°C",
            ["sensor.b"] = "°C",
        });

        var config = EntitiesConfigLoader.Load(path, logger, registry);

        Assert.Equal("valid_group", Assert.Single(config.Groups).GroupId);
        Assert.Contains(messages, m => m.Contains("holey_members"));
    }

    private static string WriteTempYaml(string content)
    {
        var path = System.IO.Path.GetTempFileName() + ".yaml";
        System.IO.File.WriteAllText(path, content);
        return path;
    }
}

/// <summary>Minimal hand-written fake IHaSensorRegistry for peer-mode unit-check tests.</summary>
internal class FakeHaSensorRegistry : Argus.Orchestrator.Ha.IHaSensorRegistry
{
    private readonly IReadOnlyList<Argus.Orchestrator.Ha.HaSensorEntry> _entries;

    public FakeHaSensorRegistry(Dictionary<string, string?> unitsByEntityId)
    {
        _entries = unitsByEntityId
            .Select(kvp => new Argus.Orchestrator.Ha.HaSensorEntry(kvp.Key, 0.0, kvp.Value, kvp.Key, true, null, "sensor"))
            .ToList();
    }

    public IReadOnlyList<Argus.Orchestrator.Ha.HaSensorEntry> GetAll() => _entries;

    public IReadOnlyList<Argus.Orchestrator.Ha.HaSensorEntry> GetFiltered(string q)
        => throw new NotImplementedException();

    public void UpdateSnapshot(
        IReadOnlyList<Argus.Orchestrator.Ha.HaStateDto> states, HashSet<string> trackedEntityIds,
        IReadOnlyDictionary<string, string?>? entityAreaNames = null)
        => throw new NotImplementedException();

    public bool Upsert(Argus.Orchestrator.Ha.HaStateDto state, bool isTracked)
        => throw new NotImplementedException();
}

/// <summary>Captures log messages into a list for assertion.</summary>
internal class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _messages;
    public CapturingLoggerProvider(List<string> messages) => _messages = messages;
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);
    public void Dispose() { }
}

internal class CapturingLogger : ILogger
{
    private readonly List<string> _messages;
    public CapturingLogger(List<string> messages) => _messages = messages;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _messages.Add(formatter(state, exception));
    }
}
