using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Minecraft.Tests;

public sealed class PackCatalogStagedStartupTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private readonly string root = Path.Combine(Path.GetTempPath(), "catalog-stages-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths paths;
    private readonly Logger logger;

    public PackCatalogStagedStartupTests()
    {
        paths = new AppPaths(root);
        paths.Ensure();
        logger = new Logger(paths.LogFile);
    }

    public void Dispose() => TempTree.Delete(root);

    [Fact]
    public async Task CatalogInstallationRename_ResumesOfflineBeforePublishingTheNewPath()
    {
        using var host = new CatalogHost();
        var entry = host.Add("generic-pack", "Previous Name", "Generic-Repo");
        const string target = "New Display Name";
        host.Index = [entry with { InstallationPath = target }];
        var settings = new AppSettings { ClientRelativePath = entry.RelativePath };
        var previous = paths.CombineUnderPacks(entry.RelativePath);
        Directory.CreateDirectory(previous);
        File.WriteAllText(Path.Combine(previous, "owned.txt"), "original");
        var catalog = Staged(host);
        catalog.MigrateStartupIndex = entries => new PackRenameMigration(paths, afterMove: _ =>
            throw new IOException("interrupted after move")).Apply(settings, entries, _ => { });

        await Assert.ThrowsAsync<IOException>(() => catalog.InitializeIndexAsync().WaitAsync(Deadline));
        Assert.False(catalog.IsIndexReady);
        Assert.DoesNotContain(catalog.Sources, source => source.RelativePath == target);
        Assert.False(Directory.Exists(previous));

        host.Offline = true;
        var restarted = Staged(host);
        new PackRenameMigration(paths).Apply(settings, restarted.Sources, _ => { });
        await restarted.InitializeIndexAsync().WaitAsync(Deadline);
        Assert.Equal(target, settings.ClientRelativePath);
        Assert.Equal(entry.RelativePath, restarted.CompatibilityPathFor(target));
        Assert.Equal(target, restarted.InstallationPathFor(entry.RelativePath));
        Assert.True(restarted.IsWithdrawn(entry.RelativePath));
        Assert.Equal("original", File.ReadAllText(Path.Combine(paths.CombineUnderPacks(target), "owned.txt")));
    }

    [Fact]
    public async Task NumberedCatalog_UsesLegacyBundleAndKeepsItsIdentityOffline()
    {
        using var host = new CatalogHost();
        var legacy = host.Add("old-catalog-id", "Existing pack", "Existing-Pack");
        host.Index = [legacy];

        var beforePublication = Staged(host);
        await beforePublication.PrepareLibraryAsync().WaitAsync(Deadline);
        Assert.Equal(legacy.Id, Assert.Single(beforePublication.Packs).Entry.Id);
        Assert.Equal(PackCatalogService.IndexUri.AbsolutePath, Assert.Single(host.Requests.Select(request => request.Uri.AbsolutePath)
            .Where(path => path.EndsWith("/catalog.json", StringComparison.Ordinal))));

        var numbered = legacy with { Id = "minecraft-500", LegacyId = legacy.Id };
        host.Index = [legacy with { NumberedId = numbered.Id }];
        var current = Staged(host);
        await current.PrepareLibraryAsync().WaitAsync(Deadline);
        var verified = Assert.Single(current.Packs);
        Assert.Equal(numbered, verified.Entry);
        Assert.Equal(2, host.IndexRequests);
        Assert.Equal(legacy.Id, verified.Content.Id);
        Assert.Equal(legacy.RelativePath, verified.Entry.RelativePath);

        host.Offline = true;
        var offline = Staged(host);
        await offline.PrepareLibraryAsync().WaitAsync(Deadline);
        var cached = Assert.Single(offline.Packs);
        Assert.Equal(numbered, cached.Entry);
        Assert.Equal(verified.ContentIdentity, cached.ContentIdentity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedSyncDoesNotWaitForOwnOrUnrelatedLibraryDownloads(bool unrelated)
    {
        using var host = new CatalogHost();
        var slow = unrelated ? Enumerable.Range(0, 4)
            .Select(index => host.Add("slow-" + index, "Slow pack " + index, "Slow-Pack-" + index)).ToArray() : [];
        var selected = host.Add("selected", "Selected pack", "Selected-Pack");
        host.Index = [.. slow, selected];
        var gates = (unrelated ? slow : [selected]).Select(host.BlockBundle).ToArray();
        var catalog = Staged(host);
        await catalog.InitializeIndexAsync().WaitAsync(Deadline);
        Assert.True(catalog.IsIndexReady);
        Assert.False(catalog.IsReady);
        Assert.Empty(catalog.Packs);
        var library = catalog.PrepareLibraryAsync();
        try
        {
            // Four unrelated ZIPs occupy the old full-refresh parallelism. A
            // foreground launch must not inherit that background queue barrier.
            await Task.WhenAll(gates.Select(gate => gate.Requested.Task)).WaitAsync(Deadline);
            await catalog.EnsureLaunchReadyAsync(selected.RelativePath).WaitAsync(Deadline);
            Assert.True(catalog.IsLaunchReady(selected.RelativePath));
            Assert.False(library.IsCompleted);
            Assert.False(catalog.IsReady);
            var sync = new PortablePackSyncService(paths, logger, host.Client) { Catalog = catalog };
            var result = await sync.SyncAsync(selected.RelativePath, null, default).WaitAsync(Deadline);
            Assert.Equal(PackSyncOutcome.Installed, result.Outcome);
            Assert.Equal(host.Payload(selected, "one"), File.ReadAllBytes(Path.Combine(paths.Packs, selected.RelativePath, "mods", "payload.jar")));
            Assert.All(gates, gate => Assert.False(gate.Released.Task.IsCompleted));
            Assert.False(library.IsCompleted);
            Assert.Equal(2, host.ManifestRequests(selected));
        }
        finally
        {
            foreach (var gate in gates) gate.Released.TrySetResult();
            await library.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task ConcurrentSelectionAndPresentationReuseTheSameFrozenManifest()
    {
        using var host = new CatalogHost();
        var selected = host.Add("selected", "Selected pack", "Selected-Pack");
        host.Index = [selected];
        var gate = host.BlockManifest(selected);
        var catalog = Staged(host);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => catalog.InitializeIndexAsync())).WaitAsync(Deadline);
        Assert.Equal(1, host.IndexRequests);
        var preparations = Enumerable.Range(0, 8).Select(index => catalog.EnsureLaunchReadyAsync(
            index % 2 == 0 ? selected.RelativePath : selected.RelativePath.ToUpperInvariant())).ToArray();
        try
        {
            await gate.Requested.Task.WaitAsync(Deadline);
            Assert.Equal(1, host.ManifestRequests(selected));
            Assert.All(preparations, task => Assert.False(task.IsCompleted));
        }
        finally { gate.Released.TrySetResult(); }
        await Task.WhenAll(preparations).WaitAsync(Deadline);
        var frozen = catalog.SessionManifest(selected.Source);
        Assert.Equal("one", Revision(frozen));
        Assert.True(catalog.IsLaunchReady(selected.RelativePath.ToUpperInvariant()));
        Assert.False(catalog.IsReady);
        host.Publish(selected, "two");
        await catalog.PrepareLibraryAsync().WaitAsync(Deadline);
        await catalog.EnsureLaunchReadyAsync(selected.RelativePath).WaitAsync(Deadline);
        Assert.Equal(frozen, catalog.SessionManifest(selected.Source));
        Assert.Equal(selected.Source, catalog.SourceFor(selected.RelativePath));
        Assert.Equal(1, host.ManifestRequests(selected));
        Assert.Equal(1, host.IndexRequests);
        var request = Assert.Single(host.Requests.Where(item => item.Uri.AbsolutePath.Contains("/Selected-Pack/", StringComparison.Ordinal) &&
            item.Uri.AbsolutePath.EndsWith("/pack-manifest.json", StringComparison.Ordinal)));
        Assert.NotEmpty(request.Uri.Query);
        Assert.True(request.NoCache && request.NoStore);
        var sync = new PortablePackSyncService(paths, logger, host.Client) { Catalog = catalog };
        await sync.SyncAsync(selected.RelativePath, null, default).WaitAsync(Deadline);
        Assert.Equal(host.Payload(selected, "two"), File.ReadAllBytes(Path.Combine(paths.Packs, selected.RelativePath, "mods", "payload.jar")));
        Assert.Equal(2, host.ManifestRequests(selected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthoritativeRemovalBlocksCachedSelectionBeforeBackgroundPreparationAndSurvivesOfflineRestart(bool empty)
    {
        using var host = new CatalogHost();
        var removed = host.Add("removed", "Removed pack", "Removed-Pack");
        var retained = host.Add("retained", "Retained pack", "Retained-Pack");
        host.Index = [removed, retained];
        await new PackCatalogService(paths, logger, host.Client).RefreshOnceAsync();
        var installed = Path.Combine(paths.Packs, removed.RelativePath, "mods", "personal.jar");
        var world = Path.Combine(paths.Worlds, removed.RelativePath, "World", "level.dat");
        Write(installed, "keep installed bytes");
        Write(world, "keep world bytes");
        Marker(removed);
        var before = host.ManifestRequests(removed);
        host.Index = empty ? [] : [retained];
        var catalog = Staged(host);
        await catalog.InitializeIndexAsync().WaitAsync(Deadline);
        await AssertRemoved(catalog);

        // Persist the authoritative index at its own stage, rather than only
        // after the slow presentation work eventually finishes.
        host.Offline = true;
        var offline = Staged(host);
        await offline.InitializeIndexAsync().WaitAsync(Deadline);
        Assert.False(offline.HasOnlineCatalog);
        await AssertRemoved(offline);
        Assert.Equal(before, host.ManifestRequests(removed));
        Assert.Equal("keep installed bytes", File.ReadAllText(installed));
        Assert.Equal("keep world bytes", File.ReadAllText(world));

        async Task AssertRemoved(PackCatalogService current)
        {
            Assert.True(current.IsIndexReady);
            Assert.True(current.IsWithdrawn(removed.RelativePath));
            Assert.Null(current.SourceFor(removed.RelativePath));
            await Assert.ThrowsAnyAsync<IOException>(() => current.EnsureLaunchReadyAsync(removed.RelativePath));
            Assert.False(current.IsLaunchReady(removed.RelativePath));
            var sync = new PortablePackSyncService(paths, logger, host.Client) { Catalog = current };
            Assert.Null(sync.TryResolveSource(removed.RelativePath));
            Assert.Equal(PackSyncOutcome.SkippedNoSource, (await sync.SyncAsync(removed.RelativePath, null, default)).Outcome);
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("corrupt-json")]
    [InlineData("unsafe-game-path")]
    public async Task OfflineSelectionRequiresAValidCheckedGameManifest(string cacheKind)
    {
        using var host = new CatalogHost();
        var selected = host.Add("selected", "Selected pack", "Selected-Pack");
        host.Index = [selected];
        var initial = new PackCatalogService(paths, logger, host.Client);
        await initial.RefreshOnceAsync();
        var verified = initial.SessionManifest(selected.Source);
        var cachePath = Directory.GetFiles(Path.Combine(paths.Launcher, "Library"), "*.json")
            .Single(path => Path.GetFileName(path) != "catalog.json");
        if (cacheKind == "corrupt-json") File.WriteAllText(cachePath, "{ corrupted cache");
        if (cacheKind == "unsafe-game-path")
        {
            var cache = JsonNode.Parse(File.ReadAllText(cachePath))!;
            var manifest = JsonNode.Parse(cache["manifest"]!.GetValue<string>())!;
            manifest["files"]![0]!["path"] = "../escape.jar";
            manifest["assets"]![0]!["covers"]![0] = "../escape.jar";
            cache["manifest"] = manifest.ToJsonString();
            File.WriteAllText(cachePath, cache.ToJsonString());
        }
        host.Offline = true;
        var catalog = Staged(host);
        Assert.Empty(catalog.Packs);
        await catalog.InitializeIndexAsync().WaitAsync(Deadline);
        if (cacheKind == "valid")
        {
            await catalog.EnsureLaunchReadyAsync(selected.RelativePath).WaitAsync(Deadline);
            Assert.True(catalog.IsLaunchReady(selected.RelativePath));
            Assert.Equal(verified, catalog.SessionManifest(selected.Source));
        }
        else
        {
            await Assert.ThrowsAnyAsync<IOException>(() => catalog.EnsureLaunchReadyAsync(selected.RelativePath));
            Assert.False(catalog.IsLaunchReady(selected.RelativePath));
            Assert.ThrowsAny<IOException>(() => catalog.SessionManifest(selected.Source));
        }
        Assert.False(File.Exists(Path.Combine(paths.Root, "escape.jar")));
    }

    [Fact]
    public async Task VerifiedRepositoryChangeCannotRetargetAnAlreadyFrozenLaunchUntilNextSession()
    {
        using var host = new CatalogHost();
        var old = host.Add("selected", "Selected pack", "Old-Repository");
        host.Index = [old];
        var initial = new PackCatalogService(paths, logger, host.Client);
        await initial.RefreshOnceAsync();
        Marker(old);
        var desired = host.Add(old.Id, old.RelativePath, "New-Repository", "two");
        host.Index = [desired];
        var catalog = Staged(host);
        await catalog.InitializeIndexAsync().WaitAsync(Deadline);
        await catalog.EnsureLaunchReadyAsync(old.RelativePath).WaitAsync(Deadline);
        var frozen = catalog.SessionManifest(old.Source);
        var sync = new PortablePackSyncService(paths, logger, host.Client) { Catalog = catalog };
        Assert.Equal(old.Source, sync.TryResolveSource(old.RelativePath));
        await catalog.PrepareLibraryAsync().WaitAsync(Deadline);
        Assert.Equal(old.Source, catalog.SourceFor(old.RelativePath));
        Assert.Equal(old.Source, sync.TryResolveSource(old.RelativePath));
        Assert.Equal(frozen, catalog.SessionManifest(old.Source));

        var next = Staged(host);
        await next.InitializeIndexAsync().WaitAsync(Deadline);
        await next.EnsureLaunchReadyAsync(old.RelativePath).WaitAsync(Deadline);
        Assert.Equal(desired.Source, next.SourceFor(old.RelativePath));
        Assert.Equal("two", Revision(next.SessionManifest(desired.Source)));
        var nextSync = new PortablePackSyncService(paths, logger, host.Client) { Catalog = next };
        Assert.Equal(desired.Source, nextSync.TryResolveSource(old.RelativePath));
    }

    [Fact]
    public async Task OfflineCustomAliasesCanReuseTheSameCheckedSourceWithoutPresentationCache()
    {
        using var host = new CatalogHost();
        var shared = host.Add("upstream", "Upstream pack", "Shared-Repository");
        host.Index = [];
        var firstAlias = shared with { Id = "first-alias", RelativePath = "First custom folder" };
        var secondAlias = shared with { Id = "second-alias", RelativePath = "Second custom folder" };
        Marker(firstAlias);
        Marker(secondAlias);
        var initial = Staged(host);
        await initial.InitializeIndexAsync().WaitAsync(Deadline);
        await initial.EnsureLaunchReadyAsync(firstAlias.RelativePath).WaitAsync(Deadline);
        var frozen = initial.SessionManifest(shared.Source);
        Assert.Empty(initial.Packs);
        Assert.Single(Directory.GetFiles(Path.Combine(paths.Launcher, "Library", "Launch"), "*.json"));
        Assert.Equal("catalog.json", Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(paths.Launcher, "Library"), "*.json"))));

        host.Offline = true;
        var offline = Staged(host);
        await offline.InitializeIndexAsync().WaitAsync(Deadline);
        await offline.EnsureLaunchReadyAsync(secondAlias.RelativePath).WaitAsync(Deadline);
        Assert.True(offline.IsLaunchReady(secondAlias.RelativePath));
        Assert.False(offline.IsKnownCatalogPath(secondAlias.RelativePath));
        Assert.Equal(shared.Source, offline.SourceFor(secondAlias.RelativePath));
        Assert.Equal(frozen, offline.SessionManifest(shared.Source));
        Assert.Empty(offline.Packs);
        Assert.DoesNotContain(host.Requests, request => request.Uri.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstalledOfflinePackWithoutRemoteCacheKeepsLocalFallbackButPendingUpdatesStayBlocked(bool pending)
    {
        using var host = new CatalogHost();
        var selected = host.Add("selected", "Selected pack", "Selected-Pack");
        host.Index = [selected];
        await Staged(host).InitializeIndexAsync().WaitAsync(Deadline);
        Assert.Equal(0, host.ManifestRequests(selected));
        var folder = paths.CombineUnderPacks(selected.RelativePath);
        var localManifest = Path.Combine(folder, PackManifestService.ManifestFileName);
        const string metadata = """{"schemaVersion":1,"minecraftVersion":"1.20.1","loader":{"type":"forge","version":"47.3.0"}}""";
        Write(localManifest, metadata);
        var jar = Path.Combine(folder, "mods", "personal.jar");
        var world = Path.Combine(paths.Worlds, selected.RelativePath, "World", "level.dat");
        Write(jar, "existing local mod bytes");
        Write(world, "existing world bytes");
        if (pending) Write(Path.Combine(folder, PortablePackSyncService.ApplyPendingFileName), "interrupted update");
        host.Offline = true;
        var catalog = Staged(host);
        await catalog.InitializeIndexAsync().WaitAsync(Deadline);
        var sync = new PortablePackSyncService(paths, logger, host.Client) { Catalog = catalog };
        if (pending)
        {
            await Assert.ThrowsAnyAsync<IOException>(() => catalog.EnsureLaunchReadyAsync(selected.RelativePath));
            Assert.False(catalog.IsLaunchReady(selected.RelativePath));
            await Assert.ThrowsAsync<PackApplyFailedException>(() => sync.SyncAsync(selected.RelativePath, null, default));
        }
        else
        {
            await catalog.EnsureLaunchReadyAsync(selected.RelativePath).WaitAsync(Deadline);
            Assert.True(catalog.IsLaunchReady(selected.RelativePath));
            Assert.Equal(selected.Source, catalog.SourceFor(selected.RelativePath));
            var result = await sync.SyncAsync(selected.RelativePath, null, default).WaitAsync(Deadline);
            Assert.Equal(PackSyncOutcome.OfflineFallback, result.Outcome);
            Assert.Equal(PortablePackSyncService.OfflineCheckWarning, result.Warning);
        }
        // Local fallback is not a fabricated verified remote snapshot.
        Assert.ThrowsAny<IOException>(() => catalog.SessionManifest(selected.Source));
        Assert.Equal(metadata, File.ReadAllText(localManifest));
        Assert.Equal("existing local mod bytes", File.ReadAllText(jar));
        Assert.Equal("existing world bytes", File.ReadAllText(world));
        Assert.DoesNotContain(host.Requests, request => request.Uri.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExpiredIndexBudgetUsesCheckedCacheWithoutStartingAnotherManifestRequest()
    {
        using var host = new CatalogHost();
        var selected = host.Add("selected", "Selected pack", "Selected-Pack");
        host.Index = [selected];
        var initial = new PackCatalogService(paths, logger, host.Client);
        await initial.RefreshOnceAsync();
        var frozen = initial.SessionManifest(selected.Source);
        var manifestRequests = host.ManifestRequests(selected);
        host.IndexTimesOut = true;
        var catalog = Staged(host);

        // Exercise the actual production timeout, rather than a handler that
        // throws cancellation immediately. Its remaining budget must be zero
        // before the selected pack could start a second network request.
        await catalog.EnsureLaunchReadyAsync(selected.RelativePath).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, host.IndexTimeoutCancellations);
        Assert.True(catalog.IsIndexReady);
        Assert.False(catalog.HasOnlineCatalog);
        Assert.True(catalog.IsLaunchReady(selected.RelativePath));
        Assert.Equal(frozen, catalog.SessionManifest(selected.Source));
        Assert.Equal(manifestRequests, host.ManifestRequests(selected));
    }

    [Fact]
    public async Task OwnedIndexCancellationUsesCheckedSelectionAndLaterSelectionGetsAFreshBudget()
    {
        using var host = new CatalogHost();
        var selected = host.Add("selected", "Selected pack", "Selected-Pack");
        var later = host.Add("later", "Later pack", "Later-Pack");
        host.Index = [selected, later];
        var initial = new PackCatalogService(paths, logger, host.Client);
        await initial.RefreshOnceAsync().WaitAsync(Deadline);
        var frozen = initial.SessionManifest(selected.Source);
        var selectedRequests = host.ManifestRequests(selected);
        var laterRequests = host.ManifestRequests(later);
        host.Publish(selected, "two");
        host.Publish(later, "two");
        host.IndexTimesOut = true;
        var ownedBudget = new TaskCompletionSource<CancellationTokenSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new PackCatalogService(paths, logger, host.Client, stagedStartup: true,
            indexTimeoutSourceFactory: token =>
            {
                var source = CancellationTokenSource.CreateLinkedTokenSource(token);
                ownedBudget.TrySetResult(source);
                return source;
            });

        var first = catalog.EnsureLaunchReadyAsync(selected.RelativePath);
        await host.IndexTimeoutStarted.Task.WaitAsync(Deadline);
        // Cancel the actual owned source while the nominal 15-second clock
        // still has time, so timer precision cannot decide the request count.
        (await ownedBudget.Task.WaitAsync(Deadline)).Cancel();
        await first.WaitAsync(Deadline);
        Assert.Equal(1, host.IndexTimeoutCancellations);
        Assert.Equal(selectedRequests, host.ManifestRequests(selected));
        Assert.Equal(frozen, catalog.SessionManifest(selected.Source));

        await catalog.EnsureLaunchReadyAsync(later.RelativePath).WaitAsync(Deadline);
        Assert.Equal("two", Revision(catalog.SessionManifest(later.Source)));
        Assert.Equal(laterRequests + 1, host.ManifestRequests(later));
        Assert.Equal(1, host.IndexTimeoutCancellations);
    }

    [Fact]
    public async Task IndexHttpFailureDoesNotSpendTheSelectedManifestBudget()
    {
        using var host = new CatalogHost();
        var selected = host.Add("selected", "Selected pack", "Selected-Pack");
        host.Index = [selected];
        await new PackCatalogService(paths, logger, host.Client).RefreshOnceAsync().WaitAsync(Deadline);
        var requests = host.ManifestRequests(selected);
        host.Publish(selected, "two");
        host.IndexFailureStatus = HttpStatusCode.ServiceUnavailable;
        var catalog = Staged(host);

        await catalog.EnsureLaunchReadyAsync(selected.RelativePath).WaitAsync(Deadline);
        Assert.False(catalog.HasOnlineCatalog);
        Assert.Equal("two", Revision(catalog.SessionManifest(selected.Source)));
        Assert.Equal(requests + 1, host.ManifestRequests(selected));
        Assert.Equal(0, host.IndexTimeoutCancellations);
    }

    [Fact]
    public async Task CorruptOwnPresentationKeepsTheFrozenCheckedGamePlanAndPreviousCardSeparate()
    {
        using var host = new CatalogHost();
        var selected = host.Add("selected", "Selected pack", "Selected-Pack");
        host.Index = [selected];
        await new PackCatalogService(paths, logger, host.Client).RefreshOnceAsync();
        host.Publish(selected, "two");
        host.CorruptBundles.Add(selected.Source.Repo);
        var catalog = Staged(host);
        await catalog.InitializeIndexAsync().WaitAsync(Deadline);
        await catalog.EnsureLaunchReadyAsync(selected.RelativePath).WaitAsync(Deadline);
        var frozen = catalog.SessionManifest(selected.Source);
        Assert.Equal("two", Revision(frozen));
        await catalog.PrepareLibraryAsync().WaitAsync(Deadline);
        Assert.True(catalog.IsLaunchReady(selected.RelativePath));
        Assert.Equal(frozen, catalog.SessionManifest(selected.Source));
        Assert.Equal("one", catalog.Find(selected.RelativePath)!.Content.Revision);
        var sync = new PortablePackSyncService(paths, logger, host.Client) { Catalog = catalog };
        await sync.SyncAsync(selected.RelativePath, null, default).WaitAsync(Deadline);
        Assert.Equal(host.Payload(selected, "two"), File.ReadAllBytes(Path.Combine(paths.Packs, selected.RelativePath, "mods", "payload.jar")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterMarkerEditsDoNotRetargetAnAlreadyFrozenPack(bool initiallyCustom)
    {
        using var host = new CatalogHost();
        var selected = host.Add("selected", "Selected pack", "Selected-Pack");
        var other = host.Add("other", "Other pack", "Other-Pack");
        host.Index = initiallyCustom ? [other] : [selected, other];
        if (initiallyCustom) Write(Path.Combine(paths.Packs, selected.RelativePath, "mods", "personal.jar"), "keep custom bytes");
        else Marker(selected);
        var catalog = Staged(host);
        await catalog.InitializeIndexAsync().WaitAsync(Deadline);
        await catalog.EnsureLaunchReadyAsync(selected.RelativePath).WaitAsync(Deadline);
        await catalog.EnsureLaunchReadyAsync(other.RelativePath).WaitAsync(Deadline);
        Assert.True(catalog.IsLaunchReady(selected.RelativePath));

        // Both remote manifests are already available. Re-reading this edited
        // marker must not silently install the other pack into the frozen path.
        Marker(selected with { Source = other.Source });
        var expected = initiallyCustom ? null : selected.Source;
        var sync = new PortablePackSyncService(paths, logger, host.Client) { Catalog = catalog };
        Assert.Equal(expected, catalog.SourceFor(selected.RelativePath));
        Assert.Equal(expected, sync.TryResolveSource(selected.RelativePath));
        var result = await sync.SyncAsync(selected.RelativePath, null, default).WaitAsync(Deadline);
        if (initiallyCustom)
        {
            Assert.Equal(PackSyncOutcome.SkippedNoSource, result.Outcome);
            Assert.Equal("keep custom bytes", File.ReadAllText(Path.Combine(paths.Packs, selected.RelativePath, "mods", "personal.jar")));
        }
        else
        {
            Assert.Equal(PackSyncOutcome.Installed, result.Outcome);
            Assert.Equal(host.Payload(selected, "one"), File.ReadAllBytes(Path.Combine(paths.Packs, selected.RelativePath, "mods", "payload.jar")));
        }
    }

    private PackCatalogService Staged(CatalogHost host) => new(paths, logger, host.Client, stagedStartup: true);
    private static string Revision(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.GetProperty("revision").GetString()!; }
    private void Marker(CatalogSource entry) => Write(Path.Combine(paths.Packs, entry.RelativePath, PortablePackSyncService.SourceMarkerFileName),
        JsonSerializer.Serialize(new { schemaVersion = 1, owner = entry.Source.Owner, repo = entry.Source.Repo, tag = entry.Source.Tag }));
    private static void Write(string path, string value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }

    private sealed record Request(Uri Uri, bool NoCache, bool NoStore);
    private sealed class Gate
    {
        internal readonly TaskCompletionSource Requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CatalogHost : HttpMessageHandler
    {
        private sealed record Release(byte[] Manifest, byte[] Payload);
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly object gate = new();
        private readonly byte[] preview;
        private readonly Dictionary<string, Release> releases = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> assets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> payloads = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Gate> bundleGates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Gate> manifestGates = new(StringComparer.Ordinal);
        internal readonly ConcurrentQueue<Request> Requests = new();
        internal readonly HashSet<string> CorruptBundles = new(StringComparer.Ordinal);
        internal CatalogSource[] Index = [];
        internal bool Offline;
        internal bool IndexTimesOut;
        internal HttpStatusCode? IndexFailureStatus;
        internal int IndexTimeoutCancellations;
        internal readonly TaskCompletionSource IndexTimeoutStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal HttpClient Client { get; }
        internal int IndexRequests => Requests.Count(request => request.Uri.AbsolutePath.EndsWith("/catalog.json", StringComparison.Ordinal));

        internal CatalogHost()
        {
            Client = new HttpClient(this);
            using var seed = typeof(PackCatalogService).Assembly.GetManifestResourceStream("Minecraft.Library.minecraft-26-2.zip")!;
            using var archive = new ZipArchive(seed, ZipArchiveMode.Read);
            using var stream = archive.GetEntry("preview.jpg")!.Open();
            using var bytes = new MemoryStream(); stream.CopyTo(bytes); preview = bytes.ToArray();
        }

        internal CatalogSource Add(string id, string folder, string repository, string revision = "one")
        {
            var entry = new CatalogSource(id, folder, new PackSyncSource("CatalogStageFixture", repository, "rolling"));
            Publish(entry, revision);
            return entry;
        }
        internal void Publish(CatalogSource entry, string revision)
        {
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                using (var metadata = archive.CreateEntry("library.json").Open())
                    JsonSerializer.Serialize(metadata, new CatalogDescription
                    {
                        SchemaVersion = 1, Id = entry.Id, RelativePath = entry.RelativePath, Name = entry.RelativePath,
                        MinecraftVersion = "1.20.1", Revision = revision, Summary = "Pack summary", Description = "Verified pack description"
                    }, Json);
                using var image = archive.CreateEntry("preview.jpg").Open(); image.Write(preview);
            }
            var bundle = output.ToArray();
            var bundleHash = Convert.ToHexStringLower(SHA256.HashData(bundle));
            var bundleName = "library-" + bundleHash + ".zip";
            var payload = Encoding.UTF8.GetBytes("fixture payload " + entry.Source.Repo + " " + revision);
            var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
            var name = revision + ".jar";
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, revision, minecraftVersion = "1.20.1",
                files = new[] { new { path = "mods/payload.jar", sizeBytes = payload.Length, sha256 = hash } },
                assets = new[] { new { name, kind = "jar", sizeBytes = payload.Length, sha256 = hash, covers = new[] { "mods/payload.jar" } } },
                library = new { name = bundleName, sizeBytes = bundle.Length, sha256 = bundleHash, revision }
            }, Json);
            lock (gate)
            {
                releases[entry.Source.Repo] = new Release(manifest, payload);
                assets[entry.Source.Repo + "/" + bundleName] = bundle;
                assets[entry.Source.Repo + "/" + name] = payload;
                payloads[entry.Source.Repo + "/" + revision] = payload;
            }
        }
        internal byte[] Payload(CatalogSource entry, string revision) { lock (gate) return payloads[entry.Source.Repo + "/" + revision]; }
        internal Gate BlockBundle(CatalogSource entry) => bundleGates.GetOrAdd(entry.Source.Repo, _ => new Gate());
        internal Gate BlockManifest(CatalogSource entry) => manifestGates.GetOrAdd(entry.Source.Repo, _ => new Gate());
        internal int ManifestRequests(CatalogSource entry) => Requests.Count(request =>
            request.Uri.AbsolutePath.Contains("/" + entry.Source.Repo + "/", StringComparison.Ordinal) &&
            request.Uri.AbsolutePath.EndsWith("/pack-manifest.json", StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            Requests.Enqueue(new Request(uri, request.Headers.CacheControl?.NoCache == true, request.Headers.CacheControl?.NoStore == true));
            if (Offline) return Reply(HttpStatusCode.ServiceUnavailable, []);
            if (uri.AbsolutePath.EndsWith("/catalog.json", StringComparison.Ordinal))
            {
                if (IndexFailureStatus is { } failureStatus) return Reply(failureStatus, []);
                if (IndexTimesOut)
                {
                    IndexTimeoutStarted.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    { Interlocked.Increment(ref IndexTimeoutCancellations); throw; }
                }
                return Reply(HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, packs = Index }, Json));
            }
            string? repository; Release? release;
            lock (gate)
            {
                repository = releases.Keys.FirstOrDefault(repo => uri.AbsolutePath.Contains("/" + repo + "/", StringComparison.Ordinal));
                release = repository is null ? null : releases[repository];
            }
            if (repository is null || release is null) return Reply(HttpStatusCode.NotFound, []);
            var name = Uri.UnescapeDataString(uri.Segments[^1]);
            if (name == "pack-manifest.json")
            {
                if (manifestGates.TryGetValue(repository, out var wait))
                { wait.Requested.TrySetResult(); await wait.Released.Task.WaitAsync(token); }
                return Reply(HttpStatusCode.OK, release.Manifest);
            }
            if (name.StartsWith("library-", StringComparison.Ordinal))
            {
                if (bundleGates.TryGetValue(repository, out var wait))
                { wait.Requested.TrySetResult(); await wait.Released.Task.WaitAsync(token); }
                if (CorruptBundles.Contains(repository)) return Reply(HttpStatusCode.OK, [9]);
            }
            lock (gate) return assets.TryGetValue(repository + "/" + name, out var bytes)
                ? Reply(HttpStatusCode.OK, bytes) : Reply(HttpStatusCode.NotFound, []);
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, byte[] bytes) => new(status) { Content = new ByteArrayContent(bytes) };
    }
}
