using System.IO;

namespace Minecraft.Tests;

public sealed class PackRuntimeFolderMigrationTests : IDisposable
{
    private const string Pack = "Example Pack";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pack-runtime-layout-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void StartupAdoptsAnOrdinaryLegacyRootAndKeepsItsReceiptReadable()
    {
        var legacy = WriteLegacyPack();
        var paths = new AppPaths(_root);
        Assert.Equal(paths.LegacyPackRuntimes, paths.Runtimes);
        Assert.False(Directory.Exists(paths.CanonicalPackRuntimes));

        using var hostGuard = new GuardedFileSystem.DirectoryLocks();
        Assert.True(hostGuard.HoldAncestors(paths.Launcher));
        Assert.True(PackRuntimeFolderMigration.TryApply(paths));
        Assert.False(Directory.Exists(paths.LegacyPackRuntimes));
        Assert.Equal(paths.CanonicalPackRuntimes, paths.Runtimes);
        Assert.Equal("original runtime", File.ReadAllText(Path.Combine(paths.CombineUnderRuntimes(Pack), "owned.dat")));
        Assert.Equal("PackRuntimes/" + Pack + "/owned.dat", paths.ResolvePackRuntimeReceiptPrefix("Runtimes/" + Pack + "/owned.dat"));
        Assert.Equal("Shared/assets/keep.dat", paths.ResolvePackRuntimeReceiptPrefix("Shared/assets/keep.dat"));
        Assert.Equal(Path.Combine(paths.LegacyPackRuntimes, Pack), legacy);
        Assert.True(PackRuntimeFolderMigration.TryApply(new AppPaths(_root)));
    }

    [Fact]
    public void PlayingOrHoldingSharedRuntimeAccessDefersUntilTheNextStartup()
    {
        var legacy = WriteLegacyPack();
        var paths = new AppPaths(_root);

        Assert.False(PackRuntimeFolderMigration.TryApply(paths, mayHaveGame: () => true));
        Assert.Equal(legacy, paths.CombineUnderRuntimes(Pack));
        using (SharedRuntimeGate.TryAcquire(paths) ?? throw new InvalidOperationException("Fixture lock was unavailable."))
            Assert.False(PackRuntimeFolderMigration.TryApply(new AppPaths(_root)));
        Assert.False(Directory.Exists(paths.CanonicalPackRuntimes));

        var nextStartup = new AppPaths(_root);
        Assert.True(PackRuntimeFolderMigration.TryApply(nextStartup));
        Assert.Equal(Path.Combine(nextStartup.CanonicalPackRuntimes, Pack), nextStartup.CombineUnderRuntimes(Pack));
    }

    [Fact]
    public void APopulatedTargetPreservesBothRootsAndFindsPacksInEither()
    {
        var legacy = WriteLegacyPack();
        var paths = new AppPaths(_root);
        var conflicting = Path.Combine(paths.CanonicalPackRuntimes, Pack);
        var canonicalOnly = Path.Combine(paths.CanonicalPackRuntimes, "Another Pack");
        Directory.CreateDirectory(conflicting);
        Directory.CreateDirectory(canonicalOnly);
        File.WriteAllText(Path.Combine(conflicting, "owned.dat"), "different runtime");

        Assert.False(PackRuntimeFolderMigration.TryApply(paths));
        Assert.Equal(legacy, paths.CombineUnderRuntimes(Pack));
        Assert.Equal(canonicalOnly, paths.CombineUnderRuntimes("Another Pack"));
        Assert.Equal("Runtimes/" + Pack + "/owned.dat", paths.ResolvePackRuntimeReceiptPrefix("Runtimes/" + Pack + "/owned.dat"));
        Assert.Equal("PackRuntimes/Another Pack/owned.dat", paths.ResolvePackRuntimeReceiptPrefix("Runtimes/Another Pack/owned.dat"));
        Assert.Equal("original runtime", File.ReadAllText(Path.Combine(legacy, "owned.dat")));
        Assert.Equal("different runtime", File.ReadAllText(Path.Combine(conflicting, "owned.dat")));
    }

    [Fact]
    public void AnEmptyExistingTargetDoesNotBlockAnOtherwiseSafeRename()
    {
        WriteLegacyPack();
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(paths.CanonicalPackRuntimes);

        using var hostGuard = new GuardedFileSystem.DirectoryLocks();
        Assert.True(hostGuard.HoldAncestors(paths.Launcher));
        Assert.True(PackRuntimeFolderMigration.TryApply(paths));
        Assert.False(Directory.Exists(paths.LegacyPackRuntimes));
        Assert.True(File.Exists(Path.Combine(paths.CombineUnderRuntimes(Pack), "owned.dat")));
    }

    [Fact]
    public void LinksInsideTheRuntimeAreNeverTraversedOrRelocated()
    {
        var legacy = WriteLegacyPack();
        var paths = new AppPaths(_root);
        var other = Path.Combine(_root, "unowned");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "keep.dat"), "outside the runtime");
        var link = Path.Combine(legacy, "redirected");
        SavesFolderService.CreateJunction(link, other);

        Assert.False(PackRuntimeFolderMigration.TryApply(paths));
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
        Assert.False(Directory.Exists(paths.CanonicalPackRuntimes));
        Assert.Equal("outside the runtime", File.ReadAllText(Path.Combine(other, "keep.dat")));
    }

    private string WriteLegacyPack()
    {
        var paths = new AppPaths(_root);
        var legacy = Path.Combine(paths.LegacyPackRuntimes, Pack);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "owned.dat"), "original runtime");
        return legacy;
    }

    public void Dispose() => TempTree.Delete(_root);
}
