using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Minecraft;

/// <summary>
/// Bounds the native host's private .NET payloads, without touching Java or
/// unowned files. Before desktop-cache migration the two newest inactive generations stay.
/// A recorded successful desktop set makes all unleased flat generations obsolete.
/// The tiny ownership leases stay even after their payload is removed: closing
/// and then deleting a lease would race a host starting that generation.
/// </summary>
internal static class PortableRuntimeCache
{
    private const int RetainedInactiveGenerations = 2;
    private const int MaximumGenerationEntries = 65_536;

    internal static void Cleanup(AppPaths paths) => CleanupIdle(paths, null);

    internal static void CleanupIdle(AppPaths paths, Action? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(paths);
        try
        {
            var root = Path.Combine(paths.Launcher, LauncherStorageLayout.LauncherRuntime);
            paths.EnsureUnderRoot(root);
            if (!Directory.Exists(root)) return;
            using var ancestors = new GuardedFileSystem.DirectoryLocks();
            if (!ancestors.HoldAncestors(root)) return;

            var inactive = new List<Generation>();
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                checkpoint?.Invoke();
                var hash = Path.GetFileName(directory);
                if (!IsHash(hash) || !IsDirectChild(root, directory)) continue;
                using var lease = OpenOwnedLease(root, hash);
                if (lease is null) continue;
                using var generationLock = new GuardedFileSystem.DirectoryLocks();
                if (!generationLock.Hold(directory)) continue;
                inactive.Add(new Generation(hash, directory, Directory.GetLastWriteTimeUtc(directory)));
            }

            var retained = DesktopBootstrapCache.HasSuccessfulSet(paths) ? 0 : RetainedInactiveGenerations;
            foreach (var generation in inactive.OrderByDescending(item => item.LastWriteUtc)
                         .ThenBy(item => item.Hash, StringComparer.Ordinal)
                         .Skip(retained))
            {
                checkpoint?.Invoke();
                // A generation may have become active since the inventory.
                // Keep the exclusive ownership lease for the whole deletion.
                using var lease = OpenOwnedLease(root, generation.Hash);
                if (lease is null) continue;
                TryDeleteGeneration(root, generation.Path, checkpoint);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Another process, a changed tree or an unreadable path means keep.
        }
    }

    internal static bool TryImportLegacy(AppPaths paths, Action? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var legacy = Path.Combine(paths.Launcher, LauncherStorageLayout.LegacyLauncherRuntime);
        try
        {
            using var parents = new GuardedFileSystem.DirectoryLocks();
            if (!parents.HoldAncestors(paths.Launcher, out var parentMissing)) return parentMissing;

            // An old native host holds this directory without delete sharing
            // during preparation and use. A DELETE handle also excludes an old
            // host starting after our lease checks, until the rename completes.
            using (var source = GuardedFileSystem.TryOpenDirectoryForRename(legacy, out var sourceMissing))
            {
                if (source is null) return sourceMissing;
                if (!IsIdleOwnedTree(legacy, checkpoint, out var empty)) return false;
                checkpoint?.Invoke();
                if (!empty)
                {
                    var current = Path.Combine(paths.Launcher, LauncherStorageLayout.LauncherRuntime);
                    return TryClearEmptyRenameTarget(current) &&
                        GuardedFileSystem.TryRenameOrdinarySibling(source, LauncherStorageLayout.LauncherRuntime);
                }
                if (!GuardedFileSystem.TrySetDeleteDisposition(source)) return false;
            }
            // A concurrent handle can defer deletion until it closes.
            using var remaining = GuardedFileSystem.TryOpenDirectoryForRename(legacy, out var missing);
            return missing;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Busy, changed or unverified legacy data is kept for another pass.
            return false;
        }
    }

    private static bool TryClearEmptyRenameTarget(string path)
    {
        using var target = GuardedFileSystem.TryOpenDirectoryForRename(path, out var missing);
        // The kernel checks emptiness when marking this exact directory for
        // deletion. A late arrival keeps the directory or blocks the rename.
        return target is null ? missing : GuardedFileSystem.TrySetDeleteDisposition(target);
    }

