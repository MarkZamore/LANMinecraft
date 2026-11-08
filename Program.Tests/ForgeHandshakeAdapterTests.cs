using System.Diagnostics;
using System.IO.Compression;
using Minecraft;

namespace Minecraft.Tests;

public sealed class ForgeHandshakeAdapterTests
{
    [Fact]
    public async Task ConcurrentAcknowledgement_PreservesPendingMessagesAndRejectsUnsupportedBytecode()
    {
        var repository = FindRepository();
        var common = Path.Combine(repository, "Program", "IdentityAdapters", "Common");
        var root = Path.Combine(Path.GetTempPath(), "forge-handshake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cp = string.Join(Path.PathSeparator, Path.Combine(common, "lib", "asm-9.8.jar"),
                Path.Combine(common, "lib", "asm-tree-9.8.jar"));
            await Run("javac", "--release", "17", "-encoding", "UTF-8", "-cp", cp, "-d", root,
                Path.Combine(common, "PortableForgeHandshakeTransformer.java"),
                Path.Combine(repository, "Program.Tests", "Fixtures", "ForgeHandshake", "ForgeHandshakeProbe.java"));
            var output = await Run("java", "-Xmx128m", "-Xverify:all", "-cp", root + Path.PathSeparator + cp,
                "ForgeHandshakeProbe");
            Assert.Contains("PASS", output, StringComparison.Ordinal);
        }
        finally { TempTree.Delete(root); }
    }

    [Theory]
    [InlineData(PackLoaderKind.Forge, true, true)]
    [InlineData(PackLoaderKind.Forge, false, false)]
    [InlineData(PackLoaderKind.NeoForge, true, false)]
    [InlineData(PackLoaderKind.Fabric, true, false)]
    public void Selection_UsesOnlyTheSelectedForgeLoader(PackLoaderKind loader, bool containsClass, bool enabled)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-handshake-mapping-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            var libs = Path.Combine(paths.Launcher, "Shared", "libraries");
            var jar = Path.Combine(libs, "net", "minecraftforge", "forge", "1.20.1-47.4.0",
                "forge-1.20.1-47.4.0-universal.jar");
            WriteJar(jar, containsClass ? ForgeHandshakeMappingService.TargetClass : "other/Platform");
            // Neither another loader version nor a content mod can supply the correction.
            WriteJar(jar.Replace("47.4.0", "47.4.1"), ForgeHandshakeMappingService.TargetClass);
            WriteJar(Path.Combine(paths.Personal, "Instances", "Example", "mods", "pretend-forge.jar"),
                ForgeHandshakeMappingService.TargetClass);
            var runtime = new PreparedRuntime(paths.Launcher, "example", "java.exe", "client.jar",
                new(1, "1.20.1", new(loader, "47.4.0"), "", "descriptor")) { LibrariesRoot = libs };
            var result = ForgeHandshakeMappingService.Build(runtime, paths);
            Assert.Equal(enabled, result is not null);
            if (enabled)
            {
                Assert.Equal("true", result!.Properties["forgeHandshakeSyncEnabled"]);
                var target = Assert.Single(result.Targets);
                Assert.Equal(jar, target.JarPath);
                Assert.Equal(ForgeHandshakeMappingService.TargetClass, target.ClassName);
            }
        }
        finally { TempTree.Delete(root); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndependentLoaderProof_NeverEnablesIdentityOrAcceptsAFailedProof(bool proofPasses)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-independent-proof-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            var libs = Path.Combine(paths.Launcher, "Shared", "libraries");
            WriteJar(Path.Combine(libs, "net", "minecraftforge", "forge", "1.20.1-47.4.0",
                "forge-1.20.1-47.4.0-universal.jar"), ForgeHandshakeMappingService.TargetClass);
            var runtime = new PreparedRuntime(paths.Launcher, "example", "java.exe", "client.jar",
                new(1, "1.20.1", new(PackLoaderKind.Forge, "47.4.0"), "", "descriptor")) { LibrariesRoot = libs };
            var properties = new Dictionary<string, string> { ["enabled"] = "false", ["identityHooksEnabled"] = "false" };
            var targets = new List<IdentityAdapterTarget>();
            var calls = 0; var warnings = new List<string>();
            var mapping = await PortableIdentityAdapterService.AddVerifiedForgeHandshakeAsync(runtime, paths,
                properties, targets, candidate =>
                {
                    calls++;
                    Assert.Equal("false", properties["forgeHandshakeSyncEnabled"]);
                    Assert.Empty(targets);
                    Assert.Equal(ForgeHandshakeMappingService.TargetClass, Assert.Single(candidate.Targets).ClassName);
                    return proofPasses ? Task.CompletedTask : Task.FromException(new InvalidOperationException("native shape rejected"));
                }, warnings.Add);
            Assert.Equal(1, calls);
            Assert.Equal("false", properties["enabled"]);
            Assert.Equal("false", properties["identityHooksEnabled"]);
            Assert.Equal(proofPasses ? "true" : "false", properties["forgeHandshakeSyncEnabled"]);
            Assert.Equal(proofPasses ? 1 : 0, targets.Count);
            Assert.Equal(proofPasses, mapping is not null);
            Assert.Equal(proofPasses ? 0 : 1, warnings.Count);
        }
        finally { TempTree.Delete(root); }
    }

    private static void WriteJar(string path, string name)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry(name + ".class").Open());
        writer.Write("mapping fixture only; bytecode verification is a separate mandatory step");
    }
    private static async Task<string> Run(string tool, params string[] args)
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(home) ? tool + ".exe" : Path.Combine(home, "bin", tool + ".exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, output); return output;
    }
    private static string FindRepository()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Program.Tests", "Fixtures", "ForgeHandshake", "ForgeHandshakeProbe.java"))) return d.FullName;
        throw new DirectoryNotFoundException("Forge handshake probe source not found.");
    }
}
