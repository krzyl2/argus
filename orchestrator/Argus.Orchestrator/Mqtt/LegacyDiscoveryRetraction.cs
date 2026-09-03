using Argus.Orchestrator.Config;

namespace Argus.Orchestrator.Mqtt;

/// <summary>
/// Carries the PRE-migration entity list from startup (where the schema-2 migration runs, ahead
/// of DI) to MqttPublisherWorker, which is the only place that can talk to the broker (D-G).
///
/// It is populated on exactly one boot: the first start after the migration rewrote
/// entities.yaml. On every other boot the list is empty and nothing is retracted — retracting
/// again would be harmless but would also mean the add-on publishes deletions for topics that
/// no longer exist on every single start, which is noise an operator would learn to ignore.
/// </summary>
/// <param name="Entities">
/// Entities exactly as they were configured before the migration, so their old detector-scoped
/// discovery ids can be reconstructed. Empty when no migration happened this boot.
/// </param>
public sealed record LegacyDiscoveryRetraction(IReadOnlyList<EntityConfig> Entities)
{
    public static LegacyDiscoveryRetraction None { get; } = new(Array.Empty<EntityConfig>());

    public bool IsPending => Entities.Count > 0;
}
