using System.Diagnostics;
using System.IO;

namespace Minecraft.Tests;

public sealed class IdentityAdapterPreflightClassSourcesTests
{
    [Fact]
    public async Task SplitNativeArchives_RequireTheirExactExplicitSupportingClasses()
    {
        var common = Path.GetDirectoryName(FileInRepository("Program", "IdentityAdapters", "Common", "PortableIdentityPreflight.java"))!;
        var root = Path.Combine(Path.GetTempPath(), "preflight-class-sources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var classPath = string.Join(Path.PathSeparator,
                Path.Combine(common, "lib", "asm-9.8.jar"), Path.Combine(common, "lib", "asm-tree-9.8.jar"));
            var sources = Directory.EnumerateFiles(common, "*.java").Concat(new[] {
                FileInRepository("Program", "IdentityAdapters", "Minecraft-1.21.1-NeoForge", "PortableIdentityTransformer.java"),
                FileInRepository("Program.Tests", "Fixtures", "PreflightClassSourcesProbe.java") });
            await Run("javac", new[] {"--release", "17", "-cp", classPath, "-d", root}.Concat(sources).ToArray());
            var output = await Run("java", "-Xverify:all", "-cp", root + Path.PathSeparator + classPath,
                "PreflightClassSourcesProbe", Path.Combine(root, "fixture"));
            Assert.Contains("PASS split native preflight:", output, StringComparison.Ordinal);
        }
        finally { TempTree.Delete(root); }
    }

    private static async Task<string> Run(string tool, params string[] arguments)
    {
        var jdk = Environment.GetEnvironmentVariable("JAVA_HOME") ?? @"C:\Tools\jdk-25.0.2";
        var start = new ProcessStartInfo(Path.Combine(jdk, "bin", tool + ".exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var result = await output + await error;
        Assert.True(process.ExitCode == 0, result);
        return result;
    }
    private static string FileInRepository(params string[] parts)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        { var file = parts.Aggregate(current.FullName, Path.Combine); if (File.Exists(file)) return file; }
        throw new FileNotFoundException(string.Join('/', parts));
    }
}
