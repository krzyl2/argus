namespace Argus.Orchestrator.Web;

/// <summary>POST /api/sensors/{entityId}/simulate request body.</summary>
/// <param name="Detector">Detector name to replay with. Empty falls back to the default.</param>
/// <param name="Params">Params exactly as they would be written to entities.yaml — including
/// values the operator has typed but not saved, which is the case the panel exists for.</param>
/// <param name="Lookback">Flux/Recorder duration literal. Null means the B5 default, 24h.</param>
/// <param name="MaxPoints">Points to replay; clamped, never trusted.</param>
public sealed record SimulateRequestDto(
    string? Detector,
    Dictionary<string, string>? Params,
    string? Lookback,
    int? MaxPoints);

/// <summary>One ON run of the replayed gate, as indices into Scores/Values/Timestamps.</summary>
public sealed record ReplayEpisodeDto(int StartIndex, int EndIndex);

/// <summary>The gate reduction, as the panel's number header renders it.</summary>
/// <param name="EpisodeSpans">The runs behind <paramref name="Episodes"/>. On the wire because
/// the chart's shaded bands are drawn from them: a panel that re-derived episodes client-side
/// would be re-implementing the gate a third time, and would disagree with the count printed
/// beside it whenever the raw channel — which the client cannot see — carried the decision.</param>
/// <param name="CalibratedFromIndex">First index at which the score channel was calibrated;
/// before it only the raw channel could fire. The panel marks that region.</param>
public sealed record SimulateSummaryDto(
    int Episodes,
    double OnTimePercent,
    double SpanHours,
    double AlertsPerDay,
    int ScorablePoints,
    int Transitions,
    IReadOnlyList<ReplayEpisodeDto> EpisodeSpans,
    int CalibratedFromIndex);

/// <summary>
/// Full response payload. D-07 allowlist boundary: an explicit record, never the raw
/// SimulateResult — the detector-side record carries a version string and a robust-z array
/// that no screen reads, and a projection is where that decision stays visible.
/// </summary>
public sealed record SimulateResponseDto(
    bool Ok,
    string? Error,
    SimulateSummaryDto? Summary,
    IReadOnlyList<double> Scores,
    IReadOnlyList<double> Values,
    IReadOnlyList<DateTimeOffset> Timestamps,
    int WarmedUpFromIndex,
    int Window);

/// <summary>Status code plus payload, so the handler is testable without an HTTP server —
/// the convention every other endpoint test in this project follows.</summary>
public sealed record SimulateOutcome(int StatusCode, object? Payload);

/// <summary>
/// DI-visible holder for an OPTIONAL simulator. The container cannot hold a null service, and
/// registering the service bare would turn "no history source configured" into a 500 at
/// request time instead of the documented 503. Same intent as ScoreStreamPipeline's nullable
/// constructor dependencies (Program.cs:207-214), expressed as a type because a minimal-API
/// handler parameter has no GetService escape hatch.
/// </summary>
public sealed record SimulateServiceHandle(ISimulateService? Service);

/// <summary>
/// Request handling for POST /api/sensors/{entityId}/simulate, extracted from Program.cs so
/// the status-code decisions are unit-testable (no WebApplicationFactory anywhere in this
/// test project — see SensorsEndpointJsonTests/SettingsEndpointTests).
/// </summary>
public static class SimulateEndpoint
{
    public static async Task<SimulateOutcome> HandleAsync(
        bool authorized,
        string entityId,
        SimulateRequestDto? body,
        ISimulateService? service,
        Func<string, bool> entityKnown,
        CancellationToken ct)
    {
        // Same TCP-peer guard as every other endpoint (Program.cs:323). §7 #16 records that
        // this is the ONLY authentication in front of a CPU lever; the clamp below is the
        // other half of that defence.
        if (!authorized) return new SimulateOutcome(403, null);

        if (string.IsNullOrWhiteSpace(entityId) || !entityKnown(entityId))
            return new SimulateOutcome(404, null);

        var lookback = string.IsNullOrWhiteSpace(body?.Lookback)
            ? SimulateService.DefaultLookback
            : body!.Lookback!;

        // Rejected here rather than in the history source: InfluxDbReader THROWS on a bad
        // duration shape, so without this an operator typo is a 500 next to a healthy system.
        if (!SimulateService.IsValidLookback(lookback))
            return new SimulateOutcome(400, null);

        // No history source configured at all (influx_url empty AND no Recorder source):
        // the panel says "no history source", it does not pretend to have simulated nothing.
        if (service is null) return new SimulateOutcome(503, null);

        var detector = string.IsNullOrWhiteSpace(body?.Detector) ? "rmad" : body!.Detector!;
        var parameters = body?.Params ?? new Dictionary<string, string>();
        var maxPoints = SimulateService.ClampMaxPoints(body?.MaxPoints);

        var result = await service
            .RunAsync(entityId, detector, parameters, lookback, maxPoints, ct)
            .ConfigureAwait(false);

        return new SimulateOutcome(200, Project(result));
    }

    public static SimulateResponseDto Project(SimulateRunResult result)
        => new(
            result.Ok,
            result.Error,
            result.Ok
                ? new SimulateSummaryDto(
                    result.Summary.Episodes,
                    result.Summary.OnTimePercent,
                    result.Summary.SpanHours,
                    result.Summary.AlertsPerDay,
                    result.Summary.ScorablePoints,
                    result.Summary.Transitions,
                    (result.Summary.EpisodeSpans ?? [])
                        .Select(e => new ReplayEpisodeDto(e.StartIndex, e.EndIndexExclusive))
                        .ToList(),
                    result.Summary.CalibratedFromIndex)
                : null,
            result.Scores,
            result.Values,
            result.Timestamps,
            result.WarmedUpFromIndex,
            result.Window);
}
