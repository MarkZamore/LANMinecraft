using System.Diagnostics;
using System.IO.Compression;

namespace Minecraft.Tests;

public sealed class IdentityAdapterContentBoundaryTests
{
    [Fact]
    public async Task PackMenuCompatibilityCannotReturnThroughPackagedAgentOrLegacyProperties()
    {
        var root = Path.Combine(Path.GetTempPath(), "lanmc-agent-content-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var adapter = Path.Combine(root, "adapter.jar");
            using (var resource = typeof(AppPaths).Assembly.GetManifestResourceStream("Minecraft.PortableIdentityAdapter.jar")!)
            using (var output = File.Create(adapter)) await resource.CopyToAsync(output);
            using (var zip = ZipFile.OpenRead(adapter))
                Assert.DoesNotContain(zip.Entries, entry => entry.FullName.EndsWith("/PortableMenuRegistrationTransformer.class", StringComparison.Ordinal));
            var fixture = RepositoryFile("Program.Tests", "Fixtures", "AgentContentBoundaryProbe.java");
            await Run("javac", "--release", "17", "-cp", adapter, "-d", root, fixture);
            var text = await Run("java", "-Xverify:all", "-cp", root + Path.PathSeparator + adapter, "AgentContentBoundaryProbe");
            Assert.Contains("Agent content boundary passed: registrations=10, unchangedMenuChecks=20.", text, StringComparison.Ordinal);
        }
        finally { TempTree.Delete(root); }
    }

    private static async Task<string> Run(string tool, params string[] arguments)
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(home) ? tool + ".exe" : Path.Combine(home, "bin", tool + ".exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var output = await stdout + await stderr; Assert.True(process.ExitCode == 0, output); return output;
    }
    private static string RepositoryFile(params string[] parts)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var path = parts.Aggregate(current.FullName, Path.Combine); if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }
}
