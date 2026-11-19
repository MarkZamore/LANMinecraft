using System.IO;
using System.Text;

namespace Minecraft.Tests;

public sealed class LauncherStorageCleanupTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "launcher-cleanup-" + Guid.NewGuid().ToString("N")));

    public LauncherStorageCleanupTests() => _paths.Ensure();

    [Fact]
    public void FirstStartupImportsRuntimeRootsAndKeepsPlayerDataBeforeRecordingSchema()
    {
        var legacyPack = Path.Combine(_paths.LegacyPackRuntimes, "Example Pack", ".portable-runtime.json");
        var legacyHost = Path.Combine(_paths.Launcher, LauncherStorageLayout.LegacyLauncherRuntime);
        var hash = new string('b', 64);
        var generation = Path.Combine(legacyHost, hash);
        var protectedFiles = new[]
        {
            Path.Combine(_paths.Worlds, "Example Pack", "My world", "playerdata", Guid.NewGuid() + ".dat"),
            _paths.IdentityRegistryFile,
            _paths.SettingsFile,
            Path.Combine(_paths.Packs, "Personal Pack", "mods", "local.jar")
        };
        foreach (var path in protectedFiles.Append(legacyPack).Append(Path.Combine(generation, "host.dll"))) Write(path);
        File.WriteAllText(generation + ".lease", $"LMHOST01\n{hash}\n", Encoding.ASCII);

        using (var guard = new GuardedFileSystem.DirectoryLocks())
        {
            Assert.True(guard.HoldAncestors(_paths.Launcher));
            Assert.True(LauncherStorageCleanup.ReconcileLayout(_paths));
        }

        Assert.Equal(LauncherStorageSchema.Current, LauncherStorageLayout.ReadSchema(_paths));
        Assert.False(Directory.Exists(_paths.LegacyPackRuntimes));
        Assert.False(Directory.Exists(legacyHost));
        Assert.Equal("fixture", File.ReadAllText(Path.Combine(_paths.Runtimes, "Example Pack", ".portable-runtime.json")));
        Assert.Equal("fixture", File.ReadAllText(Path.Combine(_paths.Launcher, LauncherStorageLayout.LauncherRuntime, hash, "host.dll")));
        Run();
        Assert.All(protectedFiles, path => Assert.Equal("fixture", File.ReadAllText(path)));
        Assert.Equal(LauncherStorageSchema.Current, LauncherStorageLayout.ReadSchema(_paths));
    }

    [Fact]
    public void DeferredImportRetriesAtStartupAndTheCompletedSchemaUsesCanonicalPaths()
    {
        var oldRuntime = Path.Combine(_paths.LegacyPackRuntimes, "Pack", "keep.dat");
        Write(oldRuntime);

        Assert.False(LauncherStorageCleanup.ReconcileLayout(_paths, mayHaveGame: () => true));
        Assert.Equal(LauncherStorageSchema.Unversioned, LauncherStorageLayout.ReadSchema(_paths));
        Assert.Equal(oldRuntime, Path.Combine(_paths.CombineUnderRuntimes("Pack"), "keep.dat"));
        Run();
        Assert.True(File.Exists(oldRuntime));

        var restarted = new AppPaths(_paths.Root);
        Assert.True(LauncherStorageCleanup.ReconcileLayout(restarted, mayHaveGame: () => false));
        Assert.Equal(LauncherStorageSchema.Current, LauncherStorageLayout.ReadSchema(restarted));
        Assert.False(File.Exists(oldRuntime));

        // Unexpected contents with an old name stay outside the current runtime.
        Write(oldRuntime);
        var laterLaunch = new AppPaths(_paths.Root);
        Assert.True(LauncherStorageCleanup.ReconcileLayout(laterLaunch,
            mayHaveGame: () => throw new InvalidOperationException("Version 1 must not rerun legacy import.")));
        Assert.Equal(laterLaunch.CanonicalPackRuntimes, laterLaunch.Runtimes);
        Run();
        Assert.True(File.Exists(oldRuntime));
        Assert.True(File.Exists(Path.Combine(laterLaunch.CombineUnderRuntimes("Pack"), "keep.dat")));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{")]
    public void UnknownSchemaStopsStorageCleanupAndIsNeverRewritten(string marker)
    {
        var markerPath = Path.Combine(_paths.Launcher, LauncherStorageLayout.SchemaFileName);
        var futureData = Path.Combine(_paths.Launcher, "FutureProductData", "keep.dat");
        Write(futureData);
        File.WriteAllText(markerPath, marker);

        Assert.False(LauncherStorageCleanup.ReconcileLayout(_paths));
        Assert.Equal(0, Run());

        Assert.Equal(marker, File.ReadAllText(markerPath));
        Assert.True(File.Exists(futureData));
    }

    [Fact]
    public void RedirectedRuntimeRootPreventsRecordingTheCurrentSchema()
    {
        var target = Path.Combine(_paths.Root, "outside-minecraft", "untouched.dat");
        Write(target);
        Directory.Delete(_paths.CanonicalPackRuntimes);
        SavesFolderService.CreateJunction(_paths.CanonicalPackRuntimes, Path.GetDirectoryName(target)!);

        Assert.False(LauncherStorageCleanup.ReconcileLayout(_paths));
        Assert.Equal(LauncherStorageSchema.Unversioned, LauncherStorageLayout.ReadSchema(_paths));
        Assert.Equal("fixture", File.ReadAllText(target));
    }

    [Fact]
    public void ApplicationPathsAreProtectedFromRootCleanup()
    {
        var applicationPaths = typeof(AppPaths).GetProperties()
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => (string)property.GetValue(_paths)!)
            .ToArray();
        foreach (var (root, entries) in new[]
        {
            (_paths.Service, LauncherStorageLayout.ServiceEntries),
            (_paths.Launcher, LauncherStorageLayout.LauncherEntries),
            (_paths.Personal, LauncherStorageLayout.PersonalEntries)
        })
        {
            var prefix = root + Path.DirectorySeparatorChar;
            var children = applicationPaths.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(path => path[prefix.Length..].Split(Path.DirectorySeparatorChar)[0]);
            Assert.All(children, name => Assert.True(entries.Contains(name), $"Unprotected launcher path: {root}/{name}"));
        }
    }

    [Fact]
    public void UnknownEntriesGoAndKnownStateAndProtectedAssetsRemain()
    {
        Assert.True(LauncherStorageCleanup.ReconcileLayout(_paths));
        var removed = new[]
        {
            Path.Combine(_paths.Service, "obsolete", "loose.bin"),
            Path.Combine(_paths.Launcher, "untracked.bin"),
            Path.Combine(_paths.Personal, "untracked.log"),
            Path.Combine(_paths.Personal, "UnknownDirectory", "discard.bin")
        };
        var preserved = new[]
        {
            _paths.SettingsFile,
            Path.Combine(_paths.Packs, "Personal build", "mods", "local.jar"),
            Path.Combine(_paths.Instances, "Personal build", "dynamic-resource-pack-cache", "warm.zip"),
            Path.Combine(_paths.Worlds, "Personal build", "My world", "level.dat"),
            Path.Combine(_paths.Personal, "UnknownDirectory", "ReSoUrCePaCkS", "custom.zip"),
            Path.Combine(_paths.Service, "Archive", "shaderpacks", "shader.zip"),
            Path.Combine(_paths.Launcher, "Archive", "TexturePacks", "texture.zip"),
            Path.Combine(_paths.Launcher, "bootstrap.log"),
            Path.Combine(_paths.Launcher, "Changelog.md"),
            Path.Combine(_paths.Launcher, "DotNetBundles", "helper.bin"),
            Path.Combine(_paths.Launcher, "LocalAI", "cached-model.bin"),
            Path.Combine(_paths.Personal, "Updates", "Ready", "update.json"),
            Path.Combine(_paths.Personal, "PackConflicts", "Some build", "old", "mod.jar.user-file"),
            Path.Combine(_paths.Launcher, LauncherMaintenance.CompletionFileName)
        };
        foreach (var path in removed.Concat(preserved)) Write(path);

        Assert.True(Run() > 0);
        Assert.All(removed, path => Assert.False(File.Exists(path), path));
        Assert.All(preserved, path => Assert.True(File.Exists(path), path));
        Assert.False(Directory.Exists(Path.Combine(_paths.Service, "obsolete")));
        Assert.Equal(LauncherStorageSchema.Current, LauncherStorageLayout.ReadSchema(_paths));
        Assert.Equal(0, Run());
    }

    [Fact]
    public void TempCleanupLeavesLiveInputsAndDeletesAbandonedAtomicFiles()
    {
        var retainedTool = Path.Combine(_paths.Personal, "Temp", "java-tool-" + Guid.NewGuid().ToString("N"), "Home", "flag");
        var pendingDownload = Path.Combine(_paths.Personal, "Updates", "LANMinecraft.exe.download");
        var expiredDownload = Path.Combine(_paths.Personal, "Updates", "LANMinecraft.from-510.bsdiff.download");
        var recoverableUpdate = Path.Combine(_paths.Personal, "Updates", UpdateService.InstallJournalFileName);
        var fresh = Path.Combine(_paths.Personal, $".settings.json.{Guid.NewGuid():N}.tmp");
        var stale = Path.Combine(_paths.Personal, $".settings.json.{Guid.NewGuid():N}.tmp");
        foreach (var path in new[] { retainedTool, pendingDownload, expiredDownload, recoverableUpdate, fresh, stale }) Write(path);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(expiredDownload, DateTime.UtcNow.AddDays(-2));

        using (File.Open(pendingDownload, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Run();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(pendingDownload));
        Assert.True(File.Exists(expiredDownload));
        Assert.True(File.Exists(recoverableUpdate));
        Assert.True(File.Exists(retainedTool));

        File.Delete(recoverableUpdate);
        Run();

        Assert.False(File.Exists(expiredDownload));
        Assert.True(File.Exists(pendingDownload));
    }

    [Fact]
    public void RootSweepPreservesJunctionsAndTheirTargets()
    {
        var target = Path.Combine(_paths.Root, "outside-minecraft", "kept.bin");
        Write(target);
        var link = Path.Combine(_paths.Service, "unknown-link");
        SavesFolderService.CreateJunction(link, Path.GetDirectoryName(target)!);
        var nestedLink = Path.Combine(_paths.Personal, "unknown", "another-link");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedLink)!);
        SavesFolderService.CreateJunction(nestedLink, Path.GetDirectoryName(target)!);

        Run();

        Assert.True(File.Exists(target));
        Assert.True(Directory.Exists(link));
        Assert.True(Directory.Exists(nestedLink));
    }

    [Fact]
    public void LogRotationLeavesOldArchivesForIdleRetention()
    {
        var expired = Path.Combine(_paths.Personal, "logs-20210101-000000.log");
        Write(expired);
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-10));
        Write(_paths.LogFile);

        LogCleanupService.RotateLauncherLog(_paths);

        Assert.False(File.Exists(_paths.LogFile));
        Assert.True(File.Exists(expired));
        Assert.Equal(2, Directory.GetFiles(_paths.Personal, "logs-*.log").Length);

        Run();

        Assert.False(File.Exists(expired));
        Assert.Single(Directory.GetFiles(_paths.Personal, "logs-*.log"));
    }

    [Fact]
    public void RootSweepResumesWithinTheSameDirectoryAndRechecksItsBoundary()
    {
        var directory = Path.Combine(_paths.Service, "unused");
        var before = Path.Combine(directory, "alpha.bin");
        var boundary = Path.Combine(directory, "beta.bin");
        var after = Path.Combine(directory, "gamma.bin");
        var sibling = Path.Combine(_paths.Service, "unused.extra");
        foreach (var path in new[] { before, boundary, after, sibling }) Write(path);

        string? cursor = null;
        Assert.Throws<OperationCanceledException>(() => LauncherStorageCleanup.RunStep(_paths, null,
            LauncherStorageCleanupStep.ServiceRoot, static () => { }, static _ => false, completedEntry: entry =>
            {
                if (entry != "unused/beta.bin") return;
                cursor = entry;
                throw new OperationCanceledException();
            }));
        Assert.False(File.Exists(before));
        Assert.False(File.Exists(boundary));
        Assert.True(File.Exists(after));
        Assert.True(File.Exists(sibling));

        Write(before);
        Write(boundary);
        LauncherStorageCleanup.RunStep(_paths, null, LauncherStorageCleanupStep.ServiceRoot,
            static () => { }, static _ => false, resumeAfter: cursor);

        Assert.True(File.Exists(before));
        Assert.False(File.Exists(boundary));
        Assert.False(File.Exists(after));
        Assert.False(File.Exists(sibling));
    }

    [Fact]
    public void PlayingAllowsIndependentStepsAndKeepsPackDataAndRuntimeCopies()
    {
        var stray = Path.Combine(_paths.Service, "old.txt");
        var managed = Path.Combine(_paths.Packs, "Running pack", "unlisted.txt");
        var runtime = Path.Combine(_paths.Runtimes, "Running pack", "assets", "copy.dat");
        var shared = Path.Combine(_paths.Launcher, "Shared", "assets", "asset.dat");
        var gameLogs = Path.Combine(_paths.Instances, "Running pack", "logs", "latest.log");
        foreach (var path in new[] { stray, managed, runtime, shared, gameLogs }) Write(path);
        File.SetLastWriteTimeUtc(gameLogs, DateTime.UtcNow.AddDays(-10));

        foreach (var step in Enum.GetValues<LauncherStorageCleanupStep>().Where(LauncherStorageCleanup.CanRunDuringGame))
            LauncherStorageCleanup.RunStep(_paths, null, step, static () => { }, _ =>
                throw new InvalidOperationException("Independent maintenance must not read running pack state."));

        Assert.False(File.Exists(stray));
        Assert.All(new[] { managed, runtime, shared, gameLogs }, path => Assert.True(File.Exists(path), path));
    }

    public void Dispose() => TempTree.Delete(_paths.Root);

    private int Run() => LauncherStorageCleanup.Run(_paths, null, static () => { }, static _ => false);

    private static void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture");
    }
}
