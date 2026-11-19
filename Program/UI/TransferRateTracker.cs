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
        if (!string.Equals(_scope, scope, StringComparison.Ordinal))
        {
            _scope = scope;
            _samples.Clear();
            _bytesPerSecond = 0;
        }
        // A small step back is a wobble in how "delivered" is measured, and
        // clamping to the last value keeps one bad sample from zeroing six
        // seconds of history.
        //
        // A step back past the oldest sample in the window is not that. This
        // used to assume a restart always changes the stage, and it does not:
        // the Java download falls over to its second source and begins again at
        // nothing under the same "Java 21.0.12.1", and the loader fetches its
        // installer and then its libraries under one name. The window then
        // describes a download that is no longer running, the clamp pins every
        // later sample to the old high-water mark, and the difference across the
        // window is exactly zero - so the line reads 0 Б/с for the rest of the
        // pass. Past the oldest sample the window is thrown away instead, and
        // the speed is nothing until the new one fills, which is the truth.
        if (_samples.Count > 0 && currentBytes < _samples.Last().Bytes)
        {
            if (currentBytes < _samples.Peek().Bytes)
            {
                _samples.Clear();
                _bytesPerSecond = 0;
            }
            else
            {
                currentBytes = _samples.Last().Bytes;
            }
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
