using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>Owns the shared Java layout and protects installs while they are moved, repaired or removed.</summary>
internal static partial class JavaRuntimeStorage
{
    internal sealed record OwnedDirectory(string Path, string RuntimeId, int MajorVersion, bool IsInstall);

    public static string InstallParent(string runtimeRoot) =>
        Path.Combine(Path.GetFullPath(runtimeRoot), "runtime", "windows-x64");

    public static string ArchiveParent(AppPaths paths) => Path.Combine(paths.Launcher, "ManagedComponents", "java-runtime");

    private const string ArchiveOwnerFileName = ".portable-java-archive.json";

    internal static void RecordArchiveOwnership(AppPaths paths, string archivePath, JavaRuntimePin pin)
    {
        var expected = Path.Combine(ArchiveParent(paths), pin.RuntimeId.Replace('+', '_'), pin.ArchiveFileName);
        RequireOrdinaryPath(paths, archivePath);
        if (!Path.GetFullPath(archivePath).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(archivePath) || new FileInfo(archivePath).Length != pin.ArchiveSizeBytes)
            throw new InvalidDataException("Verified Java archive is outside its owned cache location.");
        var marker = Path.Combine(Path.GetDirectoryName(archivePath)!, ArchiveOwnerFileName);
        RequireOrdinaryPath(paths, marker);
        AtomicFile.WriteAllText(marker, JsonSerializer.Serialize(new ArchiveOwner(1, pin.RuntimeId,
            pin.ArchiveFileName, pin.ArchiveSha256, pin.MajorVersion)));
    }

    public static async Task<FileStream> AcquireAsync(AppPaths paths, CancellationToken token)
    {
        SharedRuntimeGate.MarkActivity(paths);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (TryAcquire(paths) is { } stream) return stream;
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }

