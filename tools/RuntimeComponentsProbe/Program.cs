using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using Minecraft;

if (args.Length is not (3 or 4 or 5))
    throw new ArgumentException("Usage: <installation-root> <owned-run-directory> <qa-agent.jar> [pack-name] [evidence-id]");
var installation = Path.GetFullPath(args[0]);
var root = Path.GetFullPath(args[1]);
var observer = Path.GetFullPath(args[2]);
var pack = args.Length >= 4 ? args[3] : "Better Vanilla";
var evidenceId = args.Length == 5 ? args[4] : "";
if (evidenceId.Length > 30 || evidenceId.Any(value => !char.IsAsciiLetterOrDigit(value) && value != '-'))
    throw new ArgumentException("Evidence ID must contain at most 30 ASCII letters, digits or hyphens.");
var evidencePrefix = evidenceId.Length == 0 ? "" : evidenceId + "-";
if (Path.GetFileName(Path.GetDirectoryName(root)) != "agent-runs" || !File.Exists(Path.Combine(root, "RUN.json")))
    throw new IOException("The probe requires an owned agent-runs directory.");
if (!observer.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(observer))
    throw new IOException("Build the observer inside this owned fixture.");
var paths = new AppPaths(installation);
var source = paths.CombineUnderPacks(pack);
var descriptor = PackManifestService.Load(source);
if (descriptor.MinecraftVersion != "26.3" || descriptor.Loader.Type != PackLoaderKind.Fabric)
    throw new NotSupportedException("The reviewed native observer supports Minecraft 26.3 Fabric.");
var fixturePaths = new AppPaths(root);
var game = fixturePaths.CombineUnderInstances(pack);
if (Directory.Exists(game)) throw new IOException("Remove the previous owned mutable game copy before repeating the probe.");
if (Directory.Exists(Path.Combine(root, evidencePrefix + "online")) || Directory.Exists(Path.Combine(root, evidencePrefix + "offline")))
    throw new IOException("Archive compact results and remove this fixture's previous online/offline evidence before repeating the probe.");
