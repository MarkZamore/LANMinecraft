using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PackCatalogAuthorityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "catalog-authority-test-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly Logger _logger;

    public PackCatalogAuthorityTests()
    {
        _paths = new AppPaths(_root);
        _paths.Ensure();
        _logger = new Logger(_paths.LogFile);
    }

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public async Task RemovedEntriesStayRemovedOfflineWhileListedPackKeepsItsVerifiedBundleOnUpdateFailure()
    {
        using var host = new CatalogHost();
        var removed = host.Add("removed-pack", "Previously listed pack", "Removed-Pack");
        var retained = host.Add("retained-pack", "Retained pack", "Retained-Pack");
        host.Index = [removed, retained];
        var initial = new PackCatalogService(_paths, _logger, host.Client);
        await initial.RefreshOnceAsync();
        var retainedManifest = initial.SessionManifest(retained.Source);
        var retainedRevision = initial.Find(retained.RelativePath)!.Content.Revision;
        var jar = Path.Combine(_paths.Packs, removed.RelativePath, "mods", "personal.jar");
        var world = Path.Combine(_paths.Worlds, removed.RelativePath, "World", "level.dat");
        Write(jar, "keep installed mod");
        Write(world, "keep saved world");
        WriteMarker(removed.RelativePath, removed.Source);
        const string customFolder = "My local custom pack";
        WriteMarker(customFolder, retained.Source);

        host.Index = [retained];
        host.UnavailableRepositories.Add(retained.Source.Repo);
        var startup = new PackCatalogService(_paths, _logger, host.Client);
        await startup.RefreshOnceAsync();

        Assert.True(startup.HasOnlineCatalog);
        Assert.Equal(retained, Assert.Single(startup.Sources));
        var card = Assert.Single(startup.Packs);
        Assert.Equal(retained.RelativePath, card.Entry.RelativePath);
        Assert.Equal(retainedRevision, card.Content.Revision);
        Assert.Equal(retainedManifest, startup.SessionManifest(retained.Source));
        Assert.Null(startup.Find(removed.RelativePath));
        Assert.True(startup.IsKnownCatalogPath(removed.RelativePath));
        Assert.True(startup.IsWithdrawn(removed.RelativePath));
        Assert.Null(startup.SourceFor(removed.RelativePath));
        Assert.False(startup.IsKnownCatalogPath(customFolder));
        Assert.False(startup.IsWithdrawn(customFolder));
        Assert.Null(startup.Find(customFolder));
        var sync = new PortablePackSyncService(_paths, _logger, host.Client) { Catalog = startup };
        Assert.Null(sync.TryResolveSource(removed.RelativePath));
        Assert.Equal(retained.Source, sync.TryResolveSource(customFolder));

        host.Offline = true;
        var offline = new PackCatalogService(_paths, _logger, host.Client);
        await offline.RefreshOnceAsync();

        Assert.False(offline.HasOnlineCatalog);
        Assert.Equal(retained, Assert.Single(offline.Sources));
        Assert.Equal(retained.RelativePath, Assert.Single(offline.Packs).Entry.RelativePath);
        Assert.True(offline.IsWithdrawn(removed.RelativePath));
        Assert.Equal(retainedManifest, offline.SessionManifest(retained.Source));
        Assert.Equal("keep installed mod", File.ReadAllText(jar));
        Assert.Equal("keep saved world", File.ReadAllText(world));
    }

    [Fact]
    public async Task SuccessfulEmptyIndexRemovesAllOffersAndDoesNotResurrectEmbeddedEntriesOffline()
    {
        using var host = new CatalogHost();
        var entry = host.Add("temporary-pack", "Temporary pack", "Temporary-Pack");
        host.Index = [entry];
        await new PackCatalogService(_paths, _logger, host.Client).RefreshOnceAsync();
        host.Index = [];
        var startup = new PackCatalogService(_paths, _logger, host.Client);
        await startup.RefreshOnceAsync();

        Assert.True(startup.HasOnlineCatalog);
        Assert.Empty(startup.Sources);
        Assert.Empty(startup.Packs);
        Assert.True(startup.IsWithdrawn(entry.RelativePath));

        host.Offline = true;
        var offline = new PackCatalogService(_paths, _logger, host.Client);
        Assert.Empty(offline.Sources);
        Assert.Empty(offline.Packs);
        await offline.RefreshOnceAsync();
        Assert.Empty(offline.Sources);
        Assert.Empty(offline.Packs);
        Assert.True(offline.IsWithdrawn(entry.RelativePath));
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("unsupported")]
    [InlineData("missing-packs")]
    public async Task MissingOrMalformedIndexKeepsTheLastAuthoritativeMembership(string failure)
    {
        using var host = new CatalogHost();
        var entry = host.Add("kept-pack", "Kept pack", "Kept-Pack");
        host.Index = [entry];
        await new PackCatalogService(_paths, _logger, host.Client).RefreshOnceAsync();
        host.MalformedIndex = failure == "unsupported";
        host.MissingPacks = failure == "missing-packs";
        host.Offline = failure == "offline";
        var startup = new PackCatalogService(_paths, _logger, host.Client);
        await startup.RefreshOnceAsync();

        Assert.False(startup.HasOnlineCatalog);
        Assert.Equal(entry, Assert.Single(startup.Sources));
        Assert.Equal(entry.RelativePath, Assert.Single(startup.Packs).Entry.RelativePath);
        Assert.False(startup.IsWithdrawn(entry.RelativePath));
        Assert.Equal(entry.Source, startup.SourceFor(entry.RelativePath));
    }

    [Fact]
    public async Task ChangedRepositoryBecomesEffectiveOnlyAfterVerificationIncludingAcrossOfflineRestart()
    {
        using var host = new CatalogHost();
        var old = host.Add("stable-pack", "Stable pack folder", "Verified-Old-Repository");
        host.Index = [old];
        var initial = new PackCatalogService(_paths, _logger, host.Client);
        await initial.RefreshOnceAsync();
        var verifiedRevision = initial.Find(old.RelativePath)!.Content.Revision;
        var desired = host.Add(old.Id, old.RelativePath, "Proposed-New-Repository");
        host.Index = [desired];
        host.UnavailableRepositories.Add(desired.Source.Repo);
        var failedUpdate = new PackCatalogService(_paths, _logger, host.Client);
        await failedUpdate.RefreshOnceAsync();

        Assert.Equal(desired, Assert.Single(failedUpdate.Sources));
        Assert.Equal(old.Source, failedUpdate.SourceFor(old.RelativePath));
        Assert.Equal(verifiedRevision, failedUpdate.Find(old.RelativePath)!.Content.Revision);

        host.Offline = true;
        var offline = new PackCatalogService(_paths, _logger, host.Client);
        await offline.RefreshOnceAsync();
        Assert.Equal(desired, Assert.Single(offline.Sources));
        Assert.Equal(old.Source, offline.SourceFor(old.RelativePath));
        Assert.Equal(verifiedRevision, offline.Find(old.RelativePath)!.Content.Revision);
        Assert.Equal(initial.SessionManifest(old.Source), offline.SessionManifest(old.Source));

        host.Offline = false;
        host.UnavailableRepositories.Clear();
        var verifiedUpdate = new PackCatalogService(_paths, _logger, host.Client);
        await verifiedUpdate.RefreshOnceAsync();
        Assert.Equal(desired.Source, verifiedUpdate.SourceFor(old.RelativePath));
        Assert.NotEqual(verifiedRevision, verifiedUpdate.Find(old.RelativePath)!.Content.Revision);
        Assert.Equal(desired.Source, verifiedUpdate.ResolveHistoricalSourceMarker(old.RelativePath, old.Source));
        var explicitFork = new PackSyncSource("AnotherOwner", "Explicit-Fork", "custom-version");
        Assert.Equal(explicitFork, verifiedUpdate.ResolveHistoricalSourceMarker(old.RelativePath, explicitFork));

        host.Offline = true;
        var offlineAfterSuccess = new PackCatalogService(_paths, _logger, host.Client);
        await offlineAfterSuccess.RefreshOnceAsync();
        Assert.Equal(desired.Source, offlineAfterSuccess.ResolveHistoricalSourceMarker(old.RelativePath, old.Source));
        Assert.Equal(explicitFork, offlineAfterSuccess.ResolveHistoricalSourceMarker(old.RelativePath, explicitFork));

        // A different stable ID reusing the folder cannot inherit another
        // pack's old automatic source markers.
        host.Offline = false;
        var replacement = host.Add("different-pack-id", old.RelativePath, "Different-Pack-Repository");
        host.Index = [replacement];
        var differentPack = new PackCatalogService(_paths, _logger, host.Client);
        await differentPack.RefreshOnceAsync();
        Assert.Equal(replacement.Source, differentPack.SourceFor(old.RelativePath));
        Assert.Equal(old.Source, differentPack.ResolveHistoricalSourceMarker(old.RelativePath, old.Source));
    }

    private void WriteMarker(string folder, PackSyncSource source) => Write(
        Path.Combine(_paths.Packs, folder, PortablePackSyncService.SourceMarkerFileName),
        JsonSerializer.Serialize(new { schemaVersion = 1, owner = source.Owner, repo = source.Repo, tag = source.Tag }));

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed class CatalogHost : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
        private readonly Dictionary<string, (byte[] Bundle, object Manifest)> _releases = new(StringComparer.Ordinal);
        private readonly byte[] _preview;
        internal HttpClient Client { get; }
        internal List<CatalogSource> Index { get; set; } = [];
        internal HashSet<string> UnavailableRepositories { get; } = new(StringComparer.Ordinal);
        internal bool Offline, MalformedIndex, MissingPacks;

        internal CatalogHost()
        {
            Client = new HttpClient(this);
            using var seed = typeof(PackCatalogService).Assembly.GetManifestResourceStream("Minecraft.Library.minecraft-26-2.zip")!;
            using var archive = new ZipArchive(seed, ZipArchiveMode.Read);
            using var preview = archive.GetEntry("preview.jpg")!.Open();
            using var output = new MemoryStream();
            preview.CopyTo(output);
            _preview = output.ToArray();
        }

        internal CatalogSource Add(string id, string folder, string repository)
        {
            var entry = new CatalogSource(id, folder, new PackSyncSource("CatalogTest", repository, "pack-latest"));
            var revision = "verified-" + repository;
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                using (var metadata = archive.CreateEntry("library.json").Open())
                    JsonSerializer.Serialize(metadata, new CatalogDescription
                    {
                        SchemaVersion = 1, Id = id, RelativePath = folder, Name = folder,
                        MinecraftVersion = "26.2", Revision = revision, Summary = "Verified pack", Description = "Verified pack content"
                    }, Options);
                using var image = archive.CreateEntry("preview.jpg").Open();
                image.Write(_preview);
            }
            var bundle = output.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bundle));
            byte[] payload = [1];
            var payloadHash = Convert.ToHexStringLower(SHA256.HashData(payload));
            var manifest = new
            {
                schemaVersion = 1, revision, minecraftVersion = "26.2",
                files = new[] { new { path = "mods/payload.jar", sha256 = payloadHash, sizeBytes = payload.Length } },
                assets = new[] { new { name = "payload.jar", kind = "jar", sha256 = payloadHash, sizeBytes = payload.Length, covers = new[] { "mods/payload.jar" } } },
                library = new { name = "library-" + hash + ".zip", sha256 = hash, sizeBytes = bundle.Length, revision }
            };
            _releases[repository] = (bundle, manifest);
            return entry;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Offline) return Reply(HttpStatusCode.ServiceUnavailable, []);
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("catalog.json", StringComparison.Ordinal))
                return MissingPacks ? Json(new { schemaVersion = 1 }) :
                    Json(new { schemaVersion = MalformedIndex ? 99 : 1, packs = Index });
            foreach (var (repository, release) in _releases)
            {
                if (!uri.AbsolutePath.Contains("/" + repository + "/", StringComparison.Ordinal)) continue;
                if (UnavailableRepositories.Contains(repository)) return Reply(HttpStatusCode.ServiceUnavailable, []);
                return uri.AbsolutePath.EndsWith("pack-manifest.json", StringComparison.Ordinal)
                    ? Json(release.Manifest) : Reply(HttpStatusCode.OK, release.Bundle);
            }
            return Reply(HttpStatusCode.NotFound, []);
        }

        private static Task<HttpResponseMessage> Json(object value) => Reply(HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(value, Options));
        private static Task<HttpResponseMessage> Reply(HttpStatusCode status, byte[] bytes) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) });
    }
}
