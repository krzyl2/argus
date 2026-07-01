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

        return current
            .Where(e => e.EntityId.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <inheritdoc/>
    public void UpdateSnapshot(IReadOnlyList<HaStateDto> states, HashSet<string> trackedEntityIds)
    {
        var entries = states
            .Where(s => double.TryParse(s.State, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            .Select(s =>
            {
                double.TryParse(s.State, NumberStyles.Any, CultureInfo.InvariantCulture, out var value);
                return new HaSensorEntry(
                    EntityId: s.EntityId,
                    CurrentValue: value,
                    UnitOfMeasurement: s.UnitOfMeasurement,
                    FriendlyName: s.FriendlyName,
                    IsTracked: trackedEntityIds.Contains(s.EntityId));
            })
            .OrderBy(e => e.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _snapshot = entries;
    }
}
