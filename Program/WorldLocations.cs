using System.IO;

namespace Minecraft;

/// <summary>
/// Where worlds live: <c>Worlds/&lt;build&gt;/&lt;world&gt;</c>.
/// </summary>
/// <remarks>
/// They used to lie flat in Worlds, one folder each, and a build was shown its
/// own by a junction per world dropped into the instance's saves. That worked
/// on 1.18.2 and on 1.21.1 and failed on 1.20.1, whose own check of a world
/// folder refuses a Windows junction outright - "is not a directory" - so a
/// world the launcher had linked could be listed and never opened. A real
/// symbolic link would pass that check and cannot be made without privileges
/// this launcher does not ask for.
///
/// So the link moved up a level. Each build's saves is one junction to its own
/// folder under Worlds, and every world the game opens is a plain directory
/// again - on every version, with no check to pass. Worlds still all live in
/// Worlds, which is the one thing about this layout that must stay true: it is
/// what is backed up, what is transferred, and what a player looks in.
///
/// It also retires the empty placeholder folders. Those existed so a build
/// could not offer a name already taken by a world of another build, back when
/// every build shared one namespace. Builds have their own now.
/// </remarks>
public static class WorldLocations
{
    /// <summary>The folder holding one build's worlds.</summary>
    public static string ForBuild(string worldsRoot, string buildRelativePath)
    {
        ArgumentNullException.ThrowIfNull(worldsRoot);
        var name = (buildRelativePath ?? string.Empty).Trim().Trim('\\', '/');
        if (name.Length == 0) name = "Общие";
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return Path.Combine(worldsRoot, name);
    }

    /// <summary>
    /// Every world under Worlds, whatever build it belongs to.
    ///
    /// Both layouts are read, because one launch cannot be assumed to have
    /// tidied everything: a folder that is itself a world is one left over from
    /// before, and a folder that is not is a build holding worlds. A world is a
    /// folder with a level.dat in it, which is the same test the rest of the
    /// launcher uses and the same one the game uses.
    /// </summary>
    public static IEnumerable<string> Enumerate(string worldsRoot)
    {
        if (string.IsNullOrEmpty(worldsRoot) || !Directory.Exists(worldsRoot)) yield break;
        foreach (var entry in Directory.EnumerateDirectories(worldsRoot))
        {
            if (IsWorld(entry))
            {
                yield return entry;
                continue;
            }

            foreach (var world in Directory.EnumerateDirectories(entry))
            {
                if (IsWorld(world)) yield return world;
            }
        }
    }

    /// <summary>Whether this folder is a world, by the file only a world has.</summary>
    public static bool IsWorld(string path) => File.Exists(Path.Combine(path, "level.dat"));

    /// <summary>
    /// Moves worlds still lying flat in Worlds into the folder of the build
    /// they belong to, and returns how many moved.
    /// </summary>
    /// <remarks>
    /// Only worlds the launcher can attribute are moved. One it cannot is left
    /// exactly where it is and named in the log: it stays listed, it stays
    /// transferable, and nothing about it is guessed at. Guessing would put a
    /// world into a build whose mods would strip out every block the right
    /// build gave it.
    ///
    /// Nothing is ever deleted here, and a name already taken in the
    /// destination stops that one world rather than the whole migration.
    /// </remarks>
    public static int Migrate(string worldsRoot, WorldMetadataService metadata, Logger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (string.IsNullOrEmpty(worldsRoot) || !Directory.Exists(worldsRoot)) return 0;

        var moved = 0;
        var orphans = new List<string>();
        foreach (var world in Directory.EnumerateDirectories(worldsRoot).ToList())
        {
            if (!IsWorld(world)) continue;
            var build = metadata.Read(world)?.BuildRelativePath;
            if (string.IsNullOrWhiteSpace(build))
            {
                orphans.Add(Path.GetFileName(world));
                continue;
            }

            var destination = Path.Combine(ForBuild(worldsRoot, build), Path.GetFileName(world));
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                logger?.Warn(
                    $"World {Path.GetFileName(world)} was left where it is: {destination} already exists.");
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.Move(world, destination);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.Warn($"World {Path.GetFileName(world)} could not be moved into {build}: {ex.Message}");
            }
        }

        if (moved > 0)
        {
            logger?.Info($"Worlds moved into their build's folder: {moved}.");
        }
        if (orphans.Count > 0)
        {
            logger?.Warn(
                "These worlds say which build they belong to nowhere, so they were left in the Worlds folder " +
                $"itself and no build will list them: {string.Join(", ", orphans)}.");
        }
        return moved;
    }
}
