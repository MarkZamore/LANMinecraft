using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class CatalogSourceMarkerTests : IDisposable
{
    private const string Folder = "Published pack folder";
    private const string PackId = "published-pack";
    private static readonly PackSyncSource OldSource = new("OriginalPublisher", "Original-Repository", "pack-latest");
    private static readonly PackSyncSource NewSource = new("CurrentPublisher", "Renamed-Repository", "pack-latest");
    private static readonly PackSyncSource ForkSource = new("PlayerFork", "Explicit-Fork", "custom");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "catalog-marker-" + Guid.NewGuid().ToString("N")));
    private Logger Logger => new(_paths.LogFile);
    private string Marker => Path.Combine(_paths.Packs, Folder, PortablePackSyncService.SourceMarkerFileName);

    public void Dispose() => TempTree.Delete(_paths.Root);

    [Fact]
    public async Task AutomaticOldMarkerFollowsOnlyVerifiedSourceAndSurvivesOfflineRestartBeforeSync()
    {
        _paths.Ensure();
        using var host = new Host();
        var initial = new PackCatalogService(_paths, Logger, host.Client);
        await initial.RefreshOnceAsync();
        var install = await Sync(initial, host).SyncAsync(Folder, null, default);
        Assert.Equal(PackSyncOutcome.Installed, install.Outcome);
        Assert.Equal(OldSource, ReadMarker());

        host.Source = NewSource;
        host.NewUnavailable = true;
        var failed = new PackCatalogService(_paths, Logger, host.Client);
        await failed.RefreshOnceAsync();
        Assert.Equal(OldSource, Sync(failed, host).TryResolveSource(Folder));
        Assert.Equal(OldSource, ReadMarker());

        host.NewUnavailable = false;
        var verified = new PackCatalogService(_paths, Logger, host.Client);
        await verified.RefreshOnceAsync();
        Assert.Equal(NewSource, Sync(verified, host).TryResolveSource(Folder));
        Assert.Equal(OldSource, ReadMarker());

        // No sync has rewritten the on-disk marker yet. The persisted proof
        // must still select the verified replacement on a later offline start.
        host.Offline = true;
        var offline = new PackCatalogService(_paths, Logger, host.Client);
        await offline.RefreshOnceAsync();
        Assert.False(offline.HasOnlineCatalog);
        Assert.Equal(NewSource, Sync(offline, host).TryResolveSource(Folder));
        Assert.Equal(verified.SessionManifest(NewSource), offline.SessionManifest(NewSource));
        Assert.Equal(OldSource, ReadMarker());

        host.Offline = false;
        var update = await Sync(verified, host).SyncAsync(Folder, null, default);
        Assert.Equal(PackSyncOutcome.Updated, update.Outcome);
        Assert.Equal(NewSource, ReadMarker());
        Assert.Equal(NewSource.Repo, File.ReadAllText(Path.Combine(_paths.Packs, Folder, "mods", "payload.jar")));
        Assert.DoesNotContain(host.PayloadSources, source => source == ForkSource);
    }

    [Fact]
    public async Task ExplicitUnrelatedForkMarkerKeepsPrecedenceAcrossCatalogRenameAndOfflineRestart()
    {
        _paths.Ensure();
        using var host = new Host();
        var original = new PackCatalogService(_paths, Logger, host.Client);
        await original.RefreshOnceAsync();
        Assert.Equal(PackSyncOutcome.Installed, (await Sync(original, host).SyncAsync(Folder, null, default)).Outcome);
        WriteMarker(ForkSource);

        host.Source = NewSource;
        var renamed = new PackCatalogService(_paths, Logger, host.Client);
        await renamed.RefreshOnceAsync();
        Assert.Equal(NewSource, renamed.SourceFor(Folder));
        Assert.Equal(ForkSource, Sync(renamed, host).TryResolveSource(Folder));
        Assert.Equal(PackSyncOutcome.Updated, (await Sync(renamed, host).SyncAsync(Folder, null, default)).Outcome);
        Assert.Equal(ForkSource, ReadMarker());
        Assert.Equal(ForkSource.Repo, File.ReadAllText(Path.Combine(_paths.Packs, Folder, "mods", "payload.jar")));

        host.Offline = true;
        var offline = new PackCatalogService(_paths, Logger, host.Client);
        await offline.RefreshOnceAsync();
        Assert.Equal(NewSource, offline.SourceFor(Folder));
        Assert.Equal(ForkSource, Sync(offline, host).TryResolveSource(Folder));
        Assert.Equal(PackSyncOutcome.UpToDate, (await Sync(offline, host).SyncAsync(Folder, null, default)).Outcome);
    }

    private PortablePackSyncService Sync(PackCatalogService catalog, Host host) => new(_paths, Logger, host.Client) { Catalog = catalog };
    private PackSyncSource ReadMarker() => JsonSerializer.Deserialize<PackSyncSource>(File.ReadAllText(Marker), Options)!;
    private void WriteMarker(PackSyncSource source) => File.WriteAllText(Marker, JsonSerializer.Serialize(new
        { schemaVersion = 1, owner = source.Owner, repo = source.Repo, tag = source.Tag }, Options));

    private sealed class Host : HttpMessageHandler
    {
        private readonly Dictionary<PackSyncSource, Dictionary<string, byte[]>> _releases = [];
        public HttpClient Client { get; }
        public PackSyncSource Source { get; set; } = OldSource;
        public bool Offline { get; set; }
        public bool NewUnavailable { get; set; }
        public List<PackSyncSource> PayloadSources { get; } = [];

        public Host()
        {
            Client = new HttpClient(this);
            foreach (var source in new[] { OldSource, NewSource, ForkSource }) _releases[source] = Release(source);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (Offline) return Reply(HttpStatusCode.ServiceUnavailable, []);
            if (uri == PackCatalogService.IndexUri)
                return Reply(HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 1, packs = new[] { new CatalogSource(PackId, Folder, Source) }
                }, Options));
            foreach (var (source, release) in _releases)
            {
                if (!uri.AbsolutePath.StartsWith("/" + source.Owner + "/" + source.Repo + "/", StringComparison.Ordinal)) continue;
                if (source == NewSource && NewUnavailable) return Reply(HttpStatusCode.ServiceUnavailable, []);
                var name = Uri.UnescapeDataString(uri.Segments[^1]);
                if (!release.TryGetValue(name, out var bytes)) return Reply(HttpStatusCode.NotFound, []);
                if (name == "payload.jar") PayloadSources.Add(source);
                return Reply(HttpStatusCode.OK, bytes);
            }
            return Reply(HttpStatusCode.NotFound, []);
        }

        private static Dictionary<string, byte[]> Release(PackSyncSource source)
        {
            var payload = Encoding.UTF8.GetBytes(source.Repo);
            var runtime = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"minecraftVersion":"26.2","loader":{"type":"vanilla"}}""");
            using var buffer = new MemoryStream();
            using (var seed = typeof(PackCatalogService).Assembly.GetManifestResourceStream("Minecraft.Library.minecraft-26-2.zip")!)
            using (var original = new ZipArchive(seed))
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                using (var metadata = archive.CreateEntry("library.json").Open())
                    JsonSerializer.Serialize(metadata, new CatalogDescription
                    {
                        SchemaVersion = 1, Id = PackId, RelativePath = Folder, Name = Folder,
                        MinecraftVersion = "26.2", Revision = source.Repo, Summary = "Fixture", Description = "Fixture"
                    }, Options);
                using var preview = original.GetEntry("preview.jpg")!.Open();
                using var output = archive.CreateEntry("preview.jpg").Open();
                preview.CopyTo(output);
            }
            var bundle = buffer.ToArray();
            var libraryName = "library-" + Hash(bundle) + ".zip";
            var files = new[] { (Path: "mods/payload.jar", Name: "payload.jar", Bytes: payload),
                (Path: "portable-pack.json", Name: "runtime.json", Bytes: runtime) };
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, revision = source.Repo, minecraftVersion = "26.2",
                files = files.Select(file => new { path = file.Path, sha256 = Hash(file.Bytes), sizeBytes = file.Bytes.Length }),
                assets = files.Select(file => new { name = file.Name, kind = "jar", sha256 = Hash(file.Bytes), sizeBytes = file.Bytes.Length, covers = new[] { file.Path } }),
                library = new { name = libraryName, sha256 = Hash(bundle), sizeBytes = bundle.Length, revision = source.Repo }
            }, Options);
            return new Dictionary<string, byte[]> { ["pack-manifest.json"] = manifest, [libraryName] = bundle,
                ["payload.jar"] = payload, ["runtime.json"] = runtime };
        }

        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        private static Task<HttpResponseMessage> Reply(HttpStatusCode status, byte[] bytes) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) });
    }
}
