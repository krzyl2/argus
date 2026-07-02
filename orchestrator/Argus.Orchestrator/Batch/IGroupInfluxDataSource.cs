namespace Argus.Orchestrator.Batch;

/// <summary>
/// One aligned row of a group's time-matrix: a shared timestamp plus each member's
/// value at that timestamp. A null member value means the pivot cell was empty for
/// that window (genuine gap) — never a forward-filled value (GRP-02).
/// </summary>
public sealed record GroupRow(DateTime Timestamp, IReadOnlyDictionary<string, double?> MemberValues);

/// <summary>
/// Result of a group time-alignment query: the aligned matrix rows plus each member's
/// last-seen (most recent raw point) UTC timestamp, used by the caller to apply the
/// wall-clock staleness_cap exclusion policy (Plan 06-04). This type does not itself
/// decide which rows/members are excluded.
/// </summary>
public sealed record GroupAlignedData(
    IReadOnlyList<GroupRow> Rows,
    IReadOnlyDictionary<string, DateTime> LastSeenUtc);

/// <summary>
/// Abstraction over GroupInfluxReader for batch scheduler testability.
/// Implemented by GroupInfluxReader (production) and hand-written fakes in tests.
/// </summary>
public interface IGroupInfluxDataSource
{
    Task<GroupAlignedData> QueryGroupAsync(
        IReadOnlyList<string> members,
        string every,
        string aggFn,
        TimeSpan stalenessCap,
        CancellationToken ct);
}
