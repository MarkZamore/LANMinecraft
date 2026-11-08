using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.Version;

namespace Minecraft.Tests;

public sealed class InheritedGameArgumentsTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "legacy arguments с пробелами " + Guid.NewGuid().ToString("N"));
    private static string Fixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "Program.Tests", "Fixtures", "LegacyLaunch", name));
    }
    private JsonVersion Parent() => new(JsonDocument.Parse(Fixture("1.6.4.json")), new());
    private JsonVersion Child(JsonVersion parent, string? arguments = null)
    {
        var node = JsonNode.Parse(Fixture("legacyfabric.json"))!;
        if (arguments == "modern") node["arguments"] = new JsonObject { ["game"] = new JsonArray("--username", "${auth_player_name}", "--loader-option", "kept") };
        if (arguments == "old") node["minecraftArguments"] = "--username ${auth_player_name} --loader-option kept";
        return new(JsonDocument.Parse(node.ToJsonString()), new()) { ParentVersion = parent };
    }
    private MLaunchOption Options(bool? virtualAssets)
    {
        var path = new MinecraftPath(root) { Assets = Path.Combine(root, "shared assets") };
        Directory.CreateDirectory(Path.Combine(path.Assets, "indexes"));
        if (virtualAssets.HasValue) File.WriteAllText(Path.Combine(path.Assets, "indexes", "legacy.json"), JsonSerializer.Serialize(new { @virtual = virtualAssets.Value, objects = new { } }));
        var session = MSession.CreateOfflineSession("StableSteamName"); session.AccessToken = "fixture-session";
        return new() { JavaPath = "javaw.exe", Path = path, NativesDirectory = Path.Combine(root, "natives"), Session = session, ScreenWidth = 1200, ScreenHeight = 800,
            ExtraGameArguments = [new MArgument(["--fixture-user-option", "значение с пробелами"])], ExtraJvmArguments = [] };
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActualLegacyFabricAndVanillaProfilesReachTheProductionBuilderWithIdentityAndAssets(bool virtualAssets)
    {
        using var parent = Parent(); using var child = Child(parent); var options = Options(virtualAssets);
        using var baseline = new MinecraftLauncher(options.Path!).BuildProcess(child, options);
        Assert.DoesNotContain("--assetsDir", baseline.StartInfo.Arguments, StringComparison.Ordinal);
        using var process = MinecraftLaunchArguments.BuildProcess(new MinecraftLauncher(options.Path!), child, options);
        var args = MinecraftProcessService.CompleteJvmArguments(process.StartInfo, child, root).ToList();
        var game = args.Skip(args.IndexOf("net.fabricmc.loader.impl.launch.knot.KnotClient") + 1).ToList();
        string Value(string key) { Assert.Single(game, value => value == key); return game[game.IndexOf(key) + 1]; }
        Assert.Equal("StableSteamName", Value("--username")); Assert.Equal("fixture-session", Value("--session"));
        Assert.Equal(child.Id, Value("--version")); Assert.Equal(options.Path!.BasePath, Value("--gameDir"));
        Assert.Equal(virtualAssets ? Path.Combine(options.Path.Assets, "virtual", "legacy") : options.Path.Assets, Value("--assetsDir"));
        Assert.Equal("значение с пробелами", Value("--fixture-user-option")); Assert.Equal("1200", Value("--width"));
        Assert.DoesNotContain("${", process.StartInfo.Arguments, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root, "assets"))); // no instance-link workaround
    }
    [Theory]
    [InlineData("modern")]
    [InlineData("old")]
    public void ExplicitLoaderArgumentsKeepTheirOwnPrecedence(string kind)
    {
        using var parent = Parent(); using var child = Child(parent, kind); var options = Options(null);
        using var baseline = new MinecraftLauncher(options.Path!).BuildProcess(child, options);
        using var process = MinecraftLaunchArguments.BuildProcess(new MinecraftLauncher(options.Path!), child, options);
        Assert.Equal(baseline.StartInfo.Arguments, process.StartInfo.Arguments);
        Assert.Contains("--loader-option kept", process.StartInfo.Arguments, StringComparison.Ordinal);
    }
    [Fact]
    public void ARequiredAssetIndexMustExistBeforeLaunchingWithInheritedLegacyArguments()
    {
        using var parent = Parent(); using var child = Child(parent); var options = Options(null);
        Assert.Throws<FileNotFoundException>(() => MinecraftLaunchArguments.BuildProcess(new MinecraftLauncher(options.Path!), child, options));
    }
    public void Dispose() { if (Directory.Exists(root)) TempTree.Delete(root); }
}
