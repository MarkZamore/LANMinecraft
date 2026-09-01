using System.IO;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The structural sweep. It runs at startup, one directory away from
/// everything a player owns, so most of what is pinned here is what it must
/// not touch rather than what it takes.
///
/// The rule it works by is a whitelist: it looks only inside folders it can
/// name and removes only things it can explain. That rule was written the day
/// three files in a row looked exactly like junk and were not - Mojang's
/// mappings, the window icon's assets, and the asset index whose loss took the
/// language list with it.
/// </summary>
public sealed class StructureCleanupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-structure-{Guid.NewGuid():N}");

    private readonly AppPaths _paths;

    public StructureCleanupServiceTests()
    {
        _paths = new AppPaths(_root);
        _paths.Ensure();
    }

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

    /// <summary>
    /// A runtime whose build is gone is worth more than the space it takes: its
    /// state file names files in the shared store, and the store keeps alive
    /// whatever any state names. So it pins a whole Minecraft for ever.
    /// </summary>
    [Fact]
    public void ARuntimeForABuildThatIsGone_Goes()
    {
        Pack("LL8 Extended");
        Runtime("LL8 Extended", PackRuntimeService.RuntimeCacheGeneration);
        Runtime("Some Pack Nobody Has", PackRuntimeService.RuntimeCacheGeneration);

        StructureCleanupService.Run(_paths);

        Assert.True(Directory.Exists(Path.Combine(_paths.Runtimes, "LL8 Extended")));
        Assert.False(Directory.Exists(Path.Combine(_paths.Runtimes, "Some Pack Nobody Has")));
    }

    /// <summary>
    /// A build the launcher offers is one press of Play from existing, and its
    /// prepared runtime is the reason that press is quick. Not having its pack
    /// folder yet is not the same as being gone.
    /// </summary>
    [Fact]
    public void ARuntimeForABuildTheLauncherStillOffers_Stays()
    {
        Pack("LL8 Extended");
        var offered = PortablePackSyncService.KnownPacks[0].RelativePath;
        Runtime(offered, PackRuntimeService.RuntimeCacheGeneration);
        Directory.Delete(Path.Combine(_paths.Packs, offered), recursive: true);

        StructureCleanupService.Run(_paths);

        Assert.True(Directory.Exists(Path.Combine(_paths.Runtimes, offered)));
    }

    /// <summary>
    /// An unreadable Packs folder is not a machine with no builds. Acting on a
    /// reading it did not get is how a sweep removes every runtime at once.
    /// </summary>
    [Fact]
    public void WithNoPacksReadableAtAll_NothingIsRemoved()
    {
        Runtime("LL8 Extended", PackRuntimeService.RuntimeCacheGeneration);
        Directory.Delete(_paths.Packs, recursive: true);

        Assert.Equal(0, StructureCleanupService.Run(_paths));
        Assert.True(Directory.Exists(Path.Combine(_paths.Runtimes, "LL8 Extended")));
    }

    /// <summary>
    /// The copies of the game a build kept before the game was shared, which
    /// only an out-of-date state proves are past.
    /// </summary>
    [Fact]
    public void TheGameABuildKeptForItself_GoesOnceItsStateIsOutOfDate()
    {
        Pack("LL8 Extended");
        var runtime = Runtime("LL8 Extended", PackRuntimeService.RuntimeCacheGeneration - 1);
        foreach (var root in new[] { "assets", "libraries", "versions", "runtime", "resources" })
        {
            Write(Path.Combine(runtime, root, "deep", "file.bin"), "x");
        }
        Write(Path.Combine(runtime, "natives", "lwjgl.dll"), "dll");

        StructureCleanupService.Run(_paths);

        foreach (var root in new[] { "assets", "libraries", "versions", "runtime", "resources" })
        {
            Assert.False(Directory.Exists(Path.Combine(runtime, root)), root + " should be gone");
        }
        // Its own things stay: the natives it unpacks, and the state itself.
        Assert.True(File.Exists(Path.Combine(runtime, "natives", "lwjgl.dll")));
        Assert.True(File.Exists(Path.Combine(runtime, ".portable-runtime.json")));
    }

    /// <summary>And a build that is up to date keeps everything it has.</summary>
    [Fact]
    public void ABuildPreparedAgainstTheSharedStore_IsLeftAlone()
    {
        Pack("LL8 Extended");
        var runtime = Runtime("LL8 Extended", PackRuntimeService.RuntimeCacheGeneration);
        Write(Path.Combine(runtime, "natives", "lwjgl.dll"), "dll");

        Assert.Equal(0, StructureCleanupService.Run(_paths));
        Assert.True(File.Exists(Path.Combine(runtime, "natives", "lwjgl.dll")));
    }

    /// <summary>
    /// A state that will not parse is not a state of the wrong generation. That
    /// build prepares again anyway and takes its own copies with it; guessing
    /// here buys nothing and can be wrong.
    /// </summary>
    [Fact]
    public void ARuntimeWithADamagedState_IsLeftAlone()
    {
        Pack("LL8 Extended");
        var runtime = Path.Combine(_paths.Runtimes, "LL8 Extended");
        Write(Path.Combine(runtime, ".portable-runtime.json"), "{ not json");
        Write(Path.Combine(runtime, "assets", "objects", "ab", "file"), "x");

        Assert.Equal(0, StructureCleanupService.Run(_paths));
        Assert.True(File.Exists(Path.Combine(runtime, "assets", "objects", "ab", "file")));
    }

    /// <summary>
    /// Worlds are never touched by anything, and a world whose build is gone is
    /// least of all an orphan: it is the thing the player kept when they said
    /// "только сборку".
    /// </summary>
    [Fact]
    public void WorldsAreNeverTouched_EvenForABuildThatIsGone()
    {
        Pack("LL8 Extended");
        Runtime("Removed Pack", PackRuntimeService.RuntimeCacheGeneration);
        var world = Path.Combine(_paths.Worlds, "Removed Pack", "Дом", "level.dat");
        Write(world, "world");

        StructureCleanupService.Run(_paths);

        Assert.True(File.Exists(world));
        Assert.False(Directory.Exists(Path.Combine(_paths.Runtimes, "Removed Pack")));
    }

    /// <summary>
    /// Everything outside the folders it can name is somebody else's. The sweep
    /// never removes a top-level folder it does not recognise.
    /// </summary>
    [Fact]
    public void WhatItCannotNameIsLeftWhereItIs()
    {
        Pack("LL8 Extended");
        var stranger = Path.Combine(_paths.Service, "SomethingElse", "file.txt");
        var personal = Path.Combine(_paths.Personal, "settings.json");
        Write(stranger, "not ours");
        Write(personal, "{}");

        StructureCleanupService.Run(_paths);

        Assert.True(File.Exists(stranger));
        Assert.True(File.Exists(personal));
    }

    /// <summary>Running it twice takes nothing more and breaks nothing.</summary>
    [Fact]
    public void RunningTwiceIsHarmless()
    {
        Pack("LL8 Extended");
        Runtime("Gone", PackRuntimeService.RuntimeCacheGeneration);

        Assert.Equal(1, StructureCleanupService.Run(_paths));
        Assert.Equal(0, StructureCleanupService.Run(_paths));
    }

    private void Pack(string name) =>
        Write(Path.Combine(_paths.Packs, name, "portable-pack.json"), "{}");

    private string Runtime(string name, int generation)
    {
        var runtime = Path.Combine(_paths.Runtimes, name);
        Write(
            Path.Combine(runtime, ".portable-runtime.json"),
            JsonSerializer.Serialize(new { schemaVersion = generation, files = new { } }));
        return runtime;
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
