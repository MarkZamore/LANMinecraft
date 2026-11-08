using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.VersionLoader;
using CmlLib.Core.Version;
using Minecraft;

if (args.Length != 5) throw new ArgumentException("<repository> <output> <runtime-root> <java-home> <javac-home>");
var root = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var runtimeRoot = Path.GetFullPath(args[2]);
var javaHome = Path.GetFullPath(args[3]);
var javacHome = Path.GetFullPath(args[4]);
var allowed = Path.Combine(root, "artifacts", "agent-runs") + Path.DirectorySeparatorChar;
if (!output.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Output must be an agent-run child.");
var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(runtimeRoot, ".portable-runtime.json")));
var profileId = state.RootElement.GetProperty("profileId").GetString()!;
var paths = new AppPaths(root);
var shared = Path.Combine(paths.Launcher, "Shared");
var stage = Path.Combine(output, "stage");
Directory.CreateDirectory(stage);
var assembly = typeof(AppPaths).Assembly;
var prepare = assembly.GetType("Minecraft.LegacyOpenAlBootstrap", true)!.GetMethod("Prepare", BindingFlags.NonPublic | BindingFlags.Static)!;
var build = assembly.GetType("Minecraft.MinecraftLaunchArguments", true)!.GetMethod("BuildProcess", BindingFlags.NonPublic | BindingFlags.Static)!;
var complete = typeof(MinecraftProcessService).GetMethod("CompleteJvmArguments", BindingFlags.NonPublic | BindingFlags.Static)!;
var results = new List<object>();
var failed = false;
var sourceHashes = new Dictionary<string, string>();
string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
var metadataPath = new MinecraftPath(runtimeRoot) { Library = Path.Combine(shared, "libraries"), Versions = Path.Combine(shared, "versions") };
var metadataParameters = MinecraftLauncherParameters.CreateDefault(metadataPath);
metadataParameters.VersionLoader = new LocalJsonVersionLoader(metadataPath);
var metadataLauncher = new MinecraftLauncher(metadataParameters);
var metadataProfile = await metadataLauncher.GetVersionAsync(profileId);
var libraries = metadataProfile.ConcatInheritedCollection(version => version.Libraries)
    .Where(library => library.IsClientRequired && (library.Rules is null || metadataLauncher.RulesEvaluator.Match(library.Rules, metadataLauncher.RulesContext))).ToArray();
