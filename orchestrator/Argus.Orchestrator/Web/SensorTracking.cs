using Argus.Orchestrator.Config;

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
}
