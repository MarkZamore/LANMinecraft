using System.IO;

namespace Minecraft.Tests;

/// <summary>
/// The rectangle behind a player's name.
/// </summary>
/// <remarks>
/// Minecraft draws the name over a quarter-black plate, and asks its Text
/// Background setting how solid to make it - unless "background for chat only"
/// is on, which it is by default, and then the plate is a hard quarter and no
/// setting touches it. A shaderpack draws that plate through its own entity
/// program, and the ones that draw entities without blending turn a quarter of
/// black into all of it: a filled rectangle where the name should be, black
/// under torches and gone in the sun, because the same program multiplies it by
/// the light of the place the player is standing.
///
/// Fourteen of the seventeen shaderpacks in Limitless 8 do it. Patching them is
/// not on offer - most may not be redistributed changed - and the game's own
/// setting takes the chat's background away with it. So the launcher stops
/// asking for the plate, and only for names.
/// </remarks>
public sealed class NameTagPlateTests
{
    /// <summary>
    /// Both halves agree on the seam: the launcher names the method out of the
    /// runtime's mappings, and the adapter patches whatever it is called there.
    /// </summary>
    [Fact]
    public void TheLauncherAndTheAdapter_AgreeOnWhereTheNameIsDrawn()
    {
        var mapping = ReadRepositoryFile("Program", "IdentityAdapterMappingService.cs");
        var transformer = ReadRepositoryFile(
            "Program", "IdentityAdapters", "Minecraft-1.21.1-NeoForge", "PortableIdentityTransformer.java");

        Assert.Contains("net/minecraft/client/renderer/entity/EntityRenderer", mapping, StringComparison.Ordinal);
        Assert.Contains("\"nameTagClasses\"", mapping, StringComparison.Ordinal);
        Assert.Contains("\"nameTagMethods\"", mapping, StringComparison.Ordinal);
        Assert.Contains("\"nameTagDescriptors\"", mapping, StringComparison.Ordinal);
        Assert.Contains("\"renderNameTag\"", transformer, StringComparison.Ordinal);
        Assert.Contains("\"nameTagMethods\"", transformer, StringComparison.Ordinal);
    }

    /// <summary>
    /// The store is found by the constant the game asks its option with, not by
    /// counting instructions - and a version that stops carrying it is left
    /// alone rather than half-patched.
    /// </summary>
    [Fact]
    public void ThePlate_IsFoundByTheOpacityItIsBuiltFrom()
    {
        var transformer = ReadRepositoryFile(
            "Program", "IdentityAdapters", "Minecraft-1.21.1-NeoForge", "PortableIdentityTransformer.java");

        Assert.Contains("value == 0.25F", transformer, StringComparison.Ordinal);
        Assert.Contains("Opcodes.ISTORE", transformer, StringComparison.Ordinal);
        Assert.Contains("if (!patched) {\n            return null;", transformer, StringComparison.Ordinal);
    }

    /// <summary>
    /// And only where the rectangle is: with the shaders off the plate is the
    /// game's own way of keeping a name readable, and taking it from somebody
    /// who never saw the problem would be fixing their game for them.
    /// </summary>
    [Fact]
    public void ThePlateIsKept_WhereNoShaderIsRunning()
    {
        var service = ReadRepositoryFile("Program", "PortableIdentityAdapterService.cs");
        var transformer = ReadRepositoryFile(
            "Program", "IdentityAdapters", "Minecraft-1.21.1-NeoForge", "PortableIdentityTransformer.java");

        // The launcher asks Iris what it is about to do, per launch.
        Assert.Contains("iris.properties", service, StringComparison.Ordinal);
        Assert.Contains("enableShaders", service, StringComparison.Ordinal);
        Assert.Contains("nameTagPlateEnabled=", service, StringComparison.Ordinal);
        // And the adapter does nothing until it is told.
        Assert.Contains("nameTagPlateEnabled", transformer, StringComparison.Ordinal);
        Assert.Contains("\"false\"", transformer, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            current = current.Parent;
        }

        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(relativeParts)}");
    }
}
