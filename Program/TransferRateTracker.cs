using System.Diagnostics;

namespace Minecraft;

/// <summary>
/// Bytes per second as a player would read them: the average over the last few
/// seconds, not a smoothed instant. A Steam send does not drain evenly - the
/// queue sits full while an unacknowledged window is resent, then empties in
/// one gulp - and an exponential filter fed that turns each gulp into a burst
/// of speed the wire never had and keeps showing it for a while.
/// </summary>
internal sealed class TransferRateTracker
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(6);

    private readonly Queue<(long Timestamp, long Bytes)> _samples = new();
    private string _scope = "";
    private double _bytesPerSecond;

    public double Update(long currentBytes, string scope)
    {
        var now = Stopwatch.GetTimestamp();
        currentBytes = Math.Max(0, currentBytes);
        if (!string.Equals(_scope, scope, StringComparison.Ordinal) ||
            (_samples.Count > 0 && currentBytes < _samples.Last().Bytes))
        {
            _scope = scope;
            _samples.Clear();
            _bytesPerSecond = 0;
        }

        _samples.Enqueue((now, currentBytes));
        var horizon = now - (long)(Window.TotalSeconds * Stopwatch.Frequency);
        while (_samples.Count > 2 && _samples.Peek().Timestamp < horizon)
        {
            _samples.Dequeue();
        }

        var oldest = _samples.Peek();
        var elapsed = (now - oldest.Timestamp) / (double)Stopwatch.Frequency;
        // Two samples a few milliseconds apart say nothing about speed yet;
        // hold the last honest number until the window has something in it.
        if (elapsed >= 1d)
        {
            _bytesPerSecond = Math.Max(0, currentBytes - oldest.Bytes) / elapsed;
        }
        return _bytesPerSecond;
    }

    public void Reset()
    {
        _scope = "";
        _samples.Clear();
        _bytesPerSecond = 0;
    }
}
