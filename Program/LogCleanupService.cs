using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Minecraft;

public static partial class LogCleanupService
{
    private static readonly TimeSpan DiagnosticRetention = TimeSpan.FromDays(7);
    private static readonly string ExecutableName =
        Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "LANMinecraft";

    private const long LauncherLogBudgetBytes = 16L * 1024 * 1024;
    private const long InstanceLogBudgetBytes = 64L * 1024 * 1024;
    private const int E4steamKeptChars = 2 * 1024 * 1024;
    private static readonly string[] SessionDiagnosticDirectories = ["logs", "debug", "crash-reports"];
    internal const string E4steamPreviousLogName = "e4steam-previous.log";

    /// <summary>The only log operation that has to precede opening the current log.</summary>
    public static void RotateLauncherLog(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var current = paths.LogFile;
        if (!MaintenancePath.TryRead(paths, current, out var attributes) ||
            (attributes & FileAttributes.Directory) != 0) return;
        try
        {
            var info = new FileInfo(current);
            if (info.Length == 0)
            {
                MaintenancePath.TryDelete(paths, current, directory: false);
                return;
            }
            var stamp = info.LastWriteTimeUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var archive = Path.Combine(paths.Personal, $"logs-{stamp}.log");
            if (File.Exists(archive) || Directory.Exists(archive))
                archive = Path.Combine(paths.Personal, $"logs-{stamp}-{Guid.NewGuid():N}.log");
            File.Move(current, archive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Opening the existing log is fine if another process is holding it.
        }
    }

    public static void RunCleanup(AppPaths paths, bool rotateLauncherLog = true)
    {
        if (rotateLauncherLog) RotateLauncherLog(paths);
        PruneLauncherArchives(paths, static () => { });
    }

    internal static bool IsLauncherArchiveName(string name) => LauncherArchiveName().IsMatch(name);
    internal static bool IsSteamSpikeLogName(string name) => SteamSpikeLogName().IsMatch(name);

    internal static void PruneLauncherArchives(AppPaths paths, Action checkpoint)
    {
        var kept = new List<FileInfo>();
        var cutoff = DateTime.UtcNow - DiagnosticRetention;
        foreach (var path in LauncherStorageCleanup.ReadEntries(paths, paths.Personal, checkpoint))
        {
            var name = Path.GetFileName(path);
            if ((!IsLauncherArchiveName(name) && !IsSteamSpikeLogName(name)) ||
                !MaintenancePath.TryRead(paths, path, out var attributes) ||
                (attributes & FileAttributes.Directory) != 0) continue;
            var file = new FileInfo(path);
            try
            {
                if (file.LastWriteTimeUtc < cutoff)
                {
                    checkpoint();
                    MaintenancePath.TryDelete(paths, path, directory: false);
                }
                else kept.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        EnforceSizeBudget(kept, LauncherLogBudgetBytes, checkpoint,
            path => MaintenancePath.TryDelete(paths, path, directory: false));
    }

    /// <summary>For a proven library instance whose game session has ended.</summary>
    internal static void RetainRecentSessionDiagnostics(string gameDirectory, Action? checkpoint = null)
    {
        checkpoint ??= static () => { };
        if (!IsOrdinary(gameDirectory, directory: true)) return;
        DiscardGameDebugLogs(Path.Combine(gameDirectory, "logs"), checkpoint);

        var kept = new List<FileInfo>();
        var cutoff = DateTime.UtcNow - DiagnosticRetention;
        foreach (var name in SessionDiagnosticDirectories)
        {
            var directory = Path.Combine(gameDirectory, name);
            foreach (var path in ReadEntries(directory, checkpoint))
            {
                if (!IsOrdinary(path, directory: false)) continue;
                var fileName = Path.GetFileName(path);
                if (fileName.Equals("latest.log", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals(E4steamPreviousLogName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var crash = name == "crash-reports" && CrashReportName().IsMatch(fileName);
                if (!crash && !fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) &&
                    !fileName.EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase)) continue;
                var file = new FileInfo(path);
                try
                {
                    if (file.LastWriteTimeUtc < cutoff)
                    {
                        checkpoint();
                        DeleteFile(path);
                    }
                    else if (!crash) kept.Add(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        EnforceSizeBudget(kept, InstanceLogBudgetBytes, checkpoint, DeleteFile);
    }

    private static void EnforceSizeBudget(IEnumerable<FileInfo> files, long budgetBytes, Action checkpoint,
        Action<string> delete)
    {
        long used = 0;
        foreach (var file in files.OrderByDescending(file => file.LastWriteTimeUtc))
        {
            checkpoint();
            try
            {
                used += file.Length;
                if (used > budgetBytes) delete(file.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void DiscardGameDebugLogs(string logsDirectory, Action checkpoint)
    {
        checkpoint();
        if (!IsOrdinary(logsDirectory, directory: true)) return;
        var current = Path.Combine(logsDirectory, "debug.log");
        if (IsHeldByTheGame(current)) return;

        // The owner keeps only e4steam diagnostics from these generated logs.
        // A redirected file is never read or removed.
        var sources = E4steamLogLines.DebugLogsOldestFirst(logsDirectory)
            .Where(path => IsOrdinary(path, directory: false)).ToArray();
        if (sources.Length == 0 || sources.Any(IsHeldByTheGame)) return;
        if (!TryKeepE4steamLines(logsDirectory, sources, checkpoint)) return;
        foreach (var source in sources)
        {
            checkpoint();
            DeleteFile(source);
        }
    }

    private static bool TryKeepE4steamLines(string logsDirectory, string[] sources, Action checkpoint)
    {
        try
        {
            var lines = E4steamLogLines.Read(sources, E4steamKeptChars, checkpoint);
            if (lines.Count == 0) return true;

            var record = Path.Combine(logsDirectory, E4steamPreviousLogName);
            var ended = File.GetLastWriteTime(sources[^1]);
            var all = new List<string>();
            if (File.Exists(record))
            {
                if (!IsOrdinary(record, directory: false)) return false;
                all.AddRange(E4steamLogLines.ReadTail(record, E4steamKeptChars, checkpoint));
            }
            all.Add($"[launcher] --- session last written {ended:yyyy-MM-dd HH:mm:ss} ---");
            all.AddRange(lines);

            long size = all.Sum(line => (long)line.Length + 1);
            var skip = 0;
            while (size > E4steamKeptChars && skip < all.Count)
                size -= all[skip++].Length + 1;

            checkpoint();
            if (!IsOrdinary(logsDirectory, directory: true)) return false;
            if (File.Exists(record) && !IsOrdinary(record, directory: false)) return false;
            AtomicFile.WriteAllText(record, string.Join(Environment.NewLine, all.Skip(skip)) + Environment.NewLine);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Preserve the source logs if we cannot retain their e4steam lines.
            return false;
        }
    }

    private static bool IsHeldByTheGame(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            if (!IsOrdinary(path, directory: false)) return true;
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    internal static void PrunePortableExtractionCache(AppPaths paths, Action checkpoint)
    {
        var bundles = Path.Combine(paths.Launcher, "DotNetBundles");
        foreach (var executableName in new[] { ExecutableName, "Minecraft" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            checkpoint();
            var root = Path.Combine(bundles, executableName);
            if (!IsOrdinary(root, directory: true)) continue;
            var inUse = FindExtractionDirectoriesInUse(root, executableName, checkpoint);
            // A process without a mapped module in its own extraction root
            // may still need it on the next launch.
            if (executableName.Equals(ExecutableName, StringComparison.OrdinalIgnoreCase) && inUse is not { Count: > 0 })
                continue;
            PruneExtractionCache(root, inUse, checkpoint);
        }
    }

    /// <summary>Keeps the current bundle and any other bundle a running copy still uses.</summary>
    internal static void PruneExtractionCache(string root, IReadOnlySet<string>? directoriesInUse,
        Action? checkpoint = null)
    {
        if (directoriesInUse is null || !IsOrdinary(root, directory: true)) return;
        checkpoint ??= static () => { };
        var inUse = new HashSet<string>(directoriesInUse.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var path in ReadEntries(root, checkpoint))
        {
            if (inUse.Contains(Path.GetFullPath(path)) || !IsOrdinary(path, directory: true)) continue;
            var pending = new Stack<(string Path, bool Visited)>();
            pending.Push((path, false));
            while (pending.TryPop(out var candidate))
            {
                checkpoint();
                if (MaintenancePath.IsProtectedAssetName(Path.GetFileName(candidate.Path))) continue;
                if (candidate.Visited) DeleteEmptyDirectory(candidate.Path);
                else if (IsOrdinary(candidate.Path, directory: true))
                {
                    pending.Push((candidate.Path, true));
                    foreach (var child in ReadEntries(candidate.Path, checkpoint)) pending.Push((child, false));
                }
                else DeleteFile(candidate.Path);
            }
        }
        checkpoint();
        DeleteEmptyDirectory(root);
    }

    private static HashSet<string>? FindExtractionDirectoriesInUse(string root, string processName, Action checkpoint)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var processes = Process.GetProcessesByName(processName);
        try
        {
            foreach (var process in processes)
            {
                checkpoint();
                try
                {
                    foreach (ProcessModule module in process.Modules)
                    {
                        checkpoint();
                        var full = Path.GetFullPath(module.FileName);
                        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        var relative = full[prefix.Length..];
                        var separator = relative.IndexOf(Path.DirectorySeparatorChar);
                        var name = separator < 0 ? relative : relative[..separator];
                        if (name.Length > 0) directories.Add(Path.Combine(root, name));
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                                    or NotSupportedException)
                {
                    if (!process.HasExited) return null;
                }
            }
            return directories;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static IEnumerable<string> ReadEntries(string path, Action checkpoint)
    {
        checkpoint();
        if (!IsOrdinary(path, directory: true)) yield break;
        using var entries = TryReadEntries(path);
        if (entries is null) yield break;
        while (TryMoveNext(entries, checkpoint)) yield return entries.Current;
    }

    private static IEnumerator<string>? TryReadEntries(string path)
    {
        try { return Directory.EnumerateFileSystemEntries(path).GetEnumerator(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool TryMoveNext(IEnumerator<string> entries, Action checkpoint)
    {
        checkpoint();
        try { return entries.MoveNext(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsOrdinary(string path, bool directory)
    {
        try
        {
            var leaf = true;
            for (var part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
            {
                var attributes = File.GetAttributes(part);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    ((attributes & FileAttributes.Directory) != 0) != (leaf ? directory : true)) return false;
                leaf = false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void DeleteFile(string path)
    {
        if (IsOrdinary(path, directory: false))
            PortableRuntimeCache.TryDeleteOrdinaryEntry(Path.GetFullPath(path), directory: false);
    }

    private static void DeleteEmptyDirectory(string path)
    {
        if (IsOrdinary(path, directory: true))
            PortableRuntimeCache.TryDeleteOrdinaryEntry(Path.GetFullPath(path), directory: true);
    }

    [GeneratedRegex(@"^logs-[0-9]{8}-[0-9]{6}(-[0-9a-f]{32})?\.log$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LauncherArchiveName();

    [GeneratedRegex(@"^steam-spike-[0-9]{8}-[0-9]{6}\.log$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamSpikeLogName();

    [GeneratedRegex(@"^crash-[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}\.[0-9]{2}\.[0-9]{2}-(client|server)\.txt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CrashReportName();
}
