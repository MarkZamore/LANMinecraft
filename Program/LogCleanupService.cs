using System.Diagnostics;
using System.IO;
using System.Text;

namespace Minecraft;

public static class LogCleanupService
{
    private static readonly TimeSpan DiagnosticRetention = TimeSpan.FromDays(7);

    /// <summary>
    /// The running executable's name. It is both the single-file extraction
    /// directory and the process name, and it changed when the launcher was
    /// renamed - which is why the previous name's cache was never swept.
    /// </summary>
    private static readonly string ExecutableName =
        Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "LANMinecraft";

    /// <summary>
    /// Age alone does not bound anything: a week of modded sessions is
    /// hundreds of megabytes of logs. Each store also gets a size budget, and
    /// the oldest files go first once it is exceeded.
    /// </summary>
    private const long LauncherLogBudgetBytes = 16L * 1024 * 1024;
    private const long InstanceLogBudgetBytes = 64L * 1024 * 1024;

    /// <summary>The game's DEBUG copy; retention here is launcher diagnostics policy.</summary>
    private static readonly string[] DiscardedGameLogNames = ["debug.log"];
    private static readonly string[] SessionDiagnosticDirectories = ["logs", "debug", "crash-reports"];
    private const string DiscardedGameLogPattern = "debug-*.log.gz";

    /// <summary>The e4steam lines of the last debug.log, kept after the log itself is discarded.</summary>
    internal const string E4steamPreviousLogName = "e4steam-previous.log";

    /// <summary>A whole evening of e4steam lines fits; a runaway loop of them does not fill the budget.</summary>
    private const int E4steamKeptChars = 2 * 1024 * 1024;

    /// <summary>
    /// Moves the last run's log aside so this run writes a fresh one. It has
    /// to happen before the logger opens the file, which is why it is not
    /// left to <see cref="RunCleanup"/> with the rest of the housekeeping.
    /// </summary>
    public static void RotateLauncherLog(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        CleanupLauncherLog(paths.LogFile);
    }

    /// <param name="rotateLauncherLog">False once the logger holds the file:
    /// the log is moved aside before a run, never during one.</param>
    public static void RunCleanup(AppPaths paths, bool rotateLauncherLog = true)
    {
        if (rotateLauncherLog) CleanupLauncherLog(paths.LogFile);
        CleanupMinecraftGeneratedFiles(paths.Personal);
        CleanupInstanceGeneratedFiles(paths.Instances);
        try
        {
            PackInstanceService.CleanupEmptyWorldPlaceholders(paths.Worlds);
        }
        catch
        {
        }
        try
        {
            PackInstanceService.PruneOldConflictSnapshots(paths.PackConflicts);
        }
        catch
        {
        }
        CleanupDotNetExtractionCache();
        CleanupRenamedExtractionRoots();
    }

    private static void CleanupLauncherLog(string launcherLogPath)
    {
        var directory = Path.GetDirectoryName(launcherLogPath);
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        try
        {
            var current = new FileInfo(launcherLogPath);
            if (current.Exists && current.Length > 0)
            {
                var stamp = current.LastWriteTimeUtc.ToString(
                    "yyyyMMdd-HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture);
                var archivedPath = Path.Combine(directory, $"logs-{stamp}.log");
                if (File.Exists(archivedPath))
                {
                    archivedPath = Path.Combine(
                        directory,
                        $"logs-{stamp}-{Guid.NewGuid():N}.log");
                }
                File.Move(launcherLogPath, archivedPath);
            }
            else
            {
                DeleteFile(launcherLogPath);
            }
        }
        catch
        {
            // A second running launcher may still own the file. Keeping it is safer
            // than deleting diagnostic data.
        }

        var cutoff = DateTime.UtcNow - DiagnosticRetention;
        foreach (var archivedLog in Directory.EnumerateFiles(directory, "logs-*.log", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(archivedLog) < cutoff)
                {
                    DeleteFile(archivedLog);
                }
            }
            catch
            {
            }
        }

        EnforceSizeBudget(
            EnumerateFilesSafe(directory, "logs-*.log", SearchOption.TopDirectoryOnly),
            LauncherLogBudgetBytes);
    }

    /// <summary>
    /// Deletes the oldest files until the rest fit the budget. Nothing else
    /// bounds a store whose files all arrived within the retention window.
    /// </summary>
    private static void EnforceSizeBudget(IEnumerable<FileInfo> files, long budgetBytes)
    {
        var ordered = files
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        var used = 0L;
        foreach (var file in ordered)
        {
            used += file.Length;
            if (used > budgetBytes) DeleteFile(file.FullName);
        }
    }

