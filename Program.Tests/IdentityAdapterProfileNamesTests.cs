using System.Diagnostics;
using System.IO;

namespace Minecraft.Tests;

public sealed class IdentityAdapterProfileNamesTests
{
    [Fact]
    public async Task Portable_owner_names_work_with_cold_caches_and_preserve_unknown_accounts()
    {
        var common = RepositoryFile("Program", "IdentityAdapters", "Common", "PortableIdentityProfiles.java");
        var root = Path.Combine(Path.GetTempPath(), $"minecraft-profile-names-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var directory = Path.GetDirectoryName(common)!;
            var classPath = string.Join(Path.PathSeparator,
                Path.Combine(directory, "lib", "asm-9.8.jar"),
                Path.Combine(directory, "lib", "asm-tree-9.8.jar"));
            await Run("javac", "--release", "17", "-cp", classPath, "-d", root,
                common, Path.Combine(directory, "PortableProfileNameTransformer.java"),
                RepositoryFile("Program.Tests", "Fixtures", "ProfileNamesProbe.java"));
            var output = await Run("java", "-Xverify:all", "-cp", root + Path.PathSeparator + classPath,
                "ProfileNamesProbe", Path.Combine(root, "identities.properties"));
            Assert.Contains("Profile name compatibility checks passed.", output, StringComparison.Ordinal);
            Assert.Contains("new PortableProfileNameTransformer()", File.ReadAllText(
                Path.Combine(directory, "PortableIdentityAgent.java")), StringComparison.Ordinal);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    private static async Task<string> Run(string tool, params string[] arguments)
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(home) ? tool + ".exe" : Path.Combine(home, "bin", tool + ".exe"))
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
