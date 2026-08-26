using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// A folder of mods becoming a pack: the whole of what somebody has to do to
/// make a build of their own.
/// </summary>
public sealed class PackAutoManifestServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-auto-manifest-{Guid.NewGuid():N}");

    public void Dispose() => TempTree.Delete(_root);

    /// <summary>Answers as meta.fabricmc.net does, newest first with a stable flag.</summary>
    private sealed class FabricMeta : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            LastUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                [
                  {"loader": {"version": "0.19.4", "stable": false}},
                  {"loader": {"version": "0.19.3", "stable": true}},
                  {"loader": {"version": "0.19.2", "stable": true}}
                ]
                """)
            });
        }
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new HttpRequestException("no network");
    }

    private (AppPaths Paths, string Pack) MakePack(string name, int fabricJars, string range = ">=1.18.2 <1.19")
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var pack = Path.Combine(paths.Packs, name);
        Directory.CreateDirectory(Path.Combine(pack, "mods"));
        for (var index = 0; index < fabricJars; index++)
        {
            using var file = File.Create(Path.Combine(pack, "mods", $"mod{index}.jar"));
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            using var entry = archive.CreateEntry("fabric.mod.json").Open();
            entry.Write(Encoding.UTF8.GetBytes(
                $$$"""{"schemaVersion":1,"id":"m{{{index}}}","version":"1.0.0","depends":{"minecraft":"{{{range}}}"}}"""));
        }
        return (paths, pack);
    }

    private PackAutoManifestService Service(AppPaths paths, HttpMessageHandler handler) =>
        new(paths, new Logger(Path.Combine(_root, "log.txt")), new HttpClient(handler));

    /// <summary>
    /// Jars in a folder, and nothing else. What comes out is the same file a
    /// pack author would have written, with the client jar left for the
    /// launcher to fetch.
    /// </summary>
    [Fact]
    public async Task AFolderOfMods_WritesItsOwnManifest()
    {
        var (paths, pack) = MakePack("My Pack", fabricJars: 8);
        var meta = new FabricMeta();

        var written = await Service(paths, meta).EnsureAsync("My Pack", CancellationToken.None);

        Assert.True(written);
        Assert.Contains("1.18.2", meta.LastUrl!, StringComparison.Ordinal);

        var descriptor = PackManifestService.Load(pack);
        Assert.Equal("1.18.2", descriptor.MinecraftVersion);
        Assert.Equal(PackLoaderKind.Fabric, descriptor.Loader.Type);
        // The newest stable build, not the newest of any kind.
        Assert.Equal("0.19.3", descriptor.Loader.Version);
        Assert.Equal("", descriptor.ClientJar);

        // And the folder is now a pack by every test the launcher applies.
        Assert.True(MinecraftProcessService.HasPackData(pack));
    }

    /// <summary>A folder with mods is offered before it has ever been started.</summary>
    [Fact]
    public void AFolderOfMods_IsOfferedBeforeItHasAManifest()
    {
        var (_, pack) = MakePack("Fresh", fabricJars: 8);
        Assert.False(PackManifestService.HasManifest(pack));
        Assert.True(MinecraftProcessService.HasPackData(pack));
    }

    /// <summary>Written once; a second press changes nothing.</summary>
    [Fact]
    public async Task APackThatAlreadySaysWhatItIs_IsLeftAlone()
    {
        var (paths, pack) = MakePack("My Pack", fabricJars: 8);
        var service = Service(paths, new FabricMeta());
        await service.EnsureAsync("My Pack", CancellationToken.None);
        var manifest = Path.Combine(pack, PackManifestService.ManifestFileName);
        var first = File.ReadAllText(manifest);

        Assert.False(await service.EnsureAsync("My Pack", CancellationToken.None));
        Assert.Equal(first, File.ReadAllText(manifest));
    }

    /// <summary>
    /// A folder the jars do not agree about gets no file and no guess, and the
    /// reason is something a person can act on.
    /// </summary>
    [Fact]
    public async Task AFolderTheModsDisagreeAbout_IsRefusedWithAReason()
    {
        var (paths, pack) = MakePack("Muddle", fabricJars: 3);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => Service(paths, new FabricMeta()).EnsureAsync("Muddle", CancellationToken.None));

        Assert.Contains("too few", error.Message, StringComparison.Ordinal);
        Assert.False(PackManifestService.HasManifest(pack));
    }

    /// <summary>
    /// The mods can be read without the internet; which build of the loader to
    /// use cannot. That is said as its own thing, because it is the one a
    /// player can fix by reconnecting.
    /// </summary>
    [Fact]
    public async Task WithoutTheInternet_TheLoaderVersionIsWhatIsMissing()
    {
        var (paths, pack) = MakePack("My Pack", fabricJars: 8);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => Service(paths, new Offline()).EnsureAsync("My Pack", CancellationToken.None));

        Assert.Contains("Fabric", error.Message, StringComparison.Ordinal);
        Assert.Contains("1.18.2", error.Message, StringComparison.Ordinal);
        Assert.False(PackManifestService.HasManifest(pack));
    }

    /// <summary>
    /// A pack that comes from somewhere is not this feature's business: its
    /// manifest arrives with its files, and writing one first would tell the
    /// sync it was already installed.
    /// </summary>
    [Fact]
    public async Task APackWithASourceOfItsOwn_IsNotGuessedAt()
    {
        var (paths, pack) = MakePack("Downloaded", fabricJars: 8);
        File.WriteAllText(
            Path.Combine(pack, PortablePackSyncService.SourceMarkerFileName),
            JsonSerializer.Serialize(new { owner = "someone", repository = "a-pack" }));

        Assert.False(await Service(paths, new FabricMeta())
            .EnsureAsync("Downloaded", CancellationToken.None));
        Assert.False(PackManifestService.HasManifest(pack));
    }
}
