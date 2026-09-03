namespace Argus.Orchestrator.Ha;

/// <summary>
/// Thread-safe read cache for the live numeric-sensor snapshot from Home Assistant.
/// Written by NetDaemonHaEventSource on every HA connect (get_states snapshot) and, since WS4,
/// on every state_changed event (Upsert). Read by Kestrel HTTP threads (entity-picker endpoints).
/// </summary>
public interface IHaSensorRegistry
{
    /// <summary>Returns the current snapshot of all numeric sensors.</summary>
    IReadOnlyList<HaSensorEntry> GetAll();

    /// <summary>
    /// Returns snapshot entries whose EntityId contains <paramref name="q"/> (case-insensitive).
    /// When <paramref name="q"/> is null or empty, returns the full snapshot (same as GetAll).
    /// </summary>
    IReadOnlyList<HaSensorEntry> GetFiltered(string q);

    /// <summary>
    /// Merges a get_states response into the snapshot atomically.
    /// Filters to numeric-parseable states (invariant culture) and computes IsTracked
    /// from <paramref name="trackedEntityIds"/>. <paramref name="entityAreaNames"/> maps
    /// entity_id -> resolved HA area name (SRCH-02/03; entity-only area_id + domain fallback
    /// for v1 — device_registry-inherited area resolution is out of scope this phase).
    /// Defaults to an empty map so existing callers/tests that don't need area enrichment
    /// are unaffected.
    ///
    /// WS4/F10: this is a MERGE, not a replacement. An entity that was in a previous snapshot
    /// but is missing (or non-numeric) in <paramref name="states"/> is retained and stamped with
    /// <see cref="HaSensorEntry.StaleSince"/>. A reconnect that catches HA mid-reload must not
    /// empty the picker — a stale row is a cheaper error than a vanished sensor.
    /// </summary>
    void UpdateSnapshot(
        IReadOnlyList<HaStateDto> states,
        HashSet<string> trackedEntityIds,
        IReadOnlyDictionary<string, string?>? entityAreaNames = null);

    /// <summary>
    /// Merges a single state_changed new_state into the snapshot (WS4/F10).
    ///
    /// This is what makes the registry more than connect-only: HA's state_changed subscription is
    /// global, so any entity that ever changes state becomes pickable without a second WebSocket
    /// (ADR-4) and without waiting for a reconnect. Entities that were <c>unknown</c>/<c>unavailable</c>
    /// during the boot snapshot — integrations still loading — are recovered by their first real value.
    ///
    /// A non-numeric state (<c>unavailable</c>, <c>unknown</c>, null) is IGNORED, never a removal:
    /// a sensor blinking unavailable for one event must not disappear from the picker.
    /// </summary>
    /// <param name="state">The new_state payload from a state_changed event.</param>
    /// <param name="isTracked">Whether this entity_id is in the live configured set.</param>
    /// <returns>True when the entity was not previously in the snapshot (i.e. this is a discovery).</returns>
    bool Upsert(HaStateDto state, bool isTracked);
}

/// <summary>
/// A single numeric sensor entry in the registry snapshot.
/// </summary>
/// <param name="KnownToHa">
/// False only for entries SYNTHESIZED from entities.yaml for a tracked entity that HA has never
/// shown us (F9: <c>sensor.zamrazarkapiwnica_power</c>). The registry itself never produces such
/// an entry — see <c>SensorTracking.GhostEntries</c> — but the row must still render and stay
/// editable, so the flag travels with the entry rather than living in a parallel list.
/// </param>
/// <param name="StaleSince">
/// UTC time at which this entity first went missing from a get_states snapshot, or null while it
/// is present. Non-null means "HA no longer lists it, we are keeping it on purpose".
/// </param>
public record HaSensorEntry(
    string EntityId,
    double CurrentValue,
    string? UnitOfMeasurement,
    string? FriendlyName,
    bool IsTracked,
    string? AreaName,
    string Domain,
    bool KnownToHa = true,
    DateTime? StaleSince = null);
