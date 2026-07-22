using System.Collections.Concurrent;

namespace Argus.Orchestrator.Detection;

/// <summary>
/// Last known warm-up status for a single-sensor entity, cached in memory (QUICK-warmup-status).
/// </summary>
public sealed record EntityStatusEntry(
    string EntityId,
    bool WarmedUp,
    int ReadingCount,
    int WarmUpWindow);

/// <summary>
/// In-memory last-status cache backing GET /api/sensors's per-entity warm-up projection
/// (QUICK-warmup-status). Single writer (ScoreStreamPipeline's write loop, per reading),
/// many readers (Kestrel) — mirrors the Batch/GroupStatusCache precedent, generalized to
/// a ConcurrentDictionary since the key set (entity_id) is open, not fixed.
/// </summary>
public interface IEntityStatusCache
{
    /// <summary>Returns the last cached entry for <paramref name="entityId"/>, or null if never scored.</summary>
    EntityStatusEntry? Get(string entityId);

    /// <summary>Stores/replaces the cached entry for the entry's EntityId.</summary>
    void Set(EntityStatusEntry entry);
}

/// <inheritdoc cref="IEntityStatusCache"/>
public sealed class EntityStatusCache : IEntityStatusCache
{
    private readonly ConcurrentDictionary<string, EntityStatusEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public EntityStatusEntry? Get(string entityId) =>
        _entries.TryGetValue(entityId, out var e) ? e : null;

    /// <inheritdoc/>
    public void Set(EntityStatusEntry entry) => _entries[entry.EntityId] = entry;
}
