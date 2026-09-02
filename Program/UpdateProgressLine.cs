namespace Minecraft;

/// <summary>
/// The line written across the launcher's own update bar.
///
/// It is the third of three bars and the last to get a class of its own. The
/// other two - <see cref="PlayButtonCaption"/> and
/// <see cref="TransferProgressLine"/> - name what is moving and how far it has
/// got; this one used to say "Скачивается обновление" and nothing else, while
/// holding a release number and a byte count it never showed. So every line
/// here names the release it is about, and the one with bytes carries the same
/// four things the transfer line does: what, how much, how fast, how long left.
/// </summary>
/// <remarks>
/// The bar is 454 px: the canvas's two left columns, 550 px together, less the
/// "Обновить" button beside it (<c>Size.ButtonMin</c> 88) and the gap between
/// them (<c>Gap.Left2</c> 8). The text is centred over the bar with nothing to
/// wrap or trim it, so a line past that width spills sideways over the button
/// rather than shrinking. <c>UpdateProgressLineTests</c> measures every line
/// this class can produce against the real bar in the real window.
///
/// The byte formatting is <see cref="TransferProgressLine"/>'s rather than a
/// second copy of the same arithmetic: the two bars sit six rows apart in one
/// window and a megabyte should not be written two ways in it.
/// </remarks>
internal static class UpdateProgressLine
{
    /// <summary>
    /// How wide a line may get. Kept in characters because that is what this
    /// class can count; the pixels are measured in UpdateProgressLineTests.
    /// </summary>
    private const int Budget = 68;

    /// <summary>Before the first answer comes back, which takes a moment.</summary>
    /// <remarks>
    /// What stood here was "Вы на последней версии", written before anything had
    /// been asked - an answer to a question still in flight, and the wrong one
    /// whenever an update existed.
    /// </remarks>
    public static string Checking() => "Проверка обновлений";

    /// <summary>Nothing to install, and the release that settles it.</summary>
    public static string UpToDate(int release) => $"Вы на последней версии ({release})";

    /// <summary>Downloaded and waiting for the launcher to close.</summary>
    public static string Ready(int release) => $"Обновление {release} готово к установке";

    /// <summary>A patch being folded into the executable, which moves no bytes.</summary>
    public static string Applying(int release) => $"Применение обновления {release}";

    /// <summary>Decided on, before the first byte has landed.</summary>
    public static string Starting(int release) => $"Обновление {release}: скачивается";

    /// <summary>The check itself did not get an answer.</summary>
    public static string CheckFailed() => "Не удалось проверить обновления";

    /// <summary>The check answered and the download did not finish.</summary>
    public static string DownloadFailed(int release) => $"Не удалось скачать обновление {release}";

    /// <summary>
    /// Release, bytes, speed and - once there is an honest one - how much longer.
    /// </summary>
    /// <remarks>
    /// When all four will not fit, the speed is what goes, for the reason the
    /// transfer line drops it: it was there to answer "is this stuck", and an
    /// estimate that keeps changing answers that better.
    /// </remarks>
    public static string Downloading(
        int release, long downloaded, long total, double bytesPerSecond, TimeSpan? remaining)
    {
        var head = $"Обновление {release}: {TransferProgressLine.FormatPair(downloaded, total)}";
        var speed = $"{TransferProgressLine.FormatBytes((long)Math.Max(0, bytesPerSecond))}/с";
        var left = TransferProgressLine.Remaining(remaining);
        if (left is null) return $"{head} ({speed})";

        var full = $"{head} ({speed}, {left})";
        return full.Length <= Budget ? full : $"{head} ({left})";
    }

    /// <summary>
    /// How long the rest will take at the speed measured so far, or nothing when
    /// there is no answer worth giving - before anything has moved, and once the
    /// division would be describing a stall rather than a wait.
    /// </summary>
    public static TimeSpan? Estimate(long downloaded, long total, double bytesPerSecond) =>
        bytesPerSecond > 0 && total > downloaded
            ? TimeSpan.FromSeconds((total - downloaded) / bytesPerSecond)
            : null;
}
