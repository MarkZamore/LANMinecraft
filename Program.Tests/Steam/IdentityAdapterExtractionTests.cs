using System.Security.Cryptography;

namespace Minecraft.Tests;

public sealed class IdentityAdapterExtractionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "adapter-extraction-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void UpdatingWhilePreviousJarIsInUse_ExtractsNewBytesWithoutReplacingTheOldJar()
    {
        var paths = new AppPaths(_root);
        var directory = Path.Combine(paths.Launcher, "IdentityAdapters", "fixture");
        Directory.CreateDirectory(directory);
        var oldJar = Path.Combine(directory, "portable-identity-adapter.jar");
        byte[] oldBytes = [1, 2, 3];
        File.WriteAllBytes(oldJar, oldBytes);
        using var lease = File.Open(oldJar, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var service = new PortableIdentityAdapterService(paths, new Logger(paths.LogFile));

        var current = service.ExtractAdapter(directory);

        Assert.NotEqual(oldJar, current);
        Assert.Equal(oldBytes, File.ReadAllBytes(oldJar));
        Assert.True(File.Exists(current));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(current))).ToLowerInvariant(),
            Path.GetFileName(Path.GetDirectoryName(current)));
        using var currentLease = File.Open(current, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(current, service.ExtractAdapter(directory));
    }

    [Fact]
    public void CorruptedCurrentJarIsRepairedAndDoesNotTouchLegacyJar()
    {
        var paths = new AppPaths(_root);
        var directory = Path.Combine(paths.Launcher, "IdentityAdapters", "fixture");
        Directory.CreateDirectory(directory);
        var oldJar = Path.Combine(directory, "portable-identity-adapter.jar");
        byte[] oldBytes = [1, 2, 3];
        File.WriteAllBytes(oldJar, oldBytes);
        using var service = new PortableIdentityAdapterService(paths, new Logger(paths.LogFile));
        var current = service.ExtractAdapter(directory);
        var expected = File.ReadAllBytes(current);
        File.WriteAllBytes(current, [4, 5, 6]);

        Assert.Equal(current, service.ExtractAdapter(directory));
        Assert.Equal(expected, File.ReadAllBytes(current));
        Assert.Equal(oldBytes, File.ReadAllBytes(oldJar));
    }
}
