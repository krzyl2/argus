namespace Argus.Orchestrator.Detection;

/// <summary>
/// Fixed-size ring buffer of recent detector scores exposing the mid-rank of a
/// candidate score within its own window (WS2 / F2, F6).
///
/// WHY mid-rank and not strict-less: the measured score distributions are heavily
/// quantized — <c>sensor.zamrazarkapiwnica_power</c> produces roughly five distinct
/// score levels (F4). With a strict-less rank every tie collapses to the rank of the
/// level BELOW it, so the modal level would score 0.0 while a rarer-but-lower level
/// outranks it — an inversion that a quantile gate cannot recover from. Counting ties
/// as half keeps the rank monotone in the score even when the support is tiny.
///
/// Per-entity rank is what makes one default threshold table arithmetically correct on
/// every sensor regardless of the detector's absolute score scale (F6): the gate compares
/// a score against that entity's own recent history, never against a global constant.
/// </summary>
internal sealed class RollingRank
{
    private readonly double[] _buf;
    private int _head;
    private int _count;

    /// <param name="windowSize">Number of recent scores retained. Must be at least 1.</param>
    public RollingRank(int windowSize)
    {
        if (windowSize < 1)
            throw new ArgumentOutOfRangeException(nameof(windowSize), windowSize, "Window size must be at least 1.");
        _buf = new double[windowSize];
    }

    /// <summary>Number of scores currently held (grows to the window size, then stays).</summary>
    public int Count => _count;

    /// <summary>
    /// Mid-rank of <paramref name="s"/> in the retained window: (below + half the ties) / count.
    /// Returns 0.0 for an empty window so an uncalibrated entity can never look extreme.
    /// </summary>
    public double RankOf(double s)
    {
        if (_count == 0)
            return 0.0;

        int lt = 0, eq = 0;
        for (int i = 0; i < _count; i++)
        {
            double x = _buf[i];
            if (x < s) lt++;
            else if (x == s) eq++;
        }

        return (lt + 0.5 * eq) / _count;
    }

    /// <summary>Appends a score, evicting the oldest one once the window is full.</summary>
    public void Push(double s)
    {
        _buf[_head] = s;
        _head = (_head + 1) % _buf.Length;
        if (_count < _buf.Length)
            _count++;
    }
}
