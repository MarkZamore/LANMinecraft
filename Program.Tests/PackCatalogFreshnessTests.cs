using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PackCatalogFreshnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "catalog-freshness-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly Logger _logger;

    public PackCatalogFreshnessTests()
    {
        _paths = new AppPaths(_root);
        _paths.Ensure();
        _logger = new Logger(_paths.LogFile);
    }

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public async Task ANewStartupBypassesAnOldSuccessfulRollingResponseAndSyncUsesTheVerifiedNewSnapshot()
    {
        using var host = new RollingHost();
        var initial = new PackCatalogService(_paths, _logger, host.Client);
        await initial.RefreshOnceAsync();
        Assert.Equal("old-verified", initial.Find(RollingHost.Folder)!.Content.Revision);

        host.PublishNew = true;
        using (var headerOnly = new HttpRequestMessage(HttpMethod.Get, PackCatalogService.AssetUri(host.Entry.Source, "pack-manifest.json")))
        {
            headerOnly.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var staleResponse = await host.Client.SendAsync(headerOnly);
            Assert.True(staleResponse.IsSuccessStatusCode);
            using var stale = JsonDocument.Parse(await staleResponse.Content.ReadAsByteArrayAsync());
            Assert.Equal("old-verified", stale.RootElement.GetProperty("revision").GetString());
        }
        var startup = new PackCatalogService(_paths, _logger, host.Client);
        Assert.Equal("old-verified", startup.Find(RollingHost.Folder)!.Content.Revision);
        await startup.RefreshOnceAsync();

        Assert.Equal("new-verified", startup.Find(RollingHost.Folder)!.Content.Revision);
        Assert.Contains("new-verified", startup.SessionManifest(host.Entry.Source), StringComparison.Ordinal);
        var manifests = host.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/pack-manifest.json", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, manifests.Length);
        Assert.Empty(manifests[1].Uri.Query);
        Assert.All(manifests, r => { Assert.True(r.NoCache); Assert.True(r.NoStore); });
        Assert.NotEmpty(manifests[0].Uri.Query);
        Assert.NotEmpty(manifests[2].Uri.Query);
        Assert.NotEqual(manifests[0].Uri.Query, manifests[2].Uri.Query);
        Assert.All(host.Requests.Where(r => r.Uri.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal)), r => Assert.Empty(r.Uri.Query));

        var before = host.Requests.Count;
        var sync = new PortablePackSyncService(_paths, _logger, host.Client) { Catalog = startup };
        await sync.SyncAsync(RollingHost.Folder, null, default);
        Assert.Equal(before + 2, host.Requests.Count);
        Assert.Empty(host.Requests.Last().Uri.Query);
        Assert.Equal(RollingHost.NewPayload, File.ReadAllBytes(Path.Combine(_paths.Packs, RollingHost.Folder, "mods/payload.jar")));
        Assert.Contains("verified startup manifest revision \"new-verified\"", File.ReadAllText(_paths.LogFile), StringComparison.Ordinal);
        Assert.Contains("SHA256 ", File.ReadAllText(_paths.LogFile), StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataFreshnessPreservesAnExistingQueryAndFragment()
    {
        using var host = new RollingHost();
        var service = new PackCatalogService(_paths, _logger, host.Client);
        var original = new Uri("https://example.invalid/rolling.json?existing=a%2Bb#section");
        var refreshed = service.MutableMetadataUri(original);
        Assert.Equal(original.GetLeftPart(UriPartial.Path), refreshed.GetLeftPart(UriPartial.Path));
        Assert.Equal(original.Fragment, refreshed.Fragment);
        Assert.StartsWith("?existing=a%2Bb&catalogRefresh=", refreshed.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyGameOnlyManifestWithoutRevisionStillFreezesAndLogsSafely()
    {
        using var host = new RollingHost { LegacyGameOnly = true };
        var service = new PackCatalogService(_paths, _logger, host.Client);
        await service.RefreshOnceAsync();
        Assert.True(service.IsReady);
        Assert.Contains("mods/payload.jar", service.SessionManifest(host.Entry.Source), StringComparison.Ordinal);
        Assert.Contains("verified startup manifest revision null", File.ReadAllText(_paths.LogFile), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedFreshMetadataOrBundleKeepsTheLastVerifiedOfflineSnapshot(bool corruptBundle)
    {
        using var host = new RollingHost();
        var initial = new PackCatalogService(_paths, _logger, host.Client);
        await initial.RefreshOnceAsync();
        var oldManifest = initial.SessionManifest(host.Entry.Source);
        host.PublishNew = true;
        host.Offline = !corruptBundle;
        host.CorruptBundle = corruptBundle;

        var startup = new PackCatalogService(_paths, _logger, host.Client);
        await startup.RefreshOnceAsync();

        Assert.Equal("old-verified", startup.Find(RollingHost.Folder)!.Content.Revision);
        Assert.Equal(oldManifest, startup.SessionManifest(host.Entry.Source));
        Assert.Equal(corruptBundle, startup.HasOnlineCatalog);
    }

    [Fact]
    public async Task FreshSuccessfulEmptyIndexRemovesOldOffersAndRemainsAuthoritativeOffline()
    {
        using var host = new RollingHost();
        await new PackCatalogService(_paths, _logger, host.Client).RefreshOnceAsync();
        host.PublishEmpty = true;
        var startup = new PackCatalogService(_paths, _logger, host.Client);
        await startup.RefreshOnceAsync();

        Assert.True(startup.HasOnlineCatalog);
        Assert.Empty(startup.Sources);
        Assert.Empty(startup.Packs);
        Assert.True(startup.IsWithdrawn(RollingHost.Folder));
        host.Offline = true;
        var offline = new PackCatalogService(_paths, _logger, host.Client);
        await offline.RefreshOnceAsync();
        Assert.Empty(offline.Sources);
        Assert.Empty(offline.Packs);
        Assert.True(offline.IsWithdrawn(RollingHost.Folder));
        var indices = host.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/catalog.json", StringComparison.Ordinal)).ToArray();
        Assert.All(indices, r => Assert.NotEmpty(r.Uri.Query));
    }

    private sealed record Request(Uri Uri, bool NoCache, bool NoStore);

    private sealed class RollingHost : HttpMessageHandler
    {
        internal const string Folder = "A catalog freshness fixture";
        internal static readonly byte[] OldPayload = [1, 2, 3];
        internal static readonly byte[] NewPayload = [4, 5, 6];
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
        private readonly Dictionary<string, byte[]> _bundles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _manifests = new(StringComparer.Ordinal);
        internal readonly CatalogSource Entry = new("freshness-pack", Folder, new PackSyncSource("FixtureOwner", "FixtureRepository", "rolling"));
        internal List<Request> Requests { get; } = [];
        internal HttpClient Client { get; }
        internal bool PublishNew, PublishEmpty, Offline, CorruptBundle, LegacyGameOnly;

        internal RollingHost()
        {
            Client = new HttpClient(this);
            using var seed = typeof(PackCatalogService).Assembly.GetManifestResourceStream("Minecraft.Library.minecraft-26-2.zip")!;
            using var archive = new ZipArchive(seed, ZipArchiveMode.Read);
            using var preview = archive.GetEntry("preview.jpg")!.Open();
            using var image = new MemoryStream();
            preview.CopyTo(image);
            Add("old-verified", OldPayload, image.ToArray());
            Add("new-verified", NewPayload, image.ToArray());
        }

        private void Add(string revision, byte[] payload, byte[] image)
        {
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                using (var metadata = archive.CreateEntry("library.json").Open())
                    JsonSerializer.Serialize(metadata, new CatalogDescription
                    {
                        SchemaVersion = 1, Id = Entry.Id, RelativePath = Folder, Name = Folder,
                        MinecraftVersion = "1.20.1", Revision = revision, Summary = "Fixture", Description = "Fixture"
                    }, Options);
                using var preview = archive.CreateEntry("preview.jpg").Open();
                preview.Write(image);
            }
            var bytes = output.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var name = "library-" + hash + ".zip";
            _bundles[name] = bytes;
            var payloadHash = Convert.ToHexStringLower(SHA256.HashData(payload));
            _manifests[revision] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, revision, minecraftVersion = "1.20.1",
                files = new[] { new { path = "mods/payload.jar", sizeBytes = payload.Length, sha256 = payloadHash } },
                assets = new[] { new { name = revision + ".jar", kind = "jar", sizeBytes = payload.Length, sha256 = payloadHash, covers = new[] { "mods/payload.jar" } } },
                library = new { name, sizeBytes = bytes.Length, sha256 = hash, revision }
            }, Options);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            Requests.Add(new Request(uri, request.Headers.CacheControl?.NoCache == true, request.Headers.CacheControl?.NoStore == true));
            if (Offline) return Reply(HttpStatusCode.ServiceUnavailable, []);
            // Model an intermediary retaining an old successful mutable URL even
            // with no-cache headers. Immutable hash-addressed assets stay normal.
            var fresh = uri.Query.Contains("catalogRefresh=", StringComparison.Ordinal) ||
                        uri.Query.Contains("cache=", StringComparison.Ordinal);
            if (uri.AbsolutePath.EndsWith("/catalog.json", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, packs = PublishEmpty && fresh ? Array.Empty<CatalogSource>() : new[] { Entry } }, Options));
            var name = Uri.UnescapeDataString(uri.Segments[^1]);
            if (name == "pack-manifest.json")
            {
                if (LegacyGameOnly)
                {
                    using var document = JsonDocument.Parse(_manifests["old-verified"]);
                    return Reply(HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(document.RootElement.EnumerateObject()
                        .Where(p => p.Name is not ("revision" or "library")).ToDictionary(p => p.Name, p => p.Value)));
                }
                return Reply(HttpStatusCode.OK, _manifests[PublishNew && fresh ? "new-verified" : "old-verified"]);
            }
            if (_bundles.TryGetValue(name, out var bundle)) return Reply(HttpStatusCode.OK, CorruptBundle ? [9] : bundle);
            if (name == "new-verified.jar") return Reply(HttpStatusCode.OK, NewPayload);
            if (name == "old-verified.jar") return Reply(HttpStatusCode.OK, OldPayload);
            return Reply(HttpStatusCode.NotFound, []);
        }

        private static Task<HttpResponseMessage> Reply(HttpStatusCode status, byte[] bytes) => Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) });
    }
}
