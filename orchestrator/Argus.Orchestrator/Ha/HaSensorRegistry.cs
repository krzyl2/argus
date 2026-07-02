using System.Globalization;

namespace Argus.Orchestrator.Ha;

/// <summary>
/// Thread-safe implementation of <see cref="IHaSensorRegistry"/> using a volatile immutable-array
/// reference swap (mirrors ArgusHealthSignals volatile-field pattern).
///
/// Single writer: NetDaemonHaEventSource calls UpdateSnapshot on every HA connect.
/// Many readers: Kestrel HTTP threads call GetAll/GetFiltered concurrently.
/// No lock contention; readers always observe a complete snapshot (no torn reads).
/// </summary>
public sealed class HaSensorRegistry : IHaSensorRegistry
{
    private volatile IReadOnlyList<HaSensorEntry> _snapshot = Array.Empty<HaSensorEntry>();

    /// <inheritdoc/>
    public IReadOnlyList<HaSensorEntry> GetAll() => _snapshot;

    /// <inheritdoc/>
    public IReadOnlyList<HaSensorEntry> GetFiltered(string q)
    {
        var current = _snapshot;
        if (string.IsNullOrEmpty(q))
            return current;

        // SRCH-01: match entity_id OR friendly_name (case-insensitive substring) — a strict
        // superset of the entity_id-only behavior, so existing entity_id searches are unaffected.
        return current
            .Where(e => e.EntityId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (e.FriendlyName is not null && e.FriendlyName.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <inheritdoc/>
    public void UpdateSnapshot(
        IReadOnlyList<HaStateDto> states,
        HashSet<string> trackedEntityIds,
        IReadOnlyDictionary<string, string?>? entityAreaNames = null)
    {
        var entries = states
            .Where(s => double.TryParse(s.State, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            .Select(s =>
            {
                double.TryParse(s.State, NumberStyles.Any, CultureInfo.InvariantCulture, out var value);
                var areaName = entityAreaNames is not null &&
                    entityAreaNames.TryGetValue(s.EntityId, out var area) ? area : null;
                var dotIndex = s.EntityId.IndexOf('.');
                var domain = dotIndex > 0 ? s.EntityId[..dotIndex] : s.EntityId;
                return new HaSensorEntry(
                    EntityId: s.EntityId,
                    CurrentValue: value,
                    UnitOfMeasurement: s.UnitOfMeasurement,
                    FriendlyName: s.FriendlyName,
                    IsTracked: trackedEntityIds.Contains(s.EntityId),
                    AreaName: areaName,
                    Domain: domain);
            })
            .OrderBy(e => e.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _snapshot = entries;
    }
}
