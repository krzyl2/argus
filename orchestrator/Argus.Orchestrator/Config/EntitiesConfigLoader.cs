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

        Validate(config, path, logger);
        ValidateGroups(config, path, logger, registry);

        logger.Log(LogLevel.Information, LogEvents.EntityConfigLoaded,
            "Loaded {EntityCount} entities from {Path}", config.Entities?.Count ?? 0, path);

        return config;
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

            if (group.Members is null || group.Members.Count < 3)
            {
                logger.LogWarning(LogEvents.GroupRejected,
                    "Group '{GroupId}' has {MemberCount} member(s), below the minimum of 3 — skipped",
                    group.GroupId, group.Members?.Count ?? 0);
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

                if (registry is null || resolvedUnitValues.Count < 2)
                {
                    logger.Log(LogLevel.Information, LogEvents.GroupConfigLoaded,
                        "Group '{GroupId}' unit check skipped — sensor registry not yet populated with units for its members",
                        group.GroupId);
                }
                else if (resolvedUnitValues.Count > 1)
                {
                    logger.LogWarning(LogEvents.GroupRejected,
                        "Group '{GroupId}' members have differing units ({Units}) — skipped",
                        group.GroupId, string.Join(", ", resolvedUnitValues));
                    continue;
                }
            }

            surviving.Add(group);
        }

        config.Groups = surviving;

        logger.Log(LogLevel.Information, LogEvents.GroupConfigLoaded,
            "Loaded {GroupCount} group(s) from {Path}", config.Groups.Count, path);
    }
}
