using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Minecraft.Tests;

/// <summary>
/// e4steam is what carries multiplayer once the launcher speaks Steam, so the
/// launcher pins it the way it pins FTB Essentials: exact bytes, reinstalled
/// after every instance prepare, and never mixed with another build.
/// </summary>
public sealed class SteamTransportModTests
{
    [Fact]
    public void PinnedE4steamArtifact_MatchesTheUpstreamRelease()
    {
        Assert.Equal("0.2.4", ManagedComponentService.E4steamVersion);
        Assert.Equal(
            "e4steam-neoforge-mc1.20.2-26.2-v0.2.4.jar",
            ManagedComponentService.E4steamFileName);
        Assert.Equal(2_673_601, ManagedComponentService.E4steamSizeBytes);
        Assert.Equal(
            "47f0c8671bb8889e226df2bef41779b1e3cdb9271a8cfe3b216cfe6eaf910420",
            ManagedComponentService.E4steamSha256);
        Assert.Equal(
            [
                "https://mediafilez.forgecdn.net/files/8611/556/e4steam-neoforge-mc1.20.2-26.2-v0.2.4.jar",
                "https://github.com/Kamilhik/e4steam/releases/download/v0.2.4/e4steam-neoforge-mc1.20.2-26.2-v0.2.4.jar"
            ],
            ManagedComponentService.E4steamDownloadUris.Select(uri => uri.AbsoluteUri));
    }

    [Theory]
    [InlineData(PackLoaderKind.NeoForge, "1.21.1", true)]
    [InlineData(PackLoaderKind.NeoForge, "1.20.2", true)]
    [InlineData(PackLoaderKind.NeoForge, "1.21.8", true)]
    [InlineData(PackLoaderKind.NeoForge, "26.2", true)]
    [InlineData(PackLoaderKind.NeoForge, "1.20.1", false)]
    [InlineData(PackLoaderKind.NeoForge, "26.3", false)]
    [InlineData(PackLoaderKind.Forge, "1.21.1", false)]
    [InlineData(PackLoaderKind.Fabric, "1.21.1", false)]
    [InlineData(PackLoaderKind.Vanilla, "1.21.1", false)]
    public void SteamPlayPolicy_ServesEveryNeoForgePackTheModDeclares(
        PackLoaderKind loader,
        string minecraftVersion,
        bool expected)
    {
        var descriptor = new PackRuntimeDescriptor(
            1,
            minecraftVersion,
            new PackLoaderDescriptor(loader, "21.1.244"),
            "client.jar",
            "hash");

        Assert.Equal(expected, SteamPlayPolicy.IsSupported(descriptor));
    }

    [Fact]
    public void SteamPlayPolicy_RejectsMissingOrUnparsableVersions()
    {
        Assert.False(SteamPlayPolicy.IsSupported(null));
        Assert.False(SteamPlayPolicy.IsSupportedMinecraftVersion(null));
        Assert.False(SteamPlayPolicy.IsSupportedMinecraftVersion(""));
        Assert.False(SteamPlayPolicy.IsSupportedMinecraftVersion("1.21.1-pre1"));
    }

