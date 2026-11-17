using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Minecraft;

/// <summary>
/// Bounds the native host's private .NET payloads, without touching Java or
/// unowned files. Active leases and the two newest inactive generations stay.
/// The tiny ownership leases stay even after their payload is removed: closing
/// and then deleting a lease would race a host starting that generation.
/// </summary>
internal static class PortableRuntimeCache
{
    private const int RetainedInactiveGenerations = 2;
    private const int MaximumGenerationEntries = 65_536;
    private const uint GenericRead = 0x80000000;
    private const uint GenericReadWrite = 0xC0000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadAttributes = 0x00000080;
    private const int FileDispositionInfoEx = 21;
    private const uint DeleteIgnoringReadOnlyAttribute = 0x11;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;

    internal static void Cleanup(AppPaths paths) => CleanupIdle(paths, null);

    internal static void CleanupIdle(AppPaths paths, Action? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(paths);
        try
        {
            var root = Path.GetFullPath(Path.Combine(paths.Launcher, "Runtime"));
            paths.EnsureUnderRoot(root);
            if (!Directory.Exists(root)) return;
            using var ancestors = new DirectoryLocks();
            if (!ancestors.HoldAncestors(root)) return;

            var inactive = new List<Generation>();
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                checkpoint?.Invoke();
                var hash = Path.GetFileName(directory);
                if (!IsHash(hash) || !IsDirectChild(root, directory)) continue;
                using var lease = OpenOwnedLease(root, hash);
                if (lease is null) continue;
                using var generationLock = new DirectoryLocks();
                if (!generationLock.Hold(directory)) continue;
                inactive.Add(new Generation(hash, directory, Directory.GetLastWriteTimeUtc(directory)));
            }

            foreach (var generation in inactive.OrderByDescending(item => item.LastWriteUtc)
                         .ThenBy(item => item.Hash, StringComparer.Ordinal)
                         .Skip(RetainedInactiveGenerations))
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

    private static FileStream? OpenOwnedLease(string root, string hash)
    {
        var path = Path.Combine(root, hash + ".lease");
        if (!IsDirectChild(root, path)) return null;
        SafeFileHandle? handle = null;
        FileStream? lease = null;
        try
        {
            handle = CreateFile(NativePath(path), GenericReadWrite, 0, 0, OpenExisting, OpenReparsePoint, 0);
            if (handle.IsInvalid || !IsPlainFile(handle)) return null;
            lease = new FileStream(handle, FileAccess.ReadWrite);
            handle = null;
            var expected = Encoding.ASCII.GetBytes($"LMHOST01\n{hash}\n");
            if (lease.Length != expected.Length) return null;
            Span<byte> actual = stackalloc byte[74];
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

    private static void TryDeleteGeneration(string root, string generation, Action? checkpoint)
    {
        try
        {
            if (!IsDirectChild(root, generation) || !IsHash(Path.GetFileName(generation))) return;
            using var directories = new DirectoryLocks();
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
                using var handle = CreateFile(NativePath(file), GenericReadWrite, 0, 0, OpenExisting, OpenReparsePoint, 0);
                if (handle.IsInvalid || !IsPlainFile(handle)) return;
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

    private static bool IsPlainFile(SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var information) &&
        (information.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;

    internal static bool AreSameOrdinaryFile(string left, string right)
    {
        if (!OperatingSystem.IsWindows()) return false;
        const uint shareReadWriteDelete = 7;
        using var first = CreateFile(NativePath(left), ReadAttributes, shareReadWriteDelete, 0, OpenExisting, OpenReparsePoint, 0);
        using var second = CreateFile(NativePath(right), ReadAttributes, shareReadWriteDelete, 0, OpenExisting, OpenReparsePoint, 0);
        const FileAttributes unsupported = FileAttributes.Directory | FileAttributes.ReparsePoint;
        return !first.IsInvalid && !second.IsInvalid &&
            GetFileInformationByHandle(first, out var firstInfo) && GetFileInformationByHandle(second, out var secondInfo) &&
            ((firstInfo.Attributes | secondInfo.Attributes) & unsupported) == 0 &&
            firstInfo.NumberOfLinks > 1 && (firstInfo.FileIndexHigh | firstInfo.FileIndexLow) != 0 &&
            firstInfo.VolumeSerialNumber == secondInfo.VolumeSerialNumber &&
            firstInfo.FileIndexHigh == secondInfo.FileIndexHigh && firstInfo.FileIndexLow == secondInfo.FileIndexLow;
    }

    // Keeping each parent open closes the gap between path validation and deletion.
    // The leaf is checked on its handle; deleting an empty directory never recurses.
    internal static bool TryDeleteOrdinaryEntry(string path, bool directory, Func<bool>? stillAllowed = null)
    {
        using var parents = new DirectoryLocks();
        if (Path.GetDirectoryName(path) is not { } parent || !parents.HoldAncestors(parent)) return false;
        using var entry = CreateFile(NativePath(path), DeleteAccess | ReadAttributes, 1, 0,
            OpenExisting, BackupSemantics | OpenReparsePoint, 0);
        var expected = directory ? FileAttributes.Directory : 0;
        if (entry.IsInvalid || !GetFileInformationByHandle(entry, out var information) ||
            (information.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != expected) return false;
        if (stillAllowed?.Invoke() == false) return false;
        var flags = DeleteIgnoringReadOnlyAttribute;
        return SetFileInformationByHandle(entry, FileDispositionInfoEx, ref flags, sizeof(uint));
    }

    private static string NativePath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    private sealed record Generation(string Hash, string Path, DateTime LastWriteUtc);

    /// <summary>
    /// Hold each parent without sharing writes or deletion, so a validated
    /// ancestor cannot be replaced or converted into a reparse point in place.
    /// OPEN_REPARSE_POINT also checks the opened object rather than its target.
    /// </summary>
    private sealed class DirectoryLocks : IDisposable
    {
        private readonly Dictionary<string, SafeFileHandle> handles = new(StringComparer.Ordinal);

        public bool HoldAncestors(string path)
        {
            var pending = new Stack<string>();
            for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
                pending.Push(current);
            while (pending.TryPop(out var directory))
                if (!Hold(directory)) return false;
            return true;
        }

        public bool Hold(string path)
        {
            if (handles.ContainsKey(path)) return true;
            var handle = CreateFile(NativePath(path), GenericRead, 1, 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information) ||
                (information.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            {
                handle.Dispose();
                return false;
            }
            handles.Add(path, handle);
            return true;
        }

        public void Release(string path)
        {
            if (handles.Remove(path, out var handle)) handle.Dispose();
        }

        public void Dispose()
        {
            foreach (var handle in handles.Values) handle.Dispose();
            handles.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public FileAttributes Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref uint flags, int bufferSize);
}
