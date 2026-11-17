using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Minecraft.Tests;

public sealed class SteamComponentCatalogTests : IDisposable
{
    private const string FutureProfile = "native-lan-ui-future-test";
    private const string FutureHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FutureFile = "e4steam-forge-future-test.jar";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "steam-component-catalog-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly Logger _logger;

    public SteamComponentCatalogTests()
    {
        _paths = new AppPaths(_root);
        _paths.Ensure();
        _logger = new Logger(_paths.LogFile);
    }

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void PublishedCatalog_PreservesAllReviewedBranchesAndPublisherDelivery()
    {
        var catalog = SteamComponentCatalog.Parse(RuntimeComponentContext.Current.SteamCatalogJson);
        Assert.NotEmpty(catalog.Profile);
        Assert.Equal(22, catalog.Builds.Count);
        Assert.Equal(2, catalog.Builds.Count(build => build.PreservesWorldSettingsOnShare));
        Assert.Equal(13, catalog.Builds.Count(build => build.Version == "0.3.2"));
        Assert.Equal(7, catalog.Builds.Count(build => build.Version == "0.3.4"));
        Assert.All(catalog.Builds, build =>
        {
            Assert.True(build.NativeLanUiSuppressed);
            var uri = Assert.Single(build.DownloadUris);
            Assert.Equal("https", uri.Scheme);
            Assert.Equal("github.com", uri.Host);
            Assert.StartsWith("/MarkZamore/e4steam/releases/download/", uri.AbsolutePath, StringComparison.Ordinal);
            Assert.EndsWith("/" + build.FileName, Uri.UnescapeDataString(uri.AbsolutePath), StringComparison.Ordinal);
            Assert.Empty(uri.Query);
            if (build.Loaders.Contains(PackLoaderKind.LegacyFabric) || build.PreservesWorldSettingsOnShare)
            {
                Assert.Null(build.AuthorFileName);
                Assert.NotNull(build.UpstreamSourceCommit);
            }
            else Assert.NotEmpty(build.AuthorFileName!);
        });
        Assert.Equal(22, catalog.Builds.Select(build => build.CacheFileId).Distinct().Count());
        Assert.Contains(catalog.SupersededBuilds, old => old.FileName.Contains("native-ui.1", StringComparison.Ordinal));

    }

    [Theory]
    [InlineData("native-lan-settings-ui-hidden-or-absent")]
    [InlineData("modern-configured-online-mode-at-native-publish")]
    [InlineData("author-steam-authentication-retained")]
    public void MissingReviewedCapability_IsRejected(string capability)
    {
        var root = Embedded();
        var capabilities = root["capabilities"]!.AsArray();
        capabilities.RemoveAt(capabilities.Select((value, index) => (value, index))
            .Single(item => item.value!.GetValue<string>() == capability).index);
        Assert.Throws<InvalidDataException>(() => Parse(root));
    }

