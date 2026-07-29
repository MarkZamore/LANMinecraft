using System.Net;
using System.Security.Cryptography;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

public sealed class ManagedComponentServiceTests
{
    [Fact]
    public void PinnedFtbEssentialsArtifact_MatchesCurseForgeFile7608733()
    {
        Assert.Equal(410811, ManagedComponentService.FtbEssentialsCurseForgeProjectId);
        Assert.Equal(7608733, ManagedComponentService.FtbEssentialsCurseForgeFileId);
        Assert.Equal("2101.1.9", ManagedComponentService.FtbEssentialsVersion);
        Assert.Equal(
            "ftb-essentials-neoforge-2101.1.9.jar",
            ManagedComponentService.FtbEssentialsFileName);
        Assert.Equal(209459, ManagedComponentService.FtbEssentialsSizeBytes);
        Assert.Equal(
            "4da8e4d461ceef1a5e6f6705265fe24239b132cdc408eb77787231cb56c219bf",
            ManagedComponentService.FtbEssentialsSha256);
        Assert.Equal(
            "https://www.curseforge.com/api/v1/mods/410811/files/7608733/download",
            ManagedComponentService.FtbEssentialsDownloadUri.AbsoluteUri);
    }

    [Fact]
    public async Task MissingComponent_DownloadsVerifiesCachesAndInstallsAtomically()
    {
        using var fixture = new TemporaryPortableRoot();
        var payload = Encoding.UTF8.GetBytes("verified managed component");
        var component = TestComponent(payload);
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(Success(payload)));
        using var httpClient = new HttpClient(handler);
        var service = fixture.CreateService(httpClient, component);
        var instance = fixture.CreatePreparedInstance();
        File.WriteAllText(
            Path.Combine(instance.GameDirectory, "mods", "unrelated.jar"),
            "leave me");

        var result = await service.EnsureFtbEssentialsAsync(instance);

