using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

public sealed class LoaderVersionResolverTests
{
    [Fact]
    public async Task OfficialCatalogs_ExcludeSnapshotsAndUnstableLoadersAndCacheTheirResponses()
    {
        using var handler = new Replies(uri => uri.Host switch
        {
            "piston-meta.mojang.com" => """{"versions":[{"id":"1.21.1","type":"release"},{"id":"26.3","type":"release"},{"id":"26.4-snapshot-1","type":"snapshot"}]}""",
            "meta.fabricmc.net" => """[{"loader":{"version":"0.20.0","stable":false}},{"loader":{"version":"0.16.9","stable":true}},{"loader":{"version":"0.16.14","stable":true}},{"loader":{"version":"0.21.0-beta.1","stable":true}},{"loader":{"version":"0.22.0"}}]""",
            "meta.quiltmc.org" => """[{"loader":{"version":"0.30.0-beta.1"}},{"loader":{"version":"0.29.2"}},{"loader":{"version":"0.29.3"}}]""",
            _ => throw new InvalidOperationException("unexpected request")
        });
        using var client = new HttpClient(handler);
        var resolver = new LoaderVersionResolver(client);
        Assert.Equal(["26.3", "1.21.1"], await resolver.MinecraftVersionsAsync(default));
        Assert.Equal(["0.16.14", "0.16.9"], await resolver.LoaderVersionsAsync(PackLoaderKind.Fabric, "1.21.1", default));
        Assert.Equal(["0.29.3", "0.29.2"], await resolver.LoaderVersionsAsync(PackLoaderKind.Quilt, "1.21.1", default));
        Assert.Equal("0.16.14", await resolver.ResolveAsync(PackLoaderKind.Fabric, "1.21.1", default));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task NeoForge26AndLegacyNumbering_UseTheirOwnStableArtifactsAndFmlApi()
    {
        using var handler = new Replies(uri => uri.AbsolutePath.EndsWith("maven-metadata.xml", StringComparison.Ordinal)
            ? "<metadata><versioning><versions><version>26.3.0.99</version><version>26.3.0.100-beta</version><version>26.2.0.84</version><version>21.1.259</version></versions></versioning></metadata>"
            : "<project><dependencies><dependency><groupId>net.neoforged.fancymodloader</groupId><artifactId>loader</artifactId><version>12.0.12</version></dependency></dependencies></project>");
        using var client = new HttpClient(handler);
        var resolver = new LoaderVersionResolver(client);
        Assert.Equal(["26.3.0.99"], await resolver.LoaderVersionsAsync(PackLoaderKind.NeoForge, "26.3", default));
        Assert.Equal(["21.1.259"], await resolver.LoaderVersionsAsync(PackLoaderKind.NeoForge, "1.21.1", default));
        Assert.Equal("12.0.12", await resolver.FmlVersionAsync(PackLoaderKind.NeoForge, "26.3", "26.3.0.99", default));
        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/26.3.0.99/neoforge-26.3.0.99.pom", handler.Requests.Last().AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgeVersions_IncludeOlderStableChoicesNeededByModRangesAndLegacyArtifactSuffixes()
    {
        using var handler = new Replies(uri => uri.AbsolutePath.EndsWith("maven-metadata.xml", StringComparison.Ordinal)
            ? "<metadata><versioning><versions><version>1.20.1-47.3.0</version><version>1.20.1-47.1.9</version><version>1.20.1-47.4.0-rc1</version><version>1.7.10-10.13.4.1614-1.7.10</version></versions></versioning></metadata>"
            : "<project><dependencies><dependency><groupId>net.minecraftforge</groupId><artifactId>javafmllanguage</artifactId><version>1.20.1-47.3.0</version></dependency></dependencies></project>");
        using var client = new HttpClient(handler);
        var resolver = new LoaderVersionResolver(client);
        Assert.Equal(["47.3.0", "47.1.9"], await resolver.LoaderVersionsAsync(PackLoaderKind.Forge, "1.20.1", default));
        Assert.Equal(["10.13.4.1614-1.7.10"], await resolver.LoaderVersionsAsync(PackLoaderKind.Forge, "1.7.10", default));
        Assert.Equal("47.3.0", await resolver.FmlVersionAsync(PackLoaderKind.Forge, "1.20.1", "47.3.0", default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ForgePomWithoutDependencies_UsesVerifiedInstallerMetadataAndTheExistingRuntimeCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-fml-metadata-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            paths.Ensure();
            using var handler = new ForgeInstaller();
            using var client = new HttpClient(handler);
            Assert.Equal("47.3.7", await new LoaderVersionResolver(client, paths: paths)
                .FmlVersionAsync(PackLoaderKind.Forge, "1.20.1", "47.3.7", default));
            var installer = Path.Combine(paths.SharedRuntime, "installers", "forge", "1.20.1-47.3.7", "forge-1.20.1-47.3.7-installer.jar");
            Assert.Equal(handler.Installer, File.ReadAllBytes(installer));
            Assert.Equal("47.3.7", await new LoaderVersionResolver(client, paths: paths)
                .FmlVersionAsync(PackLoaderKind.Forge, "1.20.1", "47.3.7", default));
            Assert.Equal(1, handler.InstallerDownloads);
            File.WriteAllBytes(installer, [1, 2, 3]);
            Assert.Equal("47.3.7", await new LoaderVersionResolver(client, paths: paths)
                .FmlVersionAsync(PackLoaderKind.Forge, "1.20.1", "47.3.7", default));
            Assert.Equal(2, handler.InstallerDownloads);
            Assert.Equal(handler.Installer, File.ReadAllBytes(installer));
        }
        finally { TempTree.Delete(root); }
    }

    [Fact]
    public async Task InstallerMetadata_DoesNotEnterTheRuntimeCacheUnlessItsOfficialChecksumMatches()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-fml-bad-digest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            paths.Ensure();
            using var handler = new ForgeInstaller { WrongDigest = true };
            using var client = new HttpClient(handler);
            await Assert.ThrowsAsync<InvalidDataException>(() => new LoaderVersionResolver(client, paths: paths)
                .FmlVersionAsync(PackLoaderKind.Forge, "1.20.1", "47.3.7", default));
            Assert.False(Directory.Exists(Path.Combine(paths.SharedRuntime, "installers", "forge")));
        }
        finally { TempTree.Delete(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledInstallerHeadersOrBody_TimeOutWithoutCachingAndAllowRetry(bool beforeHeaders)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-fml-stall-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            using var handler = new ForgeInstaller { StallInstaller = true, StallBeforeHeaders = beforeHeaders };
            using var client = new HttpClient(handler);
            var resolver = new LoaderVersionResolver(client, null, paths, TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<HttpRequestException>(() => resolver.FmlVersionAsync(PackLoaderKind.Forge, "1.20.1", "47.3.7", default));
            Assert.False(Directory.Exists(Path.Combine(paths.SharedRuntime, "installers", "forge")));
            handler.StallInstaller = false;
            Assert.Equal("47.3.7", await resolver.FmlVersionAsync(PackLoaderKind.Forge, "1.20.1", "47.3.7", default));
            Assert.Equal(2, handler.InstallerDownloads);
        }
        finally { TempTree.Delete(root); }
    }

    [Fact]
    public async Task CallerCancellationDuringInstallerBody_RemainsCancellationAndCreatesNoCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-fml-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            using var handler = new ForgeInstaller { StallInstaller = true };
            using var client = new HttpClient(handler);
            var resolver = new LoaderVersionResolver(client, null, paths, TimeSpan.FromSeconds(5));
            using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.FmlVersionAsync(PackLoaderKind.Forge, "1.20.1", "47.3.7", caller.Token));
            Assert.False(Directory.Exists(Path.Combine(paths.SharedRuntime, "installers", "forge")));
        }
        finally { TempTree.Delete(root); }
    }

