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

    /// <summary>The game's own DEBUG copy of a session; see GameLogConfigurationService.</summary>
    private static readonly string[] DiscardedGameLogNames = ["debug.log"];
    private static readonly string[] SessionDiagnosticDirectories = ["logs", "debug", "crash-reports"];
    private const string DiscardedGameLogPattern = "debug-*.log.gz";

    public static void RunCleanup(AppPaths paths)
    {
        CleanupLauncherLog(paths.LogFile);
        CleanupMinecraftGeneratedFiles(paths.Personal);
        CleanupInstanceGeneratedFiles(paths.Instances);
        try
        {
            PackInstanceService.CleanupEmptyWorldPlaceholders(paths.Worlds);
        }
        catch
        {
        }
        CleanupDotNetExtractionCache();
        CleanupRenamedExtractionRoots();
    }

    public static void ScheduleCurrentExtractionCleanup(AppPaths paths, int processId)
    {
        var extractionDir = FindCurrentExtractionDirectory();
        if (string.IsNullOrWhiteSpace(extractionDir)) return;

        try
        {
            var escapedExtractionDir = extractionDir.Replace("'", "''", StringComparison.Ordinal);
            var command = "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
                          $"$processId = {processId}\r\n" +
                          $"$extractionDir = '{escapedExtractionDir}'\r\n" +
                          "function Test-ExtractionInUse {\r\n" +
                          $"  foreach ($candidate in @(Get-Process -Name '{ExecutableName}' -ErrorAction SilentlyContinue)) {{\r\n" +
                          "    if ($candidate.Id -eq $processId) { continue }\r\n" +
                          "    try {\r\n" +
                          "      foreach ($module in @($candidate.Modules)) {\r\n" +
                          "        if ($module.FileName.StartsWith($extractionDir, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }\r\n" +
                          "      }\r\n" +
                          "    } catch { return $true }\r\n" +
                          "  }\r\n" +
                          "  return $false\r\n" +
                          "}\r\n" +
                          "Wait-Process -Id $processId -Timeout 120\r\n" +
                          "Start-Sleep -Seconds 15\r\n" +
                          "if (Test-ExtractionInUse) { exit 0 }\r\n" +
                          "if (-not (Get-Process -Id $processId)) {\r\n" +
                          "  for ($attempt = 0; $attempt -lt 20 -and (Test-Path -LiteralPath $extractionDir); $attempt++) {\r\n" +
                          "    Start-Sleep -Milliseconds 500\r\n" +
                          "    Remove-Item -LiteralPath $extractionDir -Recurse -Force\r\n" +
                          "  }\r\n" +
                          "}\r\n" +
                          "$extractionParent = Split-Path -Parent $extractionDir\r\n" +
                          "if ((Test-Path -LiteralPath $extractionParent) -and -not (Get-ChildItem -LiteralPath $extractionParent -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $extractionParent -Force }\r\n";
            var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
                WorkingDirectory = paths.Personal,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch
        {
        }
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
        foreach (var name in DiscardedGameLogNames)
        {
            DeleteFile(Path.Combine(logsDirectory, name));
        }
        foreach (var file in EnumerateFilesSafe(logsDirectory, DiscardedGameLogPattern, SearchOption.TopDirectoryOnly))
        {
            DeleteFile(file.FullName);
        }
    }

    private static void CleanupDotNetExtractionCache()
    {
        var root = GetExtractionRoot();
        if (!Directory.Exists(root)) return;
        var inUse = FindExtractionDirectoriesInUse(root);
        if (inUse is null) return;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (inUse.Contains(Path.GetFullPath(directory))) continue;
            DeleteDirectory(directory);
        }
        TryDeleteDirectoryIfEmpty(root);
    }

    private static HashSet<string>? FindExtractionDirectoriesInUse(string root)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootWithSlash = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcessesByName(ExecutableName))
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

    private static string? FindCurrentExtractionDirectory()
    {
        var root = GetExtractionRoot();
        var rootWithSlash = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        try
        {
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                var directory = GetExtractionDirectory(root, rootWithSlash, module.FileName);
                if (directory is not null) return directory;
            }
        }
        catch
        {
        }
        return null;
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

    private static string GetExtractionRoot() =>
        Path.Combine(Path.GetTempPath(), ".net", ExecutableName);

    /// <summary>
    /// The single-file host extracts under a directory named after the
    /// executable, so a rename leaves the whole previous tree behind.
    /// </summary>
    private static void CleanupRenamedExtractionRoots()
    {
        var dotNetRoot = Path.Combine(Path.GetTempPath(), ".net");
        if (!Directory.Exists(dotNetRoot)) return;
        foreach (var directory in Directory.EnumerateDirectories(dotNetRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, ExecutableName, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith("Minecraft", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            DeleteDirectory(directory);
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
