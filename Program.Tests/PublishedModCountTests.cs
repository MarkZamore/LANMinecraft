using System.IO.Compression;

namespace Minecraft.Tests;

/// <summary>
/// The estimate beside the RAM field has to say the same thing before a pack is
/// downloaded and after.
/// </summary>
/// <remarks>
/// It did not. The profile taken from a folder counts the jars plus the mods
/// nested inside them, which it learns by opening every jar; the profile taken
/// from a manifest could only count the jars. On the packs published from this
/// machine that gap is 1.18 to 2.79 times, and the label moved by up to three
/// gigabytes when a pack landed - upward on a large machine, downward on a
/// small one, where the room left beside the heap is what binds.
///
/// So the publisher counts them and the manifest carries the number. These are
/// the two halves of that agreement, and this is the only thing that would
/// notice if the two counters ever stopped matching.
/// </remarks>
public class PublishedModCountTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-modcount-{Guid.NewGuid():N}");

    public void Dispose() => TempTree.Delete(_root);

    /// <summary>
    /// A pack weighed from its manifest and the same pack weighed from disk are
    /// the same weight, once the manifest carries the count.
    /// </summary>
    [Fact]
    public void APackWeighedFromItsManifest_WeighsWhatTheFolderWeighs()
    {
        var pack = BuildPack();
        var measured = PackMemoryProfile.Measure(pack);
        var published = PackMemoryProfile.FromPublishedFiles(
            FileList(pack), "1.21.1", publishedModCount: measured.ModCount);

        Assert.Equal(measured.ModCount, published.ModCount);
        Assert.Equal(measured.JarCount, published.JarCount);
        Assert.Equal(measured.ModBytes, published.ModBytes);
        Assert.Equal(measured.AssetBytes, published.AssetBytes);

        foreach (var ram in new ulong[] { 8, 16, 24, 31, 64 })
        {
            var bytes = ram * 1024UL * 1024 * 1024;
            Assert.Equal(
                MemorySizingService.GetRecommendedMemoryGb(measured, bytes),
                MemorySizingService.GetRecommendedMemoryGb(published, bytes));
        }
    }

    /// <summary>
    /// And without the count they are not, which is what makes the test above
    /// worth having: a manifest that carries no number still weighs the jars,
    /// exactly as it always did, and that is the disagreement being fixed.
    /// </summary>
    [Fact]
    public void WithoutTheCount_TheOldDisagreementIsStillThere()
    {
        var pack = BuildPack();
        var measured = PackMemoryProfile.Measure(pack);
        var published = PackMemoryProfile.FromPublishedFiles(FileList(pack), "1.21.1");

        Assert.Equal(published.JarCount, published.ModCount);
        Assert.True(
            measured.ModCount > published.ModCount,
            "the folder sees nested mods the file list cannot; that is the whole gap");
    }

    /// <summary>
    /// A published count below the jar count is a manifest disagreeing with
    /// itself, and the jars are the thing that can be checked.
    /// </summary>
    [Fact]
    public void ACountSmallerThanTheJars_IsNotBelieved()
    {
        var pack = BuildPack();
        var published = PackMemoryProfile.FromPublishedFiles(FileList(pack), "1.21.1", 1);
        Assert.Equal(published.JarCount, published.ModCount);
    }

    /// <summary>Two jars, three mods nested between them, and one resource pack.</summary>
    private string BuildPack()
    {
        var mods = Path.Combine(_root, "mods");
        Directory.CreateDirectory(mods);
        WriteJar(Path.Combine(mods, "carrier.jar"),
            ["META-INF/jars/one.jar", "META-INF/jarjar/two.jar", "META-INF/jars/three.jar",
             "assets/thing.png", "META-INF/jars/notajar.txt"]);
        WriteJar(Path.Combine(mods, "plain.jar"), ["assets/plain.png"]);

        var packs = Path.Combine(_root, "resourcepacks");
        Directory.CreateDirectory(packs);
        File.WriteAllBytes(Path.Combine(packs, "look.zip"), new byte[4096]);
        return _root;
    }

    private static void WriteJar(string path, IEnumerable<string> entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var stream = archive.CreateEntry(entry).Open();
            stream.Write(new byte[64]);
        }
    }

    private static IEnumerable<(string Path, long SizeBytes)> FileList(string pack) =>
        Directory.EnumerateFiles(pack, "*", SearchOption.AllDirectories)
            .Select(file => (
                Path.GetRelativePath(pack, file).Replace(Path.DirectorySeparatorChar, '/'),
                new FileInfo(file).Length));
}
