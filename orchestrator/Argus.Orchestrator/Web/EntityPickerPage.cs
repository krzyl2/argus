using Argus.Orchestrator.Config;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Health;
using System.Net;
using System.Reflection;
using System.Text;

namespace Argus.Orchestrator.Web;

/// <summary>
/// Builds the Phase-2 entity-picker HTML page and htmx fragments.
///
/// T-02-07: All user-originated strings (entity_id, friendly_name, query, patterns)
/// are HTML-encoded with <see cref="WebUtility.HtmlEncode"/> before interpolation.
///
/// Emits &lt;base href="{ingressPath}/"&gt; so browser-relative hrefs resolve through
/// the Supervisor Ingress proxy. The full page uses an inline max-width:880px override
/// on .argus-main (wider than the Phase 1 default 720px) to accommodate the two-column
/// filter panel.
/// </summary>
public static class EntityPickerPage
{
    private static readonly string _version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    /// <summary>
    /// Builds the full entity-picker page (GET /sensors).
    /// </summary>
    /// <param name="ingressPath">X-Ingress-Path header value — HTML-encoded before use.</param>
    /// <param name="registry">Live sensor snapshot source.</param>
    /// <param name="config">Currently loaded entity config (for pre-populating tracked state).</param>
    /// <param name="health">Add-on health signals (detector connectivity).</param>
    /// <param name="q">Current search query.</param>
    /// <param name="includePatterns">Last-known include patterns (pre-filled in the textarea).</param>
    /// <param name="excludePatterns">Last-known exclude patterns (pre-filled in the textarea).</param>
    public static string BuildFullPage(
        string ingressPath,
        IHaSensorRegistry registry,
        EntitiesConfig config,
        ArgusHealthSignals health,
        string q,
        string includePatterns = "",
        string excludePatterns = "")
    {
        // T-02-07: HTML-encode ingressPath to prevent attribute injection (mirrors T-01-08)
        var safeIngressPath = WebUtility.HtmlEncode(ingressPath);
        var safeInclude = WebUtility.HtmlEncode(includePatterns);
        var safeExclude = WebUtility.HtmlEncode(excludePatterns);
        var safeQ = WebUtility.HtmlEncode(q);

        var listFragment = BuildListRows(registry.GetFiltered(q));

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
              <main class="argus-main" style="max-width:880px">
                <div>
                  <p class="argus-heading">Entity Selection</p>
                  <p class="argus-body">Select the sensors Argus monitors. Changes take effect on the next pipeline cycle.</p>
                </div>

                <form id="argus-picker-form"
                      hx-post="api/sensors/save"
                      hx-target="#argus-flash"
                      hx-swap="innerHTML"
                      hx-indicator="#argus-spinner"
                      hx-push-url="false">

                  <div class="argus-search">
                    <input class="argus-search__input"
                           type="search"
                           name="q"
                           value="{{safeQ}}"
                           placeholder="Filter by entity ID…"
                           aria-label="Filter entities"
                           hx-get="api/sensors"
                           hx-target="#argus-sensor-list"
                           hx-trigger="keyup changed delay:200ms"
                           hx-push-url="false">
                  </div>

                  <p class="argus-heading">Sensors</p>
                  <ul id="argus-sensor-list" class="argus-list">
                    {{listFragment}}
                  </ul>

                  <p class="argus-heading">Pattern Filters</p>
                  <div class="argus-filters">
                    <div class="argus-filters__group">
                      <label class="argus-filters__label argus-label" for="include_patterns">Include patterns</label>
                      <textarea id="include_patterns"
                                name="include_patterns"
                                class="argus-filters__textarea"
                                rows="4"
                                placeholder="e.g. sensor.*temp*">{{safeInclude}}</textarea>
                    </div>
                    <div class="argus-filters__group">
                      <label class="argus-filters__label argus-label" for="exclude_patterns">Exclude patterns</label>
                      <textarea id="exclude_patterns"
                                name="exclude_patterns"
                                class="argus-filters__textarea"
                                rows="4"
                                placeholder="e.g. sensor.*test*">{{safeExclude}}</textarea>
                    </div>
                  </div>
                  <p class="argus-label">One glob pattern per line. Manual selections override patterns.</p>

                  <div id="argus-save-bar" class="argus-save-bar">
                    <span id="argus-spinner" aria-hidden="true"></span>
                    <button type="submit" class="argus-btn argus-btn--primary">Save configuration</button>
                  </div>

                </form>

                <div id="argus-flash"></div>

              </main>
              <footer class="argus-footer">
                <span class="argus-label">v{{_version}}</span>
              </footer>
            </body>
            </html>
            """;
    }

    /// <summary>
    /// Builds the sensor list fragment only — bare &lt;li&gt; rows for htmx swap into
    /// #argus-sensor-list (GET /api/sensors). No HTML shell.
    /// </summary>
    public static string BuildListFragment(IHaSensorRegistry registry, string q)
    {
        var entries = registry.GetFiltered(q);
        return BuildListRows(entries, q);
    }

    /// <summary>
    /// Builds the save-success banner fragment (POST /api/sensors/save → success path).
    /// </summary>
    public static string BuildSuccessBanner(int count)
    {
        return $"""
            <div class="argus-banner argus-banner--success"
                 role="status" aria-live="polite">
              Configuration saved. {count} {(count == 1 ? "entity" : "entities")} tracked.
            </div>
            """;
    }

    /// <summary>
    /// Builds the save-error banner fragment (POST /api/sensors/save → error path).
    /// T-02-11: reason is HTML-encoded; no internal exception detail leaks to the browser.
    /// </summary>
    public static string BuildErrorBanner(string reason)
    {
        var safeReason = WebUtility.HtmlEncode(reason);
        return $"""
            <div class="argus-banner argus-banner--error"
                 role="alert" aria-live="assertive">
              Save failed. {safeReason}. Check the add-on log for details.
            </div>
            """;
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private static string BuildListRows(IReadOnlyList<HaSensorEntry> entries, string? q = null)
    {
        if (entries.Count == 0)
        {
            return BuildEmptyState(q);
        }

        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            // T-02-07: HTML-encode all user-originated strings
            var safeEntityId = WebUtility.HtmlEncode(entry.EntityId);
            var safeValue = entry.CurrentValue.ToString("G");
            var safeUnit = entry.UnitOfMeasurement is not null
                ? WebUtility.HtmlEncode(entry.UnitOfMeasurement)
                : null;

            var checkedAttr = entry.IsTracked ? " checked" : "";
            var trackedClass = entry.IsTracked ? " argus-list-row--tracked" : "";

            // Friendly name: only render when present and differs from entity_id
            var showFriendlyName = !string.IsNullOrEmpty(entry.FriendlyName) &&
                                   !string.Equals(entry.FriendlyName, entry.EntityId, StringComparison.Ordinal);
            var safeFriendlyName = showFriendlyName
                ? WebUtility.HtmlEncode(entry.FriendlyName)
                : null;

            var friendlyNameHtml = safeFriendlyName is not null
                ? $"\n            <span class=\"argus-row-friendly-name\">{safeFriendlyName}</span>"
                : "";

            var valueDisplay = safeUnit is not null
                ? $"{safeValue} {safeUnit}"
                : safeValue;

            var trackedPillHtml = entry.IsTracked
                ? "\n            <span class=\"argus-pill argus-pill--tracked\">tracked</span>"
                : "";

            sb.AppendLine($"""
                <li class="argus-list-row{trackedClass}">
                  <label style="display:contents">
                    <input class="argus-checkbox" type="checkbox" name="entities"
                           value="{safeEntityId}"{checkedAttr} aria-label="{safeEntityId}">
                    <div class="argus-row-content">
                      <span class="argus-row-entity-id">{safeEntityId}</span>{friendlyNameHtml}
                    </div>
                    <div class="argus-row-meta">
                      <span class="argus-row-value">{valueDisplay}</span>{trackedPillHtml}
                    </div>
                  </label>
                </li>
                """);
        }
        return sb.ToString();
    }

    private static string BuildEmptyState(string? q)
    {
        if (!string.IsNullOrEmpty(q))
        {
            var safeQ = WebUtility.HtmlEncode(q);
            return $"""
                <div class="argus-empty">
                  <p class="argus-body">No sensors match &#x22;{safeQ}&#x22;.</p>
                  <p class="argus-label">Try a different search term or clear the filter.</p>
                </div>
                """;
        }

        return """
            <div class="argus-empty">
              <p class="argus-body">No sensors found.</p>
              <p class="argus-label">Argus has not yet received a sensor snapshot from Home Assistant. Check that the add-on can reach the Supervisor and that the detector is running.</p>
            </div>
            """;
    }
}
