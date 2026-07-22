using Argus.Orchestrator.Config;
using Argus.Orchestrator.Detection;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for EntityRuntimeState's public warm-up getters (QUICK-warmup-status).
/// RecordReading/WarmedUp semantics are unchanged — these tests only prove the newly
/// exposed ReadingCount/WarmUpWindow getters track those existing private fields correctly.
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
    public void ReadingCount_IncrementsByOnePerRecordReading()
    {
        var state = new EntityRuntimeState(new HstParams { Window = 3 });

        state.RecordReading();
        Assert.Equal(1, state.ReadingCount);

        state.RecordReading();
        Assert.Equal(2, state.ReadingCount);
    }

    [Fact]
    public void WarmedUp_FlipsToTrueExactlyWhenReadingCountReachesWindow()
    {
        var state = new EntityRuntimeState(new HstParams { Window = 3 });

        state.RecordReading();
        Assert.False(state.WarmedUp);
        Assert.Equal(1, state.ReadingCount);

        state.RecordReading();
        Assert.False(state.WarmedUp);
        Assert.Equal(2, state.ReadingCount);

        state.RecordReading();
        Assert.True(state.WarmedUp);
        Assert.Equal(3, state.ReadingCount);
    }
}
