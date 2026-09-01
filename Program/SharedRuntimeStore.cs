using System.IO;
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
/// What that costs is a store nobody owns. A build's runtime state lists every
/// file that build needs, so the live set is the union of those lists and the
/// garbage is everything else - and the garbage can only be collected by
/// looking at every build at once, which is what <see cref="Sweep"/> does.
/// </remarks>
public static class SharedRuntimeStore
{
    private const string RuntimeStateFileName = ".portable-runtime.json";

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

    /// <summary>
    /// The only parts of the store the sweep may take from.
    /// </summary>
    /// <remarks>
    /// A measured restriction rather than a cautious one. Everything under
    /// <c>assets</c> and <c>runtime</c> is fetched by name and reported as a
    /// file of the version, so a state lists every one of them and "unnamed"
    /// really does mean unused - four files out of five thousand on a live
    /// install.
    ///
    /// <c>libraries</c> and <c>versions</c> are not like that. A loader
    /// installer runs once and leaves behind files nobody downloaded: the
    /// NeoForge client and universal jars, the slim, srg and extra client jars,
    /// and Mojang's mappings. CmlLib reports none of them, so no state names
    /// them, so the sweep would have taken every one - sixty-three files and
    /// 132 MB on this machine, which is the whole classpath plus the file the
    /// identity hooks are built from. Three builds would have stopped starting
    /// and every player would have lost their skin, again.
    ///
    /// So those two roots are left alone until a build can record what it made
    /// and not only what it fetched. Assets are the bulk of the store, so this
    /// keeps nearly all of what the sweep is for.
    /// </remarks>
    private static readonly string[] AlwaysSweepable = ["assets", "runtime"];

    /// <summary>
    /// The two roots that are only swept once every build can speak for what is
    /// in them.
    /// </summary>
    /// <remarks>
    /// These hold the files a loader installer makes rather than downloads -
    /// the NeoForge client and universal jars, the srg and extra client jars -
    /// and until release 331 nothing recorded them. The sweep took the whole
    /// classpath, the game started with neoforge and minecraft both [MISSING],
    /// and it could not recover, because a file no state names is also a file
    /// no validation misses.
    ///
    /// A state written at the current generation does name them, so the two
    /// roots are safe to sweep - but only when EVERY state on the disk is that
    /// new. One build still carrying an older state would contribute a
    /// keep-set with no libraries in it, and its own jars would go: not a crash
    /// this time, because a state of the wrong generation fails validation and
    /// the build prepares again before it can launch, but a re-download bought
    /// for nothing.
    ///
    /// So it turns itself on. Every build gets prepared once after an update,
    /// and from the first moment they all have, the store is swept whole again
    /// with no flag to set and nothing to remember.
    /// </remarks>
    private static readonly string[] SweepableOnceEveryStateIsCurrent = ["libraries", "versions"];

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

    /// <summary>
    /// Removes from the store every file no build's runtime state names, and
    /// then the directories that leaves empty.
    /// </summary>
    /// <remarks>
    /// Run after a build is removed, which is the moment files stop being
    /// wanted, and again at startup for whatever an interrupted removal left.
    ///
    /// One state that cannot be read stops the whole sweep. That is not
    /// caution for its own sake: the live set is built out of those files, and
    /// a build whose needs are unknown counted as needing nothing is how a
    /// sweep deletes the game out from under a perfectly good install. A
    /// damaged state costs one build a rebuild, never all of them.
    ///
    /// No builds at all is the opposite case and is not a refusal: nothing is
    /// used by anything, so the store goes, and the next install refills it.
    /// </remarks>
    /// <returns>How many files were removed.</returns>
    public static int Sweep(AppPaths paths, Logger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var root = Root(paths);
        if (!Directory.Exists(root)) return 0;

        var anchor = Anchor(paths);
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var everyStateIsCurrent = true;
        foreach (var statePath in EnumerateStates(paths))
        {
            // Unreadable is not empty. A state that will not parse is a build
            // whose needs are unknown, and guessing them as "none" is how a
            // sweep deletes the game out from under a perfectly good install.
            if (!TryReadLiveFiles(statePath, anchor, live)) return 0;
            if (ReadGeneration(statePath) != PackRuntimeService.RuntimeCacheGeneration)
            {
                everyStateIsCurrent = false;
            }
        }

        // No builds at all is a different thing entirely, and it is the case
        // this was asked for: with the last build gone nothing is used by
        // anything, so the whole store goes. The next install refills it.
        var removed = 0;
        var roots = everyStateIsCurrent
            ? AlwaysSweepable.Concat(SweepableOnceEveryStateIsCurrent)
            : AlwaysSweepable;
        foreach (var sweepable in roots)
        {
            var directory = Path.Combine(root, sweepable);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in EnumerateFilesSafe(directory))
            {
                if (live.Contains(Path.GetFullPath(file))) continue;
                if (TryDelete(file, logger)) removed++;
            }

            RemoveEmptyDirectories(directory, logger);
        }
        if (removed > 0)
        {
            logger?.Info(
                $"Shared runtime store: {removed} file(s) no build needs any more were removed" +
                (everyStateIsCurrent
                    ? "."
                    : "; the libraries were left alone until every build has been prepared once."));
        }
        return removed;
    }

    /// <summary>The schema a state was written with, or -1 when it will not say.</summary>
    private static int ReadGeneration(string statePath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            return document.RootElement.TryGetProperty("schemaVersion", out var value) &&
                   value.TryGetInt32(out var generation)
                ? generation
                : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return -1;
        }
    }

    private static IEnumerable<string> EnumerateStates(AppPaths paths)
    {
        if (!Directory.Exists(paths.Runtimes)) yield break;
        string[] builds;
        try
        {
            builds = Directory.GetDirectories(paths.Runtimes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var build in builds)
        {
            var state = Path.Combine(build, RuntimeStateFileName);
            if (File.Exists(state)) yield return state;
        }
    }

    /// <summary>
    /// Adds one build's files to the live set. False when the state cannot be
    /// read at all, which stops the sweep rather than under-counting it.
    /// </summary>
    private static bool TryReadLiveFiles(string statePath, string anchor, HashSet<string> live)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            if (!document.RootElement.TryGetProperty("files", out var files) ||
                files.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var file in files.EnumerateObject())
            {
                var relative = file.Name;
                if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) continue;
                var full = Path.GetFullPath(
                    Path.Combine(anchor, relative.Replace('/', Path.DirectorySeparatorChar)));
                live.Add(full);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        // Materialised on purpose: the walk and the deleting cannot be
        // interleaved over one lazy enumeration of the same tree.
        try
        {
            return Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryDelete(string path, Logger? logger)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.IsReadOnly) info.IsReadOnly = false;
            info.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warn($"Shared runtime file could not be removed ({ex.Message}): {path}");
            return false;
        }
    }

    private static void RemoveEmptyDirectories(string root, Logger? logger)
    {
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        // Deepest first, so a directory emptied by the level below it is itself
        // seen as empty on the way up.
        foreach (var directory in directories.OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.Warn($"Empty shared runtime directory stayed ({ex.Message}): {directory}");
            }
        }
    }
}
