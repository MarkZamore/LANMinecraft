using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace Minecraft.Tests;

public sealed class IdentityAdapterLanRedirectCoexistenceTests
{
    [Fact]
    public async Task Pinned_author_redirect_keeps_native_init_and_unrelated_callbacks_before_publication()
    {
        var common = Path.GetDirectoryName(RepositoryFile("Program", "IdentityAdapters", "Common",
            "PortableLanAutoPublishHooks.java"))!;
        var fixture = RepositoryFile("Program.Tests", "Fixtures", "LanRedirect", "upstream-fixtures.zip");
        Assert.Equal("fb9a5293762a33cd4d4d516736756d288465d27b10722d66a4741c7c725d8a08",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture))));
        var root = Path.Combine(Path.GetTempPath(), "minecraft-lan-redirect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var classPath = string.Join(Path.PathSeparator, fixture,
                Path.Combine(common, "lib", "asm-9.8.jar"), Path.Combine(common, "lib", "asm-tree-9.8.jar"));
            await Run("javac", "--release", "17", "-cp", classPath, "-d", root,
                Path.Combine(common, "PortableIdentityReflection.java"),
                Path.Combine(common, "PortablePerPlayerChunksHooks.java"),
                Path.Combine(common, "PortableLanAutoPublishHooks.java"),
                Path.Combine(common, "PortableLanAutoPublishTransformer.java"),
                RepositoryFile("Program.Tests", "Fixtures", "LanRedirectCoexistenceProbe.java"));
            var output = await Run("java", "-Xverify:all", "-cp", root + Path.PathSeparator + classPath,
                "LanRedirectCoexistenceProbe", fixture);
            Assert.Contains("Pinned author redirect coexistence checks passed: 4 actual handlers.", output,
                StringComparison.Ordinal);
        }
        finally { TempTree.Delete(root); }
    }

    private static async Task<string> Run(string tool, params string[] arguments)
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(home) ? tool + ".exe" : Path.Combine(home, "bin", tool + ".exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
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
