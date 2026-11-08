using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Minecraft;

internal static class ReleaseMatrix
{
    private static readonly Assembly Launcher = typeof(UpdateService).Assembly;
    private static readonly MethodInfo Get = Launcher.GetType("Minecraft.GitHubReleaseTransport")!
        .GetMethod("GetAsync", BindingFlags.Static | BindingFlags.NonPublic, [typeof(HttpClient), typeof(Uri), typeof(CancellationToken)])!;

    internal static async Task<int> RunAsync(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var output = Path.GetFullPath(args[1]);
        if (!output.StartsWith(Path.Combine(root, "artifacts", "agent-runs") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use the assigned agent-run directory.");
        Directory.CreateDirectory(output);
        using var handler = new ApiOnlyHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LANMinecraft-QA/1.0");
        var results = new List<object>();
        var failed = 0;
        async Task<(byte[] Bytes, string Sha)> Fetch(Uri uri, long? size = null, string? sha = null)
        {
            Exception? last = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
                    using var response = await (Task<HttpResponseMessage>)Get.Invoke(null, [client, uri, deadline.Token])!;
                    response.EnsureSuccessStatusCode();
                    // Probe samples only small payloads; this is not a duplicate installation.
                    if (response.Content.Headers.ContentLength is > 32 * 1024 * 1024) throw new InvalidDataException("Probe sample is too large.");
                    await response.Content.LoadIntoBufferAsync(32 * 1024 * 1024, deadline.Token);
                    var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
                    var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    if (size is { } expected && bytes.LongLength != expected || sha is not null && !actual.Equals(sha, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Probe sample differs from the published pin.");
                    return (bytes, actual);
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { last = ex; }
                await Task.Delay(500);
            }
            throw last!;
        }

        if (args.Length == 5)
        {
            var asset = await Fetch(new Uri(args[2]), long.Parse(args[3]), args[4]);
            var proof = JsonSerializer.Serialize(new { status = "PASS", size = asset.Bytes.Length, sha256 = asset.Sha, requests = handler.Requests }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(output, "single-asset.json"), proof);
            Console.WriteLine(proof);
            return 0;
        }

        var indexUri = new Uri(PackCatalogService.IndexUri + "?probe=" + Guid.NewGuid().ToString("N"));
        var indexBytes = (await Fetch(indexUri)).Bytes;
        File.WriteAllBytes(Path.Combine(output, "catalog.json"), indexBytes);
        var entries = (IEnumerable<CatalogSource>)typeof(PackCatalogService).GetMethod("ParseIndex", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [System.Text.Encoding.UTF8.GetString(indexBytes), true])!;
        foreach (var entry in entries)
        {
            try
            {
                var fetched = await Fetch(PackCatalogService.AssetUri(entry.Source, "pack-manifest.json"));
                using var json = JsonDocument.Parse(fetched.Bytes);
                var manifest = json.RootElement;
                var samples = new List<object>();
                var library = manifest.GetProperty("library");
                var libraryName = library.TryGetProperty("assetName", out var n) ? n.GetString()! : library.GetProperty("name").GetString()!;
                var bundle = await Fetch(PackCatalogService.AssetUri(entry.Source, libraryName), library.GetProperty("sizeBytes").GetInt64(), library.GetProperty("sha256").GetString());
                samples.Add(new { name = libraryName, sha256 = bundle.Sha, size = bundle.Bytes.Length });
                var asset = manifest.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("sizeBytes").GetInt64() < 8 * 1024 * 1024)
                    .OrderBy(a => a.GetProperty("sizeBytes").GetInt64()).First();
                var assetName = asset.GetProperty("name").GetString()!;
                var payload = await Fetch(PackCatalogService.AssetUri(entry.Source, assetName), asset.GetProperty("sizeBytes").GetInt64(), asset.GetProperty("sha256").GetString());
                samples.Add(new { name = assetName, sha256 = payload.Sha, size = payload.Bytes.Length });
                results.Add(new { pack = entry.Id, source = entry.Source, status = "PASS", revision = manifest.GetProperty("revision").GetString(), manifestSha256 = fetched.Sha, samples });
                Console.WriteLine(entry.Id + ": PASS manifest, library and payload sample");
            }
            catch (Exception ex) { failed++; results.Add(new { pack = entry.Id, status = "FAIL", error = ex.Message }); Console.WriteLine(entry.Id + ": FAIL " + ex.Message); }
        }

        var componentJson = System.Text.Encoding.UTF8.GetString((await Fetch(new Uri("https://raw.githubusercontent.com/MarkZamore/e4steam/main/components.json?probe=" + Guid.NewGuid().ToString("N")))).Bytes);
        var snapshot = Launcher.GetType("Minecraft.SteamComponentCatalog")!.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [componentJson])!;
        var builds = (IEnumerable<SteamTransportBuild>)snapshot.GetType().GetProperty("Builds")!.GetValue(snapshot)!;
        foreach (var build in builds)
        {
            try
            {
                var bytes = await Fetch(build.DownloadUris[0], build.SizeBytes, build.Sha256);
                results.Add(new { component = build.FileName, status = "PASS", bytes = bytes.Bytes.Length, sha256 = bytes.Sha });
                Console.WriteLine(build.FileName + ": PASS pinned component");
            }
            catch (Exception ex) { failed++; results.Add(new { component = build.FileName, status = "FAIL", error = ex.Message }); }
        }
        // Exact bridge involved in the reported failure, in addition to the generic samples.
        try
        {
            const string uri = "https://github.com/MarkZamore/Better-Than-Wolves/releases/download/pack-latest/file-04e3441753b061d6-btw-lan-bridge-1.0.0.jar";
            var bridge = await Fetch(new Uri(uri), 25200, "04e3441753b061d6e426ef117fe50ccd8f07892740b04fd1a397b98de72e06e2");
            results.Add(new { sample = "reported bridge", status = "PASS", sha256 = bridge.Sha, bytes = bridge.Bytes.Length });
        }
        catch (Exception ex) { failed++; results.Add(new { sample = "reported bridge", status = "FAIL", error = ex.Message }); }

        File.WriteAllText(Path.Combine(output, "release-matrix.json"), JsonSerializer.Serialize(new
        {
            primary = "Injected HTTP 504 for all github.com release URLs", api = "Real public unauthenticated GitHub API and CDN",
            failed, results, requests = handler.Requests, gameLaunches = "NOT RUN; network delivery verification only",
            personalFilesModified = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }

    private sealed class ApiOnlyHandler : DelegatingHandler
    {
        internal readonly List<object> Requests = [];
        internal ApiOnlyHandler() : base(new HttpClientHandler()) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "github.com" && uri.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal))
            {
                Requests.Add(new { uri = uri.GetLeftPart(UriPartial.Path), status = 504, injected = true });
                return new(HttpStatusCode.GatewayTimeout) { RequestMessage = request };
            }
            var response = await base.SendAsync(request, token);
            Requests.Add(new { uri = uri.GetLeftPart(UriPartial.Path), status = (int)response.StatusCode, injected = false });
            return response;
        }
    }
}
