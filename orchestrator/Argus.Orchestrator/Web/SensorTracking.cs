using Argus.Orchestrator.Config;
using Argus.Orchestrator.Ha;

namespace Argus.Orchestrator.Web;

/// <summary>
/// Config-sourced tracked-id derivation, backing GET /api/sensors' isTracked field
/// (G-14-1 fix #2). The HA sensor registry's IsTracked flag is only recomputed on a live
/// HA WebSocket (re)connect (see NetDaemonHaEventSource.UpdateSnapshot call site) — it is
/// NOT reconciled by ILiveEntitiesConfig.Swap, so a just-saved sensor reads stale until a
/// reconnect happens. liveCfg.Get().Entities is the same config the save writes and swaps
/// synchronously, mirroring how GET /api/groups reads liveCfg.Get().Groups.
/// </summary>
public static class SensorTracking
{
    /// <summary>
    /// Returns the set of entity ids currently tracked per <paramref name="config"/>, computed
    /// fresh from <see cref="EntitiesConfig.Entities"/> at request time. Case-insensitive
    /// (OrdinalIgnoreCase) to match HA entity id comparison conventions elsewhere in the codebase.
    /// </summary>
    public static HashSet<string> TrackedIds(EntitiesConfig config) =>
        config.Entities.Select(e => e.EntityId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// WS4/F9: synthesizes a picker row for every TRACKED entity the HA snapshot does not contain.
    ///
    /// "In entities.yaml but not in GET /api/sensors" was a reachable state, and it is the worst
    /// one available: <c>sensor.zamrazarkapiwnica_power</c> was being scored (0.996) while being
    /// invisible in the UI, so it could be neither inspected nor untracked. Union-ing the config
    /// into the response makes that state unreachable by construction rather than by vigilance.
    ///
    /// The synthesized entry carries <c>KnownToHa: false</c> — the UI must say so, not pretend the
    /// value is merely missing. <paramref name="q"/> applies the same entity_id substring filter the
    /// registry applies, so a search does not suddenly surface unrelated ghosts.
    /// </summary>
    public static IReadOnlyList<HaSensorEntry> GhostEntries(
        IReadOnlyList<HaSensorEntry> snapshotEntries,
        EntitiesConfig config,
        string q)
    {
        var known = snapshotEntries
            .Select(e => e.EntityId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ghosts = new List<HaSensorEntry>();

        foreach (var configured in config.Entities)
        {
            var id = configured.EntityId;
            if (string.IsNullOrEmpty(id) || known.Contains(id))
                continue;
            if (!string.IsNullOrEmpty(q) && !id.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;

            var dotIndex = id.IndexOf('.');
            ghosts.Add(new HaSensorEntry(
                EntityId: id,
                // No value: the status cache holds warm-up/band state, not the last raw reading.
                // The projection reads CurrentValue only when KnownToHa is true.
                CurrentValue: 0.0,
                UnitOfMeasurement: null,
                FriendlyName: string.IsNullOrEmpty(configured.FriendlyName) ? null : configured.FriendlyName,
                IsTracked: true,
                AreaName: null,
                Domain: dotIndex > 0 ? id[..dotIndex] : id,
                KnownToHa: false,
                StaleSince: null));

            // Dedupe against a config that lists the same entity twice.
            known.Add(id);
        }

        return ghosts;
    }
}
