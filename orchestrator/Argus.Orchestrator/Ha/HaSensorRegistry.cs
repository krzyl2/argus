using System.Globalization;

namespace Argus.Orchestrator.Ha;

/// <summary>
/// Thread-safe implementation of <see cref="IHaSensorRegistry"/> using a volatile immutable
/// state-object reference swap (mirrors ArgusHealthSignals volatile-field pattern).
///
/// Single writer: NetDaemonHaEventSource calls UpdateSnapshot on every HA connect and Upsert on
/// every state_changed event — both from its own connection loop, so writes never race in
/// practice. The write lock is defence in depth (copy-on-write read-modify-write would otherwise
/// lose an update if that ever stopped being true) and costs readers nothing.
/// Many readers: Kestrel HTTP threads call GetAll/GetFiltered concurrently.
/// Readers always observe a complete snapshot (no torn reads) — the id index and the sorted
/// list live in ONE object, so they can never disagree.
///
/// The sorted projection is built LAZILY, on the first read of each state version. Upsert runs
/// on the HA WebSocket receive loop — the same loop that has to deliver a reading to the scoring
/// pipeline inside the 2 s budget — and it fires on every numeric state_changed event, which on
/// a busy installation is tens per second against a few hundred entities. Sorting the whole
/// registry there spent O(N log N) per event to produce a list that only /api/sensors ever reads,
/// at human speed. Writes now cost only the index copy; the sort is paid once per state version,
/// by the reader that needs it.
/// </summary>
public sealed class HaSensorRegistry : IHaSensorRegistry
{
    /// <summary>
    /// Index + its sorted projection, swapped together so readers never see a half-update.
    ///
    /// ById is never mutated after construction (every writer builds a fresh dictionary), which
    /// is what makes the cached projection safe to compute after publication: two readers racing
    /// produce equal lists, and CompareExchange decides which one everybody keeps.
    /// </summary>
    private sealed class State
    {
        public State(Dictionary<string, HaSensorEntry> byId) => ById = byId;

        public Dictionary<string, HaSensorEntry> ById { get; }

        private IReadOnlyList<HaSensorEntry>? _sorted;

        public IReadOnlyList<HaSensorEntry> Sorted
        {
            get
            {
                // Volatile/Interlocked, not a plain field: without the fences a reader on a weak
                // memory model could publish the reference before the list's contents.
                var cached = Volatile.Read(ref _sorted);
                if (cached is not null)
                    return cached;

                var built = ById.Values
                    .OrderBy(e => e.EntityId, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return Interlocked.CompareExchange(ref _sorted, built, null) ?? built;
            }
        }
    }

    private static readonly State Empty =
        new(new Dictionary<string, HaSensorEntry>(StringComparer.OrdinalIgnoreCase));

    private readonly object _writeLock = new();
    private volatile State _state = Empty;

    /// <inheritdoc/>
    public IReadOnlyList<HaSensorEntry> GetAll() => _state.Sorted;

    /// <inheritdoc/>
    public IReadOnlyList<HaSensorEntry> GetFiltered(string q)
    {
        var current = _state.Sorted;
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
        var now = DateTime.UtcNow;

        lock (_writeLock)
        {
            var previous = _state.ById;
            var next = new Dictionary<string, HaSensorEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in states)
            {
                if (!double.TryParse(s.State, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                    continue;

                var areaName = entityAreaNames is not null &&
                    entityAreaNames.TryGetValue(s.EntityId, out var area) ? area : null;

                next[s.EntityId] = new HaSensorEntry(
                    EntityId: s.EntityId,
                    CurrentValue: value,
                    UnitOfMeasurement: s.UnitOfMeasurement,
                    FriendlyName: s.FriendlyName,
                    IsTracked: trackedEntityIds.Contains(s.EntityId),
                    AreaName: areaName,
                    Domain: DomainOf(s.EntityId),
                    KnownToHa: true,
                    StaleSince: null);
            }

            // Merge, do not drop (WS4/F10): an entity the previous snapshot had and this one does
            // not is kept and stamped stale. StaleSince records the FIRST pass it went missing, so
            // repeated reconnects do not keep resetting the clock.
            foreach (var (id, old) in previous)
            {
                if (next.ContainsKey(id))
                    continue;

                next[id] = old with
                {
                    IsTracked = trackedEntityIds.Contains(id),
                    StaleSince = old.StaleSince ?? now,
                };
            }

            _state = Materialize(next);
        }
    }

    /// <inheritdoc/>
    public bool Upsert(HaStateDto state, bool isTracked)
    {
        // Non-numeric is never a removal — see the interface docs. This is the SAME filter as
        // UpdateSnapshot's, deliberately: two spellings of "is this a numeric sensor" would mean
        // an entity could enter the picker one way and be invisible the other.
        if (!double.TryParse(state.State, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            return false;

        lock (_writeLock)
        {
            var previous = _state.ById;
            var isNew = !previous.TryGetValue(state.EntityId, out var old);

            var next = new Dictionary<string, HaSensorEntry>(previous, StringComparer.OrdinalIgnoreCase)
            {
                [state.EntityId] = new HaSensorEntry(
                    EntityId: state.EntityId,
                    CurrentValue: value,
                    UnitOfMeasurement: state.UnitOfMeasurement ?? old?.UnitOfMeasurement,
                    FriendlyName: state.FriendlyName ?? old?.FriendlyName,
                    // Area comes from the entity/area registries, which are only fetched per
                    // connect — a state_changed event carries none, so keep what we had.
                    IsTracked: isTracked,
                    AreaName: old?.AreaName,
                    Domain: DomainOf(state.EntityId),
                    KnownToHa: true,
                    StaleSince: null),
            };

            _state = Materialize(next);
            return isNew;
        }
    }

    private static State Materialize(Dictionary<string, HaSensorEntry> byId) => new(byId);

    private static string DomainOf(string entityId)
    {
        var dotIndex = entityId.IndexOf('.');
        return dotIndex > 0 ? entityId[..dotIndex] : entityId;
    }
}
