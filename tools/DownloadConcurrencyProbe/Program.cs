using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CmlLib.Core.Files;
using Minecraft;

if (args.Length != 5)
    throw new ArgumentException("Usage: <asset-index> <run-directory> <file-count> <levels-comma-separated> <rounds>");

var indexPath = Path.GetFullPath(args[0]);
var runRoot = Path.GetFullPath(args[1]);
var count = int.Parse(args[2]);
var levels = args[3].Split(',').Select(int.Parse).Distinct().ToArray();
var rounds = int.Parse(args[4]);
if (count <= 0 || rounds <= 0 || levels.Length == 0 || levels.Any(level => level <= 0 || level > 256))
    throw new ArgumentException("Positive file count, rounds and concurrency levels up to 256 are required.");
if (!File.Exists(Path.Combine(runRoot, "RUN.json")))
    throw new InvalidOperationException("The run directory requires an ownership RUN.json.");
var downloadRoot = Path.Combine(runRoot, "downloads");
for (string? ancestor = downloadRoot; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
    if (Directory.Exists(ancestor) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
        throw new IOException("The benchmark directory must have ordinary directory ancestors.");
if (Directory.Exists(downloadRoot) && Directory.EnumerateFileSystemEntries(downloadRoot).Any())
    throw new InvalidOperationException("The download directory must be empty before the benchmark.");

using var index = JsonDocument.Parse(File.ReadAllBytes(indexPath));
var candidates = index.RootElement.GetProperty("objects").EnumerateObject()
    .Select(entry => new Asset(entry.Value.GetProperty("hash").GetString()!, entry.Value.GetProperty("size").GetInt64()))
    .DistinctBy(asset => asset.Hash).OrderBy(asset => asset.Hash, StringComparer.Ordinal).ToArray();
// Uniform deterministic sampling preserves the index's mixture of file sizes.
count = Math.Min(count, candidates.Length);
var selected = Enumerable.Range(0, count).Select(i => candidates[(int)((long)i * candidates.Length / count)]).ToArray();
var totalBytes = selected.Sum(asset => asset.Size);
var results = new List<Measurement>();
var reportPath = Path.Combine(runRoot, "results.json");
var setting = typeof(PortableGameInstaller).GetProperty("MaximumParallelDownloads", BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new MissingMemberException("PortableGameInstaller.MaximumParallelDownloads");

Console.WriteLine($"Selected {count} files, {totalBytes / 1048576d:F2} MiB, from {candidates.Length} unique assets.");
using var cancelled = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled.Cancel(); };

for (var round = 0; round < rounds; round++)
{
    // Reverse the order on alternate rounds to expose warming and time drift.
    var order = round % 2 == 0 ? levels : levels.Reverse().ToArray();
    foreach (var level in order)
    {
        var files = selected.Select(asset => new GameFile(asset.Hash)
        {
            Path = Path.Combine(downloadRoot, asset.Hash[..2], asset.Hash),
            Url = $"https://resources.download.minecraft.net/{asset.Hash[..2]}/{asset.Hash}",
            Hash = asset.Hash,
            Size = asset.Size
        }).ToArray();
        using var handler = new CountingHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MinecraftPortable/1.0");
        var installer = new PortableGameInstaller(http, downloadRoot, Path.Combine(downloadRoot, "temp"), null, "Minecraft");
        setting.SetValue(installer, level);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancelled.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var watch = Stopwatch.StartNew();
        string? error = null;
        try
        {
            // Production downloader includes writes, size/SHA-1 verification and retries.
            await installer.Install(files, null, null, deadline.Token);
        }
        catch (Exception exception) when (!cancelled.IsCancellationRequested)
        {
            error = exception.ToString();
        }
        finally
        {
            watch.Stop();
            // Delete only files created by this run, with each absolute path checked.
            if (Directory.Exists(downloadRoot))
            {
                var root = Path.GetFullPath(downloadRoot) + Path.DirectorySeparatorChar;
                var entries = Directory.EnumerateFileSystemEntries(downloadRoot, "*", SearchOption.AllDirectories).ToArray();
                foreach (var entry in entries)
                    if (!Path.GetFullPath(entry).StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                        (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Unexpected linked or escaped benchmark path.");
                foreach (var entry in entries.Where(File.Exists)) File.Delete(entry);
                foreach (var entry in entries.Where(Directory.Exists).OrderByDescending(path => path.Length)) Directory.Delete(entry);
                Directory.Delete(downloadRoot);
            }
        }
        var measurement = new Measurement(round + 1, level, watch.Elapsed.TotalSeconds,
            handler.Requests, handler.FailedResponses, error);
        results.Add(measurement);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            indexSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(indexPath))),
            files = count, totalBytes, assets = selected, results,
            summaries = results.GroupBy(result => result.Parallelism).Select(group => new
            {
                parallelism = group.Key,
                successes = group.Count(result => result.Error is null),
                averageSeconds = group.Where(result => result.Error is null).Select(result => (double?)result.Seconds).Average()
            })
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Round {round + 1}, parallel {level}: {watch.Elapsed.TotalSeconds:F2}s, {handler.Requests} requests, {handler.FailedResponses} HTTP errors, {(error is null ? "PASS" : "FAIL")}");
    }
}
if (results.Any(result => result.Error is not null)) Environment.ExitCode = 1;

record Asset(string Hash, long Size);
record Measurement(int Round, int Parallelism, double Seconds, int Requests, int FailedResponses, string? Error);

sealed class CountingHandler() : DelegatingHandler(new HttpClientHandler())
{
    public int Requests;
    public int FailedResponses;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Requests);
        var response = await base.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) Interlocked.Increment(ref FailedResponses);
        return response;
    }
}
