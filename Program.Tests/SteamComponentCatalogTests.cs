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
    public void EmbeddedV3_PreservesAllReviewedBranchesAndPublisherDelivery()
    {
        var catalog = SteamComponentCatalog.Parse(SteamComponentCatalog.EmbeddedJson());
        Assert.Equal("native-lan-ui-v3", catalog.Profile);
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
        var current = Embedded()["builds"]!.AsArray();
        var historical = JsonNode.Parse(SteamComponentCatalog.HistoricalV2Json())!;
        Assert.Equal(19, historical["builds"]!.AsArray().Count);
        foreach (var old in historical["builds"]!.AsArray())
        {
            old!["profile"] = catalog.Profile;
            var preserved = Assert.Single(current, row => row!["fileName"]!.GetValue<string>() == old["fileName"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(old, preserved), "An existing artifact's bytes or provenance changed.");
        }
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
        var catalog = await service.LoadAsync();

        AssertFuture(catalog);
        Assert.True(File.Exists(service.CachePath));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(SteamComponentCatalogService.IndexUri.GetLeftPart(UriPartial.Path), request.Uri.GetLeftPart(UriPartial.Path));
        Assert.NotEmpty(request.Uri.Query);
        Assert.True(request.NoCache && request.NoStore);
        // A separately constructed service proves this was persisted as a usable,
        // validated catalog rather than merely returning the network object.
        using var offline = new CatalogHandler { Offline = true };
        using var offlineClient = new HttpClient(offline);
        AssertFuture(await Service(offlineClient).LoadAsync());
    }

    [Fact]
    public async Task InvalidNetwork_PreservesPreviouslyCheckedCacheByteForByte()
    {
        using var handler = new CatalogHandler(Future().ToJsonString());
        using var client = new HttpClient(handler);
        var first = Service(client);
        await first.LoadAsync();
        var bytes = File.ReadAllBytes(first.CachePath);
        var modified = File.GetLastWriteTimeUtc(first.CachePath);
        var invalid = Embedded();
        invalid["capabilities"] = new JsonArray();
        handler.ResponseJson = invalid.ToJsonString();

        AssertFuture(await Service(client).LoadAsync());
        Assert.Equal(bytes, File.ReadAllBytes(first.CachePath));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(first.CachePath));
    }

    [Fact]
    public async Task OfflineWithoutCache_UsesEmbeddedV3AndExactLegacyPinWithoutCreatingUnverifiedCache()
    {
        using var handler = new CatalogHandler { Offline = true };
        using var client = new HttpClient(handler);
        var service = Service(client);
        var selected = await service.LoadAsync();
        Assert.Equal("native-lan-ui-v3", selected.Profile);
        Assert.Equal(22, selected.Builds.Count);
        AssertLegacyPin(selected);
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
        await first.LoadAsync();
        if (corruption == "malformed")
            File.WriteAllText(first.CachePath, "{", Encoding.UTF8);
        else
        {
            var envelope = JsonNode.Parse(File.ReadAllText(first.CachePath))!;
            if (corruption == "catalog-tamper")
                envelope["catalog"] = SteamComponentCatalog.EmbeddedJson();
            else if (corruption == "null-history-row")
                envelope["knownBuilds"] = new JsonArray((JsonNode?)null);
            else
                envelope["knownBuilds"]![0]!["sha256"] = new string('f', 64);
            File.WriteAllText(first.CachePath, envelope.ToJsonString(), Encoding.UTF8);
        }
        var corruptedBytes = File.ReadAllBytes(first.CachePath);
        handler.Offline = true;

        var selected = await Service(client).LoadAsync();
        Assert.Equal("native-lan-ui-v3", selected.Profile);
        Assert.Equal(22, selected.Builds.Count);
        AssertLegacyPin(selected);
        Assert.DoesNotContain(selected.Builds, build => build.FileName == FutureFile);
        Assert.Equal(corruptedBytes, File.ReadAllBytes(first.CachePath));
    }

    [Fact]
    public async Task RepeatedAndConcurrentLoads_ShareOneRequestAndFrozenSnapshot()
    {
        using var handler = new CatalogHandler(Future().ToJsonString());
        using var client = new HttpClient(handler);
        var service = Service(client);
        var tasks = Enumerable.Range(0, 8).Select(_ => service.LoadAsync()).ToArray();
        Assert.All(tasks, task => Assert.Same(tasks[0], task));
        var snapshots = await Task.WhenAll(tasks);
        Assert.All(snapshots, snapshot => Assert.Same(snapshots[0], snapshot));
        AssertFuture(snapshots[0]);
        var bytes = File.ReadAllBytes(service.CachePath);

        handler.ResponseJson = SteamComponentCatalog.EmbeddedJson();
        var later = await service.LoadAsync();
        Assert.Same(snapshots[0], later);
        AssertFuture(later);
        Assert.Single(handler.Requests);
        Assert.Equal(bytes, File.ReadAllBytes(service.CachePath));
    }

    private SteamComponentCatalogService Service(HttpClient client) =>
        new(_paths, _logger, client, TimeSpan.FromSeconds(2));

    private static JsonObject Embedded() => JsonNode.Parse(SteamComponentCatalog.EmbeddedJson())!.AsObject();
    private static SteamComponentCatalogSnapshot Parse(JsonObject root) => SteamComponentCatalog.Parse(root.ToJsonString());

    private static JsonObject Future()
    {
        var root = Embedded();
        root.Remove("java8Builds");
        root["profile"] = FutureProfile;
        root["sourceCommit"] = new string('b', 40);
        foreach (var row in root["builds"]!.AsArray())
        {
            row!["profile"] = FutureProfile;
            if (row["derivation"]!["type"]!.GetValue<string>() == "source-built-native-ui-and-lan-online-mode")
                row["derivation"]!["sourceCommit"] = new string('b', 40);
        }
        var changed = root["builds"]!.AsArray().First(value => value!["version"]!.GetValue<string>() == "0.3.4")!;
        changed["fileName"] = FutureFile;
        changed["sizeBytes"] = changed["sizeBytes"]!.GetValue<long>() + 17;
        changed["sha256"] = FutureHash;
        changed["deliveryTag"] = FutureProfile;
        changed["downloadUrl"] = "https://github.com/MarkZamore/e4steam/releases/download/" + FutureProfile + "/" + FutureFile;
        return root;
    }

    private static void AssertFuture(SteamComponentCatalogSnapshot catalog)
    {
        Assert.Equal(FutureProfile, catalog.Profile);
        Assert.Equal(new string('b', 40), catalog.SourceCommit);
        var changed = Assert.Single(catalog.Builds, build => build.FileName == FutureFile);
        Assert.Equal(FutureHash, changed.Sha256);
        Assert.Equal(FutureProfile, changed.DeliveryTag);
        Assert.True(changed.NativeLanUiSuppressed);
        Assert.Equal(22, catalog.Builds.Count);
        Assert.Equal(2, catalog.Builds.Count(build => build.PreservesWorldSettingsOnShare));
    }

    [Fact]
    public async Task HistoricalV2CacheIsPreservedButCannotRemoveLegacySupportWhenOffline()
    {
        using var handler = new CatalogHandler { Offline = true };
        using var client = new HttpClient(handler);
        var service = Service(client);
        var historical = SteamComponentCatalog.HistoricalV2Json();
        Directory.CreateDirectory(Path.GetDirectoryName(service.CachePath)!);
        var envelope = new JsonObject
        {
            ["schemaVersion"] = 1, ["catalog"] = historical,
            ["catalogSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(historical))),
            ["knownBuilds"] = new JsonArray()
        };
        File.WriteAllText(service.CachePath, envelope.ToJsonString());
        var original = File.ReadAllBytes(service.CachePath);
        var selected = await service.LoadAsync();
        Assert.Equal("native-lan-ui-v3", selected.Profile);
        AssertLegacyPin(selected);
        Assert.Equal(original, File.ReadAllBytes(service.CachePath));
        Assert.Contains("removes reviewed Legacy Fabric 1.6.4 coverage", File.ReadAllText(_paths.LogFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkV2DowngradeCannotReplaceACheckedV3Cache()
    {
        using var handler = new CatalogHandler(Future().ToJsonString());
        using var client = new HttpClient(handler);
        var service = Service(client);
        await service.LoadAsync();
        var original = File.ReadAllBytes(service.CachePath);
        handler.ResponseJson = SteamComponentCatalog.HistoricalV2Json();
        var selected = await Service(client).LoadAsync();
        AssertFuture(selected);
        AssertLegacyPin(selected);
        Assert.Equal(original, File.ReadAllBytes(service.CachePath));
        Assert.Contains("removes reviewed Legacy Fabric 1.6.4 coverage", File.ReadAllText(_paths.LogFile), StringComparison.Ordinal);
    }

    private static void AssertLegacyPin(SteamComponentCatalogSnapshot selected)
    {
        var actual = Assert.Single(selected.Builds, build => build.Loaders.Contains(PackLoaderKind.LegacyFabric));
        var expected = Assert.Single(SteamComponentCatalog.Parse(SteamComponentCatalog.EmbeddedJson()).Builds,
            build => build.Loaders.Contains(PackLoaderKind.LegacyFabric));
        Assert.Equal(expected.FileName, actual.FileName);
        Assert.Equal(expected.SizeBytes, actual.SizeBytes);
        Assert.Equal(expected.Sha256, actual.Sha256);
        Assert.Equal(expected.CacheFileId, actual.CacheFileId);
        Assert.Equal(expected.DownloadUris, actual.DownloadUris);
    }

    private sealed record Request(Uri Uri, bool NoCache, bool NoStore);

    private sealed class CatalogHandler(string? json = null) : HttpMessageHandler
    {
        internal string ResponseJson { get; set; } = json ?? "{}";
        internal bool Offline { get; set; }
        internal ConcurrentQueue<Request> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new(request.RequestUri!, request.Headers.CacheControl?.NoCache == true,
                request.Headers.CacheControl?.NoStore == true));
            if (Offline) return Task.FromException<HttpResponseMessage>(new HttpRequestException("Simulated offline catalog."));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseJson, Encoding.UTF8, "application/json")
            });
        }
    }
}
