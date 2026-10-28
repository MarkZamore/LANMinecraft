using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using Minecraft;

// All mutations, including native world creation and shutdown, are confined to
// a marked independent fixture. No personal instance/world is an input.
if (args.Length < 2) throw new ArgumentException(
    "Usage: prepare <fixture-root> --source <pack> [--cache-root <installation>] [--mode baseline|integrated]; " +
    "launch <fixture-root> --label <new-label> --agent <qa-agent.jar> [--heap 10240] [--world ATM9Qa...] [--fresh-world ATM9Qa...]; " +
    "adopt <migration-installation> --cache-root <prepared-qa-installation>; ready|stop|audit <fixture-root> --label <label>");
var command = args[0];
var root = FixturePath(args[1]);
var options = ParseOptions(args.Skip(2).ToArray());
var json = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
var marker = Path.Combine(root, ".atm9-original-qa.json");
var paths = new AppPaths(root);
var packName = options.GetValueOrDefault("pack-name", "All The Mods 9");
RequireComponent(packName);
var label = options.GetValueOrDefault("label", "run-01");
if (!System.Text.RegularExpressions.Regex.IsMatch(label, "^[A-Za-z0-9_-]{1,64}$"))
    throw new ArgumentException("Invalid evidence label.");

if (command == "adopt")
{
    // Only the migration runner's already verified synthetic installation can
    // be adopted. An arbitrary populated user installation is never accepted.
    if (File.Exists(marker) || Path.GetFileName(root) != "installation")
        throw new IOException("Use an unadopted technical migration installation.");
    var migration = Path.GetDirectoryName(root)!;
    var reportPath = Path.Combine(migration, "report.json");
    var historyPath = Path.Combine(migration, "history-proof-report.json");
    var targetPath = Path.Combine(migration, "target-manifest.json");
    using var reportDocument = JsonDocument.Parse(File.ReadAllText(reportPath));
    using var historyDocument = JsonDocument.Parse(File.ReadAllText(historyPath));
    var report = reportDocument.RootElement; var history = historyDocument.RootElement;
    if (report.GetProperty("status").GetString() != "PASS" ||
        report.GetProperty("personalWorldsCopied").GetBoolean() ||
        report.GetProperty("originalInstallationModified").GetBoolean() ||
        !report.GetProperty("originalSnapshotStillMatches").GetBoolean() ||
        history.GetProperty("status").GetString() != "PASS" ||
        history.GetProperty("personalWorldsCopied").GetBoolean() ||
        !history.GetProperty("canonicalAndInstanceHelpersAbsent").GetBoolean() ||
        !history.GetProperty("sourceArchiveUnchanged").GetBoolean())
        throw new IOException("Migration and historical helper removal must both have verified PASS reports.");
    var canonical = paths.CombineUnderPacks(packName);
    using var targetDocument = JsonDocument.Parse(File.ReadAllText(targetPath));
    long verifiedFiles = 0;
    foreach (var entry in targetDocument.RootElement.GetProperty("files").EnumerateArray())
    {
        var file = Path.GetFullPath(Path.Combine(canonical, entry.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar)));
        RejectLinks(file);
        if (!IsWithin(file, canonical) || !File.Exists(file) ||
            new FileInfo(file).Length != entry.GetProperty("sizeBytes").GetInt64() ||
            Hash(file) != entry.GetProperty("sha256").GetString())
            throw new IOException("Migrated canonical files differ from the verified target manifest.");
        verifiedFiles++;
    }
    if (PortablePackSyncService.IsUpdatePending(canonical)) throw new IOException("Migration still has a pending update.");
    var sentinel = Path.Combine(paths.CombineUnderInstances(packName), "saves", "PackMigrationQa");
    var archivedSentinel = Path.Combine(root, "migration-sentinels", "PackMigrationQa");
    if (Directory.Exists(sentinel))
    {
        RejectLinks(sentinel); RejectLinks(archivedSentinel);
        var expectedSentinels = new Dictionary<string, string>
        {
            ["level.dat"] = "QA fixture only: not a Minecraft world or a personal backup",
            [Path.Combine("playerdata", "qa.dat")] = "QA player sentinel"
        };
        var sentinelFiles = Directory.EnumerateFiles(sentinel, "*", SearchOption.AllDirectories).ToArray();
        if (!IsWithin(sentinel, root) || !IsWithin(archivedSentinel, root) || Directory.Exists(archivedSentinel) || sentinelFiles.Length != expectedSentinels.Count)
            throw new IOException("The synthetic sentinel differs from the migration fixture contract.");
        foreach (var file in sentinelFiles)
        {
            RejectLinks(file);
            if (!expectedSentinels.TryGetValue(Path.GetRelativePath(sentinel, file), out var text) || File.ReadAllText(file) != text)
                throw new IOException("Refusing to move an unrecognized world or sentinel.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(archivedSentinel)!);
        Directory.Move(sentinel, archivedSentinel);
    }
    var cacheRoot = FixturePath(Required("cache-root"));
    if (!File.Exists(Path.Combine(cacheRoot, ".atm9-original-qa.json")))
        throw new IOException("Adoption reads caches only from an independent prepared QA installation.");
    var cachePaths = new AppPaths(cacheRoot);
    var cacheCopies = new List<object>();
    foreach (var pair in new[]
    {
        (cachePaths.SharedRuntime, paths.SharedRuntime), (cachePaths.JavaRuntimes, paths.JavaRuntimes),
        (Path.Combine(cachePaths.Launcher, "ManagedComponents"), Path.Combine(paths.Launcher, "ManagedComponents")),
        (cachePaths.CombineUnderRuntimes(packName), paths.CombineUnderRuntimes(packName))
    })
    {
        if (!Directory.Exists(pair.Item1)) continue;
        var records = CopyTree(pair.Item1, pair.Item2, skipInfrastructure: false);
        cacheCopies.Add(new { source = pair.Item1, target = pair.Item2, count = records.Count, treeSha256 = TreeHash(records) });
    }
    Write(Path.Combine(root, "adoption-evidence.json"), new
    {
        adoptedAtUtc = DateTimeOffset.UtcNow, verifiedFiles, migrationReportSha256 = Hash(reportPath),
        historicalProofSha256 = Hash(historyPath), targetManifestSha256 = Hash(targetPath),
        personalWorldsCopied = false, cacheCopies, syntheticSentinelWorldNotOpened = "PackMigrationQa",
        exactSyntheticSentinelArchivedOutsideSaves = Directory.Exists(archivedSentinel)
    });
    Write(marker, new Fixture("integrated", packName, DateTimeOffset.UtcNow));
    Console.WriteLine($"ADOPTED verifiedFiles={verifiedFiles} mode=integrated");
    return;
}

if (command == "prepare")
{
    if (Directory.Exists(root)) throw new IOException("Use a new independent fixture root.");
    var source = Path.GetFullPath(Required("source"));
    RejectLinks(source);
    var mode = options.GetValueOrDefault("mode", "baseline");
    if (mode is not ("baseline" or "integrated")) throw new ArgumentException("Unknown mode.");
    var descriptor = PackManifestService.Load(source);
    if (descriptor.MinecraftVersion != "1.20.1" || descriptor.Loader.Type != PackLoaderKind.Forge)
        throw new ArgumentException("This native QA agent is limited to Minecraft 1.20.1 Forge.");
    if (IsWithin(root, source) || IsWithin(source, root)) throw new IOException("Source and fixture must be disjoint.");
    paths.Ensure();
    var destination = paths.CombineUnderPacks(packName);
    var files = CopyTree(source, destination, skipInfrastructure: true);
    Write(Path.Combine(root, "source-snapshot.json"), new
    {
        schemaVersion = 1, copiedAtUtc = DateTimeOffset.UtcNow, source, mode,
        treeSha256 = TreeHash(files), files,
        authorFilesChangedByHarness = false, personalWorldsCopied = false
    });
    var cacheCopies = new List<object>();
    if (options.TryGetValue("cache-root", out var cacheRoot))
    {
        var cachePaths = new AppPaths(Path.GetFullPath(cacheRoot));
        foreach (var pair in new[]
        {
            (cachePaths.SharedRuntime, paths.SharedRuntime),
            (cachePaths.JavaRuntimes, paths.JavaRuntimes),
            (Path.Combine(cachePaths.Launcher, "ManagedComponents"), Path.Combine(paths.Launcher, "ManagedComponents")),
            (cachePaths.CombineUnderRuntimes(packName), paths.CombineUnderRuntimes(packName))
        })
        {
            if (!Directory.Exists(pair.Item1)) continue;
            var records = CopyTree(pair.Item1, pair.Item2, skipInfrastructure: false);
            cacheCopies.Add(new { source = pair.Item1, target = pair.Item2, count = records.Count, treeSha256 = TreeHash(records) });
        }
    }
    Write(marker, new Fixture(mode, packName, DateTimeOffset.UtcNow));
    Write(Path.Combine(root, "cache-copy.json"), cacheCopies);
    var logger = new Logger(paths.LogFile);
    using var runtimes = new PackRuntimeService(paths, logger);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(45));
    var runtime = await runtimes.PrepareAsync(packName, Progress(), timeout.Token);
    Write(Path.Combine(root, "runtime.json"), runtime);
    Console.WriteLine($"PREPARED mode={mode} minecraft={descriptor.MinecraftVersion} loader={descriptor.Loader.Version} files={files.Count}");
    return;
}

