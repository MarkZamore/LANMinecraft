using System.IO;
using Xunit;

namespace Minecraft.Tests;

public sealed class LegacyIdentityAdapterPreparationTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "artifacts", nameof(LegacyIdentityAdapterPreparationTests), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PreAuthlibRuntimePassesLaunchContextWithoutExtractingOrRunningModernAgent()
    {
        var paths = new AppPaths(_root);
        var game = Path.Combine(paths.Instances, "Pack Display Name");
        Directory.CreateDirectory(game);
        var runtime = new PreparedRuntime(paths.SharedRuntime, "legacy-profile", "missing-java.exe", "missing-client.jar",
            new PackRuntimeDescriptor(1, "1.6.4", new PackLoaderDescriptor(PackLoaderKind.LegacyFabric, "0.18.2"), "client.jar", "legacy-hash")
            { JavaMajorVersion = 17 });
        using var service = new PortableIdentityAdapterService(paths, new Logger(Path.Combine(paths.Personal, "test.log")));

        var args = await service.PrepareJvmArgumentsAsync(runtime, game, CancellationToken.None);

        Assert.Contains("-Dminecraft.portable.legacy.enabled=true", args);
        Assert.Contains($"-Dminecraft.portable.legacy.registry={Path.Combine(paths.Personal, "legacy-identity-profiles.properties")}", args);
        Assert.Contains("-Dminecraft.portable.identity.windowTitleText=Pack Display Name", args);
        Assert.DoesNotContain(args, argument => argument.StartsWith("-javaagent:", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(paths.Launcher, "IdentityAdapters")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
