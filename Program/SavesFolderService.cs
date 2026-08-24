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
            if (Directory.Exists(destination))
            {
                logger?.Warn(
                    $"World {name} was made inside this build and a world of that name already " +
                    "exists beside the others; it stays where it is.");
                continue;
            }
            try
            {
                Directory.Move(entry, destination);
                CreateJunction(entry, destination);
                moved++;
                logger?.Info($"World {name} was made here and now lives beside the others.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.Warn($"World {name} could not be moved beside the others: {ex.Message}");
            }
        }
        return moved;
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
