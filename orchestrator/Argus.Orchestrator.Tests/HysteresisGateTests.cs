using Argus.Orchestrator.Detection;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for HysteresisGate anti-flap state machine (STRM-05, D-11).
/// Default: high=0.7, low=0.3, min_consecutive=3.
/// </summary>
public class HysteresisGateTests
{
    // ─── Anti-flap: N-consecutive required to flip ───────────────────────────

    [Fact]
    public void TwoHighScores_DoNotFlipOn()
    {
        // Starts OFF; two scores above high threshold — should remain OFF (need 3)
        var gate = new HysteresisGate();
        gate.Apply(0.8);
        gate.Apply(0.8);
        Assert.False(gate.IsAnomalous);
    }

    [Fact]
    public void ThreeHighScores_FlipsOn()
    {
        var gate = new HysteresisGate();
        gate.Apply(0.8);
        gate.Apply(0.8);
        gate.Apply(0.8);
        Assert.True(gate.IsAnomalous);
    }

    [Fact]
    public void TwoLowScores_DoNotFlipOff_WhenOn()
    {
        var gate = new HysteresisGate();
        // Flip ON
        gate.Apply(0.8); gate.Apply(0.8); gate.Apply(0.8);
        // Only 2 lows — should stay ON
        gate.Apply(0.2);
        gate.Apply(0.2);
        Assert.True(gate.IsAnomalous);
    }

    [Fact]
    public void ThreeLowScores_FlipsOff_WhenOn()
    {
        var gate = new HysteresisGate();
        gate.Apply(0.8); gate.Apply(0.8); gate.Apply(0.8);
        gate.Apply(0.2); gate.Apply(0.2); gate.Apply(0.2);
        Assert.False(gate.IsAnomalous);
    }

    [Fact]
    public void SingleSpike_DoesNotFlipOn()
    {
        // Single high score (PITFALL 7 — anti-flap)
        var gate = new HysteresisGate();
        gate.Apply(0.9);
        Assert.False(gate.IsAnomalous);
    }

    // ─── Dead zone resets both counters ──────────────────────────────────────

    [Fact]
    public void DeadZoneScore_ResetsCounters_And_HoldsCurrentState_WhenOff()
    {
        var gate = new HysteresisGate();
        // Two highs, then dead zone interrupts — should NOT flip with next high pair
        gate.Apply(0.8);
        gate.Apply(0.8);
        gate.Apply(0.5); // dead zone — resets high counter
        gate.Apply(0.8);
        gate.Apply(0.8);
        // Total consecutive highs after reset = 2, still OFF
        Assert.False(gate.IsAnomalous);
    }

    [Fact]
    public void DeadZoneScore_HoldsCurrentState_WhenOn()
    {
        var gate = new HysteresisGate();
        gate.Apply(0.8); gate.Apply(0.8); gate.Apply(0.8); // ON
        gate.Apply(0.5); // dead zone — stays ON
        Assert.True(gate.IsAnomalous);
    }

    [Fact]
    public void DeadZoneScore_ResetsLowCounter_WhenOn()
    {
        var gate = new HysteresisGate();
        gate.Apply(0.8); gate.Apply(0.8); gate.Apply(0.8); // ON
        gate.Apply(0.2); gate.Apply(0.2);
        gate.Apply(0.5); // dead zone — resets low counter
        gate.Apply(0.2); gate.Apply(0.2);
        // Low counter after reset = 2, still ON
        Assert.True(gate.IsAnomalous);
    }

    // ─── Custom thresholds via constructor ───────────────────────────────────

    [Fact]
    public void CustomMinConsecutive_Two_FlipsOnAfterTwo()
    {
        var gate = new HysteresisGate(highThreshold: 0.7, lowThreshold: 0.3, minConsecutive: 2);
        gate.Apply(0.8);
        gate.Apply(0.8);
        Assert.True(gate.IsAnomalous);
    }

    [Fact]
    public void StartsOff()
    {
        var gate = new HysteresisGate();
        Assert.False(gate.IsAnomalous);
    }
}
