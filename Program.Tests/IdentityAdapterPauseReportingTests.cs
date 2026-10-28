using System.Diagnostics;
using System.IO;

namespace Minecraft.Tests;

public sealed class IdentityAdapterPauseReportingTests
{
    [Fact]
    public async Task Native_pause_slot_uses_reporting_only_for_a_published_integrated_world()
    {
        var common = Path.GetDirectoryName(RepositoryFile(
            "Program", "IdentityAdapters", "Common", "PortableLanAutoPublishHooks.java"))!;
        var root = Path.Combine(Path.GetTempPath(), $"minecraft-pause-reporting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var classPath = string.Join(Path.PathSeparator,
                Path.Combine(common, "lib", "asm-9.8.jar"),
                Path.Combine(common, "lib", "asm-tree-9.8.jar"));
            await Run("javac", "--release", "17", "-cp", classPath, "-d", root,
                Path.Combine(common, "PortableIdentityReflection.java"),
                Path.Combine(common, "PortablePerPlayerChunksHooks.java"),
                Path.Combine(common, "PortableLanAutoPublishHooks.java"),
                Path.Combine(common, "PortableLanAutoPublishTransformer.java"),
                RepositoryFile("Program.Tests", "Fixtures", "PauseReportingProbe.java"));
            var output = await Run("java", "-Xverify:all", "-cp", root + Path.PathSeparator + classPath,
                "PauseReportingProbe");
            Assert.Contains("Native pause reporting checks passed.", output, StringComparison.Ordinal);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    private static async Task<string> Run(string tool, params string[] arguments)
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(home)
            ? tool + ".exe" : Path.Combine(home, "bin", tool + ".exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
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

    private static string RepositoryFile(params string[] parts)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var path = parts.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }
}
