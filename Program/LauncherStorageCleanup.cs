using System.IO;

namespace Minecraft;

internal static class LauncherStorageCleanup
{
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromDays(1);

    private static readonly HashSet<string> ServiceEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "Packs", "Launcher", "Personal", "Worlds"
    };

    // Runtime and DotNetBundles are created by the native host before WPF starts.
    private static readonly HashSet<string> LauncherEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "Temp", "DotNetBundles", "Runtime", "Runtimes", "JavaRuntimes", "Shared", "Steam", "ManagedComponents",
        "Library", "IdentityAdapters", "NoticeOverlay", "WindowPresentation", "LaunchArguments", "NativeInterop",
        "ComponentCatalog", "LocalAI", "PowerShell", "bootstrap.log", ".shared-runtime.lock", LauncherMaintenance.CompletionFileName
    };

    private static readonly HashSet<string> PersonalEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "Instances", "PackConflicts", "Backups", "WaypointConflicts", "ProfileTransactions", "BugReports", "Temp",
        "Home", "ClientSessions", "PackRenames", "Transfers", "Updates", "Logs", "settings.json", "pack-hashes.json",
        "pack-weights.json", "memory-measurements.json", "window-placement.json", "minecraft-window-placement.json",
        "skin-profiles.properties", "legacy-skin-profiles.properties", "identity-profiles.properties",
        "legacy-identity-profiles.properties", "waypoint-sync.json", "transfer-pacing.json", "logs.log",
        ".terrafirma-rename.lock", ".terrafirma-rename.pending", ".deployment-prep.lock", "UUID.json", "steam-identity.json"
    };

    private static readonly HashSet<string> PlayerEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "saves", "worlds", "screenshots", "backups", "options.txt", "servers.dat", "level.dat", "level.dat_old"
    };

    internal static int Run(AppPaths paths, Logger? logger, Action checkpoint, Func<string, bool> isManagedPack)
    {
        var cutoff = DateTime.UtcNow - AbandonedAfter;
        DeprecatedFileCleanupService.Run(paths, logger, checkpoint, isManagedPack);
        StructureCleanupService.Run(paths, logger, checkpoint, isManagedPack);
        var removed = SweepRoot(paths, paths.Service, ServiceEntries, cutoff, checkpoint);
        removed += SweepRoot(paths, paths.Launcher, LauncherEntries, cutoff, checkpoint);
        removed += SweepRoot(paths, paths.Personal, PersonalEntries, cutoff, checkpoint, isPersonalRoot: true);
        removed += PortablePackSyncService.CleanupAbandonedPlans(paths, paths.Launcher, cutoff,
            _ => checkpoint(), CancellationToken.None);
        removed += SweepAbandonedUpdateTransfers(paths, cutoff, checkpoint);
        LogCleanupService.PruneLauncherArchives(paths, checkpoint);
        LogCleanupService.PrunePortableExtractionCache(paths, checkpoint);
        JavaLaunchArgumentFile.CleanupStale(paths, checkpoint);
        PortableRuntimeCache.CleanupIdle(paths, checkpoint);
        if (removed > 0) logger?.Info($"Launcher storage cleanup removed {removed} entries.");
        return removed;
    }

    private static int SweepAbandonedUpdateTransfers(AppPaths paths, DateTime cutoff, Action checkpoint)
    {
        var directory = Path.Combine(paths.Personal, "Updates");
        var journal = Path.Combine(directory, UpdateService.InstallJournalFileName);
        var removed = 0;
        foreach (var path in ReadEntries(paths, directory, checkpoint))
        {
            if (!IsUpdateTransferName(Path.GetFileName(path)) ||
                !MaintenancePath.TryRead(paths, path, out var attributes) ||
                (attributes & FileAttributes.Directory) != 0 || !IsOlderThan(path, directory: false, cutoff)) continue;
            checkpoint();
            if (MayContainUpdateJournal(journal)) break;
            if (MaintenancePath.TryDelete(paths, path, directory: false)) removed++;
        }
        return removed;
    }

    private static bool IsUpdateTransferName(string name)
    {
        if (name.Equals(UpdateService.ExecutableAssetName + ".download", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(UpdateService.ExecutableAssetName + ".new", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(UpdateService.DeltaPatchAssetName + ".download", StringComparison.OrdinalIgnoreCase)) return true;

        const string prefix = "LANMinecraft.from-";
        const string suffix = ".bsdiff.download";
        return name.Length > prefix.Length + suffix.Length &&
               name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
               name[prefix.Length..^suffix.Length].All(char.IsAsciiDigit);
    }

    private static bool MayContainUpdateJournal(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static int SweepRoot(AppPaths paths, string root, HashSet<string> kept, DateTime cutoff,
        Action checkpoint, bool isPersonalRoot = false)
    {
        var removed = 0;
        foreach (var entry in ReadEntries(paths, root, checkpoint))
        {
            var name = Path.GetFileName(entry);
            if (kept.Contains(name) || IsProtected(name) ||
                name.StartsWith(".pack-sync-stage", StringComparison.OrdinalIgnoreCase) ||
                isPersonalRoot && (LogCleanupService.IsLauncherArchiveName(name) || LogCleanupService.IsSteamSpikeLogName(name)))
                continue;

            // AtomicFile publishes these by rename. Recent temporary files can
            // still belong to a writer whose operation does not involve Play.
            DateTime? expiry = IsAtomicTemporaryFile(name) ? cutoff : null;
            removed += DeleteTree(paths, entry, checkpoint, expiry);
        }
        return removed;
    }

    private static bool IsAtomicTemporaryFile(string name) => name.Length > 38 &&
        name[0] == '.' && name[^37] == '.' && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParseExact(name.AsSpan(name.Length - 36, 32), "N", out _);

    private static bool IsProtected(string name) =>
        MaintenancePath.IsProtectedAssetName(name) || PlayerEntries.Contains(name);

    internal static int DeleteTree(AppPaths paths, string path, Action checkpoint, DateTime? cutoff = null)
    {
        var removed = 0;
        var pending = new Stack<(string Path, bool Visited)>();
        pending.Push((path, false));
        while (pending.TryPop(out var candidate))
        {
            checkpoint();
            if (!MaintenancePath.TryRead(paths, candidate.Path, out var attributes) ||
                IsProtected(Path.GetFileName(candidate.Path))) continue;
            var directory = (attributes & FileAttributes.Directory) != 0;
            if (directory && !candidate.Visited)
            {
                pending.Push((candidate.Path, true));
                foreach (var entry in ReadEntries(paths, candidate.Path, checkpoint)) pending.Push((entry, false));
                continue;
            }

            if (cutoff is not null && !IsOlderThan(candidate.Path, directory, cutoff.Value)) continue;
            checkpoint();
            if (MaintenancePath.TryDelete(paths, candidate.Path, directory)) removed++;
        }
        return removed;
    }

    internal static IEnumerable<string> ReadEntries(AppPaths paths, string directory, Action checkpoint)
    {
        checkpoint();
        if (!MaintenancePath.TryRead(paths, directory, out var attributes) ||
            (attributes & FileAttributes.Directory) == 0) yield break;

        using var entries = TryOpenEntries(directory);
        if (entries is null) yield break;
        while (TryMoveNext(entries, checkpoint)) yield return entries.Current;
    }

    private static IEnumerator<string>? TryOpenEntries(string path)
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

    private static bool IsOlderThan(string path, bool directory, DateTime cutoff)
    {
        try
        {
            return (directory ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path)) < cutoff;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
