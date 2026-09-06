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

    /// <summary>
    /// Withdrawn on 31 August 2026. A name left in the list is a name the
    /// launcher offers and then cannot fetch, which reads to a player as a
    /// broken download rather than a build that is no longer made.
    /// </summary>
    [Fact]
    public void TheBrokenScriptEnhancedIsNoLongerOffered()
    {
        Assert.Null(PortablePackSyncService.KnownSourceFor("The Broken Script Enhanced"));
        Assert.DoesNotContain(
            PortablePackSyncService.KnownPacks,
            pack => pack.RelativePath.Contains("Broken Script", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Withdrawn on 6 September 2026 at the owner's word, repository and all.
    /// A name left here is a name the launcher offers before the folder
    /// exists, so it would go on offering a build that nothing can fetch.
    /// </summary>
    [Fact]
    public void AllTheFabric3IsNoLongerOffered()
    {
        Assert.Null(PortablePackSyncService.KnownSourceFor("All The Fabric 3"));
        Assert.DoesNotContain(
            PortablePackSyncService.KnownPacks,
            pack => pack.RelativePath.Contains("Fabric", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Withdrawn on 1 September 2026, the same way The Broken Script Enhanced
    /// was: the repository is gone, so a name left in this list would be a name
    /// the launcher offers and then cannot fetch. An instance somebody already
    /// has keeps working - the sync says it could not check for updates and
    /// plays the local copy - but nothing downloads it again.
    /// </summary>
    [Fact]
    public void RpgArsNouveauIsNoLongerOffered()
    {
        Assert.Null(PortablePackSyncService.KnownSourceFor("RPG Ars Nouveau"));
        Assert.DoesNotContain(
            PortablePackSyncService.KnownPacks,
            pack => pack.RelativePath.Contains("RPG", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Withdrawn on 6 September 2026 alongside All The Fabric 3, repository
    /// and all. It was offered under a short name because the build list is
    /// one narrow column and "Create &amp; Ars Arcane Awakened" was cut off in
    /// it; neither name answers for anything now.
    /// </summary>
    [Fact]
    public void CreateAndArsIsNoLongerOffered()
    {
        Assert.Null(PortablePackSyncService.KnownSourceFor("C&A Arcane Awakened"));
        Assert.DoesNotContain(
            PortablePackSyncService.KnownPacks,
            pack => pack.RelativePath.Contains("Arcane", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// And the long name it was offered under for one release answers for
    /// nothing: a name in this list is a name the launcher will fetch, and that
    /// repository no longer answers to it.
    /// </summary>
    [Fact]
    public void TheLongCreateAndArsNameIsNotOffered()
    {
        Assert.Null(PortablePackSyncService.KnownSourceFor("Create & Ars Arcane Awakened"));
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
