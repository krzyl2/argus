namespace Argus.Orchestrator.Detection;

/// <summary>
/// Rolling measurement of how far apart a single entity's readings actually arrive (F6-3).
///
/// WHY this exists: rmad's <c>window</c> is counted in SAMPLES, and the same 720 samples is
/// ~3 h of history on <c>memory_use_percent</c> (~15 s/reading) and ~78 h on
/// <c>lodowkababcia_power</c> (~391 s/reading). Without a measured cadence the editor can only
/// show the bare number, so the operator cannot see that one sensor's baseline spans three
/// hours and the other's spans three days — which is exactly the "one default table for every
/// sensor" problem this milestone is closing. The class of a sensor is MEASURED here, never
/// guessed from its unit or its name.
///
/// Median, not mean: a reconnect gap or a single stalled reading would drag a mean by hours,
/// and the number is shown to a human as "≈ 78 h historii".
///
/// Thread-safety: <see cref="Observe"/> runs on the pipeline's write loop and
/// <see cref="MedianIntervalSec"/> is read from its verdict read loop, so both take one lock.
/// Queue&lt;T&gt; is not thread-safe: <c>ToArray</c> copies from the backing array after reading
/// the size, so a concurrent Enqueue/Dequeue — or the growth realloc — throws
/// ArgumentException/IndexOutOfRangeException. That exception surfaces inside the read loop's
/// <c>await foreach</c> over the verdict stream and kills verdict processing for the entity while
/// the write loop keeps feeding points: the entity goes silent with nothing failing loudly.
/// </summary>
public sealed class ReadingCadence
{
    /// <summary>
    /// Intervals kept. Bounded so a long-lived entity's cadence tracks reality (a sensor whose
    /// polling changed) instead of averaging over its whole uptime.
    /// </summary>
    private const int MaxIntervals = 64;

    /// <summary>
    /// Intervals needed before a median is reported at all. A cadence claimed from one or two
    /// samples would put a wall-clock span in front of the operator that the next reading
    /// contradicts; null makes the UI show samples only, which is honest.
    /// </summary>
    private const int MinIntervals = 3;

    private readonly Queue<double> _intervals = new(MaxIntervals);
    private readonly object _gate = new();
    private DateTimeOffset? _last;

    /// <summary>
    /// Records a reading's timestamp. Non-positive gaps (duplicate or out-of-order HA
    /// timestamps) are skipped rather than recorded as a zero-second cadence.
    /// </summary>
    public void Observe(DateTimeOffset timestamp)
    {
        lock (_gate)
        {
            if (_last is { } previous)
            {
                var seconds = (timestamp - previous).TotalSeconds;
                if (seconds > 0.0)
                {
                    if (_intervals.Count >= MaxIntervals)
                        _intervals.Dequeue();
                    _intervals.Enqueue(seconds);
                }
                else
                {
                    // Out-of-order or duplicate timestamp: do not advance _last, so the next
                    // in-order reading is measured against the newest timestamp actually seen.
                    return;
                }
            }

            _last = timestamp;
        }
    }

    /// <summary>
    /// Median seconds between readings, or null until <see cref="MinIntervals"/> gaps have
    /// been measured.
    /// </summary>
    public double? MedianIntervalSec
    {
        get
        {
            double[] sorted;
            lock (_gate)
            {
                if (_intervals.Count < MinIntervals)
                    return null;

                sorted = _intervals.ToArray();
            }

            Array.Sort(sorted);
            int n = sorted.Length;
            return n % 2 == 1
                ? sorted[n / 2]
                : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2.0;
        }
    }
}
