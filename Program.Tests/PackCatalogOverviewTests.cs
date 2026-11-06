using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Minecraft.Tests;

public sealed class PackCatalogOverviewTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "catalog-overview-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly Logger _logger;

    public PackCatalogOverviewTests()
    {
        _paths = new AppPaths(_root);
        _paths.Ensure();
        _logger = new Logger(_paths.LogFile);
    }

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public async Task CheckedCachePublishesTogetherThenEachFreshCardUpdatesWithoutWaitingForOtherRequests()
    {
        using var host = new OverviewHost();
        var first = host.Add("first", "First pack", "First-Pack");
        var second = host.Add("second", "Second pack", "Second-Pack");
        host.Index = [first, second];
        await new PackCatalogService(_paths, _logger, host.Client).RefreshOnceAsync().WaitAsync(Deadline);
        host.Publish(first, "two");
        var firstGate = host.BlockManifest(first);
        var secondGate = host.BlockManifest(second);
        var catalog = Staged(host);
        await catalog.InitializeIndexAsync().WaitAsync(Deadline);
        var publications = Observe(catalog);
        var preparing = catalog.PrepareLibraryAsync();
        try
        {
            await Task.WhenAll(firstGate.Requested.Task, secondGate.Requested.Task).WaitAsync(Deadline);
            Assert.True(catalog.IsOverviewReady);
            Assert.False(catalog.IsReady);
            Assert.False(preparing.IsCompleted);
            var old = catalog.Packs;
            Assert.Equal(new[] { "first", "second" }, old.Select(pack => pack.Entry.Id));
            Assert.All(old, pack => Assert.Equal("one", pack.Content.Revision));
            var retained = old.Single(pack => pack.Entry.Id == "second");
            retained.Mods[0].IsExpanded = true;
            var readOnly = Assert.IsAssignableFrom<IList<CatalogPack>>(old);
            Assert.True(readOnly.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => readOnly.Clear());

            firstGate.Release.TrySetResult();
            await UntilAsync(() => catalog.Packs.Single(pack => pack.Entry.Id == "first").Content.Revision == "two" && publications.Count == 2);
            Assert.NotSame(old, catalog.Packs);
            Assert.Equal("two", catalog.PacksForDisplay(new AppSettings()).Single(pack => pack.Entry.Id == "first").Content.Revision);
            Assert.Same(retained, catalog.Packs.Single(pack => pack.Entry.Id == "second"));
            Assert.True(retained.Mods[0].IsExpanded);
            Assert.False(secondGate.Release.Task.IsCompleted);
            Assert.False(preparing.IsCompleted);
            Assert.False(catalog.IsReady);

            secondGate.Release.TrySetResult();
            await preparing.WaitAsync(Deadline);
            Assert.True(catalog.IsOverviewReady);
            Assert.True(catalog.IsReady);
            Assert.Equal("two", catalog.Packs.Single(pack => pack.Entry.Id == "first").Content.Revision);
            Assert.Same(retained, catalog.Packs.Single(pack => pack.Entry.Id == "second"));
            Assert.True(retained.Mods[0].IsExpanded);
            Assert.All(old, pack => Assert.Equal("one", pack.Content.Revision));
            Assert.Equal(2, publications.Count);
            Assert.All(publications, batch => Assert.Equal(2, batch.Length));
        }
        finally
        {
            firstGate.Release.TrySetResult(); secondGate.Release.TrySetResult();
            await preparing.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task CancellingAfterAFreshCardKeepsItsVerifiedReplacementAndTheOtherCachedCards()
    {
        using var host = new OverviewHost();
        var first = host.Add("first", "First pack", "First-Pack");
        var second = host.Add("second", "Second pack", "Second-Pack");
        host.Index = [first, second];
        await new PackCatalogService(_paths, _logger, host.Client).RefreshOnceAsync().WaitAsync(Deadline);
        host.Publish(first, "two");
        var firstGate = host.BlockManifest(first);
        var secondGate = host.BlockManifest(second);
        var catalog = Staged(host);
        await catalog.InitializeIndexAsync().WaitAsync(Deadline);
        var publications = Observe(catalog);
        using var cancellation = new CancellationTokenSource();
        var preparing = catalog.PrepareLibraryAsync(cancellation.Token);
        try
        {
            await Task.WhenAll(firstGate.Requested.Task, secondGate.Requested.Task).WaitAsync(Deadline);
            var retained = catalog.Packs.Single(pack => pack.Entry.Id == "second");
            retained.Mods[0].IsExpanded = true;
            firstGate.Release.TrySetResult();
            await UntilAsync(() => catalog.Packs.Single(pack => pack.Entry.Id == "first").Content.Revision == "two" && publications.Count == 2);
            var verified = catalog.Packs.Single(pack => pack.Entry.Id == "first");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing.WaitAsync(Deadline));
            Assert.False(catalog.IsReady);
            Assert.True(catalog.IsOverviewReady);
            Assert.Equal(2, catalog.Packs.Count);
            Assert.Same(verified, catalog.Packs.Single(pack => pack.Entry.Id == "first"));
            Assert.Same(retained, catalog.Packs.Single(pack => pack.Entry.Id == "second"));
            Assert.True(retained.Mods[0].IsExpanded);
            Assert.Equal(2, publications.Count);
            Assert.False(secondGate.Release.Task.IsCompleted);
        }
        finally
        {
            cancellation.Cancel(); firstGate.Release.TrySetResult(); secondGate.Release.TrySetResult();
            try { await preparing.WaitAsync(Deadline); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task UnknownCardsStayHiddenUntilEveryAttemptCompletesWithoutBlockingSelectedLaunch()
    {
        using var host = new OverviewHost();
        var first = host.Add("first", "First pack", "First-Pack");
        var second = host.Add("second", "Second pack", "Second-Pack");
        host.Index = [first, second];
        var firstGate = host.BlockBundle(first);
        var secondGate = host.BlockBundle(second);
        var catalog = Staged(host);
        await catalog.InitializeIndexAsync().WaitAsync(Deadline);
        var publications = Observe(catalog);
        var preparing = catalog.PrepareLibraryAsync();
        try
        {
            await Task.WhenAll(firstGate.Requested.Task, secondGate.Requested.Task).WaitAsync(Deadline);
            await catalog.EnsureLaunchReadyAsync(first.RelativePath).WaitAsync(Deadline);
            Assert.True(catalog.IsLaunchReady(first.RelativePath));
            Assert.False(catalog.IsOverviewReady);
            Assert.Empty(catalog.Packs);

            firstGate.Release.TrySetResult();
            await UntilAsync(() => catalog.Find(first.RelativePath) is not null);
            Assert.Equal("First pack", catalog.GetDisplayName(first.RelativePath));
            Assert.False(catalog.IsOverviewReady);
            Assert.Empty(catalog.Packs);
            Assert.Empty(catalog.PacksForDisplay(new AppSettings()));
            Assert.Empty(publications);

            secondGate.Release.TrySetResult();
            await preparing.WaitAsync(Deadline);
            Assert.True(catalog.IsOverviewReady);
            Assert.Equal(2, catalog.Packs.Count);
            Assert.Equal(2, Assert.Single(publications).Length);
        }
        finally
        {
            firstGate.Release.TrySetResult(); secondGate.Release.TrySetResult();
            await preparing.WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthoritativeRemovalOrEmptyIndexNeverRepublishesOldCardsOrDeletesInstalledData(bool empty)
    {
        using var host = new OverviewHost();
        var removed = host.Add("removed", "Removed pack", "Removed-Pack");
        var retained = host.Add("retained", "Retained pack", "Retained-Pack");
        host.Index = [removed, retained];
        await new PackCatalogService(_paths, _logger, host.Client).RefreshOnceAsync().WaitAsync(Deadline);
        var installed = Path.Combine(_paths.Packs, removed.RelativePath, "mods", "personal.jar");
        var world = Path.Combine(_paths.Worlds, removed.RelativePath, "World", "level.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        Directory.CreateDirectory(Path.GetDirectoryName(world)!);
        File.WriteAllText(installed, "installed bytes"); File.WriteAllText(world, "world bytes");
        host.Index = empty ? [] : [retained];

        var catalog = Staged(host);
        await catalog.InitializeIndexAsync().WaitAsync(Deadline);
        Assert.True(catalog.IsWithdrawn(removed.RelativePath));
        Assert.DoesNotContain(catalog.Packs, pack => pack.Entry.Id == removed.Id);
        if (empty) { Assert.True(catalog.IsOverviewReady); Assert.Empty(catalog.Packs); }
        await catalog.PrepareLibraryAsync().WaitAsync(Deadline);
        Assert.Equal(empty ? 0 : 1, catalog.Packs.Count);
        Assert.DoesNotContain(catalog.Packs, pack => pack.Entry.Id == removed.Id);

        host.Offline = true;
        var offline = Staged(host);
        await offline.InitializeIndexAsync().WaitAsync(Deadline);
        await offline.PrepareLibraryAsync().WaitAsync(Deadline);
        Assert.True(offline.IsOverviewReady);
        Assert.True(offline.IsWithdrawn(removed.RelativePath));
        Assert.Equal(empty ? 0 : 1, offline.Packs.Count);
        Assert.DoesNotContain(offline.Packs, pack => pack.Entry.Id == removed.Id);
        await Assert.ThrowsAnyAsync<IOException>(() => offline.EnsureLaunchReadyAsync(removed.RelativePath));
        Assert.Equal("installed bytes", File.ReadAllText(installed));
        Assert.Equal("world bytes", File.ReadAllText(world));
    }

    [Fact]
    public async Task DamagedCachedBundleCannotPublishAnIncompleteOverviewBeforeFreshAttemptsFinish()
    {
        using var host = new OverviewHost();
        var first = host.Add("first", "First pack", "First-Pack");
        var second = host.Add("second", "Second pack", "Second-Pack");
        host.Index = [first, second];
        await new PackCatalogService(_paths, _logger, host.Client).RefreshOnceAsync().WaitAsync(Deadline);
        var cachePath = Directory.GetFiles(Path.Combine(_paths.Launcher, "Library"), "*.json")
            .Single(path => Path.GetFileName(path) != "catalog.json" &&
                JsonNode.Parse(File.ReadAllText(path))?["entry"]?["id"]?.GetValue<string>() == first.Id);
        var cache = JsonNode.Parse(File.ReadAllText(cachePath))!;
        cache["bundle"] = Convert.ToBase64String([1, 2, 3]);
        File.WriteAllText(cachePath, cache.ToJsonString());
        host.CorruptBundle(first);
        var gate = host.BlockBundle(first);
        var catalog = Staged(host);
        await catalog.InitializeIndexAsync().WaitAsync(Deadline);
        var publications = Observe(catalog);
        var preparing = catalog.PrepareLibraryAsync();
        try
        {
            await gate.Requested.Task.WaitAsync(Deadline);
            Assert.NotNull(catalog.Find(second.RelativePath));
            Assert.Null(catalog.Find(first.RelativePath));
            Assert.False(catalog.IsOverviewReady);
            Assert.Empty(catalog.Packs);
            Assert.Empty(publications);

            gate.Release.TrySetResult();
            await preparing.WaitAsync(Deadline);
            Assert.True(catalog.IsOverviewReady);
            Assert.Equal(second.Id, Assert.Single(catalog.Packs).Entry.Id);
            Assert.Single(Assert.Single(publications));
        }
        finally { gate.Release.TrySetResult(); await preparing.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task EmbeddedOverviewPublishesAllCardsBeforeFreshMetadataReturns()
    {
        using var host = new OverviewHost();
        host.Index = PackCatalogService.SeedSources().Where(entry => typeof(PackCatalogService).Assembly
            .GetManifestResourceInfo("Minecraft.Library." + entry.StorageId + ".zip") is not null).Take(2).ToArray();
        Assert.Equal(2, host.Index.Length);
        foreach (var entry in host.Index) host.Publish(entry, "fresh");
        var gates = host.Index.Select(host.BlockManifest).ToArray();
        var catalog = Staged(host);
        await catalog.InitializeIndexAsync().WaitAsync(Deadline);
        var publications = Observe(catalog);
        var preparing = catalog.PrepareLibraryAsync();
        try
        {
            await Task.WhenAll(gates.Select(gate => gate.Requested.Task)).WaitAsync(Deadline);
            Assert.True(catalog.IsOverviewReady);
            Assert.False(preparing.IsCompleted);
            Assert.Equal(2, catalog.Packs.Count);
            Assert.Equal(2, Assert.Single(publications).Length);
        }
        finally
        {
            foreach (var gate in gates) gate.Release.TrySetResult();
            await preparing.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task CancellingFreshPreparationKeepsThePublishedCacheAndDoesNotPublishPartialChanges()
    {
        using var host = new OverviewHost();
        var first = host.Add("first", "First pack", "First-Pack");
        var second = host.Add("second", "Second pack", "Second-Pack");
        host.Index = [first, second];
        await new PackCatalogService(_paths, _logger, host.Client).RefreshOnceAsync().WaitAsync(Deadline);
        var gates = host.Index.Select(host.BlockManifest).ToArray();
        var catalog = Staged(host);
        await catalog.InitializeIndexAsync().WaitAsync(Deadline);
        var publications = Observe(catalog);
        using var cancellation = new CancellationTokenSource();
        var preparing = catalog.PrepareLibraryAsync(cancellation.Token);
        try
        {
            await Task.WhenAll(gates.Select(gate => gate.Requested.Task)).WaitAsync(Deadline);
            var snapshot = catalog.Packs;
            Assert.True(catalog.IsOverviewReady);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing.WaitAsync(Deadline));
            Assert.False(catalog.IsReady);
            Assert.Same(snapshot, catalog.Packs);
            Assert.Equal(2, catalog.Packs.Count);
            Assert.Single(publications);
        }
        finally
        {
            cancellation.Cancel();
            foreach (var gate in gates) gate.Release.TrySetResult();
            try { await preparing.WaitAsync(Deadline); } catch (OperationCanceledException) { }
        }
    }

    private PackCatalogService Staged(OverviewHost host) => new(_paths, _logger, host.Client, stagedStartup: true);
    private static ConcurrentQueue<CatalogPack[]> Observe(PackCatalogService catalog)
    {
        var snapshots = new ConcurrentQueue<CatalogPack[]>();
        catalog.PresentationChanged += () => snapshots.Enqueue(catalog.Packs.ToArray());
        return snapshots;
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    private sealed class Gate
    {
        internal readonly TaskCompletionSource Requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class OverviewHost : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly object _gate = new();
        private readonly byte[] _preview;
        private readonly Dictionary<string, byte[]> _manifests = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _assets = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Gate> _manifestGates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Gate> _bundleGates = new(StringComparer.Ordinal);
        private readonly HashSet<string> _corrupt = new(StringComparer.Ordinal);
        internal CatalogSource[] Index = [];
        internal bool Offline;
        internal HttpClient Client { get; }

        internal OverviewHost()
        {
            Client = new HttpClient(this);
            var assembly = typeof(PackCatalogService).Assembly;
            var resource = assembly.GetManifestResourceNames().First(name => name.StartsWith("Minecraft.Library.", StringComparison.Ordinal) && name.EndsWith(".zip", StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            using var image = zip.GetEntry("preview.jpg")!.Open();
            using var copy = new MemoryStream(); image.CopyTo(copy); _preview = copy.ToArray();
        }

        internal CatalogSource Add(string id, string name, string repository)
        {
            var entry = new CatalogSource(id, name, new PackSyncSource("OverviewFixture", repository, "rolling"));
            Publish(entry, "one");
            return entry;
        }
        internal void Publish(CatalogSource entry, string revision)
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                using (var json = zip.CreateEntry("library.json").Open())
                    JsonSerializer.Serialize(json, new CatalogDescription
                    {
                        SchemaVersion = 1, Id = entry.Id, RelativePath = entry.RelativePath, Name = entry.RelativePath,
                        MinecraftVersion = "1.20.1", Revision = revision, Summary = "Overview summary", Description = "Fixture description",
                        Mods = [new() { Id = "root", Name = "Root" }, new() { Id = "addon", Name = "Addon", ParentId = "root" }]
                    }, Json);
                using var image = zip.CreateEntry("preview.jpg").Open(); image.Write(_preview);
            }
            var bundle = output.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bundle));
            var asset = "library-" + hash + ".zip";
            var payload = Encoding.UTF8.GetBytes("fixture payload");
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, revision, minecraftVersion = "1.20.1",
                files = new[] { new { path = "mods/payload.jar", sizeBytes = payload.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(payload)) } },
                assets = new[] { new { name = "payload.jar", kind = "jar", sizeBytes = payload.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(payload)), covers = new[] { "mods/payload.jar" } } },
                library = new { name = asset, sizeBytes = bundle.Length, sha256 = hash, revision }
            }, Json);
            lock (_gate) { _manifests[entry.Source.Repo] = manifest; _assets[entry.Source.Repo + "/" + asset] = bundle; }
        }
        internal Gate BlockManifest(CatalogSource entry) => _manifestGates.GetOrAdd(entry.Source.Repo, _ => new Gate());
        internal Gate BlockBundle(CatalogSource entry) => _bundleGates.GetOrAdd(entry.Source.Repo, _ => new Gate());
        internal void CorruptBundle(CatalogSource entry) { lock (_gate) _corrupt.Add(entry.Source.Repo); }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            if (Offline) return Reply(HttpStatusCode.ServiceUnavailable, []);
            if (uri.AbsolutePath.EndsWith("/catalog.json", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, packs = Index }, Json));
            string? repository;
            lock (_gate) repository = _manifests.Keys.FirstOrDefault(repo => uri.AbsolutePath.Contains("/" + repo + "/", StringComparison.Ordinal));
            if (repository is null) return Reply(HttpStatusCode.NotFound, []);
            var name = Uri.UnescapeDataString(uri.Segments[^1]);
            if (name == "pack-manifest.json")
            {
                if (_manifestGates.TryGetValue(repository, out var gate))
                { gate.Requested.TrySetResult(); await gate.Release.Task.WaitAsync(token); }
                lock (_gate) return Reply(HttpStatusCode.OK, _manifests[repository]);
            }
            if (name.StartsWith("library-", StringComparison.Ordinal) && _bundleGates.TryGetValue(repository, out var bundleGate))
            { bundleGate.Requested.TrySetResult(); await bundleGate.Release.Task.WaitAsync(token); }
            lock (_gate)
            {
                if (_corrupt.Contains(repository)) return Reply(HttpStatusCode.OK, [7]);
                return _assets.TryGetValue(repository + "/" + name, out var bytes)
                    ? Reply(HttpStatusCode.OK, bytes) : Reply(HttpStatusCode.NotFound, []);
            }
        }
        private static HttpResponseMessage Reply(HttpStatusCode status, byte[] bytes) => new(status) { Content = new ByteArrayContent(bytes) };
    }
}
