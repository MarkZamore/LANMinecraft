using System.Diagnostics;

namespace Minecraft;

/// <summary>
/// One world handover being watched, from the first byte to the last.
///
/// It keeps two things: how long each step has taken so far, and how far
/// through the whole thing that puts us, using the shape in
/// <see cref="TransferPacing"/>. From those the answer is a proportion and
/// nothing more - a third of the way in after a minute means two minutes left -
/// so no speed has to be guessed for a step that has not started yet.
///
/// Nothing is said for the first few seconds. Early on the fraction is small
/// enough that dividing by it turns a moment's hesitation into an hour, and an
/// estimate that opens at "3 ч" and settles at "4 мин" is worse than no
/// estimate at all.
/// </summary>
internal sealed class TransferRun
{
    /// <summary>Below this, the division is louder than the answer.</summary>
    private const double SpeakAboveFraction = 0.02;
    private static readonly TimeSpan SpeakAfter = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TooFarToSay = TimeSpan.FromDays(1);

    private readonly TransferPacing _pacing;
    private readonly Func<TimeSpan> _elapsed;
    private readonly Dictionary<string, double> _seconds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _finished = new(StringComparer.Ordinal);
    private string _stage = "";
    private double _stageStartedAt;
    private double _fraction;

    public TransferRun(TransferPacing pacing, Func<TimeSpan>? elapsed = null)
    {
        _pacing = pacing;
        if (elapsed is not null)
        {
            _elapsed = elapsed;
        }
        else
        {
            var clock = Stopwatch.StartNew();
            _elapsed = () => clock.Elapsed;
        }
    }

    /// <summary>The step the bar was last on, for deciding whether it ended well.</summary>
    public string Stage => _stage;

    /// <summary>
    /// A run that reached the last step of its side got to the end; one that
    /// stopped anywhere else was cancelled or broke, and its timings say
    /// nothing about how long a handover takes.
    /// </summary>
    public bool Completed =>
        TransferPacing.PipelineFor(_stage) is { } pipeline &&
        string.Equals(pipeline[^1], _stage, StringComparison.Ordinal);

    /// <summary>
    /// Takes one progress update and answers how much longer the whole handover
    /// has, or nothing while that would be a guess.
    /// </summary>
    public TimeSpan? Advance(string stage, long current, long total)
    {
        var now = _elapsed();
        if (!string.Equals(_stage, stage, StringComparison.Ordinal))
        {
            if (_stage.Length > 0)
            {
                _seconds[_stage] = _seconds.GetValueOrDefault(_stage)
                    + Math.Max(0, now.TotalSeconds - _stageStartedAt);
                _finished.Add(_stage);
            }
            _stage = stage;
            _stageStartedAt = now.TotalSeconds;
        }

        var progress = total > 0 ? Math.Clamp(current / (double)total, 0, 1) : 0;
        // Never walk backwards: a step whose byte total arrives late would
        // otherwise undo the step before it.
        _fraction = Math.Max(_fraction, _pacing.FractionDone(_finished, stage, progress));

        if (_fraction < SpeakAboveFraction || now < SpeakAfter) return null;
        if (_fraction >= 1) return TimeSpan.Zero;

        var remaining = TimeSpan.FromSeconds(now.TotalSeconds * (1 - _fraction) / _fraction);
        return remaining > TooFarToSay ? null : remaining;
    }

    /// <summary>Seconds spent per step, with the one still running closed off.</summary>
    public IReadOnlyDictionary<string, double> Timings()
    {
        var timings = new Dictionary<string, double>(_seconds, StringComparer.Ordinal);
        if (_stage.Length > 0)
        {
            timings[_stage] = timings.GetValueOrDefault(_stage)
                + Math.Max(0, _elapsed().TotalSeconds - _stageStartedAt);
        }
        return timings;
    }
}
