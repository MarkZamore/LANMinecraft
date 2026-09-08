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

    /// <summary>
    /// The builds are offered side by side and each fetches from its own
    /// release. Two entries pointing at one release would pour the same files
    /// into two folders and call them different builds.
    /// </summary>
    [Fact]
    public void EveryOfferedBuildFetchesFromItsOwnRelease()
    {
        var sources = PortablePackSyncService.KnownPacks
            .Select(pack => $"{pack.Source.Owner}/{pack.Source.Repo}/{pack.Source.Tag}")
            .Select(source => source.ToLowerInvariant())
            .ToList();
        Assert.Equal(sources.Count, sources.Distinct().Count());
    }

    /// <summary>
    /// A name the list does not carry is not offered: nothing answers for it,
    /// and a folder called that is a pack of somebody's own as far as the
    /// launcher is concerned. An offered name the launcher cannot fetch reads
    /// to a player as a broken download rather than as a build that is not
    /// made any more.
    /// </summary>
    [Fact]
    public void ANameNobodyKnowsIsNotOffered()
    {
        const string unlisted = "Some Build";

        Assert.Null(PortablePackSyncService.KnownSourceFor(unlisted));
        Assert.DoesNotContain(
            PortablePackSyncService.KnownPacks,
            pack => pack.RelativePath.Equals(unlisted, StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public void SlashesAndCaseDoNotHideAKnownPack()
    {
        var known = PortablePackSyncService.KnownPacks[0].RelativePath;
        string[] written = [known.ToLowerInvariant(), known + "\\", $" {known} "];

        foreach (var name in written)
        {
            Assert.True(PortablePackSyncService.KnownSourceFor(name) is not null, name);
        }
    }
}
