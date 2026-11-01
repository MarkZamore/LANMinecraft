using System.Diagnostics;

namespace Minecraft.Tests;

public sealed class IdentityAdapterSteamLanEntryTests
{
    [Fact]
    public async Task NativeSteamWorldHeaders_KeepNativeRowsAndRejectIncompleteShapes()
    {
        var repository = FindRepository();
        var common = Path.Combine(repository, "Program", "IdentityAdapters", "Common");
        var root = Path.Combine(Path.GetTempPath(), "steam-lan-header-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var classPath = string.Join(Path.PathSeparator, Path.Combine(common, "lib", "asm-9.8.jar"),
                Path.Combine(common, "lib", "asm-tree-9.8.jar"));
            await Run("javac", "--release", "17", "-cp", classPath, "-d", root,
                Path.Combine(common, "PortableSteamLanEntryTransformer.java"),
                Path.Combine(repository, "Program.Tests", "Fixtures", "SteamLanEntryProbe.java"));
            var output = await Run("java", "-Xverify:all", "-cp", root + Path.PathSeparator + classPath,
                "minecraft.portable.identity.SteamLanEntryProbe");
            Assert.Contains("PASS: native Steam headers and narration use the world name", output, StringComparison.Ordinal);
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
            if (File.Exists(Path.Combine(directory.FullName, "Program.Tests", "Fixtures", "SteamLanEntryProbe.java"))) return directory.FullName;
        throw new DirectoryNotFoundException("The native Steam LAN entry probe source was not found.");
    }
}