    [Fact]
    public void OptionalJava8SteamBuildIsPinnedToItsReviewedForgePatch()
    {
        var root = Embedded();
        const string fileName = "e4steam-forge-mc1.12.2-world-sharing-test.jar";
        const string sourceRevision = "0123456789abcdef0123456789abcdef01234567";
        var row = new JsonObject
        {
            ["version"] = "0.3.4",
            ["authorVersion"] = "0.3.4",
            ["profile"] = root["profile"]!.GetValue<string>(),
            ["fileName"] = fileName,
            ["sizeBytes"] = 1,
            ["sha256"] = FutureHash,
            ["deliveryTag"] = "java8-world-sharing-test",
            ["downloadUrl"] = $"https://github.com/MarkZamore/e4steam/releases/download/java8-world-sharing-test/{fileName}",
            ["loaders"] = new JsonArray("Forge"),
            ["minimumMinecraftVersion"] = new JsonArray(1, 12, 2),
            ["exclusiveMaximumMinecraftVersion"] = new JsonArray(1, 12, 3),
            ["metadataPath"] = "mcmod.info",
            ["derivation"] = new JsonObject
            {
                ["type"] = "source-built-java8-forge-world-sharing",
                ["sourceCommit"] = sourceRevision,
                ["upstreamSourceCommit"] = sourceRevision,
                ["sourceBuiltClassSha256"] = new JsonObject { ["link/e4steam/retro/Test.class"] = FutureHash }
            }
        };
        root["java8Builds"] = new JsonArray(row);

        var build = Assert.Single(Parse(root).Builds, value => value.PreservesWorldSettingsOnShare);
        Assert.Equal(fileName, build.FileName);
        Assert.Equal(sourceRevision, build.UpstreamSourceCommit);
        Assert.Null(build.AuthorFileName);

        row["exclusiveMaximumMinecraftVersion"] = new JsonArray(1, 13);
        Assert.Throws<InvalidDataException>(() => Parse(root));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("overlap")]
    [InlineData("gap")]
    [InlineData("missing-branch")]
    public void AmbiguousOrReducedMinecraftLoaderCoverage_IsRejected(string mutation)
    {
        var root = Embedded();
        var rows = root["builds"]!.AsArray();
        switch (mutation)
        {
            case "duplicate": rows.Add(rows[0]!.DeepClone()); break;
            case "overlap": rows[0]!["exclusiveMaximumMinecraftVersion"] = new JsonArray(1, 9); break;
            case "gap": rows[0]!["exclusiveMaximumMinecraftVersion"] = new JsonArray(1, 7, 9); break;
            case "missing-branch": rows.RemoveAt(0); break;
        }
        Assert.Throws<InvalidDataException>(() => Parse(root));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("publisher")]
    [InlineData("sha")]
    [InlineData("path")]
    [InlineData("filename")]
    [InlineData("tag")]
    [InlineData("query")]
    public void UnpinnedOrEscapingArtifact_IsRejected(string mutation)
    {
        var root = Embedded();
        var row = root["builds"]!.AsArray().First(value => value!["version"]!.GetValue<string>() == "0.3.4")!;
        var url = row["downloadUrl"]!.GetValue<string>();
        switch (mutation)
        {
            case "host": row["downloadUrl"] = url.Replace("github.com", "example.org", StringComparison.Ordinal); break;
            case "publisher": row["downloadUrl"] = url.Replace("/MarkZamore/", "/AnotherPublisher/", StringComparison.Ordinal); break;
            case "sha": row["sha256"] = new string('g', 64); break;
            case "path": row["fileName"] = "../e4steam-outside.jar"; break;
            case "filename": row["downloadUrl"] = url[..(url.LastIndexOf('/') + 1)] + "e4steam-other.jar"; break;
            case "tag": row["deliveryTag"] = "unrelated-release"; break;
            case "query": row["downloadUrl"] = url + "?token=unapproved"; break;
        }
        Assert.Throws<InvalidDataException>(() => Parse(root));
    }

    [Fact]
    public async Task ValidNetworkProfile_SelectsNewPinnedBytesAndWritesCheckedCache()
    {
        using var handler = new CatalogHandler(Future().ToJsonString());
        using var client = new HttpClient(handler);
        var service = Service(client);
        var snapshot = await service.LoadAsync(false);
        Assert.Equal(FutureProfile, snapshot.Revision);
        Assert.True(File.Exists(service.CachePath));
        var request = Assert.Single(handler.Requests);
        Assert.True(request.NoCache && request.NoStore);
        Assert.NotEmpty(request.Uri.Query);
        Assert.Equal(RuntimeComponentContext.Current.Artifacts.Count, snapshot.Artifacts.Count);
    }

