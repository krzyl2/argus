using System.Collections.Concurrent;
using Argus.Orchestrator.Config;

namespace Argus.Orchestrator.Detection;

/// <summary>
/// Process-lifetime home for per-entity <see cref="AlertPolicy"/> instances (WS2).
///
/// WHY this exists: HaListenerWorker rebuilds every EntityRuntimeState on every config Save,
/// and the SPA's Save rewrites the whole tracked list from any screen. Without a store the
/// rank/raw windows would reset on each of those, and on the slow sensors that is expensive —
/// zamrazarkapiwnica_power produces ~225 verdicts a day, so 240-sample calibration means one
/// unrelated Save costs ~26 hours of blindness.
///
/// A policy is kept only while its params are unchanged. A params change yields a fresh policy
/// (its LastPublishedFlag is null, so the next verdict republishes the current flag value once —
/// accepted, and cheaper than reasoning about a half-migrated window).
///
/// In memory only. D-11 (gate state is not persisted across restarts) still holds.
/// </summary>
public sealed class AlertStateStore
{
    private readonly ConcurrentDictionary<string, (AlertParams Params, AlertPolicy Policy)> _states =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the entity's live policy, reusing it when <paramref name="alertParams"/> equals
    /// the params it was created with (record value equality), otherwise creating a fresh one.
    /// </summary>
    public AlertPolicy GetOrCreate(string entityId, AlertParams alertParams)
    {
        var entry = _states.AddOrUpdate(
            entityId,
            _ => (alertParams, new AlertPolicy(alertParams)),
            (_, existing) => existing.Params == alertParams
                ? existing
                : (alertParams, new AlertPolicy(alertParams)));

        return entry.Policy;
    }

    /// <summary>Drops policies for entities that are no longer tracked (called after each rebuild).</summary>
    public void PruneTo(IEnumerable<string> entityIds)
    {
        var keep = new HashSet<string>(entityIds, StringComparer.OrdinalIgnoreCase);
        foreach (var key in _states.Keys)
            if (!keep.Contains(key))
                _states.TryRemove(key, out _);
    }
}
