namespace Argus.Orchestrator.Detection;

/// <summary>
/// Rule-based frozen sensor detection via rolling variance check (FAULT-02, D-12).
/// Maintains a sliding window of the last N readings; IsFrozen is true when
/// the window is full and sample variance is strictly below the threshold.
/// </summary>
public sealed class FrozenSensorDetector
{
    private readonly int _window;
    private readonly double _varianceThreshold;
    private readonly Queue<double> _readings;

    /// <summary>
    /// Initializes with D-12 defaults: window=10, variance_threshold=0.001.
    /// </summary>
    public FrozenSensorDetector(int window = 10, double varianceThreshold = 0.001)
    {
        _window = window;
        _varianceThreshold = varianceThreshold;
        _readings = new Queue<double>(window);
    }

    /// <summary>
    /// Adds a new sensor reading and slides the window.
    /// </summary>
    public void AddReading(double value)
    {
        if (_readings.Count >= _window)
            _readings.Dequeue();
        _readings.Enqueue(value);
    }

    /// <summary>
    /// True when the rolling window is full and sample variance &lt; threshold.
    /// False when fewer readings than window size have been seen.
    /// </summary>
    public bool IsFrozen
    {
        get
        {
            if (_readings.Count < _window)
                return false;

            double variance = ComputeVariance(_readings);
            return variance < _varianceThreshold;
        }
    }

    private static double ComputeVariance(IEnumerable<double> values)
    {
        var list = values.ToList();
        int n = list.Count;
        if (n < 2)
            return 0.0;

        double mean = list.Sum() / n;
        double sumSq = list.Sum(x => (x - mean) * (x - mean));
        // Sample variance (n-1 denominator)
        return sumSq / (n - 1);
    }
}