if (!File.Exists(marker)) throw new IOException("Not a marked ATM9 original QA fixture.");
var fixture = Read<Fixture>(marker);
packName = fixture.PackName;
if (command == "prepare-integration")
{
    if (fixture.Mode != "integrated") throw new IOException("Production integration preparation requires an integrated fixture.");
    if (HasLiveQaClient()) throw new IOException("Stop the recorded QA client before preparing its instance.");
    var logger = new Logger(paths.LogFile);
    using var stageRuntimeService = new PackRuntimeService(paths, logger);
    using var stageInstances = new PackInstanceService(paths, logger);
    using var identity = new PortableIdentityAdapterService(paths, logger);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(45));
    var runtime = await stageRuntimeService.PrepareAsync(packName, Progress(), timeout.Token);
    var stageInstance = await stageInstances.PrepareAsync(packName, timeout.Token);
    await new ManagedComponentService(paths, logger).EnsureSteamTransportModAsync(stageInstance, runtime.Descriptor, timeout.Token);
    var adapterArguments = await identity.PrepareJvmArgumentsAsync(runtime, stageInstance.GameDirectory, timeout.Token);
    if (!adapterArguments.Any(argument => argument.StartsWith("-javaagent:", StringComparison.Ordinal)))
        throw new IOException("Production identity/LAN adapter was not prepared.");
    Write(Path.Combine(root, "integration-preparation.json"), new
    {
        preparedAtUtc = DateTimeOffset.UtcNow, runtime.Descriptor, runtime.ProfileId,
        launcherAssemblySha256 = Hash(typeof(PackRuntimeService).Assembly.Location),
        productionAdapterPrepared = true, e4steamPrepared = true, gameDirectory = stageInstance.GameDirectory,
        mods = Snapshot(stageInstance.GameDirectory).Where(file => file.Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)),
        gameStarted = false
    });
    Console.WriteLine("INTEGRATION_PREPARED gameStarted=false");
    return;
}
var evidence = Path.Combine(root, "evidence", label);
if (command is "ready" or "stop" or "audit")
{
    var run = Read<Run>(Path.Combine(evidence, "run.json"));
    if (command == "audit")
    {
        // Keep full logs local. The bounded report contains exact error families,
        // timestamps and evidence flags; it does not assert visual/Steam PASS.
        var errors = new List<object>();
        var errorFamilies = new Dictionary<string, ErrorFamily>(StringComparer.Ordinal);
        long errorEventCount = 0;
        var log = Path.Combine(run.GameDirectory, "logs", "latest.log");
        if (File.Exists(log))
            foreach (var line in ReadSharedLines(log))
                if (line.Contains("/ERROR]") || line.Contains("/FATAL]"))
                {
                    errorEventCount++;
                    if (errors.Count < 300) errors.Add(new { line = line.Length > 1200 ? line[..1200] : line });
                    var firstBracket = line.IndexOf(']');
                    var family = firstBracket >= 0 ? line[(firstBracket + 1)..].TrimStart() : line;
                    // Thread names vary; retain severity/logger/message verbatim.
                    var threadEnd = family.IndexOf("]: ", StringComparison.Ordinal);
                    if (threadEnd >= 0)
                    {
                        var severityStart = family.LastIndexOf('/', threadEnd);
                        if (severityStart >= 0) family = family[severityStart..];
                    }
                    var time = firstBracket >= 0 ? line[..(firstBracket + 1)] : "unknown";
                    if (errorFamilies.TryGetValue(family, out var previous))
                        errorFamilies[family] = previous with { Count = previous.Count + 1, LastTime = time };
                    else if (errorFamilies.Count < 1000)
                        errorFamilies[family] = new ErrorFamily(family, 1, time, time);
                }
        var result = new
        {
            mode = fixture.Mode, label, checkedAtUtc = DateTimeOffset.UtcNow,
            processStillRunning = IsRunning(run.ProcessId), latestLogExists = File.Exists(log),
            errorEvents = errors, errorEventCount, errorFamilies = errorFamilies.Values,
            errorReportLimit = 300, uniqueErrorFamilyLimit = 1000,
            observations = ReadJsonLines(Path.Combine(evidence, "observations.jsonl")),
            exit = File.Exists(Path.Combine(evidence, "exit.json")) ? JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "exit.json"))).RootElement.Clone() : (JsonElement?)null,
            actualSteamGuestConnectionVerified = false, overlayVerified = false, visualQualityVerified = false
        };
        Write(Path.Combine(evidence, "audit.json"), result);
        Console.WriteLine($"AUDIT running={result.processStillRunning} errorsCaptured={errors.Count}");
    }
    else
    {
        if (!IsRunning(run.ProcessId)) throw new IOException("The recorded QA process has exited.");
        File.WriteAllText(Path.Combine(evidence, command == "ready" ? "ready.flag" : "stop.flag"), DateTimeOffset.UtcNow.ToString("O"));
        Console.WriteLine(command == "ready" ? "READY_MARKER_WRITTEN" : "NATIVE_SHUTDOWN_REQUESTED");
    }
    return;
}
if (command != "launch") throw new ArgumentException("Unknown command.");
if (Directory.Exists(evidence)) throw new IOException("Use a new evidence label.");
Directory.CreateDirectory(Path.Combine(root, "evidence"));
if (HasLiveQaClient())
    throw new IOException("This fixture already has a live QA process.");
