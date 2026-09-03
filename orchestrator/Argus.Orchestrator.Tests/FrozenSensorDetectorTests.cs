using Argus.Orchestrator.Detection;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for FrozenSensorDetector rule-based variance check (FAULT-02, D-12).
/// Default: window=10, variance_threshold=0.001.
/// </summary>
public class FrozenSensorDetectorTests
{
    // ─── Zero-variance (identical values) ────────────────────────────────────

    [Fact]
    public void TenIdenticalReadings_ReportsFrozen()
    {
        var det = new FrozenSensorDetector();
        for (int i = 0; i < 10; i++)
            det.AddReading(20.0);
        Assert.True(det.IsFrozen);
    }

    [Fact]
    public void NineIdenticalReadings_NotFrozen_WindowNotFull()
    {
        var det = new FrozenSensorDetector();
        for (int i = 0; i < 9; i++)
            det.AddReading(20.0);
        Assert.False(det.IsFrozen);
    }

    // ─── Non-zero variance ────────────────────────────────────────────────────

    [Fact]
    public void HighVarianceReadings_NotFrozen()
    {
        var det = new FrozenSensorDetector();
        for (int i = 0; i < 10; i++)
            det.AddReading(i * 2.0); // variance >> 0.001
        Assert.False(det.IsFrozen);
    }

    [Fact]
    public void VarianceAtThreshold_NotFrozen()
    {
        // variance == 0.001 → NOT frozen (strictly less than required)
        var det = new FrozenSensorDetector();
        // Feed 10 readings with variance exactly 0.001
        // variance of [0, 0, ..., 0, x] ≈ x²/10 → x² = 0.01 → x ≈ 0.1
        // For simplicity use two distinct values to achieve variance ~0.001
        // 9 readings at 0.0, 1 reading at 0.1 → sample variance = n/(n-1)*pop_var
        // Just feed values known to have variance >= 0.001
        for (int i = 0; i < 10; i++)
            det.AddReading(i % 2 == 0 ? 0.0 : 0.1);
        Assert.False(det.IsFrozen);
    }

    // ─── Sliding window ───────────────────────────────────────────────────────

    [Fact]
    public void SlidingWindow_VariedThenFrozen_BecomesTrue()
    {
        var det = new FrozenSensorDetector();
        // Fill with varied data
        for (int i = 0; i < 10; i++)
            det.AddReading(i * 5.0);
        // Now slide in 10 identical values
        for (int i = 0; i < 10; i++)
            det.AddReading(25.0);
        Assert.True(det.IsFrozen);
    }

    [Fact]
    public void SlidingWindow_FrozenThenVaried_BecomesFalse()
    {
        var det = new FrozenSensorDetector();
        // Freeze first
        for (int i = 0; i < 10; i++)
            det.AddReading(20.0);
        Assert.True(det.IsFrozen);
        // Slide in varied data
        for (int i = 0; i < 10; i++)
            det.AddReading(i * 3.0);
        Assert.False(det.IsFrozen);
    }

    // ─── Custom params ────────────────────────────────────────────────────────

    [Fact]
    public void CustomWindow5_FiveIdentical_ReportsFrozen()
    {
        var det = new FrozenSensorDetector(window: 5, varianceThreshold: 0.001);
        for (int i = 0; i < 5; i++)
            det.AddReading(100.0);
        Assert.True(det.IsFrozen);
    }

    /// <summary>
    /// D-H, load-bearing: this is WHY the schema-2 migration disables frozen detection through
    /// frozen_variance_threshold: "0.0" and carries frozen_window over VERBATIM, never writing
    /// "0". With window 0 the count check (0 >= 0) passes on the very first reading and dequeues
    /// an empty queue. ScoreStreamPipeline calls AddReading unconditionally for every reading,
    /// so a migrated "0" would take the entity's stream down on its first live value.
    /// </summary>
    [Fact]
    public void WindowZero_Throws_OnFirstReading()
    {
        var det = new FrozenSensorDetector(window: 0, varianceThreshold: 0.0);

        Assert.Throws<InvalidOperationException>(() => det.AddReading(1.0));
    }

    /// <summary>
    /// The supported disable switch (D-H): sample variance is never negative, so "variance &lt; 0.0"
    /// is permanently false and IsFrozen can never latch — without touching the detector's code.
    /// </summary>
    [Fact]
    public void VarianceThresholdZero_NeverReportsFrozen_EvenOnAConstantSeries()
    {
        var det = new FrozenSensorDetector(window: 10, varianceThreshold: 0.0);

        for (int i = 0; i < 50; i++)
        {
            det.AddReading(0.0);
            Assert.False(det.IsFrozen);
        }
    }
}
