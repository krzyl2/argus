using Argus.Orchestrator.Health;
using System.Net;
using System.Reflection;

namespace Argus.Orchestrator;

/// <summary>
/// Builds the Phase 1 server-rendered placeholder HTML page.
///
/// Emits &lt;base href="{ingressPath}/"&gt; so browser-relative hrefs (htmx, CSS)
/// resolve through the Supervisor Ingress proxy. T-01-08: ingressPath is HTML-encoded
/// before interpolation to prevent attribute injection.
///
/// Detector status is read from ArgusHealthSignals.DetectorConnected (zero-latency,
/// no gRPC call). Updated by HealthPublisherWorker every ~15 s.
/// </summary>
public static class PlaceholderPage
{
    private static readonly string _version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    /// <summary>
    /// Builds the placeholder HTML document.
    /// </summary>
    /// <param name="ingressPath">
    ///   Value of the X-Ingress-Path header (may be empty string when no proxy).
    ///   HTML-encoded before use in the &lt;base href&gt; attribute.
    /// </param>
    /// <param name="health">
    ///   ArgusHealthSignals singleton — zero-latency detector status read.
    /// </param>
    public static string Build(string ingressPath, ArgusHealthSignals health)
    {
        // T-01-08: HTML-encode ingressPath to prevent attribute injection
        var safeIngressPath = WebUtility.HtmlEncode(ingressPath);

        var (statusClass, statusLabel) = health.DetectorConnected
            ? ("status-ok", "Detector connected")
            : ("status-error", "Detector unreachable");

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="UTF-8">
              <meta name="viewport" content="width=device-width, initial-scale=1.0">
              <title>Argus</title>
              <base href="{{safeIngressPath}}/">
              <link rel="stylesheet" href="css/argus.css">
              <script src="js/htmx.min.js" defer></script>
            </head>
            <body>
              <header class="argus-header">
                <span class="argus-heading">Argus</span>
              </header>
              <main class="argus-main">
                <p class="argus-display">Argus is running</p>
                <p class="argus-body">Configuration UI coming soon. Sensor anomaly detection is active.</p>
                <div class="argus-status">
                  <span class="argus-status-dot {{statusClass}}"></span>
                  <span class="argus-label">{{statusLabel}}</span>
                </div>
              </main>
              <footer class="argus-footer">
                <span class="argus-label">v{{_version}}</span>
              </footer>
            </body>
            </html>
            """;
    }
}
