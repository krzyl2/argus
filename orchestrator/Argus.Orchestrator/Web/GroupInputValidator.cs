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
