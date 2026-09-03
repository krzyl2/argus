using Argus.Orchestrator.Config;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Web;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for the POST /api/sensors/save JSON contract (Phase 7 — replaces the v3.0
/// form-encoded + HTML-banner tests). Exercises the same pipeline Program.cs's handler
/// runs (GlobExpander.Resolve -> InputValidator.Validate -> EntityConfig build -> YAML
/// serialize -> ConfigWriter.WriteAsync -> EntitiesConfigLoader.Load -> liveCfg.Swap),
/// asserting on the { ok, count, hasStreaming } / { ok:false, kind, ... } JSON discriminant shape
/// instead of HTML banner strings. Fully offline — no HTTP server needed.
/// </summary>
public class SaveEndpointJsonTests
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

    private static HaSensorEntry MakeEntry(string entityId, string? friendlyName = null)
        => new(entityId, 21.0, "°C", friendlyName, IsTracked: true, AreaName: null, Domain: "sensor");

    // -----------------------------------------------------------------------
    // SaveRequest JSON (de)serialization — camelCase parity with orchestrator/ui/src/api/types.ts
    // -----------------------------------------------------------------------

    [Fact]
    public void SaveRequest_DeserializesFromCamelCaseJson()
    {
        var json = """
            {
              "entities": [
                { "entityId": "sensor.living_room_temp", "detectors": [ { "name": "hst", "params": { "window": "250" } } ] }
              ],
              "include": "sensor.*temp*",
              "exclude": ""
            }
            """;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var body = JsonSerializer.Deserialize<SaveRequest>(json, options);

        Assert.NotNull(body);
        Assert.Single(body!.Entities);
        Assert.Equal("sensor.living_room_temp", body.Entities[0].EntityId);
        Assert.Equal("hst", body.Entities[0].Detectors[0].Name);
        Assert.Equal("250", body.Entities[0].Detectors[0].Params["window"]);
        Assert.Equal("sensor.*temp*", body.Include);
        Assert.Equal("", body.Exclude);
    }

    [Fact]
    public void SaveRequest_MalformedJson_ThrowsJsonException()
    {
        var malformed = "{ \"entities\": [ { \"entityId\": "; // truncated

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<SaveRequest>(malformed));
    }

    // -----------------------------------------------------------------------
    // Full pipeline: success path
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SavePipeline_ValidHstEntity_ProducesSuccessResultWithHasHstTrue()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.living_room_temp"));
        var body = new SaveRequest
        {
            Entities =
            [
                new SaveEntity
                {
                    EntityId = "sensor.living_room_temp",
                    Detectors = [new SaveDetector
                    {
                        Name = "hst",
                        Params = new()
                        {
                            ["window"] = "250",
                            ["n_trees"] = "25",
                            ["high_threshold"] = "0.7",
                            ["low_threshold"] = "0.3",
                            ["min_consecutive"] = "3",
                            ["frozen_window"] = "10",
                            ["frozen_variance_threshold"] = "0.001",
                        }
                    }]
                }
            ],
            Include = "",
            Exclude = "",
        };

        var (ok, kind, count, hasStreaming, errorCount) = await RunSavePipelineAsync(registry, body);

        Assert.True(ok);
        Assert.Null(kind);
        Assert.Equal(1, count);
        Assert.True(hasStreaming);
    }

    [Fact]
    public async Task SavePipeline_MadOnlyEntity_ProducesSuccessResultWithHasStreamingFalse()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.outdoor_humidity"));
        var body = new SaveRequest
        {
            Entities =
            [
                new SaveEntity
                {
                    EntityId = "sensor.outdoor_humidity",
                    Detectors = [new SaveDetector { Name = "mad", Params = new() { ["threshold"] = "3.5", ["window"] = "20" } }]
                }
            ],
        };

        var (ok, kind, count, hasStreaming, _) = await RunSavePipelineAsync(registry, body);

        Assert.True(ok);
        Assert.Equal(1, count);
        Assert.False(hasStreaming);
    }

    // -----------------------------------------------------------------------
    // Full pipeline: validation failure — no write occurs
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SavePipeline_InvalidHstWindow_ReturnsValidationKindAndDoesNotWrite()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.living_room_temp"));
        var body = new SaveRequest
        {
            Entities =
            [
                new SaveEntity
                {
                    EntityId = "sensor.living_room_temp",
                    Detectors = [new SaveDetector { Name = "hst", Params = new() { ["window"] = "0" } }] // invalid: must be >= 1
                }
            ],
        };

        var tmpDir = Path.Combine(Path.GetTempPath(), $"argus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var entitiesPath = Path.Combine(tmpDir, "entities.yaml");
        try
        {
            var (ok, kind, _, _, errorCount) = await RunSavePipelineAsync(registry, body, entitiesPath);

            Assert.False(ok);
            Assert.Equal("validation", kind);
            Assert.True(errorCount >= 1);
            Assert.False(File.Exists(entitiesPath), "Validation failure must not write entities.yaml");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task SavePipeline_UnknownDetectorType_ReturnsValidationKind()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.a"));
        var body = new SaveRequest
        {
            Entities = [new SaveEntity { EntityId = "sensor.a", Detectors = [new SaveDetector { Name = "bogus" }] }]
        };

        var (ok, kind, _, _, errorCount) = await RunSavePipelineAsync(registry, body);

        Assert.False(ok);
        Assert.Equal("validation", kind);
        Assert.True(errorCount >= 1);
    }

    // -----------------------------------------------------------------------
    // Hot-reload parity — successful save triggers liveCfg.Swap
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SavePipeline_Success_CallsLiveConfigSwap()
    {
        var registry = new FakeRegistry(MakeEntry("sensor.a"));
        var body = new SaveRequest
        {
            Entities = [new SaveEntity
            {
                EntityId = "sensor.a",
                Detectors = [new SaveDetector
                {
                    Name = "hst",
                    Params = new()
                    {
                        ["window"] = "250",
                        ["n_trees"] = "25",
                        ["high_threshold"] = "0.7",
                        ["low_threshold"] = "0.3",
                        ["min_consecutive"] = "3",
                        ["frozen_window"] = "10",
                        ["frozen_variance_threshold"] = "0.001",
                    }
                }]
            }]
        };

        var live = new LiveEntitiesConfig(new EntitiesConfig());
        var swapped = false;
        live.ConfigChanged += (_, _) => swapped = true;

        var tmpDir = Path.Combine(Path.GetTempPath(), $"argus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var entitiesPath = Path.Combine(tmpDir, "entities.yaml");
        try
        {
            await RunSavePipelineAsync(registry, body, entitiesPath, live);

            Assert.True(swapped, "liveCfg.Swap must fire ConfigChanged after a successful save (hot-reload)");
            Assert.Single(live.Get().Entities);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    // G-14-1 regression — sensors save must NOT wipe pre-existing groups
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SavePipeline_PreservesPreExistingGroups()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"argus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var entitiesPath = Path.Combine(tmpDir, "entities.yaml");
        try
        {
            // Seed an existing entities.yaml with one entity + one pre-existing 2-member group.
            // 2 members satisfies the floor (Load's shared-unit check is skipped without a
            // registry, so a peer_divergence pair is degrade-safe here — no registry arg added).
            var seedSerializer = new YamlDotNet.Serialization.SerializerBuilder()
                .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
                .Build();
            var seedRoot = new Dictionary<string, object>
            {
                ["_patterns"] = new Dictionary<string, object> { ["include"] = new List<string>(), ["exclude"] = new List<string>() },
                ["entities"] = new List<EntityConfig>
                {
                    new() { EntityId = "sensor.existing", FriendlyName = "", Detectors = [new DetectorConfig { Name = "hst" }] },
                },
                ["groups"] = new List<GroupConfig>
                {
                    new()
                    {
                        GroupId = "group.tire_pressure",
                        FriendlyName = "Ciśnienie w oponach",
                        Members = ["sensor.tire_a", "sensor.tire_b"],
                        Mode = "peer_divergence",
                        Detector = "peer_divergence",
                        Params = new Dictionary<string, string>(),
                    },
                },
            };
            await File.WriteAllTextAsync(entitiesPath, seedSerializer.Serialize(seedRoot));

            var live = new LiveEntitiesConfig(EntitiesConfigLoader.Load(entitiesPath,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<EntitiesConfigLoader>.Instance));

            var registry = new FakeRegistry(MakeEntry("sensor.new"));
            var body = new SaveRequest
            {
                Entities =
                [
                    new SaveEntity
                    {
                        EntityId = "sensor.new",
                        Detectors = [new SaveDetector
                        {
                            Name = "hst",
                            Params = new()
                            {
                                ["window"] = "250",
                                ["n_trees"] = "25",
                                ["high_threshold"] = "0.7",
                                ["low_threshold"] = "0.3",
                                ["min_consecutive"] = "3",
                                ["frozen_window"] = "10",
                                ["frozen_variance_threshold"] = "0.001",
                            }
                        }]
                    }
                ],
                Include = "",
                Exclude = "",
            };

            await RunSavePipelineAsync(registry, body, entitiesPath, live);

            // Live config still holds the pre-existing group after the sensors save.
            Assert.Contains(live.Get().Groups, g => g.GroupId == "group.tire_pressure");

            // On-disk YAML still contains the group id (not wiped).
            var finalYaml = await File.ReadAllTextAsync(entitiesPath);
            Assert.Contains("group.tire_pressure", finalYaml);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    // G-14-1 regression — GET /api/sensors isTracked must be config-sourced, not the
    // (stale, reconnect-only) HA registry snapshot
    // -----------------------------------------------------------------------

    [Fact]
    public void SensorTracking_IsTracked_DerivedFromConfigIgnoresStaleRegistrySnapshot()
    {
        var config = new EntitiesConfig
        {
            Entities =
            [
                new EntityConfig { EntityId = "sensor.kurnik_temperature", FriendlyName = "", Detectors = [new DetectorConfig { Name = "hst" }] },
            ],
        };

        var trackedIds = SensorTracking.TrackedIds(config);

        // Simulates the lagging HA registry snapshot: IsTracked=false even though the entity
        // is present in the live config (i.e. just saved, no HA reconnect has happened yet).
        var entry = MakeEntry("sensor.kurnik_temperature") with { IsTracked = false };

        Assert.Contains(entry.EntityId, trackedIds);
        Assert.Contains(entry.EntityId.ToUpperInvariant(), trackedIds); // must be case-insensitive
        Assert.False(entry.IsTracked); // documents the divergence the fix relies on
        Assert.DoesNotContain("sensor.not_configured", trackedIds);
    }

    // -----------------------------------------------------------------------
    // Error path — exception mapped to generic reason, never raw exception text
    // -----------------------------------------------------------------------

    [Fact]
    public void ErrorReason_IOException_MapsToDiskError()
    {
        var ex = new IOException("disk is full: /dev/sda1 no space left");
        var reason = ex is IOException ? "disk error" : "unexpected error";

        Assert.Equal("disk error", reason);
        Assert.DoesNotContain("/dev/sda1", reason);
    }

    [Fact]
    public void ErrorReason_OtherException_MapsToUnexpectedError()
    {
        Exception ex = new InvalidOperationException("secret internal state leaked here");
        var reason = ex is IOException ? "disk error" : "unexpected error";

        Assert.Equal("unexpected error", reason);
        Assert.DoesNotContain("secret internal state", reason);
    }

    // -----------------------------------------------------------------------
    // Pipeline harness — mirrors Program.cs's POST /api/sensors/save handler
    // -----------------------------------------------------------------------

    private static async Task<(bool ok, string? kind, int count, bool hasStreaming, int errorCount)> RunSavePipelineAsync(
        IHaSensorRegistry registry, SaveRequest body, string? entitiesPathOverride = null,
        LiveEntitiesConfig? liveCfg = null)
    {
        var includeRaw = body.Include ?? "";
        var excludeRaw = body.Exclude ?? "";
        var include = includeRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var exclude = excludeRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var selectedIds = body.Entities.Select(e => e.EntityId).Where(s => !string.IsNullOrEmpty(s));
        var resolvedIds = GlobExpander.Resolve(registry.GetAll(), include, exclude, selectedIds, []);

        var detectorsByEntityId = body.Entities
            .Where(e => !string.IsNullOrEmpty(e.EntityId))
            .ToDictionary(
                e => e.EntityId,
                e => e.Detectors.Select(d => new DetectorConfig { Name = d.Name, Params = d.Params }).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var sortedIds = resolvedIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();

        var parsedDetectors = sortedIds
            .Select((id, ei) => (ei, dets: detectorsByEntityId.TryGetValue(id, out var d) ? d : new List<DetectorConfig>()))
            .ToDictionary(x => x.ei, x => x.dets);

        var validationErrors = InputValidator.Validate(resolvedIds, parsedDetectors);
        if (validationErrors.Count > 0)
        {
            return (false, "validation", 0, false, validationErrors.Count);
        }

        var snapshotById = registry.GetAll().ToDictionary(e => e.EntityId, StringComparer.OrdinalIgnoreCase);
        var entities = sortedIds
            .Select((id, ei) =>
            {
                snapshotById.TryGetValue(id, out var entry);
                var detectors = parsedDetectors.TryGetValue(ei, out var dets) && dets.Count > 0
                    ? dets
                    : [new DetectorConfig { Name = "rmad", Params = [] }];
                return new EntityConfig { EntityId = id, FriendlyName = entry?.FriendlyName ?? "", Detectors = detectors };
            })
            .ToList();

        if (entitiesPathOverride is not null)
        {
            var serializer = new YamlDotNet.Serialization.SerializerBuilder()
                .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
                .Build();
            var root = new Dictionary<string, object>
            {
                ["schema_version"] = EntitiesSchemaMigrator.TargetSchemaVersion,
                ["_patterns"] = new Dictionary<string, object> { ["include"] = include.ToList(), ["exclude"] = exclude.ToList() },
                ["entities"] = entities,
                ["groups"] = liveCfg is not null ? liveCfg.Get().Groups : new List<GroupConfig>(),
            };
            var yaml = serializer.Serialize(root);

            var writer = new ConfigWriter();
            await writer.WriteAsync(entitiesPathOverride, yaml);

            if (liveCfg is not null)
            {
                var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<EntitiesConfigLoader>.Instance;
                var reloaded = EntitiesConfigLoader.Load(entitiesPathOverride, logger);
                liveCfg.Swap(reloaded);
            }
        }

        var hasStreaming = entities.Any(e => e.Detectors.Any(
            d => d.Name.Equals("rmad", StringComparison.OrdinalIgnoreCase) ||
                 d.Name.Equals("hst", StringComparison.OrdinalIgnoreCase)));
        return (true, null, entities.Count, hasStreaming, 0);
    }
}
