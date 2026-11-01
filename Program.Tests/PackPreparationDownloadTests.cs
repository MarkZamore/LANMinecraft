using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft.Tests;

public sealed class PackPreparationDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanmc-pack-downloads-" + Guid.NewGuid().ToString("N"));
    private const string Pack = PortablePackSyncService.DefaultPackRelativePath;
    private const string ModPath = "mods/assistant-fixture.jar";
    public void Dispose() => TempTree.Delete(_root);
    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);
    private static byte[] Zip(params (string Path, byte[] Data)[] files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var file in files) { using var stream = zip.CreateEntry(file.Path).Open(); stream.Write(file.Data); }
        return output.ToArray();
    }
    private static void Write(string directory, string relative, byte[] data)
    {
        var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, data);
    }

    [Fact]
    public async Task FirstPayloadAlreadyHasTheWholePackAndAiBudgetAndCachedPreparationNeedsNoPayload()
    {
        var release = Release.Create("one"); var paths = new AppPaths(_root); paths.Ensure();
        var progress = new Reports(); using var handler = new Handler(release); using var http = new HttpClient(handler);
        handler.BeforePayload = _ =>
        {
            var counters = progress.Counters;
            Assert.NotEmpty(counters);
            Assert.All(counters, counter => Assert.Equal(release.PendingBytes, counter.TotalBytes));
        };
        var ai = new LocalAiProvisioningService(paths, http); var coordinator = Coordinator(paths, http, ai);
        var result = await coordinator.PrepareAsync(Pack, progress, default);
        Assert.Equal(PackSyncOutcome.Installed, result.SyncResult.Outcome);
        Assert.Equal(release.PendingBytes, progress.Counters[0].TotalBytes);
        Assert.Equal(0, progress.Counters[0].DownloadedBytes);
        Assert.Equal(release.PendingBytes, progress.Counters[^1].DownloadedBytes);
        Assert.Equal(1d, progress.Counters[^1].Fraction);
        Assert.All(progress.Counters, counter => Assert.InRange(counter.DownloadedBytes, 0, counter.TotalBytes));
        Assert.Single(handler.Requests, request => request.Uri.AbsolutePath.EndsWith('/' + release.MetadataAsset, StringComparison.Ordinal));
        Assert.All(release.AiFiles.Keys, uri => Assert.Single(handler.Requests, request => request.Uri.AbsoluteUri == uri));

        var requestCount = handler.Requests.Count; handler.BeforePayload = null;
        var instance = paths.CombineUnderInstances(Pack); Write(instance, ModPath, release.Mod);
        var launch = await ai.PrepareAsync(paths.CombineUnderPacks(Pack), instance, Path.Combine(paths.Personal, "Home"), null, default, result.LocalAi);
        Assert.NotNull(launch); Assert.Equal(requestCount, handler.Requests.Count);
        var cached = await coordinator.PrepareAsync(Pack, new Reports(), default);
        Assert.Equal(PackSyncOutcome.UpToDate, cached.SyncResult.Outcome);
        Assert.Empty(cached.LocalAi.PendingArtifacts);
        Assert.Equal(requestCount + 1, handler.Requests.Count); // Only the remote manifest is checked.
    }

    [Fact]
    public async Task ResumedAndRetriedAiDownloadsStayInsideTheReservedBudget()
    {
        var release = Release.Create("resume"); var paths = new AppPaths(_root); paths.Ensure();
        var archiveHash = Sha(release.Archive); var firstBlob = release.Blobs[0];
        Write(paths.Launcher, "LocalAI/downloads/" + archiveHash + ".zip.part", release.Archive[..5]);
        Write(paths.Launcher, "LocalAI/models/blobs/sha256-" + Sha(firstBlob), firstBlob);
        var progress = new Reports(); using var handler = new Handler(release); using var http = new HttpClient(handler);
        handler.CorruptOnce = release.BlobUri(release.Blobs[1]);
        var expected = release.PendingBytes - firstBlob.Length;
        handler.BeforePayload = _ => Assert.Equal(expected, progress.Counters[0].TotalBytes);
        var result = await Coordinator(paths, http, new LocalAiProvisioningService(paths, http)).PrepareAsync(Pack, progress, default);
        Assert.Equal(PackSyncOutcome.Installed, result.SyncResult.Outcome);
        var archiveRequest = Assert.Single(handler.Requests, request => request.Uri.AbsoluteUri == release.ArchiveUri);
        Assert.Equal(5L, archiveRequest.Offset);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsoluteUri == release.BlobUri(firstBlob));
        Assert.Equal(2, handler.Requests.Count(request => request.Uri.AbsoluteUri == release.BlobUri(release.Blobs[1])));
        Assert.All(progress.Counters, counter =>
        {
            Assert.Equal(expected, counter.TotalBytes); Assert.InRange(counter.DownloadedBytes, 0, expected);
        });
        Assert.Contains(progress.Counters.Zip(progress.Counters.Skip(1)), pair => pair.Second.DownloadedBytes < pair.First.DownloadedBytes);
        Assert.Equal(expected, progress.Counters[^1].DownloadedBytes);
        Assert.Equal(1d, progress.Counters[^1].Fraction);
    }

    [Fact]
    public async Task FailedRemotePayloadFallsBackToInstalledRecipeInsteadOfDownloadingItsStagedReplacement()
    {
        var installed = Release.Create("installed"); var remote = Release.Create("remote");
        var paths = new AppPaths(_root); paths.Ensure();
        using var handler = new Handler(installed); using var http = new HttpClient(handler);
        var ai = new LocalAiProvisioningService(paths, http); var coordinator = Coordinator(paths, http, ai);
        var initial = await coordinator.PrepareAsync(Pack, null, default);
        var packDirectory = paths.CombineUnderPacks(Pack); var descriptor = File.ReadAllBytes(Path.Combine(packDirectory, "launcher/local-ai.json"));
        var count = handler.Requests.Count; handler.Release = remote; handler.FailedAsset = remote.ModAsset;
        var result = await coordinator.PrepareAsync(Pack, new Reports(), default);
        Assert.Equal(PackSyncOutcome.OfflineFallback, result.SyncResult.Outcome);
        Assert.Equal(initial.LocalAi.Requirements, result.LocalAi.Requirements);
        Assert.Equal(initial.LocalAi.Recipe!.Pin.ArchiveSha256, result.LocalAi.Recipe!.Pin.ArchiveSha256);
        Assert.Empty(result.LocalAi.PendingArtifacts);
        Assert.Equal(descriptor, File.ReadAllBytes(Path.Combine(packDirectory, "launcher/local-ai.json")));
        Assert.Equal(installed.Mod, File.ReadAllBytes(Path.Combine(packDirectory, ModPath)));
        Assert.DoesNotContain(handler.Requests.Skip(count), request => remote.AiFiles.ContainsKey(request.Uri.AbsoluteUri));
        Assert.False(PortablePackSyncService.IsUpdatePending(packDirectory));
    }

    [Fact]
    public async Task PreparedPlanCannotStartAnUnbudgetedLateDownloadOrAcceptAChangedRecipe()
    {
        var release = Release.Create("late"); var paths = new AppPaths(_root); paths.Ensure();
        using var handler = new Handler(release); using var http = new HttpClient(handler);
        var ai = new LocalAiProvisioningService(paths, http);
        var result = await Coordinator(paths, http, ai).PrepareAsync(Pack, null, default);
        var packDirectory = paths.CombineUnderPacks(Pack); var instance = paths.CombineUnderInstances(Pack); Write(instance, ModPath, release.Mod);
        var count = handler.Requests.Count;
        File.Delete(Path.Combine(paths.Launcher, "LocalAI/models/blobs/sha256-" + Sha(release.Blobs[0])));
        await Assert.ThrowsAsync<InvalidDataException>(() => ai.PrepareAsync(packDirectory, instance, Path.Combine(paths.Personal, "Home"), null, default, result.LocalAi));
        Assert.Equal(count, handler.Requests.Count);
        Write(paths.Launcher, "LocalAI/models/blobs/sha256-" + Sha(release.Blobs[0]), release.Blobs[0]);
        File.AppendAllText(Path.Combine(packDirectory, "launcher/provider-recipe.json"), " ");
        await Assert.ThrowsAsync<InvalidDataException>(() => ai.PrepareAsync(packDirectory, instance, Path.Combine(paths.Personal, "Home"), null, default, result.LocalAi));
        Assert.Equal(count, handler.Requests.Count);
    }

    private static PackPreparationDownloadService Coordinator(AppPaths paths, HttpClient http, LocalAiProvisioningService ai)
        => new(paths, new PortablePackSyncService(paths, new Logger(Path.Combine(paths.Root, "fixture.log")), http, (_, _) => true), ai);

    private sealed class Reports : IProgress<RuntimePreparationProgress>
    {
        private readonly List<RuntimePreparationProgress> _items = [];
        internal RuntimePreparationProgress[] Counters { get { lock (_items) return _items.Where(item => item.TotalBytes > 0).ToArray(); } }
        public void Report(RuntimePreparationProgress value) { lock (_items) _items.Add(value); }
    }

    private sealed record Release(Dictionary<string, byte[]> Assets, Dictionary<string, byte[]> AiFiles,
        string MetadataAsset, string ModAsset, byte[] Mod, byte[] Archive, byte[][] Blobs, string ArchiveUri, long PendingBytes)
    {
        internal string BlobUri(byte[] bytes) => "https://blobs.fixture.test/immutable/" + Sha(bytes);
        internal static Release Create(string version)
        {
            var mod = Zip(("META-INF/mods.toml", "[[mods]]\nmodId='assistant_fixture'\n"u8.ToArray()), ("fixture-version", Encoding.UTF8.GetBytes(version)));
            var archive = Zip(("ollama.exe", Encoding.UTF8.GetBytes("synthetic executable " + version)));
            byte[][] blobs = [Encoding.UTF8.GetBytes("config " + version), Encoding.UTF8.GetBytes("weights " + version)];
            var modelManifest = Json(new { schemaVersion = 2, config = new { digest = "sha256:" + Sha(blobs[0]), size = blobs[0].Length }, layers = new[] { new { digest = "sha256:" + Sha(blobs[1]), size = blobs[1].Length } } });
            var archiveUri = "https://downloads.fixture.test/provider-" + version + ".zip";
            var blobUrls = blobs.ToDictionary(Sha, blob => "https://blobs.fixture.test/immutable/" + Sha(blob));
            var recipe = Json(new
            {
                schemaVersion = 1, provider = "ollama",
                runtime = new { version = "2.3.4", archiveUrl = archiveUri, sizeBytes = archive.Length, sha256 = Sha(archive), installDirectory = "AppData/Local/AssistantFixture/provider" },
                model = new { reference = "team/fixture:reviewed", registryUrl = "https://models.fixture.test/", manifestSha256 = Sha(modelManifest), manifestBase64 = Convert.ToBase64String(modelManifest), blobUrls }
            });
            var descriptor = Json(new { schemaVersion = 1, provider = "ollama", model = "team/fixture:reviewed", modId = "assistant_fixture", modPath = ModPath, modSha256 = Sha(mod), reserveMemoryMb = 2048, recipeFile = "launcher/provider-recipe.json", recipeSha256 = Sha(recipe) });
            var packManifest = Json(new { schemaVersion = 1 });
            var metadata = Zip(("launcher/local-ai.json", descriptor), ("launcher/provider-recipe.json", recipe));
            var metadataAsset = "r-" + Sha(metadata)[..12] + "-launcher.zip";
            var modAsset = "m-" + Sha(mod)[..12] + "-assistant-fixture.jar";
            var packAsset = "m-" + Sha(packManifest)[..12] + "-portable-pack.json";
            var files = new[] { (Path: ModPath, Data: mod), (Path: "launcher/local-ai.json", Data: descriptor), (Path: "launcher/provider-recipe.json", Data: recipe), (Path: PackManifestService.ManifestFileName, Data: packManifest) };
            var assets = new[] { (Name: metadataAsset, Kind: "zipRoot", Data: metadata, Covers: new[] { files[1].Path, files[2].Path }), (Name: modAsset, Kind: "jar", Data: mod, Covers: new[] { ModPath }), (Name: packAsset, Kind: "jar", Data: packManifest, Covers: new[] { PackManifestService.ManifestFileName }) };
            var manifest = Json(new { schemaVersion = 2, requiredLauncherFeatures = new[] { "local-ai-recipe-v1", "local-ai-blob-urls-v1" }, packId = Pack, revision = version, files = files.Select(file => new { path = file.Path, sizeBytes = file.Data.Length, sha256 = Sha(file.Data) }), assets = assets.Select(asset => new { name = asset.Name, kind = asset.Kind, sizeBytes = asset.Data.Length, sha256 = Sha(asset.Data), covers = asset.Covers }) });
            var release = assets.ToDictionary(asset => asset.Name, asset => asset.Data); release[PortablePackSyncService.PackManifestAssetName] = manifest;
            var ai = blobs.ToDictionary(blob => blobUrls[Sha(blob)], blob => blob); ai[archiveUri] = archive;
            return new(release, ai, metadataAsset, modAsset, mod, archive, blobs, archiveUri, mod.Length + packManifest.Length + archive.Length + blobs.Sum(blob => (long)blob.Length));
        }
    }

    private sealed record Request(Uri Uri, long? Offset);
    private sealed class Handler(Release release) : HttpMessageHandler
    {
        private readonly List<Request> _requests = [];
        internal Release Release { get; set; } = release;
        internal string? FailedAsset { get; set; }
        internal string? CorruptOnce { get; set; }
        internal Action<HttpRequestMessage>? BeforePayload { get; set; }
        internal IReadOnlyList<Request> Requests { get { lock (_requests) return _requests.ToArray(); } }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!; var name = uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..];
            var offset = request.Headers.Range?.Ranges.Single().From;
            lock (_requests) _requests.Add(new(uri, offset));
            var ai = Release.AiFiles.TryGetValue(uri.AbsoluteUri, out var bytes);
            if (!ai && !Release.Assets.TryGetValue(name, out bytes) || name == FailedAsset)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (ai || name != PortablePackSyncService.PackManifestAssetName && name != Release.MetadataAsset) BeforePayload?.Invoke(request);
            if (uri.AbsoluteUri == CorruptOnce) { bytes = bytes!.Select(value => (byte)(value ^ 1)).ToArray(); CorruptOnce = null; }
            var content = new ByteArrayContent(offset is { } start ? bytes![(int)start..] : bytes!);
            if (offset is { } from) content.Headers.ContentRange = new ContentRangeHeaderValue(from, bytes!.Length - 1, bytes.Length);
            return Task.FromResult(new HttpResponseMessage(offset is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = content });
        }
    }
}
