using System.Globalization;

namespace Minecraft;

/// <summary>
/// The words shown inside the play button while a pack and its runtime are
/// prepared.
///
/// The button is a fixed 550 px on a canvas that never changes, its caption is
/// drawn at the display size without wrapping or trimming, and anything wider
/// is cut mid-letter with nothing to say it was. So a caption names the thing
/// being worked on and, while bytes move, the numbers - "Сборка", "Файлы 2/2",
/// "Java 21.0.12" - and never a sentence about them. The pair of sizes carries
/// one unit, taken from the total, because both halves are the same measure.
/// </summary>
internal static class PlayButtonCaption
{
    /// <summary>
    /// The widest a caption may draw: the play button spans both left columns
    /// of the 820 px canvas (275 each) less one pixel of border a side.
    /// </summary>
    public const double MaxWidth = 548;

    /// <summary>The caption for one progress report, with the speed measured for it.</summary>
    public static string For(RuntimePreparationProgress progress, double bytesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var phase = progress.PhaseCount > 1 &&
                    progress.PhaseIndex > 0 &&
                    progress.PhaseIndex <= progress.PhaseCount
            ? $" {progress.PhaseIndex}/{progress.PhaseCount}"
            : string.Empty;
        var moving = progress.TotalBytes > 0;
        return progress.Stage switch
        {
            RuntimePreparationStage.SyncingPack when moving =>
                WithBytes("Сборка", progress, bytesPerSecond),
            RuntimePreparationStage.SyncingPack => "Проверка",
            RuntimePreparationStage.Downloading when moving =>
                WithBytes("Файлы" + phase, progress, bytesPerSecond),
            RuntimePreparationStage.Downloading => "Файлы" + phase,
            RuntimePreparationStage.InstallingJava when moving =>
                WithBytes(progress.Message, progress, bytesPerSecond),
            RuntimePreparationStage.InstallingLoader => progress.Message + phase,
            _ => progress.Message
        };
    }

    private static string WithBytes(string label, RuntimePreparationProgress progress, double bytesPerSecond) =>
        $"{label}: {Pair(progress.DownloadedBytes, progress.TotalBytes)} ({Rate(bytesPerSecond)})";

    /// <summary>"12,3 / 456,8 МБ": one unit for both, chosen by the total.</summary>
    internal static string Pair(long done, long total)
    {
        var (unit, scale) = UnitFor(total);
        var clamped = Math.Clamp(done, 0, total);
        return $"{Number(clamped / scale)} / {Number(total / scale)} {unit}";
    }

    /// <summary>"12,3 МБ/с", from whatever the rate tracker measured.</summary>
    internal static string Rate(double bytesPerSecond)
    {
        var value = bytesPerSecond > 0 ? bytesPerSecond : 0;
        var (unit, scale) = UnitFor((long)value);
        return $"{Number(value / scale)} {unit}/с";
    }

    private static (string Unit, double Scale) UnitFor(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => ("ГБ", 1024d * 1024 * 1024),
        >= 1024L * 1024 => ("МБ", 1024d * 1024),
        >= 1024 => ("КБ", 1024d),
        _ => ("Б", 1d)
    };

    // One decimal is as much as a moving number can be read at; a whole number
    // keeps no decimal point at all, which is where the widest cases are saved.
    private static string Number(double value) =>
        value >= 100 || value == Math.Floor(value)
            ? value.ToString("0", CultureInfo.CurrentCulture)
            : value.ToString("0.#", CultureInfo.CurrentCulture);
}
