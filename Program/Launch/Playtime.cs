using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Minecraft;

internal static class Playtime
{
    internal const long MillisecondsPerMinute = 60000;
    private const int FileLimit = 1024 * 1024;
    private const string Header = "LANMinecraft-playtime 1";

    internal static string Key(CatalogSource entry) => Encode("catalog:" + entry.StorageId);

    internal static string LaunchKey(string path, IEnumerable<CatalogSource> sources)
    {
        var entry = sources.FirstOrDefault(source =>
            source.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            source.LegacyRelativePaths.Contains(path, StringComparer.OrdinalIgnoreCase));
        return entry is null ? Encode("local:" + path) : Key(entry);
    }

    private static string Encode(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong time);

    internal static ulong? Timestamp() => QueryUnbiasedInterruptTime(out var time) ? time : null;

    internal static void Observe(int processId, string key, ulong? start, Logger logger, Action changed)
    {
        if (start is null) return;
        try
        {
            if (!BootstrapLaunchContext.TryGet(out var context) || context is null || !File.Exists(context.ExePath)) return;
            using var game = Process.GetProcessById(processId);
            var info = new ProcessStartInfo(context.ExePath) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--track-playtime");
            info.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add(game.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add(start.Value.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add(key);
            if (Process.Start(info) is { } observer) _ = WaitForObserverAsync(observer, logger, changed);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or ArgumentException)
        {
            logger.Warn("Playtime observer could not start: " + ex.Message);
        }
    }

    private static async Task WaitForObserverAsync(Process observer, Logger logger, Action changed)
    {
        using (observer)
        {
            try
            {
                await observer.WaitForExitAsync().ConfigureAwait(false);
                if (observer.ExitCode == 0) changed();
                else if (observer.ExitCode != 183) logger.Warn($"Playtime observer exited with code {observer.ExitCode}.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                logger.Warn("Playtime observer could not be checked: " + ex.Message);
            }
        }
    }

    internal static PlaytimeSnapshot Read(AppPaths paths, Logger logger)
    {
        try
        {
            var path = paths.PlaytimeFile;
            using var parents = new GuardedFileSystem.DirectoryLocks();
            if (!parents.HoldAncestors(paths.Personal) || !File.Exists(path)) return PlaytimeSnapshot.Empty;
            using var handle = GuardedFileSystem.TryOpenOrdinaryReadFile(path, shareReplacement: true)
                ?? throw new IOException("The playtime file is not an ordinary file or is busy.");
            using var stream = new FileStream(handle, FileAccess.Read);
            if (stream.Length is <= 0 or > FileLimit) throw new InvalidDataException("Invalid playtime file size.");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            var lines = Encoding.ASCII.GetString(bytes).Split('\n');
            if (lines[0] != Header || lines[^1].Length != 0) throw new InvalidDataException("Invalid playtime header.");
            var values = new Dictionary<string, long>(StringComparer.Ordinal);
            long total = 0;
            foreach (var line in lines.AsSpan(1, lines.Length - 2))
            {
                var tab = line.IndexOf('\t');
                if (tab <= 0 || tab > 4096 || tab % 2 != 0 ||
                    !line.AsSpan(0, tab).ContainsOnlyUpperHex() ||
                    !long.TryParse(line.AsSpan(tab + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                    !values.TryAdd(line[..tab], value))
                    throw new InvalidDataException("Invalid playtime record.");
                total = checked(total + value);
            }
            return new PlaytimeSnapshot(values, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or InvalidDataException)
        {
            logger.Warn("Could not read playtime: " + ex.Message);
            return PlaytimeSnapshot.Empty;
        }
    }

    private static bool ContainsOnlyUpperHex(this ReadOnlySpan<char> text)
    {
        foreach (var ch in text)
            if (!(ch is >= '0' and <= '9' or >= 'A' and <= 'F')) return false;
        return true;
    }

    internal static string Format(long milliseconds)
    {
        var minutes = milliseconds / MillisecondsPerMinute;
        return FormattableString.Invariant($"{minutes / 60}:{minutes % 60:00}");
    }
}

internal sealed record PlaytimeSnapshot(IReadOnlyDictionary<string, long> Packs, long Total)
{
    internal static readonly PlaytimeSnapshot Empty = new(new Dictionary<string, long>(), 0);
}
