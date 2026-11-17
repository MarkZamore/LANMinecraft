using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Minecraft;

/// <summary>
/// The one copy of the game every build runs from, and the sweep that takes
/// out of it whatever no build asks for any more.
/// </summary>
/// <remarks>
/// Nothing in here belongs to a build, which is the whole reason it can be
/// shared. Mojang's asset store is addressed by the hash of each file's
/// contents; a library sits at a path built out of its own group, name and
/// version; a version id names exactly one profile. Two builds that want the
/// same thing want the same bytes at the same path, so the second one finds it
/// already there.
///
/// Each build records the files it uses. The sweep consults all builds and
/// only collects files in managed roots or explicitly recorded profile files.
/// </remarks>
public static class SharedRuntimeStore
{
    private const string RuntimeStateFileName = ".portable-runtime.json";
    private const string ProfileOwnerFileName = ".portable-profile-owner.json";
    private const string LoaderReceiptFileName = ".portable-loader.json";
    private static readonly JsonSerializerOptions OwnerJson = new(JsonSerializerDefaults.Web);

    /// <summary>Mojang's assets, addressed by content hash.</summary>
    public static string Assets(AppPaths paths) => Path.Combine(Root(paths), "assets");

    /// <summary>The libraries, in the Maven layout their coordinates give them.</summary>
    public static string Libraries(AppPaths paths) => Path.Combine(Root(paths), "libraries");

    /// <summary>Version and loader profiles, one directory per id.</summary>
    public static string Versions(AppPaths paths) => Path.Combine(Root(paths), "versions");

    /// <summary>Mojang's own Java, which runs the loader installer.</summary>
    public static string Runtime(AppPaths paths) => Path.Combine(Root(paths), "runtime");

    /// <summary>The flat asset layout the oldest versions read instead of the store.</summary>
    public static string Resources(AppPaths paths) => Path.Combine(Root(paths), "resources");

    // Loader installers can create libraries their profile never mentions. Sweep
    // profile files only with an ownership record and leave the Maven tree intact.
    private static readonly string[] AlwaysSweepable = ["assets", "runtime"];

    /// <summary>
    /// Everything a runtime state can name: the store, and the build folders
    /// whose own files are listed beside it. Paths in a state are relative to
    /// this, so one list can hold both.
    /// </summary>
    public static string Anchor(AppPaths paths) => Path.GetFullPath(paths.Launcher);

    private static string Root(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths.SharedRuntime;
    }

