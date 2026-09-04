namespace Argus.Orchestrator.Batch;

/// <summary>
/// Shapes the two Flux filter fragments that every Influx query in this folder shares, so
/// both readers agree on how Home Assistant's <c>influxdb:</c> integration ACTUALLY writes
/// sensor state. Both fragments existed inline in <see cref="InfluxDbReader"/> and
/// <see cref="GroupInfluxReader"/> and both were wrong against a stock HA writer:
///
///  - <see cref="EntityTag"/>: HA tags each point with the entity's OBJECT ID, not its full
///    entity id — <c>domain=sensor, entity_id=salon_temperature</c> for
///    <c>sensor.salon_temperature</c>. Filtering on the full id matched zero series, so the
///    whole Influx batch path (single-sensor AND group) silently returned no rows on every
///    cycle. Nobody noticed because InfluxDB had never been configured: with no
///    <c>influx_url</c> the batch worker is not even registered (Program.cs), so the defect
///    only surfaced the moment batch was switched on.
///
///  - <see cref="MeasurementClause"/>: HA's measurement name is the entity's
///    UNIT OF MEASUREMENT (<c>°C</c>, <c>%</c>, <c>V</c>, <c>W</c>, <c>kPa</c>, …) unless the
///    operator sets <c>override_measurement</c>. A single global <c>influx_measurement</c>
///    equality therefore cannot serve a group whose members span units — a temperature +
///    humidity group loses its humidity columns, and for joint mode a dropped column skips
///    the whole group forever. The measurement filter is now OPTIONAL: leave
///    <c>influx_measurement</c> empty (the new default) and the series is identified by
///    <c>entity_id</c> + <c>_field</c> alone, which is already unique. Setups that DO use
///    <c>override_measurement</c> keep the filter by naming it.
/// </summary>
internal static class InfluxFilter
{
    /// <summary>
    /// Maps a Home Assistant entity id onto the value HA writes into the <c>entity_id</c>
    /// tag: everything after the first dot. Returns the input unchanged when it carries no
    /// domain prefix (already an object id, or a hand-written config value) — the query then
    /// behaves exactly as it did before this helper existed.
    /// </summary>
    public static string EntityTag(string entityId)
    {
        var dot = entityId.IndexOf('.');
        return dot >= 0 && dot < entityId.Length - 1 ? entityId[(dot + 1)..] : entityId;
    }

    /// <summary>
    /// Renders the leading <c>_measurement</c> equality, INCLUDING its trailing
    /// <c>and </c>, or an empty string when no measurement is configured. Returning the
    /// connector with the clause is what lets both call sites keep one interpolation site
    /// instead of branching the whole filter expression.
    /// </summary>
    public static string MeasurementClause(string? measurement)
        => string.IsNullOrWhiteSpace(measurement)
            ? string.Empty
            : $"r[\"_measurement\"] == \"{measurement}\" and ";
}
