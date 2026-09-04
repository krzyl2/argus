using Argus.Orchestrator.Batch;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Web;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for the 4 new Phase 8 group endpoints (GET /api/groups, POST /api/groups/save,
/// GET /api/detectors/catalog, GET /api/groups/{id}/status). Exercises the same logic
/// Program.cs's handlers run — GroupInputValidator.Validate, YAML root-dict serialize,
/// ConfigWriter.WriteAsync, EntitiesConfigLoader.Load, liveCfg.Swap — asserting on the JSON
/// contract shapes declared authoritatively in 08-02-PLAN.md. Fully offline — no HTTP
/// server needed (mirrors the SaveEndpointJsonTests.cs harness pattern).
/// </summary>
public class GroupsEndpointsTests
{
    private sealed class FakeRegistry : IHaSensorRegistry
    {
        private readonly IReadOnlyList<HaSensorEntry> _entries;
        public FakeRegistry(params HaSensorEntry[] entries) => _entries = entries;

        public IReadOnlyList<HaSensorEntry> GetAll() => _entries;
        public IReadOnlyList<HaSensorEntry> GetFiltered(string q) => _entries;
        public void UpdateSnapshot(
            IReadOnlyList<HaStateDto> states, HashSet<string> trackedEntityIds,
            IReadOnlyDictionary<string, string?>? entityAreaNames = null)
            => throw new NotImplementedException();
        public bool Upsert(HaStateDto state, bool isTracked) => throw new NotImplementedException();
    }

    private static HaSensorEntry MakeEntry(string entityId, string? unit = "°C")
        => new(entityId, 21.0, unit, null, IsTracked: true, AreaName: null, Domain: "sensor");

    private static GroupConfig MakeGroup(string groupId, IReadOnlyList<string> members, string mode = "joint", string detector = "ecod")
        => new()
        {
            GroupId = groupId,
            FriendlyName = groupId,
            Members = members.ToList(),
            Mode = mode,
            Detector = detector,
            Params = new Dictionary<string, string>(),
        };

    // -----------------------------------------------------------------------
    // GET /api/groups — projection shape
    // -----------------------------------------------------------------------

    [Fact]
    public void GetGroups_ProjectsExpectedShape()
    {
        var group = MakeGroup("group.living_room", ["sensor.a", "sensor.b", "sensor.c"]);
        var live = new LiveEntitiesConfig(new EntitiesConfig { Groups = [group] });

        var payload = ProjectGroups(live.Get().Groups).Cast<dynamic>().ToList();

        Assert.Single(payload);
        Assert.Equal("group.living_room", (string)payload[0].groupId);
        Assert.Equal("joint", (string)payload[0].mode);
        Assert.Equal("ecod", (string)payload[0].detector);
        Assert.Equal(3, ((IEnumerable<string>)payload[0].members).Count());
    }

    [Fact]
    public void GetGroups_AfterSwap_ReturnsUpdatedGroups()
    {
        // CFG-04: the handler must call liveCfg.Get() fresh on every request.
        var live = new LiveEntitiesConfig(new EntitiesConfig());
        var updated = new EntitiesConfig { Groups = [MakeGroup("group.new", ["a.1", "a.2", "a.3"])] };

        live.Swap(updated);

        Assert.Same(updated, live.Get());
        Assert.Single(live.Get().Groups);
    }

    private static IEnumerable<object> ProjectGroups(IEnumerable<GroupConfig> groups) =>
        groups.Select(g => new
        {
            groupId = g.GroupId,
            friendlyName = g.FriendlyName,
            members = g.Members,
            mode = g.Mode,
            detector = g.Detector,
            @params = g.Params,
        });

    // -----------------------------------------------------------------------
    // POST /api/groups/save — validation
    // -----------------------------------------------------------------------

    [Fact]
    public void Validate_GroupBelowFloor_ReturnsValidationError()
    {
        // Floor is now 2 (GRP-10/GRP-12) — a 1-member group is the below-floor case.
        var registry = new FakeRegistry(MakeEntry("sensor.a"));
        var groups = new List<GroupSaveEntry>
        {
            new() { GroupId = "group.a", Members = ["sensor.a"], Mode = "joint", Detector = "ecod" },
        };

        var errors = GroupInputValidator.Validate(groups, registry);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_ValidJointGroup_ReturnsNoErrors()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.a"), MakeEntry("sensor.b"), MakeEntry("sensor.c"));
        var groups = new List<GroupSaveEntry>
        {
            new() { GroupId = "group.a", Members = ["sensor.a", "sensor.b", "sensor.c"], Mode = "joint", Detector = "ecod" },
        };

        var errors = GroupInputValidator.Validate(groups, registry);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_TwoMemberJointGroup_ReturnsNoErrors()
    {
        // GRP-10: a 2-member joint group is now a valid paired comparison, not below-floor.
        var registry = new FakeRegistry(MakeEntry("sensor.a"), MakeEntry("sensor.b"));
        var groups = new List<GroupSaveEntry>
        {
            new() { GroupId = "group.a", Members = ["sensor.a", "sensor.b"], Mode = "joint", Detector = "ecod" },
        };

        var errors = GroupInputValidator.Validate(groups, registry);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_TwoMemberPeerDivergenceGroup_SameUnits_ReturnsNoErrors()
    {
        // GRP-11/GRP-12: a 2-member peer_divergence group must pass save-time validation so it
        // can route to the pairwise-delta path (Plan 09-02/09-03) — the floor-of-2 applies to
        // both modes uniformly (Assumption A1).
        var registry = new FakeRegistry(MakeEntry("sensor.a", "°C"), MakeEntry("sensor.b", "°C"));
        var groups = new List<GroupSaveEntry>
        {
            new() { GroupId = "group.a", Members = ["sensor.a", "sensor.b"], Mode = "peer_divergence", Detector = "peer_divergence" },
        };

        var errors = GroupInputValidator.Validate(groups, registry);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_PeerDivergenceMixedUnits_ReturnsValidationError()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.a", "°C"), MakeEntry("sensor.b", "°C"), MakeEntry("sensor.c", "%"));
        var groups = new List<GroupSaveEntry>
        {
            new() { GroupId = "group.a", Members = ["sensor.a", "sensor.b", "sensor.c"], Mode = "peer_divergence", Detector = "peer_divergence" },
        };

        var errors = GroupInputValidator.Validate(groups, registry);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_PeerDivergenceSameUnits_ReturnsNoErrors()
    {
        var registry = new FakeRegistry(
            MakeEntry("sensor.a", "°C"), MakeEntry("sensor.b", "°C"), MakeEntry("sensor.c", "°C"));
        var groups = new List<GroupSaveEntry>
        {
            new() { GroupId = "group.a", Members = ["sensor.a", "sensor.b", "sensor.c"], Mode = "peer_divergence", Detector = "peer_divergence" },
        };

        var errors = GroupInputValidator.Validate(groups, registry);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_TooManyMembers_ReturnsValidationError()
    {
        var members = Enumerable.Range(0, GroupInputValidator.MaxMembers + 1)
            .Select(i => $"sensor.m{i}").ToList();
        var registry = new FakeRegistry(members.Select(m => MakeEntry(m)).ToArray());
        var groups = new List<GroupSaveEntry>
        {
            new() { GroupId = "group.a", Members = members, Mode = "joint", Detector = "ecod" },
        };

        var errors = GroupInputValidator.Validate(groups, registry);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_UnknownMode_ReturnsValidationError()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.a"), MakeEntry("sensor.b"), MakeEntry("sensor.c"));
        var groups = new List<GroupSaveEntry>
        {
            new() { GroupId = "group.a", Members = ["sensor.a", "sensor.b", "sensor.c"], Mode = "bogus", Detector = "ecod" },
        };

        var errors = GroupInputValidator.Validate(groups, registry);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_DuplicateMembers_ReturnsValidationError()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.a"), MakeEntry("sensor.b"));
        var groups = new List<GroupSaveEntry>
        {
            new() { GroupId = "group.a", Members = ["sensor.a", "sensor.a", "sensor.b"], Mode = "joint", Detector = "ecod" },
        };

        var errors = GroupInputValidator.Validate(groups, registry);

        Assert.NotEmpty(errors);
    }

    // -----------------------------------------------------------------------
    // CR-03: mode/detector consistency
    // -----------------------------------------------------------------------

    [Fact]
    public void Validate_JointModeWithPeerDivergenceDetector_ReturnsValidationError()
    {
        // The exact CR-03 scenario: mode="joint" + detector="peer_divergence" (e.g. from a
        // client that silently defaulted the detector). Must be rejected, not saved — a
        // fabricated verdict would otherwise be published at batch time.
        var registry = new FakeRegistry(MakeEntry("sensor.a"), MakeEntry("sensor.b"), MakeEntry("sensor.c"));
        var groups = new List<GroupSaveEntry>
        {
            new() { GroupId = "group.a", Members = ["sensor.a", "sensor.b", "sensor.c"], Mode = "joint", Detector = "peer_divergence" },
        };

        var errors = GroupInputValidator.Validate(groups, registry);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_PeerDivergenceModeWithJointDetector_ReturnsValidationError()
    {
        // Reverse mismatch (WR-04): mode="peer_divergence" + detector="ecod" degrades to a
        // permanent no-op (never fitted, every score attempt aborts) rather than corrupting
        // data — still must be rejected at save time.
        var registry = new FakeRegistry(MakeEntry("sensor.a"), MakeEntry("sensor.b"), MakeEntry("sensor.c"));
        var groups = new List<GroupSaveEntry>
        {
            new() { GroupId = "group.a", Members = ["sensor.a", "sensor.b", "sensor.c"], Mode = "peer_divergence", Detector = "ecod" },
        };

        var errors = GroupInputValidator.Validate(groups, registry);

        Assert.NotEmpty(errors);
    }

    [Theory]
    [InlineData("ecod")]
    [InlineData("copod")]
    [InlineData("pca")]
    [InlineData("iforest")]
    public void Validate_JointModeWithEachJointDetector_ReturnsNoErrors(string detector)
    {
        var registry = new FakeRegistry(MakeEntry("sensor.a"), MakeEntry("sensor.b"), MakeEntry("sensor.c"));
        var groups = new List<GroupSaveEntry>
        {
            new() { GroupId = "group.a", Members = ["sensor.a", "sensor.b", "sensor.c"], Mode = "joint", Detector = detector },
        };

        var errors = GroupInputValidator.Validate(groups, registry);

        Assert.Empty(errors);
    }

    [Fact]
    public void IsModeDetectorConsistent_KnownPairings_ReturnExpectedResult()
    {
        Assert.True(GroupInputValidator.IsModeDetectorConsistent("peer_divergence", "peer_divergence"));
        Assert.True(GroupInputValidator.IsModeDetectorConsistent("joint", "ecod"));
        Assert.False(GroupInputValidator.IsModeDetectorConsistent("joint", "peer_divergence"));
        Assert.False(GroupInputValidator.IsModeDetectorConsistent("peer_divergence", "ecod"));
    }

    // -----------------------------------------------------------------------
    // WR-01: duplicate group_id detection
    // -----------------------------------------------------------------------

    [Fact]
    public void Validate_DuplicateGroupId_ReturnsValidationError()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.a"), MakeEntry("sensor.b"), MakeEntry("sensor.c"),
            MakeEntry("sensor.d"), MakeEntry("sensor.e"), MakeEntry("sensor.f"));
        var groups = new List<GroupSaveEntry>
        {
            new() { GroupId = "group.kitchen", Members = ["sensor.a", "sensor.b", "sensor.c"], Mode = "joint", Detector = "ecod" },
            new() { GroupId = "group.kitchen", Members = ["sensor.d", "sensor.e", "sensor.f"], Mode = "joint", Detector = "ecod" },
        };

        var errors = GroupInputValidator.Validate(groups, registry);

        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("Duplicate group ID"));
    }

    [Fact]
    public void Validate_DistinctGroupIds_ReturnsNoErrors()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.a"), MakeEntry("sensor.b"), MakeEntry("sensor.c"),
            MakeEntry("sensor.d"), MakeEntry("sensor.e"), MakeEntry("sensor.f"));
        var groups = new List<GroupSaveEntry>
        {
            new() { GroupId = "group.kitchen", Members = ["sensor.a", "sensor.b", "sensor.c"], Mode = "joint", Detector = "ecod" },
            new() { GroupId = "group.living_room", Members = ["sensor.d", "sensor.e", "sensor.f"], Mode = "joint", Detector = "ecod" },
        };

        var errors = GroupInputValidator.Validate(groups, registry);

        Assert.Empty(errors);
    }

    // -----------------------------------------------------------------------
    // WR-02: server-side param range validation using catalog bounds
    // -----------------------------------------------------------------------

    [Fact]
    public void Validate_ContaminationAboveCatalogMax_ReturnsValidationError()
    {
        // Catalog bounds for ecod's "contamination": 0.01..0.5 (DetectorCatalog.cs).
        var registry = new FakeRegistry(MakeEntry("sensor.a"), MakeEntry("sensor.b"), MakeEntry("sensor.c"));
        var groups = new List<GroupSaveEntry>
        {
            new()
            {
                GroupId = "group.a", Members = ["sensor.a", "sensor.b", "sensor.c"], Mode = "joint", Detector = "ecod",
                Params = new Dictionary<string, string> { ["contamination"] = "0.9" },
            },
        };

        var errors = GroupInputValidator.Validate(groups, registry);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_ContaminationWithinCatalogBounds_ReturnsNoErrors()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.a"), MakeEntry("sensor.b"), MakeEntry("sensor.c"));
        var groups = new List<GroupSaveEntry>
        {
            new()
            {
                GroupId = "group.a", Members = ["sensor.a", "sensor.b", "sensor.c"], Mode = "joint", Detector = "ecod",
                Params = new Dictionary<string, string> { ["contamination"] = "0.2" },
            },
        };

        var errors = GroupInputValidator.Validate(groups, registry);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_NEstimatorsBelowCatalogMin_ReturnsValidationError()
    {
        // Catalog bounds for iforest's "n_estimators": 10..500.
        var registry = new FakeRegistry(MakeEntry("sensor.a"), MakeEntry("sensor.b"), MakeEntry("sensor.c"));
        var groups = new List<GroupSaveEntry>
        {
            new()
            {
                GroupId = "group.a", Members = ["sensor.a", "sensor.b", "sensor.c"], Mode = "joint", Detector = "iforest",
                Params = new Dictionary<string, string> { ["n_estimators"] = "1" },
            },
        };

        var errors = GroupInputValidator.Validate(groups, registry);

        Assert.NotEmpty(errors);
    }

    // -----------------------------------------------------------------------
    // GroupSaveRequest JSON (de)serialization — camelCase parity with types.ts
    // -----------------------------------------------------------------------

    [Fact]
    public void GroupSaveRequest_DeserializesFromCamelCaseJson()
    {
        var json = """
            {
              "groups": [
                { "groupId": "group.living_room", "friendlyName": "Living Room", "members": ["sensor.a", "sensor.b", "sensor.c"], "mode": "joint", "detector": "ecod", "params": { "contamination": "0.1" } }
              ]
            }
            """;

        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var body = System.Text.Json.JsonSerializer.Deserialize<GroupSaveRequest>(json, options);

        Assert.NotNull(body);
        Assert.Single(body!.Groups);
        Assert.Equal("group.living_room", body.Groups[0].GroupId);
        Assert.Equal(3, body.Groups[0].Members.Count);
        Assert.Equal("ecod", body.Groups[0].Detector);
        Assert.Equal("0.1", body.Groups[0].Params["contamination"]);
    }

    [Fact]
    public void GroupSaveRequest_MalformedJson_ThrowsJsonException()
    {
        var malformed = "{ \"groups\": [ { \"groupId\": "; // truncated

        Assert.Throws<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<GroupSaveRequest>(malformed));
    }

    // -----------------------------------------------------------------------
    // Full pipeline: valid save writes groups: and preserves entities:/_patterns:
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SavePipeline_ValidGroup_WritesGroupsAndPreservesEntitiesAndPatterns()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"argus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var entitiesPath = Path.Combine(tmpDir, "entities.yaml");
        try
        {
            // Seed an existing entities.yaml with entities + _patterns, no groups yet.
            var seedSerializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            var seedRoot = new Dictionary<string, object>
            {
                ["_patterns"] = new Dictionary<string, object> { ["include"] = new List<string> { "sensor.*" }, ["exclude"] = new List<string>() },
                ["entities"] = new List<EntityConfig>
                {
                    new() { EntityId = "sensor.existing", FriendlyName = "", Detectors = [new DetectorConfig { Name = "hst" }] },
                },
            };
            await File.WriteAllTextAsync(entitiesPath, seedSerializer.Serialize(seedRoot));

            var registry = new FakeRegistry(MakeEntry("sensor.a"), MakeEntry("sensor.b"), MakeEntry("sensor.c"));
            var body = new GroupSaveRequest
            {
                Groups =
                [
                    new GroupSaveEntry
                    {
                        GroupId = "group.living_room",
                        FriendlyName = "Living Room",
                        Members = ["sensor.a", "sensor.b", "sensor.c"],
                        Mode = "joint",
                        Detector = "ecod",
                        Params = new Dictionary<string, string> { ["contamination"] = "0.1" },
                    },
                ],
            };

            var live = new LiveEntitiesConfig(EntitiesConfigLoader.Load(entitiesPath,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<EntitiesConfigLoader>.Instance));

            var (ok, count) = await RunGroupSavePipelineAsync(registry, body, entitiesPath, live);

            Assert.True(ok);
            Assert.Equal(1, count);

            // Preserved: entities: still has the original entity
            Assert.Single(live.Get().Entities);
            Assert.Equal("sensor.existing", live.Get().Entities[0].EntityId);

            // Written: groups: now has the new group
            Assert.Single(live.Get().Groups);
            Assert.Equal("group.living_room", live.Get().Groups[0].GroupId);

            // _patterns: preserved on disk (raw re-read, not modeled in EntitiesConfig)
            var finalYaml = await File.ReadAllTextAsync(entitiesPath);
            Assert.Contains("_patterns", finalYaml);
            Assert.Contains("sensor.*", finalYaml);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task SavePipeline_ValidationFailure_DoesNotWrite()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"argus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var entitiesPath = Path.Combine(tmpDir, "entities.yaml");
        try
        {
            var registry = new FakeRegistry(MakeEntry("sensor.a"));
            var body = new GroupSaveRequest
            {
                Groups = [new GroupSaveEntry { GroupId = "group.a", Members = ["sensor.a"], Mode = "joint", Detector = "ecod" }],
            };

            var live = new LiveEntitiesConfig(new EntitiesConfig());
            var (ok, count) = await RunGroupSavePipelineAsync(registry, body, entitiesPath, live);

            Assert.False(ok);
            Assert.False(File.Exists(entitiesPath), "Validation failure must not write entities.yaml");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    /// <summary>Mirrors the POST /api/groups/save handler's core pipeline (minus HTTP plumbing).</summary>
    private static async Task<(bool ok, int count)> RunGroupSavePipelineAsync(
        IHaSensorRegistry registry, GroupSaveRequest body, string entitiesPath, LiveEntitiesConfig liveCfg)
    {
        var validationErrors = GroupInputValidator.Validate(body.Groups, registry);
        if (validationErrors.Count > 0) return (false, 0);

        var groups = body.Groups.Select(g => new GroupConfig
        {
            GroupId = g.GroupId,
            FriendlyName = g.FriendlyName,
            Members = g.Members,
            Mode = g.Mode,
            Detector = g.Detector,
            Params = g.Params,
        }).ToList();

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        object existingPatterns = new Dictionary<string, object>
        {
            ["include"] = new List<string>(),
            ["exclude"] = new List<string>(),
        };
        if (File.Exists(entitiesPath))
        {
            var existingYaml = await File.ReadAllTextAsync(entitiesPath);
            var rawDeserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var rawRoot = rawDeserializer.Deserialize<Dictionary<object, object>>(existingYaml);
            if (rawRoot is not null && rawRoot.TryGetValue("_patterns", out var patternsObj))
                existingPatterns = patternsObj;
        }

        var root = new Dictionary<string, object>
        {
            ["_patterns"] = existingPatterns,
            ["entities"] = liveCfg.Get().Entities,
            ["groups"] = groups,
        };

        var fullYaml = serializer.Serialize(root);
        var writer = new ConfigWriter();
        await writer.WriteAsync(entitiesPath, fullYaml);

        var newConfig = EntitiesConfigLoader.Load(entitiesPath,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EntitiesConfigLoader>.Instance, registry);
        liveCfg.Swap(newConfig);

        return (true, groups.Count);
    }

    // -----------------------------------------------------------------------
    // GET /api/detectors/catalog — static, no gRPC
    // -----------------------------------------------------------------------

    [Fact]
    public void DetectorCatalog_All_ReturnsFiveEntries()
    {
        var entries = DetectorCatalog.All();

        Assert.Equal(5, entries.Count);
        Assert.Contains(entries, e => e.Name == "peer_divergence");
        Assert.Contains(entries, e => e.Name == "ecod");
        Assert.Contains(entries, e => e.Name == "copod");
        Assert.Contains(entries, e => e.Name == "pca");
        Assert.Contains(entries, e => e.Name == "iforest");
    }

    [Fact]
    public void DetectorCatalog_EachEntry_HasExactlyThreePresetsAndNonEmptyBestFor()
    {
        foreach (var entry in DetectorCatalog.All())
        {
            Assert.Equal(3, entry.Presets.Count);
            Assert.False(string.IsNullOrWhiteSpace(entry.BestFor));
        }
    }

    [Fact]
    public void DetectorCatalog_Guided_MapsBothAnswers()
    {
        // ALGO-05: "together" recommends copod, not ecod — empirical PyOD testing found ECOD
        // produces ~90% false positives on correlated-pair relationship-break scenarios.
        var guided = DetectorCatalog.Guided();

        Assert.Contains(guided, g => g.Answer == "together" && g.Detector == "copod");
        Assert.Contains(guided, g => g.Answer == "diverges" && g.Detector == "peer_divergence");
    }

    [Fact]
    public void DetectorCatalog_PeerDivergenceBestFor_HasTwoMemberAttributionCaveat()
    {
        // ALGO-06 / Open Design Question #5: a 2-member peer_divergence group reports a single
        // pair-relationship verdict with no per-member attribution — the copy must say so, not
        // imply "know WHICH member is diverging" applies universally.
        var entry = DetectorCatalog.All().Single(e => e.Name == "peer_divergence");

        Assert.Contains("2 members", entry.BestFor);
        Assert.Contains("no per-member attribution", entry.BestFor);
    }

    // -----------------------------------------------------------------------
    // GET /api/groups/{id}/status — 200-with-null for unknown id
    // -----------------------------------------------------------------------

    [Fact]
    public void GroupStatusCache_UnknownId_ReturnsNullStatus()
    {
        var cache = new GroupStatusCache();

        var entry = cache.Get("group.unknown");

        Assert.Null(entry);
    }

    [Fact]
    public void GroupStatusCache_KnownId_ReturnsSortedContributions()
    {
        var cache = new GroupStatusCache();
        cache.Set(new GroupStatusEntry(
            "group.a", 0.5, true, "ecod", DateTimeOffset.UtcNow,
            [new FeatureContributionDto("sensor.b", 0.9), new FeatureContributionDto("sensor.a", 0.1)]));

        var entry = cache.Get("group.a");

        Assert.NotNull(entry);
        Assert.Equal("sensor.b", entry!.Contributions[0].MemberId);
    }
}
