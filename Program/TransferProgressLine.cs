namespace Minecraft;

/// <summary>
/// The line written across the bar while a world is handed over.
///
/// Someone watching a world move wants two things from it: how far it has got,
/// and when they can stop watching. The second is a division - what is left,
/// over how fast it is going - and all of its honesty lives in the speed.
/// <see cref="TransferRateTracker"/> averages the last six seconds rather than
/// reporting an instant, and the quotient is then rounded coarsely on top of
/// that: a number that changes every frame reads as noise, not as an answer.
///
/// The estimate covers the stage the bar is measuring, not the whole handover.
/// Copying, compressing, sending, unpacking and checking are five passes over
/// the same gigabytes at five different speeds, and nothing here has seen the
/// later ones yet. The stage's own name is written immediately to the left, so
/// what the number belongs to is on screen next to it.
/// </summary>
internal static class TransferProgressLine
{
    /// <summary>Past a day the arithmetic is describing a stall, not a wait.</summary>
    private const double TooFarToSaySeconds = 24 * 60 * 60;

    /// <summary>
    /// Bytes done, bytes in total, current speed, and - once the speed means
    /// anything - what is left of this stage.
    /// </summary>
    public static string Compose(string stage, long current, long total, double bytesPerSecond)
    {
        var speed = $"{FormatBytes((long)bytesPerSecond)}/с";
        var remaining = Remaining(total - current, bytesPerSecond);
        var line = remaining is null
            ? $"{FormatBytes(current)} / {FormatBytes(total)} ({speed})"
            : $"{FormatBytes(current)} / {FormatBytes(total)} ({speed}, {remaining})";
        return string.IsNullOrEmpty(stage) ? line : $"{stage}: {line}";
    }

    /// <summary>
    /// "ещё ≈ 3 мин 20 с", or nothing at all while an answer would be a guess:
    /// before the rate window has filled, after the last byte, and when the
    /// division comes back with a wait no one is going to sit through.
    ///
    /// The coarseness rises with the wait. Under a minute the seconds matter,
    /// so they are kept to the nearest five; a quarter of an hour in, nobody is
    /// reading the seconds, and showing them only makes the line flicker.
    /// </summary>
    public static string? Remaining(long remainingBytes, double bytesPerSecond)
    {
        if (remainingBytes <= 0 || bytesPerSecond <= 0) return null;

        var seconds = remainingBytes / bytesPerSecond;
        if (double.IsNaN(seconds) || seconds > TooFarToSaySeconds) return null;

        var step = seconds < 60 ? 5 : seconds < 600 ? 10 : seconds < 3600 ? 60 : 300;
        var rounded = (long)Math.Round(seconds / step, MidpointRounding.AwayFromZero) * step;
        // Rounding down to nothing would promise an arrival that has not
        // happened; the smallest step is the smallest thing worth saying.
        var span = TimeSpan.FromSeconds(Math.Max(step, rounded));

        if (span.TotalHours >= 1)
        {
            var hours = (int)span.TotalHours;
            return span.Minutes == 0 ? $"ещё ≈ {hours} ч" : $"ещё ≈ {hours} ч {span.Minutes} мин";
        }

        if (span.TotalSeconds < 60) return $"ещё ≈ {span.Seconds} с";
        if (span.TotalSeconds >= 600 || span.Seconds == 0) return $"ещё ≈ {(int)span.TotalMinutes} мин";
        return $"ещё ≈ {span.Minutes} мин {span.Seconds} с";
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
