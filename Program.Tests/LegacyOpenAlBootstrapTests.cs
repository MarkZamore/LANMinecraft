using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Version;

namespace Minecraft.Tests;

public sealed class LegacyOpenAlBootstrapTests : IDisposable
{
    private readonly string root = Path.Combine(RepositoryRoot(), "Program.Tests", "TestResults", "NativeInterop",
        Guid.NewGuid().ToString("N"), "Лаунчер с пробелами");
    private const string DllName = "OpenAL-amd64.dll";
    private const string MainClass = "NativeBootstrapProbe";

    [Fact]
    public void InheritedLegacyProfileUsesItsExactNativeBytesInsideThePortableRoot()
    {
        using var parent = JsonVersion("1.6.4.json");
        using var child = JsonVersion("legacyfabric.json");
        child.ParentVersion = parent;
        var paths = new AppPaths(root);
        var launcher = Launcher(paths);
        var natives = Path.Combine(paths.Runtimes, "Ручная сборка", "natives");
        var expected = PrepareNatives(launcher, child, natives);
        var nativeHash = Hash(File.ReadAllBytes(Path.Combine(natives, DllName)));
        var sourceHashes = NativeArchives(launcher, child).ToDictionary(path => path, path => Hash(File.ReadAllBytes(path)));

        var arguments = LegacyOpenAlBootstrap.Prepare(paths, launcher, child, natives);

        Assert.Equal(3, arguments.Count);
        Assert.Equal("-Dlanminecraft.native.openal=" + Path.Combine(natives, DllName), arguments[1]);
        Assert.Equal("-Dlanminecraft.native.openal.sha256=" + Hash(expected), arguments[2]);
        var agent = arguments[0]["-javaagent:".Length..];
        Assert.StartsWith(Path.Combine(paths.Launcher, "NativeInterop") + Path.DirectorySeparatorChar,
            agent, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(agent));
        Assert.Equal(nativeHash, Hash(File.ReadAllBytes(Path.Combine(natives, DllName))));
        foreach (var pair in sourceHashes) Assert.Equal(pair.Value, Hash(File.ReadAllBytes(pair.Key)));
    }

    [Fact]
    public void ChangedOrEscapingNativeDllCannotUseTheBootstrap()
    {
        var paths = new AppPaths(root);
        var launcher = Launcher(paths);
        using var profile = Profile();
        var natives = Path.Combine(paths.Runtimes, "fixture", "natives");
        PrepareNatives(launcher, profile, natives);
        File.WriteAllBytes(Path.Combine(natives, DllName), "changed DLL"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => LegacyOpenAlBootstrap.Prepare(paths, launcher, profile, natives));
        Assert.Throws<InvalidOperationException>(() => LegacyOpenAlBootstrap.Prepare(paths, launcher, profile,
            Path.Combine(Path.GetDirectoryName(root)!, "outside portable root")));
        Assert.False(Directory.Exists(Path.Combine(paths.Launcher, "NativeInterop")));
    }

    [Fact]
    public void LastEligibleNativeArchiveDeterminesThePreloadedDll()
    {
        var paths = new AppPaths(root);
        var launcher = Launcher(paths);
        using var profile = Profile(extraNatives: true);
        var natives = Path.Combine(paths.Runtimes, "fixture", "natives");
        var expected = PrepareNatives(launcher, profile, natives);
        // A later archive excluded on this OS must not override the bytes
        // selected by the same client/platform rules as CmlLib extraction.
        var blocked = profile.Libraries!.Last();
        var blockedArchive = Path.Combine(launcher.MinecraftPath.Library,
            blocked.GetNativeLibraryPath(launcher.RulesContext.OS)!);
        WriteArchive(blockedArchive, "not selected on Windows"u8.ToArray());

        var arguments = LegacyOpenAlBootstrap.Prepare(paths, launcher, profile, natives);

        Assert.Equal("-Dlanminecraft.native.openal.sha256=" + Hash(expected), arguments[2]);
    }

    [Theory]
    [InlineData("3.3.6", false)]
    [InlineData("2.9.4+legacyfabric.17", true)]
    public void UnsupportedOrMissingNativeRetainsTheNormalLaunch(string lwjglVersion, bool missingNative)
    {
        var paths = new AppPaths(root);
        var launcher = Launcher(paths);
        using var profile = Profile(lwjglVersion);
        var natives = Path.Combine(paths.Runtimes, "fixture", "natives");
        if (!missingNative) PrepareNatives(launcher, profile, natives);

        Assert.Empty(LegacyOpenAlBootstrap.Prepare(paths, launcher, profile, natives));
        Assert.False(Directory.Exists(Path.Combine(paths.Launcher, "NativeInterop")));
    }

