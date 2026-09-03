using Microsoft.Extensions.Logging;
using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Logging;
using Argus.Orchestrator.Web;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Argus.Orchestrator.Config;

/// <summary>
/// One-shot, idempotent, fail-loud migration of /data/entities.yaml to schema_version 2 (D-L).
///
/// What it does: rewrites entities that still carry the PRISTINE legacy hst block onto rmad
/// with the D-B default table, and disables frozen detection arithmetically on every hst/rmad
/// entity it sees (D-H). What it deliberately does NOT do: touch an entity whose hst params
/// were tuned by hand. There is no meaning-preserving mapping from an absolute HST rarity
/// threshold to a robust-z threshold — 0.7 on an HST mass means "rarer than 70% of the tree
/// population", and the same 0.7 on rmad means "robust z above 11.7". Rewriting a tuned entity
/// would therefore be a guess presented as a migration, so tuned entities are left alone and
/// named in a WARNING instead.
///
/// Fail-loud (Rule 12): every failure path throws after logging. A "best effort" migration that
/// swallowed its exception would start the new dimensionless semantics on the old 0.7/0.3
/// numbers, which reads as "alarm above the 70th percentile" — measurably worse than doing
/// nothing at all.
///
/// Idempotence matters for more than tidiness: every write is a rename, which
/// ConfigFileWatcherService turns into a Swap, which resets every entity's alert gate. A
/// migrator that rewrote the file on each boot would reset the gates on each boot.
/// </summary>
public static class EntitiesSchemaMigrator
{
    /// <summary>Schema version this migrator produces. Stamped by every writer of entities.yaml.</summary>
    public const int TargetSchemaVersion = 2;

    /// <summary>Suffix of the one-time backup written before the first migrating write.</summary>
    public const string BackupSuffix = ".pre-v2.bak";

    /// <summary>
    /// The EXACT parameter block the UI wrote for a never-tuned hst entity. Anything else —
    /// one changed digit, one extra key — means an operator touched it, and it is left alone.
    /// </summary>
    private static readonly Dictionary<string, string> LegacyHstFingerprint = new(StringComparer.Ordinal)
    {
        ["window"] = "250",
        ["n_trees"] = "25",
        ["high_threshold"] = "0.7",
        ["low_threshold"] = "0.3",
        ["min_consecutive"] = "3",
        ["frozen_window"] = "10",
        ["frozen_variance_threshold"] = "0.001",
    };

    /// <summary>
    /// D-I: scale_floor written for a migrated entity whose HA unit_of_measurement is "%".
    /// Measured on a memory_use_percent-shaped series (5653 samples, 1 decimal): MAD lands at
    /// 0.1, sigma at 0.148, and a benign 1.1 pp move becomes z = 7.4 — 4 episodes / 7.02%
    /// on-time. 0.05 and 0.1 change nothing; 0.3 is the first value that gives 0 episodes.
    /// </summary>
    public const string PercentScaleFloor = "0.3";

    /// <summary>
    /// Migrates <paramref name="path"/> to schema_version 2 if it is not already there.
    /// Returns true when the file was rewritten, false when nothing needed doing.
    /// </summary>
    /// <param name="path">Path to entities.yaml.</param>
    /// <param name="logger">Logger; every decision this makes is logged by entity id.</param>
    /// <param name="unitOfMeasurement">
    /// Resolves an entity's HA unit_of_measurement for D-I, or null when no HA snapshot is
    /// available yet (the production call site runs before the HA registry exists). A null
    /// resolver is announced in the log rather than assumed harmless.
    /// </param>
    public static bool MigrateIfNeeded(
        string path,
        ILogger logger,
        Func<string, string?>? unitOfMeasurement = null)
        => MigrateIfNeeded(path, logger, unitOfMeasurement, ScoreStreamPipeline.SupportsRmad);

