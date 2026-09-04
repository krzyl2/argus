using Argus.Orchestrator.Config;
using Argus.Orchestrator.Mqtt;
using Argus.Orchestrator.Web;
using Microsoft.Extensions.Logging;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// The migration is one-way and touches the operator's live config, so these tests pin the
/// three things that make it safe rather than merely working: it rewrites ONLY blocks it can
/// prove were never tuned, it never loses data it did not author (groups, _patterns, the
/// backup), and it is a true no-op on every boot after the first — because every write is a
/// rename, and every rename is a Swap that resets every entity's alert gate.
/// </summary>
public class EntitiesSchemaMigratorTests : IDisposable
{
    private readonly string _dir;

    public EntitiesSchemaMigratorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "argus-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly ILogger Silent =
        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>Captures log lines so tests can assert what the operator is actually told.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));

        public int Count(LogLevel level, string fragment)
            => Lines.Count(l => l.Level == level && l.Message.Contains(fragment, StringComparison.Ordinal));
    }

    private string WritePath(string yaml, string name = "entities.yaml")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, yaml);
        return path;
    }

    /// <summary>The exact block the UI wrote for a never-tuned hst entity — the F0 fingerprint.</summary>
    private const string PristineHstBlock = """
              - name: hst
                params:
                  window: "250"
                  n_trees: "25"
                  high_threshold: "0.7"
                  low_threshold: "0.3"
                  min_consecutive: "3"
                  frozen_window: "10"
                  frozen_variance_threshold: "0.001"
        """;

    /// <summary>The five sensors measured in F0, all on the pristine legacy block.</summary>
    private static string F0Yaml()
    {
        var ids = new[]
        {
            "sensor.load_5m", "sensor.memory_use_percent", "sensor.processor_use",
            "sensor.lodowkababcia_power", "sensor.zamrazarkapiwnica_power",
        };
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("_patterns:");
        sb.AppendLine("  include: []");
        sb.AppendLine("  exclude: []");
        sb.AppendLine("entities:");
        foreach (var id in ids)
        {
            sb.AppendLine($"  - entity_id: {id}");
            sb.AppendLine("    friendly_name: \"\"");
            sb.AppendLine("    detectors:");
            sb.AppendLine(PristineHstBlock.TrimEnd());
        }
        sb.AppendLine("groups: []");
        return sb.ToString();
    }

    private static EntitiesConfig Load(string path) => EntitiesConfigLoader.Load(path, Silent);

    private static Dictionary<object, object> RawRoot(string path)
        => new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<Dictionary<object, object>>(File.ReadAllText(path));

    // -----------------------------------------------------------------------
    // The happy path
    // -----------------------------------------------------------------------

    /// <summary>
    /// The whole point of the fix, at the config layer: five sensors that were each producing a
    /// permanently-ON binary_sensor come out on ONE default table, identical on all five,
    /// without a single per-sensor number. If this needed per-sensor tuning, F6 would not be
    /// fixed — it would just have moved.
    /// </summary>
    [Fact]
    public void PristineHstEntity_MigratesToRmadWithMedPresetThresholds()
    {
        var path = WritePath(F0Yaml());

        Assert.True(EntitiesSchemaMigrator.MigrateIfNeeded(path, Silent));

        var config = Load(path);
        Assert.Equal(5, config.Entities.Count);
        foreach (var entity in config.Entities)
        {
            var det = Assert.Single(entity.Detectors);
            Assert.Equal("rmad", det.Name);
            Assert.Equal("0.5", det.Params["high_threshold"]);
            Assert.Equal("0.375", det.Params["low_threshold"]);
            Assert.Equal("720", det.Params["window"]);
            Assert.Equal("60", det.Params["min_samples"]);
            Assert.Equal("5.0", det.Params["z_scale"]);
            Assert.Equal("3", det.Params["min_consecutive"]);
        }

        Assert.Equal("2", RawRoot(path)["schema_version"].ToString());
        // The stamp must be the FIRST key, so an operator reading the file sees it immediately.
        Assert.StartsWith("schema_version:", File.ReadAllText(path).TrimStart());
    }

    /// <summary>
    /// D-H. Frozen is switched off by the variance threshold on BOTH branches — migrated and
    /// left-on-hst. The fridge is 88% zeros, so with frozen 10/0.001 a ten-sample window of
    /// 0 W has variance 0 and IsFrozen latches for the whole compressor rest; frozen also
    /// bypasses warm-up, suppression and hysteresis on its way to forcing the flag ON, and can
    /// only be cleared by three scores below 0.3, which F2 proves never arrive.
    ///
    /// frozen_window must be carried over VERBATIM and never written as "0": with a window of 0
    /// FrozenSensorDetector dequeues an empty queue on the first reading, and InputValidator
    /// rejects anything below 1, so the entity could never be saved from the UI again.
    /// </summary>
    [Fact]
    public void FrozenDisabledByVarianceOnBothBranches()
    {
        var yaml = """
            entities:
              - entity_id: sensor.pristine
                friendly_name: ""
                detectors:
                  - name: hst
                    params:
                      window: "250"
                      n_trees: "25"
                      high_threshold: "0.7"
                      low_threshold: "0.3"
                      min_consecutive: "3"
                      frozen_window: "10"
                      frozen_variance_threshold: "0.001"
              - entity_id: sensor.tuned
                friendly_name: ""
                detectors:
                  - name: hst
                    params:
                      window: "500"
                      n_trees: "25"
                      high_threshold: "0.9"
                      low_threshold: "0.3"
                      min_consecutive: "3"
                      frozen_window: "10"
                      frozen_variance_threshold: "0.001"
            groups: []
            """;
        var path = WritePath(yaml);

        Assert.True(EntitiesSchemaMigrator.MigrateIfNeeded(path, Silent));

        var config = Load(path);
        var pristine = config.Entities.Single(e => e.EntityId == "sensor.pristine").Detectors[0];
        var tuned = config.Entities.Single(e => e.EntityId == "sensor.tuned").Detectors[0];

        Assert.Equal("rmad", pristine.Name);
        Assert.Equal("hst", tuned.Name);       // tuned stays put — but frozen still goes off
        Assert.Equal("0.0", pristine.Params["frozen_variance_threshold"]);
        Assert.Equal("0.0", tuned.Params["frozen_variance_threshold"]);
        Assert.Equal("10", pristine.Params["frozen_window"]);
        Assert.Equal("10", tuned.Params["frozen_window"]);

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("0.001", text);
        Assert.DoesNotContain("frozen_window: \"0\"", text);
    }

    /// <summary>
    /// D-H again, on the branch the "BOTH branches" wording did not name: an entity with MORE
    /// than one detector. The detector rewrite is deliberately skipped there (an operator who
    /// picked [hst, mad] chose that), but the skip must not carry frozen along with it.
    ///
    /// The rule pinned here is a property of the RUNTIME, not of the migrator's shape:
    /// ScoreStreamPipeline resolves the streaming detector as the first hst-or-rmad entry and
    /// reads FrozenSensorDetector's params off that same block. So whichever block the pipeline
    /// would pick must come out of the migration with frozen arithmetically dead — otherwise a
    /// fridge configured as [hst, mad] latches ON for every compressor rest, past warm-up: the
    /// exact F1 state D-H exists to end.
    /// </summary>
    [Fact]
    public void MultiDetectorEntity_StillGetsFrozenDisabled()
    {
        var yaml = """
            entities:
              - entity_id: sensor.lodowkababcia_power
                friendly_name: ""
                detectors:
                  - name: hst
                    params:
                      window: "250"
                      n_trees: "25"
                      high_threshold: "0.7"
                      low_threshold: "0.3"
                      min_consecutive: "3"
                      frozen_window: "10"
                      frozen_variance_threshold: "0.001"
                  - name: mad
                    params:
                      window: "100"
                      threshold: "3.0"
            groups: []
            """;
        var path = WritePath(yaml);
        var log = new RecordingLogger();

        Assert.True(EntitiesSchemaMigrator.MigrateIfNeeded(path, log));

        var entity = Load(path).Entities.Single();
        Assert.Equal(2, entity.Detectors.Count);

        // Resolved the SAME way ScoreStreamPipeline.BuildEntityStates resolves it, so the
        // assertion follows the runtime rather than the config's index order.
        var streaming = entity.Detectors.First(d => d.Name is "rmad" or "hst");

        Assert.Equal("0.0", streaming.Params["frozen_variance_threshold"]);
        Assert.Equal("10", streaming.Params["frozen_window"]);   // verbatim, never "0"

        // Not a rewrite: the operator's own detector choice survives untouched, and they are
        // not told they "tuned hst".
        Assert.Equal("hst", entity.Detectors[0].Name);
        Assert.Equal("mad", entity.Detectors[1].Name);
        Assert.Equal("3.0", entity.Detectors[1].Params["threshold"]);
        Assert.False(entity.Detectors[1].Params.ContainsKey("frozen_variance_threshold"));
        Assert.Equal(0, log.Count(LogLevel.Warning, "tuned hst params"));
    }

    // -----------------------------------------------------------------------
    // What must NOT be touched
    // -----------------------------------------------------------------------

    /// <summary>
    /// There is no meaning-preserving mapping from an absolute HST rarity threshold to a
    /// robust-z threshold: 0.9 on an HST mass means "rarer than 90% of the tree population",
    /// while 0.9 on rmad means "robust z above 45". Rewriting a tuned entity would be a guess
    /// wearing a migration's clothes, so it is left alone and NAMED — silence here would be a
    /// sensor the operator tuned deliberately, quietly running something else.
    /// </summary>
    [Fact]
    public void TunedHstEntity_IsLeftOnHstAndWarns()
    {
        var yaml = """
            entities:
              - entity_id: sensor.tuned
                friendly_name: ""
                detectors:
                  - name: hst
                    params:
                      window: "500"
                      n_trees: "40"
                      high_threshold: "0.9"
                      low_threshold: "0.2"
                      min_consecutive: "5"
                      frozen_window: "10"
                      frozen_variance_threshold: "0.001"
            groups: []
            """;
        var path = WritePath(yaml);
        var log = new RecordingLogger();

        Assert.True(EntitiesSchemaMigrator.MigrateIfNeeded(path, log));

        var det = Load(path).Entities[0].Detectors[0];
        Assert.Equal("hst", det.Name);
        Assert.Equal("500", det.Params["window"]);
        Assert.Equal("0.9", det.Params["high_threshold"]);
        Assert.Equal(1, log.Count(LogLevel.Warning, "tuned hst params"));
    }

    /// <summary>
    /// An entity already on rmad did nothing wrong and has nothing to act on, so it must be a
    /// SILENT skip. A "tuned hst" warning here would train the operator to ignore the warning
    /// that actually matters.
    /// </summary>
    [Fact]
    public void RmadEntity_IsSilentSkip_NoTunedWarning()
    {
        var yaml = """
            entities:
              - entity_id: sensor.already
                friendly_name: ""
                detectors:
                  - name: rmad
                    params:
                      window: "720"
                      min_samples: "60"
                      z_scale: "5.0"
                      scale_floor: "0.0"
                      high_threshold: "0.5"
                      low_threshold: "0.375"
                      min_consecutive: "3"
                      frozen_window: "10"
                      frozen_variance_threshold: "0.0"
            groups: []
            """;
        var path = WritePath(yaml);
        var log = new RecordingLogger();

        EntitiesSchemaMigrator.MigrateIfNeeded(path, log);

        var det = Load(path).Entities[0].Detectors[0];
        Assert.Equal("rmad", det.Name);
        Assert.Equal("720", det.Params["window"]);
        Assert.Equal(0, log.Count(LogLevel.Warning, "tuned hst params"));
    }

    /// <summary>
    /// G-14-1 was a confirmed data-loss defect: a config write that dropped the groups block.
    /// The migrator writes the whole root, so it is exactly the kind of code that reintroduces
    /// it. _patterns is doubly exposed — EntitiesConfig does not model it at all, so a
    /// load-then-write round trip loses it unless it is carried from the raw YAML.
    /// </summary>
    [Fact]
    public void Migration_PreservesGroupsAndPatternsByteForByte()
    {
        var yaml = """
            _patterns:
              include:
                - sensor.*_power
                - sensor.load_*
              exclude:
                - sensor.secret_*
            entities:
              - entity_id: sensor.load_5m
                friendly_name: ""
                detectors:
                  - name: hst
                    params: {}
            groups:
              - group_id: opony
                friendly_name: "Ciśnienie opon"
                members:
                  - sensor.tire_fl
                  - sensor.tire_fr
                mode: peer_divergence
                detector: peer_divergence
                params:
                  threshold: "3.5"
            """;
        var path = WritePath(yaml);

        Assert.True(EntitiesSchemaMigrator.MigrateIfNeeded(path, Silent));

        var root = RawRoot(path);
        var patterns = (Dictionary<object, object>)root["_patterns"];
        Assert.Equal(
            new[] { "sensor.*_power", "sensor.load_*" },
            ((List<object>)patterns["include"]).Select(x => x!.ToString()).ToArray());
        Assert.Equal(
            new[] { "sensor.secret_*" },
            ((List<object>)patterns["exclude"]).Select(x => x!.ToString()).ToArray());

        var group = Assert.Single(Load(path).Groups);
        Assert.Equal("opony", group.GroupId);
        Assert.Equal("Ciśnienie opon", group.FriendlyName);
        Assert.Equal(new[] { "sensor.tire_fl", "sensor.tire_fr" }, group.Members.ToArray());
        Assert.Equal("peer_divergence", group.Mode);
        Assert.Equal("3.5", group.Params["threshold"]);
    }

    // -----------------------------------------------------------------------
    // Idempotence
    // -----------------------------------------------------------------------

    /// <summary>
    /// Every write is a rename, ConfigFileWatcherService turns a rename into a Swap, and a Swap
    /// rebuilds every entity's runtime state. A migrator that rewrote on each boot would
    /// therefore reset every alert gate on each boot. The groups-save leg matters because that
    /// is the second writer of this file: if it dropped the stamp, the migrator would migrate
    /// again on the next start, forever.
    /// </summary>
    [Fact]
    public void SecondRun_IsNoOp_AndSurvivesGroupsSave()
    {
        var path = WritePath(F0Yaml());
        Assert.True(EntitiesSchemaMigrator.MigrateIfNeeded(path, Silent));

        var afterFirst = File.ReadAllText(path);
        var mtime = File.GetLastWriteTimeUtc(path);

        Assert.False(EntitiesSchemaMigrator.MigrateIfNeeded(path, Silent));
        Assert.Equal(afterFirst, File.ReadAllText(path));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(path));

        // Simulate POST /api/groups/save: it rewrites the whole root and MUST re-stamp.
        SimulateGroupsSave(path);

        Assert.False(EntitiesSchemaMigrator.MigrateIfNeeded(path, Silent));
        var det = Load(path).Entities[0].Detectors[0];
        Assert.Equal("rmad", det.Name);
        Assert.Equal("0.5", det.Params["high_threshold"]);
    }

    /// <summary>
    /// Byte-for-byte the root that POST /api/groups/save builds in Program.cs, including the
    /// schema_version stamp. If that stamp is ever dropped from the handler, this test still
    /// passes but the one above fails — which is the pairing that makes the omission visible.
    /// </summary>
    private static void SimulateGroupsSave(string path)
    {
        var current = Load(path);
        var rawRoot = RawRoot(path);
        var root = new Dictionary<string, object>
        {
            ["schema_version"] = EntitiesSchemaMigrator.TargetSchemaVersion,
            ["_patterns"] = rawRoot.TryGetValue("_patterns", out var p) && p is not null
                ? p
                : new Dictionary<string, object> { ["include"] = new List<string>(), ["exclude"] = new List<string>() },
            ["entities"] = current.Entities,
            ["groups"] = current.Groups,
        };
        var yaml = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build()
            .Serialize(root);
        new ConfigWriter().WriteAsync(path, yaml).GetAwaiter().GetResult();
    }

    // -----------------------------------------------------------------------
    // Refusal, backup, failure
    // -----------------------------------------------------------------------

    /// <summary>
    /// The sequence gate. A config migrated to rmad while the pipeline still resolves the
    /// detector by the literal "hst" would send every migrated entity through
    /// `new HstParams()` (250 / 0.7 / 0.3) — the pre-fix state, now with a config file that
    /// claims otherwise and a migration stamp that says it is done. Refusing to write is
    /// strictly better than that, so the refusal is an Error and the file is untouched.
    /// </summary>
    [Fact]
    public void RefusesToWriteWhenPipelineStillResolvesHst()
    {
        var path = WritePath(F0Yaml());
        var before = File.ReadAllText(path);
        var log = new RecordingLogger();

        var migrated = EntitiesSchemaMigrator.MigrateIfNeeded(
            path, log, unitOfMeasurement: null, supportsRmad: false);

        Assert.False(migrated);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.False(File.Exists(path + EntitiesSchemaMigrator.BackupSuffix));
        Assert.Equal(1, log.Count(LogLevel.Error, "does not resolve the rmad detector"));
    }

    /// <summary>
    /// The backup is the entire documented rollback (cp entities.yaml.pre-v2.bak entities.yaml),
    /// and the migration is forward-only. So it must exist before the first write, and a second
    /// run must never overwrite it with an already-migrated file — that would destroy the only
    /// copy of the operator's original config.
    /// </summary>
    [Fact]
    public void BackupIsWrittenBeforeFirstWrite_AndNeverOverwritten()
    {
        var path = WritePath(F0Yaml());
        var original = File.ReadAllText(path);
        var backup = path + EntitiesSchemaMigrator.BackupSuffix;

        EntitiesSchemaMigrator.MigrateIfNeeded(path, Silent);

        Assert.True(File.Exists(backup));
        Assert.Equal(original, File.ReadAllText(backup));

        // Force a second migrating run over an already-backed-up file.
        File.WriteAllText(path, F0Yaml());
        EntitiesSchemaMigrator.MigrateIfNeeded(path, Silent);

        Assert.Equal(original, File.ReadAllText(backup));
    }

    /// <summary>
    /// Fail loud (Rule 12). A migration that swallowed its exception would leave the add-on
    /// running the NEW dimensionless semantics against the OLD 0.7/0.3 numbers, which reads as
    /// "alarm above the 70th percentile" — measurably worse than not migrating. The original
    /// file must still be intact and un-stamped so the next start retries.
    /// </summary>
    [Fact]
    public void WriteFailure_Throws_AndLeavesOriginalFileIntact()
    {
        // An entity with no detectors: EntitiesConfigLoader.Validate rejects it, which happens
        // AFTER the backup and BEFORE the write.
        var yaml = """
            entities:
              - entity_id: sensor.broken
                friendly_name: ""
                detectors: []
            groups: []
            """;
        var path = WritePath(yaml);
        var before = File.ReadAllText(path);
        var log = new RecordingLogger();

        Assert.ThrowsAny<Exception>(() => EntitiesSchemaMigrator.MigrateIfNeeded(path, log));

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(1, log.Count(LogLevel.Error, "FAILED"));
    }

    // -----------------------------------------------------------------------
    // D-G: retracting the pre-migration retained discovery configs
    // -----------------------------------------------------------------------

    /// <summary>
    /// D-G/D1. The state topic argus/{slug}/flag/state was never detector-scoped, but the old
    /// unique_id was — so after the id change every pre-migration (slug, detector) pair still
    /// has a retained discovery config in the broker, and HA would keep a second, orphaned
    /// entity alive on the very same topic. RetractAsync cannot clean these up: it only handles
    /// entities that were REMOVED, and a migrated entity is still tracked.
    ///
    /// Every detector name from the pre-migration config is covered, not just hst — mad and stl
    /// are in InputValidator's allowlist and an operator may have set either by hand.
    /// </summary>
    [Fact]
    public async Task LegacyDiscoveryTopicsAreRetractedExactlyOnce()
    {
        var calls = new List<(string Topic, string Payload, bool Retain)>();
        Task<bool> Publish(string topic, string payload, bool retain, CancellationToken ct)
        {
            calls.Add((topic, payload, retain));
            return Task.FromResult(true);
        }

        var preMigration = new List<EntityConfig>
        {
            new() { EntityId = "sensor.load_5m", Detectors = [new DetectorConfig { Name = "hst" }] },
            new() { EntityId = "sensor.load_5m", Detectors = [new DetectorConfig { Name = "hst" }] }, // dupe
            new() { EntityId = "sensor.tuned_mad", Detectors = [new DetectorConfig { Name = "mad" }] },
            new() { EntityId = "sensor.tuned_stl", Detectors = [new DetectorConfig { Name = "stl" }] },
        };

        Assert.True(await DiscoveryPublisher.RetractLegacyDetectorScopedAsync(
            Publish, preMigration, CancellationToken.None));

        foreach (var (id, det) in new[]
                 {
                     ("sensor.load_5m", "hst"), ("sensor.tuned_mad", "mad"), ("sensor.tuned_stl", "stl"),
                 })
        {
            var anomalyTopic =
                $"homeassistant/binary_sensor/{UniqueId.LegacyAnomalyId(id, det)}/config";
            var scoreTopic = $"homeassistant/sensor/{UniqueId.LegacyScoreId(id, det)}/config";

            Assert.Equal(1, calls.Count(c => c.Topic == anomalyTopic));
            Assert.Equal(1, calls.Count(c => c.Topic == scoreTopic));
        }

        // Six topics for three distinct pairs — the repeated pair is retracted once, not twice.
        Assert.Equal(6, calls.Count);
        // An empty retained payload is what actually DELETES a retained message.
        Assert.All(calls, c => Assert.Equal(string.Empty, c.Payload));
        Assert.All(calls, c => Assert.True(c.Retain));
        // The new, detector-agnostic ids must never be retracted — those are the live entities.
        Assert.DoesNotContain(calls, c => c.Topic.Contains("argus_sensor_load_5m_anomaly"));
    }

    /// <summary>
    /// The two halves of D-G composed against a REAL migrated file, which is the composition
    /// Program.cs makes at startup: the migrator leaves the .pre-v2.bak behind, and the
    /// retraction is resolved from that file — never from "did MigrateIfNeeded return true".
    ///
    /// Boot 2 is the whole point. MigrateIfNeeded answers false there (schema_version is already
    /// 2), so a retraction gated on the migration would be gone for good on exactly the boot
    /// that has to make up for a broker that was down on boot 1. Asserting it against the
    /// migrator's own output, rather than a hand-written backup, is what ties the obligation to
    /// the file the migration actually produces.
    /// </summary>
    [Fact]
    public void RetractionIsOwedOnEveryBootAfterTheMigration_NotOnlyTheMigratingOne()
    {
        var path = WritePath(F0Yaml());

        Assert.True(EntitiesSchemaMigrator.MigrateIfNeeded(path, Silent));

        IReadOnlyList<EntityConfig> ReadBackup(string p) => Load(p).Entities;

        // Boot 1 — migrated just now.
        var boot1 = LegacyDiscoveryRetraction.Resolve(path, ReadBackup);
        Assert.True(boot1.IsPending);
        Assert.Equal(5, boot1.Entities.Count);
        // The pre-migration DETECTOR names, which the live file no longer has.
        Assert.All(boot1.Entities, e => Assert.Equal("hst", e.Detectors.Single().Name));

        // Boot 2 — nothing left to migrate, and the deletions are still owed.
        Assert.False(EntitiesSchemaMigrator.MigrateIfNeeded(path, Silent));
        Assert.True(LegacyDiscoveryRetraction.Resolve(path, ReadBackup).IsPending);
    }

    /// <summary>
    /// A retraction is only as good as the delivery it can prove. The production sink drops a
    /// publish it cannot deliver — it does not throw — so if this loop reported "done" on a
    /// dropped message, LegacyDiscoveryRetraction would write its durable marker and the stale
    /// retained configs would survive every later boot.
    ///
    /// The second half of the rule is that a drop must not abort the pass: deleting an
    /// already-deleted retained message is a no-op, so attempting the rest costs nothing, while
    /// stopping early leaves topics untried for no gain.
    /// </summary>
    [Fact]
    public async Task RetractionReportsFailure_WhenAnyDeletionWasDropped()
    {
        var attempted = new List<string>();
        Task<bool> Publish(string topic, string payload, bool retain, CancellationToken ct)
        {
            attempted.Add(topic);
            // The broker takes the first deletion and is gone for the rest.
            return Task.FromResult(attempted.Count == 1);
        }

        var preMigration = new List<EntityConfig>
        {
            new() { EntityId = "sensor.load_5m", Detectors = [new DetectorConfig { Name = "hst" }] },
            new() { EntityId = "sensor.tuned_mad", Detectors = [new DetectorConfig { Name = "mad" }] },
        };

        Assert.False(await DiscoveryPublisher.RetractLegacyDetectorScopedAsync(
            Publish, preMigration, CancellationToken.None));

        // Both entities, both topics each — the drop did not cut the pass short.
        Assert.Equal(4, attempted.Count);
    }

    // -----------------------------------------------------------------------
    // D-I
    // -----------------------------------------------------------------------

    /// <summary>
    /// D-I. Measured, not assumed: on a memory_use_percent-shaped series (5653 samples, one
    /// decimal) MAD lands at 0.1 and sigma at 0.148, so a benign 1.1 pp move scores z = 7.4 —
    /// 4 episodes / 7.02% on-time. Floors of 0.05 and 0.1 change nothing; 0.3 is the first that
    /// gives zero. A unit-based heuristic is defensible for THIS key alone, because scale_floor
    /// is itself in the sensor's units (unlike the window, which is in samples).
    /// </summary>
    [Fact]
    public void PercentUnitEntity_GetsScaleFloorFromUnit()
    {
        var yaml = """
            entities:
              - entity_id: sensor.memory_use_percent
                friendly_name: ""
                detectors:
                  - name: hst
                    params: {}
              - entity_id: sensor.lodowkababcia_power
                friendly_name: ""
                detectors:
                  - name: hst
                    params: {}
            groups: []
            """;
        var path = WritePath(yaml);

        EntitiesSchemaMigrator.MigrateIfNeeded(path, Silent,
            id => id.EndsWith("_percent", StringComparison.Ordinal) ? "%" : "W");

        var config = Load(path);
        Assert.Equal(
            EntitiesSchemaMigrator.PercentScaleFloor,
            config.Entities.Single(e => e.EntityId == "sensor.memory_use_percent").Detectors[0].Params["scale_floor"]);
        // A watt sensor keeps 0.0 — a floor of 0.3 W would be meaningless on a 0..984 W series.
        Assert.Equal(
            "0.0",
            config.Entities.Single(e => e.EntityId == "sensor.lodowkababcia_power").Detectors[0].Params["scale_floor"]);
    }

    /// <summary>
    /// The migrated block must survive the same server-side validation a hand-typed one does,
    /// or the operator's first Save after the upgrade fails on a file the add-on wrote itself.
    /// </summary>
    [Fact]
    public void MigratedParams_PassServerValidation()
    {
        var path = WritePath(F0Yaml());
        EntitiesSchemaMigrator.MigrateIfNeeded(path, Silent);

        var config = Load(path);
        var parsed = config.Entities
            .Select((e, i) => (i, e.Detectors))
            .ToDictionary(x => x.i, x => x.Detectors);

        var errors = InputValidator.Validate(config.Entities.Select(e => e.EntityId), parsed);

        Assert.Empty(errors);
    }

    [Fact]
    public void MissingFile_IsNoOp()
    {
        Assert.False(EntitiesSchemaMigrator.MigrateIfNeeded(
            Path.Combine(_dir, "absent.yaml"), Silent));
    }
}
