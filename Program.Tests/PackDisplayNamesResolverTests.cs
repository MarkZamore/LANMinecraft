using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Minecraft.Tests;

public sealed class PackDisplayNamesResolverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pack-display-names-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TempTree.Delete(root);

    [Fact]
    public void AVerifiedNameResolvesCurrentPathStableIdAndHistoricalPath()
    {
        var paths = Paths();
        using var names = new PackDisplayNames(paths);
        var pack = Pack("future-stable-id", "original-folder", "Future author title", "old-folder");
        Assert.True(names.Publish([pack]));
        Assert.Equal("Future author title", names.Resolve("original-folder", "Stale folder label"));
        Assert.Equal("Future author title", names.ResolveId("future-stable-id", "Stale ID label"));
        Assert.Equal("Future author title", names.Resolve("old-folder", "Old folder title"));
        Assert.False(names.Publish([pack]));
        Assert.Equal("original-folder", pack.Entry.RelativePath);
        Assert.Equal("future-stable-id", pack.Entry.Id);
        Assert.Equal("Future author title", pack.Content.Name);
    }

    [Fact]
    public void RenamingPublishedContentUpdatesEveryDisplayWithoutChangingProductionIdentity()
    {
        const string id = "attack-on-titan";
        const string path = "Awakening Of The Titans";
        const string renamed = "A renamed collection";
        var paths = Paths();
        using var names = new PackDisplayNames(paths);
        var pack = Pack(id, path, path);
        names.Publish([pack]);
        Assert.True(SteamId64.TryFrom(76561198986239755, out var steamId));
        var peer = new PeerViewModel { SteamId = steamId, PackName = path, DisplayNames = names,
            ProtocolVersion = PortableFormat.ProtocolVersion };
        var world = new WorldViewModel { Name = "World", Path = "unchanged-world-path",
            BuildName = path, BuildRelativePath = path, DisplayNames = names };
        var notices = new PeerArrivalNotifier();
        Assert.Empty(notices.Observe([peer]));
        peer.IsMinecraftRunning = true;
        Assert.Equal("Зашёл в " + path, Assert.Single(notices.Observe([peer])).Body);
        var before = WindowTitleSettings.Load(Path.Combine(paths.Packs, path), Runtime(), Path.Combine(paths.Instances, path));

        // The already validated content name is the only changed production value.
        pack.Content.Name = renamed;
        Assert.True(names.Publish([pack]));
        Assert.Equal(renamed, pack.Name);
        Assert.Equal(renamed, names.Resolve(path, path));
        Assert.Equal(renamed, names.ResolveId(id, id));
        Assert.Equal(renamed, peer.DisplayPackName);
        Assert.Contains(renamed, peer.StatusText, StringComparison.Ordinal);
        Assert.Equal("World (" + renamed + ")", world.DisplayName);
        var after = WindowTitleSettings.Load(Path.Combine(paths.Packs, path), Runtime(), Path.Combine(paths.Instances, path));
        Assert.Equal(renamed, after.Text);
        Assert.NotEqual(before.ContentSha256, after.ContentSha256);
        Assert.Empty(notices.Observe([peer]));
        peer.IsMinecraftRunning = false;
        Assert.Empty(notices.Observe([peer]));
        peer.IsMinecraftRunning = true;
        Assert.Equal("Зашёл в " + renamed, Assert.Single(notices.Observe([peer])).Body);
        Assert.Equal(id, pack.Entry.Id);
        Assert.Equal(path, pack.Entry.RelativePath);
        Assert.Equal(path, pack.Content.RelativePath);
        Assert.Equal(path, peer.PackIdentity);
        Assert.Equal(path, world.BuildRelativePath);
        Assert.Equal(path, world.BuildName);
        Assert.Equal("unchanged-world-path", world.Path);
    }

    [Fact]
    public void ACaseOnlyVerifiedRenameOverridesTheEmbeddedSpellingInEveryDisplay()
    {
        const string path = "All the Mods 9 Modified";
        const string oldName = "All The Mods 9 Modified";
        const string renamed = "all THE mods 9 modified";
        var paths = Paths();
        using var names = new PackDisplayNames(paths);
        names.Publish([Pack("stable", path, oldName)]);
        var firstTitle = WindowTitleSettings.Load(Path.Combine(paths.Packs, path), Runtime(), Path.Combine(paths.Instances, path));
        var pack = Pack("stable", path, renamed);
        Assert.True(names.Publish([pack]));
        Assert.Equal(renamed, pack.Name);
        Assert.Equal(renamed, names.Resolve(path, oldName));
        Assert.Equal(renamed, names.ResolveId("stable", oldName));
        Assert.True(SteamId64.TryFrom(76561198986239755, out var id));
        var peer = new PeerViewModel { SteamId = id, PackName = path, DisplayNames = names };
        var world = new WorldViewModel { Name = "World", Path = "world", BuildRelativePath = path,
            BuildName = oldName, DisplayNames = names };
        Assert.Equal(renamed, peer.DisplayPackName);
        Assert.Equal("World (" + renamed + ")", world.DisplayName);
        var newTitle = WindowTitleSettings.Load(Path.Combine(paths.Packs, path), Runtime(), Path.Combine(paths.Instances, path));
        Assert.Equal(renamed, newTitle.Text);
        Assert.NotEqual(firstTitle.ContentSha256, newTitle.ContentSha256);
        Assert.Equal(path, peer.PackIdentity);
        Assert.Equal(path, world.BuildRelativePath);
    }

    [Fact]
    public void AVerifiedCustomNameDoesNotInheritTheEmbeddedFallbackSpelling()
    {
        const string path = "All the Mods 9 Modified";
        const string custom = "Author's renamed collection";
        using var names = new PackDisplayNames(Paths());
        var pack = Pack("stable", path, custom);
        names.Publish([pack]);
        Assert.Equal(custom, pack.Name);
        Assert.Equal(custom, names.Resolve(path, path));
        Assert.Equal(custom, PackDisplayName.For(path, custom));
        names.Publish([]);
        Assert.Equal("All The Mods 9 Modified", names.Resolve(path, path));
        Assert.Equal(custom, names.Resolve(path, custom));
    }

    [Fact]
    public void AReplacementSnapshotRemovesRetiredNamesAndAnEmptySnapshotClearsThem()
    {
        using var names = new PackDisplayNames(Paths());
        names.Publish([Pack("first", "one", "First title"), Pack("second", "two", "Second title")]);
        names.Publish([Pack("second", "two", "New second title")]);
        Assert.Equal("Local one", names.Resolve("one", "Local one"));
        Assert.Equal("Local first", names.ResolveId("first", "Local first"));
        Assert.Equal("New second title", names.Resolve("two", "Local two"));
        Assert.True(names.Publish([]));
        Assert.Equal("Local two", names.Resolve("two", "Local two"));
    }

    [Fact]
    public void SeparateInstallationsAndSiblingPathsCannotBorrowEachOthersNames()
    {
        var firstPaths = Paths("first"); var secondPaths = Paths("second");
        using var first = new PackDisplayNames(firstPaths);
        using var second = new PackDisplayNames(secondPaths);
        first.Publish([Pack("stable", "folder", "First installation")]);
        second.Publish([Pack("stable", "folder", "Second installation")]);
        Assert.Equal("First installation", PackDisplayNames.ResolveDirectory(Path.Combine(firstPaths.Packs, "folder"), "Local"));
        Assert.Equal("Second installation", PackDisplayNames.ForInstallation(secondPaths, "folder", "Local"));
        Assert.Equal("Local sibling", PackDisplayNames.ResolveDirectory(firstPaths.Packs + "-other\\folder", "Local sibling"));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void GameTitleUsesTheSameVerifiedNameAndBindsItsFingerprintToRename()
    {
        var paths = Paths();
        using var names = new PackDisplayNames(paths);
        names.Publish([Pack("stable", "folder", "Published title")]);
        var game = Path.Combine(paths.Instances, "folder");
        var pack = Path.Combine(paths.Packs, "folder");
        var first = WindowTitleSettings.Load(pack, Runtime(), game);
        Assert.Equal("Published title", first.Text);
        names.Publish([Pack("stable", "folder", "Renamed published title")]);
        var renamed = WindowTitleSettings.Load(pack, Runtime(), game);
        Assert.Equal("Renamed published title", renamed.Text);
        Assert.NotEqual(first.ContentSha256, renamed.ContentSha256);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void PeerAndWorldLabelsUseMetadataWithoutChangingStoredOrWireIdentity()
    {
        var paths = Paths();
        using var names = new PackDisplayNames(paths);
        names.Publish([Pack("stable", "source-folder", "One shared title")]);
        Assert.True(SteamId64.TryFrom(76561198986239755, out var id));
        var presence = new SteamPeerPresence { SteamId = id, PackName = "source-folder",
            ProtocolVersion = PortableFormat.ProtocolVersion, State = SteamPresenceCodec.StateInGame };
        var peer = new PeerViewModel { SteamId = id, DisplayNames = names };
        peer.Apply(presence, "");
        var world = new WorldViewModel { Name = "World", Path = "unchanged-world-path",
            BuildName = "Old snapshot title", BuildRelativePath = "source-folder", DisplayNames = names };
        Assert.Equal("One shared title", peer.DisplayPackName);
        Assert.Contains("One shared title", peer.DisplayName, StringComparison.Ordinal);
        Assert.Equal("World (One shared title)", world.DisplayName);
        Assert.Equal("source-folder", peer.PackIdentity);
        Assert.Equal("source-folder", peer.PackName);
        Assert.Equal("source-folder", presence.PackName);
        Assert.Equal("source-folder", world.BuildRelativePath);
        Assert.Equal("Old snapshot title", world.BuildName);
        Assert.Equal("unchanged-world-path", world.Path);
        var wire = SteamPresenceCodec.Encode(presence);
        Assert.Contains("source-folder", wire.Values);
        Assert.DoesNotContain("One shared title", wire.Values);
    }

    [Fact]
    public void PublishingRenamedMetadataRefreshesLabelsWithoutAnnouncingAnotherGameStart()
    {
        using var names = new PackDisplayNames(Paths());
        names.Publish([Pack("stable", "folder", "Initial title")]);
        Assert.True(SteamId64.TryFrom(76561198986239755, out var id));
        var peer = new PeerViewModel { SteamId = id, PackName = "folder", DisplayNames = names,
            ProtocolVersion = PortableFormat.ProtocolVersion };
        var world = new WorldViewModel { Name = "World", Path = "world", BuildName = "folder", BuildRelativePath = "folder", DisplayNames = names };
        var notices = new PeerArrivalNotifier();
        Assert.Empty(notices.Observe([peer]));
        peer.IsMinecraftRunning = true;
        Assert.Equal("Зашёл в Initial title", Assert.Single(notices.Observe([peer])).Body);
        var peerChanges = new List<string?>(); var worldChanges = new List<string?>();
        peer.PropertyChanged += (_, change) => peerChanges.Add(change.PropertyName);
        world.PropertyChanged += (_, change) => worldChanges.Add(change.PropertyName);
        names.Publish([Pack("stable", "folder", "Renamed title")]);
        peer.RefreshPackDisplayName(); world.RefreshPackDisplayName();
        Assert.Equal("Renamed title", peer.DisplayPackName);
        Assert.Equal("World (Renamed title)", world.DisplayName);
        Assert.Contains(nameof(PeerViewModel.DisplayName), peerChanges);
        Assert.Contains(nameof(WorldViewModel.DisplayName), worldChanges);
        Assert.Empty(notices.Observe([peer]));
    }

    [Fact]
    public void SupportWorldAndPackLabelsUseMetadataWithoutWritingOwnershipOrPaths()
    {
        var paths = Paths(); paths.Ensure();
        using var names = new PackDisplayNames(paths);
        names.Publish([Pack("stable", "folder", "Shared report title", "legacy-folder")]);
        var world = Path.Combine(paths.Worlds, "World"); Directory.CreateDirectory(world);
        File.WriteAllBytes(Path.Combine(world, "level.dat"), new byte[16]);
        var metadata = Path.Combine(world, WorldMetadataService.MetadataFileName);
        File.WriteAllText(metadata, JsonSerializer.Serialize(new WorldMetadata
        {
            WorldId = "11111111-1111-4111-8111-111111111111", BuildName = "Old stored title", BuildRelativePath = "folder",
            OwnerIdentityName = "Owner", CurrentHolderIdentityName = "Holder"
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var original = File.ReadAllBytes(metadata);
        var row = Assert.Single(SupportDiagnosticSnapshotBuilder.ReadWorlds(paths));
        Assert.Equal("Shared report title", row.BuildName);
        Assert.Equal("folder", row.BuildRelativePath);
        Assert.Equal("Owner", row.OwnerName);
        Assert.Equal("Holder", row.HolderName);
        Assert.Equal("Shared report title", SupportDiagnosticSnapshotBuilder.ResolvePackName(paths, "folder"));
        Assert.Equal(original, File.ReadAllBytes(metadata));
        var legacy = Path.Combine(paths.Worlds, "Legacy world"); Directory.CreateDirectory(legacy);
        File.WriteAllBytes(Path.Combine(legacy, "level.dat"), new byte[16]);
        var legacyMetadata = Path.Combine(legacy, WorldMetadataService.MetadataFileName);
        File.WriteAllText(legacyMetadata, JsonSerializer.Serialize(new WorldMetadata
        {
            WorldId = "22222222-2222-4222-8222-222222222222", BuildName = "legacy-folder",
            OwnerIdentityName = "Original owner", CurrentHolderIdentityName = "Original holder"
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var legacyBefore = File.ReadAllBytes(legacyMetadata);
        var legacyRow = Assert.Single(SupportDiagnosticSnapshotBuilder.ReadWorlds(paths), entry => entry.Name == "Legacy world");
        Assert.Equal("Shared report title", legacyRow.BuildName);
        Assert.Equal("не записана", legacyRow.BuildRelativePath);
        Assert.Equal("Original owner", legacyRow.OwnerName);
        Assert.Equal(legacyBefore, File.ReadAllBytes(legacyMetadata));
    }

    [Fact]
    public void UnverifiedAuthorFilesCannotOverrideTheResolverOrTheTitle()
    {
        var paths = Paths();
        using var names = new PackDisplayNames(paths);
        var pack = Path.Combine(paths.Packs, "Local folder");
        var file = Path.Combine(pack, "library", "content.json"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{\"name\":\"Unverified different name\"}");
        var before = File.ReadAllBytes(file);
        Assert.Equal("Local folder", names.Resolve("Local folder", "Local folder"));
        Assert.Equal("Local folder", WindowTitleSettings.Load(pack, Runtime(), Path.Combine(paths.Instances, "Local folder")).Text);
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public void AmbiguousAliasesFallBackAndCannotOverrideAnotherPacksRealPath()
    {
        using var names = new PackDisplayNames(Paths());
        names.Publish([Pack("ambiguous", "one", "First", "two"), Pack("ambiguous", "two", "Second")]);
        Assert.Equal("First", names.Resolve("one", "Local"));
        Assert.Equal("Second", names.Resolve("two", "Local"));
        Assert.Equal("Local ambiguous", names.Resolve("ambiguous", "Local ambiguous"));
        Assert.Equal("Local ambiguous", names.ResolveId("ambiguous", "Local ambiguous"));
        names.Publish([Pack("two", "one", "First", "two"), Pack("second", "two", "Second")]);
        Assert.Equal("Second", names.Resolve("two", "Local folder"));
        Assert.Equal("First", names.ResolveId("two", "Local ID"));
    }

    [Fact]
    public void ACustomFolderMatchingACatalogIdKeepsItsOwnTitleAndLabels()
    {
        const string local = "catalog-stable-id";
        var paths = Paths();
        using var names = new PackDisplayNames(paths);
        names.Publish([Pack(local, "published-folder", "Published catalog title")]);
        var localPack = Path.Combine(paths.Packs, local);
        Directory.CreateDirectory(localPack);
        Assert.Equal("Published catalog title", names.ResolveId(local, "Local ID fallback"));
        Assert.Equal("Published catalog title", names.Resolve("published-folder", "Local path fallback"));
        Assert.Equal(local, names.Resolve(local, local));
        Assert.Equal(local, PackDisplayNames.ForInstallation(paths, local, local));
        Assert.Equal(local, WindowTitleSettings.Load(localPack, Runtime(), Path.Combine(paths.Instances, local)).Text);
        Assert.True(SteamId64.TryFrom(76561198986239755, out var steamId));
        var peer = new PeerViewModel { SteamId = steamId, PackName = local, DisplayNames = names };
        var world = new WorldViewModel { Name = "World", Path = "world", BuildRelativePath = local,
            BuildName = local, DisplayNames = names };
        Assert.Equal(local, peer.DisplayPackName);
        Assert.Equal("World (" + local + ")", world.DisplayName);
        Assert.Equal(local, SupportDiagnosticSnapshotBuilder.ResolvePackName(paths, local));
        Assert.Equal(local, peer.PackIdentity);
        Assert.Equal(local, world.BuildRelativePath);
        names.Publish([Pack(local, "published-folder", "Renamed catalog title")]);
        Assert.Equal("Renamed catalog title", names.ResolveId(local, "ID fallback"));
        Assert.Equal(local, names.Resolve(local, local));
    }

    [Fact]
    public void DisposingAnOldResolverCannotUnregisterItsReplacementOrRetainOldNames()
    {
        var paths = Paths();
        var old = new PackDisplayNames(paths); old.Publish([Pack("stable", "folder", "Old")]);
        using var replacement = new PackDisplayNames(paths); replacement.Publish([Pack("stable", "folder", "New")]);
        old.Dispose();
        Assert.Equal("Local", old.Resolve("folder", "Local"));
        Assert.Equal("New", PackDisplayNames.ResolveDirectory(Path.Combine(paths.Packs, "folder"), "Local"));
        replacement.Dispose();
        Assert.Equal("Local", PackDisplayNames.ResolveDirectory(Path.Combine(paths.Packs, "folder"), "Local"));
    }

    [Fact]
    public void TheExistingValidatedSeedAndCacheLoaderSuppliesNamesWithoutAnotherNetworkOrDiskParser()
    {
        var paths = Paths(); paths.Ensure();
        using var http = new HttpClient(new RefuseNetwork());
        var catalog = new PackCatalogService(paths, new Logger(paths.LogFile), http);
        Assert.NotEmpty(catalog.Packs);
        using var names = new PackDisplayNames(paths); names.Publish(catalog.Packs);
        foreach (var pack in catalog.Packs)
        {
            Assert.Equal(pack.Name, names.Resolve(pack.Entry.RelativePath, "Stale local label"));
            Assert.Equal(pack.Name, names.ResolveId(pack.Entry.Id, "Stale local label"));
        }
    }

    [Theory]
    [InlineData(false, "all-the-mods-9-modified", "All the Mods 9 Modified", "All the Mods 9 Modified", "all THE mods 9 modified")]
    [InlineData(true, "all-the-mods-9-modified", "All the Mods 9 Modified", "All the Mods 9 Modified", "all THE mods 9 modified")]
    [InlineData(false, "terrafirma-rebirth", "TerraFirma Rebirth 2", "TerraFirma Rebirth", "all THE mods 9 modified")]
    [InlineData(true, "terrafirma-rebirth", "TerraFirma Rebirth 2", "TerraFirma Rebirth", "all THE mods 9 modified")]
    [InlineData(false, "terrafirma-rebirth", "TerraFirma Rebirth 2", "TerraFirma Rebirth", "tERRAfIRMA rEBIRTH")]
    [InlineData(true, "terrafirma-rebirth", "TerraFirma Rebirth 2", "TerraFirma Rebirth", "tERRAfIRMA rEBIRTH")]
    public async Task VerifiedNetworkCacheAndStagedStartupPreserveTheAuthorsExactName(
        bool staged, string id, string path, string payloadPath, string authorName)
    {
        var paths = Paths(); paths.Ensure();
        using var host = new NameBundleHost(id, path, payloadPath, authorName);
        var logger = new Logger(paths.LogFile);
        var online = new PackCatalogService(paths, logger, host.Client, stagedStartup: staged);
        await online.RefreshOnceAsync();
        Assert.True(online.HasOnlineCatalog);
        Check(online);

        // Read the bytes saved by the production verifier through the real
        // startup cache path, with all HTTP requests now failing.
        host.Offline = true;
        var cached = new PackCatalogService(paths, logger, host.Client, stagedStartup: staged);
        await cached.RefreshOnceAsync();
        Assert.False(cached.HasOnlineCatalog);
        Check(cached);

        void Check(PackCatalogService catalog)
        {
            var pack = Assert.IsType<CatalogPack>(catalog.Find(path));
            Assert.Equal(id, pack.Entry.StorageId);
            Assert.Equal(path, pack.Entry.RelativePath);
            Assert.Equal(path, pack.Content.RelativePath);
            Assert.Equal(authorName, pack.Content.Name);
            Assert.Equal(authorName, pack.Name);
            Assert.Equal(authorName, catalog.GetDisplayName(path));
            using var names = new PackDisplayNames(paths);
            names.Publish(catalog.Packs);
            Assert.Equal(authorName, names.ResolveId(id, "Stale ID"));
            Assert.Equal(authorName, names.ResolveId(pack.Entry.Id, "Stale numbered ID"));
            Assert.Equal(authorName, names.Resolve(path, "Stale path"));
            Assert.Equal(authorName, WindowTitleSettings.Load(Path.Combine(paths.Packs, path), Runtime(),
                Path.Combine(paths.Instances, path)).Text);
        }
    }

    private sealed class NameBundleHost : HttpMessageHandler
    {
        private const string Revision = "verified-case-name";
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private static readonly byte[] Payload = [1];
        private readonly CatalogSource entry;
        private readonly byte[] bundle;
        public HttpClient Client { get; }
        public bool Offline { get; set; }

        public NameBundleHost(string id, string path, string payloadPath, string authorName)
        {
            Client = new HttpClient(this);
            entry = new CatalogSource(id, path, new PackSyncSource("Fixture", "Display-Names", "pack-latest"));
            using var seed = typeof(PackCatalogService).Assembly.GetManifestResourceStream("Minecraft.Library.minecraft-26-2.zip")!;
            using var original = new ZipArchive(seed, ZipArchiveMode.Read);
            using var metadata = original.GetEntry("library.json")!.Open();
            var content = JsonSerializer.Deserialize<CatalogDescription>(metadata, JsonOptions)!;
            content.Id = id;
            content.RelativePath = payloadPath;
            content.Name = authorName;
            content.Revision = Revision;
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                using (var writer = archive.CreateEntry("library.json").Open()) JsonSerializer.Serialize(writer, content, JsonOptions);
                using var source = original.GetEntry("preview.jpg")!.Open();
                using var image = archive.CreateEntry("preview.jpg").Open();
                source.CopyTo(image);
            }
            bundle = output.ToArray();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (Offline) return Reply(HttpStatusCode.ServiceUnavailable, []);
            if (path.EndsWith("catalog.json", StringComparison.Ordinal))
                return Json(new { schemaVersion = 1, packs = new[] { entry } });
            if (path.EndsWith("pack-manifest.json", StringComparison.Ordinal))
            {
                var hash = Convert.ToHexStringLower(SHA256.HashData(bundle));
                var payloadHash = Convert.ToHexStringLower(SHA256.HashData(Payload));
                return Json(new { schemaVersion = 1, revision = Revision, minecraftVersion = "26.3",
                    files = new[] { new { path = "mods/payload.jar", sha256 = payloadHash, sizeBytes = Payload.Length } },
                    assets = new[] { new { name = "payload.jar", kind = "jar", sha256 = payloadHash, sizeBytes = Payload.Length,
                        covers = new[] { "mods/payload.jar" } } },
                    library = new { name = "library-" + hash + ".zip", sha256 = hash, sizeBytes = bundle.Length, revision = Revision } });
            }
            if (path.EndsWith(".zip", StringComparison.Ordinal)) return Reply(HttpStatusCode.OK, bundle);
            return Reply(HttpStatusCode.NotFound, []);
        }

        private static Task<HttpResponseMessage> Json(object value) => Reply(HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
        private static Task<HttpResponseMessage> Reply(HttpStatusCode status, byte[] payload) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(payload) });
    }

    private AppPaths Paths(string name = "installation") => new(Path.Combine(root, name));
    private PreparedRuntime Runtime() => new(root, "profile", "java.exe", "client.jar",
        new PackRuntimeDescriptor(1, "1.21.1", new PackLoaderDescriptor(PackLoaderKind.NeoForge, "21.1.0"), "client.jar", "descriptor"));
    private static CatalogPack Pack(string id, string path, string name, params string[] aliases) => new()
    {
        Entry = new CatalogSource(id, path, new PackSyncSource("Fixture", "Future", "release")) { LegacyRelativePaths = [.. aliases] },
        Content = new CatalogDescription { Id = id, RelativePath = path, Name = name }, Preview = new BitmapImage()
    };
    private sealed class RefuseNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Display lookup must not make network requests.");
    }
}
