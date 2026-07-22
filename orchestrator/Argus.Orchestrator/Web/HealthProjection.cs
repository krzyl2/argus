using Argus.Orchestrator.Config;
using Argus.Orchestrator.Health;

namespace Argus.Orchestrator.Web;

/// <summary>One health component row in the Dashboard's "System health" list.</summary>
public record HealthComponent(string Key, string Label, string Status, string Detail);

/// <summary>Home Assistant connection state + live entity count (drives the Dashboard KPI).</summary>
public record HomeAssistantHealth(bool Connected, int EntityCount);

/// <summary>Full GET /api/health response payload.</summary>
public record HealthResponse(HomeAssistantHealth HomeAssistant, IReadOnlyList<HealthComponent> Components);

/// <summary>
/// Redacted, allowlist projection of live health signals + ConnectionSettings into the
/// GET /api/health response (QUICK-dashboard-real-data). D-07: mirrors SettingsProjection's
/// allowlist discipline — this class is the sole boundary between in-process
/// ArgusHealthSignals/ConnectionSettings (which hold connection credentials) and the health
/// JSON surface. Build MUST NOT read HaToken, MqttUser, MqttPassword, InfluxToken, or TLS
/// key/cert fields.
/// </summary>
public static class HealthProjection
{
    /// <summary>
    /// Composes the 5 allowlisted health components: Home Assistant, Detector, MQTT broker,
    /// Last batch run, InfluxDB. Status values are one of ok | warn | error | idle, matching
    /// the SPA's StatusDot union 1:1.
    /// </summary>
    public static HealthResponse Build(
        ArgusHealthSignals signals,
        bool mqttConnected,
        int haEntityCount,
        ConnectionSettings settings,
        DateTimeOffset? lastBatchRunUtc,
        DateTimeOffset now)
    {
        var homeAssistant = new HealthComponent(
            "homeAssistant",
            "Home Assistant (WebSocket)",
            signals.HaConnected ? "ok" : "error",
            signals.HaConnected ? $"Connected · {haEntityCount} entities" : "Disconnected");

        var detectorDetail = string.IsNullOrWhiteSpace(settings.DetectorEndpoint)
            ? "not configured"
            : signals.DetectorConnected
                ? $"{settings.DetectorEndpoint} · serving"
                : $"{settings.DetectorEndpoint} · unreachable";
        var detector = new HealthComponent(
            "detector",
            "Detector (gRPC, mTLS)",
            signals.DetectorConnected ? "ok" : "warn",
            detectorDetail);

        var mqtt = new HealthComponent(
            "mqtt",
            "MQTT broker",
            mqttConnected ? "ok" : "warn",
            mqttConnected ? "Connected" : "Disconnected");

        var batch = BuildBatchComponent(settings.InfluxUrl, lastBatchRunUtc, settings.BatchIntervalMinutes, now);

        var influxDetail = string.IsNullOrWhiteSpace(settings.InfluxUrl)
            ? "Not configured — streaming-only"
            : string.IsNullOrWhiteSpace(settings.InfluxBucket)
                ? settings.InfluxUrl!
                : $"{settings.InfluxUrl} · bucket {settings.InfluxBucket}";
        var influx = new HealthComponent(
            "influx",
            "InfluxDB",
            string.IsNullOrWhiteSpace(settings.InfluxUrl) ? "idle" : "ok",
            influxDetail);

        return new HealthResponse(
            new HomeAssistantHealth(signals.HaConnected, haEntityCount),
            [homeAssistant, detector, mqtt, batch, influx]);
    }

    /// <summary>
    /// Computes the "Last batch run" health component. Standalone/testable: disabled (idle)
    /// when InfluxDB is not configured; "Not run yet" (warn) before the first run; overdue
    /// (warn) when more than 1.5x the interval has elapsed since the last run; otherwise ok.
    /// </summary>
    public static HealthComponent BuildBatchComponent(
        string? influxUrl, DateTimeOffset? lastRunUtc, int intervalMinutes, DateTimeOffset now)
    {
        const string key = "batch";
        const string label = "Last batch run";

        if (string.IsNullOrWhiteSpace(influxUrl))
            return new HealthComponent(key, label, "idle", "Disabled — streaming-only");

        if (lastRunUtc is null)
            return new HealthComponent(key, label, "warn", "Not run yet");

        var minutesSince = (now - lastRunUtc.Value).TotalMinutes;
        if (minutesSince > intervalMinutes * 1.5)
        {
            var overdueBy = Math.Round(minutesSince - intervalMinutes);
            return new HealthComponent(key, label, "warn",
                $"Overdue by {overdueBy} min (interval {intervalMinutes} min)");
        }

        var minutesAgo = Math.Round(minutesSince);
        return new HealthComponent(key, label, "ok",
            $"{minutesAgo} min ago (interval {intervalMinutes} min)");
    }
}
