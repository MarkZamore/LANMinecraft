using System.IO;

namespace Minecraft;

/// <summary>
/// A pack as memory sizing sees it: how many mod jars it carries, how many
/// bytes those jars are, how much texture it ships, and which Minecraft it is
/// for.
///
/// The launcher runs whatever pack is put under Minecraft\Packs - vanilla
/// 1.7.10 as readily as something twice the size of Limitless 8 - and what the
/// game holds outside its heap is a property of that pack, not a constant.
/// Nine hundred mods keep their class data, their threads and their atlases
/// whatever the heap is set to; a bare vanilla client keeps almost none of it.
/// So the sizing rules take one of these rather than a number of gigabytes.
/// </summary>
public readonly record struct PackMemoryProfile(
    int ModCount,
    long ModBytes,
    long AssetBytes,
    string? MinecraftVersion)
{
    /// <summary>
    /// A pack nobody has looked at: not installed yet, or unreadable. The
    /// sizing rules keep to their older, pack-blind arithmetic for it.
    /// </summary>
    public static PackMemoryProfile Unknown { get; } = new(-1, 0, 0, null);

    /// <summary>False for <see cref="Unknown"/> alone.</summary>
    public bool IsKnown => ModCount >= 0;

    /// <summary>
    /// Chunk sections doubled in height in 1.18, the atlases grew with them and
    /// the buffers behind both are native memory. A version the launcher cannot
    /// read counts as modern: the modern estimate is the larger one, and it is
    /// the too-small estimate that breaks the promise the number makes.
    /// </summary>
    public bool IsModernMinecraft =>
        !TryReadVersion(MinecraftVersion, out var major, out var minor) || major != 1 || minor >= 13;

    /// <summary>
    /// Counts a pack folder: the jars under <c>mods</c>, and the texture that
    /// <c>resourcepacks</c> and <c>shaderpacks</c> hand the graphics driver.
    /// Everything else - configs, scripts, saves - never reaches memory in a
    /// size worth counting.
    /// </summary>
    public static PackMemoryProfile Measure(string packDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(packDirectory) || !Directory.Exists(packDirectory))
            {
                return Unknown;
            }

            var mods = MeasureFiles(Path.Combine(packDirectory, "mods"), ".jar");
            var assets =
                MeasureFiles(Path.Combine(packDirectory, "resourcepacks"), extension: null).Bytes +
                MeasureFiles(Path.Combine(packDirectory, "shaderpacks"), extension: null).Bytes;
            return new PackMemoryProfile(mods.Count, mods.Bytes, assets, ReadMinecraftVersion(packDirectory));
        }
        catch (IOException)
        {
            return Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return Unknown;
        }
    }

    private static string? ReadMinecraftVersion(string packDirectory)
    {
        try
        {
            return PackManifestService.HasManifest(packDirectory)
                ? PackManifestService.Load(packDirectory).MinecraftVersion
                : null;
        }
        catch
        {
            // A pack whose manifest does not parse cannot be launched at all;
            // that is said elsewhere, and here it only means the version is
            // unknown.
            return null;
        }
    }

    private static (int Count, long Bytes) MeasureFiles(string directory, string? extension)
    {
        if (!Directory.Exists(directory)) return (0, 0);

        // Packs that ship one mods folder per loader version keep the jars a
        // level down, so this walks the tree rather than the top of it.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System
        };
        var count = 0;
        long bytes = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", options))
        {
            // A jar renamed to .jar.disabled is not loaded and does not count.
            if (extension is not null &&
                !string.Equals(file.Extension, extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            count++;
            bytes += file.Length;
        }

        return (count, bytes);
    }

    private static bool TryReadVersion(string? version, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(version)) return false;

        var parts = version.Split('.');
        return parts.Length >= 2 &&
            int.TryParse(parts[0], out major) &&
            int.TryParse(parts[1], out minor);
    }
}
