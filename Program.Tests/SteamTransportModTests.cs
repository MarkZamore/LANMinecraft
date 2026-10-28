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
        Assert.Equal("0.3.4", SteamTransportCatalog.Version);
        Assert.Equal(19, SteamTransportCatalog.Builds.Count);
        Assert.Equal(6, SteamTransportCatalog.Builds.Count(build => build.Version == SteamTransportCatalog.Version));
        Assert.Equal(13, SteamTransportCatalog.Builds.Count(build => build.Version == "0.3.2"));
        Assert.All(SteamTransportCatalog.Builds, build =>
        {
            Assert.Equal(64, build.Sha256.Length);
            Assert.True(build.SizeBytes > 0);
            Assert.NotEmpty(build.Loaders);
            Assert.Equal(
                $"https://github.com/MarkZamore/e4steam/releases/download/v{build.Version}/{build.FileName}",
                Assert.Single(build.DownloadUris).AbsoluteUri);
            Assert.Equal(
                $"https://github.com/Kamilhik/e4steam/releases/download/v{build.Version}/{build.FileName}",
                build.UpstreamDownloadUri.AbsoluteUri);
        });

        (string File, long Size, string Sha, int[] Minimum, int[] Maximum)[] latest =
        [
            ("e4steam-forge-mc1.17.1-1.18.1-v0.3.4.jar", 3_820_487,
                "b1a6b34ca34edff830414a676de33a7ebe6fd3a43d0f18e687b717d45d87a312", [1, 17, 1], [1, 18, 2]),
            ("e4steam-forge-mc1.18.2-1.20.2-v0.3.4.jar", 3_817_117,
                "33421acbea4d904ac3cefa62ac1d4bd5f62f9e9169b81a4d16a052d2330bc6fc", [1, 18, 2], [1, 20, 3]),
            ("e4steam-neoforge-mc1.20.2-26.3-v0.3.4.jar", 3_815_800,
                "b7c388393b7c27d188c17981073a2471c7ac52b95d5cff8b8dc8f19946d67be0", [1, 20, 2], [26, 4]),
            ("e4steam-fabric-quilt-mc1.17-1.18.2-v0.3.4.jar", 3_816_090,
                "0aaef192271c57c9af3be4a8b09e62101592953bb7c97c276df4b26c289aeef3", [1, 17], [1, 19]),
            ("e4steam-fabric-quilt-mc1.19-1.21.11-v0.3.4.jar", 3_812_904,
                "ec692ae255704971b24bdff28724ec48224d6c87c7e5d568e4252b4df15a447d", [1, 19], [26, 1]),
            ("e4steam-fabric-quilt-mc26.1-26.3-v0.3.4.jar", 3_813_546,
                "ea7ed37c3c2b8b41c7e458043b051725c22e81b9f9fa318a1474831cc484854e", [26, 1], [26, 4])
        ];
        foreach (var expected in latest)
        {
            var build = Assert.Single(SteamTransportCatalog.Builds, build => build.FileName == expected.File);
            Assert.Equal(SteamTransportCatalog.Version, build.Version);
            Assert.Equal(expected.Size, build.SizeBytes);
            Assert.Equal(expected.Sha, build.Sha256);
            Assert.Equal(expected.Minimum, build.MinimumMinecraftVersion);
            Assert.Equal(expected.Maximum, build.ExclusiveMaximumMinecraftVersion);
        }

        // The author publishes no 0.3.4 retro JARs. Keep their newest actual
        // artifacts, with explicit per-build versions and original ranges.
        Assert.All(SteamTransportCatalog.Builds.Where(build => build.Version == "0.3.2"), build =>
        {
            Assert.Contains("-v0.3.2.jar", build.FileName, StringComparison.Ordinal);
            Assert.Equal(1, build.MinimumMinecraftVersion[0]);
            Assert.InRange(build.MinimumMinecraftVersion[1], 7, 16);
        });
        Assert.Equal(38, SteamTransportCatalog.SupersededBuilds.Count);
        Assert.Equal(19, SteamTransportCatalog.SupersededBuilds.Count(build => build.FileName.EndsWith("-v0.3.0.jar", StringComparison.Ordinal)));
        Assert.Equal(19, SteamTransportCatalog.SupersededBuilds.Count(build => build.FileName.EndsWith("-v0.3.2.jar", StringComparison.Ordinal)));
        Assert.Equal(38, SteamTransportCatalog.SupersededBuilds.Select(build => build.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var build in SteamTransportCatalog.Builds.Where(build => build.Version == "0.3.2"))
        {
            var previous = Assert.Single(SteamTransportCatalog.SupersededBuilds, old => old.FileName == build.FileName);
            Assert.Equal(build.SizeBytes, previous.SizeBytes);
            Assert.Equal(build.Sha256, previous.Sha256);
        }

        // No two builds may share a cache folder, or one would overwrite another.
        var folders = SteamTransportCatalog.Builds.Select(build => build.CacheFileId).ToList();
        Assert.Equal(folders.Count, folders.Distinct().Count());
        Assert.Equal(folders.Count, SteamTransportCatalog.CacheFileIds.Count);
    }

    /// <summary>
    /// The mod is not one artifact, and reading one of them as the whole was
    /// the bug: the older NeoForge build declared [1.20.2, 26.3) and that range was
    /// taken to be e4steam's, which refused Steam play to every Forge pack -
    /// which on 1.19.2 and 1.20.1 is most of the ones worth playing. Each range
    /// below is what that build's own metadata declares, not what its file name
    /// suggests.
    /// </summary>
    [Theory]
    // NeoForge, as before.
    [InlineData(PackLoaderKind.NeoForge, "1.21.1", "e4steam-neoforge-mc1.20.2-26.3-v0.3.4.jar")]
    [InlineData(PackLoaderKind.NeoForge, "1.20.2", "e4steam-neoforge-mc1.20.2-26.3-v0.3.4.jar")]
    [InlineData(PackLoaderKind.NeoForge, "26.2", "e4steam-neoforge-mc1.20.2-26.3-v0.3.4.jar")]
    [InlineData(PackLoaderKind.NeoForge, "26.3", "e4steam-neoforge-mc1.20.2-26.3-v0.3.4.jar")]
    [InlineData(PackLoaderKind.NeoForge, "26.4", null)]
    [InlineData(PackLoaderKind.NeoForge, "1.20.1", null)]
    // Forge, which used to be refused outright. Both versions still carry
    // packs people play.
    [InlineData(PackLoaderKind.Forge, "1.20.1", "e4steam-forge-mc1.18.2-1.20.2-v0.3.4.jar")]
    [InlineData(PackLoaderKind.Forge, "1.19.2", "e4steam-forge-mc1.18.2-1.20.2-v0.3.4.jar")]
    [InlineData(PackLoaderKind.Forge, "1.18.2", "e4steam-forge-mc1.18.2-1.20.2-v0.3.4.jar")]
    // The file is called forge-mc1.18.2-1.20.2 and declares up to 1.20.3.
    [InlineData(PackLoaderKind.Forge, "1.20.2", "e4steam-forge-mc1.18.2-1.20.2-v0.3.4.jar")]
    [InlineData(PackLoaderKind.Forge, "1.18.1", "e4steam-forge-mc1.17.1-1.18.1-v0.3.4.jar")]
    [InlineData(PackLoaderKind.Forge, "1.17", null)]
    [InlineData(PackLoaderKind.Forge, "1.21.1", null)]
    // Fabric and Quilt share the one build its author tested them both with.
    [InlineData(PackLoaderKind.Fabric, "1.20.1", "e4steam-fabric-quilt-mc1.19-1.21.11-v0.3.4.jar")]
    [InlineData(PackLoaderKind.Quilt, "1.21.1", "e4steam-fabric-quilt-mc1.19-1.21.11-v0.3.4.jar")]
    [InlineData(PackLoaderKind.Fabric, "1.18.2", "e4steam-fabric-quilt-mc1.17-1.18.2-v0.3.4.jar")]
    [InlineData(PackLoaderKind.Quilt, "26.2", "e4steam-fabric-quilt-mc26.1-26.3-v0.3.4.jar")]
    [InlineData(PackLoaderKind.Fabric, "26.3", "e4steam-fabric-quilt-mc26.1-26.3-v0.3.4.jar")]
    [InlineData(PackLoaderKind.Quilt, "26.3", "e4steam-fabric-quilt-mc26.1-26.3-v0.3.4.jar")]
    [InlineData(PackLoaderKind.Fabric, "26.4", null)]
    [InlineData(PackLoaderKind.Quilt, "26.4", null)]
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

        var selected = SteamTransportCatalog.Find(descriptor);
        Assert.Equal(expectedFile, selected?.FileName);
        if (expectedFile is not null)
            Assert.Equal(expectedFile.Contains("-v0.3.4.jar", StringComparison.Ordinal)
                ? SteamTransportCatalog.Version : "0.3.2", selected!.Version);
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
    public async Task DifferentPacks_ReuseOneVerifiedTransportDownloadAndSharedCache()
    {
        using var fixture = new TemporaryRoot();
        var payload = Encoding.UTF8.GetBytes("one published transport artifact");
        var component = TestComponent(payload);
        var handler = new StubHandler(payload);
        using var httpClient = new HttpClient(handler);
        var service = fixture.CreateService(httpClient, component);

        var first = await service.EnsureSteamTransportModAsync(fixture.CreatePreparedInstance("FirstPack"));
        var second = await service.EnsureSteamTransportModAsync(fixture.CreatePreparedInstance("SecondPack"));

        Assert.True(first.Downloaded);
        Assert.False(second.Downloaded);
        Assert.Equal(first.CachePath, second.CachePath);
        Assert.NotEqual(first.InstalledPath, second.InstalledPath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(first.InstalledPath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(second.InstalledPath));
        Assert.Equal(component.DownloadUris.Single(), Assert.Single(handler.RequestUris));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AncestorJunction_CannotRedirectManagedCacheOrInstanceWrites(bool redirectInstance)
    {
        using var fixture = new TemporaryRoot();
        using var external = new TemporaryRoot();
        var payload = Encoding.UTF8.GetBytes("verified transport bytes");
        var component = TestComponent(payload);
        var handler = new StubHandler(payload);
        using var httpClient = new HttpClient(handler);
        var old = component with { FileName = "e4steam-previous.jar", FileId = component.FileId + 1 };
        var service = new ManagedComponentService(fixture.Paths, fixture.Logger, httpClient, component)
        {
            SupersededSteamBuilds = [old]
        };
        var instance = fixture.CreatePreparedInstance();
        string junction;
        string externalDestination;
        if (redirectInstance)
        {
            junction = fixture.Paths.Instances;
            externalDestination = Path.Combine(external.Root, "TestPack", "mods");
            Directory.CreateDirectory(externalDestination);
            // Only these three empty fixture directories are removed.
            Directory.Delete(Path.Combine(instance.GameDirectory, "mods"));
            Directory.Delete(instance.GameDirectory);
            Directory.Delete(junction);
        }
        else
        {
            junction = Path.Combine(fixture.Paths.Launcher, "ManagedComponents");
            externalDestination = Path.Combine(external.Root, component.Id,
                component.FileId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(externalDestination);
        }
        var externalJar = Path.Combine(externalDestination, component.FileName);
        await File.WriteAllTextAsync(externalJar, "irreplaceable external file");
        var externalOld = Path.Combine(externalDestination, old.FileName);
        await File.WriteAllBytesAsync(externalOld, payload);
        var before = Directory.EnumerateFiles(external.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(external.Root, path), File.ReadAllBytes);
        SavesFolderService.CreateJunction(junction, external.Root);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => service.EnsureSteamTransportModAsync(instance));
            Assert.Empty(handler.RequestUris);
            var after = Directory.EnumerateFiles(external.Root, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(external.Root, path), File.ReadAllBytes);
            Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
            foreach (var (relative, bytes) in before) Assert.Equal(bytes, after[relative]);
        }
        finally
        {
            // Unlink the fixture junction before either independent root is cleaned.
            if (new DirectoryInfo(junction).LinkTarget is not null) Directory.Delete(junction);
        }
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
            [new Uri("https://github.com/MarkZamore/e4steam/releases/download/v0.3.4/e4steam-test.jar")],
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

        public PackInstanceContext CreatePreparedInstance(string packName = "TestPack")
        {
            var gameDirectory = Paths.CombineUnderInstances(packName);
            var packDirectory = Paths.CombineUnderPacks(packName);
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
