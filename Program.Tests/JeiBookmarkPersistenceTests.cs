using System.Net;
using System.Net.Sockets;
using Minecraft;

namespace Minecraft.Tests;

// JEI keeps bookmarks per world under config/jei/world/**, and for a peer's
// world that path is derived from the address Minecraft joined. Both the folder
// and the address have to survive a launcher restart or bookmarks come back
// empty every session.
public sealed class JeiBookmarkPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-jei-bookmarks-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task InstancePreparation_KeepsJeiBookmarksForPeerWorlds()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var logger = new Logger(paths.LogFile);
        var packDir = Path.Combine(paths.Packs, "TestPack");
        Directory.CreateDirectory(packDir);
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

        using var service = new PackInstanceService(paths, logger);
        await service.PrepareAsync("TestPack");

        var gameDir = service.GetInstanceDirectory("TestPack");
        var serverBookmarks = Path.Combine(
            gameDir, "config", "jei", "world", "server", "MinecraftPortable_1a2b3c4d");
        Directory.CreateDirectory(serverBookmarks);
        var bookmarksFile = Path.Combine(serverBookmarks, "bookmarks.json");
        File.WriteAllText(bookmarksFile, """[{"ingredient":{"id":"minecraft:diamond"}}]""");
        var localBookmarks = Path.Combine(gameDir, "config", "jei", "world", "local", "bookmarks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(localBookmarks)!);
        File.WriteAllText(localBookmarks, """[{"ingredient":{"id":"minecraft:emerald"}}]""");

        // Closing the game and starting it again must not touch either file.
        await service.CleanupGeneratedLocalArtifactsAsync("TestPack", removeSessionLogs: false);
        await service.PrepareAsync("TestPack");

        Assert.True(File.Exists(bookmarksFile), "Bookmarks for a peer's world were deleted.");
        Assert.Contains("minecraft:diamond", File.ReadAllText(bookmarksFile), StringComparison.Ordinal);
        // The local folder was never deleted; this only guards against a future
        // sanitizer growing to cover it too.
        Assert.True(File.Exists(localBookmarks), "Bookmarks for the local world were deleted.");
    }
}
