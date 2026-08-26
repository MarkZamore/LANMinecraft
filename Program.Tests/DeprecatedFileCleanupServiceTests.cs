using System.Text.Json;

namespace Minecraft.Tests;

/// <summary>
/// After the update a player's portable folder still holds files from the VPN,
/// voice and shared-runtime eras. They are swept once at startup - but the
/// sweep is one directory away from a player's worlds and screenshots, so what
/// is pinned here is as much what it must not touch as what it removes.
/// </summary>
public sealed class DeprecatedFileCleanupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-deprecated-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            TempTree.Delete(_root);
        }
        catch
        {
        }
    }

    [Fact]
    public void FilesOfRemovedFeatures_AreGone()
    {
        var paths = CreatePaths();
        var deprecated = new[]
        {
            Path.Combine(paths.Personal, "network-peers.json"),
            Path.Combine(paths.Personal, "lan-relay-ports.json"),
            Path.Combine(paths.Launcher, "Start-Minecraft.ps1"),
            Path.Combine(paths.Launcher, "standalone-classpath.txt")
        };
        foreach (var path in deprecated) WriteFile(path, "stale");
        WriteFile(Path.Combine(paths.Personal, "UUID.json"), "kept");
        WriteFile(Path.Combine(paths.Personal, "steam-identity.json"), "kept");
        Directory.CreateDirectory(Path.Combine(paths.Personal, "WaypointSync"));
        Directory.CreateDirectory(Path.Combine(paths.Personal, "VoiceRecordings"));
        Directory.CreateDirectory(Path.Combine(paths.Launcher, "libraries", "net"));

        DeprecatedFileCleanupService.Run(paths);

        Assert.All(deprecated, path => Assert.False(File.Exists(path), path));
        // The two identity records are kept on purpose: nothing reads them, but
        // they are the only local evidence of the profile this machine played
        // as, and they are what anyone would ask for if a player ever came back
        // as the wrong character.
        Assert.True(File.Exists(Path.Combine(paths.Personal, "UUID.json")));
        Assert.True(File.Exists(Path.Combine(paths.Personal, "steam-identity.json")));
        Assert.False(Directory.Exists(Path.Combine(paths.Personal, "WaypointSync")));
        Assert.False(Directory.Exists(Path.Combine(paths.Personal, "VoiceRecordings")));
        Assert.False(Directory.Exists(Path.Combine(paths.Launcher, "libraries")));
    }

    /// <summary>
    /// The infrastructure the launcher still uses, and everything belonging to
    /// the player, survives untouched.
    /// </summary>
    [Fact]
    public void EverythingStillInUse_Survives()
    {
        var paths = CreatePaths();
        var kept = new[]
        {
            paths.SettingsFile,
            paths.SkinRegistryFile,
            paths.WaypointSyncStateFile,
            paths.PackHashesFile,
            paths.WindowPlacementFile,
            paths.LogFile,
            Path.Combine(paths.Worlds, "Chebupeli", "level.dat"),
            Path.Combine(paths.Personal, "Backups", "Chebupeli-2026-08-15", "level.dat"),
            Path.Combine(paths.Personal, "Instances", "Infinity", "options.txt"),
            Path.Combine(paths.BugReports, "20260816-120000-76561198256236531-abcd1234", "README.md")
        };
        foreach (var path in kept) WriteFile(path, "keep");

        DeprecatedFileCleanupService.Run(paths);

        Assert.All(kept, path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public void ComponentCaches_KeepOnlyWhatIsStillPinned()
    {
        var paths = CreatePaths();
        var components = Path.Combine(paths.Launcher, "ManagedComponents");
        // Every loader's build is pinned at once now, so the sweep keeps them all.
        var pinnedBuilds = ManagedComponentService.PinnedCacheFileIds("e4steam")!;
        var pinned = pinnedBuilds.First();
        var runtime = ManagedComponentService.PinnedCacheFileIds("java-runtime")!.Single();
        WriteFile(Path.Combine(components, "e4steam", pinned, "e4steam.jar"), "current");
        WriteFile(Path.Combine(components, "e4steam", "19999", "e4steam-old.jar"), "superseded");
        WriteFile(Path.Combine(components, "java-runtime", runtime, "runtime.zip"), "current");
        WriteFile(Path.Combine(components, "tiptothescales", "1234", "mod.jar"), "no longer pinned");
        // The teleport layer's pins went with the layer itself.
        WriteFile(Path.Combine(components, "ftb-essentials", "7608733", "ftb.jar"), "unpinned");
        WriteFile(Path.Combine(components, "the-bumblezone", "71503", "bumblezone.jar"), "unpinned");
        WriteFile(Path.Combine(components, "oritech", "10205", "oritech.jar"), "unpinned");

        DeprecatedFileCleanupService.Run(paths);

        Assert.True(File.Exists(Path.Combine(components, "e4steam", pinned, "e4steam.jar")));
        Assert.True(File.Exists(Path.Combine(components, "java-runtime", runtime, "runtime.zip")));
        Assert.False(Directory.Exists(Path.Combine(components, "e4steam", "19999")));
        Assert.False(Directory.Exists(Path.Combine(components, "tiptothescales")));
        Assert.False(Directory.Exists(Path.Combine(components, "ftb-essentials")));
        Assert.False(Directory.Exists(Path.Combine(components, "the-bumblezone")));
        Assert.False(Directory.Exists(Path.Combine(components, "oritech")));
    }

    [Fact]
    public void AdapterBuilds_AreKeptForRuntimesThatStillExist()
    {
        var paths = CreatePaths();
        var live = new string('a', 64);
        var orphan = new string('b', 64);
        WriteFile(
            Path.Combine(paths.Runtimes, "Infinity", ".portable-runtime.json"),
            JsonSerializer.Serialize(new { descriptorHash = live }));
        WriteFile(Path.Combine(paths.Launcher, "IdentityAdapters", live, "adapter.jar"), "live");
        WriteFile(Path.Combine(paths.Launcher, "IdentityAdapters", orphan, "adapter.jar"), "orphan");

        DeprecatedFileCleanupService.Run(paths);

        Assert.True(File.Exists(Path.Combine(paths.Launcher, "IdentityAdapters", live, "adapter.jar")));
        Assert.False(Directory.Exists(Path.Combine(paths.Launcher, "IdentityAdapters", orphan)));
    }

    /// <summary>
    /// With no readable runtime state nothing is provably orphaned, so nothing
    /// is removed - a damaged state file must not cost the player a rebuild.
    /// </summary>
    [Fact]
    public void WithoutRuntimeState_NoAdapterBuildIsRemoved()
    {
        var paths = CreatePaths();
        var adapter = Path.Combine(paths.Launcher, "IdentityAdapters", new string('c', 64), "adapter.jar");
        WriteFile(adapter, "unknown");

        DeprecatedFileCleanupService.Run(paths);

        Assert.True(File.Exists(adapter));
    }

    [Fact]
    public void RunningTwice_IsHarmless()
    {
        var paths = CreatePaths();
        WriteFile(Path.Combine(paths.Personal, "network-peers.json"), "stale");

        DeprecatedFileCleanupService.Run(paths);
        DeprecatedFileCleanupService.Run(paths);

        Assert.False(File.Exists(Path.Combine(paths.Personal, "network-peers.json")));
    }

    private AppPaths CreatePaths()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        return paths;
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
