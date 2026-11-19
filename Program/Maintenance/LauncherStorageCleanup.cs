using System.IO;

namespace Minecraft;

internal enum LauncherStorageCleanupStep
{
    ServiceRoot,
    LauncherRoot,
    PersonalRoot,
    LauncherArchives,
    AbandonedPlans,
    UpdateTransfers,
    DeprecatedFiles,
    PrivateRuntimes,
    PortableExtraction,
    LaunchArguments,
    PortableRuntimeCache,
    RuntimeComponents,
    DesktopBootstrap
}

internal static class LauncherStorageCleanup
{
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromDays(1);

    private static readonly HashSet<string> PlayerEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "saves", "worlds", "screenshots", "backups", "options.txt", "servers.dat", "level.dat", "level.dat_old"
    };

    // Run before the session opens runtime paths. A deferred import leaves the
    // mark absent so the next launch can retry without a version hop.
    internal static bool ReconcileLayout(AppPaths paths, Logger? logger = null, Func<bool>? mayHaveGame = null,
        Action? checkpoint = null)
    {
        checkpoint?.Invoke();
        switch (LauncherStorageLayout.ReadSchema(paths))
        {
            case LauncherStorageSchema.Current:
                paths.UseCanonicalPackRuntimes();
                return true;
            case LauncherStorageSchema.Unsupported:
                logger?.Warn("Storage cleanup deferred because the layout marker could not be recognized.");
                return false;
        }

        var packRuntimesReady = PackRuntimeFolderMigration.TryApply(paths, logger, mayHaveGame, checkpoint);
        var launcherRuntimeReady = PortableRuntimeCache.TryImportLegacy(paths, checkpoint);
        if (!packRuntimesReady || !launcherRuntimeReady) return false;
        checkpoint?.Invoke();
        if (LauncherStorageLayout.TryRecordCurrentSchema(paths)) return true;
        logger?.Warn("Could not record the completed storage layout; it will be checked again on the next launch.");
        return false;
    }

    internal static int Run(AppPaths paths, Logger? logger, Action checkpoint, Func<string, bool> isManagedPack)
    {
        if (LauncherStorageLayout.ReadSchema(paths) == LauncherStorageSchema.Unsupported) return 0;
        var removed = Enum.GetValues<LauncherStorageCleanupStep>()
            .Sum(step => RunStep(paths, logger, step, checkpoint, isManagedPack));
        if (removed > 0) logger?.Info($"Launcher storage cleanup removed {removed} entries.");
        return removed;
    }

    internal static bool CanRunDuringGame(LauncherStorageCleanupStep step) => step is
        LauncherStorageCleanupStep.ServiceRoot or LauncherStorageCleanupStep.LauncherRoot or
        LauncherStorageCleanupStep.PersonalRoot or LauncherStorageCleanupStep.LauncherArchives or
        LauncherStorageCleanupStep.AbandonedPlans or LauncherStorageCleanupStep.RuntimeComponents or
        LauncherStorageCleanupStep.DesktopBootstrap;

    internal static int RunStep(AppPaths paths, Logger? logger, LauncherStorageCleanupStep step,
        Action checkpoint, Func<string, bool> isManagedPack, string? resumeAfter = null, Action<string>? completedEntry = null)
    {
        checkpoint();
        var cutoff = DateTime.UtcNow - AbandonedAfter;
        switch (step)
        {
            case LauncherStorageCleanupStep.ServiceRoot:
                return SweepRoot(paths, paths.Service, LauncherStorageLayout.ServiceEntries, cutoff, checkpoint, resumeAfter, completedEntry);
            case LauncherStorageCleanupStep.LauncherRoot:
                return SweepRoot(paths, paths.Launcher, LauncherStorageLayout.LauncherEntries, cutoff, checkpoint, resumeAfter, completedEntry);
            case LauncherStorageCleanupStep.PersonalRoot:
                return SweepRoot(paths, paths.Personal, LauncherStorageLayout.PersonalEntries, cutoff, checkpoint, resumeAfter, completedEntry,
                    isPersonalRoot: true);
            case LauncherStorageCleanupStep.LauncherArchives:
                LogCleanupService.PruneLauncherArchives(paths, checkpoint);
                break;
            case LauncherStorageCleanupStep.AbandonedPlans:
                return PortablePackSyncService.CleanupAbandonedPlans(paths, paths.Launcher, cutoff,
                    _ => checkpoint(), CancellationToken.None);
            case LauncherStorageCleanupStep.UpdateTransfers:
                return SweepAbandonedUpdateTransfers(paths, cutoff, checkpoint);
            case LauncherStorageCleanupStep.DeprecatedFiles:
                DeprecatedFileCleanupService.Run(paths, logger, checkpoint, isManagedPack);
                break;
            case LauncherStorageCleanupStep.PrivateRuntimes:
                return StructureCleanupService.Run(paths, logger, checkpoint, isManagedPack);
            case LauncherStorageCleanupStep.PortableExtraction:
                LogCleanupService.PrunePortableExtractionCache(paths, checkpoint);
                break;
            case LauncherStorageCleanupStep.LaunchArguments:
                JavaLaunchArgumentFile.CleanupStale(paths, checkpoint);
                break;
            case LauncherStorageCleanupStep.PortableRuntimeCache:
                PortableRuntimeCache.CleanupIdle(paths, checkpoint);
                break;
            case LauncherStorageCleanupStep.RuntimeComponents:
                return DeprecatedFileCleanupService.CleanupRuntimeComponents(paths, checkpoint);
            case LauncherStorageCleanupStep.DesktopBootstrap:
                DesktopBootstrapCache.CleanupIdle(paths, checkpoint);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(step));
        }
        return 0;
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
            name.Equals("LANMinecraft.bsdiff.download", StringComparison.OrdinalIgnoreCase)) return true;

        // Migration-only names from clients that downloaded binary patches.
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

    private static int SweepRoot(AppPaths paths, string root, IReadOnlySet<string> kept, DateTime cutoff,
        Action checkpoint, string? resumeAfter, Action<string>? completedEntry, bool isPersonalRoot = false)
    {
        var removed = 0;
        foreach (var entry in ReadOrderedEntries(paths, root, checkpoint))
        {
            var name = Path.GetFileName(entry);
            if (kept.Contains(name) || IsProtected(name) ||
                name.StartsWith(".pack-sync-stage", StringComparison.OrdinalIgnoreCase) ||
                isPersonalRoot && (LogCleanupService.IsLauncherArchiveName(name) || LogCleanupService.IsSteamSpikeLogName(name)))
                continue;

            // AtomicFile publishes these by rename. Recent temporary files can
            // still belong to a writer whose operation does not involve Play.
            DateTime? expiry = IsAtomicTemporaryFile(name) ? cutoff : null;
            Walk(entry, name, expiry, depth: 0);
        }
        return removed;

        void Walk(string path, string relative, DateTime? expiry, int depth)
        {
            if (depth > 128 || IsProtected(Path.GetFileName(path)) ||
                resumeAfter is not null && CompareInVisitOrder(relative, resumeAfter) < 0) return;

            checkpoint();
            if (MaintenancePath.TryRead(paths, path, out var attributes))
            {
                var directory = (attributes & FileAttributes.Directory) != 0;
                if (directory)
                    foreach (var child in ReadOrderedEntries(paths, path, checkpoint))
                        Walk(child, relative + "/" + Path.GetFileName(child), expiry, depth + 1);

                if (expiry is null || IsOlderThan(path, directory, expiry.Value))
                {
                    checkpoint();
                    if (MaintenancePath.TryDelete(paths, path, directory)) removed++;
                }
            }
            completedEntry?.Invoke(relative);
        }
    }

    private static IEnumerable<string> ReadOrderedEntries(AppPaths paths, string directory, Action checkpoint) =>
        ReadEntries(paths, directory, checkpoint).Order(StringComparer.OrdinalIgnoreCase);

    // Children are completed before their directory. Compare by name segment
    // so punctuation in sibling names cannot move them across the cursor.
    private static int CompareInVisitOrder(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        while (true)
        {
            var leftSlash = left.IndexOf('/');
            var rightSlash = right.IndexOf('/');
            var order = left[..(leftSlash < 0 ? left.Length : leftSlash)]
                .CompareTo(right[..(rightSlash < 0 ? right.Length : rightSlash)], StringComparison.OrdinalIgnoreCase);
            if (order != 0) return order;
            if (leftSlash < 0 || rightSlash < 0) return (rightSlash >= 0).CompareTo(leftSlash >= 0);
            left = left[(leftSlash + 1)..];
            right = right[(rightSlash + 1)..];
        }
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