var heap = int.Parse(options.GetValueOrDefault("heap", "10240"), CultureInfo.InvariantCulture);
if (heap is < 4096 or > 24576) throw new ArgumentException("Heap must be 4096..24576 MiB.");
var player = options.GetValueOrDefault("player", "ATM9Qa");
if (!System.Text.RegularExpressions.Regex.IsMatch(player, "^[A-Za-z0-9_]{3,16}$")) throw new ArgumentException("Invalid QA player name.");
var world = options.GetValueOrDefault("world");
var freshWorld = options.GetValueOrDefault("fresh-world");
if (world is not null && freshWorld is not null) throw new ArgumentException("Use --world or --fresh-world.");
foreach (var name in new[] { world, freshWorld }.OfType<string>())
    if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^ATM9Qa[A-Za-z0-9_-]{0,48}$")) throw new ArgumentException("Only reserved disposable ATM9Qa worlds are accepted.");
var agent = Path.GetFullPath(Required("agent"));
RejectLinks(agent);
if (!File.Exists(agent)) throw new FileNotFoundException("Build the test-only QA agent first.", agent);
Directory.CreateDirectory(evidence);
var launchLogger = new Logger(paths.LogFile);
using var runtimeService = new PackRuntimeService(paths, launchLogger);
using var preparing = new CancellationTokenSource(TimeSpan.FromMinutes(45));
var prepared = await runtimeService.PrepareAsync(packName, Progress(), preparing.Token);
using var instances = new PackInstanceService(paths, launchLogger);
PackInstanceContext instance;
var integrationArguments = new List<string>();
if (fixture.Mode == "integrated")
{
    instance = await instances.PrepareAsync(packName, preparing.Token);
    await new ManagedComponentService(paths, launchLogger).EnsureSteamTransportModAsync(instance, prepared.Descriptor, preparing.Token);
    using var identity = new PortableIdentityAdapterService(paths, launchLogger);
    integrationArguments.AddRange(await identity.PrepareJvmArgumentsAsync(prepared, instance.GameDirectory, preparing.Token));
    if (!integrationArguments.Any(arg => arg.StartsWith("-javaagent:", StringComparison.Ordinal)))
        throw new IOException("Integrated fixture did not prepare the production identity/LAN adapter.");
}
else
{
    var game = paths.CombineUnderInstances(packName);
    if (!Directory.Exists(game)) CopyTree(paths.CombineUnderPacks(packName), game, skipInfrastructure: true);
    instance = new PackInstanceContext(paths.CombineUnderPacks(packName), game, prepared.ClientJarPath);
}
if (world is not null && !File.Exists(Path.Combine(instance.GameDirectory, "saves", world, "level.dat")))
    throw new IOException("QuickPlay requires an existing disposable QA world created and saved in this fixture.");
