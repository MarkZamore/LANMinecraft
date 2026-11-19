using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

public sealed class QuiltFabricApiResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quilt-fabric-api-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public async Task OfficialProvidedFabricVersion_IsReadWithoutConfusingItWithTheQuiltRelease()
    {
        var metadata = File.ReadAllText(RepositoryFile("Program.Tests", "Fixtures", "Quilt", "quilt-loader-0.30.1.metadata.json"));
        using var handler = new Artifact(Jar(("quilt.mod.json", metadata)));
        using var client = new HttpClient(handler);
        var resolver = new QuiltFabricApiResolver(client);
        Assert.Equal("0.19.3", await resolver.ResolveAsync("1.21.1", "0.30.1", default));
        Assert.Equal("0.19.3", await resolver.ResolveAsync("26.3", "0.30.1", default));
        Assert.Equal(1, handler.Downloads);
        Assert.Equal(1, handler.Checksums);
        Assert.All(handler.Uris, uri => Assert.Equal("maven.quiltmc.org", uri.Host));
    }

    [Fact]
    public async Task VerifiedArtifact_ReusesTheExistingCmlLibraryPathAndRepairsTampering()
    {
        var paths = new AppPaths(_root);
        using var handler = new Artifact(Jar(("quilt.mod.json", Metadata())));
        using var client = new HttpClient(handler);
        Assert.Equal("0.19.3", await new QuiltFabricApiResolver(client, paths).ResolveAsync("1.21.1", "0.30.1", default));
        var cache = Path.Combine(SharedRuntimeStore.Libraries(paths), "org", "quiltmc", "quilt-loader", "0.30.1", "quilt-loader-0.30.1.jar");
        Assert.Equal(handler.Bytes, File.ReadAllBytes(cache));
        var timestamp = File.GetLastWriteTimeUtc(cache);
        Assert.Equal("0.19.3", await new QuiltFabricApiResolver(client, paths).ResolveAsync("1.21.1", "0.30.1", default));
        Assert.Equal(1, handler.Downloads);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(cache));
        File.WriteAllBytes(cache, [1, 2, 3]);
        Assert.Equal("0.19.3", await new QuiltFabricApiResolver(client, paths).ResolveAsync("1.21.1", "0.30.1", default));
        Assert.Equal(2, handler.Downloads);
        Assert.Equal(handler.Bytes, File.ReadAllBytes(cache));
    }

    [Fact]
    public async Task AnArtifactWithTheWrongOfficialDigest_IsNeverWrittenToTheRuntimeCache()
    {
        using var handler = new Artifact(Jar(("quilt.mod.json", Metadata()))) { WrongChecksum = true };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new QuiltFabricApiResolver(client, new AppPaths(_root))
            .ResolveAsync("1.21.1", "0.30.1", default));
        Assert.False(Directory.Exists(Path.Combine(_root, "Minecraft", "Launcher", "Shared", "libraries")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"fabricloader\"]")]
    [InlineData("[{\"id\":\"fabricloader\"}]")]
    public async Task MissingExplicitProvidedVersion_NeverFallsBackToTheQuiltVersion(string provides)
    {
        using var handler = new Artifact(Jar(("quilt.mod.json", Metadata(provides: provides))));
        using var client = new HttpClient(handler);
        Assert.Null(await new QuiltFabricApiResolver(client).ResolveAsync("1.21.1", "0.30.1", default));
    }

    [Theory]
    [InlineData("{\"schema_version\":2,\"quilt_loader\":{}}")]
    [InlineData("{\"schema_version\":\"1\",\"quilt_loader\":{}}")]
    [InlineData("{\"schema_version\":1,\"quilt_loader\":{\"id\":\"other\",\"version\":\"0.30.1\"}}")]
    [InlineData("{\"schema_version\":1,\"quilt_loader\":{\"id\":\"quilt_loader\",\"version\":\"0.29.0\"}}")]
    [InlineData("{\"schema_version\":1,\"quilt_loader\":{\"id\":\"quilt_loader\",\"version\":\"0.30.1\",\"provides\":[{\"id\":\"fabricloader\",\"version\":\"0.19.3\"},{\"id\":\"fabricloader\",\"version\":\"0.18.1\"}]}}")]
    public async Task WrongReleaseUnknownSchemaOrAmbiguousAlias_IsRefused(string metadata)
    {
        using var handler = new Artifact(Jar(("quilt.mod.json", metadata)));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new QuiltFabricApiResolver(client)
            .ResolveAsync("1.21.1", "0.30.1", default));
    }

    [Fact]
    public async Task DuplicateOrOversizedMetadata_IsRefusedEvenWhenTheOfficialDigestMatches()
    {
        foreach (var bytes in new[] {
                     Jar(("quilt.mod.json", Metadata()), ("QUILT.MOD.JSON", Metadata())),
                     Jar(("quilt.mod.json", new string(' ', 1024 * 1024 + 1))) })
        {
            using var handler = new Artifact(bytes);
            using var client = new HttpClient(handler);
            await Assert.ThrowsAsync<InvalidDataException>(() => new QuiltFabricApiResolver(client)
                .ResolveAsync("1.21.1", "0.30.1", default));
        }
    }

    [Fact]
    public async Task OversizedArtifact_AndUnstableOrPathLikeVersions_AreRefused()
    {
        using var handler = new Artifact(Jar(("quilt.mod.json", Metadata()))) { DeclaredLength = 33L * 1024 * 1024 };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new QuiltFabricApiResolver(client)
            .ResolveAsync("1.21.1", "0.30.1", default));
        var requests = handler.Uris.Count;
        foreach (var version in new[] { "../0.30.1", "0.31.0-beta.4", "latest" })
            await Assert.ThrowsAsync<InvalidDataException>(() => new QuiltFabricApiResolver(client)
                .ResolveAsync("1.21.1", version, default));
        Assert.Equal(requests, handler.Uris.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StalledChecksumOrJarBody_TimesOutWithoutCachingAndCanBeRetried(bool checksum)
    {
        using var handler = new Artifact(Jar(("quilt.mod.json", Metadata()))) { StallChecksum = checksum, StallJar = !checksum };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var paths = new AppPaths(_root);
        var resolver = new QuiltFabricApiResolver(client, paths, TimeSpan.FromMilliseconds(100));
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => resolver.ResolveAsync("1.21.1", "0.30.1", default)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
        Assert.False(Directory.Exists(SharedRuntimeStore.Libraries(paths)));
        handler.StallChecksum = false;
        handler.StallJar = false;
        Assert.Equal("0.19.3", await resolver.ResolveAsync("1.21.1", "0.30.1", default));
    }

    [Fact]
    public async Task CallerCancellation_RemainsCancellationRatherThanAnOfflineFailure()
    {
        using var handler = new Artifact(Jar(("quilt.mod.json", Metadata()))) { StallJar = true };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new QuiltFabricApiResolver(client, downloadStallTimeout: TimeSpan.FromSeconds(5))
                .ResolveAsync("1.21.1", "0.30.1", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static string Metadata(string provides = "[{\"id\":\"fabricloader\",\"version\":\"0.19.3\"}]") =>
        "{\"schema_version\":1,\"quilt_loader\":{\"id\":\"quilt_loader\",\"version\":\"0.30.1\",\"provides\":" + provides + "}}";

    private static byte[] Jar(params (string Name, string Contents)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry.Name).Open(), new UTF8Encoding(false));
                writer.Write(entry.Contents);
            }
        return output.ToArray();
    }

    private sealed class Artifact(byte[] bytes) : HttpMessageHandler
    {
        public byte[] Bytes { get; } = bytes;
        public bool WrongChecksum { get; init; }
        public long? DeclaredLength { get; init; }
        public bool StallChecksum { get; set; }
        public bool StallJar { get; set; }
        public int Downloads { get; private set; }
        public int Checksums { get; private set; }
        public List<Uri> Uris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Uris.Add(uri);
            Assert.Equal("https", uri.Scheme);
            Assert.Equal("maven.quiltmc.org", uri.Host);
            HttpContent content;
            if (uri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
            {
                Checksums++;
                content = StallChecksum ? new StreamContent(new StalledStream()) :
                    new StringContent(WrongChecksum ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(Bytes)));
            }
            else
            {
                Downloads++;
                Assert.EndsWith("/0.30.1/quilt-loader-0.30.1.jar", uri.AbsolutePath, StringComparison.Ordinal);
                content = StallJar ? new StreamContent(new StalledStream()) : new ByteArrayContent(Bytes);
                if (DeclaredLength is { } length) content.Headers.ContentLength = length;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class StalledStream : Stream
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

    private static string RepositoryFile(params string[] parts)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var path = parts.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }
}
