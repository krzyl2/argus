namespace Argus.Orchestrator.Detection;

/// <summary>
/// Fixed-size ring buffer of recent RAW sensor values exposing a robust z-score
/// |x − median| / scale over its own window (WS2, second evidence channel).
///
/// WHY a raw channel at all: the score channel only says how RARE a score is inside its
/// own distribution. On a sensor whose score support is a handful of quantized levels the
/// rank channel is effectively dead (F4), and rarity is not deviation. The raw channel
/// measures deviation directly, in the sensor's own units, with no model, no fit and no
/// persistence — the same category as FrozenSensorDetector.
///
/// Scale ladder, first strictly positive wins — DELIBERATELY the same ladder as
/// <c>RmadDetector._scale</c> (detector/argus_detector/rmad_detector.py):
///   1. 1.4826 · MAD — the robust default,
///   2. mean absolute deviation from the median, over the LIVE slots only,
///   3. 0.0 = abstain (return z = 0).
/// Rung 2 must never see the whole array: unfilled slots are zeros, and on a 0/984 W duty-cycle
/// sensor those zeros would look like real readings and deflate the scale into permanent alarm.
/// Rung 3 is deliberate abstention, not a fallback: a perfectly flat series has no deviation to
/// report, and after D-H there is no frozen-sensor guard behind it (§7 #8).
///
/// WHY rung 2 is MeanAD and not the IQR or the sample StdDev: on a duty-cycle series (the fridge
/// spends fraction p of its window at 984 W and the rest at 0 W) MAD and the IQR are both zero,
/// so rung 2 is what actually decides. MeanAD gives σ = 984·p ⇒ z = 1/p; StdDev gives
/// σ = 984·√(p(1−p)) ⇒ z = 1/√(p(1−p)). Those are different statistics with different cliffs:
/// the plan's §7 #3 blocker ("the duty-cycle cliff z = 1/p silences the fridge at p ≥ 0.2") is
/// computed for the first, while the second is already below z_fire = 5.0 at the fridge's own
/// measured 12 % duty cycle (z = 3.08) — i.e. the sensor would be silent at steady state and the
/// documented safety margin would not be the one in force. Two channels judging the same reading
/// have to use the same σ, and the one the plan's numbers are derived from is Python's.
///
/// Python's rung 3 (a scale_floor in the sensor's own units, D-I) has no counterpart here: the
/// alert layer takes no scale_floor parameter, so the raw channel damps nothing. Its rung 4 also
/// differs on purpose: a degenerate window scores 1.0 there (frozen coverage) and abstains here.
/// </summary>
internal sealed class RollingRobustZ
{
    private readonly double[] _buf;
    private readonly double[] _scratch;
    private int _head;
    private int _count;

    /// <summary>Minimum live samples before a z-score is meaningful; below this the channel abstains.</summary>
    public const int MinSamples = 10;

    /// <param name="windowSize">Number of recent raw values retained. Must be at least 1.</param>
    public RollingRobustZ(int windowSize)
    {
        if (windowSize < 1)
            throw new ArgumentOutOfRangeException(nameof(windowSize), windowSize, "Window size must be at least 1.");
        _buf = new double[windowSize];
        _scratch = new double[windowSize];
    }

    /// <summary>Number of raw values currently held.</summary>
    public int Count => _count;

    /// <summary>
    /// Robust z-score of <paramref name="x"/> against the retained window.
    /// Returns 0.0 below <see cref="MinSamples"/> live samples and when the whole scale
    /// ladder degenerates (abstention).
    /// </summary>
    public double ZOf(double x)
    {
        if (_count < MinSamples)
            return 0.0;

        Array.Copy(_buf, _scratch, _count);
        Array.Sort(_scratch, 0, _count);

        double med = MedianOfSorted(_scratch, _count);

        // _scratch now holds the absolute deviations from the median: the median of those is the
        // MAD (rung 1) and their mean is rung 2, so both rungs come out of one pass.
        for (int i = 0; i < _count; i++)
            _scratch[i] = Math.Abs(_scratch[i] - med);
        Array.Sort(_scratch, 0, _count);
        double mad = MedianOfSorted(_scratch, _count);

        double scale = 1.4826 * mad;
        if (scale <= 0.0)
            scale = MeanAbsoluteDeviation(_scratch, _count);
        if (scale <= 0.0)
            return 0.0;

        return Math.Abs(x - med) / scale;
    }

    /// <summary>Appends a raw value, evicting the oldest one once the window is full.</summary>
    public void Push(double x)
    {
        _buf[_head] = x;
        _head = (_head + 1) % _buf.Length;
        if (_count < _buf.Length)
            _count++;
    }

    private static double MedianOfSorted(double[] sorted, int n)
        => n % 2 == 1 ? sorted[n / 2] : 0.5 * (sorted[n / 2 - 1] + sorted[n / 2]);

    /// <summary>
    /// Mean of the absolute deviations from the median, over the first <paramref name="n"/> (live)
    /// slots only — rung 2, and the same statistic as <c>_mean_ad</c> in rmad_detector.py.
    /// Takes the array of |x − median| values the caller has already built.
    /// </summary>
    private static double MeanAbsoluteDeviation(double[] deviations, int n)
    {
        if (n < 1)
            return 0.0;

        double sum = 0.0;
        for (int i = 0; i < n; i++)
            sum += deviations[i];

        return sum / n;
    }
}
