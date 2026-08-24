using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The list of mods a bug report carries.
///
/// Every jar exists twice on disk - once in the pack the launcher downloaded,
/// once in the instance it synced that pack into - and the report was dropping
/// duplicates by full path, which two copies of the same jar never share. So
/// each mod was written down twice: one report listed 1763 mods for 882 jars,
/// and anyone reading it had to know that before trusting a count.
/// </summary>
public sealed class SupportReportModListTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-report-mods-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void AJarInBothThePackAndTheInstance_IsListedOnce()
    {
        var paths = Prepare(
            pack: ["alpha.jar", "beta.jar", "gamma.jar"],
            instance: ["alpha.jar", "beta.jar", "gamma.jar"]);

        var mods = SupportDiagnosticSnapshotBuilder.ReadMods(paths, "LL8 Extended");

        Assert.Equal(["alpha.jar", "beta.jar", "gamma.jar"], mods.Select(mod => mod.FileName));
    }

    /// <summary>
    /// The instance's copy is the one the game opens, so where the two differ
    /// the report has to describe that one.
    /// </summary>
    [Fact]
    public void WhereTheTwoCopiesDiffer_TheInstanceOneIsDescribed()
    {
        var paths = Prepare(pack: ["alpha.jar"], instance: ["alpha.jar"], instanceFiller: 4096);

        var mod = Assert.Single(SupportDiagnosticSnapshotBuilder.ReadMods(paths, "LL8 Extended"));

        Assert.Equal(4096, mod.Size);
    }

    /// <summary>A jar only one side has is still in the list.</summary>
    [Fact]
    public void AJarOnlyOneSideHas_IsStillListed()
    {
        var paths = Prepare(pack: ["only-pack.jar"], instance: ["only-instance.jar"]);

        var mods = SupportDiagnosticSnapshotBuilder.ReadMods(paths, "LL8 Extended");

        Assert.Equal(["only-instance.jar", "only-pack.jar"], mods.Select(mod => mod.FileName));
    }

    private AppPaths Prepare(string[] pack, string[] instance, int instanceFiller = 16)
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        Write(Path.Combine(paths.CombineUnderPacks("LL8 Extended"), "mods"), pack, 16);
        Write(Path.Combine(paths.CombineUnderInstances("LL8 Extended"), "mods"), instance, instanceFiller);
        return paths;
    }

    private static void Write(string directory, IEnumerable<string> names, int size)
    {
        Directory.CreateDirectory(directory);
        foreach (var name in names)
        {
            File.WriteAllBytes(Path.Combine(directory, name), new byte[size]);
        }
    }
}
