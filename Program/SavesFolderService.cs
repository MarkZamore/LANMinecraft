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
    /// Lays out the instance's saves folder for one build: a junction for every
    /// world that build may open, and nothing for the rest.
    /// </summary>
    /// <param name="worldsRoot">The portable Worlds folder, where worlds live.</param>
    /// <param name="instanceDirectory">The instance the game will run in.</param>
    /// <param name="buildRelativePath">The build being launched.</param>
    public SavesChanges Prepare(string worldsRoot, string instanceDirectory, string buildRelativePath)
    {
        ArgumentNullException.ThrowIfNull(worldsRoot);
        ArgumentNullException.ThrowIfNull(instanceDirectory);
        Directory.CreateDirectory(worldsRoot);
        var saves = Path.Combine(instanceDirectory, SavesFolderName);

        // The folder-wide junction of older releases is exactly what this
        // replaces; taking the link away leaves every world where it was.
        if (IsLink(saves))
        {
            Directory.Delete(saves);
        }
        Directory.CreateDirectory(saves);

        var adopted = Adopt(worldsRoot, instanceDirectory);
        var metadata = new WorldMetadataService();
        var shown = 0;
        var hidden = 0;

        foreach (var world in Directory.EnumerateDirectories(worldsRoot))
        {
            var name = Path.GetFileName(world);
            var link = Path.Combine(saves, name);
            var mayOpen = WorldMetadataService.BelongsToBuild(
                metadata.Read(world)?.BuildRelativePath, buildRelativePath);

            if (mayOpen)
            {
                if (IsLink(link))
                {
                    if (PointsAt(link, world)) continue;
                    Directory.Delete(link);
                }
                else if (IsEmptyDirectory(link))
                {
                    // Empty is not "something real": nothing is lost by taking
                    // the name back, and leaving it hides the world it stands
                    // in front of.
                    TryDeleteEmptyDirectory(link);
                }
                else if (Directory.Exists(link) || File.Exists(link))
                {
                    // Something real is standing where the junction belongs.
                    // Whatever it is, it is not this launcher's to remove.
                    logger?.Warn($"World {name} is not linked into this build: {link} already exists.");
                    continue;
                }
                CreateJunction(link, world);
                shown++;
            }
            else if (IsLink(link))
            {
                Directory.Delete(link);
                hidden++;
            }
        }

        // A junction whose world has gone - transferred away, or renamed - is a
        // world the game would list and fail to open.
        foreach (var entry in Directory.EnumerateDirectories(saves))
        {
            if (!IsLink(entry)) continue;
            var target = new DirectoryInfo(entry).LinkTarget;
            if (target is not null && Directory.Exists(target)) continue;
            Directory.Delete(entry);
            hidden++;
        }

        if (shown > 0 || hidden > 0 || adopted > 0)
        {
            logger?.Info(
                $"Worlds this build may open: {shown} linked, {hidden} withdrawn" +
                (adopted > 0 ? $", {adopted} moved into the shared folder" : "") + ".");
        }
        return new SavesChanges(shown, hidden, adopted);
    }

    /// <summary>
    /// Moves worlds the game created inside the instance into the shared Worlds
    /// folder, and leaves a junction where each one was. Returns how many moved.
    ///
    /// A name already taken in the shared folder is left alone: two different
    /// worlds under one name is not something to resolve by guessing.
    /// </summary>
    public int Adopt(string worldsRoot, string instanceDirectory)
    {
        ArgumentNullException.ThrowIfNull(worldsRoot);
        ArgumentNullException.ThrowIfNull(instanceDirectory);
        var saves = Path.Combine(instanceDirectory, SavesFolderName);
        if (!Directory.Exists(saves) || IsLink(saves)) return 0;

        var moved = 0;
        foreach (var entry in Directory.EnumerateDirectories(saves).ToList())
        {
            if (IsLink(entry)) continue;
            if (!File.Exists(Path.Combine(entry, "level.dat"))) continue;
            var name = Path.GetFileName(entry);
            var destination = Path.Combine(worldsRoot, name);
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
            var storedName = FreeName(worldsRoot, name);
            if (storedName.Length == 0)
            {
                logger?.Warn(
                    $"World {name} was made inside this build and every name near it is taken " +
                    $"beside the others, so it stays where it is: {entry}.");
                continue;
            }
            destination = Path.Combine(worldsRoot, storedName);
            try
            {
                Directory.Move(entry, destination);
                // Under the name it was stored as, so the pass below does not
                // then link the same world in a second time under that name and
                // list it twice.
                CreateJunction(Path.Combine(saves, storedName), destination);
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
