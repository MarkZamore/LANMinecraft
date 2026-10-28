using System.Diagnostics;

namespace Minecraft.Tests;

public sealed class IdentityAdapterLanReturnTests
{
    [Fact]
    public async Task ForgeCancellableAuthorForm_PublishesAfterInitializationOnEveryReturn()
    {
        var repository = FindRepository();
        var common = Path.Combine(repository, "Program", "IdentityAdapters", "Common");
        var root = Path.Combine(Path.GetTempPath(), "forge-lan-return-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var classPath = string.Join(Path.PathSeparator, Path.Combine(common, "lib", "asm-9.8.jar"),
                Path.Combine(common, "lib", "asm-tree-9.8.jar"));
            await Run("javac", "--release", "17", "-cp", classPath, "-d", root,
                Path.Combine(common, "PortableIdentityReflection.java"),
                Path.Combine(common, "PortablePerPlayerChunksHooks.java"),
                Path.Combine(common, "PortableLanAutoPublishHooks.java"),
                Path.Combine(common, "PortableLanAutoPublishTransformer.java"),
                Path.Combine(repository, "Program.Tests", "Fixtures", "ForgeLanReturnProbe.java"));
            var output = await Run("java", "-Xverify:all", "-cp", root + Path.PathSeparator + classPath, "ForgeLanReturnProbe");
            Assert.Contains("PASS: actual Forge hook follows initialized vanilla and cancelled author-form returns", output, StringComparison.Ordinal);
        }
        finally { TempTree.Delete(root); }
    }

    private static async Task<string> Run(string tool, params string[] arguments)
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(javaHome) ? tool + ".exe" : Path.Combine(javaHome, "bin", tool + ".exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var result = await output + await error;
        Assert.True(process.ExitCode == 0, result);
        return result;
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Program.Tests", "Fixtures", "ForgeLanReturnProbe.java"))) return directory.FullName;
        throw new DirectoryNotFoundException("The Forge return-path probe source was not found.");
    }
}
