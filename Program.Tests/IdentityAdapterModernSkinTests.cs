using System.Diagnostics;
using System.IO;

namespace Minecraft.Tests;

public sealed class IdentityAdapterModernSkinTests
{
    [Fact]
    public async Task Native_lookup_keeps_its_body_and_relaxes_secure_skins_only_for_registered_profiles()
    {
        var common = Path.GetDirectoryName(RepositoryFile(
            "Program", "IdentityAdapters", "Common", "PortableSkinProfiles.java"))!;
        var root = Path.Combine(Path.GetTempPath(), "minecraft-native-skin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var classPath = string.Join(Path.PathSeparator,
                Path.Combine(common, "lib", "asm-9.8.jar"),
                Path.Combine(common, "lib", "asm-tree-9.8.jar"));
            await Run("javac", "--release", "17", "-cp", classPath, "-d", root,
                Path.Combine(common, "PortableIdentityReflection.java"),
                Path.Combine(common, "PortableSkinProfiles.java"),
                RepositoryFile("Program", "IdentityAdapters", "Minecraft-1.21.1-NeoForge", "PortableIdentityTransformer.java"),
                RepositoryFile("Program.Tests", "Fixtures", "NativeSkinLookupProbe.java"),
                RepositoryFile("Program.Tests", "Fixtures", "ModernSkin", "com", "mojang", "authlib", "GameProfile.java"),
                RepositoryFile("Program.Tests", "Fixtures", "ModernSkin", "com", "mojang", "authlib", "properties", "Property.java"));
            var output = await Run("java", "-Xverify:all", "-cp", root + Path.PathSeparator + classPath,
                "NativeSkinLookupProbe", Path.Combine(root, "skins.properties"));
            Assert.Contains("Native skin lookup guard checks passed.", output, StringComparison.Ordinal);
            Assert.Contains("Texture domain static and instance guard checks passed.", output, StringComparison.Ordinal);
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
