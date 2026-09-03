using Argus.Orchestrator.Config;
using Argus.Orchestrator.Detection;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for AlertStateStore — the reason per-entity calibration survives a config reload.
/// </summary>
public class AlertStateStoreTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow;

    [Fact]
    public void GetOrCreate_SameParamsTwice_ReturnsSamePolicyWithAccumulatedSamples()
    {
        // WHY the store exists: HaListenerWorker rebuilds every EntityRuntimeState on every
        // config Save, and the SPA saves the whole tracked list from any screen — including the
        // pattern textareas in Settings. If the policy were rebuilt with the state, one
        // unrelated Save would restart calibration everywhere. On zamrazarkapiwnica_power
        // (~225 verdicts a day against alert_min_samples=240) that is roughly 26 hours of
        // silence bought by an unrelated click.
        var store = new AlertStateStore();
        var p = new AlertParams();

        var first = store.GetOrCreate("sensor.a", p);
        for (int i = 0; i < 10; i++)
            first.OnVerdict(0.5, true, false, false, T0.AddSeconds(i));

        var second = store.GetOrCreate("sensor.a", p);

        Assert.Same(first, second);
        Assert.Equal(10, second.SampleCount);
    }

    [Fact]
    public void GetOrCreate_ChangedParams_ReturnsFreshPolicy()
    {
        // A changed window or threshold makes the accumulated state incomparable, so the policy
        // is rebuilt rather than reinterpreted. The cost is one republished flag value (the new
        // policy's LastPublishedFlag is null), which is why this is a deliberate branch and not
        // an accident of dictionary lookup.
        var store = new AlertStateStore();
        var first = store.GetOrCreate("sensor.a", new AlertParams());
        first.OnVerdict(0.5, true, false, false, T0);

        var second = store.GetOrCreate("sensor.a", new AlertParams { QFire = 0.95 });

        Assert.NotSame(first, second);
        Assert.Equal(0, second.SampleCount);
        Assert.Null(second.LastPublishedFlag);
    }

    [Fact]
    public void GetOrCreate_EquivalentParamsRecord_ReusesPolicy()
    {
        // Reuse keys on record VALUE equality, not on reference identity — BuildEntityStates
        // constructs a fresh AlertParams from the params map on every rebuild, so a
        // reference-keyed store would never hit.
        var store = new AlertStateStore();
        var first = store.GetOrCreate("sensor.a", AlertParams.From(new Dictionary<string, string>()));
        var second = store.GetOrCreate("sensor.a", AlertParams.From(new Dictionary<string, string>()));

        Assert.Same(first, second);
    }

    [Fact]
    public void PruneTo_RemovesUntrackedEntities()
    {
        // Untracking a sensor must actually release its state; without pruning the store grows
        // for the lifetime of the process and a re-tracked sensor would silently resume a stale
        // window instead of recalibrating.
        var store = new AlertStateStore();
        var kept = store.GetOrCreate("sensor.keep", new AlertParams());
        var dropped = store.GetOrCreate("sensor.drop", new AlertParams());
        dropped.OnVerdict(0.5, true, false, false, T0);

        store.PruneTo(new[] { "sensor.keep" });

        Assert.Same(kept, store.GetOrCreate("sensor.keep", new AlertParams()));
        var reAdded = store.GetOrCreate("sensor.drop", new AlertParams());
        Assert.NotSame(dropped, reAdded);
        Assert.Equal(0, reAdded.SampleCount);
    }
}
