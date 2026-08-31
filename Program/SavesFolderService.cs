using System.Diagnostics;
using System.IO;

namespace Minecraft;

/// <summary>
/// Decides which worlds the game is allowed to see.
///
/// Every build shares one Worlds folder, and the game shows whatever is in its
/// own <c>saves</c>. That folder used to be a single link to Worlds, so every
/// build's world list was every world there is - and opening a world under the
/// wrong build is how the blocks of every mod that build does not have are
/// lost.
///
/// So <c>saves</c> is a real folder now, holding one directory junction per
/// world this build may open. A junction is not a copy: the game reads and
/// writes straight through it into the world's one place on disk, so nothing is
/// duplicated and nothing has to be moved back.
///
/// A world the launcher cannot attribute is shown to every build on purpose,
/// the same as in the launcher's own list: hiding a world nobody stamped would
/// be losing it rather than protecting it, and playing it is what gives it a
/// build.
///
/// The one thing a per-world junction costs is that a world the player creates
/// lands in the instance's saves instead of the shared folder, because the game
/// makes a real directory for it. <see cref="Adopt"/> moves those across - at
/// the end of a session, so the world is in place before it is stamped, and
/// again before the next launch in case the launcher was closed first.
/// </summary>
/// <param name="logger">Where the summary goes.</param>
public sealed class SavesFolderService(Logger? logger = null)
{
    internal const string SavesFolderName = "saves";

    /// <summary>How many junctions <see cref="Prepare"/> made and removed.</summary>
    public readonly record struct SavesChanges(int Shown, int Hidden, int Adopted);

    /// <summary>
    /// Points the instance's saves folder at this build's worlds.
    /// </summary>
    /// <remarks>
    /// One junction, on the folder rather than on each world inside it, so
    /// that every world the game opens is a plain directory. That is the whole
    /// reason for the layout: 1.20.1 checks a world folder in a way a Windows
    /// junction cannot pass, and refuses to open one it had happily listed.
    ///
    /// Worlds a previous layout left in the instance are moved out first, and
    /// worlds still lying flat in the Worlds folder are moved into the build
    /// they belong to. Both are done every time and cost nothing once there is
    /// nothing left to move.
    /// </remarks>
    /// <param name="worldsRoot">The portable Worlds folder, where worlds live.</param>
    /// <param name="instanceDirectory">The instance the game will run in.</param>
    /// <param name="buildRelativePath">The build being launched.</param>
    public SavesChanges Prepare(string worldsRoot, string instanceDirectory, string buildRelativePath)
    {
        ArgumentNullException.ThrowIfNull(worldsRoot);
        ArgumentNullException.ThrowIfNull(instanceDirectory);
        Directory.CreateDirectory(worldsRoot);
        WorldLocations.Migrate(worldsRoot, new WorldMetadataService(), logger);

        var buildRoot = WorldLocations.ForBuild(worldsRoot, buildRelativePath);
        Directory.CreateDirectory(buildRoot);
        // The link's own parent, which used to be made as a side effect of
        // making saves a real folder.
        Directory.CreateDirectory(instanceDirectory);
        var saves = Path.Combine(instanceDirectory, SavesFolderName);

        var adopted = 0;
        if (Directory.Exists(saves) && !IsLink(saves))
        {
            // What the per-world layout left behind: worlds the game made in
            // here, junctions to worlds elsewhere, and the empty folders that
            // used to hold a name against another build. Only the first are
            // worth anything.
            adopted = Adopt(buildRoot, instanceDirectory);
            foreach (var entry in Directory.EnumerateDirectories(saves).ToList())
            {
                if (IsLink(entry)) Directory.Delete(entry);
                else if (IsEmptyDirectory(entry)) TryDeleteEmptyDirectory(entry);
            }
            if (IsEmptyDirectory(saves)) TryDeleteEmptyDirectory(saves);
        }

        if (IsLink(saves))
        {
            if (PointsAt(saves, buildRoot)) return new SavesChanges(0, 0, adopted);
            Directory.Delete(saves);
        }

        if (Directory.Exists(saves) || File.Exists(saves))
        {
            // Something real is standing where the link belongs, and whatever
            // it is, it is not this launcher's to remove.
            logger?.Warn($"Worlds are not linked into this build: {saves} already exists and is not a link.");
            return new SavesChanges(0, 0, adopted);
        }

        CreateJunction(saves, buildRoot);
        if (adopted > 0)
        {
            logger?.Info($"Worlds moved out of the instance and into the shared folder: {adopted}.");
        }
        return new SavesChanges(1, 0, adopted);
    }

