using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minecraft;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("CatalogStartupProbe --fixture-root <root under artifacts> [--pack <relativePath>] [--output-root <directory under artifacts>] [--list-goal-ms 250] [--timeout-seconds 180]");
            return 0;
        }
        try
        {
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                var file = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
                return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
            };
            var options = Parse(args);
            var repository = FindRepository();
            var artifacts = Path.GetFullPath(Path.Combine(repository, "artifacts"));
            var fixture = UnderArtifacts(options.Fixture, repository, artifacts);
            var output = UnderArtifacts(options.Output ?? Path.Combine(artifacts, "CatalogStartupProbe", "runs"), repository, artifacts);
            if (output.Equals(fixture, StringComparison.OrdinalIgnoreCase) || output.StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The output root must not be inside the read-only input fixture.");
            var run = Path.Combine(output, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(run);
            var setup = Stopwatch.StartNew();
            var copied = CopyFixture(fixture, run);
            var setupMs = setup.Elapsed.TotalMilliseconds;
            Console.WriteLine("Report: " + Path.Combine(run, "report.json"));
            return MeasureAsync(run, options, copied, setupMs).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
            return 2;
        }
    }

    private static async Task<int> MeasureAsync(string root, Options options, FixtureFacts facts, double setupMs)
    {
        var paths = new AppPaths(root);
        var logger = new Logger(Path.Combine(root, "probe.log"));
        var warnings = 0;
        logger.LineWritten += line => { if (line.Contains("[WARN]", StringComparison.Ordinal)) Interlocked.Increment(ref warnings); };
        using var handler = new SnapshotHandler(Path.Combine(paths.Launcher, "Library"));
        using var http = new HttpClient(handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var clock = Stopwatch.StartNew();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var catalog = new PackCatalogService(paths, logger, http, stagedStartup: true);
        var labels = catalog.Sources.Select(source => new { source.Id, source.RelativePath, Name = catalog.GetDisplayName(source.RelativePath) }).ToArray();
        var listMs = clock.Elapsed.TotalMilliseconds;
        var first = new { catalog.IsIndexReady, catalog.IsReady, loadedPacks = catalog.Packs.Count, requests = handler.RequestCount };
        var selected = catalog.Sources.SingleOrDefault(source => source.RelativePath.Equals(options.Pack, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The selected pack must be advertised by the fixture index.");
        handler.AllowSelected(selected.Source);
        if (catalog.SourceFor(selected.RelativePath) is { } cachedSource) handler.AllowSelected(cachedSource);
        var stage = clock.Elapsed.TotalMilliseconds;
        await catalog.InitializeIndexAsync(timeout.Token);
        var indexMs = clock.Elapsed.TotalMilliseconds - stage;
        if (catalog.SourceFor(selected.RelativePath) is { } currentSource) handler.AllowSelected(currentSource);
        stage = clock.Elapsed.TotalMilliseconds;
        await catalog.EnsureLaunchReadyAsync(selected.RelativePath, timeout.Token);
        var selectedEnsureMs = clock.Elapsed.TotalMilliseconds - stage;
        var selectedReadyAtMs = clock.Elapsed.TotalMilliseconds;
        var source = catalog.SourceFor(selected.RelativePath) ?? throw new InvalidDataException("The selected source disappeared.");
        var frozen = catalog.SessionManifest(source);
        using var manifest = JsonDocument.Parse(frozen);
        var selectedFiles = manifest.RootElement.GetProperty("files").GetArrayLength();
        var selectedLegacyFiles = manifest.RootElement.TryGetProperty("legacyFiles", out var legacy) && legacy.ValueKind == JsonValueKind.Array ? legacy.GetArrayLength() : 0;
        var selectedState = new { ready = catalog.IsLaunchReady(selected.RelativePath), fullReady = catalog.IsReady, loadedPacks = catalog.Packs.Count,
            selectedManifestRequests = handler.ManifestRequests(source), bundleRequests = handler.BundleRequests };
        var backgroundStart = clock.Elapsed.TotalMilliseconds;
        var background = catalog.PrepareLibraryAsync(timeout.Token);
        var gateExercised = catalog.Sources.Any(entry => !handler.IsSelected(entry.Source));
        try
        {
            if (gateExercised) await handler.UnrelatedRequested.Task.WaitAsync(timeout.Token);
            var gated = new { gateExercised, selectedReady = catalog.IsLaunchReady(selected.RelativePath), fullReady = catalog.IsReady,
                backgroundCompleted = background.IsCompleted, unrelatedBlocked = handler.UnrelatedBlocked };
            handler.ReleaseUnrelated.TrySetResult();
            await background.WaitAsync(timeout.Token);
            var fullReadyAtMs = clock.Elapsed.TotalMilliseconds;
            var frozenStable = frozen == catalog.SessionManifest(source);
            var passed = listMs <= options.ListGoalMs && first.requests == 0 && first.loadedPacks == 0 && !first.IsReady &&
                selectedState.ready && !selectedState.fullReady && frozenStable && catalog.IsReady &&
                (!gateExercised || (gated.selectedReady && !gated.fullReady && !gated.backgroundCompleted));
            var result = new
            {
                passed, assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(PackCatalogService).Assembly.Location))),
                selected = selected.RelativePath, selectedFiles, selectedLegacyFiles, setupMs, facts,
                listMs, options.ListGoalMs, indexMs, selectedEnsureMs, selectedReadyAtMs,
                fullBackgroundMs = fullReadyAtMs - backgroundStart, fullReadyAtMs,
                totalCpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds,
                first, selectedState, gated,
                final = new { catalog.IsIndexReady, catalog.IsReady, selectedReady = catalog.IsLaunchReady(selected.RelativePath), loadedPacks = catalog.Packs.Count, frozenStable },
                requests = handler.RequestCount, selectedManifestRequests = handler.ManifestRequests(source), bundleRequests = handler.BundleRequests,
                warningCount = Volatile.Read(ref warnings), labels,
                conditions = "One fresh process per invocation; fixture copy excluded from timings; warm OS file cache; isolated empty-file pack tree; HTTP from copied snapshots, no internet latency; no Steam, game launch, installed JAR contents, worlds or personal settings."
            };
            File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(result, Json));
            Console.WriteLine(JsonSerializer.Serialize(new { passed, listMs, indexMs, selectedEnsureMs, selectedReadyAtMs, fullReadyAtMs, gateExercised }));
            return passed ? 0 : 1;
        }
        finally { timeout.Cancel(); handler.ReleaseUnrelated.TrySetResult(); }
    }

    private static FixtureFacts CopyFixture(string fixture, string run)
    {
        var cache = Path.Combine(fixture, "Minecraft", "Launcher", "Library");
        var targetCache = Path.Combine(run, "Minecraft", "Launcher", "Library");
        RejectExistingParents(cache, fixture);
        RejectExistingParents(targetCache, run);
        if (!File.Exists(Path.Combine(cache, "catalog.json"))) throw new InvalidDataException("Fixture needs Minecraft/Launcher/Library/catalog.json.");
        Directory.CreateDirectory(targetCache);
        long cacheBytes = 0; var cacheFiles = 0;
        foreach (var file in Directory.EnumerateFiles(cache, "*.json"))
        {
            RejectReparse(file);
            var name = Path.GetFileName(file);
            if (name != "catalog.json" && (name.Length != 69 || !name.EndsWith(".json", StringComparison.Ordinal) || name[..64].Any(c => !Uri.IsHexDigit(c))))
                throw new InvalidDataException("Only the index and SHA256-named cache JSON files belong in this fixture.");
            var length = new FileInfo(file).Length;
            if (length > (name == "catalog.json" ? 2 : 24) * 1024L * 1024 || (cacheBytes += length) > 256L * 1024 * 1024 || ++cacheFiles > 1000)
                throw new InvalidDataException("Fixture cache exceeds the probe limits.");
            File.Copy(file, Path.Combine(targetCache, name));
        }
        var packs = Path.Combine(fixture, "Minecraft", "Packs");
        var outputPaths = new AppPaths(run);
        RejectExistingParents(packs, fixture);
        var emptyFiles = 0; var directories = 0;
        if (Directory.Exists(packs))
        {
            var pending = new Stack<string>(); pending.Push(packs);
            while (pending.TryPop(out var directory))
            {
                RejectReparse(directory);
                if (++directories > 200000) throw new InvalidDataException("Fixture has too many directories.");
                var relative = Path.GetRelativePath(packs, directory).Replace(Path.DirectorySeparatorChar, '/');
                var target = ReplicaTarget(outputPaths, relative == "." ? "" : relative);
                Directory.CreateDirectory(target);
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    RejectReparse(file);
                    if (new FileInfo(file).Length != 0) throw new InvalidDataException("Pack replica files must be empty; game contents are never copied.");
                    if (++emptyFiles > 200000) throw new InvalidDataException("Fixture has too many empty files.");
                    var fileRelative = Path.GetRelativePath(packs, file).Replace(Path.DirectorySeparatorChar, '/');
                    using var created = new FileStream(ReplicaTarget(outputPaths, fileRelative), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }
                foreach (var child in Directory.EnumerateDirectories(directory)) pending.Push(child);
            }
        }
        return new FixtureFacts(cacheFiles, cacheBytes, emptyFiles, directories);
    }

    private static string UnderArtifacts(string value, string repository, string artifacts)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(repository, value)).TrimEnd(Path.DirectorySeparatorChar);
        if (!full.StartsWith(artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Fixture and output roots must be below this repository's artifacts directory.");
        for (var current = full; current.Length >= artifacts.Length; current = Path.GetDirectoryName(current)!)
            if (File.Exists(current) || Directory.Exists(current)) RejectReparse(current);
        return full;
    }
    private static void RejectReparse(string path)
    { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse points are not permitted in probe inputs or outputs."); }
    private static string ReplicaTarget(AppPaths outputPaths, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Any(char.IsControl) ||
            (relative.Length > 0 && relative.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.EndsWith('.') || segment.EndsWith(' '))))
            throw new InvalidDataException("Replica paths must be canonical relative paths.");
        // Reuse the production canonical containment guard before every create.
        var target = outputPaths.CombineUnderPacks(relative.Replace('/', Path.DirectorySeparatorChar));
        RejectExistingParents(target, outputPaths.Root);
        return target;
    }
    private static void RejectExistingParents(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!full.Equals(boundary, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A fixture path escapes its owner.");
        for (var current = full; current.Length >= boundary.Length; current = Path.GetDirectoryName(current)!)
            if (File.Exists(current) || Directory.Exists(current)) RejectReparse(current);
    }
    private static string FindRepository()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Program", "Minecraft.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Run the probe from its repository checkout.");
    }
    private static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
            if (i + 1 >= args.Length || args[i] is not ("--fixture-root" or "--pack" or "--output-root" or "--list-goal-ms" or "--timeout-seconds") || !values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Use --help for supported, non-repeated options.");
        if (!values.TryGetValue("--fixture-root", out var fixture)) throw new ArgumentException("--fixture-root is required.");
        var pack = values.GetValueOrDefault("--pack", PortablePackSyncService.DefaultPackRelativePath);
        if (!PackCatalogService.ValidFolder(pack)) throw new ArgumentException("--pack must be one pack relativePath.");
        var goal = double.Parse(values.GetValueOrDefault("--list-goal-ms", "250"), CultureInfo.InvariantCulture);
        var timeout = int.Parse(values.GetValueOrDefault("--timeout-seconds", "180"), CultureInfo.InvariantCulture);
        if (!double.IsFinite(goal) || goal <= 0 || goal > 60000 || timeout is < 1 or > 600) throw new ArgumentOutOfRangeException(nameof(args));
        return new Options(fixture, pack, values.GetValueOrDefault("--output-root"), goal, timeout);
    }
    private sealed record Options(string Fixture, string Pack, string? Output, double ListGoalMs, int TimeoutSeconds);
    private sealed record FixtureFacts(int CacheFiles, long CacheBytes, int EmptyPackFiles, int PackDirectories);

    private sealed class SnapshotHandler(string cache) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> manifests = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> selected = new(StringComparer.OrdinalIgnoreCase);
        private int requests, bundles, blocked;
        public int RequestCount => Volatile.Read(ref requests);
        public int BundleRequests => Volatile.Read(ref bundles);
        public int UnrelatedBlocked => Volatile.Read(ref blocked);
        public TaskCompletionSource UnrelatedRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseUnrelated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void AllowSelected(PackSyncSource source) => selected.TryAdd(Key(source), 0);
        public bool IsSelected(PackSyncSource source) => selected.ContainsKey(Key(source));
        public int ManifestRequests(PackSyncSource source) => manifests.GetValueOrDefault(Key(source));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref requests);
            var uri = request.RequestUri ?? throw new InvalidDataException("Missing fixture URI.");
            if (uri.Host == PackCatalogService.IndexUri.Host && uri.AbsolutePath == PackCatalogService.IndexUri.AbsolutePath)
                return Ok(await File.ReadAllBytesAsync(Path.Combine(cache, "catalog.json"), token));
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (uri.Host != "github.com" || parts.Length != 6 || parts[2] != "releases" || parts[3] != "download") throw new InvalidOperationException("Unexpected request outside the snapshot HTTP fixture.");
            var source = new PackSyncSource(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1]), Uri.UnescapeDataString(parts[4]));
            var asset = Uri.UnescapeDataString(parts[5]);
            if (asset == "pack-manifest.json")
            {
                manifests.AddOrUpdate(Key(source), 1, (_, count) => count + 1);
                if (!IsSelected(source))
                {
                    Interlocked.Increment(ref blocked); UnrelatedRequested.TrySetResult();
                    try { await ReleaseUnrelated.Task.WaitAsync(token); }
                    finally { Interlocked.Decrement(ref blocked); }
                }
            }
            else Interlocked.Increment(ref bundles);
            var filename = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Key(source)))) + ".json";
            var file = Path.Combine(cache, filename);
            if (!File.Exists(file)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file, token));
            var raw = document.RootElement.GetProperty("manifest").GetString()!;
            if (asset == "pack-manifest.json") return Ok(Encoding.UTF8.GetBytes(raw));
            using var manifest = JsonDocument.Parse(raw);
            if (manifest.RootElement.TryGetProperty("library", out var library) && asset == library.GetProperty("name").GetString()) return Ok(document.RootElement.GetProperty("bundle").GetBytesFromBase64());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        private static string Key(PackSyncSource source) => source.Owner + "/" + source.Repo + "/" + source.Tag;
        private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }
}
