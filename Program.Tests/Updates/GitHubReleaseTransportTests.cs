using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft.Tests;

public sealed class GitHubReleaseTransportTests
{
    [Theory]
    [InlineData("e4steam", "legacy-fabric-164-v3", "e4steam.jar")]
    [InlineData("Any-Pack", "release/with slash", "a file.jar")]
    [InlineData("temurin25-binaries", "jdk-25.0.2+10", "runtime.zip")]
    public async Task SourceIdentitySelectsTheApiAndVerifiesStreamingBytes(string repo, string tag, string name)
    {
        using var handler = new Handler(name);
        using var client = new HttpClient(handler);
        using var response = await GitHubReleaseTransport.GetAsync(client, Url(repo, tag, name), CancellationToken.None);
        Assert.Equal(handler.Bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(3, handler.Uris.Count);
        Assert.Equal($"/repos/author/{repo}/releases/tags/{Uri.EscapeDataString(tag)}", handler.Uris[1].AbsolutePath);
        Assert.Equal($"/repos/author/{repo}/releases/assets/42", handler.Uris[2].AbsolutePath);
        Assert.True(handler.OctetStream);
        Assert.Equal(Url(repo, tag, name), response.RequestMessage!.RequestUri);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("state")]
    [InlineData("digest")]
    public async Task UnusableMetadataIsRejected(string mode)
    {
        using var handler = new Handler("a.jar") { Mode = mode };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => GitHubReleaseTransport.GetAsync(client, Url(), CancellationToken.None));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("short")]
    [InlineData("long")]
    public async Task CorruptBodyCannotPassTheVerifiedStream(string mode)
    {
        using var handler = new Handler("a.jar") { Mode = mode };
        using var client = new HttpClient(handler);
        using var response = await GitHubReleaseTransport.GetAsync(client, Url(), CancellationToken.None);
        await using var input = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[2];
        await Assert.ThrowsAsync<InvalidDataException>(async () => { while (await input.ReadAsync(buffer) != 0) { } });
    }

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/file")]
    [InlineData("https://evil.example/file")]
    [InlineData("https://api.github.com/repos/other/repo/releases/assets/42")]
    public async Task ApiCannotRedirectOutsideTheApprovedEndpoints(string destination)
    {
        using var handler = new Handler("a.jar") { Redirect = destination };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => GitHubReleaseTransport.GetAsync(client, Url(), CancellationToken.None));
    }

    [Fact]
    public async Task JsonIsVerifiedBeforeAnyParserCanAcceptIt()
    {
        using var handler = new Handler("manifest.json") { Mode = "hash" };
        using var client = new HttpClient(handler);
        var error = await Record.ExceptionAsync(() => GitHubReleaseTransport.GetAsync(client, Url(name: "manifest.json"), CancellationToken.None));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task MissingAssetPreservesNotFoundForTheExistingSessionRecovery()
    {
        using var handler = new Handler("different.jar");
        using var client = new HttpClient(handler);
        using var response = await GitHubReleaseTransport.GetAsync(client, Url(), CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(2, handler.Uris.Count);
    }

    [Fact]
    public async Task RangeFallbackRestartsWithAWholeVerifiedFile()
    {
        using var handler = new Handler("a.jar");
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, Url());
        request.Headers.Range = new RangeHeaderValue(2, null);
        using var response = await GitHubReleaseTransport.SendAsync(client, request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(handler.ApiRange);
        Assert.Equal(handler.Bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task PrimaryTimeoutLeavesTimeForTheApi()
    {
        using var handler = new Handler("a.jar") { StallPrimary = true };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, Url());
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await GitHubReleaseTransport.SendAsync(client, request, budget.Token, TimeSpan.FromMilliseconds(20));
        Assert.Equal(handler.Bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationStopsPrimaryOrApiWithoutAnotherRoute(bool api)
    {
        using var handler = new Handler("a.jar") { StallPrimary = !api, StallApi = api };
        using var client = new HttpClient(handler);
        using var token = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GitHubReleaseTransport.GetAsync(client, Url(), token.Token));
        Assert.Equal(api ? 2 : 1, handler.Uris.Count);
    }

    [Fact]
    public async Task NonReleaseSourcesRetainTheirOwnResponseAndPolicy()
    {
        using var handler = new Handler("a.jar");
        using var client = new HttpClient(handler);
        using var response = await GitHubReleaseTransport.GetAsync(client, new Uri("https://libraries.minecraft.net/file.jar"), CancellationToken.None);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Single(handler.Uris);
    }

    private static Uri Url(string repo = "pack", string tag = "latest", string name = "a.jar") =>
        new($"https://github.com/author/{repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BinaryAddressesAreReusedButEveryJsonManifestIsFresh(bool json)
    {
        var name = json ? "pack-manifest.json" : "a.jar";
        using var handler = new Handler(name);
        using var client = new HttpClient(handler);
        for (var index = 0; index < 2; index++)
        {
            using var response = await GitHubReleaseTransport.GetAsync(client, Url(name: name), CancellationToken.None);
            Assert.Equal(handler.Bytes, await response.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(json ? 2 : 1, handler.Uris.Count(uri => uri.AbsolutePath.Contains("/tags/", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedComponentUsesTheFallbackWithoutWeakeningItsOwnPinOrChangingOtherFiles(bool wrongPin)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "ApiManaged-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            paths.Ensure();
            using var handler = new Handler("a.jar");
            using var client = new HttpClient(handler);
            var component = new ManagedComponentDescriptor("e4steam-test", 1234, "a.jar", [Url()], handler.Bytes.Length,
                wrongPin ? new string('b', 64) : Convert.ToHexString(SHA256.HashData(handler.Bytes)).ToLowerInvariant());
            var service = new ManagedComponentService(paths, new Logger(Path.Combine(root, "check.log")), client, component, TimeSpan.Zero);
            var game = paths.CombineUnderInstances("fixture");
            var pack = paths.CombineUnderPacks("fixture");
            Directory.CreateDirectory(Path.Combine(game, "mods"));
            Directory.CreateDirectory(pack);
            var old = Path.Combine(game, "mods", "a.jar");
            var other = Path.Combine(game, "mods", "player.jar");
            File.WriteAllText(old, "previous installed bytes");
            File.WriteAllText(other, "unrelated bytes");
            var instance = new PackInstanceContext(pack, game, Path.Combine(pack, "client.jar"));
            if (wrongPin)
            {
                await Assert.ThrowsAsync<HttpRequestException>(() => service.EnsureSteamTransportModAsync(instance));
                Assert.Equal("previous installed bytes", File.ReadAllText(old));
                Assert.False(File.Exists(service.E4steamCachePath));
            }
            else
            {
                var result = await service.EnsureSteamTransportModAsync(instance);
                Assert.True(result.Downloaded);
                Assert.Equal(handler.Bytes, File.ReadAllBytes(result.InstalledPath));
            }
            Assert.Equal("unrelated bytes", File.ReadAllText(other));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally { TempTree.Delete(root); }
    }

    private sealed class Handler(string name) : HttpMessageHandler
    {
        internal readonly byte[] Bytes = Encoding.UTF8.GetBytes("{\"payload\":true}");
        internal readonly List<Uri> Uris = [];
        internal string Mode = "valid";
        internal string? Redirect;
        internal bool StallPrimary, StallApi, ApiRange, OctetStream;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            Uris.Add(uri);
            if (uri.Host != "api.github.com")
            {
                if (StallPrimary) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new(HttpStatusCode.GatewayTimeout);
            }
            Assert.True(request.Headers.CacheControl?.NoCache);
            if (uri.AbsolutePath.Contains("/tags/", StringComparison.Ordinal))
            {
                if (StallApi) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                Assert.False(string.IsNullOrEmpty(uri.Query));
                var row = new { id = 42, name, size = Bytes.Length,
                    digest = Mode == "digest" ? "broken" : "sha256:" + Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant(),
                    state = Mode == "state" ? "uploading" : "uploaded", url = "https://evil.example/ignored" };
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { assets = Mode == "duplicate" ? new[] { row, row } : new[] { row } })) };
            }
            OctetStream = request.Headers.Accept.Any(value => value.MediaType == "application/octet-stream");
            ApiRange = request.Headers.Range is not null;
            var bytes = Mode switch { "hash" => Enumerable.Repeat((byte)'x', Bytes.Length).ToArray(), "short" => Bytes[..^1], "long" => Bytes.Concat(new byte[] { 0 }).ToArray(), _ => Bytes };
            var content = new StreamContent(new ChunkedStream(bytes));
            return new(HttpStatusCode.OK) { Content = content,
                RequestMessage = Redirect is null ? request : new HttpRequestMessage(HttpMethod.Get, Redirect) };
        }
    }

    private sealed class ChunkedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
