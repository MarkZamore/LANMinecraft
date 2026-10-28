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
    /// <summary>
    /// Every build the launcher will install is the file its author published,
    /// to the byte. Sizes and hashes were taken from the release itself.
    /// </summary>
    [Fact]
    public void EveryCataloguedBuild_MatchesTheUpstreamRelease()
    {
        Assert.Equal("0.3.2", SteamTransportCatalog.Version);
        Assert.Equal(18, SteamTransportCatalog.Builds.Count(build => build.Version == "0.3.2"));
        Assert.All(SteamTransportCatalog.Builds, build =>
        {
            Assert.Equal(64, build.Sha256.Length);
            Assert.True(build.SizeBytes > 0);
            Assert.NotEmpty(build.Loaders);
            Assert.Equal(
                $"https://github.com/Kamilhik/e4steam/releases/download/v{build.Version}/{build.FileName}",
                Assert.Single(build.DownloadUris).AbsoluteUri);
        });

        var neoforge = Assert.Single(
            SteamTransportCatalog.Builds, build => build.Loaders.Contains(PackLoaderKind.NeoForge));
        Assert.Equal("e4steam-neoforge-mc1.20.2-26.2-v0.3.2.jar", neoforge.FileName);
        Assert.Equal(3_734_370, neoforge.SizeBytes);
        Assert.Equal(
            "4f182b2560a6e001f248cd59cc063dd1c99d92a9b1b38ef099bc8ee822fe0418",
            neoforge.Sha256);

        // Two Forge builds now, one per range; this is the one that carries
        // 1.19.2 and 1.20.1, which is most of the Forge packs worth playing.
        var forge = Assert.Single(
            SteamTransportCatalog.Builds,
            build => build.FileName == "e4steam-forge-mc1.18.2-1.20.2-v0.3.4.jar");
        Assert.Equal(3_817_117, forge.SizeBytes);
        Assert.Equal(
            "33421acbea4d904ac3cefa62ac1d4bd5f62f9e9169b81a4d16a052d2330bc6fc",
            forge.Sha256);
        var previousForge = Assert.Single(SteamTransportCatalog.SupersededBuilds,
            build => build.FileName == "e4steam-forge-mc1.18.2-1.20.2-v0.3.2.jar");
        Assert.Equal(3_735_682, previousForge.SizeBytes);
        Assert.Equal("9a0fc4e1a8c170f37e04cf8469091990d5c1f0953b334481dbb5eab9475e3e1a", previousForge.Sha256);

        // No two builds may share a cache folder, or one would overwrite another.
        var folders = SteamTransportCatalog.Builds.Select(build => build.CacheFileId).ToList();
        Assert.Equal(folders.Count, folders.Distinct().Count());
        Assert.Equal(folders.Count, SteamTransportCatalog.CacheFileIds.Count);
    }

    /// <summary>
    /// The mod is not one artifact, and reading one of them as the whole was
    /// the bug: the NeoForge build declares [1.20.2, 26.3) and that range was
    /// taken to be e4steam's, which refused Steam play to every Forge pack -
    /// which on 1.19.2 and 1.20.1 is most of the ones worth playing. Each range
    /// below is what that build's own metadata declares, not what its file name
    /// suggests.
    /// </summary>
    [Theory]
    // NeoForge, as before.
    [InlineData(PackLoaderKind.NeoForge, "1.21.1", "e4steam-neoforge-mc1.20.2-26.2-v0.3.2.jar")]
    [InlineData(PackLoaderKind.NeoForge, "1.20.2", "e4steam-neoforge-mc1.20.2-26.2-v0.3.2.jar")]
    [InlineData(PackLoaderKind.NeoForge, "26.2", "e4steam-neoforge-mc1.20.2-26.2-v0.3.2.jar")]
    [InlineData(PackLoaderKind.NeoForge, "26.3", null)]
    [InlineData(PackLoaderKind.NeoForge, "1.20.1", null)]
    // Forge, which used to be refused outright. Both versions still carry
    // packs people play.
    [InlineData(PackLoaderKind.Forge, "1.20.1", "e4steam-forge-mc1.18.2-1.20.2-v0.3.4.jar")]
    [InlineData(PackLoaderKind.Forge, "1.19.2", "e4steam-forge-mc1.18.2-1.20.2-v0.3.4.jar")]
    [InlineData(PackLoaderKind.Forge, "1.18.2", "e4steam-forge-mc1.18.2-1.20.2-v0.3.4.jar")]
    // The file is called forge-mc1.18.2-1.20.2 and declares up to 1.20.3.
    [InlineData(PackLoaderKind.Forge, "1.20.2", "e4steam-forge-mc1.18.2-1.20.2-v0.3.4.jar")]
    [InlineData(PackLoaderKind.Forge, "1.18.1", "e4steam-forge-mc1.17.1-1.18.1-v0.3.2.jar")]
    [InlineData(PackLoaderKind.Forge, "1.17", null)]
    [InlineData(PackLoaderKind.Forge, "1.21.1", null)]
    // Fabric and Quilt share the one build its author tested them both with.
    [InlineData(PackLoaderKind.Fabric, "1.20.1", "e4steam-fabric-quilt-mc1.19-1.21.11-v0.3.2.jar")]
    [InlineData(PackLoaderKind.Quilt, "1.21.1", "e4steam-fabric-quilt-mc1.19-1.21.11-v0.3.2.jar")]
    [InlineData(PackLoaderKind.Fabric, "1.18.2", "e4steam-fabric-quilt-mc1.17-1.18.2-v0.3.2.jar")]
    [InlineData(PackLoaderKind.Quilt, "26.2", "e4steam-fabric-quilt-mc26.1-26.2-v0.3.2.jar")]
    [InlineData(PackLoaderKind.Fabric, "1.16.5", "e4steam-fabric-mc1.16.x-v0.3.2.jar")]
    // The oldest of all of it, and the reason there is no shortlist any
    // more: a pack of somebody's own on 1.7 gets Steam play too.
    [InlineData(PackLoaderKind.Forge, "1.7.10", "e4steam-forge-mc1.7.x-v0.3.2.jar")]
    [InlineData(PackLoaderKind.Forge, "1.12.2", "e4steam-forge-mc1.12.x-v0.3.2.jar")]
    [InlineData(PackLoaderKind.Fabric, "1.14.4", "e4steam-fabric-mc1.14.x-v0.3.2.jar")]
    // Fabric began at 1.14, so 1.12 has no Fabric build and never will.
    [InlineData(PackLoaderKind.Fabric, "1.12.2", null)]
    [InlineData(PackLoaderKind.Forge, "1.6.4", null)]
    [InlineData(PackLoaderKind.Vanilla, "1.21.1", null)]
    public void EveryPackGetsTheBuildItsAuthorPublishedForIt(
        PackLoaderKind loader,
        string minecraftVersion,
        string? expectedFile)
    {
        var descriptor = new PackRuntimeDescriptor(
            1,
            minecraftVersion,
            new PackLoaderDescriptor(loader, "any"),
            "client.jar",
            "hash");

        Assert.Equal(expectedFile, SteamTransportCatalog.Find(descriptor)?.FileName);
        Assert.Equal(expectedFile is not null, SteamPlayPolicy.IsSupported(descriptor));
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

        var result = await service.EnsureSteamTransportModAsync(instance, null, CancellationToken.None);

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

        var first = await service.EnsureSteamTransportModAsync(instance, null, CancellationToken.None);
        // PackInstanceService.MirrorMods deletes every instance JAR the pack
        // does not carry, which is exactly what happens on the next launch.
        File.Delete(first.InstalledPath);

        var second = await service.EnsureSteamTransportModAsync(instance, null, CancellationToken.None);

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

        var result = await service.EnsureSteamTransportModAsync(instance, null, CancellationToken.None);

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
            () => service.EnsureSteamTransportModAsync(instance, null, CancellationToken.None));
        Assert.Contains(conflictingName, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SupersededBuild_IsReplacedOnlyAfterVerifiedInstall(bool corruptOld, bool corruptDownload)
    {
        using var fixture = new TemporaryRoot();
        var oldPayload = Encoding.UTF8.GetBytes("old official jar");
        var payload = Encoding.UTF8.GetBytes("new official jar");
        var old = TestComponent(oldPayload) with { FileName = "e4steam-old.jar" };
        var component = TestComponent(payload);
        using var httpClient = new HttpClient(new StubHandler(corruptDownload ? oldPayload : payload));
        var service = new ManagedComponentService(fixture.Paths, fixture.Logger, httpClient, component)
        {
            SupersededSteamBuilds = [old]
        };
        var instance = fixture.CreatePreparedInstance();
        var oldPath = Path.Combine(instance.GameDirectory, "mods", old.FileName);
        await File.WriteAllBytesAsync(oldPath, corruptOld ? payload : oldPayload);
        if (corruptOld || corruptDownload)
        {
            if (corruptOld)
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureSteamTransportModAsync(instance));
            else
                await Assert.ThrowsAsync<HttpRequestException>(() => service.EnsureSteamTransportModAsync(instance));
            Assert.True(File.Exists(oldPath));
            Assert.False(File.Exists(Path.Combine(instance.GameDirectory, "mods", component.FileName)));
        }
        else
        {
            var result = await service.EnsureSteamTransportModAsync(instance);
            Assert.False(File.Exists(oldPath));
            Assert.Equal(payload, await File.ReadAllBytesAsync(result.InstalledPath));
            var again = await service.EnsureSteamTransportModAsync(instance);
            Assert.False(again.Installed);
        }
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
                TempTree.Delete(Root);
            }
            catch (IOException)
            {
            }
        }
    }
}