    /// <summary>Records the files verified during a successful runtime preparation.</summary>
    internal static void RecordProfileOwnership(AppPaths paths, string profileId,
        PackRuntimeDescriptor descriptor, IEnumerable<string> verifiedFiles, Logger? logger = null)
    {
        if (!ValidName(profileId) || !ValidName(descriptor.MinecraftVersion)) return;
        var versions = Versions(paths);
        var profileDirectory = Path.Combine(versions, profileId);
        var ownerPath = Path.Combine(profileDirectory, ProfileOwnerFileName);
        try
        {
            if (!IsOrdinaryUnder(paths.Root, Anchor(paths), directory: true) ||
                !IsOrdinaryUnder(Anchor(paths), profileDirectory, directory: true)) return;
            if (Exists(ownerPath) && ReadProfileOwner(profileDirectory) is null) return;

            var files = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in verifiedFiles.Append(Path.Combine(profileDirectory, LoaderReceiptFileName)))
            {
                var full = Path.GetFullPath(path);
                if (!IsOrdinaryUnder(profileDirectory, full, directory: false)) continue;
                var relative = Path.GetRelativePath(profileDirectory, full).Replace(Path.DirectorySeparatorChar, '/');
                if (!relative.Equals(ProfileOwnerFileName, StringComparison.OrdinalIgnoreCase))
                    files[relative] = HashFile(full);
            }

            if (!files.ContainsKey(profileId + ".json")) return;
            var isBase = profileId.Equals(descriptor.MinecraftVersion, StringComparison.OrdinalIgnoreCase);
            var owner = new ProfileOwner(1, profileId, descriptor.MinecraftVersion,
                isBase ? PackLoaderKind.Vanilla : descriptor.Loader.Type,
                isBase ? null : PackRuntimeService.CanonicalLoaderVersion(descriptor), files);
            AtomicFile.WriteAllTextIfChanged(ownerPath, JsonSerializer.Serialize(owner, OwnerJson));
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            logger?.Warn($"Could not record shared profile ownership ({ex.Message}): {profileId}");
        }
    }

    /// <summary>Removes unused assets and proven launcher-owned profile files.</summary>
    public static int Sweep(AppPaths paths, Logger? logger = null) =>
        Sweep(paths, logger, () => new ClientPresenceService(paths, logger).ReadLiveSessions().Count != 0);

    internal static int Sweep(AppPaths paths, Logger? logger, Func<bool> hasLiveGame)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!Directory.Exists(Root(paths))) return 0;
        try
        {
            using var gate = SharedRuntimeGate.TryAcquire(paths);
            if (gate is null || hasLiveGame()) return 0;
            return SweepCore(paths, logger, static () => { }, (path, directory) =>
                MaintenancePath.TryDelete(paths, path, directory), requirePreparedPacks: false);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            logger?.Warn($"Shared runtime cleanup could not finish ({ex.Message}).");
            return 0;
        }
    }

    internal static int SweepIdle(AppPaths paths, Logger? logger, Action checkpoint) =>
        SweepIdle(paths, logger, checkpoint, () => HasSessionNotes(paths));

    internal static int SweepIdle(AppPaths paths, Logger? logger, Action checkpoint, Func<bool> hasLiveGame)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!Directory.Exists(Root(paths))) return 0;

        var revision = SharedRuntimeGate.ActivityVersion(paths);
        try
        {
            Checkpoint();
            using (var gate = SharedRuntimeGate.TryAcquire(paths))
                if (gate is null) return 0;
            if (hasLiveGame()) return 0;

            // The inventory and hashes never hold the game's gate. The gate
            // only protects the mutation of a single file or empty directory.
            return SweepCore(paths, logger, Checkpoint, Delete, requirePreparedPacks: true);
        }
        catch (InventoryChangedException)
        {
            return 0;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            logger?.Warn($"Shared runtime cleanup could not finish ({ex.Message}).");
            return 0;
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
            using var gate = SharedRuntimeGate.TryAcquire(paths);
            if (gate is null || revision != SharedRuntimeGate.ActivityVersion(paths))
                throw new InventoryChangedException();
            return MaintenancePath.TryDelete(paths, path, directory);
        }
    }

    private static bool HasSessionNotes(AppPaths paths)
    {
        var root = Path.Combine(paths.Personal, "ClientSessions");
        if (!Exists(root)) return false;
        // Startup reconciles these notes. An unreadable note is still a reason
        // to defer maintenance and must not be removed by this check.
        return !MaintenancePath.TryRead(paths, root, out var attributes) ||
            !attributes.HasFlag(FileAttributes.Directory) || Directory.EnumerateFiles(root, "*.json").Any();
    }

    private static int SweepCore(AppPaths paths, Logger? logger, Action checkpoint,
        Func<string, bool, bool> delete, bool requirePreparedPacks)
    {
        var root = Root(paths);
        if (!IsOrdinaryUnder(paths.Root, Anchor(paths), directory: true) ||
            !IsOrdinaryUnder(Anchor(paths), root, directory: true)) return 0;
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var preparedPacks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!TryReadLiveStates(paths, live, profiles, preparedPacks, checkpoint)) return 0;

        var removed = 0;
        // A source pack without a matching runtime state may still benefit
        // from an already downloaded assets index or Mojang Java during Play.
        if (!requirePreparedPacks || AllInstalledPacksArePrepared(paths, preparedPacks, checkpoint))
        {
            foreach (var sweepable in AlwaysSweepable)
            {
                checkpoint();
                var directory = Path.Combine(root, sweepable);
                if (!IsOrdinaryUnder(root, directory, directory: true)) continue;
                removed += SweepFiles(paths, directory, live, checkpoint, delete);
            }
        }

        removed += SweepProfiles(paths, profiles, checkpoint, delete);
        if (removed > 0) logger?.Info($"Shared runtime store: {removed} unused file(s) removed.");
        return removed;
    }

    private static bool AllInstalledPacksArePrepared(AppPaths paths, IReadOnlyDictionary<string, string> prepared,
        Action checkpoint)
    {
        if (!Exists(paths.Packs)) return true;
        if (!IsOrdinaryUnder(paths.Root, paths.Packs, directory: true)) return false;
        foreach (var pack in Directory.EnumerateDirectories(paths.Packs))
        {
            checkpoint();
            if (!IsOrdinaryUnder(paths.Packs, pack, directory: true)) return false;
            var manifest = Path.Combine(pack, PackManifestService.ManifestFileName);
            if (!Exists(manifest)) return false;
            if (!IsOrdinaryUnder(pack, manifest, directory: false)) return false;
            var descriptor = PackManifestService.Load(pack);
            if (descriptor.NativeRuntime is not null) continue;
            if (!prepared.TryGetValue(Path.GetFileName(pack), out var hash) ||
                !descriptor.DescriptorHash.Equals(hash, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static bool TryReadLiveStates(AppPaths paths, HashSet<string> live, HashSet<string> profiles,
        Dictionary<string, string> preparedPacks, Action checkpoint)
    {
        if (!Exists(paths.Runtimes)) return true;
        var anchor = Anchor(paths);
        if (!IsOrdinaryUnder(anchor, paths.Runtimes, directory: true)) return false;

        var assets = Assets(paths);
        var indexes = Path.Combine(assets, "indexes");
        var resources = Resources(paths);
        var versions = Versions(paths);
        var readIndexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var build in Directory.EnumerateDirectories(paths.Runtimes))
        {
            checkpoint();
            if (!IsOrdinaryUnder(paths.Runtimes, build, directory: true)) return false;
            var state = Path.Combine(build, RuntimeStateFileName);
            if (!Exists(state)) continue;
            if (!IsOrdinaryUnder(build, state, directory: false)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(state));
            var root = document.RootElement;
            if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
                return false;
            if (root.TryGetProperty("profileId", out var profile))
            {
                if (profile.ValueKind != JsonValueKind.String || !ValidName(profile.GetString())) return false;
                profiles.Add(profile.GetString()!);
            }
            if (profile.ValueKind == JsonValueKind.String &&
                root.TryGetProperty("schemaVersion", out var schema) &&
                schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var version) &&
                version == PackRuntimeService.RuntimeCacheGeneration &&
                root.TryGetProperty("descriptorHash", out var descriptor) &&
                descriptor.ValueKind == JsonValueKind.String && descriptor.GetString() is { Length: 64 } hash &&
                hash.All(char.IsAsciiHexDigit) && files.EnumerateObject().MoveNext())
                preparedPacks.Add(Path.GetFileName(build), hash);
            foreach (var file in files.EnumerateObject())
            {
                checkpoint();
                if (string.IsNullOrWhiteSpace(file.Name) || Path.IsPathRooted(file.Name)) return false;
                var full = Path.GetFullPath(Path.Combine(anchor, file.Name.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsBelow(anchor, full)) return false;
                live.Add(full);
                if (IsBelow(versions, full) && !string.Equals(Path.GetDirectoryName(full), versions, StringComparison.OrdinalIgnoreCase))
                    profiles.Add(Path.GetRelativePath(versions, full).Split(Path.DirectorySeparatorChar)[0]);
                if (string.Equals(Path.GetDirectoryName(full), indexes, StringComparison.OrdinalIgnoreCase) &&
                    readIndexes.Add(full) && IsOrdinaryUnder(anchor, full, directory: false))
                    foreach (var copy in DerivedAssetLayout.Read(full, assets, resources))
                    {
                        checkpoint();
                        live.Add(copy.Destination);
                    }
            }
        }
        return true;
    }

    private static int SweepProfiles(AppPaths paths, HashSet<string> live, Action checkpoint,
        Func<string, bool, bool> delete)
    {
        var versions = Versions(paths);
        if (!IsOrdinaryUnder(Anchor(paths), versions, directory: true)) return 0;
        var owned = new List<(string Directory, ProfileOwner Owner)>();
        foreach (var directory in Directory.EnumerateDirectories(versions))
        {
            checkpoint();
            if (!IsOrdinaryUnder(versions, directory, directory: true)) continue;
            if (ReadProfileOwner(directory) is { } owner) owned.Add((directory, owner));
        }
        if (owned.Count == 0) return 0;

        if (Exists(paths.Packs))
        {
            if (!IsOrdinaryUnder(paths.Root, paths.Packs, directory: true)) return 0;
            foreach (var directory in Directory.EnumerateDirectories(paths.Packs))
            {
                checkpoint();
                if (!IsOrdinaryUnder(paths.Packs, directory, directory: true)) return 0;
                var manifest = Path.Combine(directory, PackManifestService.ManifestFileName);
                if (!Exists(manifest)) continue;
                if (!IsOrdinaryUnder(directory, manifest, directory: false)) return 0;
                var descriptor = PackManifestService.Load(directory);
                if (descriptor.NativeRuntime is not null) continue;

                live.Add(descriptor.MinecraftVersion);
                var loaderVersion = PackRuntimeService.CanonicalLoaderVersion(descriptor);
                foreach (var (_, owner) in owned)
                {
                    if (owner.MinecraftVersion.Equals(descriptor.MinecraftVersion, StringComparison.OrdinalIgnoreCase) &&
                        owner.LoaderType == descriptor.Loader.Type &&
                        string.Equals(owner.LoaderVersion, loaderVersion, StringComparison.OrdinalIgnoreCase))
                        live.Add(owner.ProfileId);
                }
            }
        }

        // Older state may only name the loader. Its local profile also names
        // the inherited Minecraft profile (and sometimes a separate jar).
        var pending = new Queue<string>(live);
        while (pending.TryDequeue(out var id))
        {
            checkpoint();
            if (!ValidName(id)) return 0;
            var profile = Path.Combine(versions, id, id + ".json");
            if (!TryReadOrdinaryAttributes(versions, profile, out var attributes)) return 0;
            if (attributes is null) continue;
            if (attributes.Value.HasFlag(FileAttributes.Directory)) return 0;
            using var json = JsonDocument.Parse(File.ReadAllText(profile));
            foreach (var key in new[] { "inheritsFrom", "jar" })
            {
                if (!json.RootElement.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) continue;
                var dependency = value.GetString();
                if (!ValidName(dependency)) return 0;
                if (live.Add(dependency!)) pending.Enqueue(dependency!);
            }
        }

        var removed = 0;
        foreach (var (directory, owner) in owned)
        {
            checkpoint();
            if (live.Contains(owner.ProfileId)) continue;
            var files = new List<(string Path, long Size, DateTime Modified)>();
            var intact = true;
            foreach (var (relative, sha256) in owner.Files)
            {
                checkpoint();
                var file = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!TryReadOrdinaryAttributes(directory, file, out var attributes) ||
                    attributes is { } value && value.HasFlag(FileAttributes.Directory))
                {
                    intact = false;
                    break;
                }
                if (attributes is null) continue;
                var stamp = new FileInfo(file);
                var size = stamp.Length;
                var modified = stamp.LastWriteTimeUtc;
                if (!HashFile(file, checkpoint).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                {
                    intact = false;
                    break;
                }
                files.Add((file, size, modified));
            }
            if (!intact) continue;

            var retry = false;
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (file, size, modified) in files)
            {
                var current = new FileInfo(file);
                if (current.Exists && current.Length == size && current.LastWriteTimeUtc == modified &&
                    delete(file, false)) removed++;
                else retry = true;
                for (var parent = Path.GetDirectoryName(file); parent is not null && IsBelow(directory, parent);
                     parent = Path.GetDirectoryName(parent))
                    parents.Add(parent);
            }
            if (!retry && delete(Path.Combine(directory, ProfileOwnerFileName), false)) removed++;
            foreach (var parent in parents.OrderByDescending(path => path.Length))
                delete(parent, true);
            delete(directory, true);
        }
        return removed;
    }

    private static ProfileOwner? ReadProfileOwner(string directory)
    {
        var path = Path.Combine(directory, ProfileOwnerFileName);
        if (!IsOrdinaryUnder(directory, path, directory: false)) return null;
        try
        {
            var owner = JsonSerializer.Deserialize<ProfileOwner>(File.ReadAllText(path), OwnerJson);
            if (owner is not { SchemaVersion: 1, Files: not null } || !ValidName(owner.ProfileId) ||
                !owner.ProfileId.Equals(Path.GetFileName(directory), StringComparison.OrdinalIgnoreCase) ||
                !ValidName(owner.MinecraftVersion) || !Enum.IsDefined(owner.LoaderType) ||
                (owner.LoaderType == PackLoaderKind.Vanilla
                    ? owner.LoaderVersion is not null || !owner.ProfileId.Equals(owner.MinecraftVersion, StringComparison.OrdinalIgnoreCase)
                    : !ValidName(owner.LoaderVersion)) ||
                !owner.Files.ContainsKey(owner.ProfileId + ".json")) return null;
            foreach (var (relative, hash) in owner.Files)
            {
                if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                    relative.Split('/', Path.DirectorySeparatorChar).Any(part => !ValidName(part)) ||
                    relative.Equals(ProfileOwnerFileName, StringComparison.OrdinalIgnoreCase) ||
                    hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit)) return null;
            }
            return owner;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            return null;
        }
    }

    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) &&
        name is not ("." or "..") && name == name.Trim() && !name.EndsWith('.') &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) == -1;

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private static bool IsBelow(string root, string path) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsOrdinaryUnder(string root, string path, bool directory) =>
        TryReadOrdinaryAttributes(root, path, out var attributes) &&
        attributes is { } value && value.HasFlag(FileAttributes.Directory) == directory;

    private static bool TryReadOrdinaryAttributes(string root, string path, out FileAttributes? attributes)
    {
        attributes = null;
        if (!IsBelow(root, path)) return false;

        var segments = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            FileAttributes found;
            try { found = File.GetAttributes(current); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }

            if (found.HasFlag(FileAttributes.ReparsePoint) ||
                index < segments.Length - 1 && !found.HasFlag(FileAttributes.Directory)) return false;
            if (index == segments.Length - 1) attributes = found;
        }
        return true;
    }

    internal static string HashFile(string path, Action? checkpoint = null)
    {
        using var stream = checkpoint is null ? File.OpenRead(path) :
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (checkpoint is null) return Convert.ToHexStringLower(SHA256.HashData(stream));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                checkpoint();
                hash.AppendData(buffer, 0, count);
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static int SweepFiles(AppPaths paths, string root, HashSet<string> live, Action checkpoint,
        Func<string, bool, bool> delete)
    {
        var removed = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            checkpoint();
            if (MaintenancePath.IsProtectedAssetName(Path.GetFileName(path)) ||
                !MaintenancePath.TryRead(paths, path, out var attributes)) continue;
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                removed += SweepFiles(paths, path, live, checkpoint, delete);
                delete(path, true);
            }
            else if (!live.Contains(path) && delete(path, false)) removed++;
        }
        return removed;
    }

    private static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException or FormatException or
        OverflowException or InvalidDataException or NotSupportedException;

    private sealed record ProfileOwner(int SchemaVersion, string ProfileId, string MinecraftVersion,
        PackLoaderKind LoaderType, string? LoaderVersion, SortedDictionary<string, string> Files);
    private sealed class InventoryChangedException : Exception
    {
    }
}
