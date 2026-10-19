using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BsDiff;
using Minecraft;

namespace Minecraft.Tests;

// Opt-in integration scenarios for the Process Monitor audit. These preserve
// their isolated trees as evidence; ordinary test runs never execute them.
[Collection("Functional audit")]
public sealed class FunctionalAuditTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const ulong FirstId = 76561198000000001;
    private const ulong SecondId = 76561198000000002;

    [FunctionalAuditFact]
    public Task CachedRealJdk17_InstallProbeReuseAndRepair() => AuditJavaAsync(17);

    [FunctionalAuditFact]
    public Task CachedRealJdk21_InstallProbeReuseAndRepair() => AuditJavaAsync(21);

    [FunctionalAuditFact]
    public Task CachedRealJdk25_InstallProbeReuseAndRepair() => AuditJavaAsync(25);

    private static async Task AuditJavaAsync(int majorVersion)
    {
        using var scenario = Begin($"java-{majorVersion}-real-cached-archive");
        var paths = Paths($"java-{majorVersion}");
        var source = new AppPaths(RequiredEnvironment("LANMINECRAFT_FUNCTIONAL_AUDIT_SOURCE_ROOT"));
        using var client = new HttpClient(new OfflineHandler());
        var pin = JavaRuntimeCatalog.ForMajorVersion(majorVersion)!;
        var sourceCache = new PortableJavaRuntimeService(source, new Logger(source.LogFile), client, pin).CachePath;
        Assert.True(File.Exists(sourceCache), $"Local cached archive is required: {sourceCache}");
        var service = new PortableJavaRuntimeService(paths, new Logger(paths.LogFile), client, pin);
        Directory.CreateDirectory(Path.GetDirectoryName(service.CachePath)!);
        File.Copy(sourceCache, service.CachePath, overwrite: false);
        Mark("java-install-start", new { pin.RuntimeId, archiveSha256 = Hash(File.ReadAllBytes(sourceCache)) });
        var runtime = await service.EnsureAsync(paths.JavaRuntimes, null, CancellationToken.None);
        Assert.True(File.Exists(runtime.JavaPath));
        Assert.Equal(pin.JavaVersion, runtime.JavaVersion);
        var timestamp = File.GetLastWriteTimeUtc(runtime.JavaPath);
        var reused = await service.EnsureAsync(paths.JavaRuntimes, null, CancellationToken.None);
        Assert.Equal(runtime, reused);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(reused.JavaPath));
        File.Delete(runtime.JavaPath);
        var repaired = await service.EnsureAsync(paths.JavaRuntimes, null, CancellationToken.None);
        Assert.True(File.Exists(repaired.JavaPath));
        Mark("java-install-reuse-repair-pass", new { pin.RuntimeId, repaired.JavaPath });
    }

    [FunctionalAuditFact]
    public async Task SkinProtocol_TransfersRefusesAndSurvivesInterruptedRequest()
    {
        using var scenario = Begin("skin-peer-protocol");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var paths = Paths("skins");
        var network = new InMemoryPeerNetwork();
        network.MakeFriends(FirstId, SecondId);
        var servingTransport = network.CreateTransport(FirstId, "Serving test peer");
        var requestingTransport = network.CreateTransport(SecondId, "Requesting test peer");
        await using var service = new SkinService(paths, new Logger(paths.LogFile), servingTransport);
        await using var router = new PeerConnectionRouter(servingTransport);
        router.Register(service);
        await router.StartAsync(timeout.Token);
        var uuid = Guid.NewGuid().ToString("D");
        // The old 64x32 layout is accepted by the production service as well as
        // old Minecraft clients. Only the header is needed for this protocol test.
        var skin = new byte[24];
        new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }.CopyTo(skin, 0);
        "IHDR"u8.CopyTo(skin.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(skin.AsSpan(16), 64);
        BinaryPrimitives.WriteInt32BigEndian(skin.AsSpan(20), 32);
        var skinPath = Path.Combine(paths.Root, "skin.png");
        File.WriteAllBytes(skinPath, skin);
        var announcement = service.SelectLocalSkin(new AppSettings(), uuid, skinPath);
        Assert.True(SteamId64.TryFrom(FirstId, out var servingPeer));

        async Task<SkinResponse> RequestAsync(string hash, bool readPayload)
        {
            await using var connection = await requestingTransport.ConnectAsync(servingPeer, SkinService.ProtocolName, timeout.Token);
            await PortableProtocol.WriteJsonAsync(connection.Stream,
                new SkinRequest { PlayerUuid = uuid, Sha256 = hash }, JsonOptions, timeout.Token);
            var response = PortableProtocol.Deserialize<SkinResponse>(
                await PortableProtocol.ReadFrameAsync(connection.Stream, timeout.Token), JsonOptions)!;
            if (readPayload)
            {
                var received = new byte[checked((int)response.SizeBytes)];
                await connection.Stream.ReadExactlyAsync(received, timeout.Token);
                Assert.Equal(skin, received);
                Assert.Equal(announcement.Sha256, Hash(received), ignoreCase: true);
            }
            return response;
        }

        Assert.True((await RequestAsync(announcement.Sha256, readPayload: true)).Ok);
        Assert.False((await RequestAsync(new string('f', 64), readPayload: false)).Ok);
        await using (var interrupted = await requestingTransport.ConnectAsync(servingPeer, SkinService.ProtocolName, timeout.Token))
        {
            await interrupted.Stream.WriteAsync(new byte[] { 0, 0 }, timeout.Token);
        }
        Assert.True((await RequestAsync(announcement.Sha256, readPayload: true)).Ok);
        Mark("skin-transfer-refusal-interruption-pass");
    }

    [FunctionalAuditFact]
    public async Task PackSyncCancellation_FirstInstallAndRepairCleanTheirStaging()
    {
        using var scenario = Begin("pack-install-repair-cancellation");
        var files = new[]
        {
            (path: "mods/audit.jar", bytes: "isolated audit payload"u8.ToArray()),
            (path: "portable-pack.json", bytes: "{\"schemaVersion\":1}"u8.ToArray())
        };
        var assets = files.Select(file => new { file.path, file.bytes, name = $"m-{Hash(file.bytes)[..12]}-{Path.GetFileName(file.path)}" }).ToArray();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, packId = PortablePackSyncService.DefaultPackRelativePath, revision = "audit-1",
            files = files.Select(file => new { file.path, sizeBytes = file.bytes.Length, sha256 = Hash(file.bytes) }),
            assets = assets.Select(asset => new { asset.name, kind = "jar", sizeBytes = asset.bytes.Length, sha256 = Hash(asset.bytes), covers = new[] { asset.path } })
        });
        foreach (var repair in new[] { false, true })
        {
            var paths = Paths(repair ? "pack-cancel-repair" : "pack-cancel-install");
            using var cancellation = new CancellationTokenSource();
            var cancelDownload = false;
            using var client = new HttpClient(new ResponseHandler(request =>
            {
                var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
                var bytes = name == PortablePackSyncService.PackManifestAssetName
                    ? manifest : assets.Single(asset => asset.name == name).bytes;
                if (cancelDownload && name != PortablePackSyncService.PackManifestAssetName) cancellation.Cancel();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }));
            var service = new PortablePackSyncService(paths, new Logger(paths.LogFile), client);
            var pack = paths.CombineUnderPacks(PortablePackSyncService.DefaultPackRelativePath);
            if (repair)
            {
                await service.SyncAsync(PortablePackSyncService.DefaultPackRelativePath, null, CancellationToken.None);
                File.WriteAllText(Path.Combine(pack, "mods", "audit.jar"), "damaged");
            }
            cancelDownload = true;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.SyncAsync(PortablePackSyncService.DefaultPackRelativePath, null, cancellation.Token));
            Assert.Equal(repair, File.Exists(Path.Combine(pack, "portable-pack.json")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(pack, ".pack-sync-stage*"));
            cancelDownload = false;
            var converged = await service.SyncAsync(PortablePackSyncService.DefaultPackRelativePath, null, CancellationToken.None);
            Assert.Equal(repair ? PackSyncOutcome.Updated : PackSyncOutcome.Installed, converged.Outcome);
            Assert.Equal(files[0].bytes, File.ReadAllBytes(Path.Combine(pack, "mods", "audit.jar")));
            Mark("pack-cancel-retry-pass", new { repair });
        }
    }

    [FunctionalAuditFact]
    public async Task ReportProtocol_RefusalDisconnectAndCancellationRemoveOutgoingArchive()
    {
        using var scenario = Begin("report-protocol-failures");
        foreach (var mode in new[] { "refusal", "disconnect", "cancel" })
        {
            var paths = Paths("report-" + mode);
            var network = new InMemoryPeerNetwork();
            network.MakeFriends(FirstId, SecondId);
            var senderTransport = network.CreateTransport(FirstId);
            var receiverTransport = network.CreateTransport(SecondId);
            using var cancellation = new CancellationTokenSource();
            var handler = new ReportFailureHandler(mode, cancellation);
            await using var router = new PeerConnectionRouter(receiverTransport);
            router.Register(handler);
            await router.StartAsync(CancellationToken.None);
            Assert.True(SteamId64.TryFrom(FirstId, out var senderId));
            Assert.True(SteamId64.TryFrom(SecondId, out var receiverId));
            var sender = new BugReportService(paths, new Logger(paths.LogFile), senderTransport,
                () => null,
                () => new BugReportContext(senderId, "Audit", "Audit", Guid.NewGuid().ToString("D"), "audit", "audit", "", false),
                _ => Task.FromResult(new SupportEnvironmentSnapshot(DateTimeOffset.UtcNow,
                    "audit", "1", ".NET 10", "Windows", "X64", "not running", "audit", "", [], [],
                    SteamDiagnosticContext.Unavailable, new Dictionary<string, string>(), "")),
                steamLogsDirectoryProvider: () => null);
            var error = await Record.ExceptionAsync(() => sender.SendAsync(receiverId, "synthetic audit report", null, cancellation.Token));
            Assert.NotNull(error);
            if (mode == "refusal") Assert.IsType<InvalidOperationException>(error);
            if (mode == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
            var staging = Path.Combine(paths.Personal, "Temp", "BugReports");
            Assert.True(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any());
            await handler.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Mark("report-failure-cleanup-pass", new { mode, exception = error.GetType().Name });
        }
    }

    [FunctionalAuditFact]
    public async Task Updater_FullDeltaFallbackCancellationInstallRestartAndRollback()
    {
        using var scenario = Begin("updater-download-install");
        var host = RequiredEnvironment("LANMINECRAFT_FUNCTIONAL_AUDIT_HOST");
        Assert.True(File.Exists(host), "Publish the audit host before running the integration audit.");
        var newBytes = File.ReadAllBytes(host);
        var oldBytes = newBytes.ToArray();
        // IMAGE_DOS_HEADER.e_res is reserved and ignored by the Windows loader.
        // Change one reserved byte to give the real patch a distinct base image.
        oldBytes[0x1c] ^= 1;
        using var patchStream = new MemoryStream();
        BinaryPatch.Create(oldBytes, newBytes, patchStream);
        var patchBytes = patchStream.ToArray();

        foreach (var kind in new[] { "full", "delta", "delta-fallback", "cancel" })
        {
            var paths = Paths("update-download-" + kind);
            var current = Path.Combine(paths.Root, UpdateService.ExecutableAssetName);
            File.WriteAllBytes(current, oldBytes);
            var manifest = Manifest(newBytes);
            var delta = new DeltaPatchManifest
            {
                AlgorithmVersion = UpdateService.DeltaPatchAlgorithmVersion,
                BaseReleaseNumber = 1, BaseCommitSha = new string('a', 40), BaseSha256 = Hash(oldBytes),
                Sha256 = Hash(patchBytes), SizeBytes = patchBytes.Length
            };
            manifest.DeltaPatches = [delta];
            var actualPatch = kind == "delta-fallback" ? new byte[patchBytes.Length] : patchBytes;
            using var cancellation = new CancellationTokenSource();
            var requests = new List<string>();
            using var client = new HttpClient(new ResponseHandler(request =>
            {
                var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
                requests.Add(name);
                if (kind == "cancel") cancellation.Cancel();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(name.EndsWith(".bsdiff", StringComparison.Ordinal) ? actualPatch : newBytes)
                };
            }));
            var service = new UpdateService(paths, new Logger(paths.LogFile), client,
                currentCommitSha: new string('a', 40), currentExecutablePath: current);
            var update = UpdateCheckResult.Available(manifest,
                new Uri("https://github.com/MarkZamore/LANMinecraft/releases/download/latest/LANMinecraft.exe"),
                kind.StartsWith("delta", StringComparison.Ordinal) ? delta : null,
                kind.StartsWith("delta", StringComparison.Ordinal)
                    ? new Uri("https://github.com/MarkZamore/LANMinecraft/releases/download/latest/LANMinecraft.bsdiff") : null);
            if (kind == "cancel")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => service.DownloadUpdateAsync(update, null, cancellation.Token));
                Assert.Empty(Directory.EnumerateFiles(Path.Combine(paths.Personal, "Updates"), "*.download"));
            }
            else
            {
                var prepared = await service.DownloadUpdateAsync(update, null, cancellation.Token);
                Assert.Equal(newBytes, File.ReadAllBytes(prepared.ExecutablePath));
                Assert.NotNull(service.TryGetPreparedUpdate());
                Assert.Equal(kind == "delta" ? 1 : kind == "delta-fallback" ? 2 : 1, requests.Count);
            }
            Mark("update-download-pass", new { kind, requests });
        }

        var installationErrors = new List<Exception>();
        foreach (var kind in new[] { "install", "restart", "replacement-failure" })
        {
            try
            {
                var paths = Paths("update-" + kind);
                var current = Path.Combine(paths.Root, UpdateService.ExecutableAssetName);
                File.WriteAllBytes(current, oldBytes);
                var updates = Path.Combine(paths.Personal, "Updates");
                var ready = Path.Combine(updates, "Ready");
                Directory.CreateDirectory(ready);
                var prepared = Path.Combine(ready, UpdateService.ExecutableAssetName);
                File.WriteAllBytes(prepared, newBytes);
                var manifestFile = Path.Combine(ready, UpdateService.ManifestAssetName);
                File.WriteAllText(manifestFile, JsonSerializer.Serialize(Manifest(newBytes), JsonOptions));
                using var heldTarget = kind == "replacement-failure"
                    ? File.Open(current, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
                var mode = kind == "restart" ? UpdateInstallMode.InstallAndRestart : UpdateInstallMode.InstallOnExit;
                var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = paths.Root };
                foreach (var argument in new[] { "install", paths.Root, current, manifestFile, prepared, mode.ToString() })
                    start.ArgumentList.Add(argument);
                using var child = Process.Start(start)!;
                Mark("updater-caller-start", new { kind, processId = child.Id, executable = host, target = current });

                await child.WaitForExitAsync();
                Assert.Equal(0, child.ExitCode);
                Mark("updater-caller-exit", new { kind, processId = child.Id, child.ExitCode });
                if (kind == "replacement-failure")
                {
                    var journal = Path.Combine(updates, UpdateService.InstallJournalFileName);
                    await WaitUntilAsync(() => ReadJournalStatus(journal) == "Failed", TimeSpan.FromSeconds(40));
                    Assert.Equal(oldBytes, File.ReadAllBytes(current));
                    Assert.DoesNotContain("Get-FileHash", File.ReadAllText(journal), StringComparison.Ordinal);
                }
                else
                {
                    await WaitUntilAsync(() =>
                    {
                        var journalPath = Path.Combine(updates, UpdateService.InstallJournalFileName);
                        if (ReadJournalStatus(journalPath) == "Failed")
                            throw new InvalidOperationException("Production updater failed: " + File.ReadAllText(journalPath));
                        return !File.Exists(journalPath) && File.ReadAllBytes(current).SequenceEqual(newBytes);
                    }, TimeSpan.FromSeconds(40));
                    if (kind == "restart")
                    {
                        var restartedPath = Path.Combine(paths.Root, "audit-restarted.txt");
                        await WaitUntilAsync(() => HasRestartMarker(restartedPath), TimeSpan.FromSeconds(20));
                        using var restarted = JsonDocument.Parse(File.ReadAllText(restartedPath));
                        Assert.Empty(restarted.RootElement.GetProperty("args").EnumerateArray());
                    }
                }
                Mark("updater-install-pass", new { kind, targetSha256 = Hash(File.ReadAllBytes(current)) });
            }
            catch (Exception error)
            {
                Mark("updater-install-fail", new { kind, error = error.ToString() });
                installationErrors.Add(error);
            }
        }

        // Exercise the production helper's rollback branch with one explicit
        // failure after replacement. This is a fault-injected COPY of the script,
        // not a claim that an unmodified normal install encountered this fault.
        try
        {
            var rollbackPaths = Paths("update-rollback-injected");
            var rollbackTarget = Path.Combine(rollbackPaths.Root, UpdateService.ExecutableAssetName);
            var rollbackPrepared = Path.Combine(rollbackPaths.Root, "prepared.exe");
            File.WriteAllBytes(rollbackTarget, oldBytes);
            File.WriteAllBytes(rollbackPrepared, newBytes);
            var script = (string)typeof(UpdateService).GetMethod("BuildUpdaterScript",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null)!;
            const string verification = "  Assert-File $ExePath $ExpectedSize $ExpectedSha256";
            Assert.Equal(1, script.Split(verification).Length - 1);
            script = script.Replace(verification, "  throw 'AUDIT_INJECTED_POST_REPLACEMENT_FAILURE'", StringComparison.Ordinal);
            var scriptPath = Path.Combine(rollbackPaths.Root, "apply-update-fault-injected.ps1");
            File.WriteAllText(scriptPath, script);
            var journalPath = Path.Combine(rollbackPaths.Root, "install-journal.json");
            var rollbackStart = new ProcessStartInfo("powershell.exe")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = rollbackPaths.Root };
            const string fallbackCommit = "audit target \"quoted\" tail\\";
            foreach (var argument in new[]
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                "-PidToWait", "2147483647", "-ExePath", rollbackTarget, "-PreparedExePath", rollbackPrepared,
                "-PreparedManifestPath", Path.Combine(rollbackPaths.Root, "unused.json"),
                "-CandidatePath", Path.Combine(rollbackPaths.Root, "candidate.exe"),
                "-BackupPath", Path.Combine(rollbackPaths.Root, "backup.exe"),
                "-JournalPath", journalPath, "-RestartRequestPath", Path.Combine(rollbackPaths.Root, "restart-requested"),
                "-LogPath", Path.Combine(rollbackPaths.Root, "update.log"),
                "-ExpectedSize", newBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-ExpectedSha256", Hash(newBytes), "-CurrentSha256", Hash(oldBytes),
                "-TargetCommitSha", fallbackCommit, "-RestartAfterInstall", "1",
                "-StartedAtUtc", DateTimeOffset.UtcNow.ToString("O")
            }) rollbackStart.ArgumentList.Add(argument);
            using var rollbackProcess = Process.Start(rollbackStart)!;
            Mark("updater-rollback-injected-start", new { processId = rollbackProcess.Id, scriptPath });
            await rollbackProcess.WaitForExitAsync();
            Assert.Equal(1, rollbackProcess.ExitCode);
            Assert.Equal("Failed", ReadJournalStatus(journalPath));
            Assert.Equal(oldBytes, File.ReadAllBytes(rollbackTarget));
            Assert.Contains("AUDIT_INJECTED_POST_REPLACEMENT_FAILURE", File.ReadAllText(journalPath), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(rollbackPaths.Root, "backup.exe")));
            var fallbackRestartedPath = Path.Combine(rollbackPaths.Root, "audit-restarted.txt");
            await WaitUntilAsync(() => HasRestartMarker(fallbackRestartedPath), TimeSpan.FromSeconds(20));
            using var fallbackRestarted = JsonDocument.Parse(File.ReadAllText(fallbackRestartedPath));
            Assert.Equal(new[] { "--skip-prestart-update=" + fallbackCommit },
                fallbackRestarted.RootElement.GetProperty("args").EnumerateArray().Select(item => item.GetString()).ToArray());
            Mark("updater-rollback-injected-pass", new
            {
                processId = rollbackProcess.Id, targetSha256 = Hash(oldBytes),
                restartedProcessId = fallbackRestarted.RootElement.GetProperty("processId").GetInt32(),
                restartedArguments = fallbackRestarted.RootElement.GetProperty("args").EnumerateArray().Select(item => item.GetString()).ToArray()
            });
        }
        catch (Exception error)
        {
            Mark("updater-rollback-injected-fail", new { error = error.ToString() });
            installationErrors.Add(error);
        }
        if (installationErrors.Count > 0) throw new AggregateException("Production updater audit failures", installationErrors);
    }

    private static string? ReadJournalStatus(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.GetProperty("status").GetString();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    private static bool HasRestartMarker(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("args", out var arguments) && arguments.ValueKind == JsonValueKind.Array;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return false; }
    }

    private static UpdateManifest Manifest(byte[] bytes) => new()
    {
        SchemaVersion = UpdateService.ManifestSchemaVersion,
        CommitSha = new string('b', 40), ReleaseNumber = 2, Version = "audit-2",
        PublishedAtUtc = DateTimeOffset.UtcNow, Sha256 = Hash(bytes), SizeBytes = bytes.Length
    };

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Audit scenario did not finish before its deadline.");
            await Task.Delay(100);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static AppPaths Paths(string name)
    {
        var paths = new AppPaths(Path.Combine(RequiredEnvironment("LANMINECRAFT_FUNCTIONAL_AUDIT_ROOT"), name));
        paths.Ensure();
        return paths;
    }
    private static string RequiredEnvironment(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException("Missing audit environment variable " + name);
    private static IDisposable Begin(string scenario)
    {
        Mark("scenario-start", new { scenario });
        return new ScenarioScope(scenario);
    }
    private static void Mark(string name, object? details = null)
    {
        var root = RequiredEnvironment("LANMINECRAFT_FUNCTIONAL_AUDIT_ROOT");
        Directory.CreateDirectory(root);
        File.AppendAllText(Path.Combine(root, "scenario-markers.jsonl"),
            JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, processId = Environment.ProcessId, name, details }) + Environment.NewLine);
    }
    private sealed class ScenarioScope(string scenario) : IDisposable
    {
        public void Dispose() => Mark("scenario-end", new { scenario });
    }
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The real-JDK audit permits cached local archives only.");
    }
    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class ReportFailureHandler(string mode, CancellationTokenSource cancellation) : IPortableProtocolHandler
    {
        public string ProtocolName => BugReportManifest.ProtocolName;
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task HandleAsync(Stream stream, byte[] initialFrame, PeerConnectionContext context, CancellationToken token)
        {
            try
            {
                if (mode == "disconnect") return;
                await PortableProtocol.WriteJsonAsync(stream,
                    new BugReportAck { Ok = mode != "refusal", Stage = "Accepted", Message = "AUDIT_REFUSAL" }, JsonOptions, token);
                if (mode == "cancel") cancellation.Cancel();
            }
            finally { Finished.TrySetResult(); }
        }
    }
}

[CollectionDefinition("Functional audit", DisableParallelization = true)]
public sealed class FunctionalAuditCollection;

public sealed class FunctionalAuditFactAttribute : FactAttribute
{
    public FunctionalAuditFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LANMINECRAFT_FUNCTIONAL_AUDIT_ROOT")))
            Skip = "Opt-in isolated Process Monitor audit; run Run-FunctionalAudit.ps1.";
    }
}
