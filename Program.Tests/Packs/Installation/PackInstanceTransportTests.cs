using System.Security.Cryptography;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PackInstanceTransportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"minecraft-instance-transport-{Guid.NewGuid():N}");
    private const string PackName = "TestPack";
    private const string CurrentBytes = "pinned-transport";
    private const string FileName = "e4steam-forge-mc1.18.2-1.20.2-v0.3.2.jar";

    public void Dispose() => TempTree.Delete(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentExactLauncherTransport_SurvivesRepeatedPreparationWithoutBackups(bool sourceHasMods)
    {
        var (paths, pack, service) = CreatePack(sourceHasMods);
        using var scope = service;
        await service.PrepareAsync(PackName);
        var game = service.GetInstanceDirectory(PackName);
        var installed = Path.Combine(game, "mods", FileName);
        File.WriteAllText(installed, CurrentBytes);
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(installed, stamp);

        await service.PrepareAsync(PackName);
        await service.PrepareAsync(PackName);

        Assert.Equal(CurrentBytes, File.ReadAllText(installed));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(installed));
        Assert.False(Directory.Exists(paths.PackConflicts));
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(game, ".portable-instance.json")));
        Assert.False(state.RootElement.GetProperty("modFiles").TryGetProperty(FileName, out _));
        Assert.False(File.Exists(Path.Combine(pack, "mods", FileName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForgedSameNameOrOtherPlatformTransport_IsPreservedOutsideTheActiveMods(bool sourceHasMods)
    {
        var (paths, _, service) = CreatePack(sourceHasMods);
        using var scope = service;
        await service.PrepareAsync(PackName);
        var mods = Path.Combine(service.GetInstanceDirectory(PackName), "mods");
        const string forged = "forged-transport";
        File.WriteAllText(Path.Combine(mods, FileName), forged);
        const string otherPlatform = "e4steam-neoforge-mc1.20.2-26.2-v0.3.2.jar";
        File.WriteAllText(Path.Combine(mods, otherPlatform), CurrentBytes);

        await service.PrepareAsync(PackName);

        Assert.False(File.Exists(Path.Combine(mods, FileName)));
        Assert.False(File.Exists(Path.Combine(mods, otherPlatform)));
        var forgedCopy = Assert.Single(Directory.GetFiles(paths.PackConflicts, FileName + ".user-file", SearchOption.AllDirectories));
        Assert.Equal(forged, File.ReadAllText(forgedCopy));
        var otherCopy = Assert.Single(Directory.GetFiles(paths.PackConflicts, otherPlatform + ".user-file", SearchOption.AllDirectories));
        Assert.Equal(CurrentBytes, File.ReadAllText(otherCopy));
    }

    [Fact]
    public async Task LegacyInstanceWithoutState_KeepsTheVerifiedTransportWhilePreservingUnknownMods()
    {
        var (paths, _, service) = CreatePack(sourceHasMods: true);
        using var scope = service;
        var mods = Path.Combine(service.GetInstanceDirectory(PackName), "mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, FileName), CurrentBytes);
        File.WriteAllText(Path.Combine(mods, "player-extra.jar"), "personal-extra");

        await service.PrepareAsync(PackName);

        Assert.Equal(CurrentBytes, File.ReadAllText(Path.Combine(mods, FileName)));
        Assert.False(File.Exists(Path.Combine(mods, "player-extra.jar")));
        Assert.Equal("personal-extra", File.ReadAllText(Assert.Single(
            Directory.GetFiles(paths.PackConflicts, "player-extra.jar", SearchOption.AllDirectories))));
        Assert.Empty(Directory.GetFiles(paths.PackConflicts, FileName, SearchOption.AllDirectories));
    }

    private (AppPaths Paths, string Pack, PackInstanceService Service) CreatePack(bool sourceHasMods)
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var pack = paths.CombineUnderPacks(PackName);
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, PackManifestService.ManifestFileName),
            """{"schemaVersion":1,"minecraftVersion":"1.20.1","loader":{"type":"forge","version":"47.4.0"}}""");
        if (sourceHasMods)
        {
            Directory.CreateDirectory(Path.Combine(pack, "mods"));
            File.WriteAllText(Path.Combine(pack, "mods", "author.jar"), "author-mod");
        }
        var expected = new SteamTransportBuild("0.3.2", FileName, CurrentBytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(CurrentBytes))),
            123, [PackLoaderKind.Forge], [1, 18, 2], [1, 20, 3]);
        return (paths, pack, new PackInstanceService(paths, new Logger(paths.LogFile), descriptor =>
        {
            Assert.Equal(PackLoaderKind.Forge, descriptor.Loader.Type);
            Assert.Equal("1.20.1", descriptor.MinecraftVersion);
            return expected;
        }));
    }
}
