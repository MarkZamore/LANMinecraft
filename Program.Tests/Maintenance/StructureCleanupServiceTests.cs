using System.IO;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Only a library-owned runtime with obsolete state may lose old private copies.
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

    [Fact]
    public void ARuntimeWithNoProofOfLibraryOwnershipStays()
    {
        Pack("Some Build");
        Runtime("Some Build", PackRuntimeService.RuntimeCacheGeneration);
        Runtime("Some Pack Nobody Has", PackRuntimeService.RuntimeCacheGeneration);

        StructureCleanupService.Run(_paths);

        Assert.True(Directory.Exists(Path.Combine(_paths.Runtimes, "Some Build")));
        Assert.True(Directory.Exists(Path.Combine(_paths.Runtimes, "Some Pack Nobody Has")));
    }

    /// <summary>
    /// A build the launcher offers is one press of Play from existing, and its
    /// prepared runtime is the reason that press is quick. Not having its pack
    /// folder yet is not the same as being gone.
    /// </summary>
    [Fact]
    public void ARuntimeForABuildTheLauncherStillOffers_Stays()
    {
        var offered = PortablePackSyncService.KnownPacks[0].RelativePath;
        Pack(offered);
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
        Runtime("Some Build", PackRuntimeService.RuntimeCacheGeneration);
        Directory.Delete(_paths.Packs, recursive: true);

        Assert.Equal(0, StructureCleanupService.Run(_paths));
        Assert.True(Directory.Exists(Path.Combine(_paths.Runtimes, "Some Build")));
    }

    /// <summary>
    /// The copies of the game a build kept before the game was shared, which
    /// only an out-of-date state proves are past.
    /// </summary>
    [Fact]
    public void TheGameABuildKeptForItself_GoesOnceItsStateIsOutOfDate()
    {
        Pack("Some Build");
        var runtime = Runtime("Some Build", PackRuntimeService.RuntimeCacheGeneration - 1);
        foreach (var root in new[] { "assets", "libraries", "versions", "runtime", "resources" })
        {
            Write(Path.Combine(runtime, root, "deep", "file.bin"), "x");
        }
        Write(Path.Combine(runtime, "natives", "lwjgl.dll"), "dll");

        var personal = Runtime("Personal build", PackRuntimeService.RuntimeCacheGeneration - 1);
        var personalAsset = Path.Combine(personal, "assets", "file.bin");
        Write(personalAsset, "personal");
        StructureCleanupService.Run(_paths, isManagedPack: name => name == "Some Build");

        foreach (var root in new[] { "assets", "libraries", "versions", "runtime", "resources" })
        {
            Assert.False(Directory.Exists(Path.Combine(runtime, root)), root + " should be gone");
        }
        // Its own things stay: the natives it unpacks, and the state itself.
        Assert.True(File.Exists(Path.Combine(runtime, "natives", "lwjgl.dll")));
        Assert.True(File.Exists(Path.Combine(runtime, ".portable-runtime.json")));
        Assert.True(File.Exists(personalAsset));
    }

    /// <summary>A current runtime keeps a referenced private copy while unused copies are collected.</summary>
    [Fact]
    public void ABuildPreparedAgainstTheSharedStore_IsLeftAlone()
    {
        Pack("Some Build");
        var runtime = Runtime("Some Build", PackRuntimeService.RuntimeCacheGeneration);
        var tracked = Path.Combine(runtime, "assets", "objects", "tracked");
        Write(tracked, "current");
        Write(Path.Combine(runtime, "versions", "obsolete.json"), "old");
        Write(Path.Combine(runtime, ".portable-runtime.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = PackRuntimeService.RuntimeCacheGeneration,
            files = new Dictionary<string, object> { ["Runtimes/Some Build/assets/objects/tracked"] = new { } }
        }));

        Assert.True(StructureCleanupService.Run(_paths, isManagedPack: _ => true) > 0);
        Assert.True(File.Exists(tracked));
        Assert.False(Directory.Exists(Path.Combine(runtime, "versions")));
    }

    /// <summary>
    /// A state that will not parse is not a state of the wrong generation. That
    /// build prepares again anyway and takes its own copies with it; guessing
    /// here buys nothing and can be wrong.
    /// </summary>
    [Fact]
    public void ARuntimeWithADamagedState_IsLeftAlone()
    {
        Pack("Some Build");
        var runtime = Path.Combine(_paths.Runtimes, "Some Build");
        Write(Path.Combine(runtime, ".portable-runtime.json"), "{ not json");
        Write(Path.Combine(runtime, "assets", "objects", "ab", "file"), "x");

        Assert.Equal(0, StructureCleanupService.Run(_paths, isManagedPack: _ => true));
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
        Pack("Some Build");
        Runtime("Removed Pack", PackRuntimeService.RuntimeCacheGeneration);
        var world = Path.Combine(_paths.Worlds, "Removed Pack", "Дом", "level.dat");
        Write(world, "world");

        StructureCleanupService.Run(_paths);

        Assert.True(File.Exists(world));
        Assert.True(Directory.Exists(Path.Combine(_paths.Runtimes, "Removed Pack")));
    }

    /// <summary>
    /// Everything outside the folders it can name is somebody else's. The sweep
    /// never removes a top-level folder it does not recognise.
    /// </summary>
    [Fact]
    public void WhatItCannotNameIsLeftWhereItIs()
    {
        Pack("Some Build");
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
        Pack("Some Build");
        var runtime = Runtime("Some Build", PackRuntimeService.RuntimeCacheGeneration - 1);
        Write(Path.Combine(runtime, "assets", "old"), "archive");

        Assert.True(StructureCleanupService.Run(_paths, isManagedPack: name => name == "Some Build") > 0);
        Assert.Equal(0, StructureCleanupService.Run(_paths, isManagedPack: name => name == "Some Build"));
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
