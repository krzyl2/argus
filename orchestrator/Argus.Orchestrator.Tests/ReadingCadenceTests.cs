using Argus.Orchestrator.Detection;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for ReadingCadence (F6-3).
///
/// Intent under test: rmad's <c>window</c> is configured in SAMPLES, and the plan's own
/// measurements say 720 samples is ~3 h on memory_use_percent (~15.3 s/reading) and ~78 h on
/// lodowkababcia_power (~391 s/reading). The editor can only put that span in front of the
/// operator if the cadence is MEASURED per sensor. Every number below is one of those measured
/// values, so a regression that starts guessing cadence from the unit or the entity name fails
/// here rather than in a screenshot.
/// </summary>
public class ReadingCadenceTests
{
    private static DateTimeOffset T0 => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FewerThanThreeIntervals_ReportsNull_RatherThanAClaimedCadence()
    {
        // WHY null and not "best effort": the value is rendered to a human as "≈ 78 h
        // historii tego czujnika". A span computed from one or two gaps is contradicted by
        // the next reading, and a wrong span is worse than showing samples only.
        var cadence = new ReadingCadence();
        cadence.Observe(T0);
        cadence.Observe(T0.AddSeconds(391));
        cadence.Observe(T0.AddSeconds(782));
        Assert.Null(cadence.MedianIntervalSec);
    }

    [Fact]
    public void SlowSensor_ReportsMeasured391Sec_WhichIs78HoursOver720Samples()
    {
        // lodowkababcia_power: the sensor whose 720-sample window is 78 h and must warn.
        var cadence = new ReadingCadence();
        for (int i = 0; i <= 8; i++)
            cadence.Observe(T0.AddSeconds(391.0 * i));

        Assert.Equal(391.0, cadence.MedianIntervalSec!.Value, 3);
        Assert.Equal(78.2, 720 * cadence.MedianIntervalSec!.Value / 3600.0, 1);
    }

    [Fact]
    public void FastSensor_ReportsMeasured15Point3Sec_WhichIs3HoursOver720Samples()
    {
        // memory_use_percent: the same 720 samples, three hours instead of three days — the
        // whole reason one default table needs a per-sensor readout.
        var cadence = new ReadingCadence();
        for (int i = 0; i <= 8; i++)
            cadence.Observe(T0.AddSeconds(15.3 * i));

        Assert.Equal(15.3, cadence.MedianIntervalSec!.Value, 3);
        Assert.Equal(3.1, 720 * cadence.MedianIntervalSec!.Value / 3600.0, 1);
    }

    [Fact]
    public void SingleReconnectGap_DoesNotMoveTheReportedCadence()
    {
        // WHY median and not mean: one six-hour reconnect gap in an otherwise 391 s series
        // drags a mean to ~2900 s (~580 h over 720 samples) and would tell the operator the
        // baseline spans three weeks. The median ignores it.
        var cadence = new ReadingCadence();
        var t = T0;
        for (int i = 0; i < 4; i++)
        {
            cadence.Observe(t);
            t = t.AddSeconds(391);
        }
        cadence.Observe(t);
        t = t.AddHours(6); // reconnect gap
        cadence.Observe(t);
        for (int i = 0; i < 4; i++)
        {
            t = t.AddSeconds(391);
            cadence.Observe(t);
        }

        Assert.Equal(391.0, cadence.MedianIntervalSec!.Value, 3);
    }

    [Fact]
    public void DuplicateTimestamp_IsSkipped_NotRecordedAsZeroCadence()
    {
        // HA re-delivers a state with an unchanged last_changed on reconnect. Recording that
        // as a 0 s gap would halve the reported cadence and, with enough of them, render
        // "≈ 0 min historii" for a window that actually spans days.
        var cadence = new ReadingCadence();
        for (int i = 0; i <= 6; i++)
        {
            cadence.Observe(T0.AddSeconds(391.0 * i));
            cadence.Observe(T0.AddSeconds(391.0 * i)); // duplicate
        }

        Assert.Equal(391.0, cadence.MedianIntervalSec!.Value, 3);
    }

    [Fact]
    public void CadenceChange_IsTrackedRatherThanAveragedOverAllUptime()
    {
        // A sensor whose polling interval changes must report the NEW cadence once the
        // bounded window has turned over — the readout describes the sensor as it is now.
        var cadence = new ReadingCadence();
        var t = T0;
        for (int i = 0; i < 70; i++)
        {
            cadence.Observe(t);
            t = t.AddSeconds(391);
        }
        for (int i = 0; i < 70; i++)
        {
            cadence.Observe(t);
            t = t.AddSeconds(15.3);
        }

        Assert.Equal(15.3, cadence.MedianIntervalSec!.Value, 3);
    }

    [Fact]
    public async Task ObserveOnWriteLoop_ConcurrentWithMedianOnReadLoop_NeverThrows()
    {
        // ScoreStreamPipeline calls Observe() from the write loop (one per HA reading) and reads
        // MedianIntervalSec from the verdict read loop (one per verdict). Queue<T> is not
        // thread-safe: ToArray() copies from the backing array after reading the size, so a
        // concurrent Enqueue/Dequeue — or the growth realloc — throws
        // ArgumentException/IndexOutOfRangeException. That exception lands inside the read loop's
        // await foreach over the verdict stream and kills verdict processing for the entity,
        // while the write loop keeps pushing points: the entity stops publishing and nothing
        // reports it. The rule this pins is not "a lock exists", it is "the two loops may call
        // this object at the same time".
        var cadence = new ReadingCadence();
        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Exception? failure = null;

        var writer = Task.Run(() =>
        {
            var t = T0;
            while (!stop.IsCancellationRequested)
            {
                cadence.Observe(t);
                t = t.AddSeconds(1);
            }
        });

        var reader = Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                    _ = cadence.MedianIntervalSec;
            }
            catch (Exception ex)
            {
                failure = ex;
                stop.Cancel();
            }
        });

        await Task.WhenAll(writer, reader);

        Assert.Null(failure);
    }
}
