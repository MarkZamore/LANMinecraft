using System.IO;
using System.IO.Compression;

namespace Minecraft;

/// <summary>
/// A pack as memory sizing sees it: how many mods it loads, how many bytes
/// their jars are, how much texture it ships, and which Minecraft it is for.
///
/// The launcher runs whatever pack is put under Minecraft\Packs - vanilla
/// 1.7.10 as readily as something twice the size of Limitless 8 - and what the
/// game holds outside its heap is a property of that pack, not a constant.
/// Nine hundred mods keep their class data, their threads and their atlases
/// whatever the heap is set to; a bare vanilla client keeps almost none of it.
/// So the sizing rules take one of these rather than a number of gigabytes.
/// </summary>
/// <remarks>
/// <see cref="ModCount"/> counts the mods the loader will load, which is not
/// the number of files in the folder. Mods carry other mods inside themselves -
/// Fabric API alone is dozens of them - and the loader loads every one. All The
/// Fabric 3 is ninety-five jars and 287 mods; Limitless 8 is eight hundred and
/// eighty-two jars and 1128. There is no ratio between the two: one pack has
/// nearly twice as many mods as files, the other a quarter more.
///
/// Counting files instead was measured getting it wrong by 1373 MB on a pack
/// whose whole budget was 4096: the launcher promised four gigabytes, the game
/// took 5225, and the missing 1129 of those were per-mod costs for mods it had
/// not counted.
/// </remarks>
public readonly record struct PackMemoryProfile(
    int ModCount,
    long ModBytes,
    long AssetBytes,
    string? MinecraftVersion,
    int JarCount = 0)
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

            var modsRoot = Path.Combine(packDirectory, "mods");
            var mods = MeasureFiles(modsRoot, ".jar");
            var assets =
                MeasureFiles(Path.Combine(packDirectory, "resourcepacks"), extension: null).Bytes +
                MeasureFiles(Path.Combine(packDirectory, "shaderpacks"), extension: null).Bytes;
            var loaded = mods.Count + CountNestedMods(modsRoot, mods.Count, mods.Bytes);
            return new PackMemoryProfile(
                loaded, mods.Bytes, assets, ReadMinecraftVersion(packDirectory), mods.Count);
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

    /// <summary>
    /// Counts a pack from the file list its manifest publishes, for a pack that
    /// is offered but not installed.
    /// </summary>
    /// <remarks>
    /// <see cref="ModCount"/> is the jar count here, not the mod count: nothing
    /// outside a jar says how many mods it carries, and the jars are not on the
    /// disk to open. So this reads low - on the pack it was checked against,
    /// eighty-eight jars against a hundred or so mods - and it is still the
    /// difference between "3 GB" and two thirds of whatever machine is asking.
    /// The number is replaced by a real measurement the first time the pack is
    /// installed, which is the moment the estimate stops being a guess.
    /// </remarks>
    /// <param name="publishedModCount">
    /// What the publisher counted: jars plus the mods nested inside them, the
    /// same number <see cref="Measure"/> arrives at by opening every jar. Null
    /// for a manifest written before the field existed, and then the jar count
    /// stands in for it, which is what this did for every manifest.
    /// </param>
    public static PackMemoryProfile FromPublishedFiles(
        IEnumerable<(string Path, long SizeBytes)> files,
        string? minecraftVersion,
        int? publishedModCount = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        var jars = 0;
        long modBytes = 0;
        long assetBytes = 0;
        foreach (var (rawPath, size) in files)
        {
            if (string.IsNullOrWhiteSpace(rawPath) || size < 0) continue;
            var path = rawPath.Replace('\\', '/');
            if (path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                jars++;
                modBytes += size;
            }
            else if (path.StartsWith("resourcepacks/", StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith("shaderpacks/", StringComparison.OrdinalIgnoreCase))
            {
                assetBytes += size;
            }
        }

        // A manifest with no mods in it is a manifest this does not understand,
        // and guessing from one is worse than saying nothing.
        if (jars == 0) return Unknown;

        // Nested mods are the whole of the disagreement this used to have with
        // Measure: the bytes, the assets and the version already matched to the
        // byte, and only the count moved - by 259 mods on Limitless 8, which is
        // three gigabytes of suggested heap. A count the publisher took cannot
        // be arrived at from a file list, because the ratio of nested to jars
        // runs from 1.18 to 2.79 across the packs here and follows neither the
        // loader nor the bytes.
        var loaded = publishedModCount is { } counted && counted >= jars ? counted : jars;
        return new PackMemoryProfile(loaded, modBytes, assetBytes, minecraftVersion, jars);
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

    /// <summary>
    /// The mods that live inside other mods. Read out of each jar's own
    /// listing, because nothing outside the jar says how many it carries.
    /// </summary>
    /// <remarks>
    /// This opens every jar in the pack, which for the largest one on record -
    /// eight hundred and eighty-two of them - takes about a second. So the
    /// answer is kept for as long as the launcher is open, under a key that
    /// changes whenever the folder does: a pack switched to and back is free,
    /// and a pack whose mods were edited is counted again.
    ///
    /// Only the top level of each jar is read. A jar inside a jar inside a jar
    /// exists, and counting it would mean opening every nested one as well -
    /// several hundred more archives for a handful of mods.
    /// </remarks>
    private static int CountNestedMods(string modsRoot, int jarCount, long jarBytes)
    {
        if (!Directory.Exists(modsRoot)) return 0;

        var key = $"{modsRoot}|{jarCount}|{jarBytes}";
        if (NestedCounts.TryGetValue(key, out var cached)) return cached;

        var nested = 0;
        try
        {
            foreach (var jar in Directory.EnumerateFiles(modsRoot, "*.jar", SearchOption.AllDirectories))
            {
                try
                {
                    using var archive = ZipFile.OpenRead(jar);
                    foreach (var entry in archive.Entries)
                    {
                        var name = entry.FullName;
                        if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                            (name.StartsWith("META-INF/jars/", StringComparison.OrdinalIgnoreCase) ||
                             name.StartsWith("META-INF/jarjar/", StringComparison.OrdinalIgnoreCase)))
                        {
                            nested++;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    // A jar that will not open carries nothing anyone can count.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        NestedCounts[key] = nested;
        return nested;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> NestedCounts =
        new(StringComparer.OrdinalIgnoreCase);

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