    /// <summary>
    /// Testable overload: <paramref name="supportsRmad"/> is the sequence gate, normally read
    /// from <see cref="ScoreStreamPipeline.SupportsRmad"/>.
    /// </summary>
    internal static bool MigrateIfNeeded(
        string path,
        ILogger logger,
        Func<string, string?>? unitOfMeasurement,
        bool supportsRmad)
    {
        try
        {
            if (!File.Exists(path))
            {
                logger.LogDebug(
                    "No entities.yaml at {Path} — nothing to migrate to schema_version {Version}",
                    path, TargetSchemaVersion);
                return false;
            }

            var rawYaml = File.ReadAllText(path);
            var rawRoot = ReadRoot(rawYaml);

            if (ReadSchemaVersion(rawRoot) >= TargetSchemaVersion)
            {
                logger.LogDebug(
                    "entities.yaml at {Path} is already schema_version {Version} — no migration",
                    path, TargetSchemaVersion);
                return false;
            }

            // (1a) Sequence gate. Writing rmad into the config while the pipeline still resolves
            // detectors by the literal "hst" would hand every migrated entity to
            // `new HstParams()` (250/0.7/0.3) — the exact pre-fix state, now with a config file
            // that claims otherwise. Refusing to write is the safe half of that trade.
            if (!supportsRmad)
            {
                logger.LogError(LogEvents.EntityConfigMigrationRefused,
                    "Refusing to migrate {Path} to schema_version {Version}: the scoring pipeline "
                    + "does not resolve the rmad detector yet, so every migrated entity would fall "
                    + "back to legacy hst params (250/0.7/0.3). Config left untouched.",
                    path, TargetSchemaVersion);
                return false;
            }

            // (2) Backup BEFORE the first write, and never overwrite an existing one — a second
            // migration attempt after a partially-applied first must not clobber the only copy
            // of the operator's original file. This is also the documented rollback (§7 #10).
            var backupPath = path + BackupSuffix;
            if (!File.Exists(backupPath))
            {
                File.Copy(path, backupPath, overwrite: false);
                logger.LogInformation(LogEvents.EntityConfigMigrated,
                    "Backed up pre-migration config to {BackupPath}", backupPath);
            }
            else
            {
                logger.LogWarning(LogEvents.EntityConfigMigrated,
                    "Backup {BackupPath} already exists — keeping it; it is the ORIGINAL, "
                    + "pre-migration config and must not be overwritten", backupPath);
            }

            // (3) Typed load — also the validation gate: a config that cannot be loaded must not
            // be rewritten into a shape that hides why.
            var config = EntitiesConfigLoader.Load(path, logger);

            if (unitOfMeasurement is null)
            {
                // Rule 12: D-I is a measured rule, not a nicety. Without a unit resolver every
                // percent-unit sensor migrates on scale_floor 0.0, which was measured at
                // 4 episodes / 7.02% on-time for a memory_use_percent-shaped series.
                logger.LogWarning(LogEvents.EntityConfigMigrated,
                    "No HA unit snapshot available during migration — scale_floor stays 0.0 for "
                    + "every entity, including percent-unit sensors where {Floor} was measured to "
                    + "be the difference between 4 false episodes/24h and none (D-I). Set "
                    + "scale_floor by hand on percent sensors after the upgrade.",
                    PercentScaleFloor);
            }

            foreach (var entity in config.Entities)
                MigrateEntity(entity, logger, unitOfMeasurement);

            // (6) Root key order is fixed: schema_version FIRST so the stamp is visible at a
            // glance, then the same _patterns/entities/groups order both existing writers use.
            // _patterns and groups are carried over from the RAW root, not from the typed model:
            // EntitiesConfig does not model _patterns at all, and losing groups on a write is a
            // confirmed past defect (G-14-1).
            var root = new Dictionary<string, object>
            {
                ["schema_version"] = TargetSchemaVersion,
                ["_patterns"] = CarryOver(rawRoot, "_patterns", EmptyPatterns()),
                ["entities"] = config.Entities,
                ["groups"] = CarryOver(rawRoot, "groups", config.Groups),
            };

            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();

            // (7) Same atomic temp-then-rename writer every other config write uses.
            new ConfigWriter().WriteAsync(path, serializer.Serialize(root)).GetAwaiter().GetResult();

            logger.LogInformation(LogEvents.EntityConfigMigrated,
                "Migrated {Path} to schema_version {Version} ({EntityCount} entities)",
                path, TargetSchemaVersion, config.Entities.Count);

            return true;
        }
        catch (Exception ex)
        {
            // (8) Never degrade to a no-op here: a half-applied or skipped schema_version
            // migration is exactly the state where the new thresholds meet the old semantics.
            logger.LogError(LogEvents.EntityConfigMigrationFailed, ex,
                "Migration of {Path} to schema_version {Version} FAILED — the add-on must not "
                + "start on a config in an unknown schema state", path, TargetSchemaVersion);
            throw;
        }
    }

