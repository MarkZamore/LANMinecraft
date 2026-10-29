using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;

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
        "launcher-runtime",
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
    private readonly Func<PackRuntimeDescriptor, SteamTransportBuild?> _steamTransportForPack;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions StateJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public PackInstanceService(AppPaths paths, Logger logger) : this(paths, logger, SteamTransportCatalog.Find)
    {
    }

    internal PackInstanceService(AppPaths paths, Logger logger,
        Func<PackRuntimeDescriptor, SteamTransportBuild?> steamTransportForPack)
    {
        _paths = paths;
        _logger = logger;
        _steamTransportForPack = steamTransportForPack;
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
        foreach (var world in WorldLocations.Enumerate(worldsRoot))
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

        // And the shell a world can leave behind. This used to be the common
        // case: the game deleted a world through a junction standing in the
        // build's saves, so the files went, the junction went, and the folder
        // they pointed at stayed holding a name and nothing else. The game now
        // deletes a real folder and takes it with it, but a shell from before
        // that - or from an interrupted transfer - is still worth nothing and
        // still holds a name. A folder with no file anywhere beneath it has
        // nothing in it to lose.
        foreach (var build in Directory.EnumerateDirectories(worldsRoot))
        {
            if (WorldLocations.IsWorld(build)) continue;
            foreach (var shell in Directory.EnumerateDirectories(build).ToList())
            {
                if (WorldLocations.IsWorld(shell) || !SavesFolderService.IsEmptyDirectory(shell)) continue;
                try
                {
                    Directory.Delete(shell, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    internal static void CleanupDisposableInstancePlaceholders(string gameDir)
    {
        DeleteDefaultOnlyXaeroData(gameDir);
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


    private PackInstanceContext PrepareCore(string packRelativePath, CancellationToken token)
    {
        var packDir = _paths.CombineUnderPacks(packRelativePath);
        var gameDir = GetInstanceDirectory(packRelativePath);
        if (new ClientPresenceService(_paths, _logger).ReadLiveSessions().Any(session =>
                string.Equals(NormalizeRelativePath(session.PackRelativePath), NormalizeRelativePath(packRelativePath),
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Close Minecraft before updating its instance.");
        }
        if (PortablePackSyncService.IsUpdatePending(packDir))
        {
            throw new InvalidOperationException("The pack update is incomplete. Retry Play with an internet connection before starting Minecraft.");
        }
        if (!Directory.Exists(packDir) || !PackManifestService.HasManifest(packDir))
        {
            throw new DirectoryNotFoundException($"Minecraft pack is missing {PackManifestService.ManifestFileName}: {packDir}");
        }
        var descriptor = PackManifestService.Load(packDir);
        var managedPaths = PortablePackSyncService.ReadManagedPaths(packDir);
        // Only ever used as a name to skip while mirroring the pack over the
        // instance - the jar itself belongs to the runtime, not here - so a
        // pack that brings none needs nothing in its place. It was checked for
        // existence here as well, which was ceremony even when every pack
        // carried one: the value the check guarded is never read.
        var clientJar = descriptor.ClientJar.Length == 0
            ? ""
            : PackManifestService.ResolveClientJarPath(packDir, descriptor);

        Directory.CreateDirectory(gameDir);
        EnsureOrdinaryPath(gameDir, gameDir);
        var statePath = Path.Combine(gameDir, StateFileName);
        EnsureOrdinaryPath(gameDir, statePath);
        var state = ReadState(statePath, packRelativePath);

        // One preparation is one snapshot, however many personal files it
        // preserves. Per-file snapshots would let the next startup's retention
        // pass erase part of this same update's backup after five files.
        string? conflictRoot = null;
        string GetConflictRoot() => conflictRoot ??= CreateConflictRoot(packRelativePath);
        RemoveVerifiedRetiredFiles(packDir, gameDir, token);
        EnsureMods(packDir, gameDir, state, managedPaths, _steamTransportForPack(descriptor), GetConflictRoot, token);
        SynchronizePackFiles(packDir, gameDir, packRelativePath, descriptor.ClientJar, state, managedPaths, GetConflictRoot, token);
        state.SchemaVersion = StateCacheGeneration;
        state.PackRelativePath = packRelativePath;
        state.PackRevision = ReadPackRevision(packDir);
        AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(state, StateJsonOptions));
        return new PackInstanceContext(packDir, gameDir, clientJar);
    }

    private void RemoveVerifiedRetiredFiles(string packDir, string gameDir, CancellationToken token)
    {
        foreach (var retired in PortablePackSyncService.ReadRetiredFiles(packDir))
        {
            token.ThrowIfCancellationRequested();
            var rootName = retired.Path.Split('/')[0];
            if (InstanceOwnedDirectories.Contains(rootName) || InstanceOwnedFiles.Contains(rootName)) continue;
            var destination = Path.GetFullPath(Path.Combine(gameDir, retired.Path.Replace('/', Path.DirectorySeparatorChar)));
            _paths.EnsureUnderRoot(destination);
            EnsureOrdinaryPath(gameDir, destination);
            if (!File.Exists(destination) || new FileInfo(destination).Length != retired.SizeBytes ||
                !HashesEqual(HashFile(destination), retired.Sha256)) continue;
            File.Delete(destination);
            DeleteEmptyParents(Path.GetDirectoryName(destination), gameDir);
        }
    }

    private void SynchronizePackFiles(
        string packDir,
        string gameDir,
        string packRelativePath,
        string clientJarName,
        InstanceState state,
        IReadOnlySet<string>? managedPaths,
        Func<string> getConflictRoot,
        CancellationToken token)
    {
        var previousFiles = new Dictionary<string, SourceFileState>(state.Files, StringComparer.OrdinalIgnoreCase);
        var currentFiles = new Dictionary<string, SourceFileState>(StringComparer.OrdinalIgnoreCase);
        string? conflictRoot = null;
        var conflictCount = 0;
        var owned = PackOwnedFileService.Load(packDir, _logger);
        var previousOwned = new HashSet<string>(state.OwnedFiles, StringComparer.OrdinalIgnoreCase);
        var currentOwned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownedWrites = 0;

        foreach (var directory in EnumerateSourceDirectories(packDir, clientJarName))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(packDir, directory);
            if (managedPaths is not null && !managedPaths.Any(path => path.StartsWith(
                    NormalizeRelativePath(relative) + "/", StringComparison.OrdinalIgnoreCase))) continue;
            var destination = Path.Combine(gameDir, relative);
            EnsureOrdinaryPath(gameDir, destination);
            if (File.Exists(destination))
            {
                conflictRoot ??= getConflictRoot();
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
            if (managedPaths is not null && !managedPaths.Contains(relative)) continue;
            previousFiles.TryGetValue(relative, out var previous);
            var source = ReadSourceState(sourcePath);
            currentFiles[relative] = source;
            if (owned.Owns(relative)) currentOwned.Add(relative);
            var destination = Path.Combine(gameDir, relative.Replace('/', Path.DirectorySeparatorChar));
            EnsureOrdinaryPath(gameDir, destination);
            if (Directory.Exists(destination))
            {
                conflictRoot ??= getConflictRoot();
                var preservedDirectory = Path.Combine(conflictRoot, relative.Replace('/', Path.DirectorySeparatorChar) + ".user-directory");
                Directory.CreateDirectory(Path.GetDirectoryName(preservedDirectory)!);
                Directory.Move(destination, preservedDirectory);
                conflictCount++;
            }
            var destinationExists = File.Exists(destination);

            // A file the pack owns is not negotiated. The local copy is kept
            // beside the other conflicts so nothing is thrown away, and then
            // the pack's version is written where the game will read it.
            if (owned.Owns(relative))
            {
                if (!destinationExists)
                {
                    CopySourceFile(sourcePath, destination);
                    continue;
                }
                var localHash = HashFile(destination);
                if (HashesEqual(localHash, source.Sha256)) continue;
                // Unless a copy of exactly these bytes is already kept. A path
                // is on the owned list precisely because the game rewrites it,
                // so the same rewrite arrives at every update, and the
                // twenty-third identical copy of it preserves nothing the first
                // one does not - it only buries the copies that matter.
                if (!IsAlreadyPreserved(packRelativePath, relative, localHash))
                {
                    conflictRoot ??= getConflictRoot();
                    PreserveLocalFile(destination, conflictRoot, relative);
                }
                CopySourceFile(sourcePath, destination);
                ownedWrites++;
                continue;
            }

            if (previous is null)
            {
                if (!destinationExists)
                {
                    CopySourceFile(sourcePath, destination);
                }
                else if (!HashesEqual(HashFile(destination), source.Sha256))
                {
                    conflictRoot ??= getConflictRoot();
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
                conflictRoot ??= getConflictRoot();
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
                conflictRoot ??= getConflictRoot();
                CopySourceFile(sourcePath, Path.Combine(conflictRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                conflictCount++;
            }
        }

        foreach (var removed in previousFiles.Where(entry => !currentFiles.ContainsKey(entry.Key)))
        {
            token.ThrowIfCancellationRequested();
            var rootName = removed.Key.Split('/')[0];
            if (InstanceOwnedDirectories.Contains(rootName) || InstanceOwnedFiles.Contains(rootName)) continue;
            var destination = Path.Combine(gameDir, removed.Key.Replace('/', Path.DirectorySeparatorChar));
            EnsureOrdinaryPath(gameDir, destination);
            if (!File.Exists(destination)) continue;
            if (HashesEqual(HashFile(destination), removed.Value.Sha256))
            {
                File.Delete(destination);
                DeleteEmptyParents(Path.GetDirectoryName(destination), gameDir);
            }
            else if (owned.Owns(removed.Key) || previousOwned.Contains(removed.Key))
            {
                // The pack owned it and the pack dropped it; a local copy left
                // behind would be a file the pack no longer knows about, still
                // being read by the game.
                conflictRoot ??= getConflictRoot();
                PreserveLocalFile(destination, conflictRoot, removed.Key);
                File.Delete(destination);
                DeleteEmptyParents(Path.GetDirectoryName(destination), gameDir);
                ownedWrites++;
            }
            else
            {
                _logger.Warn($"Pack removed {removed.Key}, but the locally modified instance file was preserved.");
            }
        }

        state.Files = currentFiles;
        state.OwnedFiles = currentOwned;
        if (ownedWrites > 0)
        {
            _logger.Info(
                $"{ownedWrites} file(s) the pack owns were rewritten from it; " +
                (conflictRoot is null
                    ? "what was there is already kept from an earlier update"
                    : $"what was there is in {conflictRoot}"));
        }
        if (conflictCount > 0)
        {
            _logger.Warn($"Pack instance synchronization preserved {conflictCount} local conflict(s). New pack files: {conflictRoot}");
        }
    }

    private void EnsureMods(string packDir, string gameDir, InstanceState state, IReadOnlySet<string>? managedPaths,
        SteamTransportBuild? transport, Func<string> getConflictRoot, CancellationToken token)
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
                foreach (var path in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).ToArray())
                {
                    token.ThrowIfCancellationRequested();
                    EnsureOrdinaryPath(destination, path);
                    var relative = NormalizeRelativePath(Path.GetRelativePath(destination, path));
                    if (IsCurrentLauncherTransport(relative, path, transport)) continue;
                    if (!state.ModFiles.TryGetValue(relative, out var previous) ||
                        !HashesEqual(HashFile(path), previous.Sha256))
                        PreserveLocalFile(path, getConflictRoot(), "mods/" + relative);
                    File.Delete(path);
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
            Directory.EnumerateFileSystemEntries(destination).Any(entry => !File.Exists(entry) ||
                !IsCurrentLauncherTransport(NormalizeRelativePath(Path.GetRelativePath(destination, entry)), entry, transport)))
        {
            var transportPath = transport is null ? "" : Path.Combine(destination, transport.FileName);
            var keepTransport = File.Exists(transportPath) &&
                IsCurrentLauncherTransport(transport!.FileName, transportPath, transport);
            var conflictRoot = getConflictRoot();
            var preserved = Path.Combine(conflictRoot, "mods-local");
            Directory.Move(destination, preserved);
            if (keepTransport)
            {
                Directory.CreateDirectory(destination);
                File.Move(Path.Combine(preserved, transport!.FileName), transportPath);
            }
            _logger.Warn($"Existing instance mods were preserved at {preserved}.");
        }

        Directory.CreateDirectory(destination);
        EnsureOrdinaryPath(packDir, source);
        var allHardLinks = MirrorMods(source, destination, state, managedPaths, transport, getConflictRoot, token);
        state.ModsMode = allHardLinks ? "HardLink" : "Copy";
    }

    private bool MirrorMods(string sourceDir, string destinationDir, InstanceState state, IReadOnlySet<string>? managedPaths,
        SteamTransportBuild? transport, Func<string> getConflictRoot, CancellationToken token)
    {
        var current = new Dictionary<string, SourceFileState>(StringComparer.OrdinalIgnoreCase);
        var allHardLinks = true;
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(Path.GetRelativePath(sourceDir, sourcePath));
            if (managedPaths is not null && !managedPaths.Contains("mods/" + relative)) continue;
            EnsureOrdinaryPath(sourceDir, sourcePath);
            var source = ReadSourceState(sourcePath);
            current[relative] = source;
            var destination = Path.Combine(destinationDir, relative.Replace('/', Path.DirectorySeparatorChar));
            EnsureOrdinaryPath(destinationDir, destination);
            if (!File.Exists(destination) ||
                new FileInfo(destination).Length != source.SizeBytes ||
                !HashesEqual(HashFile(destination), source.Sha256))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    if (!state.ModFiles.TryGetValue(relative, out var previous) ||
                        !HashesEqual(HashFile(destination), previous.Sha256))
                        PreserveLocalFile(destination, getConflictRoot(), "mods/" + relative);
                    File.Delete(destination);
                }
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
            if (current.ContainsKey(relative)) continue;
            EnsureOrdinaryPath(destinationDir, file);
            if (IsCurrentLauncherTransport(relative, file, transport)) continue;
            // A previous managed file may be removed. Unknown additions are
            // kept outside the active mod path so duplicate IDs cannot load,
            // and the player retains their bytes in PackConflicts.
            if (!state.ModFiles.TryGetValue(relative, out var previous) ||
                !HashesEqual(HashFile(file), previous.Sha256))
                PreserveLocalFile(file, getConflictRoot(), "mods/" + relative);
            File.Delete(file);
        }
        foreach (var directory in Directory.EnumerateDirectories(destinationDir, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            EnsureOrdinaryPath(destinationDir, directory);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        state.ModFiles = current;
        return allHardLinks;
    }

    private static bool IsCurrentLauncherTransport(string relative, string path, SteamTransportBuild? transport) =>
        transport is not null && relative.Equals(transport.FileName, StringComparison.Ordinal) &&
        new FileInfo(path).Length == transport.SizeBytes && HashesEqual(HashFile(path), transport.Sha256);

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
                string.Equals(name, PortablePackSyncService.ApplyPendingFileName, StringComparison.OrdinalIgnoreCase) ||
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
            else if (File.Exists(entry))
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
                else if (File.Exists(entry))
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
                    state.Files = state.Files.Where(entry => IsSafeStatePath(entry.Key))
                        .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
                    state.ModFiles = state.ModFiles.Where(entry => IsSafeStatePath(entry.Key))
                        .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
                    state.OwnedFiles = new HashSet<string>(state.OwnedFiles.Where(IsSafeStatePath), StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// How many of a pack's conflict snapshots are kept.
    ///
    /// One is made per update that had anything to set aside, and nothing ever
    /// reads them back - they are there for the day somebody goes looking for a
    /// setting an update took away. Five updates is as far as that day ever
    /// reaches, and the alternative is a folder that only grows: thirty-one
    /// snapshots of one pack on the machine this was written for, and
    /// twenty-nine of them the same config a mod rewrites at every start.
    /// </summary>
    internal const int KeptConflictSnapshots = 5;

    /// <summary>Drops all but the newest snapshots of each pack's conflicts.</summary>
    internal static void PruneOldConflictSnapshots(string conflictsRoot)
    {
        if (!Directory.Exists(conflictsRoot)) return;
        foreach (var pack in Directory.EnumerateDirectories(conflictsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            // A snapshot is named for the moment it was made, yyyyMMdd-HHmmss
            // and ticks, so newest first by name is newest first by clock.
            var older = Directory.EnumerateDirectories(pack, "*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .Skip(KeptConflictSnapshots)
                .ToArray();
            foreach (var snapshot in older)
            {
                try
                {
                    Directory.Delete(snapshot, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            try
            {
                if (!Directory.EnumerateFileSystemEntries(pack).Any()) Directory.Delete(pack);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Whether a copy of exactly these bytes is already kept for this path.</summary>
    private bool IsAlreadyPreserved(string packRelativePath, string relative, string hash)
    {
        var root = Path.Combine(_paths.PackConflicts, SafePackName(packRelativePath));
        if (!Directory.Exists(root)) return false;
        var name = relative.Replace('/', Path.DirectorySeparatorChar) + ".user-file";
        foreach (var snapshot in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var candidate = Path.Combine(snapshot, name);
            if (!File.Exists(candidate)) continue;
            try
            {
                if (HashesEqual(HashFile(candidate), hash)) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return false;
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

    /// <summary>The revision the pack folder was last synced to, or empty.</summary>
    private static string ReadPackRevision(string packDir)
    {
        try
        {
            var statePath = Path.Combine(packDir, PortablePackSyncService.SyncStateFileName);
            if (!File.Exists(statePath)) return "";
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            return document.RootElement.TryGetProperty("revision", out var revision)
                ? revision.GetString() ?? ""
                : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable means "assume the pack moved", which costs a hashing
            // pass and never costs correctness.
            return "";
        }
    }

    /// <summary>Reads the actual bytes; a revision or timestamp is not integrity proof.</summary>
    private static SourceFileState ReadSourceState(string path)
    {
        var info = new FileInfo(path);
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

    /// <summary>
    /// Puts the instance's copy of a file into the conflicts folder before the
    /// pack's version replaces it, under the same name plus ".user-file" - the
    /// same shape the directory conflicts use, so one folder holds everything
    /// that was set aside and a player can put any of it back by hand.
    /// </summary>
    private static void PreserveLocalFile(string destination, string conflictRoot, string relative)
    {
        var preserved = Path.Combine(
            conflictRoot, relative.Replace('/', Path.DirectorySeparatorChar) + ".user-file");
        Directory.CreateDirectory(Path.GetDirectoryName(preserved)!);
        // If preservation fails the update stops before removing the original.
        File.Copy(destination, preserved, overwrite: true);
    }

    private static bool IsSafeStatePath(string relative) =>
        !string.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative) &&
        !relative.Contains(':') && !relative.Split('/', '\\').Any(part => part is ".." or "." or "");

    private static void EnsureOrdinaryPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative != "." && !IsSafeStatePath(NormalizeRelativePath(relative)))
            throw new InvalidDataException("Instance synchronization path leaves its root.");
        var current = Path.GetFullPath(root);
        var segments = relative == "." ? Array.Empty<string>() : relative.Split(Path.DirectorySeparatorChar);
        foreach (var segment in segments.Prepend(""))
        {
            if (segment.Length > 0) current = Path.Combine(current, segment);
            if (TryGetAttributes(current, out var attributes) && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Instance synchronization refuses a filesystem link: {current}");
        }
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
        public HashSet<string> OwnedFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The pack revision these hashes were taken from, so that a new one
        /// throws them away. See <see cref="ReadSourceState"/> for why they
        /// cannot be trusted across an update.
        /// </summary>
        public string PackRevision { get; set; } = "";
    }

    private sealed class SourceFileState
    {
        public long SizeBytes { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string Sha256 { get; set; } = "";
    }
}
