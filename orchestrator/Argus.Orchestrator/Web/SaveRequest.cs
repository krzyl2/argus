namespace Argus.Orchestrator.Web;

/// <summary>
/// JSON request body DTO for POST /api/sensors/save (UI-03/UI-04 — Phase 7 SPA migration).
///
/// Natural nested-array shape — MUST match orchestrator/ui/src/api/types.ts's SaveRequest
/// interface exactly (locked in Plan 07-01). Deserialized via ReadFromJsonAsync&lt;SaveRequest&gt;
/// using the default System.Text.Json camelCase naming policy (entityId, detectors, name,
/// params, include, exclude, entities all map automatically — no [JsonPropertyName] needed).
/// </summary>
public class SaveRequest
{
    public List<SaveEntity> Entities { get; set; } = new();

    /// <summary>Raw newline-separated include-pattern textarea content (matches types.ts's string field).</summary>
    public string Include { get; set; } = string.Empty;

    /// <summary>Raw newline-separated exclude-pattern textarea content (matches types.ts's string field).</summary>
    public string Exclude { get; set; } = string.Empty;
}

public class SaveEntity
{
    public string EntityId { get; set; } = string.Empty;
    public List<SaveDetector> Detectors { get; set; } = new();
}

public class SaveDetector
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Params { get; set; } = new();
}