var json = new JsonSerializerOptions { WriteIndented = true };
var logger = new Logger(Path.Combine(root, "launcher.log"));
var sourceFiles = new List<SourceFile>();
var reports = new List<object>();
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(24));
try
{
    foreach (var folder in new[] { "mods", "config", "resourcepacks", "shaderpacks", "texturepacks", "kubejs", "scripts" })
    {
        var directory = Path.Combine(source, folder);
        if (Directory.Exists(directory)) CopyAuthorTree(directory, Path.Combine(game, folder));
    }
    Directory.CreateDirectory(game);
    Directory.CreateDirectory(Path.Combine(game, "saves"));
    File.WriteAllText(Path.Combine(game, "options.txt"),
        "lang:en_us\nonboardAccessibility:false\nskipMultiplayerWarning:true\ntutorialStep:none\n", new UTF8Encoding(false));
    new OptionsDefaultsService(logger).Apply(source, game, descriptor.MinecraftVersion);
    Write(Path.Combine(root, "source-manifest.json"), sourceFiles);

    foreach (var offline in new[] { false, true })
    {
        var label = offline ? "offline" : "online";
        var evidence = Directory.CreateDirectory(Path.Combine(root, evidencePrefix + label)).FullName;
        using var blocked = new OfflineHandler();
        using var http = offline ? new HttpClient(blocked) : new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MinecraftPortable/1.0");
        var components = new RuntimeComponentService(paths, logger, http);
        using var selection = await components.PrepareAsync(pack, descriptor, null, deadline.Token);
        using var scope = RuntimeComponentContext.Use(selection);
        using var runtimeService = new PackRuntimeService(paths, logger, httpClient: http);
        var prepared = await runtimeService.PrepareAsync(pack, null, deadline.Token);
        var instance = new PackInstanceContext(source, game, prepared.ClientJarPath);
        var managedSteamSeededFromSharedVerifiedCache = SeedManagedSteamJar();
        _ = await new ManagedComponentService(fixturePaths, logger, http).EnsureSteamTransportModAsync(instance, descriptor, deadline.Token);
        using var identity = new PortableIdentityAdapterService(paths, logger);
        var integration = await identity.PrepareJvmArgumentsAsync(prepared, game, deadline.Token);
        var noticeService = new GameNoticeOverlayService(paths, logger);
        using var notice = (IDisposable)(typeof(GameNoticeOverlayService)
            .GetMethod("PrepareSession", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(noticeService, [false])!);
        var noticeArguments = (IReadOnlyList<string>)notice.GetType()
            .GetProperty("JvmArguments", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(notice)!;
        var launcher = runtimeService.CreateLocalLauncher(prepared);
        var profile = await launcher.GetVersionAsync(prepared.ProfileId, deadline.Token);
        var shared = launcher.MinecraftPath;
        var launchPath = new MinecraftPath(game)
        {
            Library = shared.Library, Versions = shared.Versions, Assets = shared.Assets,
            Resource = shared.Resource, Runtime = shared.Runtime
        };
        launchPath.CreateDirs();
        var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
        var temp = Directory.CreateDirectory(Path.Combine(root, "temp")).FullName;
        var session = MSession.CreateOfflineSession("ComponentsQA");
        session.UUID = "f2cf86fc70524db3b414ff69f6ea8a81";
        var extra = new List<string>
        {
            "-Dfile.encoding=UTF-8", "-Duser.home=" + home, "-Djava.io.tmpdir=" + temp,
            "-XX:ErrorFile=" + Path.Combine(evidence, "hs_err_pid%p.log"),
            "-Dlanminecraft.qa.root=" + root, "-Dlanminecraft.qa.game=" + game,
            "-Dlanminecraft.qa.evidence=" + evidence, "-Dlanminecraft.qa.fresh=" + (!offline).ToString().ToLowerInvariant(),
            "-Dminecraft.portable.identity.localPlayer=" + session.UUID,
            "-javaagent:" + observer
        };
        extra.AddRange(new PackJvmArgumentsService(logger).Load(source));
        extra.AddRange(integration);
        extra.AddRange(noticeArguments);
        var options = new MLaunchOption
        {
            Path = launchPath, JavaPath = prepared.JavaPath, Session = session,
            MaximumRamMb = 8192, MinimumRamMb = 1024, ScreenWidth = 960, ScreenHeight = 540,
            GameLauncherName = "LANMinecraft", GameLauncherVersion = "1", FullScreen = false,
            ExtraJvmArguments = extra.Select(argument => new MArgument(argument)).ToList()
        };
        using var client = launcher.BuildProcess(profile, options);
        var tokens = client.StartInfo.ArgumentList.Count > 0 ? client.StartInfo.ArgumentList.ToArray()
            : WindowsArguments.Split(client.StartInfo.Arguments);
        var argumentFile = Path.Combine(evidence, "java.args");
        File.WriteAllLines(argumentFile, tokens.Select(JavaArgument), new UTF8Encoding(false));
        client.StartInfo.Arguments = "";
        client.StartInfo.ArgumentList.Clear();
        client.StartInfo.ArgumentList.Add("@" + argumentFile);
        client.StartInfo.WorkingDirectory = game;
        client.StartInfo.UseShellExecute = false;
        client.StartInfo.CreateNoWindow = true;
        client.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
        client.StartInfo.RedirectStandardOutput = true;
        client.StartInfo.RedirectStandardError = true;
        client.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        client.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        client.StartInfo.Environment["TEMP"] = temp;
        client.StartInfo.Environment["TMP"] = temp;
        client.StartInfo.Environment["APPDATA"] = Directory.CreateDirectory(Path.Combine(home, "AppData", "Roaming")).FullName;
        client.StartInfo.Environment["LOCALAPPDATA"] = Directory.CreateDirectory(Path.Combine(home, "AppData", "Local")).FullName;
        using var output = new StreamWriter(Path.Combine(evidence, "console.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
        var outputGate = new object();
        long written = 0;
        void Record(string? line)
        {
            if (line is null) return;
            lock (outputGate)
            {
                if (written > 32L * 1024 * 1024) return;
                output.WriteLine(line);
                written += Encoding.UTF8.GetByteCount(line) + 2;
            }
        }
        client.OutputDataReceived += (_, value) => Record(value.Data);
        client.ErrorDataReceived += (_, value) => Record(value.Data);
        var watch = Stopwatch.StartNew();
        var started = false;
        try
        {
            client.Start();
            started = true;
            client.BeginOutputReadLine();
            client.BeginErrorReadLine();
            notice.GetType().GetMethod("BindProcess", BindingFlags.Instance | BindingFlags.NonPublic,
                null, [typeof(Process)], null)!.Invoke(notice, [client]);
            Write(Path.Combine(evidence, "process.json"), new { client.Id, startedUtc = client.StartTime.ToUniversalTime() });
            Console.WriteLine($"NATIVE_CLIENT_STARTED mode={label} pid={client.Id}");
            await client.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (started && !client.HasExited)
            {
                client.Kill(entireProcessTree: true);
                await client.WaitForExitAsync();
            }
        }
        watch.Stop();
        var nativePath = Path.Combine(evidence, "native-result.json");
        using var native = JsonDocument.Parse(File.ReadAllBytes(nativePath));
        if (client.ExitCode != 0 || !native.RootElement.GetProperty("passed").GetBoolean())
            throw new InvalidOperationException($"Native {label} acceptance failed. See {nativePath}.");
        selection.Commit();
        var save = Path.Combine(game, "saves", "RuntimeComponentsQa", "level.dat");
        if (!File.Exists(save)) throw new IOException("Native QA world was not saved.");
        var result = new
        {
            mode = label, passed = true, client.ExitCode, elapsedSeconds = watch.Elapsed.TotalSeconds,
            selectedCatalog = selection.Snapshot.Revision, selection.Snapshot.Sha256,
            componentPaths = selection.ArtifactPaths,
            blockedPreparationRequests = blocked.Requests, savedWorldSha256 = Hash(save),
            managedSteamSeededFromSharedVerifiedCache,
            nativeResult = native.RootElement.Clone(),
            actualSteamGuestConnectionVerified = false,
            offlineDefinition = "All launcher preparation HTTP requests rejected; native game uses offline QA identity. Host network unchanged."
        };
        reports.Add(result);
        Write(Path.Combine(evidence, "result.json"), result);
        Console.WriteLine($"NATIVE_ACCEPTANCE_PASS mode={label} seconds={watch.Elapsed.TotalSeconds:F1}");
    }
    foreach (var file in sourceFiles)
        if (Hash(Path.Combine(source, file.Path)) != file.Sha256) throw new IOException("Author source changed during QA: " + file.Path);
    Write(Path.Combine(root, evidenceId.Length == 0 ? "verification.json" : "verification-" + evidenceId + ".json"), new
    {
        passed = true, descriptor, sourceFiles = sourceFiles.Count,
        observerSha256 = Hash(observer), launcherAssemblySha256 = Hash(typeof(PackRuntimeService).Assembly.Location),
        sharedOriginalRuntimeCachesUsed = true, copiedPersonalWorlds = false, copiedPersonalOptions = false,
        runs = reports
    });
}
finally
{
    if (Directory.Exists(fixturePaths.Service)) DeleteOwnedTree(fixturePaths.Service);
    if (Directory.Exists(Path.Combine(root, "temp"))) DeleteOwnedTree(Path.Combine(root, "temp"));
    if (Directory.Exists(Path.Combine(root, "home"))) DeleteOwnedTree(Path.Combine(root, "home"));
}

void CopyAuthorTree(string from, string to)
{
    if ((File.GetAttributes(from) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked author source directory.");
    Directory.CreateDirectory(to);
    foreach (var file in Directory.EnumerateFiles(from))
    {
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked author source file.");
        var target = Path.Combine(to, Path.GetFileName(file));
        File.Copy(file, target, overwrite: false);
        var digest = Hash(file);
        if (Hash(target) != digest) throw new IOException("Independent author copy differs.");
        sourceFiles.Add(new SourceFile(Path.GetRelativePath(source, file), new FileInfo(file).Length, digest));
    }
    foreach (var directory in Directory.EnumerateDirectories(from)) CopyAuthorTree(directory, Path.Combine(to, Path.GetFileName(directory)));
}

bool SeedManagedSteamJar()
{
    var catalog = typeof(ManagedComponentService).Assembly.GetType("Minecraft.SteamTransportCatalog", throwOnError: true)!;
    var selected = catalog.GetMethod("Find", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [descriptor]);
    if (selected is null) return false;
    var fileName = (string)selected.GetType().GetProperty("FileName")!.GetValue(selected)!;
    var digest = (string)selected.GetType().GetProperty("Sha256")!.GetValue(selected)!;
    var destination = Path.Combine(game, "mods", fileName);
    if (File.Exists(destination)) return false;
    var cache = Path.Combine(paths.Launcher, "ManagedComponents", "e4steam");
    if (!Directory.Exists(cache)) return false;
    foreach (var candidate in Directory.EnumerateFiles(cache, fileName, SearchOption.AllDirectories))
    {
        if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) continue;
        if (!Hash(candidate).Equals(digest, StringComparison.OrdinalIgnoreCase)) continue;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(candidate, destination, overwrite: false);
        if (Hash(destination) != Hash(candidate)) throw new IOException("Independent shared Steam component copy differs.");
        return true;
    }
    return false;
}

void DeleteOwnedTree(string directory)
{
    var full = Path.GetFullPath(directory);
    if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Escaped cleanup path.");
    var pending = new Stack<string>();
    pending.Push(full);
    while (pending.TryPop(out var current))
    {
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked QA cleanup path.");
        foreach (var file in Directory.EnumerateFiles(current))
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked QA cleanup file.");
        foreach (var child in Directory.EnumerateDirectories(current)) pending.Push(child);
    }
    Directory.Delete(full, recursive: true);
}

void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, json), new UTF8Encoding(false));
static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
static string JavaArgument(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
    .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
    .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
record SourceFile(string Path, long SizeBytes, string Sha256);

sealed class OfflineHandler : HttpMessageHandler
{
    public int Requests;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Requests);
        throw new HttpRequestException("Network deliberately unavailable during offline acceptance.");
    }
}

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
        try { return Enumerable.Range(1, count - 1).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(argv); }
    }
}
