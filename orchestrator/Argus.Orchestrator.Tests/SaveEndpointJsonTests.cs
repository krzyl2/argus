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
    // G-14-1 class, detectors: edition — a save must not rewrite entities the
    // SPA never had in its editor
    // -----------------------------------------------------------------------

    /// <summary>
    /// The SPA fills entityEdits from its LAST GET /api/sensors, so a save made while the
    /// screen is filtered (?q=lodowka) sends rows for the matching entities only. Everything
    /// else still resolves through the patterns and is rewritten anyway — and the old
    /// "no row -> [rmad, {}]" fallback threw that configuration away without a word,
    /// including the entities EntitiesSchemaMigrator deliberately left on hst as tuned.
    ///
    /// The rule under test is "an entity the body never mentioned keeps what is on disk",
    /// not "the fallback happens to be hst": the assertion is against the seeded block.
    /// </summary>
    [Fact]
    public async Task SavePipeline_EntityAbsentFromBody_KeepsItsStoredDetectors()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"argus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var entitiesPath = Path.Combine(tmpDir, "entities.yaml");
        try
        {
            var tunedParams = new Dictionary<string, string>
            {
                ["window"] = "250",
                ["n_trees"] = "25",
                ["high_threshold"] = "0.82",
                ["low_threshold"] = "0.41",
                ["min_consecutive"] = "4",
                ["frozen_window"] = "10",
                ["frozen_variance_threshold"] = "0.0",
            };

            var seedSerializer = new YamlDotNet.Serialization.SerializerBuilder()
                .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
                .Build();
            var seedRoot = new Dictionary<string, object>
            {
                ["_patterns"] = new Dictionary<string, object>
                {
                    ["include"] = new List<string> { "sensor.*" },
                    ["exclude"] = new List<string>(),
                },
                ["entities"] = new List<EntityConfig>
                {
                    new()
                    {
                        EntityId = "sensor.zamrazarka_power",
                        FriendlyName = "Zamrażarka",
                        Detectors = [new DetectorConfig { Name = "hst", Params = tunedParams }],
                    },
                    new()
                    {
                        EntityId = "sensor.lodowka_power",
                        FriendlyName = "Lodówka",
                        Detectors = [new DetectorConfig { Name = "rmad", Params = new Dictionary<string, string>() }],
                    },
                },
            };
            await File.WriteAllTextAsync(entitiesPath, seedSerializer.Serialize(seedRoot));

            var live = new LiveEntitiesConfig(EntitiesConfigLoader.Load(entitiesPath,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<EntitiesConfigLoader>.Instance));

            // Both entities exist in HA and both resolve through the include pattern — but the
            // operator was filtered down to the fridge, so only that row is in the body.
            var registry = new FakeRegistry(
                MakeEntry("sensor.lodowka_power", "Lodówka"),
                MakeEntry("sensor.zamrazarka_power", "Zamrażarka"));

            var body = new SaveRequest
            {
                Entities =
                [
                    new SaveEntity
                    {
                        EntityId = "sensor.lodowka_power",
                        Detectors = [new SaveDetector
                        {
                            Name = "rmad",
                            Params = new()
                            {
                                ["window"] = "720",
                                ["min_samples"] = "60",
                                ["z_scale"] = "5.0",
                                ["scale_floor"] = "0.0",
                                ["high_threshold"] = "0.5",
                                ["low_threshold"] = "0.375",
                                ["min_consecutive"] = "3",
                                ["frozen_window"] = "10",
                                ["frozen_variance_threshold"] = "0.0",
                            }
                        }]
                    }
                ],
                Include = "sensor.*",
                Exclude = "",
            };

            await RunSavePipelineAsync(registry, body, entitiesPath, live);

            var reloaded = live.Get();
            var untouched = Assert.Single(reloaded.Entities, e => e.EntityId == "sensor.zamrazarka_power");
            Assert.Single(untouched.Detectors);
            Assert.Equal("hst", untouched.Detectors[0].Name);
            Assert.Equal(tunedParams, untouched.Detectors[0].Params);

            // The entity the operator DID edit still takes the submitted block.
            var edited = Assert.Single(reloaded.Entities, e => e.EntityId == "sensor.lodowka_power");
            Assert.Equal("rmad", edited.Detectors[0].Name);
            Assert.Equal("0.5", edited.Detectors[0].Params["high_threshold"]);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    /// <summary>
    /// The other half of the rule: an entity the body DOES carry with an empty detector list is
    /// the operator having removed every detector from a row they are looking at. That must land
    /// on the rmad default (D-A) — never resurrect what happened to be on disk, or a removal
    /// could not be saved at all.
    /// </summary>
    [Fact]
    public void ResolveDetectors_SubmittedEmptyList_DefaultsToRmadInsteadOfRestoringDisk()
    {
        var submitted = new Dictionary<string, List<DetectorConfig>>(StringComparer.OrdinalIgnoreCase)
        {
            ["sensor.a"] = [],
        };
        var preSave = new Dictionary<string, EntityConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["sensor.a"] = new()
            {
                EntityId = "sensor.a",
                Detectors = [new DetectorConfig { Name = "hst", Params = new Dictionary<string, string>() }],
            },
        };

        var resolved = SensorTracking.ResolveDetectors("sensor.a", submitted, preSave);

        Assert.Equal("rmad", Assert.Single(resolved).Name);
    }

    /// <summary>
    /// The save path keys the same detector rows twice: by entity INDEX for InputValidator, by
    /// entity ID for the detector decision. Nothing pinned that the two agree, so a future edit
    /// could validate one entity's params and write another's.
    ///
    /// The rule: for every entity the body carries, the block at index i is the block stored
    /// under sortedIds[i] — the SAME list instance, not merely an equal one. For an entity the
    /// body does not carry, the index map holds an empty list, which is precisely why it cannot
    /// be used to decide detectors: "absent" and "submitted empty" collapse into one value.
    /// </summary>
    [Fact]
    public void ByEntityIndex_MatchesByEntityId_ForEverySubmittedEntity()
    {
        var body = new SaveRequest
        {
            Entities =
            [
                new SaveEntity
                {
                    EntityId = "sensor.zzz_last",
                    Detectors = [new SaveDetector { Name = "mad", Params = new() { ["threshold"] = "3.5" } }],
                },
                new SaveEntity
                {
                    EntityId = "sensor.aaa_first",
                    Detectors = [new SaveDetector { Name = "rmad", Params = new() { ["window"] = "240" } }],
                },
            ],
        };

        // Resolution order is not body order: an id the body never mentioned sorts in between.
        var sortedIds = SaveProjection.SortIds(
            ["sensor.zzz_last", "sensor.mmm_absent", "sensor.aaa_first"]);
        var byId = SaveProjection.SubmittedByEntityId(body);
        var byIndex = SaveProjection.ByEntityIndex(sortedIds, byId);

        Assert.Equal(sortedIds.Count, byIndex.Count);
        for (var ei = 0; ei < sortedIds.Count; ei++)
        {
            if (byId.TryGetValue(sortedIds[ei], out var submitted))
                Assert.Same(submitted, byIndex[ei]);
            else
                Assert.Empty(byIndex[ei]);
        }

        // And the pairing really is index -> sorted position, not index -> body position.
        Assert.Equal("rmad", byIndex[0][0].Name);
        Assert.Empty(byIndex[1]);
        Assert.Equal("mad", byIndex[2][0].Name);
    }

    /// <summary>
    /// Consequence of the "absent from the body keeps what is on disk" rule, spelled out because
    /// it changes what a broken entities.yaml does: only the BODY goes through InputValidator, so
    /// a stored block the validator would reject is written back verbatim instead of being reset
    /// to rmad. That direction is the intended one — a hand-edited file is preserved rather than
    /// silently overwritten — and it is safe because the block already passed EntitiesConfigLoader.
    ///
    /// The test exists so that flipping it back to "validate everything, drop what fails" cannot
    /// happen quietly: it would be indistinguishable from the configuration loss this fix removed.
    /// </summary>
    [Fact]
    public void SavePipeline_StoredBlockRejectedByValidator_IsPreservedNotResetToRmad()
    {
        // window = 5 is below the rmad floor of 30: InputValidator rejects this in a POST body.
        var handEdited = new Dictionary<string, string> { ["window"] = "5" };

        var byIndexIfItHadBeenSubmitted = SaveProjection.ByEntityIndex(
            ["sensor.hand_edited"],
            new Dictionary<string, List<DetectorConfig>>(StringComparer.OrdinalIgnoreCase)
            {
                ["sensor.hand_edited"] = [new DetectorConfig { Name = "rmad", Params = handEdited }],
            });
        Assert.NotEmpty(InputValidator.Validate(["sensor.hand_edited"], byIndexIfItHadBeenSubmitted));

        // Same block, but reached through the on-disk config with an empty body.
        var preSaveById = SaveProjection.ByEntityId(new EntitiesConfig
        {
            Entities =
            [
                new EntityConfig
                {
                    EntityId = "sensor.hand_edited",
                    Detectors = [new DetectorConfig { Name = "rmad", Params = handEdited }],
                },
            ],
        });

        var built = SaveProjection.BuildEntities(
            ["sensor.hand_edited"],
            new Dictionary<string, List<DetectorConfig>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, HaSensorEntry>(StringComparer.OrdinalIgnoreCase),
            preSaveById);

        var written = Assert.Single(built);
        Assert.Equal("rmad", Assert.Single(written.Detectors).Name);
        Assert.Equal(handEdited, written.Detectors[0].Params);
    }

    // -----------------------------------------------------------------------
    // Pipeline harness — the SaveProjection sequence Program.cs's POST
    // /api/sensors/save handler runs, around it
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

        // Every projection step below is the PRODUCTION method, not a copy of it. A harness that
        // reimplements the handler cannot fail when the handler changes, which is how the
        // "entity absent from the body keeps its stored detectors" rule ended up unpinned.
        var detectorsByEntityId = SaveProjection.SubmittedByEntityId(body);

        var sortedIds = SaveProjection.SortIds(resolvedIds);

        var parsedDetectors = SaveProjection.ByEntityIndex(sortedIds, detectorsByEntityId);

        var validationErrors = InputValidator.Validate(resolvedIds, parsedDetectors);
        if (validationErrors.Count > 0)
        {
            return (false, "validation", 0, false, validationErrors.Count);
        }

        var snapshotById = registry.GetAll().ToDictionary(e => e.EntityId, StringComparer.OrdinalIgnoreCase);
        var preSaveById = SaveProjection.ByEntityId(liveCfg?.Get() ?? new EntitiesConfig());
        var entities = SaveProjection.BuildEntities(sortedIds, detectorsByEntityId, snapshotById, preSaveById);

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
