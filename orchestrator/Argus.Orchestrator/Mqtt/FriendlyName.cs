namespace Argus.Orchestrator.Mqtt;

/// <summary>
/// Polish friendly-name derivation for HA entities (D-16).
/// Appends " anomalia" to the configured friendly_name, preserving UTF-8/Polish characters.
/// </summary>
public static class FriendlyName
{
    /// <summary>Returns "{friendlyName} anomalia" (e.g. "Salon temperatura anomalia").</summary>
    public static string ForAnomaly(string friendlyName) => $"{friendlyName} anomalia";
}
