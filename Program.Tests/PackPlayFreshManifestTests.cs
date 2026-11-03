using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PackPlayFreshManifestTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "play-manifest-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths paths;
    private readonly Logger logger;
    public PackPlayFreshManifestTests()
    {
        paths = new(root);
        paths.Ensure();
        logger = new(paths.LogFile);
    }
    public void Dispose() => TempTree.Delete(root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayFindsR2WithoutRestartAndRetiresTheManagedR1Jar(bool customAlias)
    {
        using var host = new MutableRelease();
        var (catalog, sync, folder) = await InstalledR1(host, customAlias);
        var directory = paths.CombineUnderPacks(folder);
        var before = host.ManifestRequests;
        host.Publish("r2");
        host.EmptyIndex = true; // Membership changes are still a next-startup concern.

        var result = await sync.SyncAsync(folder, null, default);

        Assert.Equal(PackSyncOutcome.Updated, result.Outcome);
        Assert.Equal("r2", result.Revision);
        Assert.Equal(before + 1, host.ManifestRequests);
        Assert.False(File.Exists(Path.Combine(directory, "mods", "removed.jar")));
        Assert.Equal("r2", File.ReadAllText(Path.Combine(directory, "launcher", "revision.txt")));
        Assert.Contains("r1", catalog.SessionManifest(host.Entry.Source), StringComparison.Ordinal);
        Assert.Equal(host.Entry, Assert.Single(catalog.Sources));
        Assert.Equal(1, host.IndexRequests);
        Assert.Equal(host.Entry.Source, sync.TryResolveSource(folder));
        Assert.All(host.ManifestHeaders, headers => Assert.True(headers));
        Assert.All(host.ManifestQueries, query => Assert.NotEmpty(query));
    }

    [Fact]
    public async Task OneFreshPlanPinsMeasureMetadataAndApplyEvenWhenR3Appears()
    {
        using var host = new MutableRelease();
        var (_, sync, folder) = await InstalledR1(host);
        host.Publish("r2");
        var before = host.ManifestRequests;
        using var plan = await sync.PrepareDownloadPlanAsync(folder, null, default);
        Assert.Equal(before + 1, host.ManifestRequests);
        Assert.Equal("r2", File.ReadAllText(Path.Combine(plan.MetadataDirectory, "launcher", "revision.txt")));
        Assert.Equal(MutableRelease.Sha(Encoding.UTF8.GetBytes("r2")), plan.ExpectedFileSha256("launcher/revision.txt"));
        Assert.Equal(0, plan.PendingBytes); // The only changed bytes were verified launcher metadata.
        host.Publish("r3");

        var result = await sync.SyncAsync(plan, null, default);

        Assert.Equal("r2", result.Revision);
        Assert.Equal(before + 1, host.ManifestRequests);
        Assert.Equal("r2", File.ReadAllText(Path.Combine(paths.CombineUnderPacks(folder), "launcher", "revision.txt")));
        Assert.False(File.Exists(Path.Combine(paths.CombineUnderPacks(folder), "mods", "removed.jar")));
    }

    [Fact]
    public async Task FailedOnlineCheckUsesTheCoherentInstalledPackWithoutReplayingStartup()
    {
        using var host = new MutableRelease();
        var (_, sync, folder) = await InstalledR1(host);
        var removed = Path.Combine(paths.CombineUnderPacks(folder), "mods", "removed.jar");
        var time = File.GetLastWriteTimeUtc(removed);
        var before = host.ManifestRequests;
        host.Publish("r2");
        host.OfflineManifests = true;

        var result = await sync.SyncAsync(folder, null, default);

        Assert.Equal(PackSyncOutcome.OfflineFallback, result.Outcome);
        Assert.NotNull(result.Warning);
        Assert.True(host.ManifestRequests > before);
        Assert.Equal("managed-r1", File.ReadAllText(removed));
        Assert.Equal(time, File.GetLastWriteTimeUtc(removed));
        Assert.Equal("r1", StateRevision(folder));
    }

    [Fact]
    public async Task CancellationDuringTheFreshRequestNeverFallsBackOrMutatesInstalledFiles()
    {
        using var host = new MutableRelease();
        var (_, sync, folder) = await InstalledR1(host);
        host.BlockManifests = true;
        using var cancelled = new CancellationTokenSource();
        var pending = sync.PrepareDownloadPlanAsync(folder, null, cancelled.Token);
        await host.ManifestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("r1", StateRevision(folder));
        Assert.True(File.Exists(Path.Combine(paths.CombineUnderPacks(folder), "mods", "removed.jar")));
    }

    [Fact]
    public async Task UnsupportedFreshRequiredFeatureRefusesTheInstalledStartupVersion()
    {
        using var host = new MutableRelease();
        var (_, sync, folder) = await InstalledR1(host);
        host.Publish("r2");
        host.UnsupportedFeature = true;
        var payloadRequests = host.PayloadRequests;

        await Assert.ThrowsAsync<NotSupportedException>(() => sync.PrepareDownloadPlanAsync(folder, null, default));

        Assert.Equal(payloadRequests, host.PayloadRequests);
        Assert.Equal("r1", StateRevision(folder));
        Assert.True(File.Exists(Path.Combine(paths.CombineUnderPacks(folder), "mods", "removed.jar")));
    }

    [Fact]
    public async Task ChunkedOversizeManifestKeepsTheInstalledVersionWithoutDownloadingPayloads()
    {
        using var host = new MutableRelease();
        var (_, sync, folder) = await InstalledR1(host);
        host.Publish("r2");
        host.OversizeManifests = true;
        var payloads = host.PayloadRequests;

        var result = await sync.SyncAsync(folder, null, default);

        Assert.Equal(PackSyncOutcome.OfflineFallback, result.Outcome);
        Assert.NotNull(result.Warning);
        Assert.True(host.OversizeReadBytes <= 3L * (8 * 1024 * 1024 + 64 * 1024));
        Assert.True(host.OversizeReadBytes > 8 * 1024 * 1024);
        Assert.Equal(payloads, host.PayloadRequests);
        Assert.Equal("r1", StateRevision(folder));
        Assert.True(File.Exists(Path.Combine(paths.CombineUnderPacks(folder), "mods", "removed.jar")));
    }

    private string? StateRevision(string folder)
    {
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(paths.CombineUnderPacks(folder), PortablePackSyncService.SyncStateFileName)));
        return state.RootElement.GetProperty("revision").GetString();
    }

    private async Task<(PackCatalogService Catalog, PortablePackSyncService Sync, string Folder)> InstalledR1(MutableRelease host, bool alias = false)
    {
        var folder = alias ? "A user source alias" : host.Entry.RelativePath;
        if (alias)
        {
            var directory = paths.CombineUnderPacks(folder);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, PortablePackSyncService.SourceMarkerFileName),
                JsonSerializer.Serialize(new { schemaVersion = 1, owner = host.Entry.Source.Owner, repo = host.Entry.Source.Repo, tag = host.Entry.Source.Tag }));
        }
        var catalog = new PackCatalogService(paths, logger, host.Client, stagedStartup: true);
        await catalog.InitializeIndexAsync();
        await catalog.EnsureLaunchReadyAsync(folder);
        var sync = new PortablePackSyncService(paths, logger, host.Client) { Catalog = catalog };
        Assert.Equal(PackSyncOutcome.Installed, (await sync.SyncAsync(folder, null, default)).Outcome);
        return (catalog, sync, folder);
    }

    private sealed class MutableRelease : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> assets = new(StringComparer.Ordinal);
        internal readonly CatalogSource Entry = new("mutable-fixture", "A published pack", new("FixtureOwner", "FixtureRepo", "rolling"));
        internal HttpClient Client { get; }
        internal string Revision = "r1";
        internal int ManifestRequests, IndexRequests, PayloadRequests;
        internal bool EmptyIndex, OfflineManifests, BlockManifests, UnsupportedFeature, OversizeManifests;
        internal long OversizeReadBytes;
        internal TaskCompletionSource ManifestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<bool> ManifestHeaders { get; } = [];
        internal List<string> ManifestQueries { get; } = [];
        internal MutableRelease() => Client = new HttpClient(this);
        internal void Publish(string revision) => Revision = revision;
        internal static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/catalog.json", StringComparison.Ordinal))
            {
                IndexRequests++;
                return Reply(JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, packs = EmptyIndex ? Array.Empty<CatalogSource>() : [Entry] }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            var name = Uri.UnescapeDataString(uri.Segments[^1]);
            if (name == "pack-manifest.json")
            {
                ManifestRequests++;
                ManifestHeaders.Add(request.Headers.CacheControl?.NoCache == true && request.Headers.CacheControl?.NoStore == true);
                ManifestQueries.Add(uri.Query);
                if (BlockManifests)
                {
                    ManifestStarted.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
                if (OfflineManifests) return new(HttpStatusCode.ServiceUnavailable);
                if (OversizeManifests)
                {
                    var oversizedBytes = new byte[8 * 1024 * 1024 + 1024 * 1024];
                    Array.Fill(oversizedBytes, (byte)' ');
                    Manifest().CopyTo(oversizedBytes, 0); // Valid JSON with oversized trailing whitespace.
                    var content = new StreamContent(new NonSeekableBytes(oversizedBytes, read => OversizeReadBytes += read));
                    Assert.Null(content.Headers.ContentLength);
                    return new(HttpStatusCode.OK) { Content = content };
                }
                return Reply(Manifest());
            }
            PayloadRequests++;
            return assets.TryGetValue(name, out var bytes) ? Reply(bytes) : new(HttpStatusCode.NotFound);
        }

        private byte[] Manifest()
        {
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["mods/kept.jar"] = "unchanged"u8.ToArray(),
                ["portable-pack.json"] = "{\"schemaVersion\":1}"u8.ToArray(),
                ["launcher/revision.txt"] = Encoding.UTF8.GetBytes(Revision)
            };
            if (Revision == "r1") files["mods/removed.jar"] = "managed-r1"u8.ToArray();
            var descriptors = files.Select(row => new { path = row.Key, sizeBytes = row.Value.LongLength, sha256 = Sha(row.Value) }).ToArray();
            var payloads = files.Select(row =>
            {
                var hash = Sha(row.Value);
                var name = "content-" + hash + ".jar";
                assets[name] = row.Value;
                return new { name, kind = "jar", sizeBytes = row.Value.LongLength, sha256 = hash, covers = new[] { row.Key } };
            }).ToArray();
            var manifest = new Dictionary<string, object?>
            {
                ["schemaVersion"] = UnsupportedFeature ? 2 : 1,
                ["revision"] = Revision, ["minecraftVersion"] = "1.20.1",
                ["files"] = descriptors, ["assets"] = payloads
            };
            if (UnsupportedFeature) manifest["requiredLauncherFeatures"] = new[] { "future-required-fixture-v999" };
            return JsonSerializer.SerializeToUtf8Bytes(manifest);
        }
        private static HttpResponseMessage Reply(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }

    private sealed class NonSeekableBytes(byte[] bytes, Action<int> observedRead) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = base.ReadAsync(buffer, cancellationToken);
            if (!result.IsCompletedSuccessfully) throw new InvalidOperationException("In-memory fixture read unexpectedly deferred.");
            observedRead(result.Result);
            return result;
        }
    }
}
