using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minecraft;

// This runner copies only paths established by old launcher ownership records.
// Its worlds/playerdata/screenshots are generated sentinels, never user saves.
if (args.Length > 0 && args[0] == "history-proof")
{
    await ProveHistoricalHelpers(args.Skip(1).ToArray());
    return;
}
if (args.Length < 2) throw new ArgumentException(
    "Usage: PackMigrationQa <workspace> <dist-directory> [new-artifacts-output] [--legacy]");
var workspace = Path.GetFullPath(args[0]);
var dist = Path.GetFullPath(args[1]);
var legacy = args.Contains("--legacy", StringComparer.Ordinal);
var output = Path.GetFullPath(args.Skip(2).FirstOrDefault(arg => arg != "--legacy") ??
    Path.Combine(workspace, "artifacts", "atm9-original", "qa-migration",
        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + (legacy ? "-legacy" : "-state")));
var artifacts = Path.Combine(workspace, "artifacts") + Path.DirectorySeparatorChar;
if (!output.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) || Directory.Exists(output))
    throw new ArgumentException("Use a new independent output directory inside workspace artifacts.");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
const string pack = "All The Mods 9";
var originals = new AppPaths(workspace);
var paths = new AppPaths(Path.Combine(output, "installation"));
paths.Ensure();
var canonical = paths.CombineUnderPacks(pack);
var game = paths.CombineUnderInstances(pack);
Directory.CreateDirectory(canonical);
Directory.CreateDirectory(game);
var reportPath = Path.Combine(output, "report.json");
var report = new Dictionary<string, object?> { ["status"] = "RUNNING", ["legacyWithoutState"] = legacy,
    ["personalWorldsCopied"] = false, ["originalInstallationModified"] = false };
