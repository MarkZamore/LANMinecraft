using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Minecraft.Tests;

[CollectionDefinition("Runtime component service", DisableParallelization = true)]
public sealed class RuntimeComponentServiceCollection;

[Collection("Runtime component service")]
public sealed class RuntimeComponentServiceTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "runtime-components-" + Guid.NewGuid().ToString("N")));
    private readonly Func<AppPaths, RuntimeComponentArtifact, string>? _previousResolver = RuntimeComponentContext.TestPathResolver;
    private readonly string _originalCatalog = RuntimeComponentContext.Current.CatalogJson;
    private static readonly PackRuntimeDescriptor Descriptor = new(1, "26.3", new(PackLoaderKind.Fabric, "0.20.1"), "", new string('d', 64));

    public RuntimeComponentServiceTests()
    {
        RuntimeComponentContext.TestPathResolver = null;
        _paths.Ensure();
        Directory.CreateDirectory(_paths.CombineUnderPacks("First"));
        Directory.CreateDirectory(_paths.CombineUnderPacks("Second"));
    }

    [Fact]
    public async Task IdenticalArtifactsAreDownloadedOnceAcrossServicesAndPacks()
    {
        byte[] payload = [1, 4, 9, 16];
        using var handler = new Source(Catalog("one", payload), payload);
        using var http = new HttpClient(handler);
        var firstService = Service(http);
        var secondService = Service(http);
        var prepared = await Task.WhenAll(
            firstService.PrepareAsync("First", Descriptor, null), secondService.PrepareAsync("Second", Descriptor, null));
        try
        {
            Assert.Equal(1, handler.ArtifactRequests);
            Assert.Equal(prepared[0].ArtifactPaths["identity-adapter"], prepared[1].ArtifactPaths["identity-adapter"]);
            Assert.False(Directory.Exists(Path.Combine(_paths.Launcher, "Components", "packs")));
            prepared[0].Commit();
            prepared[1].Commit();
            Assert.Equal(2, Directory.GetFiles(Path.Combine(_paths.Launcher, "Components", "packs"), "selection.json", SearchOption.AllDirectories).Length);
            Assert.Throws<IOException>(() => File.Delete(prepared[0].ArtifactPaths["identity-adapter"]));
        }
        finally { foreach (var selection in prepared) selection.Dispose(); }
    }

    [Fact]
    public async Task CheckedInstalledSelectionStartsOfflineWhenGlobalCacheIsLost()
    {
        byte[] payload = [2, 3, 5, 7];
        using (var online = new HttpClient(new Source(Catalog("installed", payload), payload)))
        using (var prepared = await Service(online).PrepareAsync("First", Descriptor, null)) prepared.Commit();
        File.Delete(Path.Combine(_paths.Launcher, "Components", "catalog.json"));
        using var offline = new HttpClient(new Offline());
        var service = Service(offline);
        Assert.Equal("installed", (await service.LoadAsync(refresh: false)).Revision);
        using var restored = await service.PrepareAsync("First", Descriptor, null);
        Assert.Equal("installed", restored.Snapshot.Revision);
        Assert.Equal(payload, File.ReadAllBytes(restored.ArtifactPaths["identity-adapter"]));

        // A failed later launch may have fetched a newer catalog and all its agents
        // without installing its Java or committing the pack's new selection.
        byte[] newerPayload = [11, 13, 17, 19];
        var newerCatalog = JsonNode.Parse(Catalog("newer-global-cache", newerPayload))!;
        newerCatalog["javaCatalog"]!["rules"]![0]!["majorVersion"] = 21;
        using (var online = new HttpClient(new Source(newerCatalog.ToJsonString(), newerPayload)))
        using (var uncommitted = await Service(online).PrepareAsync("First", Descriptor, null))
        {
            Assert.Equal("newer-global-cache", uncommitted.Snapshot.Revision);
            using var selected = RuntimeComponentContext.Use(uncommitted);
            Assert.Equal(21, JavaRuntimeCatalog.RequiredFor(Descriptor).MajorVersion);
        }
        var restarted = Service(offline);
        Assert.Equal("newer-global-cache", (await restarted.LoadAsync(refresh: false)).Revision);
        using var pinned = await restarted.PrepareAsync("First", Descriptor, null);
        Assert.Equal("installed", pinned.Snapshot.Revision);
        Assert.Equal(payload, File.ReadAllBytes(pinned.ArtifactPaths["identity-adapter"]));
        using var restoredScope = RuntimeComponentContext.Use(pinned);
        Assert.Equal(25, JavaRuntimeCatalog.RequiredFor(Descriptor).MajorVersion);
    }

    [Fact]
    public async Task FailedNewPayloadPreservesCommittedSelectionAndRemovesPartialFiles()
    {
        byte[] oldBytes = [2, 4, 8];
        byte[] newBytes = [3, 9, 27];
        using var handler = new Source(Catalog("old", oldBytes), oldBytes);
        using var http = new HttpClient(handler);
        var service = Service(http);
        using (var installed = await service.PrepareAsync("First", Descriptor, null)) installed.Commit();
        var selectionFile = Directory.GetFiles(Path.Combine(_paths.Launcher, "Components", "packs"), "selection.json", SearchOption.AllDirectories).Single();
        var original = File.ReadAllText(selectionFile);
        handler.CatalogJson = Catalog("new", newBytes);
        handler.Payload = [0, 0, 0];
        using var recovered = await service.PrepareAsync("First", Descriptor, null);
        Assert.Equal("old", recovered.Snapshot.Revision);
        Assert.Equal(original, File.ReadAllText(selectionFile));
        Assert.Empty(Directory.GetFiles(Path.Combine(_paths.Launcher, "Components", "objects"), "*.part", SearchOption.AllDirectories));
        Assert.Equal(4, handler.ArtifactRequests);
    }

    [Fact]
    public async Task UncommittedPreparationCannotReplaceOfflineReadyLock()
    {
        byte[] oldBytes = [1, 2];
        byte[] newBytes = [3, 4];
        using var handler = new Source(Catalog("old", oldBytes), oldBytes);
        using var http = new HttpClient(handler);
        var service = Service(http);
        using (var installed = await service.PrepareAsync("First", Descriptor, null)) installed.Commit();
        var selectionFile = Directory.GetFiles(Path.Combine(_paths.Launcher, "Components", "packs"), "selection.json", SearchOption.AllDirectories).Single();
        var original = File.ReadAllText(selectionFile);
        handler.CatalogJson = Catalog("new", newBytes);
        handler.Payload = newBytes;
        using (var attempt = await service.PrepareAsync("First", Descriptor, null))
        {
            Assert.Equal("new", attempt.Snapshot.Revision);
            Assert.Equal(original, File.ReadAllText(selectionFile));
        }
        Assert.Equal(original, File.ReadAllText(selectionFile));
    }

    [Fact]
    public async Task PackRenamePreservesOfflineSelectionAndResumesWithoutOverwritingAnotherSet()
    {
        byte[] oldBytes = [1, 3, 5];
        byte[] newBytes = [2, 4, 6];
        using var handler = new Source(Catalog("old", oldBytes), oldBytes);
        using var http = new HttpClient(handler);
        using (var prepared = await Service(http).PrepareAsync("First", Descriptor, null)) prepared.Commit();
        var original = Directory.GetFiles(Path.Combine(_paths.Launcher, "Components", "packs"), "selection.json", SearchOption.AllDirectories).Single();
        var originalJson = File.ReadAllText(original);
        var payload = Directory.GetFiles(Path.Combine(_paths.Launcher, "Components", "objects"), "adapter.jar", SearchOption.AllDirectories).Single();
        handler.CatalogJson = Catalog("new-global", newBytes);
        handler.Payload = newBytes;
        using (var prepared = await Service(http).PrepareAsync("Second", Descriptor, null)) prepared.Commit();
        Assert.Throws<IOException>(() => RuntimeComponentService.PreparePackRename(_paths, "First", "Second"));
        Assert.Equal(originalJson, File.ReadAllText(original));

        var entry = new CatalogSource("rename-test", "Renamed", new PackSyncSource("Example", "Pack", "latest"))
            { LegacyRelativePaths = ["First"] };
        var settings = new AppSettings { ClientRelativePath = "First" };
        Assert.Throws<IOException>(() => new PackRenameMigration(_paths, afterMove: _ =>
        {
            DeprecatedFileCleanupService.CleanupRuntimeComponents(_paths, static () => { });
            Assert.True(File.Exists(payload));
            throw new IOException("Interrupted after moving pack directory");
        }).Apply(settings, [entry], _ => { }));

        // A crash after the atomic destination write, before removing the old lock, is resumable.
        var destination = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(original))!,
            RuntimeComponentSnapshot.Digest("RENAMED"), "selection.json");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var renamed = JsonNode.Parse(originalJson)!;
        renamed["packRelativePath"] = "Renamed";
        File.WriteAllText(destination, renamed.ToJsonString());
        new PackRenameMigration(_paths).Apply(settings, [entry], _ => { });
        Assert.False(Directory.Exists(Path.GetDirectoryName(original)));
        Assert.Equal("Renamed", settings.ClientRelativePath);
        DeprecatedFileCleanupService.CleanupRuntimeComponents(_paths, static () => { });
        Assert.True(File.Exists(payload));
        using var offline = new HttpClient(new Offline());
        using var restored = await Service(offline).PrepareAsync("Renamed", Descriptor, null);
        Assert.Equal("old", restored.Snapshot.Revision);
        Assert.Equal(oldBytes, File.ReadAllBytes(restored.ArtifactPaths["identity-adapter"]));
    }

    [Fact]
    public async Task ComponentScopesRemainBoundAcrossAwaitAndRestoreTheirParent()
    {
        var first = RuntimeComponentSnapshot.Parse(Catalog("one", [1]), int.MaxValue);
        var second = RuntimeComponentSnapshot.Parse(Catalog("two", [2]), int.MaxValue);
        using var parent = RuntimeComponentContext.Use(first);
        var child = Task.Run(async () =>
        {
            using var scope = RuntimeComponentContext.Use(second);
            await Task.Yield();
            return RuntimeComponentContext.Current.Revision;
        });
        await Task.Yield();
        Assert.Equal("one", RuntimeComponentContext.Current.Revision);
        Assert.Equal("two", await child);
        Assert.Equal("one", RuntimeComponentContext.Current.Revision);
    }

    [Fact]
    public void UnsupportedArtifactProtocolAndInvertedRulesAreRejected()
    {
        var root = JsonNode.Parse(Catalog("bad", [1]))!;
        root["artifacts"]![0]!["protocolVersion"] = 2;
        Assert.Throws<InvalidDataException>(() => RuntimeComponentSnapshot.Parse(root.ToJsonString(), int.MaxValue));
        root["artifacts"]![0]!["protocolVersion"] = 1;
        root["componentRules"]![0]!["minimumMinecraftVersion"] = "26.3";
        root["componentRules"]![0]!["exclusiveMaximumMinecraftVersion"] = "1.20";
        Assert.Throws<InvalidDataException>(() => RuntimeComponentSnapshot.Parse(root.ToJsonString(), int.MaxValue));
    }

    private RuntimeComponentService Service(HttpClient http) => new(_paths, new Logger(_paths.LogFile), http, launcherRelease: int.MaxValue);

    private string Catalog(string revision, byte[] bytes)
    {
        var catalog = JsonNode.Parse(_originalCatalog)!;
        catalog["revision"] = revision;
        catalog["artifacts"] = new JsonArray(new JsonObject
        {
            ["id"] = "identity-adapter", ["fileName"] = "adapter.jar", ["protocolVersion"] = 1,
            ["url"] = "https://github.com/MarkZamore/LANMinecraft-Components/releases/download/components-1/adapter.jar",
            ["sizeBytes"] = bytes.Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes))
        });
        catalog["componentRules"] = new JsonArray(new JsonObject
        {
            ["runtimeKind"] = "java", ["componentIds"] = new JsonArray("identity-adapter")
        });
        return catalog.ToJsonString();
    }

    public void Dispose()
    {
        RuntimeComponentContext.TestPathResolver = _previousResolver;
        TempTree.Delete(_paths.Root);
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Offline fixture"));
    }

    private sealed class Source(string catalog, byte[] payload) : HttpMessageHandler
    {
        internal string CatalogJson = catalog;
        internal byte[] Payload = payload;
        internal int ArtifactRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isCatalog = request.RequestUri!.Host == "raw.githubusercontent.com";
            if (!isCatalog) Interlocked.Increment(ref ArtifactRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(isCatalog ? Encoding.UTF8.GetBytes(CatalogJson) : Payload)
            });
        }
    }
}
