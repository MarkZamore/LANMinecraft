using System.IO;

namespace Minecraft;

/// <summary>
/// Takes a downloaded build off the machine.
///
/// A build is not one folder. It is the pack under <c>Packs</c>, the instance
/// the game actually runs in under <c>Personal\Instances</c>, its runtime under
/// <c>Launcher\Runtimes</c>, whatever the last sync left in
/// <c>Personal\PackConflicts</c> and <c>Personal\Temp</c>, a line in the hash
/// cache, a line in the settings - and, if nobody else needs them, the Java it
/// pinned and its share of the game itself. Deleting the pack folder alone
/// leaves the rest, and the rest is most of the disk.
///
/// The game is the part that is not the build's to delete. Minecraft, its
/// libraries and its assets are downloaded once and shared, so what a removal
/// frees there is whatever no remaining build still names - the whole of a
/// version when this was the last build on it, and nothing at all when it was
/// not.
///
/// The worlds are the exception and are asked about separately, because they
/// are the only part a player cannot get back by pressing Play again.
/// </summary>
/// <remarks>
/// Two rules run through all of it.
///
/// The first is that the instance's <c>saves</c> folder is made of directory
/// junctions, one per world, pointing into <c>Worlds</c>. A recursive delete
/// that walks into a junction deletes the world at the far end of it - a world
/// this class may have been told explicitly to keep. So the tree is walked by
/// hand and a directory that is a reparse point is unlinked, never entered.
///
/// The second is that nothing here throws for one folder it could not remove.
/// A build half gone and a message saying which part stayed is a state a player
/// can act on; an exception in the middle of the job leaves them with neither
/// the build nor the disk space, and no idea which.
/// </remarks>
public sealed class BuildRemovalService(AppPaths paths, PackHashService? hashes = null, Logger? logger = null)
{
    /// <summary>What a removal is about to do, worked out before anything goes.</summary>
    /// <param name="BuildRelativePath">The build's folder name, as the launcher spells it.</param>
    /// <param name="Directories">Every folder that will be removed, pack first.</param>
    /// <param name="WorldDirectories">Everything holding this build's worlds that will go, or empty when the worlds stay.</param>
    /// <param name="Worlds">How many worlds those hold - the number worth showing a player.</param>
    /// <param name="JavaDirectories">Folders of a Java no remaining build asks for.</param>
    /// <param name="Java">The feature releases those folders are, for saying so.</param>
    public readonly record struct RemovalPlan(
        string BuildRelativePath,
        IReadOnlyList<string> Directories,
        IReadOnlyList<string> WorldDirectories,
        int Worlds,
        IReadOnlyList<string> JavaDirectories,
        IReadOnlyList<int> Java);

    /// <summary>What actually went, and what would not.</summary>
    public readonly record struct RemovalOutcome(
        int Directories,
        int Worlds,
        IReadOnlyList<int> Java,
        IReadOnlyList<string> Kept)
    {
        public bool Complete => Kept.Count == 0;
    }

    private readonly AppPaths _paths = paths;
    private readonly PackHashService? _hashes = hashes;
    private readonly Logger? _logger = logger;

    /// <summary>
    /// Works out what removing this build would take, without removing any of
    /// it. The window shows the world count from here, so the question it asks
    /// is about worlds that exist rather than worlds in general.
    /// </summary>
    public RemovalPlan Plan(string buildRelativePath, bool worldsToo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildRelativePath);
        var build = buildRelativePath.Trim().Trim('\\', '/');

        var directories = new List<string>();
        foreach (var candidate in BelongingTo(build))
        {
            if (candidate is not null && Directory.Exists(candidate)) directories.Add(candidate);
        }

