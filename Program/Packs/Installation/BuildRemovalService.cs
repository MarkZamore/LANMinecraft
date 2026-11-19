using System.IO;

namespace Minecraft;

/// <summary>
/// Takes a downloaded build off the machine.
///
/// A build is not one folder. It is the pack under <c>Packs</c>, the instance
/// the game actually runs in under <c>Personal\Instances</c>, its runtime under
/// <c>Launcher\PackRuntimes</c>, whatever the last sync left in
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

    public sealed record RemovalBatchPlan(IReadOnlyList<RemovalPlan> Plans, bool WithWorlds)
    {
        public int Builds => Plans.Count;
        public int Worlds => Plans.SelectMany(plan => plan.WorldDirectories)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

        public bool SameTargets(RemovalBatchPlan other) =>
            WithWorlds == other.WithWorlds && Plans.Count == other.Plans.Count &&
            Plans.Zip(other.Plans).All(pair => BuildRemovalService.SameTargets(pair.First, pair.Second));
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
        if (!PackCatalogIndex.ValidFolder(buildRelativePath))
            return new RemovalPlan(buildRelativePath, [], [], 0, [], []);
        var build = buildRelativePath;
        var directories = BelongingTo(build).OfType<string>().Where(Directory.Exists).ToArray();
        var worlds = new WorldRemovalService(_paths, _logger).PlanForBuild(build);
        var java = JavaNothingElseNeeds(build);
        return new RemovalPlan(
            build,
            directories,
            worldsToo ? worlds : [],
            worlds.Count,
            java.Select(directory => directory.Path).ToArray(),
            java.Select(directory => directory.MajorVersion).Distinct().Order().ToArray());
    }

    public RemovalBatchPlan PlanBatch(IEnumerable<string> paths, bool worldsToo)
    {
        EnsureOrdinaryPacksRoot();
        var plans = paths.Where(PackCatalogIndex.ValidFolder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Where(path => Directory.Exists(_paths.CombineUnderPacks(path)))
            .Select(path => Plan(path, worldsToo)).ToArray();
        return new RemovalBatchPlan(plans, worldsToo);
    }

    public IReadOnlyDictionary<string, RemovalOutcome> RemoveBatch(RemovalBatchPlan approved)
    {
        var requestedPacks = approved.Plans.Select(plan => plan.BuildRelativePath).ToArray();
        using var packAccess = PackAccess.RequireAvailable(_paths, requestedPacks);
        RequireGamesStopped(requestedPacks);
        using var sharedRuntime = AcquireRemovalLease(_paths);
        var current = PlanBatch(requestedPacks, approved.WithWorlds);
        if (!approved.SameTargets(current)) throw new IOException("The builds or worlds to remove have changed.");
        var worlds = new WorldRemovalService(_paths, _logger);
        var pendingWorlds = current.Plans.SelectMany(plan => plan.WorldDirectories)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        worlds.EnsureClosed(pendingWorlds);
        var outcomes = new Dictionary<string, RemovalOutcome>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in current.Plans)
        {
            // Canonical paths may let two historical names claim the same world.
            // The approved world is attempted only once. Java is recalculated so
            // a JVM shared by two builds can be freed when the last one leaves.
            var java = JavaNothingElseNeeds(plan.BuildRelativePath);
            var target = plan with
            {
                WorldDirectories = plan.WorldDirectories.Where(pendingWorlds.Remove).ToArray(),
                JavaDirectories = java.Select(directory => directory.Path).ToArray()
            };
            outcomes.Add(plan.BuildRelativePath, RemoveUnderLease(target, worlds));
        }
        return outcomes;
    }

    public RemovalOutcome Remove(RemovalPlan approved)
    {
        using var packAccess = PackAccess.RequireAvailable(_paths, [approved.BuildRelativePath]);
        RequireGamesStopped([approved.BuildRelativePath]);
        using var sharedRuntime = AcquireRemovalLease(_paths);
        EnsureOrdinaryPacksRoot();
        var worlds = new WorldRemovalService(_paths, _logger);
        var current = Plan(approved.BuildRelativePath, worldsToo: approved.WorldDirectories.Count != 0);
        if (!SameTargets(approved, current))
            throw new IOException("The build or worlds to remove have changed.");
        worlds.EnsureClosed(approved.WorldDirectories);
        return RemoveUnderLease(approved, worlds);
    }

    private void RequireGamesStopped(IEnumerable<string> packs)
    {
        if (PackAccess.IsRunning(_paths, packs, _logger))
            throw new IOException("Закройте запущенную игру перед удалением сборки.");
    }

    private static bool SameTargets(RemovalPlan a, RemovalPlan b) =>
        a.BuildRelativePath.Equals(b.BuildRelativePath, StringComparison.OrdinalIgnoreCase) &&
        a.Directories.SequenceEqual(b.Directories, StringComparer.OrdinalIgnoreCase) &&
        a.WorldDirectories.SequenceEqual(b.WorldDirectories, StringComparer.OrdinalIgnoreCase);

    private void EnsureOrdinaryPacksRoot()
    {
        if (Directory.Exists(_paths.Packs) && !MaintenancePath.TryRead(_paths, _paths.Packs, out _))
            throw new IOException("The packs directory is redirected or inaccessible.");
    }

    internal static FileStream AcquireRemovalLease(AppPaths paths)
    {
        SharedRuntimeGate.MarkActivity(paths);
        return SharedRuntimeGate.TryAcquire(paths) ??
            throw new InvalidOperationException("Another launcher is preparing the shared runtime.");
    }

    private RemovalOutcome RemoveUnderLease(RemovalPlan plan, WorldRemovalService worlds)
    {
        var kept = new List<string>();
        var removed = 0;
        foreach (var directory in plan.Directories)
        {
            if (RemoveOwnedTree(_paths, directory, _logger)) removed++;
            else kept.Add(directory);
        }

        var worldOutcome = worlds.RemoveUnderLease(plan.WorldDirectories);
        kept.AddRange(worldOutcome.Kept);

        IReadOnlyList<int> java = [];
        if (plan.JavaDirectories.Count > 0)
        {
            try
            {
                using var gate = JavaRuntimeStorage.TryAcquire(_paths);
                if (gate is not null)
                {
                    var planned = plan.JavaDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var removedJava = new SortedSet<int>();
                    foreach (var runtime in JavaNothingElseNeeds(null).Where(directory => planned.Contains(directory.Path)))
                    {
                        if (RemoveOwnedTree(_paths, runtime.Path, _logger)) removedJava.Add(runtime.MajorVersion);
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

        try
        {
            _hashes?.ForgetPack(plan.BuildRelativePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger?.Warn($"Pack hashes for {plan.BuildRelativePath} were not forgotten: {ex.Message}");
        }

        TryRemoveIfEmpty(_paths, Path.Combine(_paths.Personal, "Temp", "RuntimeDownloads"));
        TryRemoveIfEmpty(_paths, Path.Combine(_paths.Personal, "Temp", "Java"));
        TryRemoveIfEmpty(_paths, Path.Combine(_paths.Personal, "Temp"));

        var outcome = new RemovalOutcome(removed, worldOutcome.Worlds, java, kept);
        _logger?.Info(
            $"Build removed: {plan.BuildRelativePath} - {removed} folder(s)" +
            (outcome.Worlds > 0 ? $", {outcome.Worlds} world(s)" : ", worlds kept") +
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
        foreach (var runtimeRoot in _paths.PackRuntimeRoots)
            yield return Under(runtimeRoot, build);
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

    /// <summary>Removes a known ordinary tree without following any junctions inside it.</summary>
    internal static bool RemoveOwnedTree(AppPaths paths, string directory, Logger? logger)
    {
        try
        {
            if (string.Equals(Path.GetFullPath(directory), paths.Service, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!MaintenancePath.TryRead(paths, directory, out var attributes) ||
                !attributes.HasFlag(FileAttributes.Directory)) return false;
            RemoveTreeCore(paths, directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warn($"Could not remove {directory}: {ex.Message}");
            return false;
        }
    }

    private static void RemoveTreeCore(AppPaths paths, string directory)
    {
        if (!MaintenancePath.TryRead(paths, directory, out var attributes) ||
            !attributes.HasFlag(FileAttributes.Directory))
            throw new IOException("A directory being removed is redirected or inaccessible.");

        foreach (var child in Directory.EnumerateFileSystemEntries(directory))
        {
            var childAttributes = File.GetAttributes(child);
            if (childAttributes.HasFlag(FileAttributes.Directory))
            {
                if (childAttributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(child);
                else RemoveTreeCore(paths, child);
            }
            else
            {
                if (!childAttributes.HasFlag(FileAttributes.ReparsePoint) &&
                    childAttributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(child, childAttributes & ~FileAttributes.ReadOnly);
                File.Delete(child);
            }
        }

        Directory.Delete(directory);
    }

    internal static void TryRemoveIfEmpty(AppPaths paths, string directory)
    {
        try
        {
            if (MaintenancePath.TryRead(paths, directory, out var attributes) &&
                attributes.HasFlag(FileAttributes.Directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
