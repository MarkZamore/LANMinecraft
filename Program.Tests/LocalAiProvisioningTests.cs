using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Minecraft.Tests;

public sealed class LocalAiProvisioningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanmc-localai-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TempTree.Delete(_root);
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Zip(params (string Path, byte[] Bytes)[] files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var (path, bytes) in files)
            { using var stream = zip.CreateEntry(path).Open(); stream.Write(bytes); }
        return output.ToArray();
    }
    private static void Write(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
    }
    private static readonly byte[] Jar = Zip(("META-INF/mods.toml", Encoding.UTF8.GetBytes("[[mods]]\nmodId = \"verity\"\n")));
    private static string Descriptor(byte[] jar) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, provider = "ollama", model = "qwen3:1.7b", modId = "verity",
        modPath = "mods/verity-russian-reviewed.jar", modSha256 = Sha(jar), reserveMemoryMb = 3072, language = "ru"
    });
    private (AppPaths Paths, string Pack, string Instance, string Home) Installation(bool descriptor = true)
    {
        var paths = new AppPaths(_root); paths.Ensure();
        var pack = paths.CombineUnderPacks("IndependentPack"); var instance = paths.CombineUnderInstances("IndependentPack");
        if (descriptor)
        {
            Write(pack, "launcher/local-ai.json", Encoding.UTF8.GetBytes(Descriptor(Jar)));
            Write(pack, "mods/verity-russian-reviewed.jar", Jar); Write(instance, "mods/verity-russian-reviewed.jar", Jar);
        }
        return (paths, pack, instance, Path.Combine(paths.Personal, "Home"));
    }
    private static (LocalAiPin Pin, Dictionary<string, byte[]> Files) Distribution()
    {
        var archive = Zip(("ollama.exe", Encoding.ASCII.GetBytes("test executable")), ("lib/runtime.bin", [1, 2, 3]));
        byte[] config = [1, 2], weights = [12, 34, 56, 78];
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 2, config = new { digest = "sha256:" + Sha(config), size = config.Length },
            layers = new[] { new { digest = "sha256:" + Sha(weights), size = weights.Length } }
        });
        var pin = new LocalAiPin("1.2.3", new Uri("https://official.test/runtime.zip"), archive.Length, Sha(archive), Sha(manifest), manifest);
        return (pin, new Dictionary<string, byte[]>
        {
            [pin.ArchiveUri.AbsoluteUri] = archive,
            ["https://registry.ollama.ai/v2/library/qwen3/blobs/sha256:" + Sha(config)] = config,
            ["https://registry.ollama.ai/v2/library/qwen3/blobs/sha256:" + Sha(weights)] = weights
        });
    }

    [Fact]
    public async Task OrdinaryPackDoesNotDownloadOrModifySettings()
    {
        var (paths, pack, instance, home) = Installation(false);
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected download"));
        using var http = new HttpClient(handler);
        Assert.Null(await new LocalAiProvisioningService(paths, http, Distribution().Pin).PrepareAsync(pack, instance, home, null, default));
        Assert.False(Directory.Exists(Path.Combine(paths.Launcher, "LocalAI"))); Assert.False(Directory.Exists(instance));
    }

    [Fact]
    public void RussianDescriptorNeedsNoHelperAndDoesNotPinAFilenameInCode()
    {
        var (_, pack, _, _) = Installation(); var requirement = LocalAiProvisioningService.ReadRequirements(pack)!;
        Assert.Equal("ru", requirement.Language); Assert.Equal("mods/verity-russian-reviewed.jar", requirement.ModPath);
    }

    [Theory]
    [InlineData("modPath", "mods/../outside.jar")]
    [InlineData("modSha256", "incorrect")]
    [InlineData("provider", "remote")]
    [InlineData("model", "../escape:model")]
    [InlineData("language", "en/US")]
    [InlineData("schemaVersion", "one")]
    [InlineData("reserveMemoryMb", "3072")]
    public void DescriptorRejectsUnreviewedOrUnsafeBindings(string name, string value)
    {
        var (_, pack, _, _) = Installation(); var json = JsonNode.Parse(Descriptor(Jar))!; json[name] = value;
        Write(pack, "launcher/local-ai.json", Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.Throws<InvalidDataException>(() => LocalAiProvisioningService.ReadRequirements(pack));
    }

    [Fact]
    public void DescriptorRejectsDuplicateAndObsoleteHelperFields()
    {
        var (_, pack, _, _) = Installation();
        foreach (var suffix in new[] { ",\"model\":\"qwen3:1.7b\"}", ",\"requiredMods\":[]}" })
        {
            Write(pack, "launcher/local-ai.json", Encoding.UTF8.GetBytes(Descriptor(Jar)[..^1] + suffix));
            Assert.Throws<InvalidDataException>(() => LocalAiProvisioningService.ReadRequirements(pack));
        }
    }

    [Fact]
    public async Task ChangedVerityIsRejectedBeforeAnyDownload()
    {
        var (paths, pack, instance, home) = Installation(); Write(instance, "mods/verity-russian-reviewed.jar", [0, 1, 2]);
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected download"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalAiProvisioningService(paths, http, Distribution().Pin).PrepareAsync(pack, instance, home, null, default));
    }

    [Fact]
    public async Task PendingPackUpdateStopsPreparationBeforeDownloadsOrInstanceChanges()
    {
        var (paths, pack, instance, home) = Installation();
        Write(pack, PortablePackSyncService.ApplyPendingFileName, Encoding.UTF8.GetBytes("pending fixture"));
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected download"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LocalAiProvisioningService(paths, http, Distribution().Pin)
            .PrepareAsync(pack, instance, home, null, default));
        Assert.False(File.Exists(Path.Combine(instance, "config/verity-common.toml")));
    }

    [Fact]
    public async Task WrongModIdIsRejectedEvenWhenItsHashMatches()
    {
        var wrong = Zip(("META-INF/mods.toml", Encoding.UTF8.GetBytes("[[mods]]\nmodId=\"somebody_else\"\n")));
        var (_, pack, _, _) = Installation(false); Write(pack, "mods/verity.jar", wrong);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalAiProvisioningService.VerifyModAsync(pack,
            new LocalAiRequirements("qwen3:1.7b", "mods/verity.jar", Sha(wrong), 3072, "ru", "verity"), default));
    }

    [Fact]
    public async Task VerifiedCacheIsReusedAndSameLengthCorruptionIsRepaired()
    {
        var wanted = Encoding.ASCII.GetBytes("approved bytes"); var requests = 0;
        using var handler = new Handler(_ => { requests++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(wanted) }; });
        using var http = new HttpClient(handler); var store = new LocalAiFiles(_root, http); var uri = new Uri("https://official.test/blob");
        var file = await store.GetAsync(uri, wanted.Length, Sha(wanted), "cache/blob", "test", null, default);
        var time = File.GetLastWriteTimeUtc(file);
        await store.GetAsync(uri, wanted.Length, Sha(wanted), "cache/blob", "test", null, default);
        Assert.Equal(1, requests); Assert.Equal(time, File.GetLastWriteTimeUtc(file));
        File.WriteAllBytes(file, new byte[wanted.Length]); File.SetLastWriteTimeUtc(file, time);
        await store.GetAsync(uri, wanted.Length, Sha(wanted), "cache/blob", "test", null, default);
        Assert.Equal(2, requests); Assert.Equal(wanted, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task HashMismatchDoesNotPublishDownloadedFile()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 1, 1]) });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalAiFiles(_root, http).GetAsync(
            new Uri("https://official.test/blob"), 3, Sha([2, 2, 2]), "blob", "test", null, default));
        Assert.False(File.Exists(Path.Combine(_root, "blob"))); Assert.False(File.Exists(Path.Combine(_root, "blob.part")));
    }

    [Fact]
    public async Task InterruptedDownloadResumesWithRangeAndVerifiesCompleteContent()
    {
        var wanted = Encoding.ASCII.GetBytes("download content"); var requests = 0;
        using var handler = new Handler(request =>
        {
            if (requests++ == 0) return new(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream(wanted)) };
            Assert.Equal(1, request.Headers.Range!.Ranges.Single().From);
            var content = new ByteArrayContent(wanted[1..]);
            content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(1, wanted.Length - 1, wanted.Length);
            return new(HttpStatusCode.PartialContent) { Content = content };
        });
        using var http = new HttpClient(handler);
        var file = await new LocalAiFiles(_root, http).GetAsync(new Uri("https://official.test/blob"),
            wanted.Length, Sha(wanted), "blob", "test", null, default);
        Assert.Equal(wanted, File.ReadAllBytes(file)); Assert.Equal(2, requests);
    }

    [Fact]
    public async Task RuntimeTraversalCannotWriteOutsideOwnedDirectory()
    {
        Directory.CreateDirectory(_root); var archive = Path.Combine(_root, "runtime.zip");
        File.WriteAllBytes(archive, Zip(("ollama.exe", [1]), ("../outside", [2])));
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalAiFiles.ExtractRuntimeAsync(archive, Path.Combine(_root, "runtime"), null, default));
        Assert.False(File.Exists(Path.Combine(_root, "outside")));
    }

    [Fact]
    public async Task InterruptedExtractionRemovesOnlyStagesWithConfirmedLauncherOwnership()
    {
        var native = Path.Combine(_root, "native");
        var owned = "stage-" + Guid.NewGuid().ToString("N"); var unknown = "stage-" + Guid.NewGuid().ToString("N");
        Write(native, owned + "/incomplete.bin", [1, 2, 3]);
        Write(native, owned + ".owned", Encoding.UTF8.GetBytes("LANMinecraft.LocalAI staging v1\n"));
        Write(native, unknown + "/personal.bin", [4, 5, 6]); Write(native, "personal-folder/keep.bin", [7, 8]);
        var archive = Path.Combine(_root, "runtime.zip"); File.WriteAllBytes(archive, Zip(("ollama.exe", [9])));
        await LocalAiFiles.ExtractRuntimeAsync(archive, native, null, default);
        Assert.False(Directory.Exists(Path.Combine(native, owned))); Assert.False(File.Exists(Path.Combine(native, owned + ".owned")));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(Path.Combine(native, unknown, "personal.bin")));
        Assert.Equal(new byte[] { 7, 8 }, File.ReadAllBytes(Path.Combine(native, "personal-folder/keep.bin")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("# Player choices\r\n[AISettings]\r\naiEndpoint = 'http://player-choice'\r\naiModel = 'personal'\r\n[VoiceSettings]\r\nuseTTS = false\r\nttsProvider = 'PLAYER_CHOICE'\r\n[SpeechSettings]\r\nsttProvider = 'PLAYER_CHOICE'\r\n")]
    [InlineData("# The launcher does not parse or repair a mod's personal configuration\n[not valid TOML")]
    public async Task FullPreparationNeverCreatesOrRewritesModConfiguration(string? configuration)
    {
        var (paths, pack, instance, home) = Installation();
        var (pin, files) = Distribution();
        using var handler = new Handler(request => new(HttpStatusCode.OK)
            { Content = new ByteArrayContent(files[request.RequestUri!.AbsoluteUri]) });
        using var http = new HttpClient(handler);
        var service = new LocalAiProvisioningService(paths, http, pin);
        var file = Path.Combine(instance, "config", "verity-common.toml");
        if (configuration is not null) Write(instance, "config/verity-common.toml", Encoding.UTF8.GetBytes(configuration));
        var timestamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : default;

        var launch = await service.PrepareAsync(pack, instance, home, null, default);
        Assert.NotNull(launch);
        await service.PrepareAsync(pack, instance, home, null, default);

        if (configuration is null) Assert.False(Directory.Exists(Path.Combine(instance, "config")));
        else
        {
            Assert.Equal(Encoding.UTF8.GetBytes(configuration), File.ReadAllBytes(file));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(file));
        }
    }

    [Fact]
    public async Task CompletePreparationIsPortableRepairsRuntimeAndReusesDownloads()
    {
        var (paths, pack, instance, home) = Installation(); var (pin, files) = Distribution(); var requests = 0;
        using var handler = new Handler(request => { requests++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(files[request.RequestUri!.AbsoluteUri]) }; });
        using var http = new HttpClient(handler); var service = new LocalAiProvisioningService(paths, http, pin);
        var launch = (await service.PrepareAsync(pack, instance, home, null, default))!;
        Assert.True(File.Exists(launch.RuntimeExecutable)); Assert.StartsWith(home, launch.RuntimeExecutable);
        Assert.Equal(pin.ModelManifest, File.ReadAllBytes(Path.Combine(launch.ModelsDirectory, "manifests/registry.ollama.ai/library/qwen3/1.7b")));
        Assert.Equal(3, requests);
        var env = new Dictionary<string, string?> { ["OLLAMA_API_KEY"] = "private", ["CUDA_VISIBLE_DEVICES"] = "user-gpu", ["HOME"] = home };
        launch.ConfigureEnvironment(env);
        Assert.False(env.ContainsKey("OLLAMA_API_KEY")); Assert.Equal(launch.ModelsDirectory, env["OLLAMA_MODELS"]);
        Assert.Equal("user-gpu", env["CUDA_VISIBLE_DEVICES"]); Assert.Equal(home, env["HOME"]);
        await service.PrepareAsync(pack, instance, home, null, default); Assert.Equal(3, requests);
        File.WriteAllBytes(launch.RuntimeExecutable, [0, 0]);
        await service.PrepareAsync(pack, instance, home, null, default); Assert.Equal(3, requests);
        Assert.Equal(Encoding.ASCII.GetBytes("test executable"), File.ReadAllBytes(launch.RuntimeExecutable));
        var library = Path.Combine(Path.GetDirectoryName(launch.RuntimeExecutable)!, "lib/runtime.bin");
        File.WriteAllBytes(library, [0, 0, 0]);
        await service.PrepareAsync(pack, instance, home, null, default); Assert.Equal(3, requests);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(library));
    }

    [Fact]
    public async Task DescriptorChangedDuringPreparationCannotFinish()
    {
        var (paths, pack, instance, home) = Installation();
        var (pin, files) = Distribution();
        using var handler = new Handler(request =>
        {
            Write(pack, "launcher/local-ai.json", Encoding.UTF8.GetBytes(Descriptor(Jar).Replace("3072", "4096", StringComparison.Ordinal)));
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(files[request.RequestUri!.AbsoluteUri]) };
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new LocalAiProvisioningService(paths, http, pin)
            .PrepareAsync(pack, instance, home, null, default));
        Assert.Contains("descriptor changed during preparation", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(instance, "config")));
        Assert.False(Directory.Exists(Path.Combine(instance, "launcher-runtime")));
    }

    [Fact]
    public void ManifestCannotChangeWithoutUpdatingItsReviewedHash()
    {
        var (paths, _, _, _) = Installation(false); using var http = new HttpClient(); var pin = Distribution().Pin;
        Assert.Throws<InvalidDataException>(() => new LocalAiProvisioningService(paths, http, pin with { ModelManifest = [1, 2, 3] }));
    }

    [Fact]
    public void AiReserveChangesRecommendationAndAdvisoryWithoutChangingThePlayersHeap()
    {
        var (paths, pack, instance, _) = Installation(false);
        Write(pack, "mods/verity-russian-reviewed.jar", Jar);
        // Enough game content that a 16-GB machine reaches its advisory limit.
        var before = PackMemoryProfile.Measure(pack) with { ModCount = 600 };
        Write(pack, "launcher/local-ai.json", Encoding.UTF8.GetBytes(Descriptor(Jar)));
        var after = PackMemoryProfile.Measure(pack) with { ModCount = 600 };
        Assert.Equal(3072, after.LocalAiReserveMb);
        Assert.Equal(MemorySizingService.GetNativeReserveGb(before) + 3, MemorySizingService.GetNativeReserveGb(after));
        var measured = new MeasuredMemoryProfile(2048, 1024, 1);
        Assert.Equal(MemorySizingService.GetNativeReserveGb(before, measured: measured) + 3,
            MemorySizingService.GetNativeReserveGb(after, measured: measured));
        Assert.True(MemorySizingService.GetRecommendedMemoryGb(after, 16UL * 1024 * 1024 * 1024) <
                    MemorySizingService.GetRecommendedMemoryGb(before, 16UL * 1024 * 1024 * 1024));
        var settingsService = new SettingsService(paths);
        var settings = new AppSettings { ClientRelativePath = "IndependentPack", MaxHeapGb = MemorySizingService.MinHeapGb };
        settings.MemoryByPack["IndependentPack"] = settings.MaxHeapGb;
        settingsService.MeasurePack(settings.ClientRelativePath); settingsService.ApplyPackMemory(settings);
        Assert.Equal(MemorySizingService.MinHeapGb, settings.MaxHeapGb);
    }

    [Fact]
    public void AiDescriptorChangesAreReadDespiteUnchangedJarCacheLengthAndTimestamp()
    {
        var (paths, pack, _, _) = Installation(false); Write(pack, "mods/verity-russian-reviewed.jar", Jar);
        Assert.Equal(0, PackMemoryProfile.Measure(pack, paths.PackWeightsFile).LocalAiReserveMb);
        var descriptor = Path.Combine(pack, "launcher/local-ai.json");
        Write(pack, "launcher/local-ai.json", Encoding.UTF8.GetBytes(Descriptor(Jar)));
        Assert.Equal(3072, PackMemoryProfile.Measure(pack, paths.PackWeightsFile).LocalAiReserveMb);
        var time = File.GetLastWriteTimeUtc(descriptor);
        File.WriteAllText(descriptor, File.ReadAllText(descriptor).Replace("3072", "4096", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(descriptor, time);
        Assert.Equal(4096, PackMemoryProfile.Measure(pack, paths.PackWeightsFile).LocalAiReserveMb);
        File.WriteAllText(descriptor, "{\"schemaVersion\":\"bad\"}");
        Assert.Throws<InvalidDataException>(() => PackMemoryProfile.Measure(pack, paths.PackWeightsFile));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request));
    }
    private sealed class InterruptedStream(byte[] bytes) : MemoryStream(bytes, false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position == 0 ? base.ReadAsync(buffer[..1], cancellationToken) : ValueTask.FromException<int>(new IOException("fixture connection closed"));
    }
}
