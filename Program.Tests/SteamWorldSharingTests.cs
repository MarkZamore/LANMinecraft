using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Minecraft.Tests;

public sealed class SteamWorldSharingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "steam-world-sharing-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("1.12.2", "forge", 8, true)]
    [InlineData("1.16.5", "forge", 8, true)]
    [InlineData("1.18.2", "forge", 17, false)]
    [InlineData("1.19.2", "forge", 17, false)]
    [InlineData("1.20.1", "forge", 17, false)]
    [InlineData("1.21.1", "neoforge", 21, false)]
    public void DescriptorOptInAffectsTheRuntimeIdentity(string version, string loader, int java, bool legacy)
    {
        var original = WriteDescriptor(version, loader, java, null);
        var disabled = WriteDescriptor(version, loader, java, false);
        var selected = WriteDescriptor(version, loader, java, true);

        Assert.Equal(original.DescriptorHash, disabled.DescriptorHash);
        Assert.NotEqual(original.DescriptorHash, selected.DescriptorHash);
        Assert.False(original.PreserveWorldSettingsOnShare);
        Assert.True(selected.PreserveWorldSettingsOnShare);
        Assert.Equal(legacy, PackManifestService.UsesJava8SteamWorldSharing(selected));
    }

    [Theory]
    [InlineData("1.12.2", "forge", 17)]
    [InlineData("1.16.4", "forge", 8)]
    [InlineData("1.20.1", "fabric", 17)]
    public void UnsupportedRuntimeCannotDeclareThePolicy(string version, string loader, int java) =>
        Assert.Throws<InvalidDataException>(() => WriteDescriptor(version, loader, java, true));

    [Theory]
    [InlineData("1.12.2")]
    [InlineData("1.16.5")]
    public void LegacyWorldSharingIsSelectedOnlyWhenThePackRequestsIt(string version)
    {
        var original = WriteDescriptor(version, "forge", 8, null);
        var optedIn = WriteDescriptor(version, "forge", 8, true);
        Assert.False(SteamTransportCatalog.Find(original)!.PreservesWorldSettingsOnShare);
        Assert.True(SteamTransportCatalog.Find(optedIn)!.PreservesWorldSettingsOnShare);
        Assert.NotEqual(SteamTransportCatalog.Find(original)!.Sha256, SteamTransportCatalog.Find(optedIn)!.Sha256);
    }

    [Fact]
    public async Task Java8SelectsTheVerifiedModWithoutAttachingTheJava17Agent()
    {
        var descriptor = WriteDescriptor("1.12.2", "forge", 8, true);
        var paths = new AppPaths(_root);
        paths.Ensure();
        var game = Path.Combine(_root, "test-game");
        var mods = Directory.CreateDirectory(Path.Combine(game, "mods"));
        const string fileName = "e4steam-forge-world-sharing-test.jar";
        var artifact = new byte[] { 1, 2, 3 };
        var pin = new SteamTransportBuild("0.3.4", fileName, artifact.Length,
            Convert.ToHexStringLower(SHA256.HashData(artifact)), 1, [PackLoaderKind.Forge],
            [1, 12, 2], [1, 12, 3], PreservesWorldSettingsOnShare: true);
        var runtime = new PreparedRuntime(_root, "test", "javaw.exe", "client.jar", descriptor);
        using var adapter = new PortableIdentityAdapterService(paths, new Logger(paths.LogFile), _ => pin);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.PrepareJvmArgumentsAsync(runtime, game, CancellationToken.None));

        File.WriteAllBytes(Path.Combine(mods.FullName, fileName), artifact);
        Assert.Equal(["-Dlink.e4steam.launcherManagedLan=true"],
            await adapter.PrepareJvmArgumentsAsync(runtime, game, CancellationToken.None));
    }

    private PackRuntimeDescriptor WriteDescriptor(string version, string loader, int java, bool? preserve)
    {
        Directory.CreateDirectory(_root);
        var descriptor = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["minecraftVersion"] = version,
            ["loader"] = new JsonObject { ["type"] = loader, ["version"] = "1.2.3" },
            ["javaMajorVersion"] = java
        };
        if (preserve is { } value) descriptor["preserveWorldSettingsOnShare"] = value;
        File.WriteAllText(Path.Combine(_root, PackManifestService.ManifestFileName), JsonSerializer.Serialize(descriptor));
        return PackManifestService.Load(_root);
    }

    public void Dispose() => TempTree.Delete(_root);
}
