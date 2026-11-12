using System.IO;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.Version;

namespace Minecraft.Tests;

public sealed class LegacyForgeClientJarTests : IDisposable
{
    private const string GameVersion = "1.12.2";
    private const string ForgeProfile = "1.12.2-forge-14.23.5.2860";
    private const string LaunchWrapper = "net.minecraft.launchwrapper.Launch";
    private const string ForgeMetadata = """
        {
          "id": "1.12.2-forge-14.23.5.2860",
          "inheritsFrom": "1.12.2",
          "mainClass": "net.minecraft.launchwrapper.Launch",
          "type": "release",
          "minecraftArguments": "--username ${auth_player_name} --tweakClass net.minecraftforge.fml.common.launcher.FMLTweaker",
          "libraries": []
        }
        """;

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "forge launch " + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LaunchWrapperGetsTheVanillaClientJarOnTheProductionClasspath()
    {
        var path = new MinecraftPath(_root);
        var profilePath = WriteProfile(path, ForgeMetadata);
        var fakeLoaderJar = Path.Combine(path.Versions, ForgeProfile, ForgeProfile + ".jar");
        var clientJar = Path.Combine(path.Versions, GameVersion, GameVersion + ".jar");
        using var parent = new JsonVersion(JsonDocument.Parse("""
            {"id":"1.12.2","mainClass":"net.minecraft.client.main.Main","type":"release","libraries":[]}
            """), new());

        var before = LaunchArguments();
        Assert.Contains(fakeLoaderJar, before, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(clientJar, before, StringComparison.OrdinalIgnoreCase);

        var logger = new Logger(Path.Combine(_root, "log.txt"));
        Assert.True(InheritedGameJar.NameLegacyForgeJar(profilePath, GameVersion, logger));
        var after = LaunchArguments();
        Assert.Contains(clientJar, after, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fakeLoaderJar, after, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(LaunchWrapper, after, StringComparison.Ordinal);
        Assert.Contains("--tweakClass net.minecraftforge.fml.common.launcher.FMLTweaker", after, StringComparison.Ordinal);
        Assert.False(InheritedGameJar.NameLegacyForgeJar(profilePath, GameVersion, logger));

        string LaunchArguments()
        {
            using var child = new JsonVersion(JsonDocument.Parse(File.ReadAllText(profilePath)), new()) { ParentVersion = parent };
            var options = new MLaunchOption
            {
                JavaPath = "javaw.exe",
                Path = path,
                NativesDirectory = Path.Combine(_root, "natives"),
                Session = MSession.CreateOfflineSession("ForgeQa")
            };
            using var process = MinecraftLaunchArguments.BuildProcess(new MinecraftLauncher(path), child, options);
            return process.StartInfo.Arguments;
        }
    }

    [Theory]
    [InlineData("net.minecraft.launchwrapper.Launch", "cpw.mods.modlauncher.Launcher")]
    [InlineData("\"inheritsFrom\": \"1.12.2\"", "\"inheritsFrom\": \"1.11.2\"")]
    [InlineData("1.12.2-forge-14.23.5.2860", "other-profile")]
    [InlineData("\"libraries\": []", "\"libraries\": [], \"jar\": \"custom\"")]
    [InlineData("\"libraries\": []", "\"libraries\": [], \"downloads\": { \"client\": {} }")]
    public void OnlyMatchingLaunchWrapperProfilesWithAnInheritedJarAreRepaired(string original, string substitute)
    {
        var json = ForgeMetadata.Replace(original, substitute, StringComparison.Ordinal);
        var profilePath = WriteProfile(new MinecraftPath(_root), json);

        Assert.False(InheritedGameJar.NameLegacyForgeJar(profilePath, GameVersion, new Logger(Path.Combine(_root, "log.txt"))));
        Assert.Equal(json, File.ReadAllText(profilePath));
    }

    private static string WriteProfile(MinecraftPath path, string json)
    {
        var directory = Path.Combine(path.Versions, ForgeProfile);
        Directory.CreateDirectory(directory);
        var profilePath = Path.Combine(directory, ForgeProfile + ".json");
        File.WriteAllText(profilePath, json);
        return profilePath;
    }

    public void Dispose() => TempTree.Delete(_root);
}
