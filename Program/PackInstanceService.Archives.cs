using System.IO;
using System.Text.Json;

namespace Minecraft;

public sealed partial class PackInstanceService
{
    private static readonly string[] ArchiveDirectories = ["shaderpacks", "resourcepacks", "texturepacks"];
    internal static readonly object ArchiveGate = new();

    internal static void EnsureArchiveFolders(string pack)
    {
        foreach (var name in new[] { "mods", "shaderpacks", "resourcepacks" })
        {
            var path = ArchivePath(pack, name);
            if (!File.Exists(path)) Directory.CreateDirectory(path);
        }
    }

    internal static bool IsPackArchive(string path)
    {
        var separator = path.IndexOf('/');
        return separator > 0 && path.IndexOf('/', separator + 1) < 0 &&
            ArchiveDirectories.Contains(path[..separator], StringComparer.OrdinalIgnoreCase) &&
            path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    internal static void MigrateArchives(AppPaths paths, Logger logger, string packRelativePath, Action checkpoint)
    {
        lock (ArchiveGate)
        {
            checkpoint();
            var pack = paths.CombineUnderPacks(packRelativePath);
            var game = paths.CombineUnderInstances(packRelativePath);
            if (PortablePackSyncService.IsUpdatePending(pack) ||
                PortablePackSyncService.ReadManagedFiles(pack) is not { } managedFiles) return;
            EnsureArchiveFolders(pack);
            if (!MaintenancePath.TryRead(paths, game, out var attributes) ||
                !attributes.HasFlag(FileAttributes.Directory)) return;
            var statePath = Path.Combine(game, StateFileName);
            EnsureOrdinaryPath(game, statePath);
            var state = ReadState(statePath, packRelativePath, logger);
            if (SynchronizeArchives(pack, game, state, managedFiles, checkpoint))
            {
                checkpoint();
                AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(state, StateJsonOptions));
            }
        }
    }

    private static bool SynchronizeArchives(string pack, string game, InstanceState state,
        IReadOnlyList<PortablePackSyncService.ManagedFileSnapshot> managedFiles, Action checkpoint,
        Func<string>? directoryConflictRoot = null)
    {
        var managed = managedFiles.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var retired = PortablePackSyncService.ReadRetiredFiles(pack)
            .Where(file => IsPackArchive(file.Path))
            .ToLookup(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<string, SourceFileState>(StringComparer.OrdinalIgnoreCase);
        var paths = ArchivePaths(pack).Concat(ArchivePaths(game)).Concat(managed.Keys.Where(IsPackArchive))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in paths)
        {
            checkpoint();
            var previous = state.Archives.GetValueOrDefault(path) ?? state.Files.GetValueOrDefault(path);
            if ((directoryConflictRoot is null || !managed.ContainsKey(path)) &&
                (IsLinkedArchive(pack, path) || IsLinkedArchive(game, path)))
            {
                if (previous is not null) current[path] = previous;
                continue;
            }
            var source = ArchivePath(pack, path);
            var target = ArchivePath(game, path);
            var installed = File.Exists(source) ? ReadSourceState(source) : null;
            if (managed.TryGetValue(path, out var official) &&
                (installed is null || !Matches(installed, official.SizeBytes, official.Sha256)))
            {
                if (directoryConflictRoot is null)
                {
                    // Play repairs this archive from the published asset. The
                    // cleaner has no verified replacement to put in its place.
                    if (previous is not null) current[path] = previous;
                    continue;
                }
                throw new IOException($"The installed archive differs from the verified manifest: {path}");
            }
            if (installed is not null && Directory.Exists(target))
            {
                if (directoryConflictRoot is null)
                {
                    if (previous is not null) current[path] = previous;
                    continue;
                }
                var preservedDirectory = Path.Combine(directoryConflictRoot(),
                    path.Replace('/', Path.DirectorySeparatorChar) + ".user-directory");
                checkpoint();
                Directory.CreateDirectory(Path.GetDirectoryName(preservedDirectory)!);
                Directory.Move(target, preservedDirectory);
            }
            // A folder in Packs can still be a resource pack. Leave an
            // independent personal ZIP in the instance if that name is taken.
            if (installed is null && Directory.Exists(source)) continue;
            var linked = installed is not null && PortableRuntimeCache.AreSameOrdinaryFile(source, target);
            var player = linked ? installed : File.Exists(target) ? ReadSourceState(target) : null;

            if (installed is null)
            {
                if (player is null) continue;
                if (SameContent(player, previous) || retired[path].Any(file => Matches(player, file.SizeBytes, file.Sha256)))
                {
                    checkpoint();
                    File.Delete(target);
                    continue;
                }
                if (retired[path].Any(file => previous is not null && Matches(previous, file.SizeBytes, file.Sha256)))
                {
                    KeepPersonalArchive(path, target, player);
                    checkpoint();
                    File.Delete(target);
                    continue;
                }
                // Publish the canonical file first. A retry after this point sees
                // two intact files and can finish converting the instance to a link.
                PlaceArchive(target, source, player, replace: false, link: true, checkpoint);
                installed = player;
                linked = PortableRuntimeCache.AreSameOrdinaryFile(source, target);
            }
            else if (player is not null && !SameContent(player, installed) && !SameContent(player, previous))
            {
                KeepPersonalArchive(path, target, player);
            }

            // Keep a byte-identical independent copy if the volume cannot link.
            var matchesTarget = SameContent(player, installed);
            if (!matchesTarget || !linked)
                PlaceArchive(source, target, installed, replace: player is not null, link: true, checkpoint,
                    keepIdenticalCopy: matchesTarget);
            current[path] = installed;
        }

        var changed = current.Count != state.Archives.Count || current.Any(item =>
            !state.Archives.TryGetValue(item.Key, out var old) || !SameContent(item.Value, old));
        state.Archives = current;
        return changed;

        void KeepPersonalArchive(string path, string target, SourceFileState player)
        {
            var preserved = PreserveArchive(pack, path, target, player,
                candidate => managed.ContainsKey(candidate) || retired.Contains(candidate) ||
                    state.Files.ContainsKey(candidate), checkpoint);
            var playable = ArchivePath(game, preserved);
            if (!File.Exists(playable) && !Directory.Exists(playable))
                PlaceArchive(ArchivePath(pack, preserved), playable, player, replace: false, link: true, checkpoint);
            current[preserved] = player;
        }
    }

