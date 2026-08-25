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
    public void ATM10IsOffered()
    {
        var source = PortablePackSyncService.KnownSourceFor("ATM10");
        Assert.NotNull(source);
        Assert.Equal("pack-latest", source!.Tag);
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
    [InlineData("atm10")]
    [InlineData("ATM10\\")]
    [InlineData(" ATM10 ")]
    public void SlashesAndCaseDoNotHideAKnownPack(string name)
    {
        Assert.NotNull(PortablePackSyncService.KnownSourceFor(name));
    }
}
