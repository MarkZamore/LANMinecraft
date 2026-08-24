namespace Minecraft;

/// <summary>
/// The line written across the bar while a world is handed over.
///
/// Someone watching a world move wants two things from it: how far it has got,
/// and when they can stop watching. The second one is not a division of this
/// bar - the bar is one step of five, and the step in front of it runs at a
/// different speed - so it comes from <see cref="TransferRun"/>, which knows
/// the shape of the whole handover. "ещё" on this line means the whole of it
/// is that far off, not the step whose name is written beside it.
///
/// The estimate is rounded coarsely before it is written down. A number that
/// changes every frame reads as noise rather than as an answer, and the
/// coarseness rises with the wait: under a minute the seconds are what somebody
/// is sitting through, an hour in nobody is reading them.
/// </summary>
internal static class TransferProgressLine
{
    /// <summary>
    /// How wide the line may get before something has to come off it. The bar
    /// is 454px and the text is centred over it with nothing to trim or wrap
    /// it, so a line past that spills sideways over the Передать button rather
    /// than shrinking. Kept in characters because that is what this class can
    /// count; the pixels are measured in TransferProgressLineTests.
    /// </summary>
    private const int Budget = 68;

    /// <summary>
    /// Step, bytes, speed, and - once there is an honest one - how long until
    /// the whole handover is over.
    ///
    /// All four rarely collide, but they can: a step named after the other
    /// player, a world in gigabytes and a wait in hours together outgrow the
    /// bar. The speed is then what goes. It was there to answer "is this
    /// stuck", and an estimate that keeps changing answers that better.
    /// </summary>
    public static string Compose(
        string stage, long current, long total, double bytesPerSecond, TimeSpan? remaining)
    {
        var speed = $"{FormatBytes((long)bytesPerSecond)}/с";
        var left = Remaining(remaining);
        if (left is null) return Line(stage, current, total, speed);

        var full = Line(stage, current, total, $"{speed}, {left}");
        return full.Length <= Budget ? full : Line(stage, current, total, left);
    }

    private static string Line(string stage, long current, long total, string inside)
    {
        var line = $"{FormatPair(current, total)} ({inside})";
        return string.IsNullOrEmpty(stage) ? line : $"{stage}: {line}";
    }

    /// <summary>
    /// What a step with no byte count of its own says: its name, and the same
    /// estimate, because the handover behind it has not stopped being measured.
    /// </summary>
    public static string ComposeWaiting(string stage, TimeSpan? remaining)
    {
        var name = string.IsNullOrEmpty(stage) ? "Передача" : stage;
        return Remaining(remaining) is { } left ? $"{name}... {left}" : $"{name}...";
    }

    /// <summary>
    /// "ещё ≈ 3 мин 20 с", or nothing at all when there is no answer worth
    /// giving: before the handover has gone far enough to divide by, and past
    /// the point where the arithmetic is describing a stall rather than a wait.
    /// </summary>
    public static string? Remaining(TimeSpan? remaining)
    {
        if (remaining is not { } wait || wait < TimeSpan.Zero || wait > TimeSpan.FromDays(1))
        {
            return null;
        }

        var seconds = wait.TotalSeconds;
        var step = seconds < 60 ? 5 : seconds < 600 ? 10 : seconds < 3600 ? 60 : 300;
        var rounded = (long)Math.Round(seconds / step, MidpointRounding.AwayFromZero) * step;
        // Rounding down to nothing would promise an arrival that has not
        // happened; the smallest step is the smallest thing worth saying.
        var span = TimeSpan.FromSeconds(Math.Max(step, rounded));

        if (span.TotalHours >= 1)
        {
            var hours = (int)span.TotalHours;
            return span.Minutes == 0
                ? $"ещё ≈ {hours} ч"
                : $"ещё ≈ {hours} ч {span.Minutes} мин";
        }

        if (span.TotalSeconds < 60) return $"ещё ≈ {span.Seconds} с";
        if (span.TotalSeconds >= 600 || span.Seconds == 0)
        {
            return $"ещё ≈ {(int)span.TotalMinutes} мин";
        }
        return $"ещё ≈ {span.Minutes} мин {span.Seconds} с";
    }

    /// <summary>
    /// "500 МБ / 1 ГБ", but "8,99 / 9,99 ГБ" when both land on the same unit:
    /// the bar is a fixed width and the unit written twice buys nothing.
    /// </summary>
    public static string FormatPair(long current, long total)
    {
        var done = FormatBytes(current);
        var whole = FormatBytes(total);
        var unit = whole[(whole.LastIndexOf(' ') + 1)..];
        return done.EndsWith(" " + unit, StringComparison.Ordinal)
            ? $"{done[..^(unit.Length + 1)]} / {whole}"
            : $"{done} / {whole}";
    }

    public static string FormatBytes(long bytes)
    {
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;

        if (bytes >= gb) return $"{bytes / (double)gb:0.##} ГБ";
        if (bytes >= mb) return $"{bytes / (double)mb:0.##} МБ";
        if (bytes >= kb) return $"{bytes / (double)kb:0.##} КБ";
        return $"{bytes} Б";
    }
}
