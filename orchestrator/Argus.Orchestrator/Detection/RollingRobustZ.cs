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
/// Scale ladder, first strictly positive wins:
///   1. 1.4826 · MAD   — the robust default,
///   2. (Q3 − Q1) / 1.349 — survives a series where more than half the window is one value,
///   3. sample StdDev over the LIVE slots only — a last resort for a nearly-degenerate window,
///   4. 0.0 = abstain (return z = 0).
/// Step 3 must never see the whole array: unfilled slots are zeros, and on a 0/984 W duty-cycle
/// sensor those zeros would look like real readings and deflate the scale into permanent alarm.
/// Step 4 is deliberate abstention, not a fallback: a perfectly flat series has no deviation to
/// report, and after D-H there is no frozen-sensor guard behind it (§7 #8).
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

        // Quartiles must be read from the FIRST sort — the array is overwritten with
        // absolute deviations below, and reading them afterwards would silently return
        // quartiles of |x − median| instead of quartiles of the series.
        double q1 = _scratch[_count / 4];
        double q3 = _scratch[3 * _count / 4];
        double med = MedianOfSorted(_scratch, _count);

        for (int i = 0; i < _count; i++)
            _scratch[i] = Math.Abs(_scratch[i] - med);
        Array.Sort(_scratch, 0, _count);
        double mad = MedianOfSorted(_scratch, _count);

        double scale = 1.4826 * mad;
        if (scale <= 0.0)
            scale = (q3 - q1) / 1.349;
        if (scale <= 0.0)
            scale = StdDev(_buf, _count);
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

    /// <summary>Sample standard deviation over the first <paramref name="n"/> (live) slots only.</summary>
    private static double StdDev(double[] buf, int n)
    {
        if (n < 2)
            return 0.0;

        double sum = 0.0;
        for (int i = 0; i < n; i++)
            sum += buf[i];
        double mean = sum / n;

        double sumSq = 0.0;
        for (int i = 0; i < n; i++)
        {
            double d = buf[i] - mean;
            sumSq += d * d;
        }

        return Math.Sqrt(sumSq / (n - 1));
    }
}
