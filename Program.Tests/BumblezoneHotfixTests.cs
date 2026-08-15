using System.Net;
using System.Security.Cryptography;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

// The Infinity pack ships Bumblezone 7.13.2, whose chunk-packet mixin crashes
// the server in its dimension alongside ModernFix and Smooth Chunk. The
// launcher swaps in the pinned fixed build after every instance preparation.
public sealed class BumblezoneHotfixTests : IDisposable
{
    private const string SupersededName = "the_bumblezone-0.0.1-test.jar";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MinecraftBumblezoneHotfixTests",
        Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly Logger _logger;

    public BumblezoneHotfixTests()
    {
        _paths = new AppPaths(_root);
        _paths.Ensure();
        _logger = new Logger(Path.Combine(_paths.Personal, "bumblezone-hotfix.log"));
    }

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
    public void PinnedBumblezoneArtifact_MatchesModrinthVersionJHtMMEiy()
    {
        Assert.Equal("7.15.3", ManagedComponentService.BumblezoneVersion);
        Assert.Equal(
            "the_bumblezone-7.15.3+1.21.1-neoforge.jar",
            ManagedComponentService.BumblezoneFileName);
        Assert.Equal(
            "the_bumblezone-7.13.2+1.21.1-neoforge.jar",
            ManagedComponentService.BumblezoneSupersededFileName);
        Assert.Equal(69_672_269, ManagedComponentService.BumblezoneSizeBytes);
        Assert.Equal(
            "049b688a43886a3a5f70e8c0f195db7e33335e20adc1124c981a377a8b511101",
            ManagedComponentService.BumblezoneSha256);
        Assert.Equal(
            "https://cdn.modrinth.com/data/38tpSycf/versions/jHtMMEiy/the_bumblezone-7.15.3%2B1.21.1-neoforge.jar",
            ManagedComponentService.BumblezoneDownloadUri.AbsoluteUri);
    }

    [Fact]
    public async Task SupersededJar_IsReplacedByVerifiedPinnedBuild()
    {
        var payload = Encoding.UTF8.GetBytes("fixed bumblezone build");
        var handler = new RecordingHandler((_, _) => Task.FromResult(Success(payload)));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, payload);
        var instance = CreateInstance();
        var supersededPath = Path.Combine(instance.GameDirectory, "mods", SupersededName);
        await File.WriteAllTextAsync(supersededPath, "broken build");
        var unrelated = Path.Combine(instance.GameDirectory, "mods", "unrelated.jar");
        await File.WriteAllTextAsync(unrelated, "leave me");

        var result = await service.EnsureBumblezoneHotfixAsync(instance);

        Assert.True(result.Downloaded);
        Assert.True(result.Installed);
        Assert.False(File.Exists(supersededPath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.InstalledPath));
        Assert.Equal("leave me", await File.ReadAllTextAsync(unrelated));
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task SecondLaunch_ReinstallsFromCacheWithoutNetwork()
    {
        var payload = Encoding.UTF8.GetBytes("cached bumblezone build");
        var downloadsAllowed = 1;
        var handler = new RecordingHandler((_, _) =>
            Interlocked.Decrement(ref downloadsAllowed) >= 0
                ? Task.FromResult(Success(payload))
                : Task.FromException<HttpResponseMessage>(
                    new InvalidOperationException("Network must not be used twice.")));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, payload);
        var instance = CreateInstance();
        var supersededPath = Path.Combine(instance.GameDirectory, "mods", SupersededName);
        await File.WriteAllTextAsync(supersededPath, "broken build");
        await service.EnsureBumblezoneHotfixAsync(instance);

        // Instance preparation mirrors the pack again: the pinned jar is gone
        // and the broken one is back, exactly like the next real launch.
        File.Delete(Path.Combine(instance.GameDirectory, "mods", service.PinnedFileName));
        await File.WriteAllTextAsync(supersededPath, "broken build");

        var result = await service.EnsureBumblezoneHotfixAsync(instance);