    /// <summary>
    /// Applies the per-entity rules: hst pristine -> rmad, hst tuned -> left alone with a
    /// WARNING, anything else -> silent skip. Frozen is disabled on every hst/rmad entity.
    /// </summary>
    private static void MigrateEntity(
        EntityConfig entity, ILogger logger, Func<string, string?>? unitOfMeasurement)
    {
        // (5) Any other detector — INCLUDING rmad, and including a multi-detector entity — is a
        // silent skip. It must never produce the "tuned hst" warning: an operator who already
        // chose rmad, mad or stl did nothing wrong and has nothing to act on.
        if (entity.Detectors.Count != 1)
        {
            logger.LogDebug("Skipping {EntityId}: {Count} detectors configured",
                entity.EntityId, entity.Detectors.Count);
            return;
        }

        var detector = entity.Detectors[0];

        if (string.Equals(detector.Name, "rmad", StringComparison.OrdinalIgnoreCase))
        {
            DisableFrozen(entity.EntityId, detector.Params, logger);
            logger.LogDebug("Skipping {EntityId}: already on rmad", entity.EntityId);
            return;
        }

        if (!string.Equals(detector.Name, "hst", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Skipping {EntityId}: detector is {Detector}",
                entity.EntityId, detector.Name);
            return;
        }

        if (!IsPristineLegacyHst(detector.Params))
        {
            // The entity stays on hst — the rollback path (D-F), not a parity path. Frozen is
            // still disabled here (D-H): a fridge at 88% zeros latches IsFrozen for the whole
            // compressor rest with frozen 10/0.001, and frozen bypasses warm-up, suppression and
            // hysteresis on the way to forcing the flag ON.
            DisableFrozen(entity.EntityId, detector.Params, logger);
            logger.LogWarning(LogEvents.EntityConfigMigrationTuned,
                "Entity {EntityId} has tuned hst params — left on hst. There is no "
                + "meaning-preserving mapping from an HST rarity threshold to a robust-z "
                + "threshold; switch it to rmad by hand if you want the new detector.",
                entity.EntityId);
            return;
        }

        // (4) Pristine legacy block -> the D-B default rmad table.
        var migrated = new Dictionary<string, string>(DetectorDefaults.Get("rmad")!, StringComparer.Ordinal);

        // min_consecutive and frozen_window carry over VERBATIM. They mean the same thing under
        // both detectors (consecutive agreeing verdicts; readings in the frozen window), unlike
        // the thresholds, whose units changed entirely.
        if (detector.Params.TryGetValue("min_consecutive", out var minConsecutive))
            migrated["min_consecutive"] = minConsecutive;
        if (detector.Params.TryGetValue("frozen_window", out var frozenWindow))
            migrated["frozen_window"] = frozenWindow;

        var unit = unitOfMeasurement?.Invoke(entity.EntityId);
        if (string.Equals(unit?.Trim(), "%", StringComparison.Ordinal))
        {
            // D-I. A unit-based heuristic is defensible for THIS key only, because scale_floor
            // is itself expressed in the sensor's units — unlike the window, which is in samples
            // and whose wall-clock meaning cannot be guessed from a unit.
            migrated["scale_floor"] = PercentScaleFloor;
            logger.LogInformation(LogEvents.EntityConfigMigrated,
                "Set scale_floor={Floor} for {EntityId} (unit is %) — D-I",
                PercentScaleFloor, entity.EntityId);
        }

        entity.Detectors[0] = new DetectorConfig { Name = "rmad", Params = migrated };
        DisableFrozen(entity.EntityId, migrated, logger);

        logger.LogInformation(LogEvents.EntityConfigMigrated,
            "Migrated {EntityId}: hst -> rmad (schema_version {Version})",
            entity.EntityId, TargetSchemaVersion);
    }

    /// <summary>
    /// D-H: disables frozen detection through the VARIANCE threshold only.
    ///
    /// frozen_window is carried over verbatim and "0" is forbidden: with a window of 0,
    /// FrozenSensorDetector.AddReading dequeues an empty queue on the first reading — and
    /// ScoreStreamPipeline calls it for every reading — while InputValidator separately rejects
    /// anything below 1, so an entity written that way could never be saved from the UI again.
    /// </summary>
    private static void DisableFrozen(string entityId, Dictionary<string, string> p, ILogger logger)
    {
        if (p.TryGetValue("frozen_variance_threshold", out var current) && current == "0.0")
            return;

        p["frozen_variance_threshold"] = "0.0";
        if (!p.ContainsKey("frozen_window"))
            p["frozen_window"] = "10";

        logger.LogInformation(LogEvents.EntityConfigMigrated,
            "Frozen disabled for {EntityId} (frozen_variance_threshold=0.0, window kept)", entityId);
    }

    /// <summary>
    /// True when the params are the never-touched legacy hst block: empty (the UI's "use all
    /// defaults" form) or the exact seven-key fingerprint the UI wrote.
    /// </summary>
    private static bool IsPristineLegacyHst(Dictionary<string, string> p)
    {
        if (p.Count == 0)
            return true;
        if (p.Count != LegacyHstFingerprint.Count)
            return false;

        foreach (var (key, value) in LegacyHstFingerprint)
        {
            if (!p.TryGetValue(key, out var actual) || actual != value)
                return false;
        }
        return true;
    }

    private static Dictionary<object, object>? ReadRoot(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<Dictionary<object, object>>(yaml);
    }

    private static int ReadSchemaVersion(Dictionary<object, object>? root)
    {
        if (root is null || !root.TryGetValue("schema_version", out var raw) || raw is null)
            return 1;
        return int.TryParse(raw.ToString(), out var version) ? version : 1;
    }

    private static object CarryOver(Dictionary<object, object>? root, string key, object fallback)
        => root is not null && root.TryGetValue(key, out var value) && value is not null
            ? value
            : fallback;

    private static Dictionary<string, object> EmptyPatterns() => new()
    {
        ["include"] = new List<string>(),
        ["exclude"] = new List<string>(),
    };
}
