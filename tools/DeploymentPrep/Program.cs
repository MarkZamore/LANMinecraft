using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class Program
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static readonly CancellationTokenSource Stop = new();

    [STAThread]
    static async Task<int> Main(string[] args)
    {
        Dictionary<string, string?> options;
        try { options = Parse(args); }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 2; }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options["root"]!));
        var assemblyPath = Path.GetFullPath(options["assembly"]!);
        var reportPath = Path.GetFullPath(options["report"]!);
        var mode = options.GetValueOrDefault("mode") ?? "all";
        var apply = options.ContainsKey("apply");
        var minecraft = mode is "all" or "minecraft26";
        var transportAll = mode is "all" or "transport-all";
        var rehearsal = options.ContainsKey("rehearsal");
        var report = new Dictionary<string, object?> { ["status"] = "STARTING", ["root"] = root,
            ["mode"] = mode, ["apply"] = apply, ["rehearsal"] = rehearsal,
            ["minecraftClientOrServerStarted"] = false, ["runtimeCopiedManually"] = false };
        var stages = new List<object>(); report["stages"] = stages;
        if (reportPath.StartsWith(Path.Combine(root, "Minecraft") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { Console.Error.WriteLine("Reports must be outside the portable Minecraft data directory."); return 2; }
        Guard.OrdinaryParents(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        void Save() => File.WriteAllText(reportPath, JsonSerializer.Serialize(report, Json));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop.Cancel(); };
        try
        {
            if (!Directory.Exists(Path.Combine(root, "Minecraft"))) throw new IOException("Root must contain the existing Minecraft portable directory.");
            if (string.Equals(root.TrimEnd('\\'), Path.GetPathRoot(root)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new IOException("Drive roots are forbidden.");
            Guard.OrdinaryParents(root); Guard.OrdinaryParents(assemblyPath);
            foreach (var relative in new[] { "Minecraft", "Minecraft/Packs", "Minecraft/Personal", "Minecraft/Personal/settings.json",
                "Minecraft/Personal/Instances", "Minecraft/Worlds", "Minecraft/Launcher", "Minecraft/Launcher/Runtimes" })
                Guard.OrdinaryParents(Path.Combine(root, relative));
            if (rehearsal && !File.Exists(Path.Combine(root, ".deployment-rehearsal"))) throw new IOException("Rehearsal requires the generated fixture marker.");
            if ((options.ContainsKey("skip-runtime") || options.ContainsKey("fixture-dist") || options.ContainsKey("fixture-components")) && !rehearsal)
                throw new IOException("Fixture substitution and skipped runtime install are forbidden outside a marked rehearsal.");
            var assemblySha = Guard.Hash(assemblyPath);
            if (options.TryGetValue("assembly-sha", out var wantedSha) && !assemblySha.Equals(wantedSha, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Assembly SHA differs from the reviewed deployment input.");
            report["assemblySha256"] = assemblySha;
            var api = new ProductionApi(assemblyPath);
            var paths = api.New("AppPaths", root);
            var seeds = api.Static("PackCatalogService", "SeedSources")!;
            var renames = RenameMap(api, seeds);
            if (minecraft && renames.GetValueOrDefault("Minecraft 26.2") != "Minecraft 26.3")
                throw new IOException("Assembly does not contain the reviewed Minecraft 26.2 -> 26.3 migration.");
            report["canonicalRenames"] = renames;
            var initialPlan = Plan(api, root, renames, minecraft || transportAll);
            report["plan"] = initialPlan;
            report["activeProcesses"] = Guard.ActiveProcesses(root);
            string NormalizeOptions(string text) => ((ValueTuple<string, int>)api.Static("PackInstanceService", "CapDistances", text)!).Item1;
            var pins = KnownPins(api);
            var before = Guard.Snapshot(root, renames, minecraft, NormalizeOptions, path => Recognized(path, pins));
            report["beforeSnapshot"] = before;
            report["status"] = apply ? "PREPARED_TO_APPLY" : "DRY_RUN";
            Save();
            if (!apply) { Console.WriteLine(JsonSerializer.Serialize(new { status = "DRY_RUN", mode, profiles = initialPlan.Count, protectedFiles = before.Files.Count, before.HashedBytes }, Json)); return 0; }
            Guard.RequireStopped(root);
            if (initialPlan.Any(plan => plan.Error is not null)) throw new IOException("Deployment plan contains conflicts; inspect the local report.");
            if (minecraft && !initialPlan.Any(plan => plan.CanonicalName == "Minecraft 26.3"))
                throw new IOException("The existing Minecraft 26.2/26.3 profile was not found; no other profile will be created instead.");
            var backup = Path.Combine(Path.GetDirectoryName(reportPath)!, Path.GetFileNameWithoutExtension(reportPath) + ".before");
            Directory.CreateDirectory(backup);
            var settingsPath = Path.Combine(root, "Minecraft", "Personal", "settings.json");
            var originalSettings = File.Exists(settingsPath) ? JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject : null;
            if (minecraft && originalSettings is null) throw new IOException("A valid existing settings object is required for final deployment.");
            // These in-memory properties are JsonIgnore in production. A
            // hand-written legacy JSON value would otherwise be discarded by
            // Load; refuse it before allowing any installation mutation.
            foreach (var name in new[] { "localIdentityId", "localIdentityName" })
                if (originalSettings?[name] is JsonValue legacy && legacy.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                    throw new IOException("Unsupported legacy identity setting requires review before production Load: " + name);
            if (File.Exists(settingsPath)) File.Copy(settingsPath, Path.Combine(backup, "settings.json"), overwrite: false);
            using var gate = new FileStream(Path.Combine(root, "Minecraft", "Personal", ".deployment-prep.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            var logger = api.New("Logger", Path.Combine(Path.GetDirectoryName(reportPath)!, "production.log"));
            using var handler = rehearsal
                ? (System.Net.Http.HttpMessageHandler)new FixtureHttp(options.GetValueOrDefault("fixture-dist"), options.GetValueOrDefault("fixture-components"))
                : new RecordingHttp();
            using var http = new System.Net.Http.HttpClient(handler);
            var transport = api.New("ManagedComponentService", paths, logger, http);
            object? selectedInstance = null;
            if (minecraft)
            {
                Guard.RequireStopped(root);
                var settingsService = api.New("SettingsService", paths, logger);
                var settings = api.Call(settingsService, "Load")!;
                api.Call(api.New("PackRenameMigration", paths, logger, null, null), "Apply", settings, seeds, api.SaveDelegate(settingsService));
                foreach (var (jsonName, property) in new[] { ("playerName", "PlayerName"), ("previousPlayerName", "PreviousPlayerName"), ("skinPath", "SkinPath"),
                    ("localIdentityId", "LocalIdentityId"), ("localIdentityName", "LocalIdentityName") })
                    if (originalSettings![jsonName] is JsonValue oldValue && oldValue.TryGetValue<string>(out var value) &&
                        !string.Equals(value.Trim(), (string?)api.Get(settings, property), StringComparison.Ordinal))
                        throw new IOException("A personal settings value changed during production load/rename: " + jsonName);
                stages.Add(new { stage = "production-settings-and-rename", selectedPack = api.Get(settings, "ClientRelativePath"),
                    settingsSha256 = File.Exists(settingsPath) ? Guard.Hash(settingsPath) : null }); Save();
                Guard.RequireStopped(root);
                var sync = api.New("PortablePackSyncService", paths, logger, http, null);
                var catalog = api.New("PackCatalogService", paths, logger, http);
                await api.Await(api.Call(catalog, "RefreshOnceAsync", Stop.Token)!);
                sync.GetType().GetProperty("Catalog")!.SetValue(sync, catalog);
                stages.Add(new { stage = "production-catalog-startup", online = api.Get(catalog, "HasOnlineCatalog"), ready = api.Get(catalog, "IsReady") });
                var result = (await api.Await(api.Call(sync, "SyncAsync", "Minecraft 26.3", null, Stop.Token)!))!;
                var outcome = api.Get(result, "Outcome")!.ToString();
                stages.Add(new { stage = "production-pack-sync", outcome, revision = api.Get(result, "Revision"),
                    filesChanged = api.Get(result, "FilesChanged"), downloadedBytes = api.Get(result, "BytesDownloaded") }); Save();
                if (outcome is "OfflineFallback" or "SkippedNoSource") throw new IOException("Fresh published Minecraft 26.3 sync was not verified.");
                var descriptor = api.Static("PackManifestService", "Load", Path.Combine(root, "Minecraft", "Packs", "Minecraft 26.3"))!;
                if (api.Get(descriptor, "MinecraftVersion")!.ToString() != "26.3" ||
                    api.Get(api.Get(descriptor, "Loader")!, "Version")!.ToString() != "26.3.0.99")
                    throw new IOException("The synced runtime differs from the reviewed Minecraft 26.3 / NeoForge 26.3.0.99 target.");
                if (options.ContainsKey("skip-runtime")) stages.Add(new { stage = "production-runtime", status = "NOT RUN: fixture explicitly skips official installer" });
                else
                {
                    Guard.RequireStopped(root);
                    var runtimes = api.New("PackRuntimeService", paths, logger, null, null);
                    try
                    {
                        var runtime = (await api.Await(api.Call(runtimes, "PrepareAsync", "Minecraft 26.3", null, Stop.Token)!))!;
                        stages.Add(new { stage = "production-runtime", status = "PREPARED, no game main invoked", profileId = api.Get(runtime, "ProfileId"),
                            javaPath = api.Get(runtime, "JavaPath"), clientJar = api.Get(runtime, "ClientJarPath") });
                    }
                    finally { ((IDisposable)runtimes).Dispose(); }
                }
                Save(); Guard.RequireStopped(root);
                var instances = api.New("PackInstanceService", paths, logger);
                try { selectedInstance = await api.Await(api.Call(instances, "PrepareAsync", "Minecraft 26.3", Stop.Token)!); }
                finally { ((IDisposable)instances).Dispose(); }
                stages.Add(new { stage = "production-instance", profile = "Minecraft 26.3" }); Save();
            }
            var work = Plan(api, root, renames, true).Where(plan => plan.Selected is not null &&
                (transportAll || minecraft && plan.Pack == "Minecraft 26.3")).ToArray();
            if (work.Any(plan => plan.Error is not null)) throw new IOException("Managed transport conflicts appeared after rename/sync.");
            var downloads = 0; var installs = 0; var cachePopulations = 0;
            var cachePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var plan in work)
            {
                Guard.RequireStopped(root);
                var pack = Path.Combine(root, "Minecraft", "Packs", plan.Pack);
                var game = Path.Combine(root, "Minecraft", "Personal", "Instances", plan.Pack);
                if (!Directory.Exists(game)) throw new IOException("Transport-only deployment requires an existing instance: " + plan.Pack);
                var descriptor = api.Static("PackManifestService", "Load", pack)!;
                var context = plan.Pack == "Minecraft 26.3" && selectedInstance is not null ? selectedInstance :
                    api.New("PackInstanceContext", pack, game, "");
                var result = (await api.Await(api.Call(transport, "EnsureSteamTransportModAsync", context, descriptor, Stop.Token)!))!;
                var installed = (string)api.Get(result, "InstalledPath")!;
                if ((bool)api.Get(result, "Downloaded")!) downloads++;
                if ((bool)api.Get(result, "Installed")!) installs++;
                if ((bool)api.Get(result, "CachePopulated")!) cachePopulations++;
                cachePaths.Add((string)api.Get(result, "CachePath")!);
                stages.Add(new { stage = "production-managed-transport", profile = plan.Pack, selected = plan.Selected,
                    downloaded = api.Get(result, "Downloaded"), installed = api.Get(result, "Installed"), cachePopulated = api.Get(result, "CachePopulated"),
                    sha256 = Guard.Hash(installed), fileName = Path.GetFileName(installed), cachePath = api.Get(result, "CachePath") }); Save();
            }
            report["transportSummary"] = new { profiles = work.Length, downloads, installs, cachePopulations,
                distinctBuilds = work.Select(plan => plan.Selected!.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count(), sharedCachePaths = cachePaths };
            report["httpRequests"] = handler is FixtureHttp fixture ? fixture.Requests : ((RecordingHttp)handler).Requests;
            Guard.RequireStopped(root);
            if (originalSettings is not null)
            {
                var finalSettings = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? throw new IOException("Final settings JSON is invalid.");
                foreach (var name in new[] { "playerName", "previousPlayerName", "skinPath", "localIdentityId", "localIdentityName" })
                    if (originalSettings[name] is JsonValue old && old.TryGetValue<string>(out var value) &&
                        !string.Equals(value.Trim(), finalSettings[name]?.GetValue<string>(), StringComparison.Ordinal))
                        throw new IOException("A personal settings value changed during deployment: " + name);
            }
            var after = Guard.Snapshot(root, renames, minecraft, NormalizeOptions, path => Recognized(path, pins));
            report["afterSnapshot"] = after;
            report["guardVerification"] = Guard.Verify(before, after, root, renames);
            report["afterPlan"] = Plan(api, root, renames, true);
            report["status"] = "PASS"; Save();
            Console.WriteLine(JsonSerializer.Serialize(new { status = "PASS", mode, profiles = work.Length, noGameStarted = true,
                protectedFiles = before.Files.Count, runtimeInstaller = options.ContainsKey("skip-runtime") ? "NOT RUN (fixture)" : minecraft ? "production PrepareAsync" : "not requested" }, Json));
            return 0;
        }
        catch (Exception error)
        {
            report["status"] = "FAIL"; report["errorType"] = error.GetType().FullName; report["error"] = error.Message; Save();
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1;
        }
    }

    sealed record Pin(string FileName, long SizeBytes, string Sha256);
    sealed record SelectedBuild(string FileName, string Version, long SizeBytes, string Sha256, string DeliveryUrl);
    sealed record TransportFile(string RelativePath, long SizeBytes, string Sha256, string State);
    sealed record ProfilePlan(string Pack, string CanonicalName, string MinecraftVersion, string Loader, SelectedBuild? Selected,
        List<TransportFile> ExistingTransports, string? Error);

    static Pin[] KnownPins(ProductionApi api) => ProductionApi.Items(api.StaticGet("SteamTransportCatalog", "Builds")!).Concat(
        ProductionApi.Items(api.StaticGet("SteamTransportCatalog", "SupersededBuilds")!)).Select(build => new Pin(
        (string)api.Get(build, "FileName")!, (long)api.Get(build, "SizeBytes")!, (string)api.Get(build, "Sha256")!)).ToArray();
    static bool Recognized(string path, Pin[] pins)
    {
        var filename = Path.GetFileName(path); var size = new FileInfo(path).Length;
        var matches = pins.Where(pin => pin.FileName.Equals(filename, StringComparison.OrdinalIgnoreCase) && pin.SizeBytes == size).ToArray();
        return matches.Length > 0 && matches.Any(pin => pin.Sha256 == Guard.Hash(path));
    }

    static List<ProfilePlan> Plan(ProductionApi api, string root, IReadOnlyDictionary<string, string> renames, bool requireOwnGit)
    {
        var known = KnownPins(api);
        var result = new List<ProfilePlan>(); var packs = Path.Combine(root, "Minecraft", "Packs");
        if (!Directory.Exists(packs)) return result;
        foreach (var directory in Directory.EnumerateDirectories(packs).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(directory, "portable-pack.json"))) continue;
            Guard.OrdinaryParents(directory);
            var name = Path.GetFileName(directory); var canonical = renames.GetValueOrDefault(name) ?? name;
            var descriptor = api.Static("PackManifestService", "Load", directory)!;
            var loader = api.Get(api.Get(descriptor, "Loader")!, "Type")!.ToString()!;
            var selected = api.Static("SteamTransportCatalog", "Find", descriptor);
            SelectedBuild? desired = null; string? error = null;
            if (selected is not null)
            {
                var urls = ProductionApi.Items(api.Get(selected, "DownloadUris")!).Cast<Uri>().ToArray();
                desired = new((string)api.Get(selected, "FileName")!, (string)api.Get(selected, "Version")!,
                    (long)api.Get(selected, "SizeBytes")!, (string)api.Get(selected, "Sha256")!, urls.Single().AbsoluteUri);
                if (requireOwnGit && !desired.DeliveryUrl.StartsWith("https://github.com/MarkZamore/e4steam/releases/download/", StringComparison.Ordinal))
                    error = "Assembly still selects a transport download outside the owner's shared repository.";
            }
            var transports = new List<TransportFile>();
            var game = Path.Combine(root, "Minecraft", "Personal", "Instances", name);
            Guard.OrdinaryParents(game);
            if (!Directory.Exists(game) && selected is not null) error ??= "Existing instance is missing; transport-only mode does not create other profiles.";
            foreach (var mods in new[] { Path.Combine(game, "mods"), Path.Combine(directory, "mods") })
            {
                Guard.OrdinaryParents(mods);
                if (Directory.Exists(mods)) foreach (var file in Directory.EnumerateFiles(mods, "*.jar"))
                {
                    var filename = Path.GetFileName(file);
                    if (!filename.StartsWith("e4steam", StringComparison.OrdinalIgnoreCase) && !filename.StartsWith("e4mc", StringComparison.OrdinalIgnoreCase)) continue;
                    Guard.OrdinaryParents(file); var sha = Guard.Hash(file); var size = new FileInfo(file).Length;
                    var recognized = known.Any(pin => pin.FileName.Equals(filename, StringComparison.OrdinalIgnoreCase) && pin.SizeBytes == size && pin.Sha256 == sha);
                    var state = !recognized ? "unknown: preserve and refuse" : desired?.FileName.Equals(filename, StringComparison.OrdinalIgnoreCase) == true ? "selected exact author bytes" : "replace known author bytes after verified install";
                    if (!recognized) error ??= "An unrecognized/conflicting transport JAR must be preserved and reviewed.";
                    transports.Add(new(Path.GetRelativePath(root, file).Replace('\\', '/'), size, sha, state));
                }
            }
            if (name != canonical && new[] { packs, Path.Combine(root, "Minecraft", "Personal", "Instances"),
                    Path.Combine(root, "Minecraft", "Worlds"), Path.Combine(root, "Minecraft", "Launcher", "Runtimes") }
                .Any(parent => Directory.Exists(Path.Combine(parent, name)) && Directory.Exists(Path.Combine(parent, canonical))))
                error ??= "Both old and renamed deployment folders exist in one of the managed roots.";
            result.Add(new(name, canonical, (string)api.Get(descriptor, "MinecraftVersion")!, loader, desired, transports, error));
        }
        return result;
    }
    static Dictionary<string, string> RenameMap(ProductionApi api, object seeds)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in ProductionApi.Items(seeds))
        foreach (var old in ProductionApi.Items(api.Get(seed, "LegacyRelativePaths")!)) result[(string)old] = (string)api.Get(seed, "RelativePath")!;
        result[(string)api.StaticGet("PortablePackSyncService", "LegacyTerraFirmaRelativePath")!] =
            (string)api.StaticGet("PortablePackSyncService", "TerraFirmaRelativePath")!;
        return result;
    }
    static Dictionary<string, string?> Parse(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var flags = new HashSet<string> { "apply", "dry-run", "rehearsal", "skip-runtime" };
        var keys = new HashSet<string> { "root", "assembly", "assembly-sha", "report", "mode", "fixture-dist", "fixture-components" };
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index].StartsWith("--", StringComparison.Ordinal) ? args[index][2..] : "";
            if (values.ContainsKey(key) || !flags.Contains(key) && !keys.Contains(key)) throw new ArgumentException("Unknown/duplicate deployment argument.");
            if (flags.Contains(key)) values[key] = null;
            else if (++index < args.Length) values[key] = args[index]; else throw new ArgumentException("Missing value for --" + key);
        }
        foreach (var key in new[] { "root", "assembly", "report" }) if (!values.ContainsKey(key)) throw new ArgumentException("Required: --assembly <production.dll> --root <portableRoot> --report <local.json> [--dry-run|--apply] [--mode minecraft26|transport-all|all]");
        if (values.ContainsKey("apply") && values.ContainsKey("dry-run")) throw new ArgumentException("Choose --apply or --dry-run.");
        if ((values.GetValueOrDefault("mode") ?? "all") is not ("all" or "minecraft26" or "transport-all")) throw new ArgumentException("Invalid deployment mode.");
        foreach (var key in new[] { "fixture-dist", "fixture-components" }) if (values.TryGetValue(key, out var value)) values[key] = Path.GetFullPath(value!);
        return values;
    }
}
