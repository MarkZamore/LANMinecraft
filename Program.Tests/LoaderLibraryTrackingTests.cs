using System.IO;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The libraries a version profile names, which is how the files a loader
/// installer makes get written down.
///
/// Nothing downloaded them, so nothing reported them, so no runtime state named
/// them - and the shared store's sweep took the whole classpath. The game then
/// started with neoforge and minecraft both [MISSING], and it could not recover
/// on its own, because a file no state names is also a file no validation
/// misses. These pin the reading that stops that happening again.
/// </summary>
public sealed class LoaderLibraryTrackingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-libs-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            TempTree.Delete(_root);
        }
        catch
        {
        }
    }

    /// <summary>
    /// The four that were actually lost, spelled the way a NeoForge profile
    /// spells them: with a classifier, and with a group that becomes a path.
    /// </summary>
    [Fact]
    public void TheLoadersOwnJars_AreFoundFromTheProfile()
    {
        var libraries = Path.Combine(_root, "libraries");
        var wanted = new[]
        {
            "net/neoforged/neoforge/21.1.248/neoforge-21.1.248-client.jar",
            "net/neoforged/neoforge/21.1.248/neoforge-21.1.248-universal.jar",
            "net/minecraft/client/1.21.1-20240808.144430/client-1.21.1-20240808.144430-srg.jar",
            "net/minecraft/client/1.21.1-20240808.144430/client-1.21.1-20240808.144430-extra.jar"
        };
        foreach (var path in wanted) Write(Path.Combine(libraries, path.Replace('/', Path.DirectorySeparatorChar)), "jar");

        var json = Profile(
            "net.neoforged:neoforge:21.1.248:client",
            "net.neoforged:neoforge:21.1.248:universal",
            "net.minecraft:client:1.21.1-20240808.144430:srg",
            "net.minecraft:client:1.21.1-20240808.144430:extra");

        var found = PackRuntimeService.LibrariesNamedBy(json, libraries).ToArray();

        Assert.Equal(4, found.Length);
        foreach (var path in wanted)
        {
            Assert.Contains(
                Path.GetFullPath(Path.Combine(libraries, path.Replace('/', Path.DirectorySeparatorChar))),
                found);
        }
    }

    /// <summary>
    /// A library with no classifier resolves without one - the ordinary case,
    /// and the one an off-by-one in the split would break.
    /// </summary>
    [Fact]
    public void ALibraryWithoutAClassifier_ResolvesWithoutOne()
    {
        var libraries = Path.Combine(_root, "libraries");
        var jar = Path.Combine(libraries, "org", "ow2", "asm", "asm", "9.7", "asm-9.7.jar");
        Write(jar, "jar");

        Assert.Equal(
            [Path.GetFullPath(jar)],
            PackRuntimeService.LibrariesNamedBy(Profile("org.ow2.asm:asm:9.7"), libraries).ToArray());
    }

    /// <summary>
    /// A profile names libraries for every operating system. Claiming one that
    /// is not on this disk would fail every validation from here on, so only
    /// what exists is named.
    /// </summary>
    [Fact]
    public void ALibraryThatIsNotOnThisDisk_IsNotClaimed()
    {
        var libraries = Path.Combine(_root, "libraries");
        Write(Path.Combine(libraries, "here", "there", "1.0", "there-1.0.jar"), "jar");

        var found = PackRuntimeService.LibrariesNamedBy(
            Profile("here:there:1.0", "gone:missing:2.0", "gone:missing:2.0:natives-linux"),
            libraries);

        Assert.Single(found);
    }

    /// <summary>A profile that will not parse names nothing rather than throwing.</summary>
    [Fact]
    public void AProfileThatWillNotParse_NamesNothing()
    {
        var json = Path.Combine(_root, "broken.json");
        Write(json, "{ not json");

        Assert.Empty(PackRuntimeService.LibrariesNamedBy(json, Path.Combine(_root, "libraries")));
    }

    /// <summary>And so does one with nothing to say.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"libraries\":[]}")]
    [InlineData("{\"libraries\":[{},{\"name\":\"\"},{\"name\":\"nonsense\"}]}")]
    public void AProfileWithNoUsableNames_NamesNothing(string body)
    {
        var json = Path.Combine(_root, "empty.json");
        Write(json, body);

        Assert.Empty(PackRuntimeService.LibrariesNamedBy(json, Path.Combine(_root, "libraries")));
    }

    private string Profile(params string[] names)
    {
        var path = Path.Combine(_root, "profile-" + Guid.NewGuid().ToString("N")[..6] + ".json");
        Write(path, JsonSerializer.Serialize(new { libraries = names.Select(name => new { name }) }));
        return path;
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
