using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft.Tests;

public sealed class PackInstanceArchiveTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), $"launcher-pack-archives-{Guid.NewGuid():N}"));
    private readonly PortablePackSyncService.KnownPack _published = PortablePackSyncService.KnownPacks.First();
    private const string Shader = "shaderpacks/Shader.zip";
    private const string Resource = "resourcepacks/Textures.zip";

    private string Pack => _paths.CombineUnderPacks(_published.RelativePath);
    private string Game => _paths.CombineUnderInstances(_published.RelativePath);
    private Logger CreateLogger() => new(_paths.LogFile);

    [Fact]
    public async Task IdleMaintenancePromotesLegacyArchivesWithoutMovingPlayerSettings()
    {
        InstallPublished();
        var others = Enumerable.Range(2, 24).Select(number => new CatalogSource(
            $"archive-fixture-{number}", $"Archive fixture {number}", new("ArchiveFixture", $"Pack-{number}", "pack-latest")))
            .ToArray();
        var first = PackCatalogService.SeedSources().First(source => source.RelativePath == _published.RelativePath);
        using var http = new HttpClient(new CatalogIndexHandler([first, .. others]));
        var catalog = new PackCatalogService(_paths, CreateLogger(), http, stagedStartup: true);
        await catalog.InitializeIndexAsync();
        Assert.True(catalog.HasOnlineCatalog);
        Assert.Equal(25, catalog.Sources.Count);
        foreach (var entry in others)
        {
            InstallPublished(entry.RelativePath, entry.Source);
            var game = _paths.CombineUnderInstances(entry.RelativePath);
            Write(game, Resource, Zip(entry.Id));
        }
        var shader = Zip("published shader");
        var resource = Zip("personal textures");
        var legacy = Zip("personal legacy textures");
        Write(Pack, Shader, shader);
        Write(Game, Shader, shader);
        Write(Game, Resource, resource);
        Write(Game, "texturepacks/Legacy.zip", legacy);
        Write(Game, "shaderpacks/Shader.zip.txt", "shader configuration"u8);
        Write(Game, "resourcepacks/Unpacked/pack.mcmeta", "personal unpacked resource pack"u8);
        Write(Game, "options.txt", "resourcePacks:[\"file/Textures.zip\"]"u8);
        WriteState(Shader);
        var inventory = Read(Pack, PortablePackSyncService.SyncStateFileName);

        Assert.Empty(Sweep(catalog: catalog, fullPass: true).DeferredPacks);
        AssertMirrored(Shader, shader);
        AssertMirrored(Resource, resource);
        AssertMirrored("texturepacks/Legacy.zip", legacy);
        AssertOtherPacksMigrated();

        Assert.Empty(Sweep(catalog: catalog, fullPass: true).DeferredPacks);
        AssertOtherPacksMigrated();
        using var instances = new PackInstanceService(_paths, CreateLogger());
        await instances.PrepareAsync(_published.RelativePath);

        AssertMirrored(Shader, shader);
        AssertMirrored(Resource, resource);
        AssertMirrored("texturepacks/Legacy.zip", legacy);
        Assert.Equal(inventory, Read(Pack, PortablePackSyncService.SyncStateFileName));
        Assert.Equal("shader configuration", ReadText(Game, "shaderpacks/Shader.zip.txt"));
        Assert.Equal("personal unpacked resource pack", ReadText(Game, "resourcepacks/Unpacked/pack.mcmeta"));
        Assert.Equal("resourcePacks:[\"file/Textures.zip\"]", ReadText(Game, "options.txt"));
        Assert.False(File.Exists(Under(Pack, "shaderpacks/Shader.zip.txt")));
        Assert.False(Directory.Exists(Under(Pack, "resourcepacks/Unpacked")));
        Assert.False(File.Exists(Under(Pack, "options.txt")));

        void AssertOtherPacksMigrated()
        {
            foreach (var entry in others)
            {
                var pack = _paths.CombineUnderPacks(entry.RelativePath);
                var game = _paths.CombineUnderInstances(entry.RelativePath);
                Assert.True(Directory.Exists(Under(pack, "mods")));
                Assert.True(Directory.Exists(Under(pack, "shaderpacks")));
                Assert.True(Directory.Exists(Under(pack, "resourcepacks")));
                Assert.True(File.Exists(Under(game, ".portable-instance.json")));
                AssertMirrored(pack, game, Resource, Zip(entry.Id));
            }
        }
    }

    [Fact]
    public async Task PublishedPacksExposeEmptyFoldersAndKeepUnlistedModsOutOfTheInstance()
    {
        InstallPublished();
        using var instances = new PackInstanceService(_paths, CreateLogger());

        await instances.PrepareAsync(_published.RelativePath);

        Assert.True(Directory.Exists(Under(Pack, "mods")));
        Assert.True(Directory.Exists(Under(Pack, "shaderpacks")));
        Assert.True(Directory.Exists(Under(Pack, "resourcepacks")));
        Assert.False(Directory.Exists(Under(Pack, "texturepacks")));
        Write(Pack, "mods/unlisted.jar", "unlisted personal mod"u8);
        var sharedShaders = Path.Combine(_paths.Personal, "shared-shaders");
        Write(sharedShaders, "Shared.zip", Zip("shared shader"));
        var archiveDirectory = Under(Game, "shaderpacks");
        SavesFolderService.CreateJunction(archiveDirectory, sharedShaders);
        Write(Pack, Shader, Zip("personal shader in Packs"));

        await instances.PrepareAsync(_published.RelativePath);

        Assert.False(File.Exists(Under(Game, "mods/unlisted.jar")));
        Assert.Equal("unlisted personal mod", ReadText(Pack, "mods/unlisted.jar"));
        Assert.False(File.Exists(Under(sharedShaders, "Shader.zip")));
        Assert.Equal(Zip("shared shader"), Read(sharedShaders, "Shared.zip"));
        Assert.Equal(Zip("personal shader in Packs"), Read(Pack, Shader));
    }

    [Fact]
    public async Task LegacyArchiveConflictKeepsBothArchivesPlayableAcrossRepeatedPreparation()
    {
        InstallPublished();
        var published = Zip("published version");
        var personal = Zip("player version");
        Write(Pack, Resource, published);
        Write(Game, Resource, personal);
        WriteState(Resource);
        using var instances = new PackInstanceService(_paths, CreateLogger());

        await instances.PrepareAsync(_published.RelativePath);
        var namesAfterMigration = ResourceNames(Game);
        await instances.PrepareAsync(_published.RelativePath);
        Sweep();

        Assert.Equal(namesAfterMigration, ResourceNames(Game));
        Assert.Equal(namesAfterMigration, ResourceNames(Pack));
        var preserved = Assert.Single(namesAfterMigration, name => name != Path.GetFileName(Resource));
        AssertMirrored(Resource, published);
        AssertMirrored("resourcepacks/" + preserved, personal);
    }

    [Fact]
    public async Task PreparationRestoresMissingInstanceArchiveAndDoesNotResurrectDeletedPersonalArchive()
    {
        InstallPublished();
        var shader = Zip("personal shader added in Packs");
        Write(Pack, Shader, shader);
        using var instances = new PackInstanceService(_paths, CreateLogger());
        await instances.PrepareAsync(_published.RelativePath);
        AssertMirrored(Shader, shader);

        File.Delete(Under(Game, Shader));
        await instances.PrepareAsync(_published.RelativePath);
        AssertMirrored(Shader, shader);

        File.Delete(Under(Pack, Shader));
        await instances.PrepareAsync(_published.RelativePath);
        Sweep();

        Assert.False(File.Exists(Under(Pack, Shader)));
        Assert.False(File.Exists(Under(Game, Shader)));
    }

    [Fact]
    public async Task PersonalShaderAndResourceArchivesDoNotChangeSyncedPeerHash()
    {
        InstallPublished();
        Write(Pack, Resource, Zip("textures in the published manifest"));
        WriteState(Resource);
        using var hasher = new PackHashService(_paths);
        var baselineHash = await hasher.CalculateAsync(Pack);
        Write(Pack, Shader, Zip("personal shader"));
        Write(Game, "resourcepacks/Extra.zip", Zip("personal textures"));
        using var instances = new PackInstanceService(_paths, CreateLogger());

        await instances.PrepareAsync(_published.RelativePath);

        AssertMirrored(Shader, Zip("personal shader"));
        AssertMirrored("resourcepacks/Extra.zip", Zip("personal textures"));
        Assert.Equal(baselineHash, await hasher.CalculateAsync(Pack));
    }

    [Fact]
    public void IdleMaintenanceDefersAnActivePackAndMigratesDespiteAnAbsentPublishedArchive()
    {
        InstallPublished();
        Write(Pack, Shader, Zip("published archive that will be downloaded again"));
        WriteState(Shader);
        File.Delete(Under(Pack, Shader));
        var legacy = Zip("personal textures");
        Write(Game, Resource, legacy);
        Write(Game, ".portable-instance.json",
            """{"schemaVersion":1,"files":null,"modFiles":null,"ownedFiles":null,"archives":null}"""u8);

        var active = Sweep(inUse: true);
        Assert.Contains(_published.RelativePath, active.DeferredPacks);
        Assert.False(File.Exists(Under(Pack, Resource)));
        Assert.Equal(legacy, Read(Game, Resource));

        var idle = Sweep();

        Assert.Empty(idle.DeferredPacks);
        AssertMirrored(Resource, legacy);
        Assert.False(File.Exists(Under(Pack, Shader)));
    }

    private void InstallPublished() => InstallPublished(_published.RelativePath, _published.Source);

    private void InstallPublished(string relativePath, PackSyncSource source)
    {
        _paths.Ensure();
        var pack = _paths.CombineUnderPacks(relativePath);
        Write(pack, PackManifestService.ManifestFileName,
            "{\"schemaVersion\":1,\"minecraftVersion\":\"1.21.1\",\"loader\":{\"type\":\"vanilla\"},\"clientJar\":\"client.jar\"}"u8);
        Write(pack, "client.jar", "client fixture"u8);
        Write(pack, PortablePackSyncService.SourceMarkerFileName,
            JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, source.Owner, source.Repo, source.Tag },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        WriteStateForPack(pack, []);
    }

    private void WriteState(params string[] publishedPaths) => WriteStateForPack(Pack, publishedPaths);

    private static void WriteStateForPack(string pack, IEnumerable<string> publishedPaths)
    {
        var files = publishedPaths.Concat([PackManifestService.ManifestFileName, "client.jar"])
            .ToDictionary(path => path, path =>
            {
                var bytes = Read(pack, path);
                return new { sizeBytes = bytes.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
            });
        Write(pack, PortablePackSyncService.SyncStateFileName,
            JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, revision = "published", files }));
    }

    private LauncherMaintenanceProgress Sweep(bool inUse = false, PackCatalogService? catalog = null, bool fullPass = false)
    {
        var logger = CreateLogger();
        var sync = new PortablePackSyncService(_paths, logger) { Catalog = catalog };
        var progress = new LauncherMaintenanceProgress(1, UpdateService.CurrentReleaseNumber, [], [], []);
        var run = new LauncherMaintenanceRun(fullPass, () => inUse, [_published.RelativePath],
            _ => inUse, progress, static _ => { }, static _ => { }, CancellationToken.None, static _ => { });
        LauncherMaintenancePass.Run(_paths, logger, sync, run);
        return progress;
    }

    private void AssertMirrored(string relative, byte[] expected) => AssertMirrored(Pack, Game, relative, expected);

    private static void AssertMirrored(string pack, string game, string relative, byte[] expected)
    {
        Assert.Equal(expected, Read(pack, relative));
        Assert.Equal(expected, Read(game, relative));
        if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(pack)!).DriveFormat == "NTFS")
            Assert.True(GuardedFileSystem.AreSameOrdinaryFile(Under(pack, relative), Under(game, relative)), relative);
    }

    private sealed class CatalogIndexHandler(CatalogSource[] entries) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/catalog.json", StringComparison.Ordinal) != true)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, packs = entries },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private static byte[] Zip(string text)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("archive-fixture.txt", CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(text);
        }
        return stream.ToArray();
    }

    private static string[] ResourceNames(string root) =>
        Directory.GetFiles(Under(root, "resourcepacks"), "*.zip")
            .Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray();

    private static string Under(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static byte[] Read(string root, string relative) => File.ReadAllBytes(Under(root, relative));

    private static string ReadText(string root, string relative) => Encoding.UTF8.GetString(Read(root, relative));

    private static void Write(string root, string relative, ReadOnlySpan<byte> bytes)
    {
        var path = Under(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose() => TempTree.Delete(_paths.Root);
}
