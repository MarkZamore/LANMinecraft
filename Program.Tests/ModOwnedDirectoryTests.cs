using Minecraft;

namespace Minecraft.Tests;

// Required layout comes from pack contents. Empty directories need a tracked
// placeholder when the pack is distributed as files; player-created empty
// directories are also retained during cleanup.
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preparation_RestoresPackDeclaredDirectories(bool managedSnapshot)
    {
        var (paths, logger) = Prepare(_root);
        using var service = new PackInstanceService(paths, logger);
        var pack = Path.Combine(paths.Packs, "TestPack");
        var relative = "extension-resources/required/empty/.keep";
        var marker = Path.Combine(pack, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, "");
        if (managedSnapshot)
        {
            var entries = new[] { relative, PackManifestService.ManifestFileName, "client.jar" }
                .ToDictionary(path => path, path =>
                {
                    var bytes = File.ReadAllBytes(Path.Combine(pack, path.Replace('/', Path.DirectorySeparatorChar)));
                    return new { sizeBytes = bytes.LongLength, sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) };
                });
            File.WriteAllText(Path.Combine(pack, PortablePackSyncService.SyncStateFileName),
                System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, revision = "declared-layout", files = entries }));
        }
        await service.PrepareAsync("TestPack");
        var declared = Path.Combine(service.GetInstanceDirectory("TestPack"), "extension-resources", "required", "empty");
        Assert.True(Directory.Exists(declared));
        Directory.Delete(declared, recursive: true);

        await service.PrepareAsync("TestPack");

        Assert.True(
            Directory.Exists(declared),
            "A directory declared by pack files was not restored during preparation.");
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
