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
        Build("Some Build", shared);
        Build("Another Build", shared);

        Assert.Equal(0, SharedRuntimeStore.Sweep(_paths));
        Assert.True(File.Exists(shared));

        // And now the last one of the two goes.
        Directory.Delete(Path.Combine(_paths.Runtimes, "Another Build"), recursive: true);
        Assert.Equal(0, SharedRuntimeStore.Sweep(_paths));
        Assert.True(File.Exists(shared));

        Directory.Delete(Path.Combine(_paths.Runtimes, "Some Build"), recursive: true);
    }

    /// <summary>And when the last build that named it is gone, it goes.</summary>
    [Fact]
    public void WhatNobodyNeedsAnyMore_Goes()
    {
        var kept = Asset("assets/objects/ab/kept");
        var orphan = Asset("assets/objects/cd/orphan");
        Build("Some Build", kept);
        Build("Another Build", orphan);

        Directory.Delete(Path.Combine(_paths.Runtimes, "Another Build"), recursive: true);
        var removed = SharedRuntimeStore.Sweep(_paths);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(kept));
        // The empty shelf it stood on goes with it.
        Assert.False(Directory.Exists(Path.Combine(_paths.SharedRuntime, "assets", "objects", "cd")));
    }

    /// <summary>
    /// The loader's own jars survive a sweep in which nothing names them, and
    /// every state on the disk is current.
    /// </summary>
    /// <remarks>
    /// This is the shape of the bug, exactly: NeoForge 21.1 names
    /// net.neoforged:neoforge nowhere in its profile - not in libraries, not on
    /// the module path - so no amount of reading that json puts the jar in a
    /// keep-set, and the sweep that trusts the keep-set takes it. It happened
    /// twice on one install. The test that was here before asserted the
    /// opposite and passed, because its fixture only ever held jars a state had
    /// named.
    /// </remarks>
    [Fact]
    public void TheLoadersOwnJars_SurviveEvenThoughNoStateNamesThem()
    {
        Build("Some Build", Asset("assets/objects/ab/kept"));
        var client = Asset("libraries/net/neoforged/neoforge/21.1.248/neoforge-21.1.248-client.jar");
        var universal = Asset("libraries/net/neoforged/neoforge/21.1.248/neoforge-21.1.248-universal.jar");
        var natives = Asset("versions/neoforge-21.1.248/natives/lwjgl.dll");
        var orphanAsset = Asset("assets/objects/cd/orphan");

        Assert.Equal(1, SharedRuntimeStore.Sweep(_paths));
        Assert.False(File.Exists(orphanAsset), "assets are still swept");
        foreach (var path in new[] { client, universal, natives })
        {
            Assert.True(File.Exists(path), path + " is needed and named by nothing");
        }
    }

    /// <summary>
    /// A library or a profile nobody uses stays too. That is the price, and it
    /// is the right way round: these two roots are the small half of the store.
    /// </summary>
    [Fact]
    public void ALibraryNoBuildUses_StaysAnyway()
    {
        Build("Up To Date", Asset("assets/objects/ab/kept"));
        var orphanLoader = Asset("libraries/net/example/gone/1.0/gone-1.0.jar");
        var orphanProfile = Asset("versions/gone/gone.json");

        Assert.Equal(0, SharedRuntimeStore.Sweep(_paths));
        Assert.True(File.Exists(orphanLoader));
        Assert.True(File.Exists(orphanProfile));
    }

    /// <summary>
    /// What a build names is kept whatever root it is in.
    /// </summary>
    [Fact]
    public void TheLoadersOwnFiles_AreKeptWhenABuildNamesThem()
    {
        var loader = Asset("libraries/net/neoforged/neoforge/21.1.248/neoforge-21.1.248-client.jar");
        var mappings = Asset("libraries/net/minecraft/client/1.21.1/client-1.21.1-mappings.txt");
        var profile = Asset("versions/neoforge-21.1.248/neoforge-21.1.248.json");
        Build("Some Build", Asset("assets/objects/ab/kept"), loader, mappings, profile);
        // Never swept at all: the sweep is told which roots it may take from.
        var installer = Asset("installers/neoforge/21.1.248/neoforge-21.1.248-installer.jar");

        Assert.Equal(0, SharedRuntimeStore.Sweep(_paths));
        foreach (var path in new[] { loader, mappings, profile, installer })
        {
            Assert.True(File.Exists(path), path + " must not be swept");
        }
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
        var b = Asset("runtime/windows-x64/java-runtime-delta/bin/java.exe");
        Build("Only Build", a, b);

        Directory.Delete(Path.Combine(_paths.Runtimes, "Only Build"), recursive: true);

        Assert.Equal(2, SharedRuntimeStore.Sweep(_paths));
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
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
        Build("Some Build", shared);
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
        Build("Some Build", Asset("assets/objects/cd/kept"));

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
        var natives = Path.Combine(_paths.Runtimes, "Some Build", "natives", "lwjgl.dll");
        Write(natives, "dll");
        Build("Some Build", Asset("assets/objects/ab/kept"), natives);

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

    [Fact]
    public void OwnedProfilesStayUntilTheirInstalledPackAndRuntimeReferencesAreGone()
    {
        const string baseId = "1.20.1";
        const string loaderId = "1.20.1-forge-47.4.0";
        var baseProfile = Asset($"versions/{baseId}/{baseId}.json");
        var loaderProfile = Asset($"versions/{loaderId}/{loaderId}.json");
        var natives = Asset($"versions/{loaderId}/natives/lwjgl.dll");
        var receipt = Asset($"versions/{loaderId}/.portable-loader.json");
        File.WriteAllText(baseProfile, "{}");
        File.WriteAllText(loaderProfile, """{"inheritsFrom":"1.20.1"}""");
        var manifest = Path.Combine(_paths.Packs, "New", PackManifestService.ManifestFileName);
        Write(manifest, """{"schemaVersion":1,"minecraftVersion":"1.20.1","loader":{"type":"forge","version":"1.20.1-47.4.0"}}""");
        var descriptor = new PackRuntimeDescriptor(1, baseId,
            new PackLoaderDescriptor(PackLoaderKind.Forge, "47.4.0"), "", "");
        SharedRuntimeStore.RecordProfileOwnership(_paths, baseId, descriptor, [baseProfile]);
        SharedRuntimeStore.RecordProfileOwnership(_paths, loaderId, descriptor, [loaderProfile, natives]);

        Assert.Equal(0, SharedRuntimeStore.Sweep(_paths));
        Assert.True(File.Exists(baseProfile));
        Assert.True(File.Exists(loaderProfile));

        File.Delete(manifest);
        Build("First", loaderProfile);
        Build("Second", loaderProfile);
        Directory.Delete(Path.Combine(_paths.Runtimes, "First"), recursive: true);
        Assert.Equal(0, SharedRuntimeStore.Sweep(_paths));
        Assert.True(File.Exists(baseProfile), "The old state only names the child profile.");
        Assert.True(File.Exists(natives));
        Assert.True(File.Exists(receipt));

        Directory.Delete(Path.Combine(_paths.Runtimes, "Second"), recursive: true);
        Assert.Equal(6, SharedRuntimeStore.Sweep(_paths));
        Assert.False(Directory.Exists(Path.GetDirectoryName(baseProfile)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(loaderProfile)));
    }

    [Fact]
    public void UnmarkedAndModifiedProfilesArePreserved()
    {
        var unmarked = Asset("versions/custom/custom.json");
        var profile = Asset("versions/1.20.1/1.20.1.json");
        var client = Asset("versions/1.20.1/1.20.1.jar");
        var descriptor = new PackRuntimeDescriptor(1, "1.20.1",
            new PackLoaderDescriptor(PackLoaderKind.Vanilla, null), "", "");
        SharedRuntimeStore.RecordProfileOwnership(_paths, descriptor.MinecraftVersion, descriptor, [profile, client]);
        File.WriteAllText(profile, """{"id":"modified"}""");

        Assert.Equal(0, SharedRuntimeStore.Sweep(_paths));
        Assert.True(File.Exists(unmarked));
        Assert.True(File.Exists(profile));
        Assert.True(File.Exists(client));
    }

    [Fact]
    public void APreparingRuntimeOrALiveGameDefersCleanup()
    {
        var profile = Asset("versions/1.20.1/1.20.1.json");
        var descriptor = new PackRuntimeDescriptor(1, "1.20.1",
            new PackLoaderDescriptor(PackLoaderKind.Vanilla, null), "", "");
        SharedRuntimeStore.RecordProfileOwnership(_paths, descriptor.MinecraftVersion, descriptor, [profile]);

        using (Assert.IsType<FileStream>(SharedRuntimeGate.TryAcquire(_paths)))
            Assert.Equal(0, SharedRuntimeStore.Sweep(_paths));
        Assert.True(File.Exists(profile));

        Assert.Equal(0, SharedRuntimeStore.Sweep(_paths, logger: null, hasLiveGame: () => true));
        Assert.True(File.Exists(profile));
        Assert.Equal(2, SharedRuntimeStore.Sweep(_paths));
    }

    [Fact]
    public void IdleInventoryYieldsItsLeaseAndStopsAfterForegroundActivity()
    {
        var orphan = Asset("assets/objects/ab/orphan");
        var checkpoints = 0;

        Assert.Equal(0, SharedRuntimeStore.SweepIdle(_paths, null, () =>
        {
            using var lease = SharedRuntimeGate.TryAcquire(_paths);
            Assert.NotNull(lease);
            if (++checkpoints == 4) SharedRuntimeGate.MarkActivity(_paths);
        }, hasLiveGame: static () => false));
        Assert.Equal(4, checkpoints);
        Assert.True(File.Exists(orphan));

        Assert.Equal(1, SharedRuntimeStore.SweepIdle(_paths, null, static () => { },
            hasLiveGame: static () => false));
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public void IdleSweepKeepsCachedAssetsWhileAnInstalledPackHasNoPreparedState()
    {
        var orphan = Asset("assets/objects/ab/orphan");
        var active = Asset("assets/objects/ab/active");
        var pack = _paths.CombineUnderPacks("Some Build");
        Write(Path.Combine(pack, PackManifestService.ManifestFileName),
            """{"schemaVersion":1,"minecraftVersion":"1.20.1","loader":{"type":"vanilla"}}""");
        var descriptor = PackManifestService.Load(pack);

        Assert.Equal(0, SharedRuntimeStore.SweepIdle(_paths, null, static () => { },
            hasLiveGame: static () => false));
        Assert.True(File.Exists(orphan));

        Write(Path.Combine(_paths.Runtimes, "Some Build", ".portable-runtime.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = PackRuntimeService.RuntimeCacheGeneration,
                profileId = "1.20.1",
                descriptorHash = descriptor.DescriptorHash,
                files = new Dictionary<string, object>
                {
                    [Path.GetRelativePath(SharedRuntimeStore.Anchor(_paths), active).Replace('\\', '/')] = new { }
                }
            }));
        Assert.Equal(1, SharedRuntimeStore.SweepIdle(_paths, null, static () => { },
            hasLiveGame: static () => false));
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(active));
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
            JsonSerializer.Serialize(new { schemaVersion = PackRuntimeService.RuntimeCacheGeneration, files = listed }));
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
