using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>
/// What e4steam said about its Steam sessions, picked out of a game log.
/// </summary>
/// <remarks>
/// e4steam writes when it accepts a guest, closes a bridge or loses a session
/// at DEBUG, and only the game's debug copy keeps DEBUG - the copy the launcher
/// discards at start and a report keeps only two megabytes of the end of. So
/// the one account of why a guest fell out of a world used to be gone by the
/// time anybody asked. These lines are small; a whole evening of them fits.
/// </remarks>
public static partial class E4steamLogLines
{
    // [13Sep2026 20:35:02.000] [e4steam-steam-runtime/DEBUG] [e4steam/]: ...
    [GeneratedRegex(@"^\[[^\]]+\] \[(?<thread>[^\]]+)/[A-Z]+\] \[(?<logger>[^\]]*)\]")]
    private static partial Regex Header();

    [GeneratedRegex(@"^debug-(?<index>\d+)\.log\.gz$", RegexOptions.IgnoreCase)]
    private static partial Regex RolledDebugLog();

    /// <summary>
    /// Whether a log line was written by e4steam: its logger or its thread,
    /// not merely a mention - the loader lists e4steam among every pack it
    /// sorts and every mixin config it applies, in lines megabytes long.
    /// </summary>
    public static bool IsE4steamLine(string line)
    {
        var header = Header().Match(line);
        return header.Success &&
               (header.Groups["thread"].Value.StartsWith("e4steam", StringComparison.OrdinalIgnoreCase) ||
                header.Groups["logger"].Value.StartsWith("e4steam", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The game's debug log and the archives it rolled, oldest first.</summary>
    /// <remarks>
    /// The loader rolls debug.log into debug-1.log.gz on every start and past
    /// 200 MB, shifting the older ones up, so the highest number is the oldest.
    /// </remarks>
    public static IReadOnlyList<string> DebugLogsOldestFirst(string logsDirectory)
    {
        if (!Directory.Exists(logsDirectory)) return [];
        var logs = Directory.EnumerateFiles(logsDirectory, "debug-*.log.gz")
            .Select(path => (Path: path, Match: RolledDebugLog().Match(Path.GetFileName(path))))
            .Where(file => file.Match.Success)
            .OrderByDescending(file => int.Parse(file.Match.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Select(file => file.Path)
            .ToList();
        var current = Path.Combine(logsDirectory, "debug.log");
        if (File.Exists(current)) logs.Add(current);
        return logs;
    }

    /// <summary>
    /// The e4steam lines of some logs, in order, with the lines that continue
    /// them, keeping the last <paramref name="maxChars"/> characters when there are more.
    /// </summary>
    /// <remarks>
    /// A log line opens with its timestamp in brackets. A line that does not is
    /// the rest of the one above - the exception and stack trace under an
    /// e4steam failure belong to it and are kept with it. Each file is read line
    /// by line, gzipped ones through the archive, so a debug log of hundreds of
    /// megabytes costs time, not memory.
    /// </remarks>
    public static IReadOnlyList<string> Read(IEnumerable<string> paths, int maxChars)
    {
        var kept = new Queue<string>();
        long size = 0;
        foreach (var path in paths)
        {
            var following = false;
            foreach (var line in ReadLines(path))
            {
                var continuation = line.Length > 0 && line[0] != '[';
                var keep = continuation ? following : IsE4steamLine(line);
                if (!continuation) following = keep;
                if (!keep) continue;
                Keep(kept, ref size, line, maxChars);
            }
        }
        return [.. kept];
    }

    /// <summary>Every line of a file, keeping the last <paramref name="maxChars"/> characters.</summary>
    public static IReadOnlyList<string> ReadTail(string path, int maxChars)
    {
        var kept = new Queue<string>();
        long size = 0;
        foreach (var line in ReadLines(path)) Keep(kept, ref size, line, maxChars);
        return [.. kept];
    }

    private static void Keep(Queue<string> kept, ref long size, string line, int maxChars)
    {
        kept.Enqueue(line);
        size += line.Length + 1;
        while (size > maxChars && kept.Count > 0)
        {
            size -= kept.Dequeue().Length + 1;
        }
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line) yield return line;
    }
}
