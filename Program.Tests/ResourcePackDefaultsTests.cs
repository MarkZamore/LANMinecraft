using System.IO;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The pack ships resource packs and says which order they belong in; the game
/// reads its seed options only for an instance that has none, so a player who
/// has already played gets the files and never sees them switched on.
///
/// The order is not decoration. Two packs that redraw the same mob differ only
/// in which of them sits higher, so a build whose look depends on that order is
/// a different build once the selection has been dragged about. Every launch
/// puts the pack's own entries back where the pack says, and back on - except
/// the ones the pack marks "?" or "-", whose on and off it hands to the player
/// on purpose.
/// </summary>
public sealed class ResourcePackDefaultsTests
{
    /// <summary>
    /// The "!" mark is not decoration and not free. It puts a pack into the
    /// game's own list of incompatible ones, and the game deselects any pack it
    /// finds there and then judges compatible - which on NeoForge is nearly all
    /// of them. So taking a mark off has to count as a new list, and the mark
    /// itself has to be withdrawn from the game's list, or the pack the player
    /// was given stays dark launch after launch.
    /// </summary>
    [Fact]
    public void TakingTheMarkOff_CountsAsANewList_AndWithdrawsIt()
    {
        var marked = ResourcePackDefaultsService.Parse(string.Join('\n', ["file/One.zip", "!file/Old.zip"]));
        var plain = ResourcePackDefaultsService.Parse(string.Join('\n', ["file/One.zip", "file/Old.zip"]));

        Assert.Equal(marked.Entries, plain.Entries);
        Assert.NotEqual(marked.Sha256, plain.Sha256);
        Assert.Equal(["file/Old.zip"], marked.Incompatible);
        Assert.Empty(plain.Incompatible);

        var (withMark, _) = ResourcePackDefaultsService.Select("resourcePacks:[\"vanilla\"]", marked);
        Assert.Contains("incompatibleResourcePacks:[\"file/Old.zip\"]", withMark, StringComparison.Ordinal);

        var (withoutMark, _) = ResourcePackDefaultsService.Select(withMark, plain);
        Assert.Contains("incompatibleResourcePacks:[]", withoutMark, StringComparison.Ordinal);
        Assert.Contains("\"file/One.zip\",\"file/Old.zip\"", withoutMark, StringComparison.Ordinal);
    }

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
            TempTree.Delete(root);
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

    /// <summary>
    /// A pack the build decides is put back on the next launch. The player can
    /// still switch it off for the session they are in; what they cannot do is
    /// leave the build looking like a different build.
    /// </summary>
    [Fact]
    public void APackSwitchedOff_ComesBackOnTheNextLaunch()
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

