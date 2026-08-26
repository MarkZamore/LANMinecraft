using System.IO;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Telling a Fabric profile which jar is the game.
/// </summary>
/// <remarks>
/// A loader profile has no jar of its own; it inherits the base version and
/// runs out of the jar that version brought. Forge and NeoForge write the
/// launcher format's <c>jar</c> field to say so and the Fabric and Quilt
/// installers do not, because their own launcher works it out from
/// <c>inheritsFrom</c>. CmlLib does not: with no <c>jar</c> it takes the
/// profile's own id, puts a file that has never existed on the class path,
/// and Fabric's Knot - which looks there and nowhere else for the game -
/// found nothing and said so.
/// </remarks>
public sealed class KnotGameJarTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-knot-jar-{Guid.NewGuid():N}");

    public void Dispose() => TempTree.Delete(_root);

    private string WriteProfile(string json)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "fabric-loader-0.14.10-1.18.2.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string? JarOf(string path) =>
        JsonDocument.Parse(File.ReadAllText(path)).RootElement
            .TryGetProperty("jar", out var jar) ? jar.GetString() : null;

    [Fact]
    public void AProfileWithoutAJar_IsToldWhichOneIsTheGame()
    {
        var path = WriteProfile("""
        {
          "id": "fabric-loader-0.14.10-1.18.2",
          "inheritsFrom": "1.18.2",
          "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
          "libraries": []
        }
        """);

        Assert.True(KnotGameJar.NameIt(path, "1.18.2", new Logger(Path.Combine(_root, "log.txt"))));

        Assert.Equal("1.18.2", JarOf(path));
        // And everything else it said is still there.
        var profile = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        Assert.Equal("1.18.2", profile.GetProperty("inheritsFrom").GetString());
        Assert.Equal(
            "net.fabricmc.loader.impl.launch.knot.KnotClient",
            profile.GetProperty("mainClass").GetString());
    }

    /// <summary>A profile that already names one is left exactly as it is.</summary>
    [Fact]
    public void AProfileThatAlreadyNamesAJar_IsNotTouched()
    {
        var path = WriteProfile("""
        {"id": "1.20.1-forge-47.3.0", "inheritsFrom": "1.20.1", "jar": "1.20.1"}
        """);
        var before = File.ReadAllText(path);

        Assert.False(KnotGameJar.NameIt(path, "1.20.1", new Logger(Path.Combine(_root, "log.txt"))));

        Assert.Equal(before, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    public void AProfileThatCannotBeRead_IsLeftAlone(string json)
    {
        var path = WriteProfile(json);
        Assert.False(KnotGameJar.NameIt(path, "1.18.2", new Logger(Path.Combine(_root, "log.txt"))));
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void AProfileThatIsNotThere_IsLeftAlone()
    {
        Directory.CreateDirectory(_root);
        Assert.False(KnotGameJar.NameIt(
            Path.Combine(_root, "absent.json"), "1.18.2", new Logger(Path.Combine(_root, "log.txt"))));
    }
}
