using System.IO;

namespace Minecraft;

/// <summary>Profile preparation must never follow a world link into another save.</summary>
internal static class WorldProfilePathGuard
{
    public static void EnsureUnlinked(string worldPath, string? allowedRoot = null)
    {
        var world = Path.GetFullPath(worldPath).TrimEnd(Path.DirectorySeparatorChar);
        var root = allowedRoot is null ? world : Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!world.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !world.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The world is outside the portable profile root.");
        for (var current = world; ; current = Path.GetDirectoryName(current)!)
        {
            RejectLink(current);
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
        foreach (var name in new[] { "level.dat", "level.dat_old", "session.lock", NbtFile.LauncherBackupFileName,
                     WorldMetadataService.MetadataFileName, WorldPlayerManifestService.ManifestFileName })
            RejectLink(Path.Combine(world, name));
        foreach (var name in new[] { "players", "stats", "playerdata", "advancements" })
            RejectDirectoryAndEntries(Path.Combine(world, name));
        foreach (var name in new[] { "data", "stats", "advancements" })
            RejectDirectoryAndEntries(Path.Combine(world, "players", name));
        RejectLink(Path.Combine(world, "data"));
        RejectLink(Path.Combine(world, "data", "minecraft"));
        RejectLink(WorldPlayerStorage.WorldGenSettingsPath(world));
    }

    private static void RejectDirectoryAndEntries(string directory)
    {
        RejectLink(directory);
        if (Directory.Exists(directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory)) RejectLink(entry);
        }
    }

    private static void RejectLink(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked world metadata or player profiles cannot be prepared.");
    }
}
