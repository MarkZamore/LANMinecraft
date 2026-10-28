using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Minecraft.Tests;

public sealed class PackLauncherFeaturesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pack-features-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Payload = "author mod"u8.ToArray();
    private static string Manifest(int schema, string? features = null)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(Payload));
        var node = JsonSerializer.SerializeToNode(new
        {
            schemaVersion = schema, revision = "feature-fixture", minecraftVersion = "1.20.1",
            files = new[] { new { path = "mods/author.jar", sizeBytes = Payload.Length, sha256 = sha } },
            assets = new[] { new { name = "author.jar", kind = "jar", sizeBytes = Payload.Length, sha256 = sha, covers = new[] { "mods/author.jar" } } }
        })!;
        if (features is not null) node["requiredLauncherFeatures"] = JsonNode.Parse(features);
        return node.ToJsonString();
    }
    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public async Task KnownFeaturesAreAcceptedAndUnknownFeaturesRequireAnUpdateBeforeAnyDownload()
    {
        var paths = new AppPaths(_root); paths.Ensure();
        var pack = paths.CombineUnderPacks(PortablePackSyncService.DefaultPackRelativePath);
        PortablePackSyncService.ValidateSnapshot(Manifest(1), pack);
        foreach (var features in new[] { """["local-ai-native-v1"]""", """["curated-mod-list-v1"]""", """["local-ai-native-v1","curated-mod-list-v1"]""" })
            PortablePackSyncService.ValidateSnapshot(Manifest(2, features), pack);

        // A previously installed pack must not convert a capability failure
        // into offline fallback and silently continue with an unsupported pack.
        Directory.CreateDirectory(pack);
        var runtime = Path.Combine(pack, PackManifestService.ManifestFileName);
        File.WriteAllText(runtime, """{"schemaVersion":1}""");
        var unknown = Manifest(2, """["future-required-feature-v1"]""");
        using var handler = new ManifestHandler(unknown);
        using var http = new HttpClient(handler);
        var service = new PortablePackSyncService(paths, new Logger(paths.LogFile), http);
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => service.SyncAsync(
            PortablePackSyncService.DefaultPackRelativePath, null, default));
        Assert.Contains("Обновите лаунчер", error.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
        Assert.EndsWith("/pack-manifest.json", handler.Requests[0].AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("""{"schemaVersion":1}""", File.ReadAllText(runtime));
        Assert.False(Directory.Exists(Path.Combine(pack, "mods")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""[""]""")]
    [InlineData("""[" "]""")]
    [InlineData("[null]")]
    [InlineData("[1]")]
    [InlineData("\"local-ai-native-v1\"")]
    [InlineData("""["local-ai-native-v1","local-ai-native-v1"]""")]
    [InlineData("""["LOCAL-AI-NATIVE-V1"]""")]
    [InlineData("""["curated-mod-list-v1","unknown-v1"]""")]
    public void InvalidFeatureDeclarationsAreRejectedWithAnActionableUpdateMessage(string? features)
    {
        var error = Assert.Throws<NotSupportedException>(() => PortablePackSyncService.ValidateSnapshot(Manifest(2, features), _root));
        Assert.Contains("Обновите лаунчер", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FeaturesRequireVersionTwoAndUnknownFutureFormatsAreRejected()
    {
        foreach (var manifest in new[] { Manifest(1, """["local-ai-native-v1"]"""), Manifest(3, """["local-ai-native-v1"]""") })
        {
            var error = Assert.Throws<NotSupportedException>(() => PortablePackSyncService.ValidateSnapshot(manifest, _root));
            Assert.Contains("Обновите лаунчер", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CatalogueUsesTheSameGateAndKeepsOnlyItsPreviouslyVerifiedManifest()
    {
        var paths = new AppPaths(_root); paths.Ensure();
        var logger = new Logger(paths.LogFile);
        var source = new PackSyncSource("author", "Feature-Pack", "pack-latest");
        var known = Manifest(2, """["local-ai-native-v1","curated-mod-list-v1"]""");
        using var handler = new ManifestHandler(known, source);
        using var http = new HttpClient(handler);
        var catalogue = new PackCatalogService(paths, logger, http);
        await catalogue.RefreshOnceAsync();
        Assert.Equal(known, catalogue.SessionManifest(source));

        handler.Manifest = Manifest(2, """["future-required-feature-v1"]""");
        var nextStartup = new PackCatalogService(paths, logger, http);
        await nextStartup.RefreshOnceAsync();
        Assert.Equal(known, nextStartup.SessionManifest(source));
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsolutePath.EndsWith("author.jar", StringComparison.Ordinal));
        Assert.Contains("Обновите лаунчер", File.ReadAllText(paths.LogFile), StringComparison.Ordinal);
    }

    private sealed class ManifestHandler(string manifest, PackSyncSource? catalogueSource = null) : HttpMessageHandler
    {
        public string Manifest { get; set; } = manifest;
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!);
            if (request.RequestUri!.AbsoluteUri == PackCatalogService.IndexUri.AbsoluteUri && catalogueSource is not null)
                return Task.FromResult(Json(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    packs = new[] { new CatalogSource("feature-test", "Feature Pack", catalogueSource) }
                })));
            return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("pack-manifest.json", StringComparison.Ordinal)
                ? Json(Manifest) : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }
}
