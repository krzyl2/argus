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

    // ─── rmad entities (D-A/D-B/D-M) ─────────────────────────────────────────

    /// <summary>
    /// D-M: an rmad entity's warm-up denominator is min_samples, not the baseline window.
    /// This is what the operator reads as "Rozgrzewka n/N". Seeding it from the 720-sample
    /// baseline instead would tell them to wait ~78 h on a 391 s/sample sensor for a verdict
    /// the detector already emits at 60 — the rolling median/MAD IS the calibration.
    /// </summary>
    [Fact]
    public void WarmUpWindow_RmadParams_SeededFromMinSamples_NotBaselineWindow()
    {
        var state = new EntityRuntimeState(new RmadParams());

        Assert.Equal(60, state.WarmUpWindow);
    }

    /// <summary>
    /// The gate is shared with the hst path unchanged (D-C) — only the numbers differ — so an
    /// rmad entity must arrive with ITS thresholds in the gate, not the 0.7/0.3 hst defaults.
    /// A silent fallback here is exactly how a migrated config would keep running F0.
    /// </summary>
    [Fact]
    public void RmadParams_ConfigureTheGateAndFrozenDetector()
    {
        var rmad = new RmadParams { HighThreshold = 0.5, LowThreshold = 0.375, MinConsecutive = 3 };
        var state = new EntityRuntimeState(rmad);

        Assert.Equal("rmad", state.DetectorName);
        Assert.Same(rmad, state.RmadParams);

        // Three consecutive scores above 0.5 fire; the hst default (0.7) would not have.
        Assert.False(state.Hysteresis.Apply(0.6));
        Assert.False(state.Hysteresis.Apply(0.6));
        Assert.True(state.Hysteresis.Apply(0.6));

        // frozen_variance_threshold 0.0 can never latch — sample variance is never negative.
        for (int i = 0; i < 20; i++)
            state.FrozenDetector.AddReading(0.0);
        Assert.False(state.FrozenDetector.IsFrozen);
    }

    [Fact]
    public void DetectorName_HstConstructor_IsHst()
    {
        Assert.Equal("hst", new EntityRuntimeState(new HstParams()).DetectorName);
        Assert.Null(new EntityRuntimeState(new HstParams()).RmadParams);
    }
}
