using System.IO;
using System.IO.Enumeration;

namespace Minecraft;

/// <summary>Adopts the clearer name before the session opens any pack runtime.</summary>
internal static class PackRuntimeFolderMigration
{
    internal static bool TryApply(AppPaths paths, Logger? logger = null, Func<bool>? mayHaveGame = null,
        Action? checkpoint = null)
    {
        var source = paths.LegacyPackRuntimes;
        var destination = paths.CanonicalPackRuntimes;
        try
        {
            if (!Exists(source))
            {
                paths.UseCanonicalPackRuntimes();
                return !Exists(destination) || IsOrdinaryDirectory(paths, destination) ||
                    Defer("the new runtime path is redirected, inaccessible or not a directory.");
            }
            if (!IsOrdinaryDirectory(paths, source))
                return Defer("the old runtime path is redirected, inaccessible or not a directory.");
            paths.UseLegacyPackRuntimes();
            if (Exists(destination) && !IsOrdinaryDirectory(paths, destination))
                return Defer("the new runtime path is redirected, inaccessible or not a directory.");

            using var parents = new GuardedFileSystem.DirectoryLocks();
            if (!parents.HoldAncestors(paths.Launcher))
                return Defer("the launcher directory could not be guarded.");
            using var lease = SharedRuntimeGate.TryAcquire(paths);
            if (lease is null) return Defer("a runtime is being prepared.");
            if (mayHaveGame?.Invoke() ?? MayHaveRunningGame(paths, logger))
                return Defer("a game is running or its state could not be verified.");
            checkpoint?.Invoke();

            using var runtime = GuardedFileSystem.TryOpenDirectoryForRename(source);
            if (runtime is null) return Defer("the old runtime directory is in use or changed.");
            if (Exists(destination))
            {
                if (!IsOrdinaryDirectory(paths, destination))
                    return Defer("the new runtime path changed.");
                if (!Directory.EnumerateFileSystemEntries(source).Any())
                {
                    runtime.Dispose();
                    if (!TryRemoveEmpty(source)) return Defer("the empty old runtime directory changed.");
                    paths.UseCanonicalPackRuntimes();
                    return true;
                }
                if (Directory.EnumerateFileSystemEntries(destination).Any())
                    return Defer("both folders contain data.");
            }

            if (ContainsRedirectedEntry(source, checkpoint))
                return Defer("the runtime contains a redirected path.");
            checkpoint?.Invoke();
            // The target may be the empty directory an earlier startup made.
            // Removing an empty directory and a same-volume rename are both
            // restartable, and neither operation overwrites files.
            if (Exists(destination) && !TryRemoveEmpty(destination))
                return Defer("the new runtime directory contains data or is in use.");
            if (!GuardedFileSystem.TryRenameOrdinarySibling(runtime, LauncherStorageLayout.PackRuntimes))
                return Defer("the runtime directory could not be renamed.");
            paths.UseCanonicalPackRuntimes();
            logger?.Info("Pack runtime files moved from Runtimes to PackRuntimes.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return Defer(ex.Message);
        }

        bool TryRemoveEmpty(string path) =>
            MaintenancePath.TryDelete(paths, path, directory: true,
                stillAllowed: () => !Directory.EnumerateFileSystemEntries(path).Any());

        bool Defer(string reason)
        {
            logger?.Warn("Pack runtime folder rename was deferred: " + reason);
            return false;
        }
    }

    private static bool MayHaveRunningGame(AppPaths paths, Logger? logger)
    {
        var presence = new ClientPresenceService(paths, logger);
        return presence.ReadComponentReferences() is null || presence.ReadLiveSessions().Count != 0;
    }

    private static bool IsOrdinaryDirectory(AppPaths paths, string path) =>
        MaintenancePath.TryRead(paths, path, out var attributes) && attributes.HasFlag(FileAttributes.Directory);

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private static bool ContainsRedirectedEntry(string path, Action? checkpoint)
    {
        var links = new FileSystemEnumerable<bool>(path, static (ref FileSystemEntry _) => true,
            new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true, IgnoreInaccessible = false })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
            {
                checkpoint?.Invoke();
                return (entry.Attributes & FileAttributes.ReparsePoint) != 0;
            },
            ShouldRecursePredicate = static (ref FileSystemEntry entry) =>
                (entry.Attributes & FileAttributes.ReparsePoint) == 0
        };
        return links.Any();
    }
}