    private static FileInfo[] EnumerateFilesSafe(string directory, string pattern, SearchOption option)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern, option)
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void CleanupMinecraftGeneratedFiles(string personalRoot)
    {
        Directory.CreateDirectory(personalRoot);
        foreach (var directoryName in new[] { "BootstrapDownloads", "Build", "Temp" })
        {
            DeleteDirectory(Path.Combine(personalRoot, directoryName));
        }

        CleanupUpdateArtifacts(Path.Combine(personalRoot, "Updates"));

        var logsRoot = Path.Combine(personalRoot, "Logs");
        if (!Directory.Exists(logsRoot)) return;
        foreach (var file in Directory.EnumerateFiles(logsRoot, "*", SearchOption.AllDirectories)
                     .Select(path => new FileInfo(path))
                     .Where(ShouldDeleteExpiredGeneratedFile)
                     .ToList())
        {
            DeleteFile(file.FullName);
        }
    }

    private static void CleanupUpdateArtifacts(string updatesRoot)
    {
        if (!Directory.Exists(updatesRoot)) return;
        if (UpdateService.IsInstallationInProgress(updatesRoot)) return;

        var journalPath = Path.Combine(updatesRoot, UpdateService.InstallJournalFileName);
        var hasJournal = File.Exists(journalPath);

        foreach (var fileName in new[]
                 {
                     $"{UpdateService.ExecutableAssetName}.download",
                     $"{UpdateService.DeltaPatchAssetName}.download",
                     $"{UpdateService.ExecutableAssetName}.new",
                     UpdateService.InstallCandidateFileName,
                     UpdateService.InstallScriptFileName,
                     UpdateService.InstallRestartRequestFileName,
                     "update.log"
                 })
        {
            DeleteFile(Path.Combine(updatesRoot, fileName));
        }

        if (!hasJournal)
        {
            DeleteFile(Path.Combine(updatesRoot, UpdateService.InstallBackupFileName));
        }

        foreach (var file in Directory.EnumerateFiles(updatesRoot, "*.bsdiff.download", SearchOption.TopDirectoryOnly))
        {
            DeleteFile(file);
        }

        var readyDirectory = Path.Combine(updatesRoot, "Ready");
        var hasPreparedUpdate =
            File.Exists(Path.Combine(readyDirectory, UpdateService.ExecutableAssetName)) &&
            File.Exists(Path.Combine(readyDirectory, UpdateService.ManifestAssetName)) ||
            File.Exists(Path.Combine(updatesRoot, UpdateService.ExecutableAssetName)) &&
            File.Exists(Path.Combine(updatesRoot, UpdateService.ManifestAssetName));
        if (hasJournal && !hasPreparedUpdate)
        {
            DeleteFile(journalPath);
            DeleteFile(Path.Combine(updatesRoot, UpdateService.InstallBackupFileName));
        }
        TryDeleteDirectoryIfEmpty(updatesRoot);
    }

    private static bool ShouldDeleteExpiredGeneratedFile(FileInfo file)
    {
        var directory = file.DirectoryName ?? string.Empty;
        if (directory.Contains("dynamic-data-pack-cache", StringComparison.OrdinalIgnoreCase) ||
            directory.Contains("dynamic-resource-pack-cache", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return file.LastWriteTimeUtc < DateTime.UtcNow - DiagnosticRetention &&
               (file.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
                file.Extension.Equals(".gz", StringComparison.OrdinalIgnoreCase) ||
                directory.Contains("crash-reports", StringComparison.OrdinalIgnoreCase) ||
                directory.Contains("debug", StringComparison.OrdinalIgnoreCase));
    }

    private static void CleanupInstanceGeneratedFiles(string instancesRoot)
    {
        if (!Directory.Exists(instancesRoot)) return;
        var generatedDirectories = new[]
        {
            ".mixin.out",
            "downloads",
            "dynamic-data-pack-cache",
            "dynamic-resource-pack-cache"
        };
        foreach (var instanceDir in Directory.EnumerateDirectories(instancesRoot, "*", SearchOption.TopDirectoryOnly))
        {
            foreach (var directoryName in generatedDirectories)
            {
                DeleteDirectory(Path.Combine(instanceDir, directoryName));
            }
            RetainRecentSessionDiagnostics(instanceDir);
            try
            {
                PackInstanceService.CleanupDisposableInstancePlaceholders(instanceDir);
            }
            catch
            {
            }
        }
    }

    internal static void RetainRecentSessionDiagnostics(string gameDirectory)
    {
        var cutoff = DateTime.UtcNow - DiagnosticRetention;
        foreach (var directoryName in SessionDiagnosticDirectories)
        {
            var directory = Path.Combine(gameDirectory, directoryName);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        DeleteFile(path);
                    }
                }
                catch
                {
                }
            }
        }

        DiscardGameDebugLogs(Path.Combine(gameDirectory, "logs"));
        EnforceSizeBudget(
            SessionDiagnosticDirectories
                .Select(name => Path.Combine(gameDirectory, name))
                .Where(Directory.Exists)
                .SelectMany(directory => EnumerateFilesSafe(directory, "*", SearchOption.AllDirectories)),
            InstanceLogBudgetBytes);
    }

    /// <summary>
    /// The game's DEBUG copy of a session, left over from before the launcher
    /// configured logging - tens of megabytes that latest.log and the crash
    /// report already cover.
    /// </summary>
    private static void DiscardGameDebugLogs(string logsDirectory)
    {
        if (!Directory.Exists(logsDirectory)) return;
        // A launcher restarted over a game still playing sweeps its instance
        // too; that session's debug log is still being written, and neither
        // its e4steam lines nor the file itself are finished yet.
        if (IsHeldByTheGame(Path.Combine(logsDirectory, "debug.log"))) return;
        KeepE4steamLines(logsDirectory);
        foreach (var name in DiscardedGameLogNames)
        {
            DeleteFile(Path.Combine(logsDirectory, name));
        }
        foreach (var file in EnumerateFilesSafe(logsDirectory, DiscardedGameLogPattern, SearchOption.TopDirectoryOnly))
        {
            DeleteFile(file.FullName);
        }
    }

    /// <summary>
    /// Writes down what e4steam said before the debug copies that hold it go;
    /// see <see cref="E4steamLogLines"/>. Nothing else in those copies is kept.
    /// </summary>
    /// <remarks>
    /// Sessions are added to the record rather than replacing it: the one worth
    /// reading is the one a guest fell out of, and by the time a report is sent
    /// the players have usually restarted the game once or twice to try again.
    /// The oldest lines go first once the record is full.
    /// </remarks>
    private static void KeepE4steamLines(string logsDirectory)
    {
        var sources = E4steamLogLines.DebugLogsOldestFirst(logsDirectory);
        if (sources.Count == 0) return;
        try
        {
            var lines = E4steamLogLines.Read(sources, E4steamKeptChars);
            if (lines.Count == 0) return;

            var record = Path.Combine(logsDirectory, E4steamPreviousLogName);
            var ended = File.GetLastWriteTime(sources[^1]);
            var all = new List<string>();
            if (File.Exists(record)) all.AddRange(E4steamLogLines.ReadTail(record, E4steamKeptChars));
            all.Add($"[launcher] --- session last written {ended:yyyy-MM-dd HH:mm} ---");
            all.AddRange(lines);

            long size = all.Sum(line => (long)line.Length + 1);
            var skip = 0;
            while (size > E4steamKeptChars && skip < all.Count)
            {
                size -= all[skip].Length + 1;
                skip++;
            }
            File.WriteAllLines(record, all.Skip(skip), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The session still happened; only this record of it is missing.
        }
    }

    /// <summary>Whether the game still has the file open for writing.</summary>
    private static bool IsHeldByTheGame(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void CleanupDotNetExtractionCache()
    {
        var root = GetExtractionRoot();
        if (!Directory.Exists(root)) return;
        PruneExtractionCache(root, FindExtractionDirectoriesInUse(root, ExecutableName));
    }

    /// <summary>Keeps the current bundle for the next launch and any other
    /// bundle a running copy still uses. An unreadable process means no sweep.</summary>
    internal static void PruneExtractionCache(string root, IReadOnlySet<string>? directoriesInUse)
    {
        if (directoriesInUse is null || !Directory.Exists(root)) return;
        var inUse = new HashSet<string>(directoriesInUse.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (inUse.Contains(Path.GetFullPath(directory))) continue;
            DeleteDirectory(directory);
        }
        TryDeleteDirectoryIfEmpty(root);
    }

    private static HashSet<string>? FindExtractionDirectoriesInUse(string root, string processName)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootWithSlash = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    foreach (ProcessModule module in process.Modules)
                    {
                        var directory = GetExtractionDirectory(root, rootWithSlash, module.FileName);
                        if (directory is not null) directories.Add(directory);
                    }
                }
                catch
                {
                    if (!process.HasExited) return null;
                }
            }
        }

        return directories;
    }

    private static string? GetExtractionDirectory(string root, string rootWithSlash, string? modulePath)
    {
        if (string.IsNullOrWhiteSpace(modulePath)) return null;
        var full = Path.GetFullPath(modulePath);
        if (!full.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase)) return null;
        var relative = full[rootWithSlash.Length..];
        var separator = relative.IndexOf(Path.DirectorySeparatorChar);
        var directoryName = separator < 0 ? relative : relative[..separator];
        return string.IsNullOrWhiteSpace(directoryName) ? null : Path.GetFullPath(Path.Combine(root, directoryName));
    }

    private static string GetExtractionBaseDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), ".net")
            : Path.GetFullPath(configured);
    }

    private static string GetExtractionRoot() => Path.Combine(GetExtractionBaseDirectory(), ExecutableName);

    /// <summary>
    /// The single-file host extracts under a directory named after the
    /// executable, so a rename leaves the whole previous tree behind.
    /// </summary>
    private static void CleanupRenamedExtractionRoots()
    {
        var dotNetRoot = GetExtractionBaseDirectory();
        if (!Directory.Exists(dotNetRoot)) return;
        foreach (var directory in Directory.EnumerateDirectories(dotNetRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, ExecutableName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(name, "Minecraft", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            PruneExtractionCache(directory, FindExtractionDirectoriesInUse(directory, name));
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        catch
        {
        }
    }
}
