namespace Argus.Orchestrator.Ha;

/// <summary>
/// Thread-safe read cache for the live numeric-sensor snapshot from Home Assistant.
/// Written exclusively by NetDaemonHaEventSource on every HA connect (get_states snapshot).
/// Read by Kestrel HTTP threads (Wave 2 entity-picker endpoints).
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
    /// Replaces the snapshot atomically from a get_states response.
    /// Filters to numeric-parseable states (invariant culture) and computes IsTracked
    /// from <paramref name="trackedEntityIds"/>.
    /// </summary>
    void UpdateSnapshot(IReadOnlyList<HaStateDto> states, HashSet<string> trackedEntityIds);
}

/// <summary>
/// A single numeric sensor entry in the registry snapshot.
/// </summary>
public record HaSensorEntry(
    string EntityId,
    double CurrentValue,
    string? UnitOfMeasurement,
    string? FriendlyName,
    bool IsTracked);