        Assert.True(result.Downloaded);
        Assert.True(result.Installed);
        Assert.True(result.CachePopulated);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.CachePath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.InstalledPath));
        Assert.Equal("leave me", File.ReadAllText(
            Path.Combine(instance.GameDirectory, "mods", "unrelated.jar")));
        Assert.Contains(
            Path.Combine("Launcher", "ManagedComponents"),
            result.CachePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(component.DownloadUri, Assert.Single(handler.RequestUris));
        Assert.Empty(Directory.EnumerateFiles(
            fixture.Root,
            "*.tmp",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task VerifiedCache_SkipsNetworkAndPreservesArbitraryMods()
    {
        using var fixture = new TemporaryPortableRoot();
        var payload = Encoding.UTF8.GetBytes("cached managed component");
        var component = TestComponent(payload);
        var handler = new RecordingHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(
                new InvalidOperationException("Network must not be used.")));
        using var httpClient = new HttpClient(handler);
        var service = fixture.CreateService(httpClient, component);
        var instance = fixture.CreatePreparedInstance();
        Directory.CreateDirectory(Path.GetDirectoryName(service.FtbEssentialsCachePath)!);
        await File.WriteAllBytesAsync(service.FtbEssentialsCachePath, payload);
        var arbitraryMod = Path.Combine(instance.GameDirectory, "mods", "example-mod.jar");
        await File.WriteAllTextAsync(arbitraryMod, "unchanged");

        var result = await service.EnsureFtbEssentialsAsync(instance);

        Assert.False(result.Downloaded);
        Assert.True(result.Installed);
        Assert.False(result.CachePopulated);
        Assert.Empty(handler.RequestUris);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.InstalledPath));
        Assert.Equal("unchanged", await File.ReadAllTextAsync(arbitraryMod));
    }

    [Fact]
    public async Task VerifiedInstalledFile_RecoversCacheWithoutNetwork()
    {
        using var fixture = new TemporaryPortableRoot();
        var payload = Encoding.UTF8.GetBytes("installed managed component");
        var component = TestComponent(payload);
        var handler = new RecordingHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(
                new InvalidOperationException("Network must not be used.")));
        using var httpClient = new HttpClient(handler);
        var service = fixture.CreateService(httpClient, component);
        var instance = fixture.CreatePreparedInstance();
        var installedPath = Path.Combine(
            instance.GameDirectory,
            "mods",
            component.FileName);
        await File.WriteAllBytesAsync(installedPath, payload);

        var result = await service.EnsureFtbEssentialsAsync(instance);

        Assert.False(result.Downloaded);
        Assert.False(result.Installed);
        Assert.True(result.CachePopulated);
        Assert.Empty(handler.RequestUris);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.CachePath));
    }

    [Fact]
    public async Task InvalidDownload_FailsClosedAndPreservesExistingFiles()
    {
        using var fixture = new TemporaryPortableRoot();
        var expected = Encoding.UTF8.GetBytes("correct bytes");
        var rejected = Encoding.UTF8.GetBytes("wrong__ bytes");
        Assert.Equal(expected.Length, rejected.Length);
        var component = TestComponent(expected);
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(Success(rejected)));
        using var httpClient = new HttpClient(handler);
        var service = fixture.CreateService(httpClient, component);
        var instance = fixture.CreatePreparedInstance();
        var installedPath = Path.Combine(
            instance.GameDirectory,
            "mods",
            component.FileName);
        await File.WriteAllBytesAsync(installedPath, rejected);
        Directory.CreateDirectory(Path.GetDirectoryName(service.FtbEssentialsCachePath)!);
        await File.WriteAllBytesAsync(service.FtbEssentialsCachePath, rejected);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.EnsureFtbEssentialsAsync(instance));

        Assert.Equal(component.DownloadUri, Assert.Single(handler.RequestUris));
        Assert.Equal(rejected, await File.ReadAllBytesAsync(installedPath));
        Assert.Equal(rejected, await File.ReadAllBytesAsync(service.FtbEssentialsCachePath));
        Assert.Empty(Directory.EnumerateFiles(
            fixture.Root,
            "*.tmp",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CorruptInstalledAndCacheFiles_AreRedownloadedAndRepaired()
    {
        using var fixture = new TemporaryPortableRoot();
        var expected = Encoding.UTF8.GetBytes("correct bytes");
        var corrupt = Encoding.UTF8.GetBytes("wrong__ bytes");
        Assert.Equal(expected.Length, corrupt.Length);
        var component = TestComponent(expected);
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(Success(expected)));
        using var httpClient = new HttpClient(handler);
        var service = fixture.CreateService(httpClient, component);
        var instance = fixture.CreatePreparedInstance();
        var installedPath = Path.Combine(
            instance.GameDirectory,
            "mods",
            component.FileName);
        await File.WriteAllBytesAsync(installedPath, corrupt);
        Directory.CreateDirectory(Path.GetDirectoryName(service.FtbEssentialsCachePath)!);
        await File.WriteAllBytesAsync(service.FtbEssentialsCachePath, corrupt);

        var result = await service.EnsureFtbEssentialsAsync(instance);

        Assert.True(result.Downloaded);
        Assert.True(result.Installed);
        Assert.True(result.CachePopulated);
        Assert.Equal(component.DownloadUri, Assert.Single(handler.RequestUris));
        Assert.Equal(expected, await File.ReadAllBytesAsync(installedPath));
        Assert.Equal(expected, await File.ReadAllBytesAsync(service.FtbEssentialsCachePath));
        Assert.Empty(Directory.EnumerateFiles(
            fixture.Root,
            "*.tmp",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ConcurrentPreparation_DownloadsPinnedArtifactOnlyOnce()
    {
        using var fixture = new TemporaryPortableRoot();
        var payload = Encoding.UTF8.GetBytes("one shared download");
        var component = TestComponent(payload);
        var handler = new RecordingHandler(async (_, token) =>
        {
            await Task.Delay(30, token);
            return Success(payload);
        });
        using var httpClient = new HttpClient(handler);
        var service = fixture.CreateService(httpClient, component);
        var instance = fixture.CreatePreparedInstance();

        await Task.WhenAll(
            service.EnsureFtbEssentialsAsync(instance),
            service.EnsureFtbEssentialsAsync(instance));

        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task ConflictingFtbJar_BlocksAndDoesNotDeleteAnything()
    {
        using var fixture = new TemporaryPortableRoot();
        var payload = Encoding.UTF8.GetBytes("component");
        var component = TestComponent(payload);
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(Success(payload)));
        using var httpClient = new HttpClient(handler);
        var service = fixture.CreateService(httpClient, component);
        var instance = fixture.CreatePreparedInstance();
        var conflict = Path.Combine(
            instance.GameDirectory,
            "mods",
            "ftb-essentials-neoforge-another-version.jar");
        await File.WriteAllTextAsync(conflict, "foreign file");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnsureFtbEssentialsAsync(instance));

        Assert.Equal("foreign file", await File.ReadAllTextAsync(conflict));
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task TargetOutsidePreparedInstances_IsRejectedBeforeNetwork()
    {
        using var fixture = new TemporaryPortableRoot();
        var payload = Encoding.UTF8.GetBytes("component");
        var component = TestComponent(payload);
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(Success(payload)));
        using var httpClient = new HttpClient(handler);
        var service = fixture.CreateService(httpClient, component);
        var outside = Path.Combine(fixture.Paths.Personal, "NotAnInstance");
        Directory.CreateDirectory(outside);
        var instance = new PackInstanceContext(
            fixture.Paths.Packs,
            outside,
            Path.Combine(outside, "client.jar"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnsureFtbEssentialsAsync(instance));

        Assert.Empty(handler.RequestUris);
    }

    private static ManagedComponentDescriptor TestComponent(byte[] payload) =>
        new(
            "ftb-essentials-test",
            123456,
            "ftb-essentials-neoforge-test.jar",
            new Uri("https://example.test/ftb-essentials.jar"),
            payload.LongLength,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());

    private static HttpResponseMessage Success(byte[] payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
            _responseFactory = responseFactory;

        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri
                ?? throw new InvalidOperationException("Request URI is missing."));
            return _responseFactory(request, cancellationToken);
        }
    }

    private sealed class TemporaryPortableRoot : IDisposable
    {
        public TemporaryPortableRoot()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "MinecraftManagedComponentTests",
                Guid.NewGuid().ToString("N"));
            Paths = new AppPaths(Root);
            Paths.Ensure();
            Logger = new Logger(Path.Combine(Paths.Personal, "managed-components.log"));
        }

        public string Root { get; }
        public AppPaths Paths { get; }
        public Logger Logger { get; }

        public ManagedComponentService CreateService(
            HttpClient httpClient,
            ManagedComponentDescriptor component) =>
            new(Paths, Logger, httpClient, component);

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
            catch
            {
            }
        }
    }
}
