using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>
/// Recognises a game that has finished and still will not end, and ends it.
/// </summary>
/// <remarks>
/// Minecraft 26.2's client main ends by starting a shutdown watchdog: a daemon
/// thread that sleeps fifteen seconds and, if the process is still there,
/// calls System.exit(-8). It is the game's own answer to a thread left behind,
/// and on NeoForge 26.2 one is: a non-daemon pool worker keeps the JVM alive
/// after main returns. The watchdog does not save it, in two ways. The loader
/// closes the minecraft module the moment main returns, so the watchdog dies
/// loading ServerWatchdog with NoClassDefFoundError. And a player who closed
/// the window with its cross has already spent the game's one watchdog on the
/// window-close path, which is told not to exit, so the one after main is never
/// started at all. Either way the process stays with no window for as long as
/// nobody ends it, and the launcher, which lets one game run at a time, said
/// it was running forever.
///
/// What holds in every case: the loader writes "Closing FML Loader" once
/// Minecraft's main is over, and main is over only after the world was left
/// and saved. The game meant to be gone fifteen seconds later, so a process
/// still there twenty seconds after that line - with no window of any kind,
/// and nothing logged after it but the loader's own last words - is ended, and
/// nothing is lost by it.
///
/// The line is read from latest.log rather than from the console. log4j writes
/// that file from inside the game, so it keeps coming for a game a restarted
/// launcher adopted, whose console pipe died with the launcher that started it.
///
/// It is matched as a whole line in the loader's own format, not as words
/// anywhere: the game writes chat into the same file, and a player pasting
/// this very line into chat must not end anybody's game. The window check is
/// the second guard - a game still playing has one, and so has the dialog a
/// fatal error opens after the same line.
/// </remarks>
public static partial class ClientShutdownHang
{
    /// <summary>How long the game's own watchdog waits before it exits the process.</summary>
    public static readonly TimeSpan GameOwnWatchdog = TimeSpan.FromSeconds(15);

    /// <summary>Time on top of the game's own wait, in case the process was already on its way out.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    /// <summary>How long after main has finished a still-running process is ended.</summary>
    public static TimeSpan EndAfter => GameOwnWatchdog + Grace;

    /// <summary>How much of the end of latest.log is read when looking for the line.</summary>
    internal const int TailBytes = 64 * 1024;

    // [20:35:24] [Render thread/INFO]: Closing FML Loader 4d770bcd
    // [20:35:24] [Render thread/INFO] [net.neoforged.fml.loading.FMLLoader]: Closing FML Loader 4d770bcd
    [GeneratedRegex(@"^\[\d{2}:\d{2}:\d{2}\] \[[^\]\[]+/INFO\](?: \[[\w.$]+\])?: Closing FML Loader [0-9a-f]+$")]
    private static partial Regex LoaderClosedLine();

    [GeneratedRegex(@"^\[\d{2}:\d{2}:\d{2}\] \[[^\]\[]+/INFO\](?: \[[\w.$]+\])?: Clearing ModLoader$")]
    private static partial Regex ModLoaderClearedLine();

    [GeneratedRegex(@"^\[\d{2}:\d{2}:\d{2}\] ")]
    private static partial Regex LogLine();

    /// <summary>Whether one latest.log line is the loader itself saying main is over.</summary>
    public static bool IsMainFinished(string? line) => line is not null && LoaderClosedLine().IsMatch(line);

    /// <summary>
    /// Whether this game's latest.log ends with main being over.
    /// </summary>
    /// <remarks>
    /// The loader's line has to be the last thing the game logged, apart from
    /// the loader's "Clearing ModLoader" after it: a line from an earlier session
    /// that a new one kept writing below is not the end of this one. Until log4j
    /// starts, latest.log is still the previous game's; a log last written before
    /// this process started says nothing about it. The time is read from the
    /// open handle, because the directory entry of a file another process holds
    /// open for writing can lag behind it.
    /// </remarks>
    public static bool LatestLogShowsMainFinished(string latestLogPath, DateTime processStartedUtc)
    {
        try
        {
            using var stream = new FileStream(
                latestLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (File.GetLastWriteTimeUtc(stream.SafeFileHandle) < processStartedUtc) return false;
            if (stream.Length > TailBytes) stream.Seek(-TailBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var closed = false;
            while (reader.ReadLine() is { } line)
            {
                if (IsMainFinished(line)) closed = true;
                else if (closed && LogLine().IsMatch(line) && !ModLoaderClearedLine().IsMatch(line)) closed = false;
            }
            return closed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Watches until the game ends by itself, or until main has been over for
    /// <see cref="EndAfter"/> with the process still there and no window, and
    /// then ends it.
    /// </summary>
    /// <returns>True when this ended the process; false when it ended by itself.</returns>
    /// <remarks>
    /// The wait is timed from the first time the state is seen, not from the
    /// file's own clock, and starts over whenever it is not - a log rotated by
    /// the next session, a window that is there after all.
    /// </remarks>
    public static async Task<bool> EndWhenStuckAsync(
        Func<bool> mainFinished,
        Func<bool> hasExited,
        Func<bool> hasWindow,
        Action end,
        Func<DateTime> utcNow,
        TimeSpan pollInterval,
        CancellationToken token)
    {
        DateTime? seenAt = null;
        while (true)
        {
            await Task.Delay(pollInterval, token).ConfigureAwait(false);
            if (hasExited()) return false;
            if (!mainFinished() || hasWindow())
            {
                seenAt = null;
                continue;
            }

            var now = utcNow();
            seenAt ??= now;
            if (now - seenAt.Value < EndAfter) continue;
            if (hasExited() || hasWindow()) return false;
            end();
            return true;
        }
    }
}
