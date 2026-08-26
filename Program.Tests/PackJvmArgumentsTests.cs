using System.IO;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// A pack asking to be started with the Java options its own mods need.
/// </summary>
/// <remarks>
/// This exists because the launcher used to decide that for everybody. It
/// passed ModernFix's lazy model loading to every pack it started - the option
/// is worth a great deal of heap and can only be set as a property - and that
/// was a decision about somebody else's mods, made when every pack it ran was
/// NeoForge 1.21.1. On 1.18.2 the same option takes model loading away from
/// BetterEnd, which then fails to apply at all, and the pack does not start.
/// </remarks>
public sealed class PackJvmArgumentsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-jvm-args-{Guid.NewGuid():N}");

    public void Dispose() => TempTree.Delete(_root);

    private string Pack(string? list)
    {
        var pack = Path.Combine(_root, "pack");
        Directory.CreateDirectory(Path.Combine(pack, PackInstanceService.LauncherDataRoot));
        if (list is not null)
        {
            File.WriteAllText(
                Path.Combine(pack, PackInstanceService.LauncherDataRoot, "jvm-args.txt"),
                list);
        }
        return pack;
    }

    [Fact]
    public void APackGetsTheOptionsItAsksFor()
    {
        var pack = Pack("""
        # Lazy model loading: worth a great deal of heap on a pack this size,
        # and only settable as a property.
        -Dmodernfix.config.mixin.perf.dynamic_resources=true

        -Dsomething.else=1
        """);

        Assert.Equal(
            ["-Dmodernfix.config.mixin.perf.dynamic_resources=true", "-Dsomething.else=1"],
            new PackJvmArgumentsService().Load(pack));
    }

    /// <summary>A pack that asks for nothing is started with nothing extra.</summary>
    [Fact]
    public void APackWithNoList_AsksForNothing()
    {
        Assert.Empty(new PackJvmArgumentsService().Load(Pack(null)));
        Assert.Empty(new PackJvmArgumentsService().Load(Path.Combine(_root, "absent")));
        Assert.Empty(new PackJvmArgumentsService().Load(""));
    }

    /// <summary>
    /// Only Java options. The JVM refuses to start at all on an argument it
    /// does not understand, so a line that is not one is left out rather than
    /// passed on - and a line that is not one is also how somebody would try to
    /// make the launcher run a different program.
    /// </summary>
    [Fact]
    public void OnlyJavaOptionsAreEverPassedOn()
    {
        var parsed = PackJvmArgumentsService.Parse("""

        # a comment
        -XX:+UseG1GC
        cmd.exe /c format
        net.minecraft.client.main.Main
        -XX:+UseG1GC
        -Xshare:off
        """);

        // The repeat is dropped with it: a pack naming an option twice meant it
        // once.
        Assert.Equal(["-XX:+UseG1GC", "-Xshare:off"], parsed);
    }
}
