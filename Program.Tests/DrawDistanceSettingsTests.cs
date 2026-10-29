using System.Text;

namespace Minecraft.Tests;

/// <summary>Preparation leaves graphics choices to native defaults, authored pack data and the player.</summary>
public sealed class DrawDistanceSettingsTests : IDisposable
{
    private const string PackName = "GraphicsChoices";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanmc-graphics-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TempTree.Delete(_root);

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task PreparationPreservesPlayerGraphicsBytesAndTimestamps(string newline)
    {
        var (_, service) = CreatePack();
        using var scope = service;
        var game = (await service.PrepareAsync(PackName)).GameDirectory;
        var options = string.Join(newline,
            new[] {"version:2865", "renderDistance:32", "simulationDistance:24", "graphicsMode:2",
             "key_key.attack:key.mouse.left", "soundCategory_master:0.7", "" });
        var iris = string.Join(newline,
            new[] {"# Personal shader settings", "#maxShadowRenderDistance=16", "maxShadowRenderDistance=64",
             "shaderPack=player-choice.zip", "enableShaders=true", "" });
        var optionsPath = Write(game, "options.txt", options);
        var irisPath = Write(game, "config/iris.properties", iris);
        var timestamp = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(optionsPath, timestamp);
        File.SetLastWriteTimeUtc(irisPath, timestamp);
        var optionsTime = File.GetLastWriteTimeUtc(optionsPath);
        var irisTime = File.GetLastWriteTimeUtc(irisPath);

        await service.PrepareAsync(PackName);
        await service.PrepareAsync(PackName);

        Assert.Equal(Encoding.UTF8.GetBytes(options), File.ReadAllBytes(optionsPath));
        Assert.Equal(Encoding.UTF8.GetBytes(iris), File.ReadAllBytes(irisPath));
        Assert.Equal(optionsTime, File.GetLastWriteTimeUtc(optionsPath));
        Assert.Equal(irisTime, File.GetLastWriteTimeUtc(irisPath));
        Assert.Equal(options, File.ReadAllText(optionsPath));
        Assert.Equal(iris, File.ReadAllText(irisPath));
    }

    [Fact]
    public async Task FreshPreparationDoesNotSeedCustomGraphicsSettings()
    {
        var (pack, service) = CreatePack();
        using var scope = service;
        const string authorOptions = "lang:ru_ru\nrenderDistance:12\nsimulationDistance:10\ngraphicsMode:1\n";
        const string authorShaderDefaults = "maxShadowRenderDistance=48\nenableShaders=true\n";
        Write(pack, "configureddefaults/options.txt", authorOptions);
        Write(pack, "defaultconfigs/iris.properties", authorShaderDefaults);

        var game = (await service.PrepareAsync(PackName)).GameDirectory;
        await service.PrepareAsync(PackName);

        Assert.False(File.Exists(Path.Combine(game, "options.txt")));
        Assert.False(File.Exists(Path.Combine(game, "config", "iris.properties")));
        Assert.Equal(authorOptions, File.ReadAllText(Path.Combine(game, "configureddefaults", "options.txt")));
        Assert.Equal(authorShaderDefaults, File.ReadAllText(Path.Combine(game, "defaultconfigs", "iris.properties")));
    }

    [Fact]
    public async Task AuthoredShaderSettingsAreCopiedWithoutClamping()
    {
        var (pack, service) = CreatePack();
        using var scope = service;
        const string authored = "# Author defaults\r\nmaxShadowRenderDistance=48\r\nshaderPack=author.zip\r\n";
        Write(pack, "config/iris.properties", authored);

        var game = (await service.PrepareAsync(PackName)).GameDirectory;
        await service.PrepareAsync(PackName);

        Assert.Equal(Encoding.UTF8.GetBytes(authored), File.ReadAllBytes(Path.Combine(game, "config", "iris.properties")));
        Assert.Equal(Encoding.UTF8.GetBytes(authored), File.ReadAllBytes(Path.Combine(pack, "config", "iris.properties")));
    }

    [Theory]
    [InlineData("renderDistance:16\nsimulationDistance:8\n", "maxShadowRenderDistance=8\n")]
    [InlineData("renderDistance:4\nsimulationDistance:5\n", "#maxShadowRenderDistance=32\nshaderPack=chosen.zip\n")]
    [InlineData("renderDistance:\nsimulationDistance:auto\nrenderDistanceExtra:32\n", "maxShadowRenderDistance=auto\n")]
    public async Task LowCommentedOrUnrecognizedSettingsRemainUntouched(string options, string iris)
    {
        var (_, service) = CreatePack();
        using var scope = service;
        var game = (await service.PrepareAsync(PackName)).GameDirectory;
        var optionsPath = Write(game, "options.txt", options);
        var irisPath = Write(game, "config/iris.properties", iris);

        await service.PrepareAsync(PackName);

        Assert.Equal(Encoding.UTF8.GetBytes(options), File.ReadAllBytes(optionsPath));
        Assert.Equal(Encoding.UTF8.GetBytes(iris), File.ReadAllBytes(irisPath));
    }

    private (string Pack, PackInstanceService Service) CreatePack()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var pack = paths.CombineUnderPacks(PackName);
        Write(pack, PackManifestService.ManifestFileName,
            """{"schemaVersion":1,"minecraftVersion":"1.20.1","loader":{"type":"vanilla"},"clientJar":"client.jar"}""");
        Write(pack, "client.jar", "client-fixture");
        return (pack, new PackInstanceService(paths, new Logger(paths.LogFile)));
    }

    private static string Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }
}
