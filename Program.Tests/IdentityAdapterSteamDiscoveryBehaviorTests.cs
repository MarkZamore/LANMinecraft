using System.Diagnostics;

namespace Minecraft.Tests;

public sealed class IdentityAdapterSteamDiscoveryBehaviorTests
{
    [Fact]
    public async Task NativeDiscovery_UsesLauncherWorldMetadataAndScopesUdpFiltering()
    {
        var repository = FindRepository();
        var common = Path.Combine(repository, "Program", "IdentityAdapters", "Common");
        var fixture = Path.Combine(repository, "Program.Tests", "Fixtures", "SteamDiscoveryBehavior");
        var root = Path.Combine(Path.GetTempPath(), "steam-discovery-behavior-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var classPath = string.Join(Path.PathSeparator, Path.Combine(common, "lib", "asm-9.8.jar"),
                Path.Combine(common, "lib", "asm-tree-9.8.jar"));
            var arguments = new List<string> { "--release", "17", "-encoding", "UTF-8", "-cp", classPath, "-d", root,
                Path.Combine(common, "PortableSteamDiscoveryHooks.java"), Path.Combine(common, "PortableSteamDiscoveryTransformer.java"),
                Path.Combine(common, "PortableSteamLanEntryTransformer.java") };
            arguments.AddRange(Directory.EnumerateFiles(fixture, "*.java").Order(StringComparer.Ordinal));
            await Run("javac", arguments.ToArray());
            File.Copy(Path.Combine(fixture, "e4steam-version.properties"), Path.Combine(root, "e4steam-version.properties"));
            var output = await Run("java", "-Xverify:all", "-Dfile.encoding=UTF-8", "-cp", root + Path.PathSeparator + classPath,
                "SteamDiscoveryBehaviorProbe");
            Assert.Contains("PASS: actual discovery hooks list one Steam world", output, StringComparison.Ordinal);
            var adapter = Path.Combine(root, "native-preflight-adapter.jar");
            await using (var resource = typeof(PortableIdentityAdapterService).Assembly
                .GetManifestResourceStream("Minecraft.PortableIdentityAdapter.jar")
                ?? throw new InvalidOperationException("Build the actual Java adapter before native preflight behavior tests."))
            await using (var target = File.Create(adapter)) await resource.CopyToAsync(target);
            var hostOutput = await Run("java", "-Djava.io.tmpdir=" + root,
                "-cp", root + Path.PathSeparator + classPath + Path.PathSeparator + adapter,
                "SteamHostDescriptionPreflightProbe");
            Assert.Contains("PASS: host publication preflight independently rejects each missing metadata getter", hostOutput, StringComparison.Ordinal);
        }
        finally { TempTree.Delete(root); }
    }

    private static async Task<string> Run(string tool, params string[] arguments)
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(javaHome)
            ? tool + ".exe" : Path.Combine(javaHome, "bin", tool + ".exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, output);
        return output;
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "Program.Tests", "Fixtures", "SteamDiscoveryBehavior"))) return directory.FullName;
        throw new DirectoryNotFoundException("The actual Steam discovery behavior fixture source was not found.");
    }
}
