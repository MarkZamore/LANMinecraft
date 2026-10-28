using System.Security.Cryptography;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PackHashServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"minecraft-pack-hash-{Guid.NewGuid():N}");

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public async Task SyncedPeers_HaveTheSameHashDespitePreservedUnknownCanonicalFiles()
    {
        var paths = CreatePaths();
        var clean = paths.CombineUnderPacks("Clean");
        var migrated = paths.CombineUnderPacks("Migrated");
        foreach (var pack in new[] { clean, migrated })
        {
            Write(pack, "mods/current.jar", "author-current-jar");
            Write(pack, "config/current.toml", "author-current-config");
            Write(pack, "kubejs/server_scripts/current.js", "author-current-script");
            WriteState(pack, "mods/current.jar", "config/current.toml", "kubejs/server_scripts/current.js");
        }
        Write(migrated, "mods/unknown-old-helper.jar", "preserved-player-jar");
        Write(migrated, "config/unknown-old-mod.toml", "preserved-player-config");
        Write(migrated, "kubejs/server_scripts/unknown-old-mod.js", "preserved-player-script");
        using var service = new PackHashService(paths);

        var expected = await service.CalculateAsync(clean);
        Assert.Equal(expected, await service.CalculateAsync(migrated));
        Write(migrated, "mods/unknown-old-helper.jar", "different-player-jar");
        Assert.Equal(expected, await service.CalculateAsync(migrated));
        Assert.Equal("different-player-jar", Read(migrated, "mods/unknown-old-helper.jar"));
        Assert.Equal("preserved-player-config", Read(migrated, "config/unknown-old-mod.toml"));
        Assert.Equal("preserved-player-script", Read(migrated, "kubejs/server_scripts/unknown-old-mod.js"));
    }

    [Fact]
    public async Task CustomPackWithoutSyncState_StillIncludesItsOwnUnlistedFiles()
    {
        var paths = CreatePaths();
        var pack = paths.CombineUnderPacks("Custom");
        Write(pack, "mods/current.jar", "custom-current");
        using var service = new PackHashService(paths);
        var first = await service.CalculateAsync(pack);

        Write(pack, "mods/player-addon.jar", "custom-addon");
        var withAddon = await service.CalculateAsync(pack);
        Assert.NotEqual(first, withAddon);
        Write(pack, "config/player-settings.toml", "custom-config");
        Assert.NotEqual(withAddon, await service.CalculateAsync(pack));
    }

    [Theory]
    [InlineData("mods/current.jar")]
    [InlineData("config/current.toml")]
    public async Task SyncedFileChangedWithTheSameSizeAndTimestamp_ChangesTheHash(string relative)
    {
        var paths = CreatePaths();
        var pack = paths.CombineUnderPacks("Synced");
        Write(pack, relative, "author-v1");
        WriteState(pack, relative);
        string first;
        using (var initial = new PackHashService(paths)) first = await initial.CalculateAsync(pack);
        var path = Path.Combine(pack, relative.Replace('/', Path.DirectorySeparatorChar));
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "author-v2");
        File.SetLastWriteTimeUtc(path, stamp);

        using var nextLaunch = new PackHashService(paths);
        Assert.NotEqual(first, await nextLaunch.CalculateAsync(pack));
    }

    [Fact]
    public async Task PendingApply_RefusesAHashAndLeavesThePreviousCacheUntouched()
    {
        var paths = CreatePaths();
        var pack = paths.CombineUnderPacks("Synced");
        Write(pack, "mods/current.jar", "author-current");
        WriteState(pack, "mods/current.jar");
        using var service = new PackHashService(paths);
        var previousHash = await service.CalculateAsync(pack);
        var previousCache = File.ReadAllBytes(paths.PackHashesFile);
        Write(pack, PortablePackSyncService.ApplyPendingFileName, "{\"revision\":\"pending\"}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CalculateAsync(pack));

        Assert.Equal(previousCache, File.ReadAllBytes(paths.PackHashesFile));
        File.Delete(Path.Combine(pack, PortablePackSyncService.ApplyPendingFileName));
        Assert.Equal(previousHash, await service.CalculateAsync(pack));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":1,\"revision\":\"new\",\"files\":{}}")]
    [InlineData("{\"schemaVersion\":2,\"files\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"files\":{\"mods/current.jar\":{\"sizeBytes\":1,\"sha256\":\"bad\"}}}")]
    [InlineData("{\"schemaVersion\":1,\"files\":{\"../outside.jar\":{\"sizeBytes\":1,\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}}}")]
    public async Task DamagedOrUnsafeExistingSyncState_FailsClosedInsteadOfHashingAsCustom(string invalidState)
    {
        var paths = CreatePaths();
        var pack = paths.CombineUnderPacks("Synced");
        Write(pack, "mods/current.jar", "author-current");
        WriteState(pack, "mods/current.jar");
        using var service = new PackHashService(paths);
        await service.CalculateAsync(pack);
        var previousCache = File.ReadAllBytes(paths.PackHashesFile);
        Write(pack, PortablePackSyncService.SyncStateFileName, invalidState);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CalculateAsync(pack));

        Assert.Equal(previousCache, File.ReadAllBytes(paths.PackHashesFile));
        Assert.Equal("author-current", Read(pack, "mods/current.jar"));
    }

    [Fact]
    public async Task MissingManagedFile_DoesNotProduceAReadyHash()
    {
        var paths = CreatePaths();
        var pack = paths.CombineUnderPacks("Synced");
        Write(pack, "mods/current.jar", "author-current");
        WriteState(pack, "mods/current.jar");
        File.Delete(Path.Combine(pack, "mods", "current.jar"));
        using var service = new PackHashService(paths);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CalculateAsync(pack));

        Assert.False(File.Exists(paths.PackHashesFile));
    }

    [Fact]
    public async Task SyncedHash_IgnoresUnknownDirectoryJunctionsWithoutFollowingThem()
    {
        var paths = CreatePaths();
        var pack = paths.CombineUnderPacks("Synced");
        Write(pack, "mods/current.jar", "author-current");
        WriteState(pack, "mods/current.jar");
        using var service = new PackHashService(paths);
        var expected = await service.CalculateAsync(pack);
        var external = Path.Combine(_root, "ExternalPrivateMods");
        Write(external, "personal.jar", "private-jar");
        var junction = Path.Combine(pack, "mods", "unknown-folder");
        SavesFolderService.CreateJunction(junction, external);
        try
        {
            Assert.Equal(expected, await service.CalculateAsync(pack));
            Assert.Equal("private-jar", Read(external, "personal.jar"));
        }
        finally
        {
            if (new DirectoryInfo(junction).LinkTarget is not null) Directory.Delete(junction);
        }
    }

    private AppPaths CreatePaths()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        return paths;
    }

    private static void WriteState(string pack, params string[] managed)
    {
        var files = managed.ToDictionary(relative => relative, relative =>
        {
            var path = Path.Combine(pack, relative.Replace('/', Path.DirectorySeparatorChar));
            var file = new FileInfo(path);
            return new
            {
                sizeBytes = file.Length,
                lastWriteUtcTicks = file.LastWriteTimeUtc.Ticks,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
            };
        });
        Write(pack, PortablePackSyncService.SyncStateFileName,
            JsonSerializer.Serialize(new { schemaVersion = 1, revision = "revision", files }));
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Read(string root, string relative) =>
        File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
}