        // Worlds live in a folder per build, but they only move there when that
        // build is launched - WorldLocations.Migrate runs from Prepare and
        // nowhere else. A build updated and then deleted without being played
        // still has its worlds lying flat in the root of Worlds, and looking
        // only at the folder found none of them: the window offered "and its
        // worlds", the player agreed, and the worlds stayed. A build withdrawn
        // from the list can never be launched again and so could never migrate
        // at all.
        var worldDirectories = new List<string>();
        var packFolder = WorldLocations.ForBuild(_paths.Worlds, build);
        var worlds = 0;
        if (!string.Equals(packFolder, _paths.Worlds, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(packFolder))
        {
            worldDirectories.Add(packFolder);
            worlds += CountWorlds(packFolder);
        }
        foreach (var stray in StrandedWorlds(build))
        {
            worldDirectories.Add(stray);
            worlds++;
        }
        // Java has nothing to do with the worlds: it goes if the build that
        // pinned it was the last one asking for it, either way round.
        var java = JavaNothingElseNeeds(build);
        return new RemovalPlan(
            build,
            directories,
            worldsToo ? worldDirectories : [],
            worlds,
            java.Select(directory => directory.Path).ToArray(),
            java.Select(directory => directory.MajorVersion).Distinct().Order().ToArray());
    }

    /// <summary>
    /// Worlds of this build still lying flat in the root of Worlds, which is
    /// where every world lived before they were filed by build.
    /// </summary>
    /// <remarks>
    /// The match is exact and deliberately not
    /// <see cref="WorldMetadataService.BelongsToBuild"/>. That one answers yes
    /// for a world that names no build at all, which is right when deciding what
    /// to show - an unattributed world is shown to everybody rather than lost -
    /// and would be a disaster here: it would delete every unattributed world on
    /// the machine along with the first build a player removes. A world goes
    /// only if it says, in as many words, that it is this build's.
    /// </remarks>
    private IEnumerable<string> StrandedWorlds(string build)
    {
        if (!Directory.Exists(_paths.Worlds)) yield break;

        var metadata = new WorldMetadataService();
        foreach (var candidate in Directory.EnumerateDirectories(_paths.Worlds))
        {
            if (!WorldLocations.IsWorld(candidate)) continue;
            var recorded = metadata.Read(candidate)?.BuildRelativePath?.Trim().Trim('\\', '/');
            if (string.IsNullOrEmpty(recorded)) continue;
            if (string.Equals(recorded, build, StringComparison.OrdinalIgnoreCase)) yield return candidate;
        }
    }

