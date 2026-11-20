using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Minecraft;

/// <summary>A verified game payload shared by every pack that pins the same release archive.</summary>
internal sealed class SharedNativeRuntimeStore(AppPaths paths)
{
    internal const string FamilyPath = "native/runtime-family.json";
    private const int MaximumFamilyBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { AllowDuplicateProperties = false };
    private string StoreRoot => Path.Combine(paths.SharedRuntime, "NativeRuntimes");

    internal static string PreparedCache(AppPaths paths) => Path.Combine(paths.SharedRuntime, "NativePrepared");

    internal bool IsInstalled(NativeSharedRuntimeDescriptor runtime, string minecraftVersion, CancellationToken token)
    {
        try
        {
            using var lease = Open(runtime, minecraftVersion, token);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    internal Lease Open(NativeSharedRuntimeDescriptor runtime, string minecraftVersion, CancellationToken token) =>
        OpenAt(Path.Combine(StoreRoot, runtime.Archive.Sha256), runtime, minecraftVersion, token);

    /// <returns>The number of network bytes transferred by this installation.</returns>
    internal async Task<long> PrepareAsync(NativeSharedRuntimeDescriptor runtime, string minecraftVersion,
        Func<string, CancellationToken, Task> downloadArchive, Func<string, long, bool> hasSpace, CancellationToken token)
    {
        var root = StoreRoot;
        RuntimeComponentService.RequireOrdinaryPath(paths, root);
        Directory.CreateDirectory(root);
        using var ancestors = new GuardedFileSystem.DirectoryLocks();
        if (!ancestors.HoldAncestors(root)) throw new IOException("Shared native runtime directory is redirected.");
        var sha = runtime.Archive.Sha256;
        using var installLock = await AcquireLockAsync(Path.Combine(root, sha + ".lock"), token).ConfigureAwait(false);
        if (IsInstalled(runtime, minecraftVersion, token)) return 0;
        if (!hasSpace(root, checked(runtime.Archive.SizeBytes + runtime.Files.Sum(file => file.SizeBytes))))
            throw new IOException("Недостаточно места для подготовки общего нативного рантайма.");

        using var stageLease = PortablePackSyncService.CreateTransferStage(paths, out var stage);
        var archivePath = Path.Combine(stage, "download.zip");
        var target = Path.Combine(root, sha);
        var suffix = Guid.NewGuid().ToString("N");
        var candidate = Path.Combine(root, $".prepare-{sha}-{suffix}");
        var previous = Path.Combine(root, $".previous-{sha}-{suffix}");
        try
        {
            await downloadArchive(archivePath, token).ConfigureAwait(false);
            using (var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (archive.Length != runtime.Archive.SizeBytes || Hash(archive) != sha)
                    throw new InvalidDataException("Shared native runtime archive SHA-256 mismatch.");
                PackArchiveService.CheckCentralDirectory(archive);
                archive.Position = 0;
                Extract(archive, candidate, runtime, token);
            }
            using (OpenAt(candidate, runtime, minecraftVersion, token)) { }
            token.ThrowIfCancellationRequested();
            RuntimeComponentService.RequireOrdinaryPath(paths, target);
            if (Directory.Exists(target) && !RenameSibling(target, Path.GetFileName(previous)))
                throw new IOException("The shared native runtime changed while it was being repaired.");
            if (!RenameSibling(candidate, sha))
            {
                if (Directory.Exists(previous) && !Directory.Exists(target))
                    _ = RenameSibling(previous, sha);
                throw new IOException("The new shared native runtime could not be published.");
            }
            return runtime.Archive.SizeBytes;
        }
        finally
        {
            ancestors.Dispose();
            RemoveTransient(candidate);
            if (Directory.Exists(target)) RemoveTransient(previous);
        }
    }

    private static bool RenameSibling(string source, string name)
    {
        using var handle = GuardedFileSystem.TryOpenDirectoryForRename(source);
        return handle is not null && GuardedFileSystem.TryRenameOrdinarySibling(handle, name);
    }

    private void RemoveTransient(string directory)
    {
        // Only a unique, launcher-owned child of the verified store is removed.
        // Directory.Delete does not traverse nested Windows junction targets.
        if (Path.GetDirectoryName(directory) != StoreRoot ||
            !MaintenancePath.TryRead(paths, directory, out var attributes) || !attributes.HasFlag(FileAttributes.Directory)) return;
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Shared native runtime lock is redirected.");
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { }
            await Task.Delay(75, token).ConfigureAwait(false);
        }
    }

    private static Lease OpenAt(string root, NativeSharedRuntimeDescriptor runtime, string minecraftVersion, CancellationToken token)
    {
        var lease = new Lease(root);
        try
        {
            if (!lease.Directories.HoldAncestors(root)) throw new IOException("Shared native runtime is missing or redirected.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in runtime.Files)
            {
                token.ThrowIfCancellationRequested();
                var path = Under(root, file.Path);
                if (!lease.Directories.HoldAncestors(Path.GetDirectoryName(path)!))
                    throw new IOException("Shared native runtime file has a redirected directory: " + file.Path);
                var handle = GuardedFileSystem.TryOpenOrdinaryReadFile(path)
                    ?? throw new IOException("Shared native runtime file is missing or redirected: " + file.Path);
                var stream = new FileStream(handle, FileAccess.Read);
                lease.Files.Add(file.Path, stream);
                if (stream.Length != file.SizeBytes || Hash(stream) != file.Sha256)
                    throw new InvalidDataException("Shared native runtime file failed SHA-256 verification: " + file.Path);
                NativeRuntimeFiles.VerifyWindowsImage(file.Path, stream);
                seen.Add(file.Path);
            }
            CheckContents(root, root, seen, lease.Directories, token);
            VerifyFamily(lease.Files[FamilyPath], runtime, minecraftVersion);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static void CheckContents(string root, string directory, HashSet<string> seen,
        GuardedFileSystem.DirectoryLocks directories, CancellationToken token)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            var attr = File.GetAttributes(entry);
            if ((attr & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Shared native runtime cannot contain filesystem links.");
            if ((attr & FileAttributes.Directory) != 0)
            {
                if (!directories.Hold(entry)) throw new IOException("Shared native runtime directory changed during verification.");
                CheckContents(root, entry, seen, directories, token);
            }
            else if (!seen.Contains(Path.GetRelativePath(root, entry).Replace('\\', '/')))
                throw new InvalidDataException("Unexpected file in the shared native runtime: " + Path.GetFileName(entry));
        }
    }

    private static void VerifyFamily(FileStream input, NativeSharedRuntimeDescriptor runtime, string minecraftVersion)
    {
        if (input.Length is <= 0 or > MaximumFamilyBytes)
            throw new InvalidDataException("Invalid shared native runtime family manifest length.");
        input.Position = 0;
        Family? family;
        try { family = JsonSerializer.Deserialize<Family>(input, JsonOptions); }
        catch (JsonException error) { throw new InvalidDataException("Invalid shared native runtime family manifest.", error); }
        if (family is not { SchemaVersion: 1 } || family.Id != runtime.Id || family.Version != runtime.Version ||
            family.Platform != runtime.Platform || family.MinecraftVersion != minecraftVersion)
            throw new InvalidDataException("Shared native runtime family manifest disagrees with the pack descriptor.");
    }

    private static void Extract(Stream input, string destination, NativeSharedRuntimeDescriptor runtime, CancellationToken token)
    {
        var files = runtime.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count != files.Count)
            throw new InvalidDataException("Shared native runtime ZIP must contain only its declared files.");
        Directory.CreateDirectory(destination);
        var buffer = new byte[128 * 1024];
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var unixMode = (entry.ExternalAttributes >> 16) & 0xf000;
            if (!files.Remove(entry.FullName, out var file) || entry.Length != file.SizeBytes ||
                (entry.ExternalAttributes & (int)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                unixMode is not (0 or 0x8000))
                throw new InvalidDataException("Invalid shared native runtime ZIP member: " + entry.FullName);
            var outputPath = Under(destination, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var source = entry.Open();
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long remaining = file.SizeBytes;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var count = source.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                if (count == 0) throw new InvalidDataException("Truncated shared native runtime ZIP member.");
                output.Write(buffer, 0, count);
                remaining -= count;
            }
            if (source.ReadByte() != -1) throw new InvalidDataException("Oversized shared native runtime ZIP member.");
        }
    }

    private static string Under(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Hash(Stream source) => Convert.ToHexStringLower(SHA256.HashData(source));
    private sealed record Family(int SchemaVersion, string Id, string Version, string Platform, string MinecraftVersion);

    internal sealed class Lease(string root) : IDisposable
    {
        internal string Root { get; } = root;
        internal GuardedFileSystem.DirectoryLocks Directories { get; } = new();
        internal Dictionary<string, FileStream> Files { get; } = new(StringComparer.Ordinal);

        public void Dispose()
        {
            foreach (var stream in Files.Values) stream.Dispose();
            Directories.Dispose();
        }
    }
}
