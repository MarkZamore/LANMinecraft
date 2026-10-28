using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class TerraFirmaCatalogRenameTests : IDisposable
{
    private const string StableId = "terrafirma-rebirth";
    private const string LegacyFolder = "TerraFirma Rebirth";
    private const string Folder = "TerraFirma Rebirth 2";
    private const string CachedSummary = "A verified catalogue saved before the rename.";
    private const string CachedRevision = "verified-before-rename";
    private static readonly PackSyncSource LegacySource = new("MarkZamore", "TerraFirma-Rebirth", "pack-latest");
    private static readonly PackSyncSource Source = new("MarkZamore", "TerraFirma-Rebirth-2", "pack-latest");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "terrafirma-rename-test-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly Logger _logger;

    public TerraFirmaCatalogRenameTests()
    {
        _paths = new AppPaths(_root);
        _paths.Ensure();
        _logger = new Logger(_paths.LogFile);
    }

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public async Task LegacyDiskIndexAndOfflineSeedShowOneRenamedCardWithTheStableId()
    {
        WriteLegacyIndex();
        using var http = new HttpClient(new OfflineHandler());
        var catalog = new PackCatalogService(_paths, _logger, http);
        var settings = new AppSettings { AddedPackRelativePaths = [Folder] };

        var card = Assert.Single(catalog.PacksForDisplay(settings));
        Assert.Equal(StableId, card.Entry.Id);
        Assert.Equal(StableId, card.Content.Id);
        Assert.Equal(Folder, card.Entry.RelativePath);
        Assert.Equal(Folder, card.Content.RelativePath);
        Assert.Equal(Folder, card.Name);
        Assert.Equal(Source, card.Entry.Source);
        Assert.Null(catalog.Find(LegacyFolder));
        Assert.Single(catalog.Sources, entry => entry.Id == StableId);

        await catalog.RefreshOnceAsync();

        Assert.False(catalog.HasOnlineCatalog);
        Assert.Same(card, Assert.Single(catalog.PacksForDisplay(settings)));
        Assert.Null(catalog.Find(LegacyFolder));
        Assert.Single(catalog.Sources, entry => entry.Id == StableId);
        Assert.Equal(Source, catalog.SourceFor(Folder));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacySourceCacheIsReusedOfflineOnlyWhenItsBundlePassesVerification(bool damageBundle)
    {
        WriteLegacyIndex();
        var manifest = WriteLegacyCache(damageBundle);
        using var http = new HttpClient(new OfflineHandler());
        var catalog = new PackCatalogService(_paths, _logger, http);
        var card = Assert.Single(catalog.Packs, entry => entry.Entry.Id == StableId);

        Assert.Equal(Folder, card.Name);
        Assert.Equal(Folder, card.Content.RelativePath);
        Assert.Equal(StableId, card.Content.Id);
        if (damageBundle)
        {
            Assert.NotEqual(CachedSummary, card.Summary);
            Assert.NotEqual(CachedRevision, card.Content.Revision);
        }
        else
        {
            Assert.Equal(CachedSummary, card.Summary);
            Assert.Equal(CachedRevision, card.Content.Revision);
            Assert.Equal("cached_mod", Assert.Single(card.Content.Mods).Id);
        }

        await catalog.RefreshOnceAsync();

        Assert.False(catalog.HasOnlineCatalog);
        Assert.Same(card, catalog.Find(Folder));
        if (damageBundle)
            Assert.Throws<IOException>(() => catalog.SessionManifest(Source));
        else
            Assert.Equal(manifest, catalog.SessionManifest(Source));
    }

    [Theory]
    [InlineData(LegacyFolder, "MarkZamore", "TerraFirma-Rebirth", "pack-latest", true)]
    [InlineData(Folder, "MarkZamore", "TerraFirma-Rebirth", "pack-latest", true)]
    [InlineData(Folder, "AnotherOwner", "TerraFirma-Rebirth", "pack-latest", false)]
    [InlineData(Folder, "MarkZamore", "My-TerraFirma-Fork", "pack-latest", false)]
    [InlineData(Folder, "MarkZamore", "TerraFirma-Rebirth", "custom-version", false)]
    [InlineData("My Custom Pack", "MarkZamore", "TerraFirma-Rebirth", "pack-latest", false)]
    public void LegacyMarkerResolvesTheRenamedRepositoryWhileExplicitCustomSourcesKeepTheirMeaning(
        string folder, string owner, string repo, string tag, bool legacyOfficial)
    {
        var directory = _paths.CombineUnderPacks(folder);
        Directory.CreateDirectory(directory);
        var marker = new PackSyncSource(owner, repo, tag);
        var markerPath = Path.Combine(directory, PortablePackSyncService.SourceMarkerFileName);
        var markerJson = JsonSerializer.Serialize(new { schemaVersion = 1, owner, repo, tag }, JsonOptions);
        File.WriteAllText(markerPath, markerJson);
        using var http = new HttpClient(new OfflineHandler());
        var sync = new PortablePackSyncService(_paths, _logger, http);

        Assert.Equal(legacyOfficial ? Source : marker, sync.TryResolveSource(folder));
        Assert.Equal(markerJson, File.ReadAllText(markerPath));
    }

    private void WriteLegacyIndex()
    {
        Directory.CreateDirectory(CacheDirectory);
        File.WriteAllText(Path.Combine(CacheDirectory, "catalog.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            packs = new[] { new CatalogSource(StableId, LegacyFolder, LegacySource) }
        }, JsonOptions));
    }

    private string WriteLegacyCache(bool damageBundle)
    {
        using var seed = typeof(PackCatalogService).Assembly.GetManifestResourceStream("Minecraft.Library." + StableId + ".zip")!;
        using var original = new ZipArchive(seed, ZipArchiveMode.Read);
        using var metadata = original.GetEntry("library.json")!.Open();
        var description = JsonSerializer.Deserialize<CatalogDescription>(metadata, JsonOptions)!;
        description.Id = StableId;
        description.Name = LegacyFolder;
        description.RelativePath = LegacyFolder;
        description.Revision = CachedRevision;
        description.Summary = CachedSummary;
        description.Mods = [new CatalogMod { Id = "cached_mod", Name = "A cached mod" }];
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (var writer = archive.CreateEntry("library.json").Open())
                JsonSerializer.Serialize(writer, description, JsonOptions);
            using var imageSource = original.GetEntry("preview.jpg")!.Open();
            using var imageTarget = archive.CreateEntry("preview.jpg").Open();
            imageSource.CopyTo(imageTarget);
        }
        var bundle = output.ToArray();
        var bundleHash = Convert.ToHexStringLower(SHA256.HashData(bundle));
        byte[] payload = [1, 2, 3];
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            revision = CachedRevision,
            minecraftVersion = description.MinecraftVersion,
            files = new[] { new { path = "mods/payload.jar", sizeBytes = payload.Length, sha256 = payloadHash } },
            assets = new[] { new { name = "payload.jar", kind = "jar", sizeBytes = payload.Length, sha256 = payloadHash, covers = new[] { "mods/payload.jar" } } },
            library = new { name = "library-" + bundleHash + ".zip", sizeBytes = bundle.Length, sha256 = bundleHash, revision = CachedRevision }
        }, JsonOptions);
        if (damageBundle) bundle[^1] ^= 1;
        var sourceKey = LegacySource.Owner + "/" + LegacySource.Repo + "/" + LegacySource.Tag;
        var cacheName = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey))) + ".json";
        File.WriteAllText(Path.Combine(CacheDirectory, cacheName), JsonSerializer.Serialize(new
        {
            entry = new CatalogSource(StableId, LegacyFolder, LegacySource),
            manifest,
            bundle
        }, JsonOptions));
        return manifest;
    }

    private string CacheDirectory => Path.Combine(_paths.Launcher, "Library");

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
