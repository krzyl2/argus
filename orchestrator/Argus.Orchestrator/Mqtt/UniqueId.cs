namespace Argus.Orchestrator.Mqtt;

/// <summary>
/// Deterministic unique_id and object_id formula (D-13, D-14, PITFALL 5).
/// No randomness — stable across restarts.
/// </summary>
public static class UniqueId
{
    /// <summary>entity_id with "." replaced by "_" (e.g. sensor.salon_temperatura → sensor_salon_temperatura).</summary>
    public static string Slug(string entityId) => entityId.Replace(".", "_");

    /// <summary>argus_{slug}_{detector}_anomaly — binary_sensor unique_id.</summary>
    public static string AnomalyId(string entityId, string detector)
        => $"argus_{Slug(entityId)}_{detector}_anomaly";

    /// <summary>argus_{slug}_{detector}_score — score sensor unique_id.</summary>
    public static string ScoreId(string entityId, string detector)
        => $"argus_{Slug(entityId)}_{detector}_score";

    /// <summary>
    /// Group binary_sensor unique_id (GRP-08).
    /// argus_group_{groupSlug}_flag (joint, memberId null) or argus_group_{groupSlug}_{memberSlug}_flag (peer).
    /// </summary>
    public static string GroupFlagId(string groupId, string? memberId = null)
        => memberId is null
            ? $"argus_group_{Slug(groupId)}_flag"
            : $"argus_group_{Slug(groupId)}_{Slug(memberId)}_flag";

    /// <summary>
    /// Group score sensor unique_id (GRP-08).
    /// argus_group_{groupSlug}_score (joint, memberId null) or argus_group_{groupSlug}_{memberSlug}_score (peer).
    /// </summary>
    public static string GroupScoreId(string groupId, string? memberId = null)
        => memberId is null
            ? $"argus_group_{Slug(groupId)}_score"
            : $"argus_group_{Slug(groupId)}_{Slug(memberId)}_score";
}