var coordinate = libraries.First(library => library.Name.StartsWith("org.lwjgl.lwjgl:lwjgl:2.", StringComparison.Ordinal)).Name.Split(':');
var lwjgl = Path.Combine(metadataPath.Library, coordinate[0].Replace('.', Path.DirectorySeparatorChar), coordinate[1], coordinate[2], coordinate[1] + "-" + coordinate[2] + ".jar");
var nativeJars = libraries.Select(library => library.GetNativeLibraryPath(metadataLauncher.RulesContext.OS))
    .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.Combine(metadataPath.Library, path!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
var clientJar = Path.Combine(paths.Launcher, state.RootElement.GetProperty("clientJarRelativePath").GetString()!);
foreach (var pin in nativeJars.Concat(new[] { lwjgl, clientJar, Path.Combine(javaHome, "bin", "java.exe") })) sourceHashes[Path.GetRelativePath(root, pin)] = Hash(pin);

try
{
    var classes = Path.Combine(stage, "classes"); Directory.CreateDirectory(classes);
    var compilation = new ProcessStartInfo(Path.Combine(javacHome, "bin", "javac.exe"));
    foreach (var arg in new[] { "--release", "8", "-encoding", "UTF-8", "-cp", lwjgl, "-d", classes,
        Path.Combine(root, "tools", "NativeInteropProbe", "NativeAudioProbe.java") }) compilation.ArgumentList.Add(arg);
    var compile = await Run(compilation, stage);
    if (compile.ExitCode != 0) throw new InvalidOperationException(compile.Output);

    var cases = new[] {
        (Name: "ascii-control", Folder: "ascii", Agent: false, Mode: "direct"),
        (Name: "ascii-fixed", Folder: "ascii", Agent: true, Mode: "direct"),
        (Name: "unicode-baseline", Folder: "Майнкруфт с пробелами", Agent: false, Mode: "direct"),
        (Name: "unicode-fixed", Folder: "Майнкруфт с пробелами", Agent: true, Mode: "direct"),
        (Name: "unicode-isolated", Folder: "Майнкруфт с пробелами", Agent: true, Mode: "isolated"),
        (Name: "unicode-long", Folder: Path.Combine("Длинный путь", new string('a', 28)), Agent: true, Mode: "isolated"),
        (Name: "windows-path-limit", Folder: Path.Combine("Длинный путь", new string('a', 70)), Agent: true, Mode: "isolated"),
        (Name: "unicode-knot", Folder: "Настоящий Knot с пробелами", Agent: true, Mode: "knot")
    };
    foreach (var item in cases)
    {
        var caseRoot = Path.Combine(stage, item.Folder);
        var gamePath = new MinecraftPath(caseRoot) {
            Library = Path.Combine(shared, "libraries"), Versions = Path.Combine(shared, "versions"),
            Assets = Path.Combine(shared, "assets"), Resource = Path.Combine(shared, "resources"), Runtime = Path.Combine(shared, "runtime")
        };
        var parameters = MinecraftLauncherParameters.CreateDefault(gamePath);
        parameters.VersionLoader = new LocalJsonVersionLoader(gamePath);
        var launcher = new MinecraftLauncher(parameters);
        var profile = await launcher.GetVersionAsync(profileId);
        // CmlLib derives its extraction path from Versions, not BasePath. Use
        // an isolated extraction-only Versions root while retaining the real
        // profile/client/classpath in the normal shared, immutable store.
        var extractionPath = new MinecraftPath(caseRoot) { Library = gamePath.Library, Versions = Path.Combine(caseRoot, "versions") };
        var nativesDirectory = launcher.NativeLibraryExtractor.Extract(extractionPath, profile, launcher.RulesContext);
        var temp = Path.Combine(caseRoot, "temp"); var home = Path.Combine(caseRoot, "home");
        Directory.CreateDirectory(temp); Directory.CreateDirectory(Path.Combine(home, "AppData", "Roaming")); Directory.CreateDirectory(Path.Combine(home, "AppData", "Local"));
        var options = new MLaunchOption {
            Path = gamePath, JavaPath = Path.Combine(javaHome, "bin", "java.exe"), Session = MSession.CreateOfflineSession("AudioProbe"),
            NativesDirectory = nativesDirectory,
            MaximumRamMb = 512, MinimumRamMb = 64,
            ExtraJvmArguments = new[] { "-Dfile.encoding=UTF-8", "-XX:-UsePerfData", "-Djava.io.tmpdir=" + temp, "-Duser.home=" + home,
                "-Dorg.lwjgl.util.Debug=true", "-Dlanminecraft.probe.mode=" + item.Mode, "-Dlanminecraft.probe.classes=" + classes }
                .Select(value => new MArgument(value)).ToArray()
        };
        using var candidate = (Process) build.Invoke(null, new object[] { launcher, profile, options })!;
        var launcherArgs = item.Agent ? (IReadOnlyList<string>) prepare.Invoke(null, new object?[] { paths, launcher, profile, options.NativesDirectory })! : Array.Empty<string>();
        var final = ((IReadOnlyList<string>) complete.Invoke(null, new object?[] { candidate.StartInfo, profile, caseRoot, null, launcherArgs })!).ToList();
        var entrypoint = final.IndexOf(profile.GetInheritedProperty(v => v.MainClass)!);
        if (entrypoint < 0) throw new InvalidOperationException("Cml main boundary not found.");
        final[entrypoint] = "NativeAudioProbe";
        var cp = final.IndexOf("-cp"); if (cp < 0) cp = final.IndexOf("-classpath");
        if (cp < 0) throw new InvalidOperationException("Cml classpath missing.");
        final[cp + 1] = classes + Path.PathSeparator + final[cp + 1];
        candidate.StartInfo.Arguments = ""; candidate.StartInfo.ArgumentList.Clear();
        foreach (var arg in final) candidate.StartInfo.ArgumentList.Add(arg);
        foreach (var (key, value) in new Dictionary<string, string> { ["TEMP"] = temp, ["TMP"] = temp, ["USERPROFILE"] = home,
            ["HOME"] = home, ["APPDATA"] = Path.Combine(home, "AppData", "Roaming"), ["LOCALAPPDATA"] = Path.Combine(home, "AppData", "Local") }) candidate.StartInfo.Environment[key] = value;
        var run = await Run(candidate.StartInfo, stage);
        File.WriteAllText(Path.Combine(output, item.Name + ".log"), run.Output, new UTF8Encoding(false));
        var dll = Path.Combine(options.NativesDirectory!, "OpenAL-amd64.dll");
        var passed = run.ExitCode == 0 && run.Output.Contains("PCM.advance=PASS", StringComparison.Ordinal);
        var pathLimit = item.Name == "windows-path-limit" && !passed && run.Output.Contains("UnsatisfiedLinkError", StringComparison.Ordinal) &&
            (run.Output.Contains("слишком большую длину", StringComparison.Ordinal) || run.Output.Contains("filename or extension is too long", StringComparison.OrdinalIgnoreCase));
        var expected = item.Name == "unicode-baseline" ? !passed && run.Output.Contains("Could not locate OpenAL library", StringComparison.Ordinal) :
            item.Name == "windows-path-limit" ? pathLimit : passed;
        failed |= !expected;
        results.Add(new { name = item.Name, exitCode = run.ExitCode, matchedExpected = expected, pcmPassed = passed,
            mode = item.Mode, agent = item.Agent, nativePathCharacters = dll.Length, openAlSha256 = Hash(dll), utf8 = run.Output.Contains("Charset.defaultCharset=UTF-8", StringComparison.Ordinal),
            knownWindowsPathLimit = pathLimit,
            agentSha256 = item.Agent ? Hash(launcherArgs.Single(value => value.StartsWith("-javaagent:", StringComparison.Ordinal))[11..]) : null,
            knotInitialized = run.Output.Contains("Knot.init=PASS", StringComparison.Ordinal), actualCmlProfile = profileId });
        Console.WriteLine(item.Name + ": " + (expected ? "PASS" : "FAIL") + " (exit " + run.ExitCode + ")");
    }
}
finally
{
    foreach (var pin in sourceHashes) if (Hash(Path.Combine(root, pin.Key)) != pin.Value) throw new InvalidDataException("Source pin changed: " + pin.Key);
    File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new { profileId, windowsACP = NativeMethods.GetACP(), sourceHashes, cases = results,
        audibleOutput = false, gameMainInvoked = false, personalWorldsCopied = false }, new JsonSerializerOptions { WriteIndented = true }));
    if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
}
Environment.ExitCode = failed ? 1 : 0;

static async Task<(int ExitCode, string Output)> Run(ProcessStartInfo start, string directory)
{
    start.WorkingDirectory = directory; start.UseShellExecute = false; start.CreateNoWindow = true;
    start.RedirectStandardOutput = true; start.RedirectStandardError = true;
    start.StandardOutputEncoding = Encoding.UTF8; start.StandardErrorEncoding = Encoding.UTF8;
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Process did not start.");
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
    return (process.ExitCode, await stdout + "\n" + await stderr);
}

static class NativeMethods
{
    [DllImport("kernel32.dll")]
    internal static extern uint GetACP();
}
