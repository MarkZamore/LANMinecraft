using System.IO;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// A setting the launcher turns on inside somebody else's mods, and the one
/// pack it stopped from starting.
/// </summary>
/// <remarks>
/// ModernFix's lazy model loading is worth a great deal of heap on a heavy
/// pack, so the launcher switches it on for every pack it runs. That was
/// written when every pack it ran was NeoForge 1.21.1. On Minecraft before
/// 1.19.4 the option and Continuity cannot both be on - ModernFix says so
/// itself and refuses to start:
///
///     Continuity and ModernFix's dynamic resources option are not compatible
///     before Minecraft 1.19.4.
///
/// which is a compatibility screen where a game should have been.
/// </remarks>
public sealed class DynamicResourcesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-dynamic-resources-{Guid.NewGuid():N}");

    public void Dispose() => TempTree.Delete(_root);

    private string Pack(params string[] modFileNames)
    {
        var pack = Path.Combine(_root, "pack");
        Directory.CreateDirectory(Path.Combine(pack, "mods"));
        foreach (var name in modFileNames) File.WriteAllText(Path.Combine(pack, "mods", name), "");
        return pack;
    }

    [Theory]
    // The pack that hit it.
    [InlineData("1.18.2", true)]
    [InlineData("1.16.5", true)]
    [InlineData("1.19.3", true)]
    // From the version ModernFix names, the two live together.
    [InlineData("1.19.4", false)]
    [InlineData("1.20.1", false)]
    [InlineData("1.21.1", false)]
    public void WithContinuity_TheOptionIsWithheldOnlyWhereItWouldClash(string version, bool withheld)
    {
        var pack = Pack("continuity-2.0.1+1.18.2.jar", "sodium-fabric-mc1.18.2-0.4.1.jar");
        Assert.Equal(withheld, MinecraftProcessService.DynamicResourcesWouldClash(pack, version));
    }

    /// <summary>Without Continuity there is nothing to clash with, at any age.</summary>
    [Theory]
    [InlineData("1.7.10")]
    [InlineData("1.18.2")]
    [InlineData("1.21.1")]
    public void WithoutContinuity_TheOptionIsGiven(string version)
    {
        var pack = Pack("sodium-fabric-mc1.18.2-0.4.1.jar", "lithium-fabric-mc1.18.2-0.7.10.jar");
        Assert.False(MinecraftProcessService.DynamicResourcesWouldClash(pack, version));
    }

    /// <summary>
    /// And a pack nobody can read is left alone rather than forced: the setting
    /// belongs to the pack's mods, not to the launcher.
    /// </summary>
    [Fact]
    public void APackWithNoModsFolder_IsNotForced()
    {
        Directory.CreateDirectory(_root);
        Assert.False(MinecraftProcessService.DynamicResourcesWouldClash(Path.Combine(_root, "absent"), "1.21.1"));
        Assert.False(MinecraftProcessService.DynamicResourcesWouldClash(Path.Combine(_root, "absent"), "1.18.2"));
    }
}
