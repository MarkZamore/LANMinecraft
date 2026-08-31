using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// A pack update that does not change a file's length.
///
/// Every file in a pack folder carries the same fixed timestamp - they are
/// unpacked from an archive, and an archive has no clock - so a cheap
/// comparison has only the length to go on, and the recorded hash was handed
/// back whenever the length matched. An update that rewrites a file to the same
/// length was therefore invisible: the old hash matched the instance's old
/// copy, and the new file was dropped without a word.
///
/// It shipped. Zeroing six teleport delays turned "2" into "0" and "3" into
/// "0" in a file whose length did not move by a byte, four times over two days,
/// and every launcher kept the old one - including a file the pack had
/// explicitly claimed as its own, which is the one thing claiming is for.
/// </summary>
public sealed class PackUpdateSameSizeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-same-size-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { TempTree.Delete(_root); } catch { }
    }

    [Theory]
    // The claimed case: the pack decides this file outright.
    [InlineData(true)]
    // And the ordinary merge, where an untouched local copy still follows the
    // pack.
    [InlineData(false)]
    public async Task AnUpdateOfTheSameLength_StillReachesTheInstance(bool claimed)
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var packDir = Path.Combine(paths.Packs, "TestPack");
        Directory.CreateDirectory(Path.Combine(packDir, "config"));
        Directory.CreateDirectory(Path.Combine(packDir, PackInstanceService.LauncherDataRoot));
        File.WriteAllText(Path.Combine(packDir, "client.jar"), "jar");
        File.WriteAllText(
            Path.Combine(packDir, PackManifestService.ManifestFileName),
            """
            {
              "schemaVersion": 1,
              "minecraftVersion": "1.21.1",
              "loader": { "type": "vanilla" },
              "clientJar": "client.jar"
            }
            """);
        var packFile = Path.Combine(packDir, "config", "delays.snbt");
        File.WriteAllText(packFile, "warmup: 3\n");
        File.WriteAllText(
            Path.Combine(packDir, PackInstanceService.LauncherDataRoot, "pack-owned.txt"),
            claimed ? "config/delays.snbt\n" : "# nothing\n");
        WriteRevision(packDir, "first");

        using var service = new PackInstanceService(paths, new Logger(paths.LogFile));
        await service.PrepareAsync("TestPack");
        var instanceCopy = Path.Combine(service.GetInstanceDirectory("TestPack"), "config", "delays.snbt");
        Assert.Equal("warmup: 3\n", Read(instanceCopy));

        // The pack ships a new revision in which one digit changed, and every
        // file keeps the archive's timestamp, as they do.
        var stamp = File.GetLastWriteTimeUtc(packFile);
        File.WriteAllText(packFile, "warmup: 0\n");
        File.SetLastWriteTimeUtc(packFile, stamp);
        WriteRevision(packDir, "second");

        await service.PrepareAsync("TestPack");

        Assert.Equal("warmup: 0\n", Read(instanceCopy));
    }

    /// <summary>
    /// And within one revision the hashes are still kept: an instance the
    /// player edited is theirs, and nothing re-reads the whole pack to find out
    /// what it already knows.
    /// </summary>
    [Fact]
    public async Task WithinOneRevision_ALocalEditIsStillTheirs()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var packDir = Path.Combine(paths.Packs, "TestPack");
        Directory.CreateDirectory(Path.Combine(packDir, "config"));
        Directory.CreateDirectory(Path.Combine(packDir, PackInstanceService.LauncherDataRoot));
        File.WriteAllText(Path.Combine(packDir, "client.jar"), "jar");
        File.WriteAllText(
            Path.Combine(packDir, PackManifestService.ManifestFileName),
            """
            {
              "schemaVersion": 1,
              "minecraftVersion": "1.21.1",
              "loader": { "type": "vanilla" },
              "clientJar": "client.jar"
            }
            """);
        File.WriteAllText(Path.Combine(packDir, "config", "mine.toml"), "renderDistance = 8\n");
        File.WriteAllText(
            Path.Combine(packDir, PackInstanceService.LauncherDataRoot, "pack-owned.txt"), "# nothing\n");
        WriteRevision(packDir, "only");

        using var service = new PackInstanceService(paths, new Logger(paths.LogFile));
        await service.PrepareAsync("TestPack");
        var instanceCopy = Path.Combine(service.GetInstanceDirectory("TestPack"), "config", "mine.toml");

        await File.WriteAllTextAsync(instanceCopy, "renderDistance = 4\n");
        await service.PrepareAsync("TestPack");

        Assert.Equal("renderDistance = 4\n", Read(instanceCopy));
    }

    private static void WriteRevision(string packDir, string revision) =>
        File.WriteAllText(
            Path.Combine(packDir, PortablePackSyncService.SyncStateFileName),
            "{\"schemaVersion\":1,\"revision\":\"" + revision + "\",\"files\":{}}");

    private static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n");
}
