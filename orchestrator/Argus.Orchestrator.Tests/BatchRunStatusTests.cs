using Argus.Orchestrator.Batch;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for BatchRunStatus — the in-memory last-batch-run timestamp tracker backing the
/// Dashboard's "Last batch run" health component (QUICK-dashboard-real-data).
/// Fully offline, no DI required.
/// </summary>
public class BatchRunStatusTests
{
    [Fact]
    public void LastRunUtc_BeforeAnyMarkRun_IsNull()
    {
        var status = new BatchRunStatus();

        Assert.Null(status.LastRunUtc);
    }

    [Fact]
    public void LastRunUtc_AfterMarkRun_EqualsMarkedTime()
    {
        var status = new BatchRunStatus();
        var t = DateTimeOffset.UtcNow;

        status.MarkRun(t);

        Assert.NotNull(status.LastRunUtc);
        Assert.Equal(t.UtcTicks, status.LastRunUtc!.Value.UtcTicks);
    }

    [Fact]
    public void MarkRun_Later_ReplacesEarlierValue()
    {
        var status = new BatchRunStatus();
        var first = DateTimeOffset.UtcNow.AddMinutes(-10);
        var second = DateTimeOffset.UtcNow;

        status.MarkRun(first);
        status.MarkRun(second);

        Assert.Equal(second.UtcTicks, status.LastRunUtc!.Value.UtcTicks);
    }
}
