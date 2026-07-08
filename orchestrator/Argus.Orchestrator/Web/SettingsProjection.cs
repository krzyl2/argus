using Argus.Orchestrator.Config;
using Microsoft.Extensions.Configuration;

namespace Argus.Orchestrator.Web;

/// <summary>
/// Redacted, field-by-field projection of ConnectionSettings + IConfiguration into the
/// GET /api/settings response (D-06). Backing the Settings screen with truthful live
/// configuration instead of mocked values.
///
/// D-07: this class is the sole allowlist boundary between in-process configuration (which
/// holds connection credentials) and the JSON surface exposed over Ingress HTTP. Build MUST
/// read ConnectionSettings field-by-field and MUST NOT serialize the settings object as a
/// whole — any field not explicitly listed here never reaches the response.
/// </summary>
public static class SettingsProjection
{
    /// <summary>
    /// Projects the 6 non-sensitive configuration fields the Settings screen needs.
    /// logLevel is read from IConfiguration (not ConnectionSettings, which has no such field)
    /// and is null when unset — never a hardcoded guess.
    /// </summary>
    public static object Build(ConnectionSettings settings, IConfiguration config)
    {
        return new
        {
            detectorEndpoint = settings.DetectorEndpoint,
            influxUrl = settings.InfluxUrl,
            influxBucket = settings.InfluxBucket,
            batchIntervalMinutes = settings.BatchIntervalMinutes,
            nightlyFitHour = settings.NightlyFitHour,
            logLevel = config["Logging:LogLevel:Default"],
        };
    }
}
