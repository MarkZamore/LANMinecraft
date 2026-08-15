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
    public void PinnedOritechArtifact_MatchesModrinthVersionCXCIlwHu()
    {
        Assert.Equal("1.2.5", ManagedComponentService.OritechVersion);
        Assert.Equal(
            "oritech-neoforge-1.21.1-1.2.5.jar",
            ManagedComponentService.OritechFileName);
        Assert.Equal(
            "oritech-neoforge-1.21.1-1.2.3.jar",
            ManagedComponentService.OritechSupersededFileName);
        Assert.Equal(10_675_443, ManagedComponentService.OritechSizeBytes);
        Assert.Equal(
            "e863aa387cec1eca46fa5ca3c3233d4d05399f73017362f23a86fd6533551873",
            ManagedComponentService.OritechSha256);
        Assert.Equal(
            "https://cdn.modrinth.com/data/4sYI62kA/versions/cXCIlwHu/oritech-neoforge-1.21.1-1.2.5.jar",
            ManagedComponentService.OritechDownloadUri.AbsoluteUri);
    }

    [Fact]
    public void DefaultReplacements_CoverBumblezoneAndOritech_AndSpareOritechThings()
    {
        var replacements = ManagedComponentService.DefaultModReplacements;
        Assert.Equal(2, replacements.Count);
        Assert.Equal(
            ["the_bumblezone-*.jar", "oritech-*.jar"],
            replacements.Select(replacement => replacement.VersionScanPattern));
        // The oritechthings addon must never match the Oritech scan pattern,
        // or its mere presence would disable the hotfix.
        Assert.False(System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
            "oritech-*.jar", "oritechthings-0.0.42.jar", ignoreCase: true));
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
    public async Task FirstReplacementFailingOpen_DoesNotSuppressTheSecond()
    {
        var bumblezonePayload = Encoding.UTF8.GetBytes("bumblezone fixed build");
        var oritechPayload = Encoding.UTF8.GetBytes("oritech fixed build");
        var handler = new RecordingHandler((request, _) =>
            request.RequestUri!.AbsolutePath.Contains("oritech", StringComparison.Ordinal)
                ? Task.FromResult(Success(oritechPayload))
                : Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("bumblezone source is down")));
        using var httpClient = new HttpClient(handler);
        var ftb = new ManagedComponentDescriptor(
            "ftb-essentials-test", 123456, "ftb-essentials-neoforge-test.jar",
            [new Uri("https://example.test/ftb-essentials.jar")],
            1, Convert.ToHexString(SHA256.HashData(new byte[] { 1 })).ToLowerInvariant());
        var bumblezone = new ManagedModReplacement(
            new ManagedComponentDescriptor(
                "the-bumblezone-test", 1111, "the_bumblezone-2.0.0-test.jar",
                [new Uri("https://example.test/the_bumblezone.jar")],
                bumblezonePayload.LongLength,
                Convert.ToHexString(SHA256.HashData(bumblezonePayload)).ToLowerInvariant()),
            "the_bumblezone-1.0.0-test.jar", "Bumblezone", "2.0.0");
        var oritech = new ManagedModReplacement(
            new ManagedComponentDescriptor(
                "oritech-test", 2222, "oritech-neoforge-2.0.0-test.jar",
                [new Uri("https://example.test/oritech.jar")],
                oritechPayload.LongLength,
                Convert.ToHexString(SHA256.HashData(oritechPayload)).ToLowerInvariant()),
            "oritech-neoforge-1.0.0-test.jar", "Oritech", "2.0.0");
        var service = new ManagedComponentService(
            _paths, _logger, httpClient, ftb, [bumblezone, oritech]);
        var instance = CreateInstance();
        var mods = Path.Combine(instance.GameDirectory, "mods");
        await File.WriteAllTextAsync(
            Path.Combine(mods, "the_bumblezone-1.0.0-test.jar"), "broken bumblezone");
        await File.WriteAllTextAsync(
            Path.Combine(mods, "oritech-neoforge-1.0.0-test.jar"), "broken oritech");
        // The addon must never match the oritech scan pattern in a real
        // directory walk, or its presence would disable the hotfix.
        await File.WriteAllTextAsync(
            Path.Combine(mods, "oritechthings-0.0.42.jar"), "addon");

        var results = await service.EnsureModHotfixesAsync(instance);

        Assert.Equal(2, results.Count);
        Assert.False(results[0].Installed);
        Assert.True(File.Exists(Path.Combine(mods, "the_bumblezone-1.0.0-test.jar")));
        Assert.True(results[1].Installed);
        Assert.False(File.Exists(Path.Combine(mods, "oritech-neoforge-1.0.0-test.jar")));
        Assert.Equal(
            oritechPayload,
            await File.ReadAllBytesAsync(Path.Combine(mods, "oritech-neoforge-2.0.0-test.jar")));
        Assert.Equal("addon", await File.ReadAllTextAsync(
            Path.Combine(mods, "oritechthings-0.0.42.jar")));
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
        var replacement = new ManagedModReplacement(
            bumblezone, SupersededName, "Bumblezone", "1.0.0");
        return new TestService(
            new ManagedComponentService(
                _paths, _logger, httpClient, ftb, [replacement]),
            bumblezone,
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

    private sealed record TestService(
        ManagedComponentService Service,
        ManagedComponentDescriptor Descriptor,
        string PinnedFileName)
    {
        public string CachePath => Service.CachePathFor(Descriptor);

        public async Task<ManagedComponentInstallResult> EnsureBumblezoneHotfixAsync(
            PackInstanceContext instance)
        {
            var results = await Service.EnsureModHotfixesAsync(instance);
            return Assert.Single(results);
        }
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
