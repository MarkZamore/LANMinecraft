using System.Globalization;

namespace Minecraft;

/// <summary>
/// The words shown inside the play button while a pack and its runtime are
/// prepared.
///
/// Captions wrap inside the bounded play button. They name the work and its
/// moving numbers - "Сборка", "Java", "NeoForge" - without a sentence
/// around them. The pair of sizes carries one unit because both halves use
/// the same measure.
/// </summary>
internal static class PlayButtonCaption
{
    /// <summary>The caption for one progress report, with the speed measured for it.</summary>
    public static string For(RuntimePreparationProgress progress, double bytesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var moving = progress.TotalBytes > 0;
        return progress.Stage switch
        {
            RuntimePreparationStage.SyncingPack when moving =>
                WithBytes("Сборка", progress, bytesPerSecond),
            RuntimePreparationStage.SyncingPack => "Проверка",
            RuntimePreparationStage.Downloading or RuntimePreparationStage.InstallingJava when moving =>
                WithBytes(progress.Message, progress, bytesPerSecond),
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