    [Fact]
    public async Task MissingMod_IsDownloadedVerifiedAndInstalled()
    {
        using var fixture = new TemporaryRoot();
        var payload = Encoding.UTF8.GetBytes("pretend e4steam jar");
        var component = TestComponent(payload);
        var handler = new StubHandler(payload);
        using var httpClient = new HttpClient(handler);
        var service = fixture.CreateService(httpClient, component);
        var instance = fixture.CreatePreparedInstance();

        var result = await service.EnsureSteamTransportModAsync(instance, CancellationToken.None);

        Assert.True(result.Downloaded);
        Assert.True(result.Installed);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.InstalledPath, CancellationToken.None));
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.CachePath, CancellationToken.None));
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task ModRemovedByInstanceMirroring_IsRestoredFromTheCacheWithoutDownloading()
    {
        using var fixture = new TemporaryRoot();
        var payload = Encoding.UTF8.GetBytes("pretend e4steam jar");
        var component = TestComponent(payload);
        var handler = new StubHandler(payload);
        using var httpClient = new HttpClient(handler);
        var service = fixture.CreateService(httpClient, component);
        var instance = fixture.CreatePreparedInstance();

        var first = await service.EnsureSteamTransportModAsync(instance, CancellationToken.None);
        // PackInstanceService.MirrorMods deletes every instance JAR the pack
        // does not carry, which is exactly what happens on the next launch.
        File.Delete(first.InstalledPath);

        var second = await service.EnsureSteamTransportModAsync(instance, CancellationToken.None);

        Assert.False(second.Downloaded);
        Assert.True(second.Installed);
        Assert.True(File.Exists(second.InstalledPath));
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task PackShippedModWithTheSameBytes_IsAcceptedAndNotRedownloaded()
    {
        using var fixture = new TemporaryRoot();
        var payload = Encoding.UTF8.GetBytes("pretend e4steam jar");
        var component = TestComponent(payload);
        var handler = new StubHandler(payload);
        using var httpClient = new HttpClient(handler);
        var service = fixture.CreateService(httpClient, component);
        var instance = fixture.CreatePreparedInstance();
        await File.WriteAllBytesAsync(
            Path.Combine(instance.GameDirectory, "mods", component.FileName),
            payload,
            CancellationToken.None);

        var result = await service.EnsureSteamTransportModAsync(instance, CancellationToken.None);

        Assert.False(result.Downloaded);
        Assert.False(result.Installed);
        Assert.True(result.CachePopulated);
        Assert.Empty(handler.RequestUris);
    }

    [Theory]
    [InlineData("e4steam-neoforge-mc1.20.2-26.2-v0.2.3.jar")]
    [InlineData("e4mc_minecraft-neoforge-1.21.1.jar")]
    public async Task AnotherBuildOfTheSameMod_StopsTheLaunch(string conflictingName)
    {
        using var fixture = new TemporaryRoot();
        var payload = Encoding.UTF8.GetBytes("pretend e4steam jar");
        var component = TestComponent(payload);
        using var httpClient = new HttpClient(new StubHandler(payload));
        var service = fixture.CreateService(httpClient, component);
        var instance = fixture.CreatePreparedInstance();
        await File.WriteAllTextAsync(
            Path.Combine(instance.GameDirectory, "mods", conflictingName),
            "other build",
            CancellationToken.None);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnsureSteamTransportModAsync(instance, CancellationToken.None));
        Assert.Contains(conflictingName, failure.Message, StringComparison.Ordinal);
    }

    private static ManagedComponentDescriptor TestComponent(byte[] payload) =>
        new(
            "e4steam-test",
            20400,
            "e4steam-neoforge-test.jar",
            [new Uri("https://github.com/Kamilhik/e4steam/releases/download/v0.2.4/e4steam.jar")],
            payload.LongLength,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());

    private sealed class StubHandler(byte[] payload) : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri
                ?? throw new InvalidOperationException("Request URI is missing."));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
        }
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "MinecraftSteamTransportTests",
                Guid.NewGuid().ToString("N"));
            Paths = new AppPaths(Root);
            Paths.Ensure();
            Logger = new Logger(Path.Combine(Paths.Personal, "steam-transport.log"));
        }

        public string Root { get; }
        public AppPaths Paths { get; }
        public Logger Logger { get; }

        public ManagedComponentService CreateService(
            HttpClient httpClient,
            ManagedComponentDescriptor e4steam) =>
            new(Paths, Logger, httpClient, e4steam);

        public PackInstanceContext CreatePreparedInstance()
        {
            var gameDirectory = Paths.CombineUnderInstances("TestPack");
            var packDirectory = Paths.CombineUnderPacks("TestPack");
            Directory.CreateDirectory(gameDirectory);
            Directory.CreateDirectory(packDirectory);
            Directory.CreateDirectory(Path.Combine(gameDirectory, "mods"));
            return new PackInstanceContext(
                packDirectory,
                gameDirectory,
                Path.Combine(packDirectory, "client.jar"));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