if (freshWorld is not null && Directory.Exists(Path.Combine(instance.GameDirectory, "saves", freshWorld)))
    throw new IOException("Fresh QA world already exists.");
Directory.CreateDirectory(Path.Combine(instance.GameDirectory, "saves"));
Write(Path.Combine(evidence, "game-before-launch.json"), Snapshot(instance.GameDirectory));
var launcher = runtimeService.CreateLocalLauncher(prepared);
var profile = await launcher.GetVersionAsync(prepared.ProfileId, preparing.Token);
var launchPath = new MinecraftPath(instance.GameDirectory)
{
    Library = launcher.MinecraftPath.Library, Versions = launcher.MinecraftPath.Versions,
    Assets = launcher.MinecraftPath.Assets, Resource = launcher.MinecraftPath.Resource, Runtime = launcher.MinecraftPath.Runtime
};
launchPath.CreateDirs();
var home = Path.Combine(root, "java-home");
var temporary = Path.Combine(root, "java-temp", label);
Directory.CreateDirectory(home); Directory.CreateDirectory(temporary);
var session = MSession.CreateOfflineSession(player);
var uuidBytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + player));
uuidBytes[6] = (byte)((uuidBytes[6] & 15) | 48); uuidBytes[8] = (byte)((uuidBytes[8] & 63) | 128);
session.UUID = new Guid(uuidBytes, bigEndian: true).ToString("N");
var useSelectedSkin = options.ContainsKey("skin-from-install-root") || options.ContainsKey("skin-path");
if (options.ContainsKey("skin-from-install-root") && options.ContainsKey("skin-path"))
    throw new ArgumentException("Use --skin-from-install-root or --skin-path, not both.");