    // A future published file may use the name of an archive a player added to
    // Packs. Keep the player's bytes under a distinct playable ZIP name first.
    internal static void PreserveArchiveBeforePackUpdate(string pack, string path, string? expectedOldHash,
        string? expectedNewHash, Func<string, bool> isPublished, Action checkpoint)
    {
        if (!IsPackArchive(path)) return;
        var source = ArchivePath(pack, path);
        if (!File.Exists(source)) return;
        var bytes = ReadSourceState(source);
        if (HashesEqual(bytes.Sha256, expectedOldHash ?? "") ||
            HashesEqual(bytes.Sha256, expectedNewHash ?? "")) return;
        PreserveArchive(pack, path, source, bytes, isPublished, checkpoint);
    }

    private static string PreserveArchive(string pack, string path, string source, SourceFileState bytes,
        Func<string, bool> isPublished, Action checkpoint)
    {
        var parent = path[..path.IndexOf('/')];
        var stem = Path.GetFileNameWithoutExtension(path);
        stem = stem[..Math.Min(stem.Length, 100)];
        for (var number = 0; ; number++)
        {
            checkpoint();
            var suffix = number == 0 ? "" : "-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var relative = $"{parent}/{stem}.personal-{bytes.Sha256[..12]}{suffix}.zip";
            if (isPublished(relative)) continue;
            var target = ArchivePath(pack, relative);
            if (Directory.Exists(target)) continue;
            if (File.Exists(target))
            {
                if (SameContent(ReadSourceState(target), bytes)) return relative;
                continue;
            }
            PlaceArchive(source, target, bytes, replace: false, link: false, checkpoint);
            return relative;
        }
    }

    private static IEnumerable<string> ArchivePaths(string root)
    {
        foreach (var folder in ArchiveDirectories)
        {
            var directory = Path.Combine(root, folder);
            if (IsReparsePoint(directory) || !Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var relative = folder + "/" + Path.GetFileName(file);
                if (IsPackArchive(relative)) yield return relative;
            }
        }
    }

    private static bool IsLinkedArchive(string root, string relative)
    {
        var folder = Path.Combine(root, relative[..relative.IndexOf('/')]);
        return IsReparsePoint(folder) || IsReparsePoint(Path.Combine(folder, Path.GetFileName(relative)));
    }

    private static bool IsReparsePoint(string path) =>
        TryGetAttributes(path, out var attributes) && attributes.HasFlag(FileAttributes.ReparsePoint);

    private static string ArchivePath(string root, string relative)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        EnsureOrdinaryPath(root, path);
        return path;
    }

    private static bool SameContent(SourceFileState? left, SourceFileState? right) =>
        left is not null && right is not null && Matches(left, right.SizeBytes, right.Sha256);

    private static bool Matches(SourceFileState file, long size, string sha256) =>
        file.SizeBytes == size && HashesEqual(file.Sha256, sha256);

    private static void PlaceArchive(string source, string target, SourceFileState expected, bool replace, bool link,
        Action checkpoint, bool keepIdenticalCopy = false)
    {
        var folder = Path.GetDirectoryName(target)!;
        var temporary = Path.Combine(folder, $".archive-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(folder);
        try
        {
            checkpoint();
            if (!link || !TryCreateHardLink(temporary, source))
            {
                if (keepIdenticalCopy) return;
                File.Copy(source, temporary, overwrite: false);
                if (!SameContent(ReadSourceState(temporary), expected))
                    throw new IOException($"Archive changed while being copied: {source}");
            }
            checkpoint();
            try
            {
                File.Move(temporary, target, overwrite: replace);
            }
            catch (Exception ex) when (keepIdenticalCopy &&
                ex is IOException or UnauthorizedAccessException &&
                File.Exists(target) && SameContent(ReadSourceState(target), expected))
            {
                // The already verified ZIP is usable. Replacing its storage
                // with a hard link can wait until Windows releases the file.
            }
        }
        finally
        {
            if (File.Exists(temporary) && (!OperatingSystem.IsWindows() ||
                !PortableRuntimeCache.TryDeleteOrdinaryEntry(temporary, directory: false)))
                File.Delete(temporary);
        }
    }
}
