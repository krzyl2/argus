using System.Linq;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Argus.Orchestrator.Logging;
using Argus.Orchestrator.Ha;

namespace Argus.Orchestrator.Config;

/// <summary>
/// Loads and validates entities.yaml.
/// </summary>
public class EntitiesConfigLoader
{
    private static readonly string[] ValidModes = { "peer_divergence", "joint" };

    public static EntitiesConfig Load(string path, ILogger logger, IHaSensorRegistry? registry = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"entities.yaml not found at '{path}'");

        var yaml = File.ReadAllText(path);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var config = deserializer.Deserialize<EntitiesConfig>(yaml)
            ?? new EntitiesConfig();

        NormalizeParams(config);
        Validate(config, path, logger);
        ValidateGroups(config, path, logger, registry);

        logger.Log(LogLevel.Information, LogEvents.EntityConfigLoaded,
            "Loaded {EntityCount} entities from {Path}", config.Entities?.Count ?? 0, path);

        return config;
    }

    /// <summary>
    /// Replaces every null params dictionary with an empty one, ONCE, at the only door every
    /// consumer comes through.
    ///
    /// WHY it cannot be left to the readers: `params:` followed by nothing is a valid YAML null,
    /// and YamlDotNet assigns that null straight over the C# property initializer — so a
    /// hand-edited config yields DetectorConfig.Params == null even though EntitiesConfig.cs
    /// says `= new()`. Every reader then dereferences null. The first one on the boot path is
    /// EntitiesSchemaMigrator, which logs and RETHROWS by design, into a Program.cs call that
    /// does not catch: one operator-typed `params:` and the add-on does not start at all.
    /// Guarding the readers one at a time would fix that call site and leave the next one.
    ///
    /// Normalizing on load also stops the shape being REPRODUCED: the migrator serializes the
    /// typed model back to disk, and a null here comes out as another bare `params:`.
    /// </summary>
    private static void NormalizeParams(EntitiesConfig config)
    {
        foreach (var entity in config.Entities ?? new List<EntityConfig>())
        {
            foreach (var detector in entity?.Detectors ?? new List<DetectorConfig>())
            {
                if (detector is not null)
                    detector.Params ??= new Dictionary<string, string>();
            }
        }

        foreach (var group in config.Groups ?? new List<GroupConfig>())
        {
            if (group is not null)
                group.Params ??= new Dictionary<string, string>();
        }
    }

    private static void Validate(EntitiesConfig config, string path, ILogger logger)
    {
        if (config.Entities == null || config.Entities.Count == 0)
        {
            logger.LogWarning(LogEvents.EmptyEntitiesWarning,
                "entities.yaml at '{Path}' contains no entities — orchestrator running with empty pipeline; configure via UI.",
                path);
            return;
        }

        foreach (var entity in config.Entities)
        {
            if (entity is null)
                throw new InvalidOperationException(
                    "entities.yaml contains a null entity entry (check for bare '-' list items)");

            if (string.IsNullOrWhiteSpace(entity.EntityId))
                throw new InvalidOperationException(
                    "An entity in entities.yaml is missing 'entity_id'");

            if (entity.Detectors == null || entity.Detectors.Count == 0)
                throw new InvalidOperationException(
                    $"Entity '{entity.EntityId}' has no detectors configured");

            // A bare '-' under `detectors:` is a null LIST ITEM — valid YAML, and YamlDotNet puts
            // the null straight into List<DetectorConfig>, so the count check above passes with a
            // hole in the list. Every reader then dereferences it, and the first one on the boot
            // path is EntitiesSchemaMigrator: `detector.Name` throws, MigrateIfNeeded rethrows by
            // design, and Program.cs does not catch — the add-on does not start, on the same
            // class of typo as the `params:` null NormalizeParams answers above.
            //
            // Rejected, not dropped: this is the entity side of the file, where a null entry
            // already throws (above), and there is no detector to invent for an empty item.
            // Silently dropping it would leave an entity that passed "has detectors" running
            // with none — a config the operator cannot see is wrong.
            if (entity.Detectors.Any(d => d is null))
                throw new InvalidOperationException(
                    $"Entity '{entity.EntityId}' has a null detector entry in entities.yaml "
                    + "(check for bare '-' list items)");
        }
    }

    /// <summary>
    /// Validates config.Groups in place, pruning invalid entries and logging a warning for each.
    /// Degrade-not-crash: unlike entity Validate(), this NEVER throws — a bad group is skipped so
    /// valid groups (and all entities) still load. registry may be null (e.g. cold boot, before
    /// IHaSensorRegistry is populated) — the peer-mode unit check degrades to skip+keep in that case.
    /// </summary>
    private static void ValidateGroups(EntitiesConfig config, string path, ILogger logger, IHaSensorRegistry? registry)
    {
        if (config.Groups is null || config.Groups.Count == 0)
        {
            config.Groups = new List<GroupConfig>();
            return;
        }

        var unitsByEntityId = registry?.GetAll()
            .GroupBy(e => e.EntityId)
            .ToDictionary(g => g.Key, g => g.First().UnitOfMeasurement);

        var surviving = new List<GroupConfig>();

        foreach (var group in config.Groups.ToArray())
        {
            if (group is null)
            {
                logger.LogWarning(LogEvents.GroupRejected,
                    "entities.yaml at '{Path}' contains a null group entry (check for bare '-' list items) — skipped",
                    path);
                continue;
            }

            if (string.IsNullOrWhiteSpace(group.GroupId))
            {
                logger.LogWarning(LogEvents.GroupRejected,
                    "Group in entities.yaml at '{Path}' is missing 'group_id' — skipped", path);
                continue;
            }

            if (string.IsNullOrWhiteSpace(group.Detector))
            {
                logger.LogWarning(LogEvents.GroupRejected,
                    "Group '{GroupId}' has no detector configured — skipped", group.GroupId);
                continue;
            }

            if (group.Members is null || group.Members.Count < 2)
            {
                logger.LogWarning(LogEvents.GroupRejected,
                    "Group '{GroupId}' has {MemberCount} member(s), below the minimum of 2 — skipped",
                    group.GroupId, group.Members?.Count ?? 0);
                continue;
            }

            // The same bare '-', one level down. A null member survives the count check above
            // and the duplicate check below, and then peer-divergence unit resolution indexes
            // ResolvedUnits by it — ArgumentNullException('key'), thrown out of a method whose
            // entire contract is that it never throws (D-14, degrade-not-crash). Groups degrade,
            // so the group is skipped here rather than taking every entity down with it.
            if (group.Members.Any(string.IsNullOrWhiteSpace))
            {
                logger.LogWarning(LogEvents.GroupRejected,
                    "Group '{GroupId}' has an empty member entry (check for bare '-' list items) — skipped",
                    group.GroupId);
                continue;
            }

            // WR-01: reject duplicate member ids — BuildGroupMatrix.ToDictionary would otherwise
            // throw ArgumentException on the first duplicate key, crashing the group's batch cycle
            // (caught upstream, but with a misleading "duplicate key" error instead of a clear
            // config diagnostic). Degrade-not-crash: skip the group here with a clear message.
            var distinctMemberCount = group.Members.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (distinctMemberCount != group.Members.Count)
            {
                logger.LogWarning(LogEvents.GroupRejected,
                    "Group '{GroupId}' has duplicate member ids — skipped", group.GroupId);
                continue;
            }

            var isPeerDivergence = string.Equals(group.Mode, "peer_divergence", StringComparison.OrdinalIgnoreCase);
            var isJoint = string.Equals(group.Mode, "joint", StringComparison.OrdinalIgnoreCase);

            if (!isPeerDivergence && !isJoint)
            {
                logger.LogWarning(LogEvents.GroupRejected,
                    "Group '{GroupId}' has unknown mode '{Mode}' (expected one of: {ValidModes}) — skipped",
                    group.GroupId, group.Mode, string.Join(", ", ValidModes));
                continue;
            }

            if (isPeerDivergence)
            {
                group.ResolvedUnits = new Dictionary<string, string?>();
                if (unitsByEntityId is not null)
                {
                    foreach (var member in group.Members)
                        group.ResolvedUnits[member] = unitsByEntityId.TryGetValue(member, out var unit) ? unit : null;
                }

                var resolvedUnitValues = group.ResolvedUnits.Values
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Distinct()
                    .ToList();

                if (registry is null)
                {
                    // WR-03: cold boot — registry not yet populated. Warn-only, do not reject.
                    logger.Log(LogLevel.Information, LogEvents.GroupConfigLoaded,
                        "Group '{GroupId}' unit check skipped — sensor registry not yet populated",
                        group.GroupId);
                }
                else if (resolvedUnitValues.Count > 1)
                {
                    logger.LogWarning(LogEvents.GroupRejected,
                        "Group '{GroupId}' members have differing units ({Units}) — skipped",
                        group.GroupId, string.Join(", ", resolvedUnitValues));
                    continue;
                }
                // else: registry populated and units consistent (0 or 1 distinct value) — no log needed.
            }

            surviving.Add(group);
        }

        config.Groups = surviving;

        logger.Log(LogLevel.Information, LogEvents.GroupConfigLoaded,
            "Loaded {GroupCount} group(s) from {Path}", config.Groups.Count, path);
    }
}
