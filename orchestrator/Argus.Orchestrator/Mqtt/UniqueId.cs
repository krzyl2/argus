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
}
