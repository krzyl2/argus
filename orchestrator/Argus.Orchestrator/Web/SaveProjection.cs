using Argus.Orchestrator.Config;
using Argus.Orchestrator.Ha;

namespace Argus.Orchestrator.Web;

/// <summary>
/// The POST /api/sensors/save projection: request body + registry snapshot + on-disk config
/// -> the <see cref="EntityConfig"/> list that gets serialized into entities.yaml.
///
/// WHY this is a class and not inline handler code: FIX-PLAN "Czego nie robimy" rules out
/// Microsoft.AspNetCore.Mvc.Testing, so the handler itself can never be exercised by a test —
/// testability is bought by keeping NO decision inside the handler. While the projection lived
/// in Program.cs the tests carried their own copy of it, and a copy cannot go red when the
/// original changes: reverting the detector fallback in the handler to the old index-keyed
/// `[rmad, {}]` (i.e. reintroducing the whole configuration-loss bug) left all 659 tests green.
/// Every step of the save path therefore lives here, and both the handler and the tests call
/// these methods rather than reimplementing them.
/// </summary>
public static class SaveProjection
{
    /// <summary>
    /// Detector rows from the POST body, keyed by entity id (case-insensitive, matching HA
    /// entity id conventions). A body row with an empty id is dropped — it cannot be matched
    /// to a resolved entity and would only collide in the dictionary.
    /// </summary>
    public static Dictionary<string, List<DetectorConfig>> SubmittedByEntityId(SaveRequest body)
        => body.Entities
            .Where(e => !string.IsNullOrEmpty(e.EntityId))
            .ToDictionary(
                e => e.EntityId,
                e => e.Detectors
                    .Select(d => new DetectorConfig { Name = d.Name, Params = d.Params })
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The resolved entity ids in write order: alphabetical, case-insensitive. This ordering is
    /// the definition of the "entity index" used by <see cref="ByEntityIndex"/>.
    /// </summary>
    public static List<string> SortIds(IEnumerable<string> resolvedIds)
        => resolvedIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Re-keys the submitted rows by entity INDEX (position in <paramref name="sortedIds"/>),
    /// which is the shape <see cref="InputValidator.Validate"/> takes.
    ///
    /// Two keyings of the same data coexist on the save path on purpose: validation is
    /// index-keyed for historical parity with the v3.0 form parser, while the detector decision
    /// is id-keyed because "this entity was not submitted at all" is exactly the case an
    /// index-keyed map cannot express (every index always has an entry, empty at worst). They
    /// must stay equivalent for every entity the body does carry — see the equivalence test.
    /// </summary>
    public static Dictionary<int, List<DetectorConfig>> ByEntityIndex(
        IReadOnlyList<string> sortedIds,
        IReadOnlyDictionary<string, List<DetectorConfig>> submittedByEntityId)
        => sortedIds
            .Select((id, ei) => (ei, dets: submittedByEntityId.TryGetValue(id, out var d)
                ? d
                : new List<DetectorConfig>()))
            .ToDictionary(x => x.ei, x => x.dets);

    /// <summary>
    /// Builds the entity list that will be written, one entry per resolved id in write order.
    ///
    /// Friendly name: the HA snapshot wins, then whatever is already on disk (WS4/F9 — an
    /// entity HA is not listing has no snapshot name, and blanking its label on every save is
    /// worse than keeping a stale one), then empty.
    ///
    /// Detectors: delegated to <see cref="SensorTracking.ResolveDetectors"/> — submitted row
    /// wins, otherwise what is on disk, otherwise the rmad default (D-A).
    ///
    /// Note that a detector block coming from <paramref name="preSaveByEntityId"/> is written
    /// back WITHOUT passing through <see cref="InputValidator"/>: the handler validates the
    /// body only. That is deliberate. The block came from a file EntitiesConfigLoader already
    /// accepted, and the alternative — dropping anything the current validator dislikes — is
    /// the silent configuration loss this projection exists to prevent. A hand-edited
    /// entities.yaml block is preserved on save rather than quietly reset to rmad.
    /// </summary>
    public static List<EntityConfig> BuildEntities(
        IReadOnlyList<string> sortedIds,
        IReadOnlyDictionary<string, List<DetectorConfig>> submittedByEntityId,
        IReadOnlyDictionary<string, HaSensorEntry> snapshotById,
        IReadOnlyDictionary<string, EntityConfig> preSaveByEntityId)
        => sortedIds
            .Select(id =>
            {
                snapshotById.TryGetValue(id, out var entry);
                preSaveByEntityId.TryGetValue(id, out var stored);

                return new EntityConfig
                {
                    EntityId = id,
                    FriendlyName = entry?.FriendlyName ?? stored?.FriendlyName ?? "",
                    Detectors = SensorTracking.ResolveDetectors(id, submittedByEntityId, preSaveByEntityId),
                };
            })
            .ToList();

    /// <summary>The on-disk config keyed by entity id, tolerating a file that lists one twice.</summary>
    public static Dictionary<string, EntityConfig> ByEntityId(EntitiesConfig config)
        => config.Entities
            .GroupBy(e => e.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
}
