using System.IO;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The pack ships resource packs and says which order they belong in; the game
/// reads its seed options only for an instance that has none, so a player who
/// has already played gets the files and never sees them switched on. The list
/// is applied once per version of itself: after that the choice is the
/// player's, and it survives every later launch.
/// </summary>
public sealed class ResourcePackDefaultsTests
{
    /// <summary>
    /// A pack the build used to ship and ships no longer has its file taken away
    /// by the instance mirror; its line in the selection has to go too, or the
    /// game warns about a missing pack at every start. The player's own packs
    /// were never on the build's list, so they are never touched.
    /// </summary>
    [Fact]
    public void APackTheBuildDropped_ComesOffTheSelection_AndThePlayersOwnStay()
    {
        var root = Path.Combine(Path.GetTempPath(), "ll8-defaults-" + Guid.NewGuid().ToString("N"));
        var packDirectory = Path.Combine(root, "pack");
        var instance = Path.Combine(root, "instance");
        Directory.CreateDirectory(Path.Combine(packDirectory, "launcher"));
        Directory.CreateDirectory(instance);
        var list = Path.Combine(packDirectory, "launcher", "resourcepacks-default.txt");
        var optionsPath = Path.Combine(instance, "options.txt");
        var service = new ResourcePackDefaultsService();
        try
        {
            File.WriteAllText(list, "file/One.zip\n!file/Meme.zip\nfile/Top.zip\n");
            File.WriteAllText(optionsPath, "resourcePacks:[\"vanilla\",\"file/Mine.zip\"]\n");
            service.Apply(packDirectory, instance);
            Assert.Contains("\"file/Mine.zip\",\"file/One.zip\",\"file/Meme.zip\",\"file/Top.zip\"", File.ReadAllText(optionsPath), StringComparison.Ordinal);

            // The build drops the meme pack.
            File.WriteAllText(list, "file/One.zip\nfile/Top.zip\n");
            Assert.True(ResourcePackDefaultsService.NeedsApplying(packDirectory, instance));
            service.Apply(packDirectory, instance);

            var options = File.ReadAllText(optionsPath);
            Assert.DoesNotContain("file/Meme.zip", options, StringComparison.Ordinal);
            Assert.Contains("resourcePacks:[\"vanilla\",\"file/Mine.zip\",\"file/One.zip\",\"file/Top.zip\"]", options, StringComparison.Ordinal);
            Assert.Contains("incompatibleResourcePacks:[]", options, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private const string Wanted = """
        # a pack the game calls outdated still plays
        file/One.zip
        !file/Old.zip
        file/Top.zip
        """;

    [Fact]
    public void ThePacksOrder_WinsOverTheOrderAnInstanceHappensToHave()
    {
        var defaults = ResourcePackDefaultsService.Parse(Wanted);
        var options = "resourcePacks:[\"vanilla\",\"file/Top.zip\",\"mod/somemod:extra\",\"file/One.zip\"]\n";

        var (text, added) = ResourcePackDefaultsService.Select(options, defaults);

        // Everything that is not the pack's keeps its place; the pack's own
        // entries land above them, last line highest, as the pack listed them.
        Assert.Contains(
            "resourcePacks:[\"vanilla\",\"mod/somemod:extra\",\"file/One.zip\",\"file/Old.zip\",\"file/Top.zip\"]",
            text,
            StringComparison.Ordinal);
        Assert.Equal(1, added);
        Assert.Contains("incompatibleResourcePacks:[\"file/Old.zip\"]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstanceThatAlreadyMatches_IsLeftAlone()
    {
        var defaults = ResourcePackDefaultsService.Parse(Wanted);
        var options =
            "resourcePacks:[\"vanilla\",\"file/One.zip\",\"file/Old.zip\",\"file/Top.zip\"]\n" +
            "incompatibleResourcePacks:[\"file/Old.zip\"]\n";

        var (text, added) = ResourcePackDefaultsService.Select(options, defaults);

        Assert.Equal(options, text);
        Assert.Equal(0, added);
    }

    /// <summary>What the player switched off is theirs; the same list never asks twice.</summary>
    [Fact]
    public void APackSwitchedOff_StaysOffAcrossLaunches()
    {
        var root = Path.Combine(Path.GetTempPath(), "ll8-defaults-" + Guid.NewGuid().ToString("N"));
        var packDirectory = Path.Combine(root, "pack");
        var instance = Path.Combine(root, "instance");
        Directory.CreateDirectory(Path.Combine(packDirectory, "launcher"));
        Directory.CreateDirectory(instance);
        File.WriteAllText(Path.Combine(packDirectory, "launcher", "resourcepacks-default.txt"), Wanted);
        var optionsPath = Path.Combine(instance, "options.txt");
        File.WriteAllText(optionsPath, "resourcePacks:[\"vanilla\"]\n");
        var service = new ResourcePackDefaultsService();

        try
        {
            Assert.True(ResourcePackDefaultsService.NeedsApplying(packDirectory, instance));
            Assert.Equal(3, service.Apply(packDirectory, instance));

            // The player opens the game and turns one of them off.
            File.WriteAllText(
                optionsPath,
                "resourcePacks:[\"vanilla\",\"file/One.zip\",\"file/Top.zip\"]\n" +
                "incompatibleResourcePacks:[\"file/Old.zip\"]\n");

            Assert.False(ResourcePackDefaultsService.NeedsApplying(packDirectory, instance));
            Assert.Equal(0, service.Apply(packDirectory, instance));
            Assert.DoesNotContain("file/Old.zip\",\"file/Top.zip", File.ReadAllText(optionsPath), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
