using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Minecraft;

/// <summary>Windows file operations that preserve ordinary-path and handle-sharing checks.</summary>
internal static class GuardedFileSystem
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericReadWrite = 0xC0000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadAttributes = 0x00000080;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int FileRenameInformation = 10;
    private const int FileDispositionInfoEx = 21;
    private const uint DeleteIgnoringReadOnlyAttribute = 0x11;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;

    internal static SafeFileHandle? TryOpenExclusiveWritableFile(string path)
    {
        var handle = CreateFile(NativePath(path), GenericReadWrite, 0, 0, OpenExisting, OpenReparsePoint, 0);
        if (!handle.IsInvalid && IsPlainFile(handle)) return handle;
        handle.Dispose();
        return null;
    }

    private static bool IsPlainFile(SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var information) &&
        (information.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;

    private static bool IsPlainDirectory(SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var information) &&
        (information.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;

    internal static SafeFileHandle? TryOpenDirectoryForRename(string path) => TryOpenDirectoryForRename(path, out _);

    internal static SafeFileHandle? TryOpenDirectoryForRename(string path, out bool missing) =>
        TryOpenForRename(path, directory: true, out missing);

    internal static bool TryPublishNewOrdinaryFile(string temporary, string newName)
    {
        using var source = TryOpenForRename(temporary, directory: false, out _);
        return source is not null && TryRenameOrdinarySibling(source, newName);
    }

    private static SafeFileHandle? TryOpenForRename(string path, bool directory, out bool missing)
    {
        var source = CreateFile(NativePath(path), DeleteAccess | GenericRead, 1, 0,
            OpenExisting, BackupSemantics | OpenReparsePoint, 0);
        missing = source.IsInvalid && LastOpenWasMissing();
        if (!source.IsInvalid && (directory ? IsPlainDirectory(source) : IsPlainFile(source))) return source;
        source.Dispose();
        return null;
    }

    private static bool LastOpenWasMissing() => Marshal.GetLastPInvokeError() is ErrorFileNotFound or ErrorPathNotFound;

    internal static bool TryRenameOrdinarySibling(SafeFileHandle source, string newName)
    {
        if (newName.Length == 0 || newName[^1] is '.' or ' ' || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        var name = Encoding.Unicode.GetBytes(newName);
        var lengthOffset = Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileNameLength)).ToInt32();
        var nameOffset = Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileName)).ToInt32();
        var information = new byte[nameOffset + name.Length + sizeof(char)];
        BinaryPrimitives.WriteInt32LittleEndian(information.AsSpan(lengthOffset), name.Length);
        name.CopyTo(information, nameOffset);

        // A leaf name with a null RootDirectory resolves through the source
        // handle's parent, even while the bootstrap keeps that parent read-only.
        // ReplaceIfExists stays zero so a concurrently created sibling wins.
        return NtSetInformationFile(source, out _, information, information.Length, FileRenameInformation) == 0;
    }

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
        return TrySetDeleteDisposition(entry);
    }

    internal static bool TrySetDeleteDisposition(SafeFileHandle entry)
    {
        var flags = DeleteIgnoringReadOnlyAttribute;
        return SetFileInformationByHandle(entry, FileDispositionInfoEx, ref flags, sizeof(uint));
    }

    private static string NativePath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    /// <summary>
    /// Hold each parent without sharing writes or deletion, so a validated
    /// ancestor cannot be replaced or converted into a reparse point in place.
    /// OPEN_REPARSE_POINT also checks the opened object rather than its target.
    /// </summary>
    internal sealed class DirectoryLocks : IDisposable
    {
        private readonly Dictionary<string, SafeFileHandle> handles = new(StringComparer.Ordinal);

        public bool HoldAncestors(string path) => HoldAncestors(path, out _);

        public bool HoldAncestors(string path, out bool missing)
        {
            missing = false;
            var pending = new Stack<string>();
            for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
                pending.Push(current);
            while (pending.TryPop(out var directory))
                if (!Hold(directory, out missing)) return false;
            return true;
        }

        public bool Hold(string path) => Hold(path, out _);

        private bool Hold(string path, out bool missing)
        {
            missing = false;
            if (handles.ContainsKey(path)) return true;
            var handle = CreateFile(NativePath(path), GenericRead, 1, 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0);
            missing = handle.IsInvalid && LastOpenWasMissing();
            if (handle.IsInvalid || !IsPlainDirectory(handle))
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RenameInformation
    {
        public uint Flags;
        public nint RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
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

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock status,
        byte[] information, int bufferSize, int informationClass);
}
