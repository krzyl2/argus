namespace Argus.Orchestrator.Mqtt;

/// <summary>
/// Deterministic unique_id and object_id formula (D-13, D-14, PITFALL 5).
/// No randomness — stable across restarts.
/// </summary>
public static class UniqueId
{
    /// <summary>entity_id with "." replaced by "_" (e.g. sensor.salon_temperatura → sensor_salon_temperatura).</summary>
    public static string Slug(string entityId) => entityId.Replace(".", "_");

    /// <summary>
    /// argus_{slug}_anomaly — binary_sensor unique_id (D-G).
    ///
    /// The detector name is deliberately NOT part of the identity. The state topic
    /// (argus/{slug}/flag/state) and the availability topics never carried it, so keeping it
    /// in unique_id/object_id meant that switching an entity's detector (hst -> rmad) created
    /// a SECOND HA entity fed by the SAME topic, while the first one stayed behind as a
    /// retained orphan RetractAsync never touches (it only handles removed entities).
    /// Cutting it here happens once; every future detector change is free.
    /// </summary>
    public static string AnomalyId(string entityId)
        => $"argus_{Slug(entityId)}_anomaly";

    /// <summary>argus_{slug}_score — score sensor unique_id (D-G, see AnomalyId).</summary>
    public static string ScoreId(string entityId)
        => $"argus_{Slug(entityId)}_score";

    /// <summary>
    /// Pre-D-G, detector-scoped binary_sensor unique_id. Retained ONLY so the one-shot
    /// migration can retract the retained discovery configs it published under the old
    /// formula; never use it to publish.
    /// </summary>
    public static string LegacyAnomalyId(string entityId, string detector)
        => $"argus_{Slug(entityId)}_{detector}_anomaly";

    /// <summary>Pre-D-G, detector-scoped score sensor unique_id (see LegacyAnomalyId).</summary>
    public static string LegacyScoreId(string entityId, string detector)
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
