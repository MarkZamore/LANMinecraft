using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Minecraft.Tests;

public sealed class LocalAiDownloadPlanningTests : IDisposable
{
    private const string ModPath = "mods/verity-reviewed.jar";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanmc-ai-plan-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Jar = Zip(("META-INF/mods.toml", Encoding.UTF8.GetBytes("[[mods]]\nmodId = \"verity\"\n")));

    public void Dispose() => TempTree.Delete(_root);
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] Zip(params (string Path, byte[] Bytes)[] files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var (path, bytes) in files)
            {
                using var entry = zip.CreateEntry(path).Open();
                entry.Write(bytes);
            }
        return output.ToArray();
    }

    private static void Write(string directory, string relative, byte[] bytes)
    {
        var file = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, bytes);
    }

    private static byte[] Descriptor() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1, provider = "ollama", model = "qwen3:1.7b", modId = "verity",
        modPath = ModPath, modSha256 = Sha(Jar), reserveMemoryMb = 3072, language = "ru"
    });

    private (AppPaths Paths, string Pack, string Instance, string Home) Installation(bool descriptor = true, bool packJar = true)
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var pack = paths.CombineUnderPacks("IndependentPack");
        var instance = paths.CombineUnderInstances("IndependentPack");
        Directory.CreateDirectory(pack);
        if (descriptor) Write(pack, "launcher/local-ai.json", Descriptor());
        if (packJar) Write(pack, ModPath, Jar);
        Write(instance, ModPath, Jar);
        return (paths, pack, instance, Path.Combine(paths.Personal, "Home"));
    }

    private sealed record Distribution(LocalAiPin Pin, Dictionary<string, byte[]> HttpFiles,
        Dictionary<string, byte[]> CacheFiles)
    {
        public Dictionary<string, long> ExpectedArtifacts => CacheFiles.ToDictionary(
            file => file.Key.StartsWith("downloads/", StringComparison.Ordinal)
                ? "runtime:" + Pin.ArchiveSha256
                : "model:" + file.Key["models/blobs/sha256-".Length..],
            file => file.Value.LongLength, StringComparer.Ordinal);
    }

    private static Distribution Provider()
    {
        var archive = Zip(("ollama.exe", Encoding.ASCII.GetBytes("synthetic executable; never started")),
            ("lib/runtime.bin", [1, 2, 3]));
        byte[] configuration = [1, 2], weights = [12, 34, 56, 78];
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 2,
            config = new { digest = "sha256:" + Sha(configuration), size = configuration.Length },
            layers = new[] { new { digest = "sha256:" + Sha(weights), size = weights.Length } }
        });
        var pin = new LocalAiPin("1.2.3", new Uri("https://official.test/runtime.zip"),
            archive.Length, Sha(archive), Sha(manifest), manifest);
        return new(pin, new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [pin.ArchiveUri.AbsoluteUri] = archive,
            ["https://registry.ollama.ai/v2/library/qwen3/blobs/sha256:" + Sha(configuration)] = configuration,
            ["https://registry.ollama.ai/v2/library/qwen3/blobs/sha256:" + Sha(weights)] = weights
        }, new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["downloads/" + pin.ArchiveSha256 + ".zip"] = archive,
            ["models/blobs/sha256-" + Sha(configuration)] = configuration,
            ["models/blobs/sha256-" + Sha(weights)] = weights
        });
    }

    private static string Cache(AppPaths paths) => Path.Combine(paths.Launcher, "LocalAI");
    private static string? ExpectedModSha(string relative) => relative == ModPath ? Sha(Jar) : null;
    private static void SeedCache(AppPaths paths, Distribution provider)
    {
        foreach (var file in provider.CacheFiles) Write(Cache(paths), file.Key, file.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task PlanningRejectsMissingOrDifferentFrozenManifestModShaBeforeHttp(string? frozenSha)
    {
        var (paths, pack, _, _) = Installation();
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var http = new HttpClient(handler);
        var service = new LocalAiProvisioningService(paths, http, Provider().Pin);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.PlanDownloadsAsync(pack, _ => frozenSha, default));

        Assert.Empty(handler.Requests);
        Assert.False(Directory.Exists(Cache(paths)));
    }

    [Fact]
    public async Task ColdMetadataOnlyPlanBudgetsRuntimeAndEveryModelBlobWithoutHttp()
    {
        var (paths, metadata, _, _) = Installation(packJar: false);
        var provider = Provider();
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var http = new HttpClient(handler);
        var requestedManifestPaths = new List<string>();
        var service = new LocalAiProvisioningService(paths, http, provider.Pin);

        var plan = await service.PlanDownloadsAsync(metadata, path =>
        {
            requestedManifestPaths.Add(path);
            return ExpectedModSha(path);
        }, default);

        Assert.Equal(new[] { ModPath }, requestedManifestPaths);
        Assert.False(File.Exists(Path.Combine(metadata, ModPath)));
        Assert.Equal(3, plan.PendingArtifacts.Count);
        Assert.Equal(provider.HttpFiles.Values.Sum(bytes => bytes.LongLength), plan.PendingArtifacts.Values.Sum());
        Assert.Equal(provider.ExpectedArtifacts.Keys.OrderBy(id => id, StringComparer.Ordinal),
            plan.PendingArtifacts.Keys.OrderBy(id => id, StringComparer.Ordinal));
        Assert.All(provider.ExpectedArtifacts, expected => Assert.Equal(expected.Value, plan.PendingArtifacts[expected.Key]));
        Assert.Empty(handler.Requests);
        Assert.False(Directory.Exists(Cache(paths)));
    }

    [Fact]
    public async Task FullyVerifiedCacheIsExcludedFromBudgetAndAcquisitionUsesNoHttpOrCompletions()
    {
        var (paths, pack, _, _) = Installation();
        var provider = Provider();
        SeedCache(paths, provider);
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var http = new HttpClient(handler);
        var service = new LocalAiProvisioningService(paths, http, provider.Pin);
        var completed = new List<string>();

        var plan = await service.PlanDownloadsAsync(pack, ExpectedModSha, default);
        await service.AcquireDownloadsAsync(pack, plan,
            _ => throw new InvalidOperationException("Cached files must not consume a download budget"), completed.Add, default);

        Assert.Empty(plan.PendingArtifacts);
        Assert.Equal(3, plan.Artifacts.Count);
        Assert.All(plan.Artifacts, artifact => Assert.True(artifact.Cached));
        Assert.Empty(handler.Requests);
        Assert.Empty(completed);
    }

    [Fact]
    public async Task AcquiredPlanCompletesEachVerifiedArtifactOnceAndLatePrepareMakesNoFurtherHttpRequests()
    {
        var (paths, pack, instance, home) = Installation();
        var provider = Provider();
        var downloadsAllowed = true;
        using var handler = new Handler((request, _) =>
        {
            Assert.True(downloadsAllowed, "Late preparation must consume only the acquired plan");
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(provider.HttpFiles[request.RequestUri!.AbsoluteUri]) };
        });
        using var http = new HttpClient(handler);
        var service = new LocalAiProvisioningService(paths, http, provider.Pin);
        var plan = await service.PlanDownloadsAsync(pack, ExpectedModSha, default);
        var progress = new Dictionary<string, List<RuntimePreparationProgress>>(StringComparer.Ordinal);
        var completed = new List<string>();

        await service.AcquireDownloadsAsync(pack, plan, id =>
        {
            var events = progress[id] = [];
            return new ImmediateProgress(events.Add);
        }, id =>
        {
            var artifact = plan.Artifacts.Single(file => file.Id == id);
            var file = Path.Combine(Cache(paths), artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file));
            Assert.Equal(artifact.Sha256, Sha(File.ReadAllBytes(file)));
            completed.Add(id);
        }, default);
        downloadsAllowed = false;
        var launch = await service.PrepareAsync(pack, instance, home, null, default, downloadPlan: plan);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(plan.PendingArtifacts.Keys.OrderBy(id => id, StringComparer.Ordinal),
            completed.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(3, completed.Distinct(StringComparer.Ordinal).Count());
        Assert.All(provider.ExpectedArtifacts, artifact =>
        {
            var last = progress[artifact.Key].Last();
            Assert.Equal(artifact.Value, last.TotalBytes);
            Assert.Equal(artifact.Value, last.DownloadedBytes);
        });
        Assert.NotNull(launch);
        Assert.True(File.Exists(launch.RuntimeExecutable));
        Assert.Equal(provider.Pin.ModelManifest,
            File.ReadAllBytes(Path.Combine(launch.ModelsDirectory, "manifests/registry.ollama.ai/library/qwen3/1.7b")));
    }

    [Fact]
    public async Task ModChangedAfterPlanningIsRejectedBeforeDownloadsOrCompletion()
    {
        var (paths, pack, _, _) = Installation();
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var http = new HttpClient(handler);
        var service = new LocalAiProvisioningService(paths, http, Provider().Pin);
        var plan = await service.PlanDownloadsAsync(pack, ExpectedModSha, default);
        Write(pack, ModPath, new byte[Jar.Length]);
        var completed = new List<string>();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.AcquireDownloadsAsync(pack, plan, _ => null, completed.Add, default));

        Assert.Empty(handler.Requests);
        Assert.Empty(completed);
        Assert.False(Directory.Exists(Cache(paths)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedDescriptorIncludingAiAddedToAnOrdinaryPlanRejectsAcquisitionAndLatePrepare(bool initiallyEnabled)
    {
        var (paths, pack, instance, home) = Installation(descriptor: initiallyEnabled);
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var http = new HttpClient(handler);
        var service = new LocalAiProvisioningService(paths, http, Provider().Pin);
        var plan = await service.PlanDownloadsAsync(pack, ExpectedModSha, default);
        var changed = JsonNode.Parse(Descriptor())!;
        changed["reserveMemoryMb"] = 4096;
        Write(pack, "launcher/local-ai.json", Encoding.UTF8.GetBytes(changed.ToJsonString()));
        var completed = new List<string>();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.AcquireDownloadsAsync(pack, plan, _ => null, completed.Add, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(pack, instance, home, null, default, downloadPlan: plan));

        Assert.Empty(handler.Requests);
        Assert.Empty(completed);
        Assert.False(Directory.Exists(Cache(paths)));
    }

    [Fact]
    public async Task CachedArtifactChangedAfterPlanningCannotCauseAnUnbudgetedDownload()
    {
        var (paths, pack, _, _) = Installation();
        var provider = Provider();
        SeedCache(paths, provider);
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var http = new HttpClient(handler);
        var service = new LocalAiProvisioningService(paths, http, provider.Pin);
        var plan = await service.PlanDownloadsAsync(pack, ExpectedModSha, default);
        Assert.Empty(plan.PendingArtifacts);
        Write(Cache(paths), "downloads/" + provider.Pin.ArchiveSha256 + ".zip", new byte[provider.Pin.ArchiveSizeBytes]);
        var completed = new List<string>();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.AcquireDownloadsAsync(pack, plan, _ => null, completed.Add, default));

        Assert.Empty(handler.Requests);
        Assert.Empty(completed);
    }

    [Fact]
    public async Task CancellationDuringFirstTransferDoesNotPublishOrCompleteAnyArtifact()
    {
        var (paths, pack, _, _) = Installation();
        var provider = Provider();
        using var cancelled = new CancellationTokenSource();
        using var handler = new Handler((request, _) => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CancellingStream(provider.HttpFiles[request.RequestUri!.AbsoluteUri], cancelled))
        });
        using var http = new HttpClient(handler);
        var service = new LocalAiProvisioningService(paths, http, provider.Pin);
        var plan = await service.PlanDownloadsAsync(pack, ExpectedModSha, default);
        var completed = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.AcquireDownloadsAsync(pack, plan, _ => null, completed.Add, cancelled.Token));

        Assert.True(cancelled.IsCancellationRequested);
        Assert.Single(handler.Requests);
        Assert.Empty(completed);
        Assert.All(provider.CacheFiles, file =>
            Assert.False(File.Exists(Path.Combine(Cache(paths), file.Key.Replace('/', Path.DirectorySeparatorChar)))));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(response(request, token));
        }
    }

    private sealed class ImmediateProgress(Action<RuntimePreparationProgress> report) : IProgress<RuntimePreparationProgress>
    {
        public void Report(RuntimePreparationProgress value) => report(value);
    }

    private sealed class CancellingStream(byte[] bytes, CancellationTokenSource cancelled) : MemoryStream(bytes, false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancelled.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