await using var skinService = useSelectedSkin ? new SkinService(paths, launchLogger, new NullPeerTransport()) : null;
string? skinRegistryPath = null;
if (skinService is not null)
{
    // Read no personal settings by default. With explicit opt-in only SkinPath
    // is consumed; no source settings, identity, or chosen PNG is written.
    string selectedSkin;
    if (options.TryGetValue("skin-path", out var explicitSkin)) selectedSkin = Path.GetFullPath(explicitSkin);
    else
    {
        var selectedInstall = new AppPaths(Path.GetFullPath(Required("skin-from-install-root")));
        using var selectedSettings = JsonDocument.Parse(File.ReadAllText(Path.Combine(selectedInstall.Personal, "settings.json")));
        var skinPathSetting = selectedSettings.RootElement.EnumerateObject().FirstOrDefault(property =>
            property.Name.Equals("skinPath", StringComparison.OrdinalIgnoreCase));
        var value = skinPathSetting.Value.ValueKind == JsonValueKind.String ? skinPathSetting.Value.GetString() : null;
        if (string.IsNullOrWhiteSpace(value)) throw new IOException("The opted-in installation has no selected skin.");
        selectedSkin = Path.GetFullPath(value, selectedInstall.Root);
    }
    if (!File.Exists(selectedSkin)) throw new IOException("The opted-in selected skin is unavailable.");
    var selectedHash = Hash(selectedSkin);
    var copiedSkin = Path.Combine(evidence, "selected-skin.png");
    File.Copy(selectedSkin, copiedSkin, overwrite: false);
    if (Hash(copiedSkin) != selectedHash || Hash(selectedSkin) != selectedHash)
        throw new IOException("The selected skin changed during its independent copy.");
    await skinService.StartAsync(preparing.Token);
    var skinSettings = new AppSettings { SkinPath = copiedSkin, PlayerName = player };
    var skinIdentity = new LocalIdentityContext
    {
        IdentityName = player, MinecraftUuid = MinecraftProcessService.PlayingHere(session.UUID),
        PlayerUuid = Guid.Parse(session.UUID), SessionAccessToken = session.AccessToken ?? ""
    };
    skinRegistryPath = skinService.PrepareRegistry(skinSettings, skinIdentity);
    var announcement = skinService.GetAnnouncement(skinSettings, skinIdentity.MinecraftUuid);
    if (!announcement.IsAvailable || !File.Exists(skinRegistryPath))
        throw new IOException("The production skin service did not register the selected skin.");
    var entry = File.ReadAllLines(skinRegistryPath).Single(line =>
        line.StartsWith(skinIdentity.MinecraftUuid + "|", StringComparison.OrdinalIgnoreCase)).Split('|');
    if (entry.Length != 4 || !Uri.TryCreate(entry[3], UriKind.Absolute, out var textureUri) ||
        textureUri.Scheme != "http" || textureUri.Host != "127.0.0.1")
        throw new IOException("The production skin registry is not the expected loopback endpoint.");
    using var skinHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var servedSkin = await skinHttp.GetByteArrayAsync(textureUri, preparing.Token);
    var servedHash = Convert.ToHexString(SHA256.HashData(servedSkin)).ToLowerInvariant();
    if (servedHash != selectedHash || !announcement.Sha256.Equals(selectedHash, StringComparison.OrdinalIgnoreCase) ||
        !entry[1].Equals(selectedHash, StringComparison.OrdinalIgnoreCase) || entry[2] != announcement.Model ||
        !servedSkin.AsSpan().SequenceEqual(File.ReadAllBytes(copiedSkin)))
        throw new IOException("Production loopback skin bytes/hash/model differ from the selected PNG.");
    Write(Path.Combine(evidence, "skin-evidence.json"), new
    {
        exists = true, sha256 = selectedHash, model = announcement.Model, sizeBytes = servedSkin.Length,
        loopbackExactBytesVerified = true, sourcePngUnchanged = Hash(selectedSkin) == selectedHash,
        sourceSettingsWritten = false, peerTransport = "NullPeerTransport", actualPeerSkinVerified = false,
        qaOfflineIdentity = true, registryPassedToGame = true,
        serviceLifetime = "Started before launch and held until native game exit by await using."
    });
    Console.WriteLine($"SKIN_READY exists=true sha256={selectedHash} model={announcement.Model} sizeBytes={servedSkin.Length}");
}
var arguments = new List<string>
{
    "-Dfile.encoding=UTF-8", "-Duser.home=" + home, "-Djava.io.tmpdir=" + temporary,
    "-XX:ErrorFile=" + Path.Combine(evidence, "hs_err_pid%p.log"),
    "-Dlanminecraft.qa.root=" + root, "-Dlanminecraft.qa.game=" + instance.GameDirectory,
    "-Dlanminecraft.qa.evidence=" + evidence, "-javaagent:" + agent
};
if (freshWorld is not null) arguments.Add("-Dlanminecraft.qa.freshWorld=" + freshWorld);
if (skinRegistryPath is not null) arguments.Add("-Dminecraft.portable.skin.registry=" + skinRegistryPath);
if (fixture.Mode == "integrated")
{
    arguments.Add("-Dminecraft.portable.identity.localPlayer=" + MinecraftProcessService.PlayingHere(session.UUID));
    arguments.AddRange(new PackJvmArgumentsService(launchLogger).Load(instance.PackDirectory));
    arguments.AddRange(MinecraftProcessService.HeapTuningArgumentsFor(heap));
    arguments.AddRange(MinecraftProcessService.JavaCompatibilityArguments);
}
arguments.AddRange(integrationArguments);
var launchOption = new MLaunchOption
{
    Path = launchPath, JavaPath = prepared.JavaPath, Session = session,
    MaximumRamMb = heap, MinimumRamMb = fixture.Mode == "integrated" ? heap : 1024, GameLauncherName = "LANMinecraft",
    GameLauncherVersion = "1", VersionType = $"{prepared.Descriptor.Loader.Type} {prepared.Descriptor.Loader.Version}",
    FullScreen = false, ScreenWidth = 960, ScreenHeight = 540, QuickPlaySingleplayer = world,
    QuickPlayPath = Path.Combine(evidence, "quickplay.json"), ExtraJvmArguments = arguments.Select(value => new MArgument(value)).ToList()
};
using var process = launcher.BuildProcess(profile, launchOption);
// Deep independent migration fixtures otherwise exceed CreateProcess' 32767
// character limit. Java 17 reads the same tokens from an ordinary local argfile.
var javaArguments = process.StartInfo.ArgumentList.Count > 0
    ? process.StartInfo.ArgumentList.ToArray() : WindowsArguments.Split(process.StartInfo.Arguments);