    public static FileStream? TryAcquire(AppPaths paths)
    {
        var path = Path.Combine(paths.JavaRuntimes, ".java-cache.lock");
        RequireOrdinaryPath(paths, path);
        Directory.CreateDirectory(paths.JavaRuntimes);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return null; }
    }

    public static void RequireOrdinaryPath(AppPaths paths, string path)
    {
        paths.EnsureUnderRoot(path);
        var root = Path.TrimEndingDirectorySeparator(paths.Root);
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Java runtime path contains a link: {current}");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    public static bool OrdinaryTree(string directory, Action? checkpoint = null)
    {
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.Count > 0)
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    checkpoint?.Invoke();
                    if (MaintenancePath.IsProtectedAssetName(Path.GetFileName(path))) return false;
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool IsInUse(string directory)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        foreach (var name in new[] { "java", "javaw" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        var path = ProcessImagePath.TryRead(process.Id);
                        if (path is null ? !HasExited(process) : path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch (InvalidOperationException) { /* The process exited before it could be inspected. */ }
                    catch (Win32Exception)
                    {
                        // Keep the files if process state cannot establish that it exited.
                        if (!HasExited(process)) return true;
                    }
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
        return false;
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
        catch (Win32Exception) { return false; }
    }

    /// <summary>Descriptors request the new pin. Prepared states retain any older pin still in use.</summary>
    private static HashSet<string>? ReadNeededRuntimes(AppPaths paths, string? excludingBuild, Action? checkpoint)
    {
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            RequireOrdinaryPath(paths, paths.Packs);
            RequireOrdinaryPath(paths, paths.Runtimes);
            if (Directory.Exists(paths.Packs))
            {
                foreach (var pack in Directory.EnumerateDirectories(paths.Packs))
                {
                    checkpoint?.Invoke();
                    if (string.Equals(Path.GetFileName(pack), excludingBuild, StringComparison.OrdinalIgnoreCase)) continue;
                    var descriptorPath = Path.Combine(pack, PackManifestService.ManifestFileName);
                    RequireOrdinaryPath(paths, descriptorPath);
                    if (!MinecraftProcessService.HasPackData(pack)) continue;
                    var descriptor = PackManifestService.Load(pack);
                    if (descriptor.NativeRuntime is null) needed.Add(JavaRuntimeCatalog.RequiredFor(descriptor).RuntimeId);
                }
            }

            if (Directory.Exists(paths.Runtimes))
            {
                foreach (var packRuntime in Directory.EnumerateDirectories(paths.Runtimes))
                {
                    checkpoint?.Invoke();
                    if (string.Equals(Path.GetFileName(packRuntime), excludingBuild, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Directory.Exists(paths.CombineUnderPacks(Path.GetFileName(packRuntime)))) continue;
                    var statePath = Path.Combine(packRuntime, ".portable-runtime.json");
                    RequireOrdinaryPath(paths, statePath);
                    if (!File.Exists(statePath)) continue;
                    using var document = JsonDocument.Parse(File.ReadAllText(statePath));
                    if (!document.RootElement.TryGetProperty("javaRuntimeId", out var runtimeId) ||
                        runtimeId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(runtimeId.GetString()))
                        return null;
                    needed.Add(runtimeId.GetString()!);
                }
            }
            var selected = RuntimeComponentService.ReadProtectedJavaRuntimeIds(paths, checkpoint, excludingBuild);
            if (selected is null) return null;
            needed.UnionWith(selected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
                                   InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        return needed;
    }

    public static IReadOnlyList<OwnedDirectory> ReadUnusedDirectories(AppPaths paths, string? excludingBuild = null,
        Action? checkpoint = null)
    {
        if (ReadNeededRuntimes(paths, excludingBuild, checkpoint) is not { } needed) return [];
        var directories = ReadOwnedDirectories(paths, needed, checkpoint);
        foreach (var directory in directories.Where(entry => entry.IsInstall && !needed.Contains(entry.RuntimeId)))
        {
            checkpoint?.Invoke();
            if (IsInUse(directory.Path)) needed.Add(directory.RuntimeId);
        }
        return directories.Where(entry => !needed.Contains(entry.RuntimeId)).ToArray();
    }

    private static IReadOnlyList<OwnedDirectory> ReadOwnedDirectories(AppPaths paths, HashSet<string> needed,
        Action? checkpoint)
    {
        var result = new List<OwnedDirectory>();
        var historical = new Dictionary<string, (string ArchiveSha256, int MajorVersion)>(StringComparer.OrdinalIgnoreCase);
        var installs = InstallParent(paths.JavaRuntimes);
        var archives = ArchiveParent(paths);
        RequireOrdinaryPath(paths, installs);
        RequireOrdinaryPath(paths, archives);
        if (Directory.Exists(installs))
        {
            foreach (var directory in Directory.EnumerateDirectories(installs))
            {
                checkpoint?.Invoke();
                if (!TryReadOwner(paths, directory, out var runtimeId, out var archiveSha256, out var major) ||
                    needed.Contains(runtimeId)) continue;
                if (!OrdinaryTree(directory, checkpoint))
                {
                    needed.Add(runtimeId);
                    continue;
                }
                result.Add(new OwnedDirectory(directory, runtimeId, major, IsInstall: true));
                historical[runtimeId] = (archiveSha256, major);
            }
        }

        if (!Directory.Exists(archives)) return result;
        foreach (var directory in Directory.EnumerateDirectories(archives))
        {
            checkpoint?.Invoke();
            var name = Path.GetFileName(directory);
            if (TryReadArchiveOwner(paths, directory, checkpoint, out var ownedArchive))
            {
                if (!needed.Contains(ownedArchive.RuntimeId)) result.Add(ownedArchive);
                continue;
            }
            var pin = JavaRuntimeCatalog.Releases.FirstOrDefault(release =>
                string.Equals(release.RuntimeId.Replace('+', '_'), name, StringComparison.OrdinalIgnoreCase));
            if (pin is not null && needed.Contains(pin.RuntimeId)) continue;
            if (!OrdinaryTree(directory, checkpoint)) continue;
            var contents = Directory.EnumerateFileSystemEntries(directory).Take(2).ToArray();
            if (contents.Length != 1 || !File.Exists(contents[0])) continue;
            if (pin is not null && !needed.Contains(pin.RuntimeId) &&
                string.Equals(Path.GetFileName(contents[0]), pin.ArchiveFileName, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new OwnedDirectory(directory, pin.RuntimeId, pin.MajorVersion, IsInstall: false));
                continue;
            }

            // An old archive can only be identified by its matching install marker.
            foreach (var (id, owner) in historical)
            {
                if (!string.Equals(id.Replace('+', '_'), name, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(SharedRuntimeStore.HashFile(contents[0], checkpoint), owner.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                    result.Add(new OwnedDirectory(directory, id, owner.MajorVersion, IsInstall: false));
                break;
            }
        }
        return result;
    }

    private static bool TryReadArchiveOwner(AppPaths paths, string directory, Action? checkpoint, out OwnedDirectory owner)
    {
        owner = null!;
        try
        {
            var marker = Path.Combine(directory, ArchiveOwnerFileName);
            RequireOrdinaryPath(paths, marker);
            if (!File.Exists(marker) || new FileInfo(marker).Length > 64 * 1024) return false;
            var saved = JsonSerializer.Deserialize<ArchiveOwner>(File.ReadAllText(marker));
            if (saved is not { SchemaVersion: 1, MajorVersion: > 0 } || !OwnedRuntimeId().IsMatch(saved.RuntimeId) ||
                !Path.GetFileName(directory).Equals(saved.RuntimeId.Replace('+', '_'), StringComparison.OrdinalIgnoreCase) ||
                saved.ArchiveSha256 is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigit) ||
                saved.ArchiveFileName is not { Length: > 0 } name || name != Path.GetFileName(name) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !OrdinaryTree(directory, checkpoint)) return false;
            var archive = Path.Combine(directory, name);
            var entries = Directory.GetFileSystemEntries(directory);
            if (entries.Length != 2 || !entries.Contains(archive, StringComparer.OrdinalIgnoreCase) ||
                !entries.Contains(marker, StringComparer.OrdinalIgnoreCase) || !File.Exists(archive) ||
                !string.Equals(SharedRuntimeStore.HashFile(archive, checkpoint), hash, StringComparison.OrdinalIgnoreCase)) return false;
            owner = new OwnedDirectory(directory, saved.RuntimeId, saved.MajorVersion, IsInstall: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private sealed record ArchiveOwner(int SchemaVersion, string RuntimeId, string ArchiveFileName,
        string ArchiveSha256, int MajorVersion);

    private static bool TryReadOwner(AppPaths paths, string directory, out string runtimeId, out string archiveSha256, out int major)
    {
        runtimeId = archiveSha256 = "";
        major = 0;
        try
        {
            var markerPath = Path.Combine(directory, PortableJavaRuntimeService.MarkerFileName);
            RequireOrdinaryPath(paths, markerPath);
            if (!File.Exists(markerPath)) return false;
            var marker = JsonSerializer.Deserialize<PortableJavaRuntimeService.JavaRuntimeMarker>(File.ReadAllText(markerPath));
            if (marker is null || marker.SchemaVersion != PortableJavaRuntimeService.MarkerCacheGeneration ||
                marker.RuntimeId is not { } id || !OwnedRuntimeId().IsMatch(id) ||
                marker.ArchiveSha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit) ||
                !TryMajorVersion(marker.JavaVersion, out major))
                return false;

            var pin = JavaRuntimeCatalog.Releases.FirstOrDefault(release =>
                string.Equals(release.RuntimeId, id, StringComparison.OrdinalIgnoreCase));
            if (pin is not null && (pin.MajorVersion != major ||
                !string.Equals(pin.ArchiveSha256, hash, StringComparison.OrdinalIgnoreCase))) return false;

            var folder = Path.GetFileName(directory);
            if (!string.Equals(folder, id, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(folder, $"java-{major}", StringComparison.OrdinalIgnoreCase)) return false;
            runtimeId = id;
            archiveSha256 = hash;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool TryMajorVersion(string? version, out int major)
    {
        major = 0;
        if (string.IsNullOrWhiteSpace(version)) return false;
        var parts = version.Split('.');
        var feature = parts.Length > 1 && parts[0] == "1" ? parts[1] : parts[0];
        return int.TryParse(feature, NumberStyles.None, CultureInfo.InvariantCulture, out major) && major > 0;
    }

    public static void SweepUnusedSuperseded(AppPaths paths, Logger? logger, Action checkpoint)
    {
        var revision = SharedRuntimeGate.ActivityVersion(paths);
        try
        {
            var installs = InstallParent(paths.JavaRuntimes);
            var archives = ArchiveParent(paths);
            if (!Directory.Exists(installs) && !Directory.Exists(archives)) return;
            Checkpoint();
            using (var gate = TryAcquire(paths))
                if (gate is null) return;

            var stale = DateTime.UtcNow - TimeSpan.FromHours(1);
            if (MaintenancePath.TryRead(paths, installs, out var installsAttributes) &&
                installsAttributes.HasFlag(FileAttributes.Directory))
            {
                foreach (var directory in Directory.EnumerateDirectories(installs))
                {
                    Checkpoint();
                    if (!AbandonedStagingRegex().IsMatch(Path.GetFileName(directory)) ||
                        !MaintenancePath.TryRead(paths, directory, out var attributes) ||
                        !attributes.HasFlag(FileAttributes.Directory) ||
                        Directory.GetLastWriteTimeUtc(directory) > stale ||
                        IsInUse(directory) || !OrdinaryTree(directory, Checkpoint)) continue;
                    DeleteTree(directory);
                }
            }

            foreach (var pin in JavaRuntimeCatalog.Releases)
            {
                Checkpoint();
                var legacy = Path.Combine(installs, pin.LegacyInstallDirectoryName);
                if (!pin.LegacyInstallDirectoryName.Equals(pin.InstallDirectoryName, StringComparison.OrdinalIgnoreCase) &&
                    PortableJavaRuntimeService.DescribeInstalled(paths, pin, legacy, logger) is not null &&
                    PortableJavaRuntimeService.DescribeInstalled(paths, pin, Path.Combine(installs, pin.InstallDirectoryName),
                        logger) is not null && !IsInUse(legacy) && OrdinaryTree(legacy, Checkpoint))
                    DeleteTree(legacy);

                var cache = Path.Combine(archives, pin.RuntimeId.Replace('+', '_'));
                if (!MaintenancePath.TryRead(paths, cache, out var attributes) ||
                    !attributes.HasFlag(FileAttributes.Directory)) continue;
                foreach (var file in Directory.EnumerateFiles(cache, pin.ArchiveFileName + ".*.tmp"))
                {
                    Checkpoint();
                    var name = Path.GetFileName(file);
                    if (name.Length == pin.ArchiveFileName.Length + 37 &&
                        name.StartsWith(pin.ArchiveFileName + ".", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                        Guid.TryParseExact(name.AsSpan(pin.ArchiveFileName.Length + 1, 32), "N", out _) &&
                        MaintenancePath.TryRead(paths, file, out _) && File.GetLastWriteTimeUtc(file) <= stale)
                        Delete(file, false);
                }
            }

            foreach (var directory in ReadUnusedDirectories(paths, checkpoint: Checkpoint)
                         .OrderBy(directory => directory.IsInstall))
            {
                Checkpoint();
                DeleteTree(directory.Path);
            }
        }
        catch (InventoryChangedException)
        {
            // Foreground work will recompute the next inventory.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger?.Warn($"Superseded Java runtimes could not be inspected: {ex.Message}");
        }

        void Checkpoint()
        {
            checkpoint();
            if (revision != SharedRuntimeGate.ActivityVersion(paths))
                throw new InventoryChangedException();
        }

        bool Delete(string path, bool directory)
        {
            Checkpoint();
            using var gate = TryAcquire(paths);
            if (gate is null || revision != SharedRuntimeGate.ActivityVersion(paths))
                throw new InventoryChangedException();
            return MaintenancePath.TryDelete(paths, path, directory);
        }

        bool DeleteTree(string directory)
        {
            Checkpoint();
            if (!MaintenancePath.TryRead(paths, directory, out var attributes) ||
                !attributes.HasFlag(FileAttributes.Directory)) return false;
            var complete = true;
            var marker = Path.Combine(directory, PortableJavaRuntimeService.MarkerFileName);
            var archiveMarker = Path.Combine(directory, ArchiveOwnerFileName);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                Checkpoint();
                if (entry.Equals(marker, StringComparison.OrdinalIgnoreCase) ||
                    entry.Equals(archiveMarker, StringComparison.OrdinalIgnoreCase)) continue;
                if (MaintenancePath.IsProtectedAssetName(Path.GetFileName(entry)) ||
                    !MaintenancePath.TryRead(paths, entry, out var entryAttributes))
                {
                    complete = false;
                    continue;
                }
                complete &= entryAttributes.HasFlag(FileAttributes.Directory)
                    ? DeleteTree(entry) : Delete(entry, false);
            }
            if (!complete || File.Exists(marker) && !Delete(marker, false) ||
                File.Exists(archiveMarker) && !Delete(archiveMarker, false)) return false;
            return Delete(directory, true);
        }
    }

    private sealed class InventoryChangedException : Exception
    {
    }

    [GeneratedRegex(@"^temurin-[0-9][0-9A-Za-z.+-]*$", RegexOptions.IgnoreCase)]
    private static partial Regex OwnedRuntimeId();

    [GeneratedRegex(@"^\.(?:java-[0-9]+|temurin-[0-9][0-9A-Za-z.+-]*)\.install\.[0-9a-f]{32}$", RegexOptions.IgnoreCase)]
    private static partial Regex AbandonedStagingRegex();
}
