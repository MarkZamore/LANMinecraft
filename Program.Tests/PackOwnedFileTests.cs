using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Files a pack keeps for itself.
///
/// An instance is merged from the pack three ways, and a local copy that
/// matches neither the pack's old nor its new version is treated as the
/// player's and left alone. That protects settings somebody tuned and defeats
/// the pack whenever the game rewrites a file itself - NeoForge normalises
/// config/fml.toml at every start, and mods regenerate their own tables. One
/// stale dependency line in fml.toml met a player with a red screen on every
/// launch, twice, while the corrected file sat unread in the conflicts folder.
/// </summary>
public sealed class PackOwnedFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-pack-owned-{Guid.NewGuid():N}");

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
    public async Task AFileThePackOwns_IsRewrittenOverTheLocalOne()
    {
        var (service, paths, packDir) = CreatePack();
        using var scope = service;
        await service.PrepareAsync("TestPack");
        var gameDir = service.GetInstanceDirectory("TestPack");
        var instanceCopy = Path.Combine(gameDir, "config", "fml.toml");
        Assert.True(File.Exists(instanceCopy));

        // The game rewrites it, the way a loader normalises its own config.
        await File.WriteAllTextAsync(instanceCopy, "[dependencyOverrides]\n  a = [\"+gone\"]\n");
        // And the pack ships a corrected one.
        await File.WriteAllTextAsync(Path.Combine(packDir, "config", "fml.toml"), "maxThreads = -1\n");

        await service.PrepareAsync("TestPack");

        Assert.Equal("maxThreads = -1\n", (await File.ReadAllTextAsync(instanceCopy)).Replace("\r\n", "\n"));
        // What was there is kept, not thrown away.
        var preserved = Directory.EnumerateFiles(
            Path.Combine(paths.Personal, "PackConflicts"), "fml.toml.user-file",
            SearchOption.AllDirectories).ToArray();
        Assert.Single(preserved);
        Assert.Contains("+gone", await File.ReadAllTextAsync(preserved[0]), StringComparison.Ordinal);
    }

    /// <summary>
    /// The claim is a list, not a switch: a file outside it keeps the merge
    /// that protects a player's own settings.
    /// </summary>
    [Fact]
    public async Task AFileThePackDoesNotClaim_IsStillThePlayers()
    {
        var (service, _, packDir) = CreatePack();
        using var scope = service;
        await service.PrepareAsync("TestPack");
        var gameDir = service.GetInstanceDirectory("TestPack");
        var tuned = Path.Combine(gameDir, "config", "mine.toml");

        await File.WriteAllTextAsync(tuned, "renderDistance = 32\n");
        await File.WriteAllTextAsync(Path.Combine(packDir, "config", "mine.toml"), "renderDistance = 8\n");

        await service.PrepareAsync("TestPack");

        Assert.Equal("renderDistance = 32\n", (await File.ReadAllTextAsync(tuned)).Replace("\r\n", "\n"));
    }

    /// <summary>A pack that drops a file it owns takes it out of the instance too.</summary>
    [Fact]
    public async Task AFileThePackOwnsAndDrops_LeavesTheInstance()
    {
        var (service, _, packDir) = CreatePack();
        using var scope = service;
        await service.PrepareAsync("TestPack");
        var gameDir = service.GetInstanceDirectory("TestPack");
        var instanceCopy = Path.Combine(gameDir, "config", "fml.toml");

        await File.WriteAllTextAsync(instanceCopy, "rewritten by the game\n");
        File.Delete(Path.Combine(packDir, "config", "fml.toml"));

        await service.PrepareAsync("TestPack");

        Assert.False(File.Exists(instanceCopy));
    }

    [Theory]
    [InlineData("config/fml.toml", "config/fml.toml", true)]
    [InlineData("config/agritech/*.json", "config/agritech/crops.json", true)]
    [InlineData("config/agritech/*.json", "config/agritech/deep/crops.json", false)]
    [InlineData("config/**/*.json", "config/agritech/deep/crops.json", true)]
    [InlineData("config/fml.toml", "config/fml.toml.bak", false)]
    [InlineData("config/fml.toml", "mods/fml.toml", false)]
    public void TheClaimIsMatchedAsAGlob(string pattern, string path, bool owned)
    {
        var directory = Path.Combine(_root, "pack", PackInstanceService.LauncherDataRoot);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "pack-owned.txt"), pattern + "\n");

        var service = PackOwnedFileService.Load(Path.Combine(_root, "pack"));

        Assert.Equal(owned, service.Owns(path));
    }

    /// <summary>
    /// A claim on everything would turn the merge off for the whole instance,
    /// which is not a thing a pack gets to do by writing one line.
    /// </summary>
    [Theory]
    [InlineData("*")]
    [InlineData("**")]
    [InlineData("../outside.txt")]
    public void ClaimingEverythingIsRefused(string pattern)
    {
        var directory = Path.Combine(_root, "greedy", PackInstanceService.LauncherDataRoot);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "pack-owned.txt"), pattern + "\n");

        var service = PackOwnedFileService.Load(Path.Combine(_root, "greedy"));

        Assert.False(service.Owns("config/fml.toml"));
        Assert.False(service.Owns("mods/anything.jar"));
    }

    [Fact]
    public void APackWithoutTheListOwnsNothing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "plain"));
        Assert.False(PackOwnedFileService.Load(Path.Combine(_root, "plain")).Owns("config/fml.toml"));
        Assert.False(PackOwnedFileService.None.Owns("config/fml.toml"));
    }

    private (PackInstanceService Service, AppPaths Paths, string PackDir) CreatePack()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var packDir = Path.Combine(paths.Packs, "TestPack");
        Directory.CreateDirectory(Path.Combine(packDir, "config"));
        Directory.CreateDirectory(Path.Combine(packDir, PackInstanceService.LauncherDataRoot));
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
        File.WriteAllText(Path.Combine(packDir, "config", "fml.toml"), "maxThreads = -1\n");
        File.WriteAllText(Path.Combine(packDir, "config", "mine.toml"), "renderDistance = 8\n");
        File.WriteAllText(
            Path.Combine(packDir, PackInstanceService.LauncherDataRoot, "pack-owned.txt"),
            "# the loader rewrites this one itself\nconfig/fml.toml\n");
        return (new PackInstanceService(paths, new Logger(paths.LogFile)), paths, packDir);
    }
}