    /// <summary>Carries out a plan. Never throws for a folder that would not go.</summary>
    public RemovalOutcome Remove(RemovalPlan plan)
    {
        SharedRuntimeGate.MarkActivity(_paths);
        using var sharedRuntime = SharedRuntimeGate.TryAcquire(_paths) ??
            throw new InvalidOperationException("Another launcher is preparing the shared runtime.");
        var kept = new List<string>();
        var removed = 0;
        foreach (var directory in plan.Directories)
        {
            if (RemoveTree(directory)) removed++;
            else kept.Add(directory);
        }

        var worlds = 0;
        foreach (var worldDirectory in plan.WorldDirectories)
        {
            var held = WorldLocations.IsWorld(worldDirectory) ? 1 : CountWorlds(worldDirectory);
            if (RemoveTree(worldDirectory)) worlds += held;
            else kept.Add(worldDirectory);
        }

        IReadOnlyList<int> java = [];
        if (plan.JavaDirectories.Count > 0)
        {
            try
            {
                using var gate = JavaRuntimeStorage.TryAcquire(_paths);
                if (gate is not null)
                {
                    // The concrete references may have changed since the window displayed the plan.
                    var planned = plan.JavaDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var removedJava = new SortedSet<int>();
                    foreach (var runtime in JavaNothingElseNeeds(null).Where(directory => planned.Contains(directory.Path)))
                    {
                        if (RemoveTree(runtime.Path)) removedJava.Add(runtime.MajorVersion);
                        else kept.Add(runtime.Path);
                    }
                    java = removedJava.ToArray();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger?.Warn($"Unused Java runtimes could not be removed: {ex.Message}");
            }
        }

        // The hash cache is keyed by pack folder, so an entry for a pack that is
        // gone can never be hit again - and a pack downloaded again under the
        // same name must not be compared against what the old one hashed to.
        try
        {
            _hashes?.ForgetPack(plan.BuildRelativePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger?.Warn($"Pack hashes for {plan.BuildRelativePath} were not forgotten: {ex.Message}");
        }

        // Empty parents left behind read as a build that is still there.
        TryRemoveIfEmpty(Path.Combine(_paths.Personal, "Temp", "RuntimeDownloads"));
        TryRemoveIfEmpty(Path.Combine(_paths.Personal, "Temp", "Java"));
        TryRemoveIfEmpty(Path.Combine(_paths.Personal, "Temp"));

        var outcome = new RemovalOutcome(removed, worlds, java, kept);
        _logger?.Info(
            $"Build removed: {plan.BuildRelativePath} - {removed} folder(s)" +
            (worlds > 0 ? $", {worlds} world(s)" : ", worlds kept") +
            (java.Count > 0 ? $", Java {string.Join(", ", java)}" : "") +
            (kept.Count > 0 ? $"; could not remove {string.Join(", ", kept)}" : "") + ".");
        return outcome;
    }

    /// <summary>
    /// Takes the build out of the settings: the number the player chose for it,
    /// and the selection itself when it is the build that just went.
    /// </summary>
    /// <returns>True when the settings changed and are worth saving.</returns>
    public static bool Forget(AppSettings settings, string buildRelativePath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var changed = settings.MemoryByPack.Remove(buildRelativePath);
        if (string.Equals(settings.ClientRelativePath, buildRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            settings.ClientRelativePath = "";
            // The world was one of that build's, and the build is gone.
            settings.SelectedWorldRelativePath = "";
            changed = true;
        }

        return changed;
    }

    /// <summary>Everything under the launcher's own folders that is named for this build.</summary>
    private IEnumerable<string?> BelongingTo(string build)
    {
        yield return Under(_paths.Packs, build);
        yield return Under(_paths.Instances, build);
        yield return Under(_paths.Runtimes, build);
        yield return Under(_paths.PackConflicts, build);
        yield return Under(_paths.WaypointConflicts, build);
        // The two temporary folders a launch leaves. RuntimeDownloads flattens
        // the name the way PackRuntimeService writes it; the Java one does not.
        yield return Under(Path.Combine(_paths.Personal, "Temp", "RuntimeDownloads"), SafeName(build));
        yield return Under(Path.Combine(_paths.Personal, "Temp", "Java"), build);
    }

    /// <summary>The owned Java installations and archives that no remaining pack needs.</summary>
    private IReadOnlyList<JavaRuntimeStorage.OwnedDirectory> JavaNothingElseNeeds(string? build)
    {
        try
        {
            return JavaRuntimeStorage.ReadUnusedDirectories(_paths, build);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger?.Warn($"Unused Java runtimes could not be inspected: {ex.Message}");
            return [];
        }
    }

    private string? Under(string parent, string name)
    {
        if (name.Length == 0) return null;
        try
        {
            var full = Path.GetFullPath(Path.Combine(parent, name));
            // The build's name arrives from a folder listing, but a delete is
            // not the place to take that on trust: it must land directly inside
            // the folder it was looked up in, and inside the portable root.
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            _paths.EnsureUnderRoot(full);
            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(ch => invalid.Contains(ch) || ch is '\\' or '/' ? '_' : ch));
    }

    private static int CountWorlds(string buildWorlds)
    {
        try
        {
            return Directory.EnumerateDirectories(buildWorlds).Count(WorldLocations.IsWorld);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string[] EnumerateDirectoriesSafe(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return [];
            return Directory.EnumerateDirectories(root).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Removes a folder and everything in it, and unlinks - never follows - a
    /// junction found on the way down.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the walk is written out by hand. The instance's
    /// <c>saves</c> holds one junction per world, pointing into <c>Worlds</c>,
    /// and a recursive delete that steps through one of those takes the world
    /// with it. <see cref="Directory.Delete(string, bool)"/> is believed not to
    /// follow reparse points, but "believed not to" is not a thing to stake
    /// somebody's world on when the alternative is thirty lines.
    /// </remarks>
    private bool RemoveTree(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return true;
            RemoveTreeCore(directory);
            return !Directory.Exists(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warn($"Could not remove {directory}: {ex.Message}");
            return false;
        }
    }

    private static void RemoveTreeCore(string directory)
    {
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (IsLink(child)) Directory.Delete(child);
            else RemoveTreeCore(child);
        }

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            // Read-only is what a jar unpacked from an archive often is, and it
            // is not a reason to leave half a build behind.
            var info = new FileInfo(file);
            if (info.IsReadOnly) info.IsReadOnly = false;
            info.Delete();
        }

        Directory.Delete(directory);
    }

    private static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void TryRemoveIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
