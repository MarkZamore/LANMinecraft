using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PortablePackSyncServiceTests : IDisposable
{
    private const string DefaultPack = PortablePackSyncService.DefaultPackRelativePath;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-pack-sync-{Guid.NewGuid():N}");

    public void Dispose()
    {
        TempTree.Delete(_root);
    }

    [Fact]
    public async Task FirstInstall_DownloadsFullTree_ManifestWrittenLast_MarkerAndStateCreated()
    {
        var mod = new PackFile("mods/foo-1.0.jar", Encoding.UTF8.GetBytes("mod-one"));
        var configA = new PackFile("config/a.toml", Encoding.UTF8.GetBytes("a = 1"));
        var configB = new PackFile("config/b/c.json", Encoding.UTF8.GetBytes("{\"c\":1}"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        var assets = new[] { JarAsset(mod), ZipRootAsset("config", configA, configB), JarAsset(packManifest) };
        var release = BuildRelease("rev-1", [mod, configA, configB, packManifest], assets);
        var packDir = PackDir(DefaultPack);

        // The pack manifest is the "pack is complete" flag, so it must be the
        // very last file the apply writes: block the first placement with a
        // directory squatting on the jar path and require that the manifest
        // never appears.
        var blockingDirectory = Path.Combine(packDir, "mods", "foo-1.0.jar");
        Directory.CreateDirectory(blockingDirectory);
        File.WriteAllText(Path.Combine(blockingDirectory, "inner.txt"), "blocker");
        using (var blockedClient = new HttpClient(CreateReleaseHandler(release)))
        {
            var blockedService = CreateService(blockedClient);
            await Assert.ThrowsAsync<PackApplyFailedException>(
                () => blockedService.SyncAsync(DefaultPack, null, CancellationToken.None));
        }
        Assert.False(PackManifestService.HasManifest(packDir));
        Assert.False(File.Exists(Path.Combine(packDir, PortablePackSyncService.SyncStateFileName)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(packDir, ".pack-sync-stage*"));
        Directory.Delete(blockingDirectory, recursive: true);

        var handler = CreateReleaseHandler(release);
        using var httpClient = new HttpClient(handler);
        var result = await CreateService(httpClient).SyncAsync(DefaultPack, null, CancellationToken.None);

        Assert.Equal(PackSyncOutcome.Installed, result.Outcome);
        Assert.Equal("rev-1", result.Revision);
        Assert.Equal(4, result.FilesChanged);
        Assert.Equal(assets.Sum(asset => (long)asset.Content.Length), result.BytesDownloaded);
        Assert.Null(result.Warning);
        AssertFileContent(packDir, mod);
        AssertFileContent(packDir, configA);
        AssertFileContent(packDir, configB);
        AssertFileContent(packDir, packManifest);
        using var marker = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(packDir, PortablePackSyncService.SourceMarkerFileName)));
        Assert.Equal("MarkZamore", marker.RootElement.GetProperty("owner").GetString());
        using var state = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(packDir, PortablePackSyncService.SyncStateFileName)));
        Assert.Equal("rev-1", state.RootElement.GetProperty("revision").GetString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(packDir, ".pack-sync-stage*"));
    }

    /// <summary>
    /// An update that takes a mod away says so, because nothing else will.
    /// </summary>
    /// <remarks>
    /// Everything that mod ever placed in a world disappears the next time the
    /// world is opened, and by then the update is done and there is nothing to
    /// undo. The launcher already knew - it deleted the jar itself - and wrote
    /// "1 extra(s) removed" into a log, which is not a sentence about anybody's
    /// world.
    /// </remarks>
    [Fact]
    public async Task AnUpdateThatTakesAModAway_SaysSo()
    {
        var kept = new PackFile("mods/kept-1.0.jar", Encoding.UTF8.GetBytes("kept"));
        var dropped = new PackFile("mods/dropped-2.3.jar", Encoding.UTF8.GetBytes("dropped"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        await InstallAsync(
            BuildRelease("rev-1", [kept, dropped, packManifest],
                [JarAsset(kept), JarAsset(dropped), JarAsset(packManifest)]),
            DefaultPack);

        var next = BuildRelease("rev-2", [kept, packManifest], [JarAsset(kept), JarAsset(packManifest)]);
        using var httpClient = new HttpClient(CreateReleaseHandler(next));
        var result = await CreateService(httpClient).SyncAsync(DefaultPack, null, CancellationToken.None);

        Assert.Equal(PackSyncOutcome.Updated, result.Outcome);
        Assert.NotNull(result.Warning);
        Assert.Contains("dropped-2.3", result.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("kept-1.0", result.Warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a file the player put there themselves is not a mod the author
    /// removed. The launcher deletes both - everything the manifest does not
    /// name - so "what is about to be deleted" is the wrong question; the right
    /// one is what the previous revision had and this one does not.
    /// </summary>
    [Fact]
    public async Task AFileThePlayerAdded_IsNotReportedAsRemoved()
    {
        var kept = new PackFile("mods/kept-1.0.jar", Encoding.UTF8.GetBytes("kept"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        var release = BuildRelease("rev-1", [kept, packManifest], [JarAsset(kept), JarAsset(packManifest)]);
        await InstallAsync(release, DefaultPack);

        // Somebody drops a mod of their own into the pack.
        var packDir = PackDir(DefaultPack);
        File.WriteAllText(Path.Combine(packDir, "mods", "mine-9.9.jar"), "mine");

        var next = BuildRelease("rev-2", [kept, packManifest], [JarAsset(kept), JarAsset(packManifest)]);
        using var httpClient = new HttpClient(CreateReleaseHandler(next));
        var result = await CreateService(httpClient).SyncAsync(DefaultPack, null, CancellationToken.None);

        // It is still deleted - the pack is what the manifest says it is - but
        // nobody is told a mod was taken out of the pack, because none was.
        Assert.Equal(PackSyncOutcome.Updated, result.Outcome);
        Assert.False(File.Exists(Path.Combine(packDir, "mods", "mine-9.9.jar")));
        Assert.Null(result.Warning);
    }

    /// <summary>
    /// Raising a mod's version renames its jar, and a rename is not a removal.
    /// This is the ordinary shape of an update - most revisions bump several
    /// mods and remove none - so getting it wrong would put the warning on
    /// screen every time and teach the player to look past it.
    /// </summary>
    [Fact]
    public async Task AVersionBump_IsNotAModRemoval()
    {
        var before = new PackFile("mods/jei-1.21.1-19.21.0.jar", Encoding.UTF8.GetBytes("jei old"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        await InstallAsync(
            BuildRelease("rev-1", [before, packManifest], [JarAsset(before), JarAsset(packManifest)]),
            DefaultPack);

        var after = new PackFile("mods/jei-1.21.1-19.22.0.jar", Encoding.UTF8.GetBytes("jei new"));
        var next = BuildRelease("rev-2", [after, packManifest], [JarAsset(after), JarAsset(packManifest)]);
        using var httpClient = new HttpClient(CreateReleaseHandler(next));
        var result = await CreateService(httpClient).SyncAsync(DefaultPack, null, CancellationToken.None);

        // The old jar is gone from the disk and the new one is there. Nobody is
        // told a mod left the pack, because the mod did not.
        var packDir = PackDir(DefaultPack);
        Assert.Equal(PackSyncOutcome.Updated, result.Outcome);
        Assert.False(File.Exists(Path.Combine(packDir, "mods", "jei-1.21.1-19.21.0.jar")));
        Assert.True(File.Exists(Path.Combine(packDir, "mods", "jei-1.21.1-19.22.0.jar")));
        Assert.Null(result.Warning);
    }

    /// <summary>
    /// The case that carries both at once, which is what a real update looks
    /// like: several mods raised a version, one taken out. Only the one that
    /// left is named.
    /// </summary>
    [Fact]
    public async Task AModTakenOutAmongBumpedOnes_IsTheOnlyOneNamed()
    {
        var oldJei = new PackFile("mods/jei-1.21.1-19.21.0.jar", Encoding.UTF8.GetBytes("jei old"));
        var oldSodium = new PackFile("mods/sodium-fabric-mc1.20.1-0.5.3.jar", Encoding.UTF8.GetBytes("sodium old"));
        var doomed = new PackFile("mods/twilightforest-4.1.268.jar", Encoding.UTF8.GetBytes("twilight"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        await InstallAsync(
            BuildRelease(
                "rev-1",
                [oldJei, oldSodium, doomed, packManifest],
                [JarAsset(oldJei), JarAsset(oldSodium), JarAsset(doomed), JarAsset(packManifest)]),
            DefaultPack);

        var newJei = new PackFile("mods/jei-1.21.1-19.22.0.jar", Encoding.UTF8.GetBytes("jei new"));
        var newSodium = new PackFile("mods/sodium-fabric-mc1.20.1-0.5.8.jar", Encoding.UTF8.GetBytes("sodium new"));
        var next = BuildRelease(
            "rev-2",
            [newJei, newSodium, packManifest],
            [JarAsset(newJei), JarAsset(newSodium), JarAsset(packManifest)]);
        using var httpClient = new HttpClient(CreateReleaseHandler(next));
        var result = await CreateService(httpClient).SyncAsync(DefaultPack, null, CancellationToken.None);

        Assert.NotNull(result.Warning);
        Assert.Contains("twilightforest", result.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("jei", result.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("sodium", result.Warning, StringComparison.Ordinal);
        Assert.Contains("(1)", result.Warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// A KubeJS startup script registers real blocks and items - Limitless 8
    /// creates tnp:limitless_sword in one - so losing it costs a world exactly
    /// what losing a mod does. A resource pack does not, and saying so about
    /// one would spend the warning on a change of texture.
    /// </summary>
    [Fact]
    public async Task AStartupScriptTakenAway_IsWarnedAbout_AResourcePackIsNot()
    {
        var script = new PackFile(
            "kubejs/startup_scripts/items.js", Encoding.UTF8.GetBytes("StartupEvents.registry(...)"));
        var texture = new PackFile("resourcepacks/pretty.zip", Encoding.UTF8.GetBytes("textures"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        await InstallAsync(
            BuildRelease(
                "rev-1",
                [script, texture, packManifest],
                [JarAsset(script), JarAsset(texture), JarAsset(packManifest)]),
            DefaultPack);

        var next = BuildRelease("rev-2", [packManifest], [JarAsset(packManifest)]);
        using var httpClient = new HttpClient(CreateReleaseHandler(next));
        var result = await CreateService(httpClient).SyncAsync(DefaultPack, null, CancellationToken.None);

        // Both are gone from the disk; only the one that took content out of the
        // save is spoken about, and it is not called a mod.
        Assert.NotNull(result.Warning);
        Assert.Contains("items", result.Warning, StringComparison.Ordinal);
        Assert.Contains("скрипты", result.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("pretty", result.Warning, StringComparison.Ordinal);
        Assert.Contains("(1)", result.Warning, StringComparison.Ordinal);
    }

    /// <summary>A first install removes nothing: there was no world before it.</summary>
    [Fact]
    public async Task AFirstInstall_WarnsAboutNothing()
    {
        var mod = new PackFile("mods/foo-1.0.jar", Encoding.UTF8.GetBytes("mod"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        var release = BuildRelease("rev-1", [mod, packManifest], [JarAsset(mod), JarAsset(packManifest)]);

        using var httpClient = new HttpClient(CreateReleaseHandler(release));
        var result = await CreateService(httpClient).SyncAsync(DefaultPack, null, CancellationToken.None);

        Assert.Equal(PackSyncOutcome.Installed, result.Outcome);
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task UpToDate_OnlyManifestRequested_NoFileRewritten()
    {
        var mod = new PackFile("mods/foo-1.0.jar", Encoding.UTF8.GetBytes("mod-one"));
        var config = new PackFile("config/a.toml", Encoding.UTF8.GetBytes("a = 1"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        var release = BuildRelease(
            "rev-1",
            [mod, config, packManifest],
            [JarAsset(mod), ZipRootAsset("config", config), JarAsset(packManifest)]);
        await InstallAsync(release, DefaultPack);
        var packDir = PackDir(DefaultPack);
        var timestamps = new[] { mod.Path, config.Path, packManifest.Path }
            .ToDictionary(path => path, path => File.GetLastWriteTimeUtc(LivePath(packDir, path)));

        var handler = CreateReleaseHandler(release);
        using var httpClient = new HttpClient(handler);
        var result = await CreateService(httpClient).SyncAsync(DefaultPack, null, CancellationToken.None);

        Assert.Equal(PackSyncOutcome.UpToDate, result.Outcome);
        Assert.Equal(0, result.FilesChanged);
        Assert.Equal(0, result.BytesDownloaded);
        var request = Assert.Single(handler.RequestUris);
        Assert.Contains(PortablePackSyncService.PackManifestAssetName, request.AbsolutePath, StringComparison.Ordinal);
        foreach (var (path, timestamp) in timestamps)
        {
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(LivePath(packDir, path)));
        }
    }

    [Fact]
    public async Task ChangedJar_NewFileIdentity()
    {
        var oldMod = new PackFile("mods/foo-1.0.jar", Encoding.UTF8.GetBytes("mod-one"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        await InstallAsync(
            BuildRelease("rev-1", [oldMod, packManifest], [JarAsset(oldMod), JarAsset(packManifest)]),
            DefaultPack);
        var packDir = PackDir(DefaultPack);
        var livePath = LivePath(packDir, oldMod.Path);
        var linkPath = Path.Combine(_root, "hardlink-to-old.jar");
        Assert.True(CreateHardLink(linkPath, livePath, IntPtr.Zero));

        var newMod = new PackFile("mods/foo-1.0.jar", Encoding.UTF8.GetBytes("mod-two"));
        var release = BuildRelease("rev-2", [newMod, packManifest], [JarAsset(newMod), JarAsset(packManifest)]);
        var handler = CreateReleaseHandler(release);
        using var httpClient = new HttpClient(handler);
        var result = await CreateService(httpClient).SyncAsync(DefaultPack, null, CancellationToken.None);

        Assert.Equal(PackSyncOutcome.Updated, result.Outcome);
        Assert.Equal(newMod.Content, File.ReadAllBytes(livePath));
        // The live file was replaced with a fresh identity, so the hard link an
        // instance may still hold keeps the old bytes instead of mutating.
        Assert.Equal(oldMod.Content, File.ReadAllBytes(linkPath));
    }

    [Fact]
    public async Task RemovedFiles_Deleted_ServiceFilesAndLegacyNamesSurvive()
    {
        var keptMod = new PackFile("mods/a.jar", Encoding.UTF8.GetBytes("keep"));
        var removedMod = new PackFile("mods/sub/b.jar", Encoding.UTF8.GetBytes("remove"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        await InstallAsync(
            BuildRelease(
                "rev-1",
                [keptMod, removedMod, packManifest],
                [JarAsset(keptMod), JarAsset(removedMod), JarAsset(packManifest)]),
            DefaultPack);
        var packDir = PackDir(DefaultPack);
        File.WriteAllText(Path.Combine(packDir, "mods", "rogue.jar"), "rogue");
        Directory.CreateDirectory(Path.Combine(packDir, "logs"));
        File.WriteAllText(Path.Combine(packDir, "logs", "latest.log"), "log");
        File.WriteAllText(Path.Combine(packDir, "options.txt"), "fov:1.0");
        File.WriteAllText(Path.Combine(packDir, ".portable-instance.json"), "{}");

        var release = BuildRelease(
            "rev-2",
            [keptMod, packManifest],
            [JarAsset(keptMod), JarAsset(packManifest)]);
        var handler = CreateReleaseHandler(release);
        using var httpClient = new HttpClient(handler);
        var result = await CreateService(httpClient).SyncAsync(DefaultPack, null, CancellationToken.None);

        Assert.Equal(PackSyncOutcome.Updated, result.Outcome);
        Assert.Equal(2, result.FilesChanged);
        var request = Assert.Single(handler.RequestUris);
        Assert.Contains(PortablePackSyncService.PackManifestAssetName, request.AbsolutePath, StringComparison.Ordinal);
        AssertFileContent(packDir, keptMod);
        Assert.False(File.Exists(LivePath(packDir, removedMod.Path)));
        Assert.False(Directory.Exists(Path.Combine(packDir, "mods", "sub")));
        Assert.False(File.Exists(Path.Combine(packDir, "mods", "rogue.jar")));
        Assert.True(File.Exists(Path.Combine(packDir, "logs", "latest.log")));
        Assert.True(File.Exists(Path.Combine(packDir, "options.txt")));
        Assert.True(File.Exists(Path.Combine(packDir, ".portable-instance.json")));
        Assert.True(File.Exists(Path.Combine(packDir, PortablePackSyncService.SourceMarkerFileName)));
        Assert.True(File.Exists(Path.Combine(packDir, PortablePackSyncService.SyncStateFileName)));
    }

    [Fact]
    public async Task RootOutsideTheManifest_Removed_PlayerOwnedEntriesSurvive()
    {
        var mod = new PackFile("mods/a.jar", Encoding.UTF8.GetBytes("keep"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        await InstallAsync(
            BuildRelease("rev-1", [mod, packManifest], [JarAsset(mod), JarAsset(packManifest)]),
            DefaultPack);
        var packDir = PackDir(DefaultPack);
        // A root the previous pack shipped and this one knows nothing about.
        Directory.CreateDirectory(Path.Combine(packDir, "scripts"));
        File.WriteAllText(Path.Combine(packDir, "scripts", "legacy.zs"), "legacy");
        Directory.CreateDirectory(Path.Combine(packDir, "xaero", "minimap"));
        File.WriteAllText(Path.Combine(packDir, "xaero", "minimap", "waypoints.txt"), "player");

        var release = BuildRelease("rev-2", [mod, packManifest], [JarAsset(mod), JarAsset(packManifest)]);
        using var httpClient = new HttpClient(CreateReleaseHandler(release));
        var result = await CreateService(httpClient).SyncAsync(DefaultPack, null, CancellationToken.None);

        Assert.Equal(PackSyncOutcome.Updated, result.Outcome);
        Assert.False(Directory.Exists(Path.Combine(packDir, "scripts")));
        Assert.True(File.Exists(Path.Combine(packDir, "xaero", "minimap", "waypoints.txt")));
        AssertFileContent(packDir, mod);
    }

    [Fact]
    public async Task ApplyBlocked_FailsLoudly_InsteadOfPretendingToBeOffline()
    {
        var first = new PackFile("mods/a.jar", Encoding.UTF8.GetBytes("v1"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        await InstallAsync(
            BuildRelease("rev-1", [first, packManifest], [JarAsset(first), JarAsset(packManifest)]),
            DefaultPack);
        var packDir = PackDir(DefaultPack);

        var second = new PackFile("mods/a.jar", Encoding.UTF8.GetBytes("v2"));
        using var httpClient = new HttpClient(CreateReleaseHandler(
            BuildRelease("rev-2", [second, packManifest], [JarAsset(second), JarAsset(packManifest)])));
        var service = CreateService(httpClient);
        using var hold = new FileStream(
            LivePath(packDir, first.Path), FileMode.Open, FileAccess.Read, FileShare.None);

        await Assert.ThrowsAsync<PackApplyFailedException>(
            () => service.SyncAsync(DefaultPack, null, CancellationToken.None));
    }

    [Fact]
    public async Task ApplyBlockedBriefly_SucceedsOnRetry()
    {
        var first = new PackFile("mods/a.jar", Encoding.UTF8.GetBytes("v1"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        await InstallAsync(
            BuildRelease("rev-1", [first, packManifest], [JarAsset(first), JarAsset(packManifest)]),
            DefaultPack);
        var packDir = PackDir(DefaultPack);

        var second = new PackFile("mods/a.jar", Encoding.UTF8.GetBytes("v2"));
        using var httpClient = new HttpClient(CreateReleaseHandler(
            BuildRelease("rev-2", [second, packManifest], [JarAsset(second), JarAsset(packManifest)])));
        // A scanner that opens the jar and lets go a moment later - on a thread
        // of its own rather than the pool's. The sync being tested keeps the
        // pool busy, and a timer callback waiting its turn there is what made
        // this test fail on a loaded machine: the file was still held when the
        // last retry came round, and the failure said nothing about packs.
        var hold = new FileStream(
            LivePath(packDir, first.Path), FileMode.Open, FileAccess.Read, FileShare.None);
        var released = new Thread(() =>
        {
            Thread.Sleep(200);
            hold.Dispose();
        })
        {
            IsBackground = true,
            Name = "pack-sync-test-release",
        };
        released.Start();

        try
        {
            var result = await CreateService(httpClient).SyncAsync(DefaultPack, null, CancellationToken.None);

            Assert.Equal(PackSyncOutcome.Updated, result.Outcome);
            AssertFileContent(packDir, second);
        }
        finally
        {
            released.Join(TimeSpan.FromSeconds(30));
            hold.Dispose();
        }
    }

    [Fact]
    public async Task ZipRoot_SingleChangedFile_SiblingsKeepMtime()
    {
        var configA = new PackFile("config/a.toml", Encoding.UTF8.GetBytes("a = 1"));
        var configB = new PackFile("config/b.toml", Encoding.UTF8.GetBytes("b = 1"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        await InstallAsync(
            BuildRelease(
                "rev-1",
                [configA, configB, packManifest],
                [ZipRootAsset("config", configA, configB), JarAsset(packManifest)]),
            DefaultPack);
        var packDir = PackDir(DefaultPack);
        var siblingTimestamp = File.GetLastWriteTimeUtc(LivePath(packDir, configB.Path));

        var changedA = new PackFile("config/a.toml", Encoding.UTF8.GetBytes("a = 2"));
        var release = BuildRelease(
            "rev-2",
            [changedA, configB, packManifest],
            [ZipRootAsset("config", changedA, configB), JarAsset(packManifest)]);
        var handler = CreateReleaseHandler(release);
        using var httpClient = new HttpClient(handler);
        var result = await CreateService(httpClient).SyncAsync(DefaultPack, null, CancellationToken.None);

        Assert.Equal(PackSyncOutcome.Updated, result.Outcome);
        Assert.Equal(1, result.FilesChanged);
        AssertFileContent(packDir, changedA);
        AssertFileContent(packDir, configB);
        Assert.Equal(siblingTimestamp, File.GetLastWriteTimeUtc(LivePath(packDir, configB.Path)));
    }

    [Fact]
    public async Task ManifestFetchFails_WithLocalPack_OfflineFallback()
    {
        var mod = new PackFile("mods/foo-1.0.jar", Encoding.UTF8.GetBytes("mod-one"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        await InstallAsync(
            BuildRelease("rev-1", [mod, packManifest], [JarAsset(mod), JarAsset(packManifest)]),
            DefaultPack);

        var offline = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var offlineClient = new HttpClient(offline);
        var result = await CreateService(offlineClient).SyncAsync(DefaultPack, null, CancellationToken.None);

        Assert.Equal(PackSyncOutcome.OfflineFallback, result.Outcome);
        Assert.Equal(PortablePackSyncService.OfflineCheckWarning, result.Warning);
        AssertFileContent(PackDir(DefaultPack), mod);
        AssertFileContent(PackDir(DefaultPack), packManifest);
    }

    [Fact]
    public async Task ManifestFetchFails_WithoutLocalPack_Throws()
    {
        var offline = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var offlineClient = new HttpClient(offline);
        var service = CreateService(offlineClient);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SyncAsync(DefaultPack, null, CancellationToken.None));

        Assert.Equal(PortablePackSyncService.FirstInstallUnavailableMessage, exception.Message);
        Assert.False(Directory.Exists(PackDir(DefaultPack)));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("C:/x")]
    [InlineData("a/../../x")]
    [InlineData(".portable-pack-sync.json")]
    [InlineData("config/nul.txt")]
    [InlineData("config/COM1")]
    [InlineData("config/x.")]
    [InlineData("config/x ")]
    [InlineData("config/a<b.txt")]
    [InlineData("config/a|b.txt")]
    public async Task ManifestPathTraversal_Rejected(string maliciousPath)
    {
        var malicious = new PackFile(maliciousPath, Encoding.UTF8.GetBytes("payload"));
        var release = BuildRelease("rev-1", [malicious], [JarAsset(malicious)]);
        var handler = CreateReleaseHandler(release);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SyncAsync(DefaultPack, null, CancellationToken.None));

        Assert.False(Directory.Exists(PackDir(DefaultPack)));
        Assert.False(File.Exists(Path.Combine(PacksDir, "x")));
        Assert.False(File.Exists(Path.Combine(_root, "Minecraft", "x")));
    }

    [Fact]
    public async Task ManifestPaths_RealWorldModNames_Accepted()
    {
        // Names the built-in pack actually ships: spaces, '#', '$', '&', '+',
        // quotes, parentheses and brackets must all survive the path check.
        var files = new[]
        {
            new PackFile("config/#STOP_MOD_REPOSTS.txt", Encoding.UTF8.GetBytes("marker")),
            new PackFile("config/adchimneys/bakery$brick_stove.cfg", Encoding.UTF8.GetBytes("cfg")),
            new PackFile("config/ipn/1.2.3.4&5/trades.json", Encoding.UTF8.GetBytes("{}")),
            new PackFile("mods/L_Ender's Cataclysm 1.21.1-3.32.jar", Encoding.UTF8.GetBytes("jar-1")),
            new PackFile("mods/ATi Structures V1.4.4 (1.21+).jar", Encoding.UTF8.GetBytes("jar-2")),
            new PackFile("mods/DnT-overhaul-v2 [NeoForge].jar", Encoding.UTF8.GetBytes("jar-3")),
        };
        var assets = files.Select(JarAsset).ToArray();
        var release = BuildRelease("rev-1", files, assets);
        var handler = CreateReleaseHandler(release);
        using var httpClient = new HttpClient(handler);

        var result = await CreateService(httpClient)
            .SyncAsync(DefaultPack, null, CancellationToken.None);

        Assert.Equal(PackSyncOutcome.Installed, result.Outcome);
        foreach (var file in files)
        {
            AssertFileContent(PackDir(DefaultPack), file);
        }
    }

    [Fact]
    public async Task Marker_Invalid_TreatedAsCustom()
    {
        var packDir = PackDir("Custom");
        Directory.CreateDirectory(packDir);
        File.WriteAllText(
            Path.Combine(packDir, PortablePackSyncService.SourceMarkerFileName),
            "{\"schemaVersion\":1,\"owner\":\"bad/owner\",\"repo\":\"repo\",\"tag\":\"tag\"}");
        var handler = new RecordingHandler(_ =>
            throw new InvalidOperationException("Network must not be used."));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        Assert.Null(service.TryResolveSource("Custom"));
        var result = await service.SyncAsync("Custom", null, CancellationToken.None);

        Assert.Equal(PackSyncOutcome.SkippedNoSource, result.Outcome);
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task Marker_AbsentDefaultPack_UsesBuiltInSource()
    {
        var mod = new PackFile("mods/foo-1.0.jar", Encoding.UTF8.GetBytes("mod-one"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        var release = BuildRelease("rev-1", [mod, packManifest], [JarAsset(mod), JarAsset(packManifest)]);
        var handler = CreateReleaseHandler(release);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        Assert.Equal(PortablePackSyncService.DefaultPackSource, service.TryResolveSource(DefaultPack));
        var result = await service.SyncAsync(DefaultPack, null, CancellationToken.None);

        Assert.Equal(PackSyncOutcome.Installed, result.Outcome);
        Assert.StartsWith(
            $"https://github.com/{PortablePackSyncService.DefaultPackSource.Owner}/{PortablePackSyncService.DefaultPackSource.Repo}/releases/download/{PortablePackSyncService.DefaultPackSource.Tag}/pack-manifest.json",
            handler.RequestUris[0].AbsoluteUri,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A player who installed the pack under its former name still has a marker
    /// naming the repository it was published from. It must never send the sync
    /// back there, and the next sync must retire it.
    /// </summary>
    [Theory]
    [InlineData("Infinity")]
    [InlineData("InfinityPack")]
    public async Task Marker_LegacyRepoInDefaultPack_IsHealedAndRewritten(string legacyRepo)
    {
        var mod = new PackFile("mods/foo-1.0.jar", Encoding.UTF8.GetBytes("mod-one"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        var release = BuildRelease("rev-1", [mod, packManifest], [JarAsset(mod), JarAsset(packManifest)]);
        var packDir = PackDir(DefaultPack);
        Directory.CreateDirectory(packDir);
        WriteMarker(packDir, "MarkZamore", legacyRepo, "pack-latest");
        var handler = CreateReleaseHandler(release);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        Assert.Equal(PortablePackSyncService.DefaultPackSource, service.TryResolveSource(DefaultPack));
        var result = await service.SyncAsync(DefaultPack, null, CancellationToken.None);

        Assert.Equal(PackSyncOutcome.Installed, result.Outcome);
        Assert.All(
            handler.RequestUris,
            uri => Assert.StartsWith(
                $"https://github.com/{PortablePackSyncService.DefaultPackSource.Owner}/{PortablePackSyncService.DefaultPackSource.Repo}/releases/download/{PortablePackSyncService.DefaultPackSource.Tag}/",
                uri.AbsoluteUri,
                StringComparison.Ordinal));
        Assert.Equal(
            PortablePackSyncService.DefaultPackSource,
            service.TryResolveSource(DefaultPack));
        using var marker = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(packDir, PortablePackSyncService.SourceMarkerFileName)));
        Assert.Equal(
            PortablePackSyncService.DefaultPackSource.Repo,
            marker.RootElement.GetProperty("repo").GetString());
    }

    [Fact]
    public void Marker_ForeignOwnerInDefaultPack_Wins()
    {
        var packDir = PackDir(DefaultPack);
        Directory.CreateDirectory(packDir);
        WriteMarker(packDir, "SomebodyElse", "Infinity", "nightly");
        using var httpClient = new HttpClient(new RecordingHandler(_ =>
            throw new InvalidOperationException("Network must not be used.")));
        var service = CreateService(httpClient);

        Assert.Equal(
            new PackSyncSource("SomebodyElse", "Infinity", "nightly"),
            service.TryResolveSource(DefaultPack));

        service.EnsureDefaultSourceMarker(DefaultPack);

        Assert.Equal(
            new PackSyncSource("SomebodyElse", "Infinity", "nightly"),
            service.TryResolveSource(DefaultPack));
    }

    [Fact]
    public async Task Marker_AbsentOther_Skipped()
    {
        var handler = new RecordingHandler(_ =>
            throw new InvalidOperationException("Network must not be used."));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        Assert.Null(service.TryResolveSource("Custom"));
        var result = await service.SyncAsync("Custom", null, CancellationToken.None);

        Assert.Equal(PackSyncOutcome.SkippedNoSource, result.Outcome);
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task RedirectToForeignHost_Rejected()
    {
        var mod = new PackFile("mods/foo-1.0.jar", Encoding.UTF8.GetBytes("mod-one"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        var release = BuildRelease("rev-1", [mod, packManifest], [JarAsset(mod), JarAsset(packManifest)]);
        var handler = new RecordingHandler(request =>
        {
            var asset = LastPathSegment(request.RequestUri!);
            var response = release.TryGetValue(asset, out var payload)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            if (!string.Equals(asset, PortablePackSyncService.PackManifestAssetName, StringComparison.Ordinal))
            {
                response.RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get,
                    new Uri("https://evil-githubusercontent.com/asset"));
            }
            return response;
        });
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        await Assert.ThrowsAsync<IOException>(
            () => service.SyncAsync(DefaultPack, null, CancellationToken.None));

        var packDir = PackDir(DefaultPack);
        Assert.False(PackManifestService.HasManifest(packDir));
        Assert.False(File.Exists(LivePath(packDir, mod.Path)));
        if (Directory.Exists(packDir))
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(packDir, ".pack-sync-stage*"));
        }
    }

    [Fact]
    public async Task AssetShaMismatch_NothingApplied()
    {
        var oldMod = new PackFile("mods/foo-1.0.jar", Encoding.UTF8.GetBytes("mod-one"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        await InstallAsync(
            BuildRelease("rev-1", [oldMod, packManifest], [JarAsset(oldMod), JarAsset(packManifest)]),
            DefaultPack);

        var newMod = new PackFile("mods/foo-1.0.jar", Encoding.UTF8.GetBytes("mod-two"));
        var newAsset = JarAsset(newMod);
        var release = BuildRelease("rev-2", [newMod, packManifest], [newAsset, JarAsset(packManifest)]);
        var tampered = newMod.Content.ToArray();
        tampered[^1] ^= 0xFF;
        release[newAsset.Name] = tampered;
        var handler = CreateReleaseHandler(release);
        using var httpClient = new HttpClient(handler);
        var result = await CreateService(httpClient).SyncAsync(DefaultPack, null, CancellationToken.None);

        Assert.Equal(PackSyncOutcome.OfflineFallback, result.Outcome);
        Assert.Equal(PortablePackSyncService.OfflineSyncWarning, result.Warning);
        var packDir = PackDir(DefaultPack);
        AssertFileContent(packDir, oldMod);
        AssertFileContent(packDir, packManifest);
        Assert.Empty(Directory.EnumerateFileSystemEntries(packDir, ".pack-sync-stage*"));
    }

    [Fact]
    public async Task InterruptedApply_StagingSwept_NextSyncConverges()
    {
        var mod = new PackFile("mods/foo-1.0.jar", Encoding.UTF8.GetBytes("mod-one"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        var release = BuildRelease("rev-1", [mod, packManifest], [JarAsset(mod), JarAsset(packManifest)]);
        await InstallAsync(release, DefaultPack);
        var packDir = PackDir(DefaultPack);
        // Simulate a run that was killed mid-apply: stale staging plus a live
        // tree that no longer matches the manifest.
        var staleStage = Path.Combine(packDir, ".pack-sync-stage", "deadbeef", "assets");
        Directory.CreateDirectory(staleStage);
        File.WriteAllText(Path.Combine(staleStage, "junk.bin"), "junk");
        File.Delete(LivePath(packDir, mod.Path));

        var handler = CreateReleaseHandler(release);
        using var httpClient = new HttpClient(handler);
        var result = await CreateService(httpClient).SyncAsync(DefaultPack, null, CancellationToken.None);

        Assert.Equal(PackSyncOutcome.Updated, result.Outcome);
        Assert.Equal(1, result.FilesChanged);
        AssertFileContent(packDir, mod);
        AssertFileContent(packDir, packManifest);
        Assert.Empty(Directory.EnumerateFileSystemEntries(packDir, ".pack-sync-stage*"));
    }

    [Fact]
    public async Task Progress_ReportsSyncingPack()
    {
        var mod = new PackFile("mods/foo-1.0.jar", Encoding.UTF8.GetBytes("mod-one"));
        var config = new PackFile("config/a.toml", Encoding.UTF8.GetBytes("a = 1"));
        var packManifest = PackManifestFile("{\"schemaVersion\":1}");
        var assets = new[] { JarAsset(mod), ZipRootAsset("config", config), JarAsset(packManifest) };
        var release = BuildRelease("rev-1", [mod, config, packManifest], assets);
        var handler = CreateReleaseHandler(release);
        using var httpClient = new HttpClient(handler);
        var progress = new SynchronousProgress();

        await CreateService(httpClient).SyncAsync(DefaultPack, progress, CancellationToken.None);

        Assert.NotEmpty(progress.Reports);
        Assert.All(progress.Reports, report =>
            Assert.Equal(RuntimePreparationStage.SyncingPack, report.Stage));
        var totalBytes = assets.Sum(asset => (long)asset.Content.Length);
        Assert.Contains(progress.Reports, report =>
            report.TotalBytes == totalBytes && report.DownloadedBytes == totalBytes);
        Assert.Contains(progress.Reports, report => report.Fraction == 1d);
    }

    private sealed class SynchronousProgress : IProgress<RuntimePreparationProgress>
    {
        public List<RuntimePreparationProgress> Reports { get; } = [];

        public void Report(RuntimePreparationProgress value) => Reports.Add(value);
    }

    private string PacksDir => Path.Combine(_root, "Minecraft", "Packs");

    private string PackDir(string relativePath) => Path.Combine(PacksDir, relativePath);

    private static void WriteMarker(string packDir, string owner, string repo, string tag) =>
        File.WriteAllText(
            Path.Combine(packDir, PortablePackSyncService.SourceMarkerFileName),
            JsonSerializer.Serialize(new { schemaVersion = 1, owner, repo, tag }));

    private static string LivePath(string packDir, string relativePath) =>
        Path.Combine(packDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static void AssertFileContent(string packDir, PackFile file)
    {
        var path = LivePath(packDir, file.Path);
        Assert.True(File.Exists(path), $"Expected pack file is missing: {file.Path}");
        Assert.Equal(file.Content, File.ReadAllBytes(path));
    }

    private async Task InstallAsync(Dictionary<string, byte[]> release, string packRelativePath)
    {
        var handler = CreateReleaseHandler(release);
        using var httpClient = new HttpClient(handler);
        var result = await CreateService(httpClient).SyncAsync(packRelativePath, null, CancellationToken.None);
        Assert.Equal(PackSyncOutcome.Installed, result.Outcome);
    }

    private PortablePackSyncService CreateService(
        HttpClient httpClient,
        Func<string, long, bool>? freeSpaceProbe = null)
    {
        Directory.CreateDirectory(_root);
        return new PortablePackSyncService(
            new AppPaths(_root),
            new Logger(Path.Combine(_root, "log.txt")),
            httpClient,
            freeSpaceProbe ?? ((_, _) => true));
    }

    private static PackFile PackManifestFile(string json) =>
        new(PackManifestService.ManifestFileName, Encoding.UTF8.GetBytes(json));

    // Asset names mirror generate_manifest.py: sha prefix plus the sanitized
    // file name (everything outside [A-Za-z0-9._-] collapses to '-').
    private static PackAsset JarAsset(PackFile file) =>
        new(
            $"m-{file.Sha[..12]}-{SanitizeAssetName(file.Path.Split('/')[^1])}",
            "jar",
            file.Content,
            [file.Path]);

    private static string SanitizeAssetName(string name)
    {
        var sanitized = System.Text.RegularExpressions.Regex.Replace(name, "[^A-Za-z0-9._-]+", "-");
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, "--+", "-");
        return sanitized.Length > 80 ? sanitized[..80] : sanitized;
    }

    private static PackAsset ZipRootAsset(string rootName, params PackFile[] files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                using var stream = archive.CreateEntry(file.Path).Open();
                stream.Write(file.Content);
            }
        }
        var content = buffer.ToArray();
        return new PackAsset(
            $"r-{Hash(content)[..12]}-{rootName}.zip",
            "zipRoot",
            content,
            files.Select(file => file.Path).ToArray());
    }

    private static Dictionary<string, byte[]> BuildRelease(
        string revision,
        IReadOnlyList<PackFile> files,
        IReadOnlyList<PackAsset> assets)
    {
        var manifest = new
        {
            schemaVersion = 1,
            packId = DefaultPack,
            revision,
            files = files
                .Select(file => new { path = file.Path, sizeBytes = (long)file.Content.Length, sha256 = file.Sha })
                .ToArray(),
            assets = assets
                .Select(asset => new
                {
                    name = asset.Name,
                    kind = asset.Kind,
                    sha256 = asset.Sha,
                    sizeBytes = (long)asset.Content.Length,
                    covers = asset.Covers
                })
                .ToArray()
        };
        var release = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [PortablePackSyncService.PackManifestAssetName] = JsonSerializer.SerializeToUtf8Bytes(manifest)
        };
        foreach (var asset in assets)
        {
            release[asset.Name] = asset.Content;
        }
        return release;
    }

    private static RecordingHandler CreateReleaseHandler(Dictionary<string, byte[]> release) =>
        new(request => release.TryGetValue(LastPathSegment(request.RequestUri!), out var payload)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));

    private static string LastPathSegment(Uri uri)
    {
        var path = uri.AbsolutePath;
        return Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record PackFile(string Path, byte[] Content)
    {
        public string Sha => Hash(Content);
    }

    private sealed record PackAsset(string Name, string Kind, byte[] Content, string[] Covers)
    {
        public string Sha => Hash(Content);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (RequestUris)
            {
                RequestUris.Add(request.RequestUri!);
            }
            return Task.FromResult(responseFactory(request));
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);
}
