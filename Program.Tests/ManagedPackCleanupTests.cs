using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft.Tests;

public sealed class ManagedPackCleanupTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "pack-cleanup-" + Guid.NewGuid().ToString("N")));
    private readonly CatalogSource _entry = PackCatalogService.SeedSources().First();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ManagedPackCleanupTests() => _paths.Ensure();
    public void Dispose() => TempTree.Delete(_paths.Root);

    [Fact]
    public void ManagedSourceDropsUnlistedFilesAndKeepsPlayerContent()
    {
        var directory = Install(_entry, _entry.Source, completeLauncher: true);
        var extra = Write(directory, "mods/old/note.txt");
        var rootExtra = Write(directory, "extraneous.txt");
        var original = Write(directory, "mods/official.jar");
        var options = Write(directory, "options.txt");
        var texture = Write(directory, "ReSoUrCePaCkS/added.zip");
        var oldTexture = Write(directory, "nested/texturepacks/added.zip");
        var shader = Write(directory, "nested/SHADERPACKS/added.zip");
        var instance = Write(_paths.CombineUnderInstances(_entry.RelativePath), "custom-mod-storage/notes.dat");

        Assert.True(ManagedPackCleanup.IsManagedPack(_paths, Service(), _entry.RelativePath));
        Assert.Equal(3, Sweep(_entry.RelativePath));
        Assert.False(File.Exists(extra));
        Assert.False(Directory.Exists(Path.GetDirectoryName(extra)));
        Assert.False(File.Exists(rootExtra));
        Assert.All(new[] { original, options, texture, oldTexture, shader, instance }, path => Assert.True(File.Exists(path)));
        Assert.Equal(0, Sweep(_entry.RelativePath));
    }

    [Fact]
    public void AnUnverifiedSourceOrInterruptedSyncProtectsTheWholePack()
    {
        var directory = Install(_entry, new PackSyncSource("player", "personal-pack", "current"), completeLauncher: true);
        var unknown = Write(directory, "config/custom.dat");
        Assert.Equal(0, Sweep(_entry.RelativePath));

        WriteSource(directory, _entry.Source);
        var statePath = Path.Combine(directory, PortablePackSyncService.SyncStateFileName);
        var validState = File.ReadAllText(statePath);
        File.WriteAllText(statePath, "{ invalid");
        Assert.Equal(0, Sweep(_entry.RelativePath));
        File.WriteAllText(statePath, validState);
        Write(directory, PortablePackSyncService.ApplyPendingFileName);
        Assert.Equal(0, Sweep(_entry.RelativePath));
        Assert.True(File.Exists(unknown));
    }

    [Fact]
    public void HistoricalLibrarySourceIsRecognizedAndDirectoryJunctionIsNeverFollowed()
    {
        var historical = PackCatalogService.SeedSources().First(entry => entry.LegacySources.Count != 0);
        var directory = Install(historical, historical.LegacySources[0], completeLauncher: true);
        var outside = Write(_paths.Root, "outside/keep.txt");
        var redirected = Path.Combine(directory, "redirected");
        SavesFolderService.CreateJunction(redirected, Path.GetDirectoryName(outside)!);
        var surplus = Write(directory, "old-config.txt");

        Assert.True(ManagedPackCleanup.IsManagedPack(_paths, Service(), historical.RelativePath));
        Assert.Equal(1, Sweep(historical.RelativePath));
        Assert.False(File.Exists(surplus));
        Assert.True(File.Exists(outside));
        Assert.True(Directory.Exists(redirected));
    }

    [Fact]
    public async Task FreshLauncherMetadataProtectsVerifiedFilesBeforeFullSync()
    {
        var directory = Install(_entry, _entry.Source, completeLauncher: false);
        var old = Write(directory, "launcher/unverified-local.txt");
        var preset = "launcher/options-default.txt";
        var bytes = Encoding.UTF8.GetBytes("gamma:0.5\n");
        var manifest = Path.Combine(directory, PackManifestService.ManifestFileName);
        var fullDescriptor = File.ReadAllBytes(manifest);
        using var http = new HttpClient(new ReleaseHandler(_entry.RelativePath,
            new Dictionary<string, byte[]> { [preset] = bytes, [PackManifestService.ManifestFileName] = fullDescriptor }));
        var sync = Service(http);

        Assert.Equal(0, Sweep(_entry.RelativePath));
        Assert.True(File.Exists(old));
        Assert.True(await sync.RefreshLauncherDataAsync(_entry.RelativePath, CancellationToken.None));
        Assert.Equal(1, ManagedPackCleanup.Run(_paths, sync, _entry.RelativePath, static _ => { }, CancellationToken.None));
        Assert.False(File.Exists(old));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(directory, preset.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void AChangedSyncStateInvalidatesQueuedRemoval()
    {
        var directory = Install(_entry, _entry.Source, completeLauncher: true);
        var original = Write(directory, "mods/official.jar");
        var extra = Write(directory, "z-later.txt");
        var state = Path.Combine(directory, PortablePackSyncService.SyncStateFileName);
        var observed = 0;

        var removed = ManagedPackCleanup.Run(_paths, Service(), _entry.RelativePath, _ =>
        {
            if (++observed == 4) File.AppendAllText(state, " ");
        }, CancellationToken.None);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(original));
        Assert.True(File.Exists(extra));
    }

    [Fact]
    public void AbandonedPlanCleanupSkipsActiveAndUnmarkedDirectories()
    {
        const string ownerFile = ".pack-sync-plan-owner";
        const string ownerText = "LANMinecraft.PackSync download plan v1\n";
        var cutoff = DateTime.UtcNow.AddDays(-1);
        var abandoned = Stage(_paths.Launcher);
        var active = Stage(_paths.Launcher);
        var unknown = Stage(_paths.Launcher);
        Write(abandoned, ownerFile, ownerText);
        var activeMarker = Write(active, ownerFile, ownerText);
        Write(abandoned, "assets/extra.dat");
        Write(active, "assets/extra.dat");
        Write(unknown, "assets/extra.dat");
        foreach (var directory in new[] { abandoned, active, unknown }) Directory.SetLastWriteTimeUtc(directory, cutoff.AddDays(-1));
        using (new FileStream(activeMarker, FileMode.Open, FileAccess.Write, FileShare.Delete))
        {
            Assert.Equal(4, PortablePackSyncService.CleanupAbandonedPlans(_paths, _paths.Launcher, cutoff,
                static _ => { }, CancellationToken.None));
            Assert.True(Directory.Exists(active));
        }
        Assert.False(Directory.Exists(abandoned));
        Assert.True(Directory.Exists(unknown));

        static string Stage(string root) => Path.Combine(root, ".pack-sync-stage-plan-" + Guid.NewGuid().ToString("N"));
    }

    private PortablePackSyncService Service(HttpClient? http = null) => new(_paths, new Logger(_paths.LogFile), http);
    private int Sweep(string name) => ManagedPackCleanup.Run(_paths, Service(), name, static _ => { }, CancellationToken.None);

    private string Install(CatalogSource entry, PackSyncSource source, bool completeLauncher)
    {
        var directory = _paths.CombineUnderPacks(entry.RelativePath);
        Write(directory, PackManifestService.ManifestFileName, "{\"schemaVersion\":1}");
        WriteSource(directory, source);
        var statePaths = new[] { PackManifestService.ManifestFileName, "mods/official.jar" };
        var files = statePaths.ToDictionary(path => path, _ => new { sizeBytes = 1L, sha256 = new string('0', 64) });
        Write(directory, PortablePackSyncService.SyncStateFileName, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, revision = "installed", hasCompleteLauncherPaths = completeLauncher, files
        }, Json));
        return directory;
    }

    private static void WriteSource(string directory, PackSyncSource source) => Write(directory, PortablePackSyncService.SourceMarkerFileName,
        JsonSerializer.Serialize(new { schemaVersion = 1, source.Owner, source.Repo, source.Tag }, Json));

    private static string Write(string directory, string relative, string content = "stored")
    {
        var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class ReleaseHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _assets = new(StringComparer.Ordinal);

        public ReleaseHandler(string packName, IReadOnlyDictionary<string, byte[]> files)
        {
            var assets = files.Select(pair =>
            {
                var hash = Hash(pair.Value);
                var name = $"m-{hash[..12]}-{Path.GetFileName(pair.Key)}";
                _assets.Add(name, pair.Value);
                return new { name, kind = "jar", sha256 = hash, sizeBytes = (long)pair.Value.Length, covers = new[] { pair.Key } };
            }).ToArray();
            _assets.Add(PortablePackSyncService.PackManifestAssetName, JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, revision = "new", packId = packName,
                files = files.Select(pair => new { path = pair.Key, sizeBytes = (long)pair.Value.Length, sha256 = Hash(pair.Value) }),
                assets
            }));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = Uri.UnescapeDataString(request.RequestUri!.Segments[^1]);
            var response = _assets.TryGetValue(name, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
            return Task.FromResult(response);
        }

        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