    [Fact]
    public void BootstrapArgumentsPreserveUtf8UnicodeAndTheApplicationBoundary()
    {
        var paths = new AppPaths(root);
        var launcher = Launcher(paths);
        using var profile = Profile();
        var natives = Path.Combine(paths.Runtimes, "сборка с пробелами", "natives");
        PrepareNatives(launcher, profile, natives);
        var bootstrap = LegacyOpenAlBootstrap.Prepare(paths, launcher, profile, natives);
        var start = new ProcessStartInfo("java.exe");
        foreach (var argument in new[] { "-Dfile.encoding=UTF-8", "-Dauthor.option=сохранено", "-cp", "игровой путь",
            MainClass, "--gameDir", "мир игрока", "-Dfile.encoding=application-only" }) start.ArgumentList.Add(argument);

        var final = MinecraftProcessService.CompleteJvmArguments(start, profile, root, launcherArguments: bootstrap);
        var boundary = final.ToList().IndexOf(MainClass);

        Assert.Equal("-Dfile.encoding=UTF-8", Assert.Single(final.Take(boundary), value => value.StartsWith("-Dfile.encoding=", StringComparison.Ordinal)));
        Assert.Contains("-Dauthor.option=сохранено", final.Take(boundary));
        foreach (var argument in bootstrap) Assert.Contains(argument, final.Take(boundary));
        Assert.Equal(new[] { "--gameDir", "мир игрока", "-Dfile.encoding=application-only" }, final.Skip(boundary + 1));
        Assert.Equal(final, start.ArgumentList);
    }

