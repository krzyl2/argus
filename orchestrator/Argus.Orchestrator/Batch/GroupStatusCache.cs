using System.Collections.Concurrent;

namespace Argus.Orchestrator.Batch;

/// <summary>One member's ranked contribution to a joint-multivariate group verdict (GRP-09).</summary>
public sealed record FeatureContributionDto(string MemberId, double Contribution);

/// <summary>
/// Last known joint-multivariate verdict for a group, cached in memory. Contributions are
/// always sorted descending before being stored (RESEARCH Pitfall 4) and are an empty list
/// (never null, never fabricated) for detectors that produce no per-feature attribution
/// (pca/iforest).
/// </summary>
public sealed record GroupStatusEntry(
    string GroupId,
    double? Score,
    bool? IsAnomaly,
    string Detector,
    DateTimeOffset ScoredAtUtc,
    IReadOnlyList<FeatureContributionDto> Contributions);

/// <summary>
/// In-memory last-verdict cache backing GET /api/groups/{id}/status (GRP-09).
/// Single writer (BatchSchedulerWorker's joint-mode branch), many readers (Kestrel) —
/// mirrors the ArgusHealthSignals/HaSensorRegistry volatile-cache precedent, generalized
/// to a ConcurrentDictionary since the key set (group_id) is open, not fixed.
/// </summary>
public interface IGroupStatusCache
{
    /// <summary>Returns the last cached entry for <paramref name="groupId"/>, or null if never scored.</summary>
    GroupStatusEntry? Get(string groupId);

    /// <summary>Stores/replaces the cached entry for the entry's GroupId.</summary>
    void Set(GroupStatusEntry entry);
}

/// <inheritdoc cref="IGroupStatusCache"/>
public sealed class GroupStatusCache : IGroupStatusCache
{
    private readonly ConcurrentDictionary<string, GroupStatusEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public GroupStatusEntry? Get(string groupId) =>
        _entries.TryGetValue(groupId, out var e) ? e : null;

    /// <inheritdoc/>
    public void Set(GroupStatusEntry entry) => _entries[entry.GroupId] = entry;
}
