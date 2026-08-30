using Minecraft;

namespace Minecraft.Tests;

// A mod may make a folder once, on its first run, and afterwards only read it.
// KubeJS does exactly that with kubejs/assets: ClientAssetPacks asks
// File.listFiles what is inside, that answers null rather than empty when the
// folder is gone, and the null lands in Objects.requireNonNull - a red "KubeJS
// client script errors" screen at every start. The launcher used to delete every
// empty folder in an instance while tidying, which took that one with it.
public sealed class ModOwnedDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-mod-directories-{Guid.NewGuid():N}");

    public void Dispose()
    {
        TempTree.Delete(_root);
    }

    private static (AppPaths Paths, Logger Logger) Prepare(string root)
    {
        var paths = new AppPaths(root);
        paths.Ensure();
        var packDir = Path.Combine(paths.Packs, "TestPack");
        Directory.CreateDirectory(packDir);
        File.WriteAllText(Path.Combine(packDir, "client.jar"), "jar");
        File.WriteAllText(
            Path.Combine(packDir, PackManifestService.ManifestFileName),
            """
            {
              "schemaVersion": 1,
              "minecraftVersion": "1.21.1",
              "loader": { "type": "vanilla" },
              "clientJar": "client.jar"
            }
            """);
        return (paths, new Logger(paths.LogFile));
    }

    [Fact]
    public async Task Cleanup_LeavesAnEmptyFolderAModMade()
    {
        var (paths, logger) = Prepare(_root);
        using var service = new PackInstanceService(paths, logger);
        await service.PrepareAsync("TestPack");

        var gameDir = service.GetInstanceDirectory("TestPack");
        var modMade = Path.Combine(gameDir, "kubejs", "assets");
        Directory.CreateDirectory(modMade);
        var alsoEmpty = Path.Combine(gameDir, "somemod", "cache");
        Directory.CreateDirectory(alsoEmpty);

        await service.CleanupGeneratedLocalArtifactsAsync("TestPack", removeSessionLogs: true);

        Assert.True(Directory.Exists(modMade), "kubejs/assets was deleted for being empty.");
        Assert.True(Directory.Exists(alsoEmpty), "An empty folder the launcher never made was deleted.");
    }

    [Fact]
    public async Task Preparation_PutsBackKubeJsFoldersThatAreGone()
    {
        var (paths, logger) = Prepare(_root);
        using var service = new PackInstanceService(paths, logger);
        await service.PrepareAsync("TestPack");

        var gameDir = service.GetInstanceDirectory("TestPack");
        var kubejs = Path.Combine(gameDir, "kubejs");
        // A pack with KubeJS in it, and the folder the mod would have made
        // missing - which is the state the old tidying left behind.
        Directory.CreateDirectory(Path.Combine(kubejs, "server_scripts"));
        Assert.False(Directory.Exists(Path.Combine(kubejs, "assets")));

        await service.PrepareAsync("TestPack");

        Assert.True(
            Directory.Exists(Path.Combine(kubejs, "assets")),
            "kubejs/assets was not put back, so KubeJS will fail on the next start.");
    }

    [Fact]
    public async Task Preparation_MakesNoKubeJsFoldersForAPackWithoutKubeJs()
    {
        var (paths, logger) = Prepare(_root);
        using var service = new PackInstanceService(paths, logger);
        await service.PrepareAsync("TestPack");

        var gameDir = service.GetInstanceDirectory("TestPack");
        Assert.False(
            Directory.Exists(Path.Combine(gameDir, "kubejs")),
            "A pack that never had KubeJS was given a kubejs folder.");
    }
}
