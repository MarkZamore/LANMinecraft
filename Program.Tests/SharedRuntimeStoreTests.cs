using System.IO;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The game is downloaded once and every build runs from that one copy, so the
/// store has no owner: a file is alive while any build's runtime state names
/// it, and garbage the moment the last one stops.
///
/// The whole risk is in the arithmetic of that union. Under-count it and the
/// sweep deletes the game out from under a build that is still installed;
/// over-count it and nothing is ever freed. So what is pinned here is mostly
/// the ways the count could go wrong.
/// </summary>
public sealed class SharedRuntimeStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-shared-{Guid.NewGuid():N}");

    private readonly AppPaths _paths;

    public SharedRuntimeStoreTests()
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

    /// <summary>Two builds on one version, one removed: the shared copy stays.</summary>
    [Fact]
    public void WhatAnotherBuildStillNeeds_Stays()
    {
        var shared = Asset("assets/objects/ab/abcdef");
        Build("LL8 Extended", shared);
        Build("C&A Arcane Awakened", shared);

        Assert.Equal(0, SharedRuntimeStore.Sweep(_paths));
        Assert.True(File.Exists(shared));

        // And now the last one of the two goes.
        Directory.Delete(Path.Combine(_paths.Runtimes, "C&A Arcane Awakened"), recursive: true);
        Assert.Equal(0, SharedRuntimeStore.Sweep(_paths));
        Assert.True(File.Exists(shared));

        Directory.Delete(Path.Combine(_paths.Runtimes, "LL8 Extended"), recursive: true);
    }

    /// <summary>And when the last build that named it is gone, it goes.</summary>
    [Fact]
    public void WhatNobodyNeedsAnyMore_Goes()
    {
        var kept = Asset("assets/objects/ab/kept");
        var orphan = Asset("libraries/net/example/old/1.0/old-1.0.jar");
        Build("LL8 Extended", kept);
        Build("RPG Ars Nouveau", orphan);

        Directory.Delete(Path.Combine(_paths.Runtimes, "RPG Ars Nouveau"), recursive: true);
        var removed = SharedRuntimeStore.Sweep(_paths);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(kept));
        // The empty shelves it stood on go with it.
        Assert.False(Directory.Exists(Path.Combine(_paths.SharedRuntime, "libraries", "net")));
    }

    /// <summary>
    /// With every build gone the store is empty rather than merely swept: this
    /// is the case the player asked for by name, and the one where getting it
    /// wrong is invisible - a gigabyte that stays for ever.
    /// </summary>
    [Fact]
    public void WithTheLastBuildGone_TheStoreIsEmptied()
    {
        var a = Asset("assets/objects/ab/one");
        var b = Asset("versions/1.21.1/1.21.1.jar");
        Build("Only Build", a, b);

        Directory.Delete(Path.Combine(_paths.Runtimes, "Only Build"), recursive: true);

        Assert.Equal(2, SharedRuntimeStore.Sweep(_paths));
        Assert.Empty(Directory.GetFiles(_paths.SharedRuntime, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// A state that cannot be read is not an empty state. Treating it as one
    /// would delete the game for a build that is perfectly installed, so the
    /// sweep stops instead - a damaged file costs one build a rebuild, never
    /// every build at once.
    /// </summary>
    [Fact]
    public void ADamagedStateStopsTheSweepEntirely()
    {
        var shared = Asset("assets/objects/ab/abcdef");
        Build("LL8 Extended", shared);
        var orphan = Asset("assets/objects/cd/orphan");
        Build("Broken", orphan);
        File.WriteAllText(
            Path.Combine(_paths.Runtimes, "Broken", ".portable-runtime.json"), "{ not json");

        Assert.Equal(0, SharedRuntimeStore.Sweep(_paths));
        Assert.True(File.Exists(shared));
        Assert.True(File.Exists(orphan));
    }

    /// <summary>
    /// No builds at all means nothing is used by anything, so the store goes -
    /// which is not the same as a build whose state could not be read, and the
    /// test above is the one that keeps the two apart.
    /// </summary>
    [Fact]
    public void WithNoBuildsAtAll_TheStoreGoes()
    {
        var asset = Asset("assets/objects/ab/abcdef");

        Assert.Equal(1, SharedRuntimeStore.Sweep(_paths));
        Assert.False(File.Exists(asset));
    }

    /// <summary>Running it twice frees nothing more and breaks nothing.</summary>
    [Fact]
    public void SweepingTwice_IsHarmless()
    {
        var orphan = Asset("assets/objects/ab/orphan");
        Build("LL8 Extended", Asset("assets/objects/cd/kept"));

        Assert.Equal(1, SharedRuntimeStore.Sweep(_paths));
        Assert.Equal(0, SharedRuntimeStore.Sweep(_paths));
        Assert.False(File.Exists(orphan));
    }

    /// <summary>
    /// The store is under Launcher, and a state's paths are relative to
    /// Launcher - so a build's own files are named in the same list and must
    /// never be mistaken for something in the store.
    /// </summary>
    [Fact]
    public void ABuildsOwnFilesAreNotInTheStoreAndAreNotSwept()
    {
        var natives = Path.Combine(_paths.Runtimes, "LL8 Extended", "natives", "lwjgl.dll");
        Write(natives, "dll");
        Build("LL8 Extended", Asset("assets/objects/ab/kept"), natives);

        SharedRuntimeStore.Sweep(_paths);

        Assert.True(File.Exists(natives));
    }

    /// <summary>Every shared root hangs off one folder, which is where it is.</summary>
    [Fact]
    public void TheRootsAreWhereTheLauncherPutsThem()
    {
        var shared = Path.Combine(_paths.Launcher, "Shared");
        Assert.Equal(shared, _paths.SharedRuntime);
        Assert.Equal(Path.Combine(shared, "assets"), SharedRuntimeStore.Assets(_paths));
        Assert.Equal(Path.Combine(shared, "libraries"), SharedRuntimeStore.Libraries(_paths));
        Assert.Equal(Path.Combine(shared, "versions"), SharedRuntimeStore.Versions(_paths));
        Assert.Equal(Path.Combine(shared, "runtime"), SharedRuntimeStore.Runtime(_paths));
        Assert.Equal(Path.Combine(shared, "resources"), SharedRuntimeStore.Resources(_paths));
        Assert.Equal(Path.GetFullPath(_paths.Launcher), SharedRuntimeStore.Anchor(_paths));
    }

    /// <summary>Writes a file into the shared store and answers with its path.</summary>
    private string Asset(string relativePath)
    {
        var full = Path.Combine(_paths.SharedRuntime, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Write(full, relativePath);
        return full;
    }

    /// <summary>A build whose runtime state names exactly these files.</summary>
    private void Build(string name, params string[] files)
    {
        var anchor = SharedRuntimeStore.Anchor(_paths);
        var listed = files.ToDictionary(
            path => Path.GetRelativePath(anchor, path).Replace('\\', '/'),
            path => new { sizeBytes = new FileInfo(path).Length });
        Write(
            Path.Combine(_paths.Runtimes, name, ".portable-runtime.json"),
            JsonSerializer.Serialize(new { schemaVersion = 4, files = listed }));
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
