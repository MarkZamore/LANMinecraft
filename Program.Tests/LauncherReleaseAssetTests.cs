using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft.Tests;

public sealed class LauncherReleaseAssetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GatewayFailureUsesAnIndependentOfficialAssetAndConfirmsTheVersion(bool newer)
    {
        using var fixture = new Fixture(newer, "valid");
        var result = await fixture.Service.CheckAsync(CancellationToken.None, attempts: 1);
        Assert.False(result.IsUnavailable);
        Assert.Equal(newer, result.IsUpdateAvailable);
        Assert.Contains(fixture.Requests, entry => entry.Contains("api.github.com/repos/MarkZamore/LANMinecraft/releases/assets/123", StringComparison.Ordinal));
        Assert.True(fixture.OctetStream);
    }

    [Theory]
    [InlineData("changed-bytes")]
    [InlineData("different-name")]
    public async Task UnverifiedApiContentCannotClaimTheLauncherIsUpToDate(string failure)
    {
        using var fixture = new Fixture(false, failure);
        var result = await fixture.Service.CheckAsync(CancellationToken.None, attempts: 1);
        Assert.True(result.IsUnavailable);
        Assert.False(result.IsUpdateAvailable);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly string root = Path.Combine(AppContext.BaseDirectory, "ApiFallback-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient client;
        private readonly byte[] bytes;
        private readonly string failure;
        internal readonly List<string> Requests = [];
        internal bool OctetStream;
        internal UpdateService Service { get; }
        internal Fixture(bool newer, string failure)
        {
            this.failure = failure;
            bytes = JsonSerializer.SerializeToUtf8Bytes(new UpdateManifest
            {
                SchemaVersion = 2, ReleaseNumber = 480, CommitSha = new string(newer ? 'b' : 'a', 40),
                Version = "480", Sha256 = new string('c', 64), SizeBytes = 100
            });
            client = new HttpClient(this);
            Service = new UpdateService(new AppPaths(root), new Logger(Path.Combine(root, "check.log")), client, new string('a', 40));
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.ToString());
            Assert.True(request.Headers.CacheControl?.NoCache);
            if (uri.Host == "github.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
            Assert.Equal("api.github.com", uri.Host);
            if (uri.AbsolutePath.EndsWith("/tags/latest", StringComparison.Ordinal))
            {
                var metadata = new { assets = new[] { new { id = 123, name = failure == "different-name" ? "other.json" : "update.json",
                    size = bytes.Length, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), state = "uploaded" } } };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(metadata)) });
            }
            Assert.EndsWith("/assets/123", uri.AbsolutePath, StringComparison.Ordinal);
            OctetStream = request.Headers.Accept.Any(value => value.MediaType == "application/octet-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(failure == "changed-bytes" ? Encoding.UTF8.GetBytes("{}") : bytes) });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Directory.Exists(root)) TempTree.Delete(root);
        }
    }
}
