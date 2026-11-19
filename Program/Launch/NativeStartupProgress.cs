using System.Text.Json;

namespace Minecraft;

internal sealed class NativeStartupProgress
{
    internal const string PreparationMessage = "Подготовка ресурсов игры";
    internal const string OpeningMessage = "Открывается игра";
    private static readonly TimeSpan OpeningSilenceLimit = TimeSpan.FromMinutes(5);
    private const int MaximumProgressLineLength = 256;

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private readonly long _startedAt;
    private IProgress<RuntimePreparationProgress>? _progress;
    private double? _fraction;
    private long? _lastOpeningActivity;
    private long _reportedWaitSeconds;
    private bool _complete;

    internal NativeStartupProgress(IProgress<RuntimePreparationProgress>? progress, TimeProvider? timeProvider = null)
    {
        _progress = progress;
        _clock = timeProvider ?? TimeProvider.System;
        _startedAt = _clock.GetTimestamp();
    }

    internal double? Fraction
    {
        get
        {
            lock (_gate) return _fraction;
        }
    }

    internal void Observe(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_gate)
        {
            if (_complete) return;
            if (_lastOpeningActivity is not null)
            {
                _lastOpeningActivity = _clock.GetTimestamp();
                return;
            }
            if (line.Length > MaximumProgressLineLength || !line.AsSpan().TrimStart().StartsWith("{") ||
                !TryReadFraction(line, out var fraction) || (_fraction is double last && fraction <= last)) return;

            _fraction = fraction;
            if (fraction == 1)
            {
                _lastOpeningActivity = _clock.GetTimestamp();
                Report(OpeningMessage);
            }
            else
            {
                Report(PreparationMessage, fraction);
            }
        }
    }

    internal bool PollForOpeningTimeout()
    {
        lock (_gate)
        {
            if (_complete) return false;
            if (_lastOpeningActivity is long last)
                return _clock.GetElapsedTime(last) >= OpeningSilenceLimit;

            // The bootstrap can download thousands of files before it has a
            // fraction to report. Count time without guessing a percentage.
            if (_fraction is null && _progress is not null)
            {
                var seconds = (long)_clock.GetElapsedTime(_startedAt).TotalSeconds;
                if (seconds >= 15 && seconds - _reportedWaitSeconds >= 5)
                {
                    _reportedWaitSeconds = seconds;
                    Report($"Подготовка ресурсов · {seconds / 60}:{seconds % 60:00}");
                }
            }
            return false;
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            _complete = true;
            _progress = null;
        }
    }

    private void Report(string message, double? fraction = null) =>
        _progress?.Report(new(RuntimePreparationStage.PreparingNativeGame, message, fraction));

    private static bool TryReadFraction(string line, out double fraction)
    {
        fraction = default;
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("fraction", out var value) &&
                   value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out fraction) &&
                   fraction is >= 0 and <= 1;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