    [Fact]
    public async Task AStalledInstallerChecksum_IsEvictedAndCanBeRetried()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-checksum-stall-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            using var handler = new ForgeInstaller { StallInstaller = true, StallChecksum = true };
            using var client = new HttpClient(handler);
            var resolver = new LoaderVersionResolver(client, null, paths, TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<HttpRequestException>(() => resolver.FmlVersionAsync(PackLoaderKind.Forge, "1.20.1", "47.3.7", default));
            Assert.Equal(0, handler.InstallerDownloads);
            Assert.False(Directory.Exists(Path.Combine(paths.SharedRuntime, "installers", "forge")));
            handler.StallInstaller = false;
            Assert.Equal("47.3.7", await resolver.FmlVersionAsync(PackLoaderKind.Forge, "1.20.1", "47.3.7", default));
            Assert.Equal(1, handler.InstallerDownloads);
        }
        finally { TempTree.Delete(root); }
    }

    [Fact]
    public async Task FailedMetadataRequest_IsNotPermanentlyCached()
    {
        var fail = true;
        using var handler = new Replies(_ =>
        {
            if (fail) throw new HttpRequestException("offline");
            return """[{"loader":{"version":"0.16.14","stable":true}}]""";
        });
        using var client = new HttpClient(handler);
        var resolver = new LoaderVersionResolver(client);
        Assert.Null(await resolver.ResolveAsync(PackLoaderKind.Fabric, "1.21.1", default));
        fail = false;
        Assert.Equal("0.16.14", await resolver.ResolveAsync(PackLoaderKind.Fabric, "1.21.1", default));
        Assert.Equal(2, handler.Requests.Count);
    }

    // Real Forge 1.20.1 POMs omit dependencies. The language coordinate is in
    // install_profile.json, while version.json need not name it at all.
    private sealed class ForgeInstaller : HttpMessageHandler
    {
        public byte[] Installer { get; }
        public bool WrongDigest { get; init; }
        public bool StallInstaller { get; set; }
        public bool StallBeforeHeaders { get; init; }
        public bool StallChecksum { get; init; }
        public int InstallerDownloads { get; private set; }
        public ForgeInstaller()
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var profile = zip.CreateEntry("install_profile.json").Open())
                    profile.Write(Encoding.UTF8.GetBytes("""{"libraries":[{"name":"net.minecraftforge:javafmllanguage:1.20.1-47.3.7"}]}"""));
                using var version = zip.CreateEntry("version.json").Open();
                version.Write(Encoding.UTF8.GetBytes("""{"libraries":[]}"""));
            }
            Installer = output.ToArray();
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "Tests the official Forge installer SHA-1 protocol.")]
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpContent payload;
            if (request.RequestUri!.AbsolutePath.EndsWith(".pom", StringComparison.Ordinal)) payload = new StringContent("<project />");
            else if (request.RequestUri.AbsolutePath.EndsWith(".sha1", StringComparison.Ordinal))
                payload = StallInstaller && StallChecksum ? new StreamContent(new StalledReadStream()) :
                    new StringContent(WrongDigest ? new string('0', 40) : Convert.ToHexString(SHA1.HashData(Installer)));
            else
            {
                Assert.EndsWith("-installer.jar", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
                InstallerDownloads++;
                if (StallInstaller && StallBeforeHeaders) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                payload = StallInstaller ? new StreamContent(new StalledReadStream()) : new ByteArrayContent(Installer);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) {Content = payload};
        }
    }

    private sealed class StalledReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class Replies(Func<Uri, string> reply) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content = new StringContent(reply(request.RequestUri!))});
        }
    }
}
