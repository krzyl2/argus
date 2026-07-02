namespace Argus.Orchestrator.Web;

/// <summary>
/// JSON request body DTO for POST /api/groups/save (GRP-09/ALGO-01..04/08-02 SPA).
///
/// Natural nested shape — MUST match orchestrator/ui/src/api/types.ts's group save request
/// interface exactly. Deserialized via ReadFromJsonAsync&lt;GroupSaveRequest&gt; using the
/// default System.Text.Json camelCase naming policy (groupId, friendlyName, members, mode,
/// detector, params all map automatically — no [JsonPropertyName] needed).
/// </summary>
public class GroupSaveRequest
{
    public List<GroupSaveEntry> Groups { get; set; } = new();
}

public class GroupSaveEntry
{
    public string GroupId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public List<string> Members { get; set; } = new();

    /// <summary>"peer_divergence" | "joint"</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>"peer_divergence" | "ecod" | "copod" | "pca" | "iforest"</summary>
    public string Detector { get; set; } = string.Empty;
    public Dictionary<string, string> Params { get; set; } = new();
}
