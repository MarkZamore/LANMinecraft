using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The builds the launcher is willing to fetch. A pack listed here is offered
/// in the window before it exists on disk, so a friend who has never had it can
/// press Play and get it; a pack that is not listed can only be seen by someone
/// who already has its folder.
/// </summary>
public sealed class KnownPackTests
{
    [Fact]
    public void TheBuiltInPackIsTheFirstOne()
    {
        Assert.NotEmpty(PortablePackSyncService.KnownPacks);
        Assert.Equal(
            PortablePackSyncService.DefaultPackRelativePath,
            PortablePackSyncService.KnownPacks[0].RelativePath);
        Assert.Equal(
            PortablePackSyncService.DefaultPackSource,
            PortablePackSyncService.KnownPacks[0].Source);
    }

    [Fact]
    public void EveryKnownPackIsFetchable()
    {
        foreach (var pack in PortablePackSyncService.KnownPacks)
        {
            Assert.False(string.IsNullOrWhiteSpace(pack.RelativePath));
            Assert.False(string.IsNullOrWhiteSpace(pack.Source.Owner));
            Assert.False(string.IsNullOrWhiteSpace(pack.Source.Repo));
            Assert.False(string.IsNullOrWhiteSpace(pack.Source.Tag));
            // The name is a folder under Packs, so it must not try to leave it.
            Assert.DoesNotContain("..", pack.RelativePath, StringComparison.Ordinal);
            Assert.Equal(pack.Source, PortablePackSyncService.KnownSourceFor(pack.RelativePath));
        }
    }

    [Fact]
    public void NoTwoKnownPacksShareAName()
    {
        var names = PortablePackSyncService.KnownPacks
            .Select(pack => pack.RelativePath.ToLowerInvariant())
            .ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void AllTheMods10IsOffered()
    {
        var source = PortablePackSyncService.KnownSourceFor("All The Mods 10");
        Assert.NotNull(source);
        Assert.Equal("MarkZamore", source!.Owner);
        Assert.Equal("All-The-Mods-10", source.Repo);
        Assert.Equal("pack-latest", source.Tag);
    }

    [Fact]
    public void TheBrokenScriptEnhancedIsOffered()
    {
        var source = PortablePackSyncService.KnownSourceFor("The Broken Script Enhanced");
        Assert.NotNull(source);
        Assert.Equal("MarkZamore", source!.Owner);
        Assert.Equal("The-Broken-Script-Enhanced", source.Repo);
        Assert.Equal("pack-latest", source.Tag);
    }

    [Fact]
    public void AllTheFabric3IsOffered()
    {
        var source = PortablePackSyncService.KnownSourceFor("All The Fabric 3");
        Assert.NotNull(source);
        Assert.Equal("MarkZamore", source!.Owner);
        Assert.Equal("All-The-Fabric-3", source.Repo);
        Assert.Equal("pack-latest", source.Tag);
    }

    /// <summary>
    /// The one built for a machine that has nothing to spare: 73 mods on
    /// 1.20.1, which is the only pack here whose room beside the heap leaves a
    /// three gigabyte heap inside what an eight gigabyte laptop can be asked
    /// for.
    /// </summary>
    [Fact]
    public void RpgArsNouveauIsOffered()
    {
        var source = PortablePackSyncService.KnownSourceFor("RPG Ars Nouveau");
        Assert.NotNull(source);
        Assert.Equal("MarkZamore", source!.Owner);
        Assert.Equal("RPG-Ars-Nouveau", source.Repo);
        Assert.Equal("pack-latest", source.Tag);
    }

    /// <summary>
    /// The names that were dropped are dropped: nothing answers for them, and a
    /// folder still called one of them is a pack of somebody's own as far as
    /// the launcher is concerned.
    /// </summary>
    [Theory]
    [InlineData("ATM10")]
    [InlineData("E10")]
    public void ARetiredNameIsNotOffered(string retired)
    {
        Assert.Null(PortablePackSyncService.KnownSourceFor(retired));
    }

    [Fact]
    public void ACustomPackHasNoSource()
    {
        Assert.Null(PortablePackSyncService.KnownSourceFor("SomebodysOwnPack"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingIsNotAPack(string? name)
    {
        Assert.Null(PortablePackSyncService.KnownSourceFor(name));
    }

    [Theory]
    [InlineData("all the mods 10")]
    [InlineData("All The Mods 10\\")]
    [InlineData(" All The Mods 10 ")]
    public void SlashesAndCaseDoNotHideAKnownPack(string name)
    {
        Assert.NotNull(PortablePackSyncService.KnownSourceFor(name));
    }
}