    /// <summary>
    /// Moves worlds the game created inside the instance into the shared Worlds
    /// folder, and leaves a junction where each one was. Returns how many moved.
    ///
    /// A name already taken in the shared folder is left alone: two different
    /// worlds under one name is not something to resolve by guessing.
    /// </summary>
    public int Adopt(string destinationRoot, string instanceDirectory)
    {
        ArgumentNullException.ThrowIfNull(destinationRoot);
        ArgumentNullException.ThrowIfNull(instanceDirectory);
        var saves = Path.Combine(instanceDirectory, SavesFolderName);
        if (!Directory.Exists(saves) || IsLink(saves)) return 0;

        var moved = 0;
        foreach (var entry in Directory.EnumerateDirectories(saves).ToList())
        {
            if (IsLink(entry)) continue;
            if (!File.Exists(Path.Combine(entry, "level.dat"))) continue;
            // Not while the game has it open. Windows will rename a folder out
            // from under a held session.lock without complaint - measured - but
            // not one whose region files are open, and a world being played has
            // both. Waiting for the lock to go is waiting for the player to
            // leave the world, which is the moment this can be done at all.
            if (IsWorldOpen(entry)) continue;
            var name = Path.GetFileName(entry);
            var destination = Path.Combine(destinationRoot, name);
            // A directory is not a world. The line above already says what one
            // is - a folder with a level.dat in it - and this used to forget it
            // one line later, so an empty folder left behind by a crash or a
            // transfer stood in the way of every world of that name, from every
            // pack, for ever. "New World" is the name Minecraft offers by
            // default, so that folder blocked the likeliest name there is.
            if (IsEmptyDirectory(destination))
            {
                TryDeleteEmptyDirectory(destination);
            }

            // A world of that name really is there. Two packs both offered
            // "New World" and the player took it both times, which is not a
            // corner case - it is the name Minecraft suggests. The world moves
            // over under a free folder name rather than staying behind, and
            // nobody has to rename anything: what the player sees in the game
            // is the name inside level.dat, which is untouched. The folder is
            // only how the launcher tells two of them apart.
            var storedName = FreeName(destinationRoot, name);
            if (storedName.Length == 0)
            {
                logger?.Warn(
                    $"World {name} was made inside this build and every name near it is taken " +
                    $"beside the others, so it stays where it is: {entry}.");
                continue;
            }
            destination = Path.Combine(destinationRoot, storedName);
            try
            {
                Directory.CreateDirectory(destinationRoot);
                Directory.Move(entry, destination);
                moved++;
                logger?.Info(storedName == name
                    ? $"World {name} was made here and now lives beside the others."
                    : $"World {name} was made here and now lives beside the others as {storedName}, " +
                      "because that name was already taken. In the game it is still called what it was.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.Warn($"World {name} could not be moved beside the others: {ex.Message}");
            }
        }
        return moved;
    }

    /// <summary>
    /// The wanted folder name, or the first free one beside it, in the shape
    /// Minecraft itself uses when a world name is taken: "New World (1)".
    /// Empty where even that runs out.
    /// </summary>
    private static string FreeName(string worldsRoot, string name)
    {
        if (!Directory.Exists(Path.Combine(worldsRoot, name))) return name;
        for (var counter = 1; counter <= 999; counter++)
        {
            var candidate = $"{name} ({counter})";
            if (!Directory.Exists(Path.Combine(worldsRoot, candidate))) return candidate;
        }
        return "";
    }

    /// <summary>
    /// Whether the game currently has this world open.
    /// </summary>
    /// <remarks>
    /// Minecraft keeps a session.lock in every world and holds it open for as
    /// long as the world is open - not as long as the game runs. It is not
    /// deleted on the way out, only let go, so the question is whether anybody
    /// holds it rather than whether it is there. Asking for it with no sharing
    /// answers that in one call and costs nothing: it either opens or it does
    /// not.
    ///
    /// A world with no session.lock at all has never been opened, and is not
    /// open now.
    /// </remarks>
    internal static bool IsWorldOpen(string worldPath)
    {
        var lockPath = Path.Combine(worldPath, "session.lock");
        if (!File.Exists(lockPath)) return false;
        try
        {
            using var held = File.Open(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Unreadable for some other reason. Treated as open, because the
            // cost of being wrong the other way is moving a world mid-write.
            return true;
        }
    }

    /// <summary>
    /// A directory that exists and holds no file anywhere beneath it. Not a
    /// world, not anybody's data, and safe to take the name back from.
    /// </summary>
    internal static bool IsEmptyDirectory(string path)
    {
        try
        {
            return Directory.Exists(path) &&
                   !IsLink(path) &&
                   !Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not empty.
            return false;
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool PointsAt(string link, string world)
    {
        var target = new DirectoryInfo(link).LinkTarget;
        if (target is null) return false;
        return string.Equals(
            Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(world).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A directory junction, which Windows makes without administrator rights
    /// where a symbolic link would need them.
    /// </summary>
    internal static void CreateJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start mklink.");
        process.WaitForExit();
        if (process.ExitCode == 0) return;
        throw new InvalidOperationException(
            ($"Could not link {linkPath}: " +
             process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd()).Trim());
    }
}
