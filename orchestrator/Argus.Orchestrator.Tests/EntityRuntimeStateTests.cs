using Argus.Orchestrator.Config;
using Argus.Orchestrator.Detection;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for EntityRuntimeState's warm-up getters (D-01/WARM-01, Phase 15-02).
/// Warm-up is detector-owned: ApplyVerdictWarmup is the only way WarmedUp/ReadingCount/
/// WarmUpWindow change value — there is no more self-incrementing RecordReading counter.
/// </summary>
public class EntityRuntimeStateTests
{
    [Fact]
    public void WarmUpWindow_ReflectsConfiguredWindow()
    {
        var state = new EntityRuntimeState(new HstParams { Window = 3 });

        Assert.Equal(3, state.WarmUpWindow);
    }

    [Fact]
    public void WarmUpWindow_DefaultHstParams_Is250()
    {
        var state = new EntityRuntimeState(new HstParams());

        Assert.Equal(250, state.WarmUpWindow);
    }

    [Fact]
    public void ReadingCount_StartsAtZero()
    {
        var state = new EntityRuntimeState(new HstParams { Window = 3 });

        Assert.Equal(0, state.ReadingCount);
    }

    [Fact]
    public void FreshState_ReportsSeededWindow_ZeroCount_NotWarmedUp()
    {
        // GET /api/sensors must be able to render 0/50 before any verdict has arrived.
        var state = new EntityRuntimeState(new HstParams { Window = 50 });

        Assert.Equal(50, state.WarmUpWindow);
        Assert.Equal(0, state.ReadingCount);
        Assert.False(state.WarmedUp);
    }

    [Fact]
    public void ApplyVerdictWarmup_SetsAllThreeGetters()
    {
        var state = new EntityRuntimeState(new HstParams { Window = 250 });

        state.ApplyVerdictWarmup(true, 250, 250);

        Assert.True(state.WarmedUp);
        Assert.Equal(250, state.ReadingCount);
        Assert.Equal(250, state.WarmUpWindow);
    }

    [Fact]
    public void ApplyVerdictWarmup_ZeroWindow_DoesNotOverwriteConfiguredSeed()
    {
        // (false, 0, 0) is the tuple a detector returns for an entity it has no
        // entry for yet — WarmUpWindow must keep the constructor-seeded value,
        // not blank out to 0 (which would render a zero denominator in the UI).
        var state = new EntityRuntimeState(new HstParams { Window = 250 });

        state.ApplyVerdictWarmup(false, 0, 0);

        Assert.False(state.WarmedUp);
        Assert.Equal(0, state.ReadingCount);
        Assert.Equal(250, state.WarmUpWindow);
    }

    [Fact]
    public void ApplyVerdictWarmup_CalledRepeatedly_LatestVerdictWins()
    {
        // D2 fix: no local counter — each call reflects only what the latest
        // verdict reported, exactly as multiple verdicts arrive over a stream.
        var state = new EntityRuntimeState(new HstParams { Window = 3 });

        state.ApplyVerdictWarmup(false, 1, 3);
        Assert.False(state.WarmedUp);
        Assert.Equal(1, state.ReadingCount);

        state.ApplyVerdictWarmup(false, 2, 3);
        Assert.False(state.WarmedUp);
        Assert.Equal(2, state.ReadingCount);

        state.ApplyVerdictWarmup(true, 3, 3);
        Assert.True(state.WarmedUp);
        Assert.Equal(3, state.ReadingCount);
    }

    [Fact]
    public void HstParams_ExposesConstructorValue()
    {
        var hstParams = new HstParams { Window = 42, NTrees = 7 };
        var state = new EntityRuntimeState(hstParams);

        Assert.Same(hstParams, state.HstParams);
    }
}
