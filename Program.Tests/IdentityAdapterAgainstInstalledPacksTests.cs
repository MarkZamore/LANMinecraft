using System.Diagnostics;
using System.Text.Json;
using Minecraft;
using Xunit.Abstractions;

namespace Minecraft.Tests;

/// <summary>
/// The bytecode preflight, run against the game the launcher actually installed.
///
/// Every other test here reads mappings this file wrote itself, which proves the
/// builder does what it was told and nothing about whether what it was told is
/// true of Minecraft. Twice in one evening it was not: PlayerChunkSender was
/// looked for in server.level when it lives in server.network, and the class an
/// inherited call names was taken to be the one that declares the method rather
/// than the one it was called on. Both passed every unit test. Both would have
/// cost a pack its whole adapter - skins, names and all - because a target the
/// transformer does not change fails the preflight, and a failed preflight
/// starts the pack with no hooks.
///
/// So this derives the aliases with today's code, hands them to the real
/// preflight, and points it at the real jars. It needs a machine with the packs
/// installed and a JDK; where either is missing it has nothing to say and says
/// so rather than failing. That is deliberate - it means CI, which has neither,
/// stays green while a developer, who has both, gets the check.
/// </summary>
public sealed class IdentityAdapterAgainstInstalledPacksTests(ITestOutputHelper output)
{
    /// <summary>
    /// The launcher's data folder: the one holding Launcher and Packs. Named by
    /// the environment where it is not on the desktop.
    /// </summary>
    private static string? InstallRoot
    {
        get
        {
            foreach (var candidate in new[]
                     {
                         Environment.GetEnvironmentVariable("LANMINECRAFT_ROOT"),
                         Path.Combine(
                             Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                             "Minecraft")
                     })
            {
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    Directory.Exists(Path.Combine(candidate, "Launcher", "Runtimes")))
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    [Fact]
    public async Task EveryInstalledPack_SurvivesTheRealPreflight()
    {
        var install = InstallRoot;
        var java = FindJava(install);
        var adapter = FindUpwards("Program", "Build", "IdentityAdapters", "Minecraft-1.21.1-NeoForge",
            "portable-identity-adapter.jar");
        if (install is null || java is null || adapter is null)
        {
            // Nothing installed, no JDK, or the adapter was not built. None of
            // the three is a fault; there is simply nothing here to check.
            return;
        }

        var failures = new List<string>();
        var checks = new List<Func<Task>>();
        foreach (var pack in Directory.EnumerateDirectories(Path.Combine(install, "Launcher", "Runtimes")))
        {
            var configuration = Describe(install, pack);
            if (configuration is null) continue;
            foreach (var target in configuration.Targets)
            {
                var jarPath = target.JarPath;
                var className = target.ClassName;
                var properties = configuration.Properties;
                var name = Path.GetFileName(pack);
                checks.Add(async () =>
                {
                    var complaint = await PreflightAsync(java, adapter, properties, jarPath, className);
                    if (complaint is null) return;
                    lock (failures) failures.Add($"{name} :: {className}: {complaint}");
                });
            }
        }

        output.WriteLine($"Real bytecode preflight targets: {checks.Count}");
        if (checks.Count == 0)
        {
            // An explicit local audit must not silently pass because the
            // installed runtime layout changed and no jar was found.
            Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LANMINECRAFT_ROOT")),
                "LANMINECRAFT_ROOT was specified, but no installed pack supplied a preflight target.");
            return;
        }
        await RunAsync(checks, atOnce: 8);

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>What the launcher would derive for this pack, or null if it cannot be described.</summary>
    private static IdentityAdapterConfiguration? Describe(string install, string runtimeRoot)
    {
        var name = Path.GetFileName(runtimeRoot);
        var manifest = Path.Combine(install, "Packs", name, "portable-pack.json");
        if (!File.Exists(manifest)) return null;

        PackRuntimeDescriptor descriptor;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = document.RootElement;
            var loader = root.GetProperty("loader");
            descriptor = new PackRuntimeDescriptor(
                1,
                root.GetProperty("minecraftVersion").GetString() ?? "",
                new PackLoaderDescriptor(
                    Enum.Parse<PackLoaderKind>(loader.GetProperty("type").GetString() ?? "", ignoreCase: true),
                    loader.GetProperty("version").GetString() ?? ""),
                root.TryGetProperty("clientJar", out var jar) ? jar.GetString() ?? "" : "",
                name);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or ArgumentException)
        {
            return null;
        }

        // InstallRoot names the Minecraft data directory; AppPaths takes the
        // directory containing Minecraft and the launcher executable.
        var paths = new AppPaths(Directory.GetParent(install)!.FullName);
        var sharedLibraries = SharedRuntimeStore.Libraries(paths);
        var shared = Directory.Exists(sharedLibraries);
        var libraries = shared ? sharedLibraries : Path.Combine(runtimeRoot, "libraries");
        var versions = shared ? SharedRuntimeStore.Versions(paths) : Path.Combine(runtimeRoot, "versions");
        var clientJar = FindClientJar(libraries, versions, descriptor.MinecraftVersion);
        if (clientJar is null) return null;

        try
        {
            return new IdentityAdapterMappingService(paths).Build(
                new PreparedRuntime(runtimeRoot, "profile", "java", clientJar, descriptor)
                {
                    LibrariesRoot = libraries
                },
                Path.Combine(install, "Personal", "Instances", name));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException)
        {
            // A runtime the launcher would start without hooks is not a
            // failure here either; it is the same answer the launcher gives.
            return null;
        }
    }

    /// <summary>
    /// The jar the loader remapped, which is where the mappings are read
    /// against: Forge and NeoForge leave an -srg jar beside the libraries, and
    /// Fabric runs the vanilla one under versions.
    /// </summary>
    private static string? FindClientJar(string librariesRoot, string versionsRoot, string minecraftVersion)
    {
        var vanilla = Path.Combine(versionsRoot, minecraftVersion, minecraftVersion + ".jar");
        if (File.Exists(vanilla)) return vanilla;
        var libraries = Path.Combine(librariesRoot, "net", "minecraft", "client");
        if (Directory.Exists(libraries))
        {
            var srg = Directory.EnumerateFiles(libraries, "client-*-srg.jar", SearchOption.AllDirectories)
                .Where(path => IdentityAdapterMappingService.IsFiledUnderVersion(path, minecraftVersion))
                .OrderBy(path => path, StringComparer.Ordinal).LastOrDefault();
            if (srg is not null) return srg;
        }

        return null;
    }

    [Fact]
    public void SharedStorePreflight_UsesOnlyTheRequestedMinecraft()
    {
        var root = Path.Combine(Path.GetTempPath(), "identity-preflight-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var libraries = Path.Combine(root, "Launcher", "Shared", "libraries");
            var versions = Path.Combine(root, "Launcher", "Shared", "versions");
            var expected = Path.Combine(libraries, "net", "minecraft", "client", "1.20.1-20230612.114412",
                "client-1.20.1-20230612.114412-srg.jar");
            var other = Path.Combine(libraries, "net", "minecraft", "client", "1.21.1-20240808.144430",
                "client-1.21.1-20240808.144430-srg.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
            Directory.CreateDirectory(Path.GetDirectoryName(other)!);
            File.WriteAllBytes(expected, []);
            File.WriteAllBytes(other, []);

            Assert.Equal(expected, FindClientJar(libraries, versions, "1.20.1"));
            Assert.Null(FindClientJar(libraries, versions, "1.18.2"));

            var vanilla = Path.Combine(versions, "1.18.2", "1.18.2.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(vanilla)!);
            File.WriteAllBytes(vanilla, []);
            Assert.Equal(vanilla, FindClientJar(libraries, versions, "1.18.2"));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    /// <summary>The preflight's complaint about one class, or null if it passed.</summary>
    private static async Task<string?> PreflightAsync(
        string java,
        string adapter,
        IReadOnlyDictionary<string, string> properties,
        string jarPath,
        string className)
    {
        var start = new ProcessStartInfo(java)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var (name, value) in properties)
        {
            start.ArgumentList.Add($"-Dminecraft.portable.identity.{name}={value}");
        }
        start.ArgumentList.Add("-cp");
        start.ArgumentList.Add(adapter);
        start.ArgumentList.Add("minecraft.portable.identity.PortableIdentityPreflight");
        start.ArgumentList.Add(jarPath);
        start.ArgumentList.Add(className);

        using var process = Process.Start(start);
        if (process is null) return "the preflight could not be started";
        var error = await process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode == 0) return null;

        var first = error.Split('\n').FirstOrDefault(line => line.Contains("Exception", StringComparison.Ordinal));
        return (first ?? error).Trim();
    }

    private static async Task RunAsync(List<Func<Task>> work, int atOnce)
    {
        using var room = new SemaphoreSlim(atOnce);
        await Task.WhenAll(work.Select(async one =>
        {
            await room.WaitAsync();
            try
            {
                await one();
            }
            finally
            {
                room.Release();
            }
        }));
    }

    /// <summary>A java that can load the adapter: the one asked for, else the launcher's own.</summary>
    private static string? FindJava(string? install)
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            var named = Path.Combine(home, "bin", "java.exe");
            if (File.Exists(named)) return named;
        }

        if (install is null) return null;
        var runtimes = Path.Combine(install, "Launcher", "JavaRuntimes", "runtime", "windows-x64");
        if (!Directory.Exists(runtimes)) return null;
        // The newest the launcher installed: the adapter is built for the
        // oldest Java a pack runs on, so any of them can load it.
        return Directory.EnumerateDirectories(runtimes)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Path.Combine(path, "bin", "java.exe"))
            .LastOrDefault(File.Exists);
    }

    private static string? FindUpwards(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        return null;
    }
}
