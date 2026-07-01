using Argus.Orchestrator.Config;
using Argus.Orchestrator.Ha;
using Argus.Orchestrator.Health;
using System.Net;
using System.Reflection;
using System.Text;

namespace Argus.Orchestrator.Web;

/// <summary>
/// Builds the Phase-2/3 entity-picker HTML page and htmx fragments.
///
/// T-02-07: All user-originated strings (entity_id, friendly_name, query, patterns)
/// are HTML-encoded with <see cref="WebUtility.HtmlEncode"/> before interpolation.
///
/// T-03-11: All detector-originated strings (Name, Params values) are HTML-encoded
/// before interpolation in BuildDetectorEntry.
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

    // HST defaults (D-09/D-11/D-12)
    private const string HstWindowDefault = "250";
    private const string HstNTreesDefault = "25";
    private const string HstHighThresholdDefault = "0.7";
    private const string HstLowThresholdDefault = "0.3";
    private const string HstMinConsecutiveDefault = "3";
    private const string HstFrozenWindowDefault = "10";
    private const string HstFrozenVarianceThresholdDefault = "0.001";

    // MAD defaults
    private const string MadThresholdDefault = "3.5";
    private const string MadWindowDefault = "20";

    // STL defaults
    private const string StlPeriodDefault = "24";
    private const string StlSeasonalDefault = "7";
    private const string StlThresholdDefault = "3.0";

    /// <summary>
    /// Builds the full entity-picker page (GET /sensors).
    /// </summary>
    /// <param name="ingressPath">X-Ingress-Path header value — HTML-encoded before use.</param>
    /// <param name="registry">Live sensor snapshot source.</param>
    /// <param name="config">Currently loaded entity config (for pre-populating tracked state + detector assignments).</param>
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

        var listFragment = BuildListRows(registry.GetFiltered(q), config);

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
                  <p class="argus-body">Select the sensors Argus monitors and assign detectors to each.</p>
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
    /// <param name="registry">Live sensor snapshot source.</param>
    /// <param name="config">Currently loaded entity config — passed through to BuildListRows so
    /// tracked-entity detector disclosure panels are preserved on htmx search refresh.
    /// Use <see cref="ILiveEntitiesConfig.Get()"/> at the call site (not a captured stale ref).</param>
    /// <param name="q">Current search query.</param>
    public static string BuildListFragment(IHaSensorRegistry registry, EntitiesConfig config, string q)
    {
        var entries = registry.GetFiltered(q);
        return BuildListRows(entries, config, q);
    }

    /// <summary>
    /// Builds one .argus-detector-entry HTML fragment.
    /// Used both in full-page rendering and in the GET /api/detectors/new-entry htmx endpoint.
    /// T-03-11: detector.Name and all param values are HTML-encoded.
    /// </summary>
    /// <param name="entityIdx">0-based index of the entity within the tracked set.</param>
    /// <param name="detIdx">0-based index of this detector within the entity's detector list.</param>
    /// <param name="detector">The detector config to render (pre-filled from entities.yaml or defaults).</param>
    public static string BuildDetectorEntry(int entityIdx, int detIdx, DetectorConfig detector)
    {
        var detectorNameLower = detector.Name?.ToLowerInvariant() ?? "hst";

        // Select options — mark the current detector type as selected
        var hstSelected = string.Equals(detectorNameLower, "hst", StringComparison.Ordinal) ? " selected" : "";
        var madSelected = string.Equals(detectorNameLower, "mad", StringComparison.Ordinal) ? " selected" : "";
        var stlSelected = string.Equals(detectorNameLower, "stl", StringComparison.Ordinal) ? " selected" : "";

        // Timing caption
        var timingCaption = detectorNameLower == "hst"
            ? "streaming (live, ~2 s reload)"
            : "batch (runs every N min)";

        // Build parameter grid based on detector type
        var paramGridHtml = detectorNameLower switch
        {
            "mad" => BuildMadParamGrid(entityIdx, detIdx, detector.Params),
            "stl" => BuildStlParamGrid(entityIdx, detIdx, detector.Params),
            _ => BuildHstParamGrid(entityIdx, detIdx, detector.Params)  // default to HST
        };

        var nameAttr = $"detectors[{entityIdx}][{detIdx}][name]";
        var ariaLabelSelect = $"Detector type for entity {entityIdx}";

        return $"""
            <div class="argus-detector-entry">
              <div class="argus-detector-header">
                <select class="argus-detector-select"
                        name="{nameAttr}"
                        aria-label="{ariaLabelSelect}">
                  <option value="hst"{hstSelected}>HST</option>
                  <option value="mad"{madSelected}>MAD</option>
                  <option value="stl"{stlSelected}>STL</option>
                </select>
                <span class="argus-timing-caption">{timingCaption}</span>
                <button type="button"
                        class="argus-btn argus-btn--destructive-ghost"
                        onclick="this.closest('.argus-detector-entry').remove()"
                        aria-label="Remove this detector">
                  Remove
                </button>
              </div>
              {paramGridHtml}
            </div>
            """;
    }

    /// <summary>
    /// Builds the save-success banner fragment (POST /api/sensors/save → success path).
    /// </summary>
    public static string BuildSuccessBanner(int count)
    {
        return $"""
            <div class="argus-banner argus-banner--success"
                 role="status" aria-live="polite">
              Saved — pipeline active. {count} {(count == 1 ? "entity" : "entities")} tracked.
            </div>
            """;
    }

    /// <summary>
    /// Builds the reloading-state banner fragment (Phase 3 — intermediate save state).
    /// </summary>
    public static string BuildReloadingBanner(int count)
    {
        return $"""
            <div class="argus-banner argus-banner--reloading"
                 role="status" aria-live="polite">
              Saved — pipeline reloading… ({count} {(count == 1 ? "entity" : "entities")})
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

    private static string BuildListRows(
        IReadOnlyList<HaSensorEntry> entries,
        EntitiesConfig config,
        string? q = null)
    {
        if (entries.Count == 0)
        {
            return BuildEmptyState(q);
        }

        // Build lookup for entity configs by EntityId (case-insensitive)
        var entityConfigById = config.Entities
            .ToDictionary(e => e.EntityId, StringComparer.OrdinalIgnoreCase);

        // trackedEntityIdx is the entity's 0-based position in the sorted tracked-entity list.
        // This MUST match the correlation used in the save handler (POST /api/sensors/save):
        //   sortedIds = resolvedIds.OrderBy(id => id, OrdinalIgnoreCase) — same alphabetical sort.
        // On GET, only IsTracked entries increment this counter (unchecked entries are not tracked).
        // On POST, only checked entities appear in resolvedIds and sortedIds.
        // Both sides iterate the same alphabetical order, so detectors[ei] maps to the correct entity.
        var trackedEntityIdx = 0;

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

            // Build detector disclosure section for tracked entities only
            var detectorDisclosureHtml = "";
            if (entry.IsTracked)
            {
                detectorDisclosureHtml = BuildDetectorDisclosure(
                    entry.EntityId, safeEntityId, trackedEntityIdx, entityConfigById);
                trackedEntityIdx++;
            }

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
                  </label>{detectorDisclosureHtml}
                </li>
                """);
        }
        return sb.ToString();
    }

    private static string BuildDetectorDisclosure(
        string entityId,
        string safeEntityId,
        int entityIdx,
        Dictionary<string, EntityConfig> entityConfigById)
    {
        // Get detectors from config; default to a single HST entry if not found or empty
        entityConfigById.TryGetValue(entityId, out var entityConfig);
        var detectors = entityConfig?.Detectors is { Count: > 0 }
            ? entityConfig.Detectors
            : new List<DetectorConfig> { new() { Name = "hst", Params = [] } };

        // Build detector entries
        var detectorEntriesSb = new StringBuilder();
        for (int di = 0; di < detectors.Count; di++)
        {
            detectorEntriesSb.AppendLine(BuildDetectorEntry(entityIdx, di, detectors[di]));
        }

        var summaryText = detectors.Count > 0
            ? $"Detectors ({detectors.Count})"
            : "Detectors (none)";

        // Next det_idx for the Add button = current count
        var nextDetIdx = detectors.Count;

        var hxTarget = $".argus-add-detector-row[data-entity-idx='{entityIdx}']";
        return $"""

                  <details class="argus-detectors-details">
                    <summary class="argus-disclosure-toggle">{summaryText}</summary>
                    <div class="argus-detectors-panel">
                      {detectorEntriesSb.ToString().TrimEnd()}
                      <div class="argus-add-detector-row" data-entity-idx="{entityIdx}">
                        <button type="button"
                                class="argus-btn argus-btn--add-detector"
                                data-entity-id="{safeEntityId}"
                                hx-get="api/detectors/new-entry?entity_idx={entityIdx}&amp;det_idx={nextDetIdx}"
                                hx-target="{hxTarget}"
                                hx-swap="beforebegin"
                                hx-indicator="#argus-spinner"
                                hx-push-url="false"
                                aria-label="Add detector to {safeEntityId}">
                          + Add detector
                        </button>
                      </div>
                    </div>
                  </details>
                  """;
    }

    private static string BuildHstParamGrid(
        int entityIdx, int detIdx, Dictionary<string, string> storedParams)
    {
        var window = GetParam(storedParams, "window", HstWindowDefault);
        var nTrees = GetParam(storedParams, "n_trees", HstNTreesDefault);
        var highThreshold = GetParam(storedParams, "high_threshold", HstHighThresholdDefault);
        var lowThreshold = GetParam(storedParams, "low_threshold", HstLowThresholdDefault);
        var minConsecutive = GetParam(storedParams, "min_consecutive", HstMinConsecutiveDefault);
        var frozenWindow = GetParam(storedParams, "frozen_window", HstFrozenWindowDefault);
        var frozenVariance = GetParam(storedParams, "frozen_variance_threshold", HstFrozenVarianceThresholdDefault);

        return $"""
            <div class="argus-param-grid">
              <div class="argus-param-field">
                <label class="argus-param-field__label" for="p_{entityIdx}_{detIdx}_window">window</label>
                <input class="argus-param-field__input" type="number" id="p_{entityIdx}_{detIdx}_window"
                       name="detectors[{entityIdx}][{detIdx}][params][window]" value="{window}">
              </div>
              <div class="argus-param-field">
                <label class="argus-param-field__label" for="p_{entityIdx}_{detIdx}_n_trees">n_trees</label>
                <input class="argus-param-field__input" type="number" id="p_{entityIdx}_{detIdx}_n_trees"
                       name="detectors[{entityIdx}][{detIdx}][params][n_trees]" value="{nTrees}">
              </div>
              <div class="argus-param-field">
                <label class="argus-param-field__label" for="p_{entityIdx}_{detIdx}_high_threshold">high_threshold</label>
                <input class="argus-param-field__input" type="number" step="0.01" id="p_{entityIdx}_{detIdx}_high_threshold"
                       name="detectors[{entityIdx}][{detIdx}][params][high_threshold]" value="{highThreshold}">
              </div>
              <div class="argus-param-field">
                <label class="argus-param-field__label" for="p_{entityIdx}_{detIdx}_low_threshold">low_threshold</label>
                <input class="argus-param-field__input" type="number" step="0.01" id="p_{entityIdx}_{detIdx}_low_threshold"
                       name="detectors[{entityIdx}][{detIdx}][params][low_threshold]" value="{lowThreshold}">
              </div>
              <div class="argus-param-field">
                <label class="argus-param-field__label" for="p_{entityIdx}_{detIdx}_min_consecutive">min_consecutive</label>
                <input class="argus-param-field__input" type="number" id="p_{entityIdx}_{detIdx}_min_consecutive"
                       name="detectors[{entityIdx}][{detIdx}][params][min_consecutive]" value="{minConsecutive}">
              </div>
              <div class="argus-param-field">
                <label class="argus-param-field__label" for="p_{entityIdx}_{detIdx}_frozen_window">frozen_window</label>
                <input class="argus-param-field__input" type="number" id="p_{entityIdx}_{detIdx}_frozen_window"
                       name="detectors[{entityIdx}][{detIdx}][params][frozen_window]" value="{frozenWindow}">
              </div>
              <div class="argus-param-field argus-param-grid--span2">
                <label class="argus-param-field__label" for="p_{entityIdx}_{detIdx}_frozen_variance_threshold">frozen_variance</label>
                <input class="argus-param-field__input" type="number" step="0.0001" id="p_{entityIdx}_{detIdx}_frozen_variance_threshold"
                       name="detectors[{entityIdx}][{detIdx}][params][frozen_variance_threshold]" value="{frozenVariance}">
              </div>
            </div>
            """;
    }

    private static string BuildMadParamGrid(
        int entityIdx, int detIdx, Dictionary<string, string> storedParams)
    {
        var threshold = GetParam(storedParams, "threshold", MadThresholdDefault);
        var window = GetParam(storedParams, "window", MadWindowDefault);

        return $"""
            <div class="argus-param-grid">
              <div class="argus-param-field">
                <label class="argus-param-field__label" for="p_{entityIdx}_{detIdx}_threshold">threshold</label>
                <input class="argus-param-field__input" type="number" step="0.1" id="p_{entityIdx}_{detIdx}_threshold"
                       name="detectors[{entityIdx}][{detIdx}][params][threshold]" value="{threshold}">
              </div>
              <div class="argus-param-field">
                <label class="argus-param-field__label" for="p_{entityIdx}_{detIdx}_window">window</label>
                <input class="argus-param-field__input" type="number" id="p_{entityIdx}_{detIdx}_window"
                       name="detectors[{entityIdx}][{detIdx}][params][window]" value="{window}">
              </div>
            </div>
            """;
    }

    private static string BuildStlParamGrid(
        int entityIdx, int detIdx, Dictionary<string, string> storedParams)
    {
        var period = GetParam(storedParams, "period", StlPeriodDefault);
        var seasonal = GetParam(storedParams, "seasonal", StlSeasonalDefault);
        var threshold = GetParam(storedParams, "threshold", StlThresholdDefault);

        return $"""
            <div class="argus-param-grid">
              <div class="argus-param-field">
                <label class="argus-param-field__label" for="p_{entityIdx}_{detIdx}_period">period</label>
                <input class="argus-param-field__input" type="number" id="p_{entityIdx}_{detIdx}_period"
                       name="detectors[{entityIdx}][{detIdx}][params][period]" value="{period}">
              </div>
              <div class="argus-param-field">
                <label class="argus-param-field__label" for="p_{entityIdx}_{detIdx}_seasonal">seasonal</label>
                <input class="argus-param-field__input" type="number" id="p_{entityIdx}_{detIdx}_seasonal"
                       name="detectors[{entityIdx}][{detIdx}][params][seasonal]" value="{seasonal}">
              </div>
              <div class="argus-param-field argus-param-grid--span2">
                <label class="argus-param-field__label" for="p_{entityIdx}_{detIdx}_threshold">threshold</label>
                <input class="argus-param-field__input" type="number" step="0.1" id="p_{entityIdx}_{detIdx}_threshold"
                       name="detectors[{entityIdx}][{detIdx}][params][threshold]" value="{threshold}">
              </div>
            </div>
            """;
    }

    /// <summary>
    /// Returns stored param value (HTML-encoded) or the type-specific default.
    /// T-03-11: param values are HTML-encoded to prevent stored XSS.
    /// </summary>
    private static string GetParam(Dictionary<string, string> storedParams, string key, string defaultValue)
    {
        if (storedParams.TryGetValue(key, out var stored) && !string.IsNullOrEmpty(stored))
            return WebUtility.HtmlEncode(stored);
        return WebUtility.HtmlEncode(defaultValue);
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
