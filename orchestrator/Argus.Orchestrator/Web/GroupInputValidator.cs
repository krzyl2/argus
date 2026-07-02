using Argus.Orchestrator.Ha;

namespace Argus.Orchestrator.Web;

/// <summary>
/// Server-side input validator for POST /api/groups/save. Mirrors
/// EntitiesConfigLoader.ValidateGroups (floor of 3 members, peer-divergence unit consistency)
/// so a bad save request is rejected before any write reaches disk — the client-side
/// validation in orchestrator/ui/src/validation/groupParams.ts is UX-only; this is the
/// authoritative boundary (same "client is UX-only, server is authority" split as
/// InputValidator.cs for /api/sensors/save).
///
/// T-08-04: also caps Members.Count (DoS guard) — nothing else in the config model bounds
/// group size.
/// </summary>
public static class GroupInputValidator
{
    /// <summary>Upper bound on group size — DoS guard (T-08-04), no legitimate group needs more.</summary>
    public const int MaxMembers = 100;

    private const int MinMembers = 3;

    /// <summary>Joint-multivariate detector names — the only detectors valid for Mode="joint".</summary>
    public static readonly IReadOnlySet<string> JointDetectors =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ecod", "copod", "pca", "iforest" };

    /// <summary>
    /// CR-03: true if <paramref name="mode"/> and <paramref name="detector"/> are a valid pairing
    /// ("peer_divergence" mode requires detector "peer_divergence"; "joint" mode requires a
    /// <see cref="JointDetectors"/> member). Shared by <see cref="Validate"/> (save-time,
    /// authoritative) and the batch scheduler (defense-in-depth guard against a mismatch that
    /// reached disk via a hand-edited entities.yaml, bypassing this validator).
    /// </summary>
    public static bool IsModeDetectorConsistent(string mode, string detector)
    {
        if (string.Equals(mode, "peer_divergence", StringComparison.OrdinalIgnoreCase))
            return string.Equals(detector, "peer_divergence", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(mode, "joint", StringComparison.OrdinalIgnoreCase))
            return JointDetectors.Contains(detector);
        return false; // unknown mode is never consistent
    }

    /// <summary>
    /// Validates the raw (untrusted) group list parsed from a POST /api/groups/save body.
    /// </summary>
    /// <param name="groups">Groups parsed from the request body.</param>
    /// <param name="registry">
    /// Sensor registry used to resolve member units for the peer-divergence consistency check.
    /// May be empty (cold boot) — unit check degrades to skip when no units resolve.
    /// </param>
    /// <returns>Empty list on success; one or more error strings on failure.</returns>
    public static List<string> Validate(IReadOnlyList<GroupSaveEntry> groups, IHaSensorRegistry registry)
    {
        var errors = new List<string>();
        var unitsByEntityId = registry.GetAll()
            .GroupBy(e => e.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().UnitOfMeasurement, StringComparer.OrdinalIgnoreCase);

        // WR-01: reject a duplicate group_id in the submitted list instead of silently
        // letting the second entry overwrite the first (e.g. two friendly names that
        // slugify to the same id).
        var duplicateGroupIds = groups
            .Where(g => !string.IsNullOrWhiteSpace(g.GroupId))
            .GroupBy(g => g.GroupId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        foreach (var duplicateId in duplicateGroupIds)
        {
            errors.Add($"Duplicate group ID '{duplicateId}' — group IDs must be unique.");
        }

        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.GroupId))
            {
                errors.Add("Each group must have a group ID.");
                continue;
            }

            if (group.Members.Count > MaxMembers)
            {
                errors.Add($"Group '{group.GroupId}' has too many members (max {MaxMembers}).");
                continue;
            }

            if (group.Members.Count < MinMembers)
            {
                errors.Add($"Group '{group.GroupId}' needs at least {MinMembers} members.");
                continue;
            }

            var distinctMemberCount = group.Members.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (distinctMemberCount != group.Members.Count)
            {
                errors.Add($"Group '{group.GroupId}' has duplicate members.");
                continue;
            }

            var isPeerDivergence = string.Equals(group.Mode, "peer_divergence", StringComparison.OrdinalIgnoreCase);
            var isJoint = string.Equals(group.Mode, "joint", StringComparison.OrdinalIgnoreCase);

            if (!isPeerDivergence && !isJoint)
            {
                errors.Add($"Group '{group.GroupId}' has an unknown mode '{group.Mode}'.");
                continue;
            }

            // CR-03: mode/detector consistency — a joint-mode group must use one of the
            // four joint detectors, and a peer-divergence-mode group must use
            // detector="peer_divergence". Without this check, a mismatch (e.g. from a
            // client that silently defaulted detector to 'peer_divergence' with
            // mode="joint") reaches disk and publishes a fabricated verdict at batch time.
            if (!IsModeDetectorConsistent(group.Mode, group.Detector))
            {
                errors.Add(isPeerDivergence
                    ? $"Group '{group.GroupId}' is in peer-divergence mode but has detector '{group.Detector}' " +
                      "— peer-divergence mode requires detector 'peer_divergence'."
                    : $"Group '{group.GroupId}' is in joint mode but has an incompatible detector " +
                      $"'{group.Detector}' — joint mode requires one of: {string.Join(", ", JointDetectors)}.");
                continue;
            }

            if (isPeerDivergence)
            {
                var resolvedUnits = group.Members
                    .Select(m => unitsByEntityId.TryGetValue(m, out var unit) ? unit : null)
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Distinct()
                    .ToList();

                if (resolvedUnits.Count > 1)
                {
                    errors.Add(
                        $"Group '{group.GroupId}' members must share the same unit for peer-divergence " +
                        $"mode — found: {string.Join(", ", resolvedUnits)}.");
                }
            }
        }

        return errors;
    }
}
