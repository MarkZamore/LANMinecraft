using System.Net;
using System.Security.Cryptography;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

public sealed class ManagedComponentServiceTests
{
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

        var result = await service.EnsureSteamTransportModAsync(instance);

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
        Assert.Equal(component.DownloadUris[0], Assert.Single(handler.RequestUris));
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
        Directory.CreateDirectory(Path.GetDirectoryName(service.E4steamCachePath)!);
        await File.WriteAllBytesAsync(service.E4steamCachePath, payload);
        var arbitraryMod = Path.Combine(instance.GameDirectory, "mods", "example-mod.jar");
        await File.WriteAllTextAsync(arbitraryMod, "unchanged");

        var result = await service.EnsureSteamTransportModAsync(instance);

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

        var result = await service.EnsureSteamTransportModAsync(instance);

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
        Directory.CreateDirectory(Path.GetDirectoryName(service.E4steamCachePath)!);
        await File.WriteAllBytesAsync(service.E4steamCachePath, rejected);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.EnsureSteamTransportModAsync(instance));

        Assert.Equal(component.DownloadUris[0], Assert.Single(handler.RequestUris));
        Assert.Equal(rejected, await File.ReadAllBytesAsync(installedPath));
        Assert.Equal(rejected, await File.ReadAllBytesAsync(service.E4steamCachePath));
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
        Directory.CreateDirectory(Path.GetDirectoryName(service.E4steamCachePath)!);
        await File.WriteAllBytesAsync(service.E4steamCachePath, corrupt);

        var result = await service.EnsureSteamTransportModAsync(instance);

        Assert.True(result.Downloaded);
        Assert.True(result.Installed);
        Assert.True(result.CachePopulated);
        Assert.Equal(component.DownloadUris[0], Assert.Single(handler.RequestUris));
        Assert.Equal(expected, await File.ReadAllBytesAsync(installedPath));
        Assert.Equal(expected, await File.ReadAllBytesAsync(service.E4steamCachePath));
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
            service.EnsureSteamTransportModAsync(instance),
            service.EnsureSteamTransportModAsync(instance));

        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task PrimaryForbidden_FallsBackToNextOfficialSource()
    {
        using var fixture = new TemporaryPortableRoot();
        var payload = Encoding.UTF8.GetBytes("fallback managed component");
        var primary = new Uri("https://primary.example.test/e4steam.jar");
        var fallback = new Uri("https://fallback.example.test/e4steam.jar");
        var component = TestComponent(payload, primary, fallback);
        var handler = new RecordingHandler((request, _) =>
            Task.FromResult(
                request.RequestUri == primary
                    ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                    : Success(payload)));
        using var httpClient = new HttpClient(handler);
        var service = fixture.CreateService(httpClient, component);
        var instance = fixture.CreatePreparedInstance();

        var result = await service.EnsureSteamTransportModAsync(instance);

        Assert.True(result.Downloaded);
        Assert.True(result.Installed);
        Assert.Equal([primary, fallback], handler.RequestUris);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.InstalledPath));
        Assert.Empty(Directory.EnumerateFiles(
            fixture.Root,
            "*.tmp",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InvalidPrimaryPayload_FallsBackWithoutPublishingIt()
    {
        using var fixture = new TemporaryPortableRoot();
        var expected = Encoding.UTF8.GetBytes("correct bytes");
        var rejected = Encoding.UTF8.GetBytes("wrong__ bytes");
        Assert.Equal(expected.Length, rejected.Length);
        var primary = new Uri("https://primary.example.test/e4steam.jar");
        var fallback = new Uri("https://fallback.example.test/e4steam.jar");
        var component = TestComponent(expected, primary, fallback);
        var handler = new RecordingHandler((request, _) =>
            Task.FromResult(Success(request.RequestUri == primary ? rejected : expected)));
        using var httpClient = new HttpClient(handler);
        var service = fixture.CreateService(httpClient, component);
        var instance = fixture.CreatePreparedInstance();

        var result = await service.EnsureSteamTransportModAsync(instance);

        Assert.Equal([primary, fallback], handler.RequestUris);
        Assert.Equal(expected, await File.ReadAllBytesAsync(result.CachePath));
        Assert.Equal(expected, await File.ReadAllBytesAsync(result.InstalledPath));
        Assert.Empty(Directory.EnumerateFiles(
            fixture.Root,
            "*.tmp",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PrimaryResponseBodyFailure_FallsBackToNextOfficialSource()
    {
        using var fixture = new TemporaryPortableRoot();
        var payload = Encoding.UTF8.GetBytes("body fallback managed component");
        var primary = new Uri("https://primary.example.test/e4steam.jar");
        var fallback = new Uri("https://fallback.example.test/e4steam.jar");
        var component = TestComponent(payload, primary, fallback);
        var handler = new RecordingHandler((request, _) =>
            Task.FromResult(
                request.RequestUri == primary
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new FailingHttpResponseStream())
                    }
                    : Success(payload)));
        using var httpClient = new HttpClient(handler);
        var service = fixture.CreateService(httpClient, component);
        var instance = fixture.CreatePreparedInstance();

        var result = await service.EnsureSteamTransportModAsync(instance);

        Assert.Equal([primary, fallback], handler.RequestUris);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.CachePath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.InstalledPath));
        Assert.Empty(Directory.EnumerateFiles(
            fixture.Root,
            "*.tmp",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AllOfficialSourcesFail_FailsClosedAndReportsEachStatus()
    {
        using var fixture = new TemporaryPortableRoot();
        var payload = Encoding.UTF8.GetBytes("component");
        var primary = new Uri(
            "https://primary.example.test/e4steam.jar?temporary-token=secret");
        var fallback = new Uri("https://fallback.example.test/e4steam.jar");
        var component = TestComponent(payload, primary, fallback);
        var handler = new RecordingHandler((request, _) =>
            Task.FromResult(new HttpResponseMessage(
                request.RequestUri == primary
                    ? HttpStatusCode.Forbidden
                    : HttpStatusCode.ServiceUnavailable)));
        using var httpClient = new HttpClient(handler);
        var service = fixture.CreateService(httpClient, component);
        var instance = fixture.CreatePreparedInstance();

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.EnsureSteamTransportModAsync(instance));

        Assert.Equal([primary, fallback], handler.RequestUris);
        Assert.Contains("HTTP 403", error.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP 503", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("?", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(service.E4steamCachePath));
        Assert.Empty(Directory.EnumerateFiles(
            fixture.Root,
            "*.tmp",
            SearchOption.AllDirectories));
    }

    [Fact]
    public void ComponentSourceWithCredentials_IsRejected()
    {
        using var fixture = new TemporaryPortableRoot();
        var payload = Encoding.UTF8.GetBytes("component");
        var component = TestComponent(
            payload,
            new Uri(
                "https://user:password@example.test/e4steam.jar?temporary-token=secret"));
        using var httpClient = new HttpClient(new RecordingHandler((_, _) =>
            Task.FromResult(Success(payload))));

        var error = Assert.Throws<ArgumentException>(
            () => fixture.CreateService(httpClient, component));

        Assert.DoesNotContain("password", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConflictingJarOfTheSameMod_BlocksAndDoesNotDeleteAnything()
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
            "e4steam-neoforge-another-version.jar");
        await File.WriteAllTextAsync(conflict, "foreign file");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnsureSteamTransportModAsync(instance));

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
            () => service.EnsureSteamTransportModAsync(instance));

        Assert.Empty(handler.RequestUris);
    }

    private static ManagedComponentDescriptor TestComponent(
        byte[] payload,
        params Uri[] downloadUris) =>
        new(
            "e4steam-test",
            123456,
            "e4steam-neoforge-test.jar",
            downloadUris.Length == 0
                ? [new Uri("https://example.test/e4steam.jar")]
                : downloadUris,
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

    private sealed class FailingHttpResponseStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw CreateFailure();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(CreateFailure());

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        private static HttpIOException CreateFailure() =>
            new(HttpRequestError.ResponseEnded, "Simulated response body failure.");
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