void WriteReport() => File.WriteAllText(reportPath, JsonSerializer.Serialize(report, json));
WriteReport();
try
{
    var snapshots = new List<Snapshot>();
    var originalPack = originals.CombineUnderPacks(pack);
    var originalGame = originals.CombineUnderInstances(pack);
    var oldSyncState = ReadJson(Path.Combine(originalPack, PortablePackSyncService.SyncStateFileName));
    report["oldRevision"] = oldSyncState.GetProperty("revision").GetString();
    foreach (var item in oldSyncState.GetProperty("files").EnumerateObject())
        CopyManaged(originalPack, canonical, item.Name, snapshots, "pack");
    var oldInstanceStatePath = Path.Combine(originalGame, ".portable-instance.json");
    var oldInstanceState = ReadJson(oldInstanceStatePath);
    foreach (var item in oldInstanceState.GetProperty("files").EnumerateObject())
        CopyManaged(originalGame, game, item.Name, snapshots, "instance");
    if (oldInstanceState.TryGetProperty("modFiles", out var modFiles))
        foreach (var item in modFiles.EnumerateObject())
            CopyManaged(originalGame, game, "mods/" + item.Name, snapshots, "instance");
    if (!legacy)
    {
        File.WriteAllText(Path.Combine(canonical, PortablePackSyncService.SyncStateFileName), oldSyncState.GetRawText());
        File.WriteAllText(Path.Combine(game, ".portable-instance.json"), oldInstanceState.GetRawText());
    }
    File.WriteAllText(Path.Combine(canonical, PortablePackSyncService.SourceMarkerFileName),
        JsonSerializer.Serialize(new { schemaVersion = 1, owner = "MarkZamore", repo = "All-The-Mods-9", tag = "pack-latest" }));
    File.WriteAllText(Path.Combine(output, "source-snapshot.json"), JsonSerializer.Serialize(snapshots, json));
    report["copiedManagedFiles"] = snapshots.Count;
    report["copiedManagedBytes"] = snapshots.Sum(file => file.SizeBytes);
    WriteReport();

    var sentinels = new Dictionary<string, string>
    {
        ["saves/PackMigrationQa/level.dat"] = "QA fixture only: not a Minecraft world or a personal backup",
        ["saves/PackMigrationQa/playerdata/qa.dat"] = "QA player sentinel",
        ["screenshots/qa.png"] = "QA screenshot sentinel",
        ["options.txt"] = "fov:0.25\nkey_key.forward:key.keyboard.r\n",
        ["config/qa-user-preference.ini"] = "user-setting=true"
    };
    foreach (var (relative, content) in sentinels) Put(game, relative, Encoding.UTF8.GetBytes(content));
    const string unknownMod = "mods/qa-unknown-user.jar";
    const string unknownScript = "kubejs/server_scripts/qa-unknown-canonical.js";
    Put(canonical, unknownMod, Encoding.UTF8.GetBytes("unknown canonical mod bytes"));
    Put(canonical, unknownScript, Encoding.UTF8.GetBytes("unknown canonical script bytes"));
    Put(game, unknownMod, Encoding.UTF8.GetBytes("unknown instance mod bytes"));
    var sourceSentinelHashes = new[] { unknownMod, unknownScript }.ToDictionary(
        relative => relative, relative => Hash(Safe(canonical, relative)));

    var manifest = ReadJson(Path.Combine(dist, PortablePackSyncService.PackManifestAssetName));
    File.WriteAllText(Path.Combine(output, "target-manifest.json"), manifest.GetRawText());
    var expected = manifest.GetProperty("files").EnumerateArray().Select(item => new ExpectedFile(
        item.GetProperty("path").GetString()!, item.GetProperty("sizeBytes").GetInt64(),
        item.GetProperty("sha256").GetString()!)).ToArray();
    report["newRevision"] = manifest.GetProperty("revision").GetString();
    report["expectedFiles"] = expected.Length;
    report["targetFilesSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
        expected.OrderBy(file => file.Path, StringComparer.Ordinal).Select(file => $"{file.Path}|{file.SizeBytes}|{file.Sha256}")))))
        .ToLowerInvariant();
    var reusable = expected.Where(file => Matches(canonical, file)).ToArray();
    var unchangedTimes = reusable.ToDictionary(file => file.Path,
        file => File.GetLastWriteTimeUtc(Safe(canonical, file.Path)));
    report["reusableFiles"] = reusable.Length;
    report["reusableBytes"] = reusable.Sum(file => file.SizeBytes);
    var logger = new Logger(paths.LogFile);
    using var handler = new LocalReleaseHandler(dist);
    using var http = new HttpClient(handler);
    var service = new PortablePackSyncService(paths, logger, http);
    var changed = expected.Where(file => !Matches(canonical, file)).ToArray();
    Check(changed.Length > 0, "The snapshot has no changed files; download/apply interruption cannot be exercised.");

    // Fail after bytes have arrived on all attempts; the original tree must
    // remain intact and have no pending-apply marker.
    handler.InterruptAsset = manifest.GetProperty("assets").EnumerateArray().First(asset =>
        asset.GetProperty("covers").EnumerateArray().Any(path => path.GetString() == changed[0].Path))
        .GetProperty("name").GetString();
    var before = expected.Where(file => File.Exists(Safe(canonical, file.Path))).ToDictionary(
        file => file.Path, file => Hash(Safe(canonical, file.Path)));
    var interrupted = await service.SyncAsync(pack, null, CancellationToken.None);
    Check(interrupted.Outcome == PackSyncOutcome.OfflineFallback, "An interrupted download did not preserve the old pack.");
    Check(!PortablePackSyncService.IsUpdatePending(canonical), "A download failure incorrectly marked an apply.");
    foreach (var (relative, sha) in before) Check(Hash(Safe(canonical, relative)) == sha, "Download failure changed " + relative);
    report["interruptedDownload"] = "PASS";
    WriteReport();
    handler.InterruptAsset = null;

    // Interrupt a real apply on the independent copy, then reject an offline
    // launch and let the next online sync converge on the same manifest.
    var block = changed.FirstOrDefault(file => file.Path.StartsWith("config/", StringComparison.OrdinalIgnoreCase)) ?? changed[0];
    var blockedPath = Safe(canonical, block.Path);
    if (File.Exists(blockedPath)) File.Delete(blockedPath);
    Directory.CreateDirectory(blockedPath);
    File.WriteAllText(Path.Combine(blockedPath, "qa-blocker"), "fixture");
    var applyFailed = false;
    try { await service.SyncAsync(pack, null, CancellationToken.None); }
    catch (PackApplyFailedException) { applyFailed = true; }
    Check(applyFailed && PortablePackSyncService.IsUpdatePending(canonical), "The interrupted apply was not persisted.");
    handler.Offline = true;
    var refusedOffline = false;
    try { await service.SyncAsync(pack, null, CancellationToken.None); }
    catch (PackApplyFailedException) { refusedOffline = true; }
    Check(refusedOffline, "A partly updated canonical pack was allowed to use an offline fallback.");
    using (var instances = new PackInstanceService(paths, logger))
    {
        var refusedInstance = false;
        try { await instances.PrepareAsync(pack); }
        catch (InvalidOperationException) { refusedInstance = true; }
        Check(refusedInstance, "The instance accepted a pending pack apply.");
    }
    handler.Offline = false;
    Directory.Delete(blockedPath, recursive: true);
    var bytesBeforeRetry = handler.BytesRead;
    var updated = await service.SyncAsync(pack, null, CancellationToken.None);
    Check(updated.Outcome is PackSyncOutcome.Updated or PackSyncOutcome.UpToDate, "Retry did not converge.");
    Check(!PortablePackSyncService.IsUpdatePending(canonical), "A successful retry kept its pending marker.");
    report["retryDownloadedBytes"] = handler.BytesRead - bytesBeforeRetry;
    report["interruptedApplyRecovery"] = "PASS";
    WriteReport();
    foreach (var file in expected) Check(Matches(canonical, file), "Canonical hash/size mismatch: " + file.Path);
    foreach (var (relative, timestamp) in unchangedTimes)
        Check(File.GetLastWriteTimeUtc(Safe(canonical, relative)) == timestamp, "A correct file was rewritten: " + relative);
    foreach (var (relative, sha) in sourceSentinelHashes)
        Check(Hash(Safe(canonical, relative)) == sha, "An unknown canonical user file was discarded.");

    using (var instances = new PackInstanceService(paths, logger)) await instances.PrepareAsync(pack);
    var expectedMods = expected.Where(file => file.Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
    var actualMods = Directory.EnumerateFiles(Path.Combine(game, "mods"), "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(game, path).Replace('\\', '/')).ToArray();
    Check(expectedMods.Keys.Order().SequenceEqual(actualMods.Order()), "Active instance mods differ from the manifest.");
    foreach (var file in expectedMods.Values) Check(Matches(game, file), "Instance mod hash mismatch: " + file.Path);
    Check(!File.Exists(Safe(game, unknownScript)), "An unknown canonical script became active in the instance.");
    Check(Directory.EnumerateFiles(paths.PackConflicts, "qa-unknown-user.jar*", SearchOption.AllDirectories)
        .Any(path => File.ReadAllText(path) == "unknown instance mod bytes"), "Unknown instance mod bytes were not preserved.");
    foreach (var (relative, content) in sentinels)
        Check(File.ReadAllText(Safe(game, relative)) == content, "Personal sentinel changed: " + relative);
    var retired = manifest.TryGetProperty("legacyFiles", out var historical)
        ? historical.EnumerateArray().Select(item => item.GetProperty("path").GetString()!).Distinct().ToArray() : [];
    var removed = retired.Where(relative => !expected.Any(file => file.Path.Equals(relative, StringComparison.OrdinalIgnoreCase)))
        .Where(relative => snapshots.Any(file => file.Path == relative)).ToArray();
    foreach (var relative in removed)
    {
        Check(!File.Exists(Safe(canonical, relative)), "Retired canonical file survived: " + relative);
        Check(!File.Exists(Safe(game, relative)), "Retired instance file survived: " + relative);
    }
    report["verifiedRemovedHistoricalPaths"] = removed;
    report["activeModFiles"] = actualMods.Length;
    report["sentinelsPreserved"] = sentinels.Count;
    report["unknownFilesPreservedOutsideActiveSet"] = true;
    var requestCount = handler.Requests;
    var downloaded = handler.BytesRead;
    var again = await service.SyncAsync(pack, null, CancellationToken.None);
    Check(again.Outcome == PackSyncOutcome.UpToDate && again.BytesDownloaded == 0, "Repeat Play redownloaded pack payload.");
    Check(handler.Requests - requestCount == 1, "Repeat Play requested payload assets.");
    using (var instances = new PackInstanceService(paths, logger)) await instances.PrepareAsync(pack);
    report["idempotence"] = "PASS";
    report["payloadBytesOnFinalSync"] = 0;
    report["totalLocalHttpBytesIncludingManifestsAndRetries"] = handler.BytesRead;
    report["originalSnapshotStillMatches"] = VerifyOriginals(snapshots, originalPack, originalGame);
    report["status"] = "PASS";
    WriteReport();
    Console.WriteLine(JsonSerializer.Serialize(report, json));
}
catch (Exception ex)
{
    report["status"] = "FAIL";
    report["error"] = ex.Message;
    WriteReport();
    throw;
}

static JsonElement ReadJson(string path)
{
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    return document.RootElement.Clone();
}
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static string Hash(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}
static string Safe(string root, string relative)
{
    Check(!Path.IsPathRooted(relative) && !relative.Contains('\\') && !relative.Split('/').Any(part =>
        part is "" or "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0), "Unsafe snapshot path.");
    var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    Check(full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase), "Snapshot path escaped fixture.");
    var current = full;
    while (current.Length >= root.Length)
    {
        if (File.Exists(current) || Directory.Exists(current))
            Check((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "A snapshot path passes through a link.");
        current = Path.GetDirectoryName(current) ?? "";
    }
    return full;
}
static void Put(string root, string relative, byte[] content)
{
    var path = Safe(root, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, content);
}
static void CopyManaged(string source, string destination, string relative, List<Snapshot> snapshots, string kind)
{
    var first = relative.Split('/')[0];
    Check(first is not ("saves" or "screenshots" or "playerdata" or "logs" or "xaero"), "Ownership snapshot tried to copy personal data.");
    var sourcePath = Safe(source, relative);
    if (!File.Exists(sourcePath)) return;
    var sha = Hash(sourcePath);
    var target = Safe(destination, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.Copy(sourcePath, target);
    File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(sourcePath));
    Check(Hash(target) == sha && Hash(sourcePath) == sha, "Source changed while the technical copy was taken.");
    snapshots.Add(new Snapshot(kind, relative, new FileInfo(target).Length, sha));
}
static bool Matches(string root, ExpectedFile file)
{
    var path = Safe(root, file.Path);
    return File.Exists(path) && new FileInfo(path).Length == file.SizeBytes && Hash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
}
static bool VerifyOriginals(IEnumerable<Snapshot> snapshots, string packRoot, string gameRoot)
{
    foreach (var file in snapshots)
        Check(Hash(Safe(file.Kind == "pack" ? packRoot : gameRoot, file.Path)) == file.Sha256,
            "An original managed file changed during the QA run: " + file.Path);
    return true;
}

static async Task ProveHistoricalHelpers(string[] options)
{
    if (options.Length != 4) throw new ArgumentException(
        "history-proof <migration-installation> <dist> <historical-manifest> <historical-payload-root>");
    var installation = Path.GetFullPath(options[0]);
    Check(installation.Contains(Path.DirectorySeparatorChar + "artifacts" + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase), "Historical proof requires a disposable artifacts fixture.");
    var output = Path.GetDirectoryName(installation)!;
    Check(ReadJson(Path.Combine(output, "report.json")).GetProperty("status").GetString() == "PASS",
        "Historical proof requires a previously passed independent migration fixture.");
    var paths = new AppPaths(installation);
    const string packName = "All The Mods 9";
    var canonical = paths.CombineUnderPacks(packName);
    var game = paths.CombineUnderInstances(packName);
    var historical = ReadJson(options[2]);
    var current = ReadJson(Path.Combine(options[1], PortablePackSyncService.PackManifestAssetName));
    var names = new[] { "atm9-modified-alchemy-wire", "lanminecraft-memory-warnings", "lanmc-steam-flow" };
    var helpers = historical.GetProperty("files").EnumerateArray().Where(file =>
        names.Any(name => file.GetProperty("path").GetString()!.StartsWith("mods/" + name + "-", StringComparison.Ordinal)))
        .Select(file => new ExpectedFile(file.GetProperty("path").GetString()!, file.GetProperty("sizeBytes").GetInt64(),
            file.GetProperty("sha256").GetString()!)).ToArray();
    Check(helpers.Length == 3, "Historical manifest did not identify exactly three requested helper JARs.");
    foreach (var helper in helpers)
    {
        Check(Matches(options[3], helper), "Archived helper does not match its published exact SHA and size.");
        Check(!current.GetProperty("files").EnumerateArray().Any(file =>
            file.GetProperty("path").GetString() == helper.Path), "A retired helper is present in the new pack.");
        foreach (var root in new[] { canonical, game })
        {
            var target = Safe(root, helper.Path);
            Check(!File.Exists(target), "Fixture already contains a helper; do not overwrite unknown bytes.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Safe(options[3], helper.Path), target);
        }
    }
    using var handler = new LocalReleaseHandler(Path.GetFullPath(options[1]));
    using var http = new HttpClient(handler);
    var logger = new Logger(paths.LogFile);
    var sync = new PortablePackSyncService(paths, logger, http);
    var changed = await sync.SyncAsync(packName, null, CancellationToken.None);
    using (var instances = new PackInstanceService(paths, logger)) await instances.PrepareAsync(packName);
    foreach (var helper in helpers)
    {
        Check(!File.Exists(Safe(canonical, helper.Path)) && !File.Exists(Safe(game, helper.Path)),
            "A historical custom helper survived in the active canonical/instance composition.");
        Check(Matches(options[3], helper), "The archived source helper was modified by the QA run.");
    }
    Check(changed.Outcome == PackSyncOutcome.Updated && changed.FilesChanged == 3 && changed.BytesDownloaded == 0,
        "Historical cleanup did not remove exactly three managed files without payload downloads.");
    var again = await sync.SyncAsync(packName, null, CancellationToken.None);
    Check(again.Outcome == PackSyncOutcome.UpToDate && again.BytesDownloaded == 0, "Historical cleanup is not idempotent.");
    var result = new
    {
        status = "PASS", personalWorldsCopied = false, historicalRevision = historical.GetProperty("revision").GetString(),
        targetRevision = current.GetProperty("revision").GetString(), helpers,
        canonicalAndInstanceHelpersAbsent = true, sourceArchiveUnchanged = true,
        filesChanged = changed.FilesChanged, payloadBytesDownloaded = changed.BytesDownloaded, idempotence = "PASS"
    };
    var text = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    File.WriteAllText(Path.Combine(output, "history-proof-report.json"), text);
    Console.WriteLine(text);
}
sealed record Snapshot(string Kind, string Path, long SizeBytes, string Sha256);
sealed record ExpectedFile(string Path, long SizeBytes, string Sha256);

sealed class LocalReleaseHandler(string dist) : HttpMessageHandler
{
    public string? InterruptAsset { get; set; }
    public bool Offline { get; set; }
    public long BytesRead;
    public int Requests;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Interlocked.Increment(ref Requests);
        var uri = request.RequestUri!;
        if (uri.Scheme != "https" || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith("/MarkZamore/All-The-Mods-9/releases/download/pack-latest/", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected network request in offline migration QA.");
        if (Offline) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var name = Uri.UnescapeDataString(uri.Segments[^1]);
        var local = name == PortablePackSyncService.PackManifestAssetName
            ? Path.Combine(dist, name) : Path.Combine(dist, "assets", name);
        if (!File.Exists(local)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        var stream = new CountedStream(local, name == InterruptAsset, bytes => Interlocked.Add(ref BytesRead, bytes));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
    }
}
sealed class CountedStream(string path, bool interrupt, Action<int> count) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (interrupt && Position > 0) throw new IOException("QA connection interrupted after its first byte.");
        var read = await base.ReadAsync(interrupt ? buffer[..Math.Min(1, buffer.Length)] : buffer, token);
        count(read);
        return read;
    }
}