        Assert.False(result.Downloaded);
        Assert.True(result.Installed);
        Assert.False(File.Exists(supersededPath));
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task InstanceWithoutBumblezone_IsLeftAloneWithoutNetwork()
    {
        var payload = Encoding.UTF8.GetBytes("never used");
        var handler = new RecordingHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(
                new InvalidOperationException("Network must not be used.")));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, payload);
        var instance = CreateInstance();

        var result = await service.EnsureBumblezoneHotfixAsync(instance);

        Assert.False(result.Downloaded);
        Assert.False(result.Installed);
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task ManuallyUpdatedBumblezone_IsRespectedWithoutNetwork()
    {
        var payload = Encoding.UTF8.GetBytes("never used");
        var handler = new RecordingHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(
                new InvalidOperationException("Network must not be used.")));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, payload);
        var instance = CreateInstance();
        var manual = Path.Combine(
            instance.GameDirectory, "mods", "the_bumblezone-9.9.9-test.jar");
        await File.WriteAllTextAsync(manual, "newer than the pin");
        var superseded = Path.Combine(instance.GameDirectory, "mods", SupersededName);
        await File.WriteAllTextAsync(superseded, "broken build");

        var result = await service.EnsureBumblezoneHotfixAsync(instance);

        // Installing the pin next to a manual update would duplicate the mod,
        // so the instance must stay untouched.
        Assert.False(result.Downloaded);
        Assert.False(result.Installed);
        Assert.True(File.Exists(manual));
        Assert.True(File.Exists(superseded));
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task FailedDownload_FailsOpenWithTheUntouchedOldJar()
    {
        var expected = Encoding.UTF8.GetBytes("correct bytes");
        var wrong = Encoding.UTF8.GetBytes("wrong__ bytes");
        var handler = new RecordingHandler((_, _) => Task.FromResult(Success(wrong)));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, expected);
        var instance = CreateInstance();
        var supersededPath = Path.Combine(instance.GameDirectory, "mods", SupersededName);
        await File.WriteAllTextAsync(supersededPath, "broken build");

        // The instance is still in its shipped state, so a failed or corrupt
        // download must not block the launch - the old jar plays this session.
        var result = await service.EnsureBumblezoneHotfixAsync(instance);

        Assert.False(result.Installed);
        Assert.True(File.Exists(supersededPath));
        Assert.False(File.Exists(
            Path.Combine(instance.GameDirectory, "mods", service.PinnedFileName)));
    }

    [Fact]
    public async Task BothJarsAfterACrash_ConvergeToThePinWithoutNetwork()
    {
        var payload = Encoding.UTF8.GetBytes("fixed bumblezone build");
        var handler = new RecordingHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(
                new InvalidOperationException("Network must not be used.")));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, payload);
        var instance = CreateInstance();
        var supersededPath = Path.Combine(instance.GameDirectory, "mods", SupersededName);
        await File.WriteAllTextAsync(supersededPath, "broken build");
        var installedPath = Path.Combine(
            instance.GameDirectory, "mods", service.PinnedFileName);
        await File.WriteAllBytesAsync(installedPath, payload);

        var result = await service.EnsureBumblezoneHotfixAsync(instance);

        Assert.False(File.Exists(supersededPath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(installedPath));
        Assert.True(result.CachePopulated);
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task CorruptInstalledPin_IsRepairedFromCacheWithoutNetwork()
    {
        var payload = Encoding.UTF8.GetBytes("fixed bumblezone build");
        var handler = new RecordingHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(
                new InvalidOperationException("Network must not be used.")));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, payload);
        var instance = CreateInstance();
        var installedPath = Path.Combine(
            instance.GameDirectory, "mods", service.PinnedFileName);
        await File.WriteAllTextAsync(installedPath, "corrupt pin");
        Directory.CreateDirectory(Path.GetDirectoryName(service.CachePath)!);
        await File.WriteAllBytesAsync(service.CachePath, payload);

        var result = await service.EnsureBumblezoneHotfixAsync(instance);

        Assert.True(result.Installed);
        Assert.False(result.Downloaded);
        Assert.Equal(payload, await File.ReadAllBytesAsync(installedPath));
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task LockedSupersededJar_FailsClosedAndRollsBackThePin()
    {
        var payload = Encoding.UTF8.GetBytes("fixed bumblezone build");
        var handler = new RecordingHandler((_, _) => Task.FromResult(Success(payload)));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, payload);
        var instance = CreateInstance();
        var supersededPath = Path.Combine(instance.GameDirectory, "mods", SupersededName);
        await File.WriteAllTextAsync(supersededPath, "broken build");

        // Two Bumblezone builds side by side would fail mod loading, so a
        // superseded jar that cannot be removed must abort the launch and
        // remove the freshly installed pin.
        await using (new FileStream(
            supersededPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.EnsureBumblezoneHotfixAsync(instance));
        }

        Assert.True(File.Exists(supersededPath));
        Assert.False(File.Exists(
            Path.Combine(instance.GameDirectory, "mods", service.PinnedFileName)));
    }

    [Fact]
    public void DownloadTimeout_ScalesWithArtifactSize()
    {
        var small = new ManagedComponentDescriptor(
            "small", 1, "small.jar", [new Uri("https://example.test/small.jar")],
            209_459, new string('a', 64));
        var large = new ManagedComponentDescriptor(
            "large", 2, "large.jar", [new Uri("https://example.test/large.jar")],
            ManagedComponentService.BumblezoneSizeBytes, new string('a', 64));

        // A flat 45s bound meant the 69 MB jar needed >=12 Mbit/s or the
        // launch stayed blocked forever; the scaled bound keeps a ~1 Mbit/s
        // floor while remaining finite.
        Assert.True(ManagedComponentService.DownloadTimeoutFor(small) < TimeSpan.FromMinutes(1));
        Assert.True(ManagedComponentService.DownloadTimeoutFor(large) > TimeSpan.FromMinutes(5));
        Assert.True(ManagedComponentService.DownloadTimeoutFor(large) < TimeSpan.FromMinutes(15));
    }

    private TestService CreateService(HttpClient httpClient, byte[] payload)
    {
        var ftb = new ManagedComponentDescriptor(
            "ftb-essentials-test",
            123456,
            "ftb-essentials-neoforge-test.jar",
            [new Uri("https://example.test/ftb-essentials.jar")],
            1,
            Convert.ToHexString(SHA256.HashData(new byte[] { 1 })).ToLowerInvariant());
        var bumblezone = new ManagedComponentDescriptor(
            "the-bumblezone-test",
            654321,
            "the_bumblezone-1.0.0-test.jar",
            [new Uri("https://example.test/the_bumblezone.jar")],
            payload.LongLength,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
        return new TestService(
            new ManagedComponentService(
                _paths, _logger, httpClient, ftb, bumblezone, SupersededName),
            bumblezone.FileName);
    }

    private PackInstanceContext CreateInstance()
    {
        var gameDirectory = _paths.CombineUnderInstances("TestPack");
        var packDirectory = _paths.CombineUnderPacks("TestPack");
        Directory.CreateDirectory(gameDirectory);
        Directory.CreateDirectory(packDirectory);
        Directory.CreateDirectory(Path.Combine(gameDirectory, "mods"));
        return new PackInstanceContext(
            packDirectory,
            gameDirectory,
            Path.Combine(packDirectory, "client.jar"));
    }

    private sealed record TestService(ManagedComponentService Service, string PinnedFileName)
    {
        public string CachePath => Service.BumblezoneCachePath;

        public Task<ManagedComponentInstallResult> EnsureBumblezoneHotfixAsync(
            PackInstanceContext instance) =>
            Service.EnsureBumblezoneHotfixAsync(instance);
    }

    private static HttpResponseMessage Success(byte[] payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri
                ?? throw new InvalidOperationException("Request URI is missing."));
            return responseFactory(request, cancellationToken);
        }
    }
}
