namespace Argus.Orchestrator.Batch;

/// <summary>
/// In-memory last-batch-run timestamp tracker backing the Dashboard's "Last batch run"
/// health component (QUICK-dashboard-real-data). Single writer (BatchSchedulerWorker,
/// end of each RunBatchAsync cycle), many readers (Kestrel) — a 64-bit field cannot be
/// marked volatile, so cross-thread visibility is provided via Interlocked instead.
/// </summary>
public interface IBatchRunStatus
{
    /// <summary>The UTC time of the last completed batch run, or null if the worker has never run.</summary>
    DateTimeOffset? LastRunUtc { get; }

    /// <summary>Records the completion time of a batch run.</summary>
    void MarkRun(DateTimeOffset utc);
}

/// <inheritdoc cref="IBatchRunStatus"/>
public sealed class BatchRunStatus : IBatchRunStatus
{
    private long _lastRunUtcTicks;

    /// <inheritdoc/>
    public DateTimeOffset? LastRunUtc
    {
        get
        {
            var ticks = System.Threading.Interlocked.Read(ref _lastRunUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <inheritdoc/>
    public void MarkRun(DateTimeOffset utc) =>
        System.Threading.Interlocked.Exchange(ref _lastRunUtcTicks, utc.UtcTicks);
}
