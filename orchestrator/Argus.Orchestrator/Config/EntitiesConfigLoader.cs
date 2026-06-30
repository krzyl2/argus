using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Argus.Orchestrator.Logging;

namespace Argus.Orchestrator.Config;

/// <summary>
/// Loads and validates entities.yaml.
/// Covariates and Groups keys are parsed but ignored with a structured warning (CONF-01, D-09).
/// </summary>
public class EntitiesConfigLoader
{
    public static EntitiesConfig Load(string path, ILogger logger)
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
        if (config.Entities?.Count > 0)
            WarnIgnoredKeys(config, logger);

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

    private static void WarnIgnoredKeys(EntitiesConfig config, ILogger logger)
    {
        if (config.Entities is null) return;
        foreach (var entity in config.Entities)
        {
            if (entity.Covariates != null || entity.Groups != null)
            {
                logger.Log(LogLevel.Warning, LogEvents.CovariatesIgnored,
                    "covariates/groups ignored in phase 1 for {EntityId} — these keys are parsed but not used until Phase 2",
                    entity.EntityId);
            }
        }
    }
}
