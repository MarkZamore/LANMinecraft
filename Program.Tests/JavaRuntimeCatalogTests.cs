using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Which Java a pack runs on.
///
/// It used to be one answer for everybody - Java 21, whatever the pack was -
/// and that is wrong in both directions. A 1.18.2 pack is built against 17 and
/// its loader and libraries are of that era; a mod may declare a Java bound and
/// NeoForge answers a bound it cannot satisfy by refusing that one mod, which
/// arrives as an unrelated crash somewhere else entirely. So the version is a
/// property of the pack's Minecraft, taken from what Mojang says that Minecraft
/// wants.
/// </summary>
public sealed class JavaRuntimeCatalogTests
{
    /// <summary>
    /// The boundaries, and the packs this launcher actually carries. Mojang's
    /// own version manifest names the feature release for each: 17 for 1.18.2
    /// and 1.20.1, 21 from 1.20.5 onward.
    /// </summary>
    [Theory]
    [InlineData("1.21.1", 21)]   // Limitless 8, All The Mods 10
    [InlineData("1.20.1", 17)]   // RPG Ars Nouveau
    [InlineData("1.18.2", 17)]   // All The Fabric 3
    [InlineData("1.20.5", 21)]
    [InlineData("1.20.4", 17)]
    [InlineData("1.21", 21)]
    [InlineData("1.17", 17)]
    [InlineData("1.16.5", 8)]
    [InlineData("1.12.2", 8)]
    [InlineData("26.1", 25)]
    public void AMinecraftAsksForTheJavaItWasBuiltAgainst(string minecraftVersion, int expected)
    {
        Assert.Equal(expected, JavaRuntimeCatalog.MajorVersionFor(minecraftVersion));
    }

    /// <summary>
    /// What a prepared runtime is remembered under is the pack's Java, and for
    /// half the packs that is not the one the launcher pins for itself.
    ///
    /// Anything deciding whether a prepared runtime may be reused has to ask
    /// this catalogue, not that constant. Comparing against the constant can
    /// never match a pack built on 17, so every launch of one threw away a good
    /// runtime and built it again out of Mojang's metadata - slow while those
    /// hosts answer, and a pack that will not start once they do not.
    /// </summary>
    [Theory]
    [InlineData("1.18.2")]   // All The Fabric 3
    [InlineData("1.20.1")]   // RPG Ars Nouveau
    public void APackOnAnOlderJavaIsNotTheOneTheLauncherPins(string minecraftVersion)
    {
        var required = JavaRuntimeCatalog.ForMajorVersion(
            JavaRuntimeCatalog.MajorVersionFor(minecraftVersion));

        Assert.NotNull(required);
        Assert.NotEqual(PortableJavaRuntimeService.PinnedRuntimeId, required!.RuntimeId);
    }

    /// <summary>And where they do agree, they agree for a reason, not by luck.</summary>
    [Fact]
    public void APackOnTheCurrentJavaIsTheOneTheLauncherPins()
    {
        var required = JavaRuntimeCatalog.ForMajorVersion(JavaRuntimeCatalog.MajorVersionFor("1.21.1"));

        Assert.NotNull(required);
        Assert.Equal(PortableJavaRuntimeService.PinnedRuntimeId, required!.RuntimeId);
    }

    /// <summary>
    /// A version nobody can read is not a reason to refuse a launch: it gets
    /// what every pack got before any of this existed.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.21.1-pre2")]
    [InlineData("snapshot")]
    public void AVersionNobodyCanRead_GetsTheDefault(string? minecraftVersion)
    {
        Assert.Equal(
            JavaRuntimeCatalog.DefaultMajorVersion,
            JavaRuntimeCatalog.MajorVersionFor(minecraftVersion));
    }

    /// <summary>
    /// And every answer is a runtime that is actually pinned, with bytes behind
    /// it - a mapping that named a version nothing could install would fail at
    /// the one moment it matters.
    /// </summary>
    [Theory]
    [InlineData("1.21.1")]
    [InlineData("1.20.1")]
    [InlineData("1.18.2")]
    [InlineData("1.16.5")]
    [InlineData("26.1")]
    [InlineData("not a version")]
    public void EveryAnswerIsARuntimeTheLauncherCanInstall(string minecraftVersion)
    {
        var descriptor = new PackRuntimeDescriptor(
            1,
            minecraftVersion,
            new PackLoaderDescriptor(PackLoaderKind.NeoForge, "any"),
            "client.jar",
            "hash");

        var pin = JavaRuntimeCatalog.RequiredFor(descriptor);

        Assert.NotNull(pin);
        Assert.Contains(pin, JavaRuntimeCatalog.Releases);
        Assert.Equal(64, pin.ArchiveSha256.Length);
        Assert.True(pin.ArchiveSizeBytes > 0);
        Assert.EndsWith("/", pin.ArchiveRootPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// No two runtimes may share an install folder or a cache folder, or one
    /// would be installed over another and the sweep would take the survivor.
    /// </summary>
    [Fact]
    public void EveryRuntimeKeepsItsOwnFolders()
    {
        var installs = JavaRuntimeCatalog.Releases.Select(r => r.InstallDirectoryName).ToList();
        var caches = JavaRuntimeCatalog.Releases.Select(r => r.RuntimeId).ToList();

        Assert.Equal(installs.Count, installs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(caches.Count, caches.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(installs.Count, JavaRuntimeCatalog.InstallDirectoryNames.Count);
        Assert.Equal(caches.Count, JavaRuntimeCatalog.CacheDirectoryNames.Count);
    }
}
