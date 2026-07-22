namespace Argus.Orchestrator.Detection;

/// <summary>
/// One recorded anomaly event backing the Dashboard's "Recent anomalies" card
/// (QUICK-dashboard-real-data). Exactly one of EntityId/GroupId is non-null:
/// EntityId for a single-sensor (streaming) anomaly, GroupId for a joint-group
/// (batch) anomaly.
/// </summary>
public sealed record RecentAnomaly(
    string? EntityId,
    string? GroupId,
    double Score,
    string Detector,
    DateTimeOffset DetectedAtUtc);

/// <summary>
/// In-memory bounded ring buffer of recent anomaly events backing
/// GET /api/anomalies/recent (QUICK-dashboard-real-data). Single writers are the
/// streaming pipeline (ScoreStreamPipeline) and the batch worker
/// (BatchSchedulerWorker); readers are Kestrel threads. A plain lock is used
/// (not ConcurrentDictionary) because ordering + fixed capacity are required.
/// </summary>
public interface IRecentAnomaliesCache
{
    /// <summary>Records an anomaly event, evicting the oldest entry if the buffer is full.</summary>
    void Record(RecentAnomaly anomaly);

    /// <summary>Returns a point-in-time snapshot of recorded anomalies, newest-first.</summary>
    IReadOnlyList<RecentAnomaly> GetRecent();
}

/// <inheritdoc cref="IRecentAnomaliesCache"/>
public sealed class RecentAnomaliesCache : IRecentAnomaliesCache
{
    private const int Capacity = 20;

    private readonly LinkedList<RecentAnomaly> _entries = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public void Record(RecentAnomaly anomaly)
    {
        lock (_lock)
        {
            _entries.AddFirst(anomaly);
            while (_entries.Count > Capacity)
                _entries.RemoveLast();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<RecentAnomaly> GetRecent()
    {
        lock (_lock)
        {
            return new List<RecentAnomaly>(_entries);
        }
    }
}
