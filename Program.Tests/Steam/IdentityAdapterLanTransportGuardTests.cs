using System.Text;
using Minecraft;

namespace Minecraft.Tests;

public sealed class IdentityAdapterLanTransportGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lan-transport-guard-" + Guid.NewGuid().ToString("N"));
    private static readonly SteamTransportBuild Pin = new("0.3.4", "transport.jar", 3,
        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
        1, [PackLoaderKind.NeoForge], [26, 3], [26, 4]);

    public void Dispose() => TempTree.Delete(_root);

    [Theory]
    [InlineData("abc", true)]
    [InlineData("abd", false)]
    [InlineData("abcd", false)]
    public void NativeInitRedirectGuard_RequiresExactLengthAndPinnedBytes(string contents, bool expected)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Pin.FileName);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(contents));
        Assert.Equal(expected, IdentityAdapterMappingService.IsPinnedTransportJar(path, Pin));
    }

    [Fact]
    public void NativeInitRedirectGuard_RejectsMissingTransport()
    {
        Assert.False(IdentityAdapterMappingService.IsPinnedTransportJar(Path.Combine(_root, Pin.FileName), Pin));
    }
}
