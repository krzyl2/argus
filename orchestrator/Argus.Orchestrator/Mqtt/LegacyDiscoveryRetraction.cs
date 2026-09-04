using Argus.Orchestrator.Config;
using Argus.Orchestrator.Logging;

namespace Argus.Orchestrator.Mqtt;

/// <summary>
/// Carries the PRE-migration entity list to MqttPublisherWorker, the only place that can talk to
/// the broker, so the retained discovery configs those entities published under the OLD
/// detector-scoped unique_id can be deleted (D-G).
///
/// Why this is resolved from DISK rather than from "did we migrate this boot":
/// the retraction is a network operation against a broker that may be down, and the add-on may
/// die between rewriting entities.yaml and publishing. Binding it to the migration meant it had
/// exactly ONE chance: the next boot sees schema_version 2, migrates nothing, retracts nothing,
/// and the retained argus_{slug}_{det}_* configs stay in the broker forever — a second, orphaned
/// HA entity per sensor, fed from the same detector-agnostic argus/{slug}/flag/state topic. That
/// is precisely the outcome D-G exists to prevent.
///
/// So the state lives where it can survive a crash: the retraction is pending whenever the
/// migration backup (.pre-v2.bak) exists and the completion marker does not, and the marker is
/// written only after the broker has accepted every deletion. The backup is also the only
/// trustworthy source of the pre-migration DETECTOR NAMES — the live entities.yaml has already
/// been rewritten, and the old ids cannot be reconstructed from it.
/// </summary>
/// <param name="Entities">
/// Entities exactly as they were configured before the migration. Empty when nothing is pending.
/// </param>
/// <param name="MarkerPath">
/// Where to record completion, or null when nothing is pending.
/// </param>
public sealed record LegacyDiscoveryRetraction(
    IReadOnlyList<EntityConfig> Entities,
    string? MarkerPath = null)
{
    /// <summary>Suffix of the durable "retraction already done" marker, written next to the backup.</summary>
    public const string MarkerSuffix = ".v2-retracted";

    public static LegacyDiscoveryRetraction None { get; } = new(Array.Empty<EntityConfig>());

    public bool IsPending => Entities.Count > 0;

    /// <summary>
    /// Decides, from the files on disk alone, whether the legacy retraction still owes the broker
    /// a set of deletions. Independent of whether a migration ran in THIS process.
    /// </summary>
    /// <param name="entitiesPath">Path to entities.yaml; backup and marker sit beside it.</param>
    /// <param name="readEntities">
    /// Reads an entities.yaml-shaped file. Must not throw: a backup too broken to parse means
    /// "nothing to retract", not "fail to start".
    /// </param>
    public static LegacyDiscoveryRetraction Resolve(
        string entitiesPath,
        Func<string, IReadOnlyList<EntityConfig>> readEntities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entitiesPath);
        ArgumentNullException.ThrowIfNull(readEntities);

        var markerPath = entitiesPath + MarkerSuffix;
        var backupPath = entitiesPath + EntitiesSchemaMigrator.BackupSuffix;

        // No backup means no migration has ever rewritten this file, so no entity ever published
        // under a detector-scoped id that is now stale.
        if (File.Exists(markerPath) || !File.Exists(backupPath))
            return None;

        var entities = readEntities(backupPath);
        return entities.Count == 0 ? None : new LegacyDiscoveryRetraction(entities, markerPath);
    }

    /// <summary>
    /// Runs the retraction and records completion — in that order, and only in that order.
    ///
    /// If <paramref name="retract"/> throws (broker down, host shutting down), the marker is NOT
    /// written and the whole thing is attempted again on the next boot. Republishing empty
    /// retained payloads for topics that are already gone is a no-op at the broker, so retrying
    /// is always cheaper than the alternative of leaving an orphaned entity behind.
    /// </summary>
    /// <returns>True when a retraction was performed this boot.</returns>
    public async Task<bool> RunAsync(
        Func<IReadOnlyList<EntityConfig>, CancellationToken, Task> retract,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(retract);
        ArgumentNullException.ThrowIfNull(logger);

        if (!IsPending)
            return false;

        await retract(Entities, ct);

        logger.LogInformation(LogEvents.MqttDiscoveryPublished,
            "Retracted legacy detector-scoped discovery for {Count} pre-migration entities "
            + "(entity_id changes once; see argus/CHANGELOG.md)",
            Entities.Count);

        MarkCompleted(logger);
        return true;
    }

    /// <summary>
    /// Writes the durable marker. A failure here is a WARNING, not a throw: the retraction itself
    /// already succeeded, and the only consequence is that it runs again — harmlessly — next boot.
    /// </summary>
    private void MarkCompleted(ILogger logger)
    {
        if (MarkerPath is null)
            return;

        try
        {
            File.WriteAllText(MarkerPath,
                $"Legacy detector-scoped MQTT discovery configs were retracted at "
                + $"{DateTimeOffset.UtcNow:O} for {Entities.Count} pre-migration entities (D-G).{Environment.NewLine}"
                + $"Delete this file to make the add-on publish those deletions again.{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(LogEvents.MqttDiscoveryPublished, ex,
                "Could not write the legacy-retraction marker {MarkerPath} — the retraction "
                + "succeeded, but it will be repeated on the next start (harmless: deleting an "
                + "already-deleted retained message is a no-op)", MarkerPath);
        }
    }
}