    [Fact]
    public async Task InvalidNetwork_PreservesPreviouslyCheckedCacheByteForByte()
    {
        using var handler = new CatalogHandler(Future().ToJsonString());
        using var client = new HttpClient(handler);
        var first = Service(client);
        await first.LoadAsync(false);
        var bytes = File.ReadAllBytes(first.CachePath);
        var modified = File.GetLastWriteTimeUtc(first.CachePath);
        var invalid = Future();
        invalid["componentApiVersion"] = 999;
        handler.ResponseJson = invalid.ToJsonString();
        Assert.Equal(FutureProfile, (await Service(client).LoadAsync(true)).Revision);
        Assert.Equal(bytes, File.ReadAllBytes(first.CachePath));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(first.CachePath));
    }

    [Fact]
    public async Task OfflineWithoutCache_RequiresFirstNetworkPreparation()
    {
        using var handler = new CatalogHandler { Offline = true };
        using var client = new HttpClient(handler);
        var service = Service(client);
        await Assert.ThrowsAnyAsync<Exception>(() => service.LoadAsync(false));
        Assert.False(File.Exists(service.CachePath));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("catalog-tamper")]
    [InlineData("history-tamper")]
    [InlineData("null-history-row")]
    public async Task CorruptCache_IsNotTrustedWhenOffline(string corruption)
    {
        using var handler = new CatalogHandler(Future().ToJsonString());
        using var client = new HttpClient(handler);
        var first = Service(client);
        await first.LoadAsync(false);
        if (corruption == "malformed") File.WriteAllText(first.CachePath, "{", Encoding.UTF8);
        else
        {
            var envelope = JsonNode.Parse(File.ReadAllText(first.CachePath))!;
            if (corruption == "catalog-tamper") envelope["catalogJson"] = "{}";
            else if (corruption == "history-tamper") envelope["catalogSha256"] = new string('f', 64);
            else envelope["catalogJson"] = null;
            File.WriteAllText(first.CachePath, envelope.ToJsonString(), Encoding.UTF8);
        }
        var rejected = File.ReadAllBytes(first.CachePath);
        handler.Offline = true;
        await Assert.ThrowsAnyAsync<Exception>(() => Service(client).LoadAsync(false));
        Assert.Equal(rejected, File.ReadAllBytes(first.CachePath));
    }

    [Fact]
    public async Task RepeatedAndConcurrentLoads_ShareOneRequestAndFrozenSnapshot()
    {
        using var handler = new CatalogHandler(Future().ToJsonString());
        using var client = new HttpClient(handler);
        var service = Service(client);
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.LoadAsync(false)));
        Assert.All(snapshots, snapshot => Assert.Equal(FutureProfile, snapshot.Revision));
        Assert.Single(handler.Requests);
        handler.ResponseJson = Published().ToJsonString();
        Assert.Equal(FutureProfile, (await service.LoadAsync(false)).Revision);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OfflineCacheRetainsTheSelectedComponentSet()
    {
        using var handler = new CatalogHandler(Future().ToJsonString());
        using var client = new HttpClient(handler);
        var prepared = await Service(client).LoadAsync(false);
        handler.Offline = true;
        var restored = await Service(client).LoadAsync(false);
        Assert.Equal(prepared.Sha256, restored.Sha256);
        Assert.Equal(prepared.Artifacts, restored.Artifacts);
        Assert.Equal(prepared.JavaCatalogJson, restored.JavaCatalogJson);
    }

    [Fact]
    public async Task UnsupportedApiCannotReplaceACheckedCatalog()
    {
        using var handler = new CatalogHandler(Future().ToJsonString());
        using var client = new HttpClient(handler);
        var service = Service(client);
        var checkedCatalog = await service.LoadAsync(false);
        var bytes = File.ReadAllBytes(service.CachePath);
        var incompatible = Future();
        incompatible["componentApiVersion"] = 2;
        handler.ResponseJson = incompatible.ToJsonString();
        Assert.Equal(checkedCatalog.Sha256, (await Service(client).LoadAsync(true)).Sha256);
        Assert.Equal(bytes, File.ReadAllBytes(service.CachePath));
    }

    private RuntimeComponentService Service(HttpClient client) =>
        new(_paths, _logger, client, launcherRelease: int.MaxValue);

    private static JsonObject Embedded() => JsonNode.Parse(RuntimeComponentContext.Current.SteamCatalogJson)!.AsObject();
    private static SteamComponentCatalogSnapshot Parse(JsonObject root) => SteamComponentCatalog.Parse(root.ToJsonString());
    private static JsonObject Published() => JsonNode.Parse(RuntimeComponentContext.Current.CatalogJson)!.AsObject();
    private static JsonObject Future()
    {
        var root = Published();
        root["revision"] = FutureProfile;
        return root;
    }

    private sealed record Request(Uri Uri, bool NoCache, bool NoStore);
    private sealed class CatalogHandler(string? json = null) : HttpMessageHandler
    {
        public string? ResponseJson { get; set; } = json;
        public bool Offline { get; set; }
        public ConcurrentQueue<Request> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new(request.RequestUri!, request.Headers.CacheControl?.NoCache == true,
                request.Headers.CacheControl?.NoStore == true));
            if (Offline) throw new HttpRequestException("fixture offline");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseJson ?? "{}", Encoding.UTF8, "application/json")
            });
        }
    }
}