            // The list has not changed, but the selection has, and the launch
            // puts it back - in the pack's order, all three of them.
            Assert.False(ResourcePackDefaultsService.NeedsApplying(packDirectory, instance));
            Assert.Equal(1, service.Apply(packDirectory, instance));
            Assert.Contains(
                "\"file/One.zip\",\"file/Old.zip\",\"file/Top.zip\"",
                File.ReadAllText(optionsPath),
                StringComparison.Ordinal);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    /// <summary>
    /// The order is the reason this runs at all: a player who drags the build's
    /// packs about finds them back where the build put them, and their own
    /// packs exactly where they left them.
    /// </summary>
    [Fact]
    public void AReorderedSelection_IsPutBack_AndThePlayersOwnPacksAreNot()
    {
        var defaults = ResourcePackDefaultsService.Parse(
            string.Join('\n', ["file/One.zip", "file/Two.zip", "file/Three.zip"]));
        var options = "resourcePacks:[\"vanilla\",\"file/Three.zip\",\"file/Mine.zip\",\"file/One.zip\",\"file/Two.zip\"]\n";

        var (text, added) = ResourcePackDefaultsService.Select(options, defaults);

        Assert.Equal(0, added);
        Assert.Contains(
            "resourcePacks:[\"vanilla\",\"file/Mine.zip\",\"file/One.zip\",\"file/Two.zip\",\"file/Three.zip\"]",
            text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// "?" hands one entry's on and off to the player and keeps only its place.
    /// The pack still decides where it sits, so switching it off and on again
    /// cannot move it under something it is meant to beat.
    /// </summary>
    [Fact]
    public void AnOptionalPackSwitchedOff_StaysOff_ButKeepsItsPlace()
    {
        var defaults = ResourcePackDefaultsService.Parse(
            string.Join('\n', ["file/Base.zip", "?file/Choice.zip", "file/Top.zip"]));
        Assert.Equal(["file/Choice.zip"], defaults.Optional);
        Assert.Equal(["file/Base.zip", "file/Top.zip"], defaults.Forced);

        // The marker holds the marks too, so an unchanged line reads as offered.
        var offered = defaults.Marked;
        var off = "resourcePacks:[\"vanilla\",\"file/Base.zip\",\"file/Top.zip\"]\n";
        var (text, _) = ResourcePackDefaultsService.Select(off, defaults, null, offered);
        Assert.Contains("resourcePacks:[\"vanilla\",\"file/Base.zip\",\"file/Top.zip\"]", text, StringComparison.Ordinal);

        var on = "resourcePacks:[\"vanilla\",\"file/Choice.zip\",\"file/Base.zip\",\"file/Top.zip\"]\n";
        var (back, _) = ResourcePackDefaultsService.Select(on, defaults, null, offered);
        Assert.Contains(
            "resourcePacks:[\"vanilla\",\"file/Base.zip\",\"file/Choice.zip\",\"file/Top.zip\"]",
            back,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// "-" is the same hand-off shipped off: the alternative rides along in the
    /// folder, in its proper place in the order, and waits for a tick.
    /// </summary>
    [Fact]
    public void AnAlternativeMarkedOff_ArrivesOff_AndCanBeSwitchedOn()
    {
        var defaults = ResourcePackDefaultsService.Parse(
            string.Join('\n', ["file/Base.zip", "?file/Chosen.zip", "-file/Other.zip"]));
        Assert.Equal(["file/Chosen.zip", "file/Other.zip"], defaults.Optional);
        Assert.Equal(["file/Other.zip"], defaults.OptionalOff);

        // Never offered before: the marked one arrives on, the other does not.
        var (first, _) = ResourcePackDefaultsService.Select("resourcePacks:[\"vanilla\"]\n", defaults, null, []);
        Assert.Contains("resourcePacks:[\"vanilla\",\"file/Base.zip\",\"file/Chosen.zip\"]", first, StringComparison.Ordinal);

        // The player swaps them, and the swap survives the next launch.
        var swapped = "resourcePacks:[\"vanilla\",\"file/Base.zip\",\"file/Other.zip\"]\n";
        var (kept, _) = ResourcePackDefaultsService.Select(swapped, defaults, null, defaults.Marked);
        Assert.Contains("resourcePacks:[\"vanilla\",\"file/Base.zip\",\"file/Other.zip\"]", kept, StringComparison.Ordinal);
    }

    /// <summary>A mark is part of the list's identity, the same way "!" is.</summary>
    [Fact]
    public void MarkingAnEntryOptional_CountsAsANewList()
    {
        var forced = ResourcePackDefaultsService.Parse("file/One.zip");
        var optional = ResourcePackDefaultsService.Parse("?file/One.zip");
        var off = ResourcePackDefaultsService.Parse("-file/One.zip");

        Assert.Equal(forced.Entries, optional.Entries);
        Assert.NotEqual(forced.Sha256, optional.Sha256);
        Assert.NotEqual(optional.Sha256, off.Sha256);
    }

    /// <summary>
    /// The build changing its mind about an entry gets one turn to be obeyed.
    ///
    /// An entry the player owns keeps whatever they left it at, which is right
    /// until the build says something new about it. Thicc Villagers was an
    /// ordinary listed pack, so every instance had it on; when it became "-" -
    /// shipped, but off - keeping the player's state would have meant the new
    /// default never arrived anywhere it mattered. The marker records the marks
    /// beside the entry, so a line whose marks changed reads as one this
    /// instance has not been offered, and the default applies once.
    /// </summary>
    [Fact]
    public void AnEntryWhoseMarksChanged_GetsTheNewDefaultOnce()
    {
        var before = ResourcePackDefaultsService.Parse(
            string.Join('\n', ["file/Base.zip", "file/Luigi.zip", "file/Thicc.zip"]));
        var after = ResourcePackDefaultsService.Parse(
            string.Join('\n', ["file/Base.zip", "?file/Luigi.zip", "-file/Thicc.zip"]));

        Assert.Equal(["file/Base.zip", "file/Luigi.zip", "file/Thicc.zip"], before.Marked);
        Assert.Equal(["file/Base.zip", "?file/Luigi.zip", "-file/Thicc.zip"], after.Marked);

        // The instance ran on the old list, so it has all three on.
        var options = "resourcePacks:[\"vanilla\",\"file/Base.zip\",\"file/Luigi.zip\",\"file/Thicc.zip\"]\n";
        var (text, _) = ResourcePackDefaultsService.Select(options, after, null, before.Marked);

        // Luigi stays on: "?" only hands the choice over. Thicc goes off,
        // because "-" is the build saying it ships switched off.
        Assert.Contains(
            "resourcePacks:[\"vanilla\",\"file/Base.zip\",\"file/Luigi.zip\"]",
            text,
            StringComparison.Ordinal);

        // Once offered, the answer is the player's again: switching Thicc on
        // survives the launch after that.
        var swapped = "resourcePacks:[\"vanilla\",\"file/Base.zip\",\"file/Thicc.zip\"]\n";
        var (kept, _) = ResourcePackDefaultsService.Select(swapped, after, null, after.Marked);
        Assert.Contains(
            "resourcePacks:[\"vanilla\",\"file/Base.zip\",\"file/Thicc.zip\"]",
            kept,
            StringComparison.Ordinal);
    }
}
