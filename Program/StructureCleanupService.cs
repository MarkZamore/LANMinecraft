using System.IO;
using System.Text.Json;

namespace Minecraft;

/// <summary>
/// Walks the launcher's own folders and takes out what no build has any use
/// for: runtimes for builds that are gone, and the copies of the game a build
/// kept for itself before the game was shared.
/// </summary>
/// <remarks>
/// It works from a list of what is known to be needed rather than a list of
/// what is known to be junk, and the difference is not academic. Three files in
/// one day looked exactly like junk and were not: Mojang's mappings, the window
/// icon's assets, and the asset index whose loss took the language list with
/// it. Every one of them was needed by something that never said so. So this
/// sweep only ever looks inside folders it can name, only ever removes things
/// it can explain, and leaves anything it does not recognise alone.
///
/// Two rules follow from that.
///
/// Worlds are never touched, by anything, ever. A build removed on purpose
/// leaves its worlds behind unless the player says otherwise, so a world whose
/// build is gone is not an orphan - it is the thing they kept.
///
/// And a folder that cannot be read is not an empty one. Every guard here
/// refuses to act on a reading it did not get, because the cost of being wrong
/// in that direction is somebody's install and the cost of being wrong the
/// other way is a folder that stays one more day.
/// </remarks>
public static class StructureCleanupService
{
    private const string RuntimeStateFileName = ".portable-runtime.json";

    /// <summary>
    /// What a build kept for itself before the game moved into the shared
    /// store. Named here rather than discovered, because the folder they sit in
    /// also holds the build's own natives and its state file.
    /// </summary>
    private static readonly string[] SharedNowRoots =
        ["assets", "libraries", "versions", "runtime", "resources"];

    /// <summary>Sweeps, and answers how many things went.</summary>
    public static int Run(AppPaths paths, Logger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var removed = RemoveOrphanedRuntimes(paths, logger) + RemoveSupersededRuntimeCopies(paths, logger);
        if (removed > 0)
        {
            logger?.Info($"Structure swept: {removed} folder(s) no build had a use for.");
        }
        return removed;
    }

    /// <summary>
    /// Runtimes for builds that are neither installed nor offered.
    /// </summary>
    /// <remarks>
    /// This one is worth more than the megabytes it frees. A runtime folder is
    /// small now - a state file and some natives - but that state file names
    /// every file in the shared store the build was using, and the store's
    /// sweep keeps alive whatever any state names. So a runtime nobody can
    /// reach pins a whole version of Minecraft in the store for ever, and
    /// removing it is what lets that go.
    ///
    /// Offered counts as needed even with no pack folder: a build in the list
    /// is one press of Play from existing again, and its prepared runtime is
    /// the reason that press is quick.
    /// </remarks>
    private static int RemoveOrphanedRuntimes(AppPaths paths, Logger? logger)
    {
        if (!Directory.Exists(paths.Runtimes)) return 0;
        var installed = ReadDirectoryNames(paths.Packs);
        // Unreadable, or a Packs folder that is somehow empty while runtimes
        // exist: that is a reading we did not get, not a machine with no builds.
        if (installed is null || installed.Count == 0) return 0;

        foreach (var known in PortablePackSyncService.KnownPacks)
        {
            installed.Add(known.RelativePath);
        }

        var removed = 0;
        foreach (var runtime in ReadDirectories(paths.Runtimes))
        {
            var name = Path.GetFileName(runtime);
            if (installed.Contains(name)) continue;
            if (TryDeleteTree(runtime, logger))
            {
                logger?.Info($"Runtime for a build that is gone was removed: {name}.");
                removed++;
            }
        }
        return removed;
    }

    /// <summary>
    /// The game a build downloaded for itself, in the days when every build
    /// downloaded its own.
    /// </summary>
    /// <remarks>
    /// Only for a build whose runtime state is of an older generation than the
    /// launcher now writes, which is exactly the build that is going to prepare
    /// itself again from the shared store the next time it is played. Its own
    /// copies cannot be read by anything after that, and until it is played
    /// they are a gigabyte apiece sitting still.
    ///
    /// A state that is missing or will not parse is left alone. That build will
    /// prepare again too, and its own cleanup will take these folders the
    /// moment it does - there is no need to guess on its behalf here.
    /// </remarks>
    private static int RemoveSupersededRuntimeCopies(AppPaths paths, Logger? logger)
    {
        if (!Directory.Exists(paths.Runtimes)) return 0;

        var removed = 0;
        foreach (var runtime in ReadDirectories(paths.Runtimes))
        {
            if (ReadStateGeneration(Path.Combine(runtime, RuntimeStateFileName)) is not { } generation) continue;
            if (generation >= PackRuntimeService.RuntimeCacheGeneration) continue;

            // Counted for this build rather than for the whole sweep: saying
            // "the copy is gone" on the strength of another build's copy is a
            // line that is simply not true.
            var before = removed;
            foreach (var root in SharedNowRoots)
            {
                var directory = Path.Combine(runtime, root);
                if (!Directory.Exists(directory)) continue;
                if (TryDeleteTree(directory, logger)) removed++;
            }

            if (removed > before)
            {
                logger?.Info(
                    $"{Path.GetFileName(runtime)} kept its own copy of the game from before it was shared; " +
                    "it prepares from the shared one now and the copy is gone.");
            }
        }
        return removed;
    }

    /// <summary>The schema a runtime state was written with, or null when it cannot be read.</summary>
    private static int? ReadStateGeneration(string statePath)
    {
        try
        {
            if (!File.Exists(statePath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            return document.RootElement.TryGetProperty("schemaVersion", out var value) &&
                   value.TryGetInt32(out var generation)
                ? generation
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The names of the directories in one folder, or null when it cannot be read.</summary>
    private static HashSet<string>? ReadDirectoryNames(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return null;
            return Directory.EnumerateDirectories(root)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string[] ReadDirectories(string root)
    {
        try
        {
            return Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Removes a tree, unlinking rather than following any junction in it.
    /// </summary>
    /// <remarks>
    /// Nothing swept here is supposed to contain one. That is exactly why the
    /// walk is written out: the day something does, the difference is a folder
    /// removed against somebody's worlds removed.
    /// </remarks>
    private static bool TryDeleteTree(string directory, Logger? logger)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            DeleteTreeCore(directory);
            return !Directory.Exists(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warn($"Could not remove {directory}: {ex.Message}");
            return false;
        }
    }

    private static void DeleteTreeCore(string directory)
    {
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) Directory.Delete(child);
            else DeleteTreeCore(child);
        }

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var info = new FileInfo(file);
            if (info.IsReadOnly) info.IsReadOnly = false;
            info.Delete();
        }

        Directory.Delete(directory);
    }
}
