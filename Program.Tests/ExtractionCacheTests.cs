using Minecraft;

namespace Minecraft.Tests;

public sealed class ExtractionCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "minecraft-extraction-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void Current_and_other_running_bundles_are_reused_and_stale_bundles_are_removed()
    {
        var current = CreateBundle("current");
        var other = CreateBundle("other-running");
        var stale = CreateBundle("old");

        LogCleanupService.PruneExtractionCache(_root, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            current, other
        });

        Assert.True(File.Exists(Path.Combine(current, "native.dll")));
        Assert.True(File.Exists(Path.Combine(other, "native.dll")));
        Assert.False(Directory.Exists(stale));
        LogCleanupService.PruneExtractionCache(_root, new HashSet<string> { current, other });
        Assert.True(Directory.Exists(current));
    }

    [Fact]
    public void An_unreadable_running_process_means_no_cache_is_removed()
    {
        var bundle = CreateBundle("unverified");

        LogCleanupService.PruneExtractionCache(_root, directoriesInUse: null);

        Assert.True(File.Exists(Path.Combine(bundle, "native.dll")));
    }

    [Fact]
    public void A_cache_with_no_running_users_is_removed()
    {
        CreateBundle("old");

        LogCleanupService.PruneExtractionCache(_root, new HashSet<string>());

        Assert.False(Directory.Exists(_root));
    }

    private string CreateBundle(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "native.dll"), "native library");
        return directory;
    }
}