    private static bool IsIdleOwnedTree(string root, Action? checkpoint, out bool empty)
    {
        empty = true;
        using var directories = new GuardedFileSystem.DirectoryLocks();
        var pending = new Stack<string>();
        var count = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            checkpoint?.Invoke();
            empty = false;
            if (++count > MaximumGenerationEntries) return false;
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            var name = Path.GetFileName(entry);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                if (!IsHash(name) || !directories.Hold(entry)) return false;
                using var lease = OpenOwnedLease(root, name);
                if (lease is null) return false;
                pending.Push(entry);
            }
            else
            {
                const string suffix = ".lease";
                if (!name.EndsWith(suffix, StringComparison.Ordinal) || !IsHash(name[..^suffix.Length])) return false;
                using var lease = OpenOwnedLease(root, name[..^suffix.Length]);
                if (lease is null) return false;
            }
        }
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                checkpoint?.Invoke();
                if (++count > MaximumGenerationEntries) return false;
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
                if (!attributes.HasFlag(FileAttributes.Directory)) continue;
                if (!directories.Hold(entry)) return false;
                pending.Push(entry);
            }
        }
        // Close our descendant handles before renaming. NTFS does not rename
        // directories with open descendant files, including a live payload.
        return true;
    }

    internal static FileStream? OpenOwnedLease(string root, string hash, string? kind = null)
    {
        var path = Path.Combine(root, hash + ".lease");
        if (!IsDirectChild(root, path)) return null;
        SafeFileHandle? handle = null;
        FileStream? lease = null;
        try
        {
            handle = GuardedFileSystem.TryOpenExclusiveWritableFile(path);
            if (handle is null) return null;
            lease = new FileStream(handle, FileAccess.ReadWrite);
            handle = null;
            var expected = Encoding.ASCII.GetBytes(kind is null
                ? $"LMHOST01\n{hash}\n" : $"LMDESK01\n{kind}\n{hash}\n");
            if (lease.Length != expected.Length) return null;
            Span<byte> actual = stackalloc byte[expected.Length];
            lease.ReadExactly(actual);
            if (!actual.SequenceEqual(expected)) return null;
            var owned = lease;
            lease = null;
            return owned;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            lease?.Dispose();
            handle?.Dispose();
        }
    }

    internal static void TryDeleteGeneration(string root, string generation, Action? checkpoint)
    {
        try
        {
            if (!IsDirectChild(root, generation) || !IsHash(Path.GetFileName(generation))) return;
            using var directories = new GuardedFileSystem.DirectoryLocks();
            if (!directories.Hold(generation)) return;
            var files = new List<string>();
            var pending = new Stack<string>();
            var orderedDirectories = new List<string> { generation };
            pending.Push(generation);
            while (pending.TryPop(out var directory))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    checkpoint?.Invoke();
                    if (!IsDescendant(generation, entry) || files.Count + orderedDirectories.Count >= MaximumGenerationEntries)
                        return;
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!directories.Hold(entry)) return;
                        pending.Push(entry);
                        orderedDirectories.Add(entry);
                    }
                    else files.Add(entry);
                }
            }

            // Probe every file before deleting any of them. A locked DLL or
            // read-only file leaves the whole known tree for a later run.
            foreach (var file in files)
            {
                checkpoint?.Invoke();
                using var handle = GuardedFileSystem.TryOpenExclusiveWritableFile(file);
                if (handle is null) return;
            }

            foreach (var file in files)
            {
                checkpoint?.Invoke();
                var attributes = File.GetAttributes(file);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return;
                File.Delete(file);
            }
            for (var index = orderedDirectories.Count - 1; index >= 0; index--)
            {
                checkpoint?.Invoke();
                var directory = orderedDirectories[index];
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
                directories.Release(directory);
                // Never recurse through a tree that changed after validation.
                Directory.Delete(directory, recursive: false);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Keep the lease and anything still present. The native host checks
            // the complete payload again before it loads a partially kept tree.
        }
    }

    private static bool IsHash(string name) => name.Length == 64 &&
        name.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsDirectChild(string root, string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), root, StringComparison.OrdinalIgnoreCase);

    private static bool IsDescendant(string root, string path) =>
        Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private sealed record Generation(string Hash, string Path, DateTime LastWriteUtc);
}
