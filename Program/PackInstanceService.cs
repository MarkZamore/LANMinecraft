using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Minecraft;

public sealed record PackInstanceContext(string PackDirectory, string GameDirectory, string ClientJar);

public sealed class PackInstanceService : IDisposable
{
    // A cache generation, not a data format: it is bumped to throw the cached
    // work away and redo it, so it is deliberately independent of
    // PortableFormat's version - a release must not cost every player a
    // re-download for an unrelated change.
    private const int StateCacheGeneration = 1;
    private const string StateFileName = ".portable-instance.json";

    internal static readonly HashSet<string> InstanceOwnedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mixin.out",
        "blueprints",
        "crash-reports",
        "debug",
        "downloads",
        "dynamic-data-pack-cache",
        "dynamic-resource-pack-cache",
        "ftbbackups3",
        "ldlib2",
        "local",
        "logs",
        "moddata",
        "moonlight-global-datapacks",
        "saves",
        "schematics",
        "screenshots",
        "xaero"
    };

    internal static readonly HashSet<string> InstanceOwnedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "command_history.txt",
        "observable_announce",
        "options.txt",
        "patchouli_data.json",
        "servers.dat",
        "servers.dat_old",
        "usercache.json",
        "usernamecache.json"
    };

    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions StateJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public PackInstanceService(AppPaths paths, Logger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public string GetInstanceDirectory(string packRelativePath) => _paths.CombineUnderInstances(packRelativePath);

    public async Task<PackInstanceContext> PrepareAsync(string packRelativePath, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => PrepareCore(packRelativePath, token), token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CleanupGeneratedLocalArtifactsAsync(string packRelativePath, bool removeSessionLogs)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var gameDir = GetInstanceDirectory(packRelativePath);
            if (Directory.Exists(gameDir))
            {
                SanitizeInstanceForLocalPlay(gameDir, Path.GetFileName(packRelativePath));
                if (removeSessionLogs)
                {
                    LogCleanupService.RetainRecentSessionDiagnostics(gameDir);
                }
                CleanupDisposableInstancePlaceholders(gameDir);
                CleanupEmptyWorldPlaceholders(_paths.Worlds);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static void CleanupEmptyWorldPlaceholders(string worldsRoot)
    {
        if (!Directory.Exists(worldsRoot)) return;
        foreach (var world in Directory.EnumerateDirectories(worldsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            foreach (var name in new[] { "datapacks", "EnderStorage" })
            {
                var path = Path.Combine(world, name);
                if (Directory.Exists(path) && !Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any())
                {
                    Directory.Delete(path, recursive: true);
                }
            }
        }
    }

    internal static void CleanupDisposableInstancePlaceholders(string gameDir)
    {
        DeleteDefaultOnlyXaeroData(gameDir);
        PruneEmptyDirectories(gameDir);
    }

    private static void DeleteDefaultOnlyXaeroData(string gameDir)
    {
        var root = Path.Combine(gameDir, "xaero");
        if (!Directory.Exists(root)) return;
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
        if (files.Length == 0)
        {
            Directory.Delete(root, recursive: true);
            return;
        }
        if (files.Any(file => !string.Equals(Path.GetFileName(file), "config.txt", StringComparison.OrdinalIgnoreCase)) ||
            files.Any(file => !IsDefaultOnlyXaeroConfig(file)))
        {
            return;
        }
        Directory.Delete(root, recursive: true);
    }

    private static bool IsDefaultOnlyXaeroConfig(string path)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "usingMultiworldDetection:false",
            "ignoreServerLevelId:false",
            "teleportationEnabled:true",
            "usingDefaultTeleportCommand:true",
            "sortType:NONE",
            "sortReversed:false",
            "ignoreHeightmaps:false"
        };
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) ||
                line.StartsWith("dimensionType:", StringComparison.Ordinal))
            {
                continue;
            }
            if (!allowed.Contains(line)) return false;
        }
        return true;
    }

    private static void PruneEmptyDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToArray())
        {
            PruneEmptyDirectoryTree(directory);
        }
    }

    private static void PruneEmptyDirectoryTree(string directory)
    {
        var info = new DirectoryInfo(directory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return;
        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).ToArray())
        {
            PruneEmptyDirectoryTree(child);
        }
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    private PackInstanceContext PrepareCore(string packRelativePath, CancellationToken token)
    {
        var packDir = _paths.CombineUnderPacks(packRelativePath);
        var gameDir = GetInstanceDirectory(packRelativePath);
        if (!Directory.Exists(packDir) || !PackManifestService.HasManifest(packDir))
        {
            throw new DirectoryNotFoundException($"Minecraft pack is missing {PackManifestService.ManifestFileName}: {packDir}");
        }
        var descriptor = PackManifestService.Load(packDir);
        var clientJar = PackManifestService.ResolveClientJarPath(packDir, descriptor);
        if (!File.Exists(clientJar))
        {
            throw new FileNotFoundException("Minecraft client jar is missing from the selected pack.", clientJar);
        }

        Directory.CreateDirectory(gameDir);
        var statePath = Path.Combine(gameDir, StateFileName);
        var state = ReadState(statePath, packRelativePath);

        EnsureMods(packDir, gameDir, state, token);
        SynchronizePackFiles(packDir, gameDir, packRelativePath, descriptor.ClientJar, state, token);
        SanitizeInstanceForLocalPlay(gameDir, Path.GetFileName(packRelativePath));
        state.SchemaVersion = StateCacheGeneration;
        state.PackRelativePath = packRelativePath;
        AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(state, StateJsonOptions));
        return new PackInstanceContext(packDir, gameDir, clientJar);
    }

    private void SynchronizePackFiles(
        string packDir,
        string gameDir,
        string packRelativePath,
        string clientJarName,
        InstanceState state,
        CancellationToken token)
    {
        var previousFiles = new Dictionary<string, SourceFileState>(state.Files, StringComparer.OrdinalIgnoreCase);
        var currentFiles = new Dictionary<string, SourceFileState>(StringComparer.OrdinalIgnoreCase);
        string? conflictRoot = null;
        var conflictCount = 0;

        foreach (var directory in EnumerateSourceDirectories(packDir, clientJarName))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(packDir, directory);
            var destination = Path.Combine(gameDir, relative);
            if (File.Exists(destination))
            {
                conflictRoot ??= CreateConflictRoot(packRelativePath);
                var preservedFile = Path.Combine(conflictRoot, relative + ".user-file");
                Directory.CreateDirectory(Path.GetDirectoryName(preservedFile)!);
                File.Copy(destination, preservedFile, overwrite: true);
                File.Delete(destination);
                conflictCount++;
            }
            Directory.CreateDirectory(destination);
        }

        foreach (var sourcePath in EnumerateSourceFiles(packDir, clientJarName))
        {
            token.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(Path.GetRelativePath(packDir, sourcePath));
            previousFiles.TryGetValue(relative, out var previous);
            var source = ReadSourceState(sourcePath, previous);
            currentFiles[relative] = source;
            var destination = Path.Combine(gameDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(destination))
            {
                conflictRoot ??= CreateConflictRoot(packRelativePath);
                CopySourceFile(sourcePath, Path.Combine(conflictRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                conflictCount++;
                continue;
            }
            var destinationExists = File.Exists(destination);

            if (previous is null)
            {
                if (!destinationExists)
                {
                    CopySourceFile(sourcePath, destination);
                }
                else if (!HashesEqual(HashFile(destination), source.Sha256))
                {
                    conflictRoot ??= CreateConflictRoot(packRelativePath);
                    CopySourceFile(sourcePath, Path.Combine(conflictRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                    conflictCount++;
                }
                continue;
            }

            if (HashesEqual(source.Sha256, previous.Sha256))
            {
                continue;
            }

            if (!destinationExists)
            {
                conflictRoot ??= CreateConflictRoot(packRelativePath);
                CopySourceFile(sourcePath, Path.Combine(conflictRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                conflictCount++;
                continue;
            }

            var destinationHash = HashFile(destination);
            if (HashesEqual(destinationHash, previous.Sha256))
            {
                CopySourceFile(sourcePath, destination);
            }
            else if (!HashesEqual(destinationHash, source.Sha256))
            {
                conflictRoot ??= CreateConflictRoot(packRelativePath);
                CopySourceFile(sourcePath, Path.Combine(conflictRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                conflictCount++;
            }
        }

        foreach (var removed in previousFiles.Where(entry => !currentFiles.ContainsKey(entry.Key)))
        {
            token.ThrowIfCancellationRequested();
            var destination = Path.Combine(gameDir, removed.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(destination)) continue;
            if (HashesEqual(HashFile(destination), removed.Value.Sha256))
            {
                File.Delete(destination);
                DeleteEmptyParents(Path.GetDirectoryName(destination), gameDir);
            }
            else
            {
                _logger.Warn($"Pack removed {removed.Key}, but the locally modified instance file was preserved.");
            }
        }

        state.Files = currentFiles;
        if (conflictCount > 0)
        {
            _logger.Warn($"Pack instance synchronization preserved {conflictCount} local conflict(s). New pack files: {conflictRoot}");
        }
    }

    private void EnsureMods(string packDir, string gameDir, InstanceState state, CancellationToken token)
    {
        var source = Path.Combine(packDir, "mods");
        var destination = Path.Combine(gameDir, "mods");
        if (!Directory.Exists(source))
        {
            if (TryGetAttributes(destination, out var existingAttributes) &&
                (existingAttributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(destination);
            }
            else if (Directory.Exists(destination))
            {
                foreach (var (relative, previous) in state.ModFiles)
                {
                    token.ThrowIfCancellationRequested();
                    var path = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(path) && HashesEqual(HashFile(path), previous.Sha256)) File.Delete(path);
                }
            }
            Directory.CreateDirectory(destination);
            state.ModsMode = "Empty";
            state.ModFiles.Clear();
            return;
        }

        if (TryGetAttributes(destination, out var attributes) && (attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(destination);
        }

        var managedModsDirectory =
            string.Equals(state.ModsMode, "HardLink", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state.ModsMode, "Copy", StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(destination) &&
            !managedModsDirectory &&
            Directory.EnumerateFileSystemEntries(destination).Any())
        {
            var conflictRoot = CreateConflictRoot(state.PackRelativePath);
            var preserved = Path.Combine(conflictRoot, "mods-local");
            Directory.Move(destination, preserved);
            _logger.Warn($"Existing instance mods were preserved at {preserved}.");
        }

        Directory.CreateDirectory(destination);
        var allHardLinks = MirrorMods(source, destination, state, token);
        state.ModsMode = allHardLinks ? "HardLink" : "Copy";
    }

    private static bool MirrorMods(string sourceDir, string destinationDir, InstanceState state, CancellationToken token)
    {
        var current = new Dictionary<string, SourceFileState>(StringComparer.OrdinalIgnoreCase);
        var allHardLinks = true;
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(Path.GetRelativePath(sourceDir, sourcePath));
            state.ModFiles.TryGetValue(relative, out var previous);
            var source = ReadSourceState(sourcePath, previous);
            current[relative] = source;
            var destination = Path.Combine(destinationDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(destination) ||
                new FileInfo(destination).Length != source.SizeBytes ||
                !HashesEqual(HashFile(destination), source.Sha256))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination)) File.Delete(destination);
                if (!TryCreateHardLink(destination, sourcePath))
                {
                    CopySourceFile(sourcePath, destination);
                    allHardLinks = false;
                }
            }
            else if (!string.Equals(state.ModsMode, "HardLink", StringComparison.OrdinalIgnoreCase))
            {
                allHardLinks = false;
            }
        }

        foreach (var file in Directory.EnumerateFiles(destinationDir, "*", SearchOption.AllDirectories).ToArray())
        {
            var relative = NormalizeRelativePath(Path.GetRelativePath(destinationDir, file));
            if (!current.ContainsKey(relative)) File.Delete(file);
        }
        foreach (var directory in Directory.EnumerateDirectories(destinationDir, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        state.ModFiles = current;
        return allHardLinks;
    }

    private IEnumerable<string> EnumerateSourceFiles(string packDir, string clientJarName)
    {
        foreach (var entry in EnumerateCanonicalEntries(packDir, clientJarName))
        {
            if (File.Exists(entry)) yield return entry;
        }
    }

    private IEnumerable<string> EnumerateSourceDirectories(string packDir, string clientJarName)
    {
        foreach (var entry in EnumerateCanonicalEntries(packDir, clientJarName))
        {
            if (Directory.Exists(entry)) yield return entry;
        }
    }

    private IEnumerable<string> EnumerateCanonicalEntries(string packDir, string clientJarName)
    {
        var pending = new Stack<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(packDir))
        {
            var name = Path.GetFileName(entry);
            if (string.Equals(name, "mods", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, clientJarName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, PackManifestService.ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, PortablePackSyncService.SourceMarkerFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, PortablePackSyncService.SyncStateFileName, StringComparison.OrdinalIgnoreCase) ||
                InstanceOwnedDirectories.Contains(name) ||
                InstanceOwnedFiles.Contains(name) ||
                name.StartsWith("XaeroWaypoints_BACKUP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                if (!ShouldExcludeDirectory(packDir, entry)) pending.Push(entry);
            }
            else if (File.Exists(entry) && !ShouldExcludeSourceFile(entry))
            {
                yield return entry;
            }
        }

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Pack contains an unsupported directory link: {directory}");
            }
            yield return directory;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Directory.Exists(entry))
                {
                    if (!ShouldExcludeDirectory(packDir, entry)) pending.Push(entry);
                }
                else if (File.Exists(entry) && !ShouldExcludeSourceFile(entry))
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>The pack root that holds data for the launcher, not the game.</summary>
    internal const string LauncherDataRoot = "launcher";

    private static bool ShouldExcludeDirectory(string packDir, string directory)
    {
        var relative = NormalizeRelativePath(Path.GetRelativePath(packDir, directory));
        return relative.Equals("config/jei/world/server", StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith("config/jei/world/server/", StringComparison.OrdinalIgnoreCase) ||
               // The controls preset and whatever joins it: read by the launcher
               // straight from the pack, meaningless inside the game directory.
               relative.Equals(LauncherDataRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldExcludeSourceFile(string path)
    {
        if (!path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 1024 * 1024) return false;
            var text = File.ReadAllText(path);
            return text.Contains("serverIP", StringComparison.OrdinalIgnoreCase) &&
                   text.Contains("serverProxyIP", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void SanitizeInstanceForLocalPlay(string gameDir, string buildName)
    {
        // config/jei/world/server holds the player's own bookmarks for peer
        // worlds - packs never seed it (ShouldExcludeDirectory keeps it out of
        // the sync), so deleting it here only ever destroyed player data.
        var configRoot = Path.Combine(gameDir, "config");
        if (Directory.Exists(configRoot))
        {
            foreach (var file in Directory.EnumerateFiles(configRoot, "*.toml", SearchOption.AllDirectories).ToArray())
            {
                if (!ShouldExcludeSourceFile(file)) continue;
                File.Delete(file);
                DeleteEmptyParents(Path.GetDirectoryName(file), configRoot);
            }
        }

        var clientConfigPath = Path.Combine(gameDir, "kubejs", "config", "client.json");
        if (!File.Exists(clientConfigPath)) return;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(clientConfigPath)) as JsonObject;
            if (root is null || root["window_title"] is null) return;
            var title = string.IsNullOrWhiteSpace(buildName) ? "Minecraft" : buildName.Trim();
            if (string.Equals(root["window_title"]?.GetValue<string>(), title, StringComparison.Ordinal)) return;
            root["window_title"] = title;
            AtomicFile.WriteAllText(clientConfigPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Deletes instance files and drops them from the recorded pack-file map.
    /// Both halves matter: the removed teleport layer rewrote these pack files,
    /// so a state that still remembers its edits makes the three-way merge read
    /// the pack's own copies as local modifications and divert them to
    /// PackConflicts instead of installing them.
    /// </summary>
    internal static void ForgetInstanceFiles(
        string gameDirectory,
        string packRelativePath,
        IReadOnlyCollection<string> relativePaths,
        Logger? logger)
    {
        foreach (var relativePath in relativePaths)
        {
            DeleteInstanceFile(gameDirectory, relativePath, logger);
        }

        var statePath = Path.Combine(gameDirectory, StateFileName);
        if (!File.Exists(statePath)) return;
        try
        {
            var state = JsonSerializer.Deserialize<InstanceState>(File.ReadAllText(statePath), StateJsonOptions);
            if (state is null) return;
            var files = new Dictionary<string, SourceFileState>(state.Files, StringComparer.OrdinalIgnoreCase);
            foreach (var relativePath in relativePaths)
            {
                files.Remove(NormalizeRelativePath(relativePath));
            }
            state.Files = files;
            state.PackRelativePath = packRelativePath;
            AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(state, StateJsonOptions));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger?.Warn($"Pack instance state still records the removed files: {ex.Message}");
        }
    }

    private static void DeleteInstanceFile(string gameDirectory, string relativePath, Logger? logger)
    {
        var path = Path.Combine(gameDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (!File.Exists(path)) return;
            File.Delete(path);
            DeleteEmptyParents(Path.GetDirectoryName(path), gameDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warn($"Instance file {relativePath} could not be removed: {ex.Message}");
        }
    }

    private InstanceState ReadState(string statePath, string packRelativePath)
    {
        try
        {
            if (File.Exists(statePath))
            {
                var state = JsonSerializer.Deserialize<InstanceState>(File.ReadAllText(statePath), StateJsonOptions);
                if (state?.SchemaVersion == StateCacheGeneration)
                {
                    state.Files = new Dictionary<string, SourceFileState>(state.Files, StringComparer.OrdinalIgnoreCase);
                    state.ModFiles = new Dictionary<string, SourceFileState>(state.ModFiles, StringComparer.OrdinalIgnoreCase);
                    return state;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.Warn($"Pack instance state could not be read and will be rebuilt: {ex.Message}");
        }

        return new InstanceState { PackRelativePath = packRelativePath };
    }

    private string CreateConflictRoot(string packRelativePath)
    {
        var root = Path.Combine(
            _paths.PackConflicts,
            SafePackName(packRelativePath),
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture));
        _paths.EnsureUnderRoot(root);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string SafePackName(string relativePath) => relativePath
        .Replace(Path.DirectorySeparatorChar, '_')
        .Replace(Path.AltDirectorySeparatorChar, '_');

    private static SourceFileState ReadSourceState(string path, SourceFileState? previous)
    {
        var info = new FileInfo(path);
        if (previous is not null &&
            previous.SizeBytes == info.Length &&
            previous.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks &&
            !string.IsNullOrWhiteSpace(previous.Sha256))
        {
            return new SourceFileState
            {
                SizeBytes = info.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                Sha256 = previous.Sha256
            };
        }

        return new SourceFileState
        {
            SizeBytes = info.Length,
            LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
            Sha256 = HashFile(path)
        };
    }

    private static void CopySourceFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        File.Copy(source, destination, overwrite: true);
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool HashesEqual(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static void DeleteEmptyParents(string? path, string stopAt)
    {
        var stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(path) &&
               !string.Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), stop, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path).Any()) return;
            Directory.Delete(path);
            path = Path.GetDirectoryName(path);
        }
    }

    private static bool TryCreateHardLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return CreateHardLink(linkPath, targetPath, IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    private static string ResolveLinkTarget(string linkPath)
    {
        var target = new DirectoryInfo(linkPath).LinkTarget
            ?? throw new InvalidDataException($"Directory link has no target: {linkPath}");
        return Path.IsPathRooted(target)
            ? Path.GetFullPath(target)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkPath)!, target));
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (IOException)
        {
            attributes = default;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            attributes = default;
            return false;
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

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private sealed class InstanceState
    {
        public int SchemaVersion { get; set; } = StateCacheGeneration;
        public string PackRelativePath { get; set; } = "";
        public string ModsMode { get; set; } = "";
        public Dictionary<string, SourceFileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SourceFileState> ModFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SourceFileState
    {
        public long SizeBytes { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string Sha256 { get; set; } = "";
    }
}