    [Fact]
    public void EmbeddedBootstrapUsesJava8WithoutGameOrLoaderDependencies()
    {
        using var input = AgentResource();
        using var jar = new ZipArchive(input);
        using (var manifest = new StreamReader(jar.GetEntry("META-INF/MANIFEST.MF")!.Open()))
            Assert.Contains("Premain-Class: minecraft.portable.nativeinterop.PortableNativeInteropAgent",
                manifest.ReadToEnd().Replace("\r\n ", "", StringComparison.Ordinal).Replace("\n ", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain(jar.Entries, entry => entry.FullName.StartsWith("org/", StringComparison.Ordinal) ||
            entry.FullName.StartsWith("net/minecraft/", StringComparison.Ordinal));
        Assert.Contains(jar.Entries, entry => entry.FullName == "minecraft/portable/nativeinterop/PortableNativeInteropAgent.class");
        foreach (var entry in jar.Entries.Where(entry => entry.FullName.EndsWith(".class", StringComparison.Ordinal)))
        {
            using var body = entry.Open();
            var header = new byte[8];
            body.ReadExactly(header);
            Assert.Equal(new byte[] { 0xca, 0xfe, 0xba, 0xbe }, header[..4]);
            Assert.True((header[6] << 8 | header[7]) <= 52, entry.FullName + " requires Java newer than 8");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedPreloadCannotSuppressMainOrChangeItsEncoding(bool missingDll)
    {
        // This case verifies rejection and continuation, not javac's command-line
        // conversion through the Windows ANSI code page. Keep its tool paths
        // representable on English CI; Unicode paths remain covered above and
        // by the real-runtime audio probe.
        var jvmRoot = Path.Combine(Path.GetDirectoryName(root)!, "jvm");
        Directory.CreateDirectory(jvmRoot);
        var agent = Path.Combine(jvmRoot, "native-interop-agent.jar");
        await using (var output = File.Create(agent))
        await using (var input = AgentResource()) await input.CopyToAsync(output);
        var dll = Path.Combine(jvmRoot, DllName);
        if (!missingDll) await File.WriteAllBytesAsync(dll, "not a native library"u8.ToArray());
        var source = Path.Combine(jvmRoot, MainClass + ".java");
        await File.WriteAllTextAsync(source, """
            import java.nio.charset.Charset;
            public final class NativeBootstrapProbe {
                public static void main(String[] args) {
                    if (!"UTF-8".equals(Charset.defaultCharset().name())) throw new AssertionError("Default charset changed");
                    if (!"UTF-8".equals(System.getProperty("file.encoding"))) throw new AssertionError("Encoding property changed");
                    System.out.println("MAIN_REACHED_UTF8");
                }
            }
            """);
        await Run(jvmRoot, "javac", "--release", "8", "-encoding", "UTF-8", "-d", jvmRoot, source);

        var outputText = await Run(jvmRoot, "java", "-Xverify:all", "-Dfile.encoding=UTF-8", "-javaagent:" + agent,
            "-Dlanminecraft.native.openal=" + dll, "-Dlanminecraft.native.openal.sha256=" + new string('0', 64),
            "-cp", jvmRoot, MainClass);

        Assert.Contains("MAIN_REACHED_UTF8", outputText, StringComparison.Ordinal);
        Assert.Contains("[LauncherNative]", outputText, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAL preload succeeded", outputText, StringComparison.Ordinal);
    }

    private static JsonVersion JsonVersion(string fixture) => new(JsonDocument.Parse(File.ReadAllText(
        Path.Combine(RepositoryRoot(), "Program.Tests", "Fixtures", "LegacyLaunch", fixture))), new());

    private static JsonVersion Profile(string lwjgl = "2.9.4+legacyfabric.17", bool extraNatives = false)
    {
        var libraries = new List<object>
        {
            new { name = (lwjgl.StartsWith("2.", StringComparison.Ordinal) ? "org.lwjgl.lwjgl:lwjgl:" : "org.lwjgl:lwjgl:") + lwjgl },
            new { name = "fixture:native-a:1", natives = new { windows = "natives-windows" } }
        };
        if (extraNatives)
        {
            libraries.Add(new { name = "fixture:native-b:1", natives = new { windows = "natives-windows" } });
            libraries.Add(new { name = "fixture:native-blocked:1", natives = new { windows = "natives-windows" },
                rules = new[] { new { action = "disallow", os = new { name = "windows" } } } });
        }
        return new(JsonDocument.Parse(JsonSerializer.Serialize(new { id = "native-interop-fixture", mainClass = MainClass, libraries })), new());
    }

    private static MinecraftLauncher Launcher(AppPaths paths)
    {
        Directory.CreateDirectory(paths.Root);
        return new(new MinecraftPath(Path.Combine(paths.Launcher, "fixture-runtime")));
    }

    private static IEnumerable<string> NativeArchives(MinecraftLauncher launcher, IVersion profile) =>
        profile.ConcatInheritedCollection(version => version.Libraries)
            .Where(library => library.IsClientRequired && (library.Rules is null || launcher.RulesEvaluator.Match(library.Rules, launcher.RulesContext)))
            .Select(library => library.GetNativeLibraryPath(launcher.RulesContext.OS))
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => Path.Combine(launcher.MinecraftPath.Library, path!));

    private static byte[] PrepareNatives(MinecraftLauncher launcher, IVersion profile, string natives)
    {
        Directory.CreateDirectory(natives);
        byte[] selected = [];
        var index = 0;
        foreach (var archive in NativeArchives(launcher, profile))
        {
            selected = Encoding.UTF8.GetBytes("fixture native bytes " + ++index);
            WriteArchive(archive, selected);
        }
        Assert.NotEmpty(selected);
        File.WriteAllBytes(Path.Combine(natives, DllName), selected);
        return selected;
    }

    private static void WriteArchive(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var output = archive.CreateEntry(DllName).Open();
        output.Write(bytes);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static Stream AgentResource() => typeof(MinecraftProcessService).Assembly.GetManifestResourceStream("Minecraft.NativeInteropAgent.jar")
        ?? throw new FileNotFoundException("The launcher must embed its native interop agent.");

    private static async Task<string> Run(string jvmRoot, string tool, params string[] arguments)
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(home) ? tool + ".exe" : Path.Combine(home, "bin", tool + ".exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = jvmRoot,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        var temporary = Path.Combine(jvmRoot, "Temp");
        var profile = Path.Combine(jvmRoot, "Home");
        Directory.CreateDirectory(temporary);
        Directory.CreateDirectory(profile);
        PortableJavaEnvironment.ConfigureEnvironment(start.Environment, temporary, profile);
        var prefix = tool == "javac" ? "-J" : "";
        start.ArgumentList.Add(prefix + "-Djava.io.tmpdir=" + temporary);
        start.ArgumentList.Add(prefix + "-Duser.home=" + profile);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var text = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, text);
        return text;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Program", "Minecraft.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Launcher checkout was not found.");
    }

    public void Dispose() => TempTree.Delete(Path.GetDirectoryName(root)!);
}