var argumentFile = Path.Combine(evidence, "java-arguments.txt");
File.WriteAllLines(argumentFile, javaArguments.Select(JavaArgument), new UTF8Encoding(false));
process.StartInfo.Arguments = "";
process.StartInfo.ArgumentList.Clear();
process.StartInfo.ArgumentList.Add("@" + argumentFile);
process.StartInfo.WorkingDirectory = instance.GameDirectory;
process.StartInfo.UseShellExecute = false; process.StartInfo.CreateNoWindow = true; process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
process.StartInfo.RedirectStandardOutput = true; process.StartInfo.RedirectStandardError = true;
process.StartInfo.StandardOutputEncoding = Encoding.UTF8; process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
process.StartInfo.Environment.Remove("SteamNoOverlayUIDrawing");
if (fixture.Mode == "integrated")
{
    // SteamworksApiFacade.Initialize sets these on the production launcher;
    // reproduce exactly the environment its game child inherits.
    var appId = SteamworksApiFacade.SharedAppId.ToString(CultureInfo.InvariantCulture);
    process.StartInfo.Environment["SteamAppId"] = appId;
    process.StartInfo.Environment["SteamGameId"] = appId;
}
foreach (var pair in new Dictionary<string, string>
{
    ["TEMP"] = temporary, ["TMP"] = temporary,
    ["APPDATA"] = Path.Combine(home, "AppData", "Roaming"), ["LOCALAPPDATA"] = Path.Combine(home, "AppData", "Local")
})
{
    Directory.CreateDirectory(pair.Value); process.StartInfo.Environment[pair.Key] = pair.Value;
}
using var output = new StreamWriter(Path.Combine(evidence, "console.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
var outputGate = new object();
void Record(string? value) { if (value is not null) lock (outputGate) output.WriteLine(value); }
process.OutputDataReceived += (_, e) => Record(e.Data); process.ErrorDataReceived += (_, e) => Record(e.Data);
var started = DateTimeOffset.UtcNow;
process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
Write(Path.Combine(evidence, "run.json"), new Run(process.Id, started, instance.GameDirectory, fixture.Mode, label));
Write(Path.Combine(evidence, "launch-evidence.json"), new
{
    fixture.Mode, player, session.UUID, heapMiB = heap, world, freshWorld,
    localPlayerUuid = fixture.Mode == "integrated" ? MinecraftProcessService.PlayingHere(session.UUID) : null,
    prepared.Descriptor, prepared.ProfileId, qaAgentSha256 = Hash(agent),
    launcherAssemblySha256 = Hash(typeof(PackRuntimeService).Assembly.Location),
    productionAdapterPrepared = fixture.Mode == "integrated", e4steamPrepared = fixture.Mode == "integrated",
    authorOptionsEditedByHarness = false, ramAdvisoryDisabled = false,
    steamAccountIdentity = false, fullProductionUiLaunchClaimed = false,
    steamAppId = fixture.Mode == "integrated" ? SteamworksApiFacade.SharedAppId : (uint?)null,
    steamGameId = fixture.Mode == "integrated" ? SteamworksApiFacade.SharedAppId : (uint?)null,
    steamOverlayDisabled = process.StartInfo.Environment.ContainsKey("SteamNoOverlayUIDrawing"),
    javaArgumentFileUsed = true,
    selectedSkinPrepared = skinRegistryPath is not null,
    note = "Isolated offline QA identity. Native observation/creation/stop agent is test instrumentation, never a shipped mod."
});
Console.WriteLine($"CLIENT_STARTED pid={process.Id} mode={fixture.Mode} label={label}");
await process.WaitForExitAsync();
Write(Path.Combine(evidence, "exit.json"), new { processId = process.Id, exitCode = process.ExitCode, exitedAtUtc = DateTimeOffset.UtcNow });
var latestNativeLog = Path.Combine(instance.GameDirectory, "logs", "latest.log");
if (File.Exists(latestNativeLog)) File.Copy(latestNativeLog, Path.Combine(evidence, "latest.log"), overwrite: false);
Write(Path.Combine(evidence, "game-after-exit.json"), Snapshot(instance.GameDirectory));
Console.WriteLine("CLIENT_EXIT code=" + process.ExitCode);
Environment.ExitCode = process.ExitCode;

string Required(string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException("Missing --" + key);
void Write(string path, object value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(value, json)); }
T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), json) ?? throw new IOException("Empty evidence.");
bool HasLiveQaClient() => Directory.Exists(Path.Combine(root, "evidence")) &&
    Directory.EnumerateFiles(Path.Combine(root, "evidence"), "run.json", SearchOption.AllDirectories)
        .Select(Read<Run>).Any(run => IsRunning(run.ProcessId));
