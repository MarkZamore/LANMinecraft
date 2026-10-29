using System.Text;
using Minecraft;

namespace Minecraft.Tests;

public sealed class MinecraftLaunchPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "minecraft-launch-policy-" + Guid.NewGuid().ToString("N"));
    private const string TimeoutProperty = "-Dsun.net.client.defaultConnectTimeout=";

    [Theory]
    [InlineData("")]
    [InlineData("mixin.perf.dedicated_reload_executor=true\r\n")]
    [InlineData("mixin.perf.dedicated_reload_executor=false\r\n")]
    public async Task NativeModernFixSettings_ArePreservedWithoutGlobalNetworkTimeout(string nativeSetting)
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var pack = Path.Combine(paths.Packs, "Native settings fixture");
        Write(pack, PackManifestService.ManifestFileName,
            """{"schemaVersion":1,"minecraftVersion":"1.20.1","loader":{"type":"vanilla"},"clientJar":"client.jar"}""");
        Write(pack, "client.jar", "jar");
        var nativeBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(
            "# Author native defaults\r\n" + nativeSetting + "mixin.perf.dynamic_resources=true\r\n")).ToArray();
        var nativeRelative = Path.Combine("config", "modernfix-mixins.properties");
        var sourceConfig = Path.Combine(pack, nativeRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceConfig)!);
        File.WriteAllBytes(sourceConfig, nativeBytes);
        var sourceStamp = File.GetLastWriteTimeUtc(sourceConfig);
        using var instances = new PackInstanceService(paths, new Logger(paths.LogFile));
        var prepared = await instances.PrepareAsync("Native settings fixture");
        var instanceConfig = Path.Combine(prepared.GameDirectory, nativeRelative);
        var instanceStamp = File.GetLastWriteTimeUtc(instanceConfig);

        var arguments = BuildArguments(pack, prepared.GameDirectory);

        Assert.DoesNotContain(arguments, argument => argument.StartsWith(TimeoutProperty, StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, argument => argument.Contains("modernfix", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(nativeBytes, File.ReadAllBytes(sourceConfig));
        Assert.Equal(sourceStamp, File.GetLastWriteTimeUtc(sourceConfig));
        Assert.Equal(nativeBytes, File.ReadAllBytes(instanceConfig));
        Assert.Equal(instanceStamp, File.GetLastWriteTimeUtc(instanceConfig));
    }

    [Fact]
    public void ExplicitPackNetworkTimeout_IsRetainedByActualArgumentComposition()
    {
        var pack = Path.Combine(_root, "pack");
        var game = Path.Combine(_root, "instance");
        Write(pack, "launcher/jvm-args.txt", TimeoutProperty + "12345\n-Dfixture.keep=true\n");

        var arguments = BuildArguments(pack, game);

        Assert.Equal(TimeoutProperty + "12345", Assert.Single(arguments.Where(argument =>
            argument.StartsWith(TimeoutProperty, StringComparison.Ordinal))));
        Assert.Contains("-Dfixture.keep=true", arguments);
        Assert.Contains("-Dfile.encoding=UTF-8", arguments);
        Assert.Contains("-XX:+ExitOnOutOfMemoryError", arguments);
    }

    private List<string> BuildArguments(string pack, string game) =>
        MinecraftProcessService.BuildBaseJvmArguments(
            pack, game, Path.Combine(_root, "temp"), Path.Combine(_root, "home"),
            Path.Combine(_root, "skin-registry.json"), Path.Combine(_root, "identity-registry.json"),
            "86ecf95a-859f-4ca4-8576-c25eb8fc39cb");

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose() => TempTree.Delete(_root);
}
