using System.Diagnostics;
using System.IO;

namespace Minecraft.Tests;

/// <summary>
/// Executes the production title transformer in a verified JVM. The fixture
/// simulates lifecycle events; it does not start Minecraft or a Steam session.
/// </summary>
public sealed class WindowTitleAdapterTests
{
    [Fact]
    public async Task Native_title_producer_keeps_only_pack_name_across_simulated_lifecycle_without_changing_connections()
    {
        var fixture = RepositoryFile("Program.Tests", "Fixtures", "WindowTitleTransformerProbe.java");
        var transformer = RepositoryFile("Program", "IdentityAdapters", "Common", "PortableWindowTitleTransformer.java");
        var common = Path.GetDirectoryName(transformer)!;
        var classPath = string.Join(Path.PathSeparator,
            Path.Combine(common, "lib", "asm-9.8.jar"),
            Path.Combine(common, "lib", "asm-tree-9.8.jar"));
        var outputDirectory = Path.Combine(Path.GetTempPath(), "minecraft-window-title-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        try
        {
            await Run("javac", "--release", "17", "-cp", classPath, "-d", outputDirectory, transformer, fixture);
            var output = await Run("java", "-Xverify:all", "-cp",
                outputDirectory + Path.PathSeparator + classPath, "WindowTitleTransformerProbe");
            Assert.Contains("WINDOW_TITLE_SYNTHETIC_PASS", output, StringComparison.Ordinal);
        }
        finally
        {
            TempTree.Delete(outputDirectory);
        }
    }

    private static async Task<string> Run(string tool, params string[] arguments)
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        var executable = string.IsNullOrWhiteSpace(javaHome)
            ? tool + ".exe" : Path.Combine(javaHome, "bin", tool + ".exe");
        var start = new ProcessStartInfo(executable)
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
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
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