static IProgress<RuntimePreparationProgress> Progress() => new Progress<RuntimePreparationProgress>(p => Console.WriteLine(p.Stage + ": " + p.Message));
static Dictionary<string, string> ParseOptions(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < values.Length; i += 2)
    {
        if (!values[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= values.Length || !result.TryAdd(values[i][2..], values[i + 1]))
            throw new ArgumentException("Options must be unique --name value pairs.");
    }
    return result;
}
static string FixturePath(string path)
{
    var full = Path.GetFullPath(path);
    if (!full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("artifacts", StringComparer.OrdinalIgnoreCase))
        throw new IOException("Fixture must be below an artifacts directory.");
    RejectLinks(full);
    return full;
}
static void RequireComponent(string value)
{
    if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || Path.GetFileName(value) != value || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        throw new ArgumentException("Expected one safe path component.");
}
static void RejectLinks(string path)
{
    for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked paths are forbidden in the QA fixture.");
}
static bool IsWithin(string path, string parent) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFullPath(path), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase);
static bool IsRunning(int pid) { try { return !Process.GetProcessById(pid).HasExited; } catch (ArgumentException) { return false; } }
static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
static List<FileRecord> CopyTree(string source, string destination, bool skipInfrastructure)
{
    RejectLinks(source); RejectLinks(destination); Directory.CreateDirectory(destination);
    var records = new List<FileRecord>();
    var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { ".git", "artifacts", "tools", "library", "logs", "crash-reports", "screenshots", "saves", "playerdata", "xaero", "local", ".mixin.out" };
    void CopyDirectory(string input, string output, bool first)
    {
        foreach (var item in Directory.EnumerateFileSystemEntries(input).Order(StringComparer.Ordinal))
        {
            RejectLinks(item); var name = Path.GetFileName(item);
            if (skipInfrastructure && first && (skipped.Contains(name) || name.StartsWith(".portable-", StringComparison.Ordinal))) continue;
            var target = Path.Combine(output, name);
            if (Directory.Exists(item)) { Directory.CreateDirectory(target); CopyDirectory(item, target, false); }
            else
            {
                File.Copy(item, target, overwrite: false); File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(item));
                var expected = Hash(item); if (Hash(target) != expected) throw new IOException("Independent copy checksum mismatch.");
                records.Add(new FileRecord(Path.GetRelativePath(source, item).Replace('\\', '/'), new FileInfo(item).Length, expected));
            }
        }
    }
    CopyDirectory(source, destination, true);
    return records;
}
static List<FileRecord> Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    .Where(file => !Path.GetRelativePath(root, file).StartsWith("logs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    .Select(file => { RejectLinks(file); return new FileRecord(Path.GetRelativePath(root, file).Replace('\\', '/'), new FileInfo(file).Length, Hash(file)); }).ToList();
static string TreeHash(IEnumerable<FileRecord> records) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", records.OrderBy(row => row.Path, StringComparer.Ordinal).Select(row => $"{row.Path}\t{row.SizeBytes}\t{row.Sha256}"))))).ToLowerInvariant();
static string JavaArgument(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
    .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
    .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
static List<JsonElement> ReadJsonLines(string path)
{
    var rows = new List<JsonElement>();
    if (!File.Exists(path)) return rows;
    foreach (var line in ReadSharedLines(path).TakeLast(100))
        try { rows.Add(JsonDocument.Parse(line).RootElement.Clone()); } catch (JsonException) { }
    return rows;
}
static IEnumerable<string> ReadSharedLines(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
    while (reader.ReadLine() is { } line) yield return line;
}
record Fixture(string Mode, string PackName, DateTimeOffset CreatedAtUtc);
record FileRecord(string Path, long SizeBytes, string Sha256);
record Run(int ProcessId, DateTimeOffset StartedAtUtc, string GameDirectory, string Mode, string Label);
record ErrorFamily(string Family, long Count, string FirstTime, string LastTime);
static class WindowsArguments
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
    public static string[] Split(string arguments)
    {
        var argv = CommandLineToArgvW("java " + arguments, out var count);
        if (argv == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            return Enumerable.Range(1, count - 1)
                .Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size))!).ToArray();
        }
        finally { LocalFree(argv); }
    }
}
