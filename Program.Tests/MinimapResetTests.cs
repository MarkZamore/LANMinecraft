using System.IO;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// When the pack resets the world's chunks, every player's minimap still holds
/// a drawing of ground that no longer exists. The pack names the reset once
/// and each instance performs it once, without anybody deleting folders by
/// hand - and never twice, so a map drawn after it stays.
/// </summary>
public sealed class MinimapResetTests
{
    [Fact]
    public void ThePackAsksOnce_AndTheDrawnMapGoes()
    {
        var (pack, instance, root) = NewPair(token: "2026-08-18 world trimmed");
        try
        {
            var tiles = Path.Combine(instance, "xaero", "world-map", "Chebupeli");
            Directory.CreateDirectory(tiles);
            File.WriteAllText(Path.Combine(tiles, "cache_1.zip"), "drawn");
            var waypoints = Path.Combine(instance, "xaero", "minimap", "Chebupeli");
            Directory.CreateDirectory(waypoints);
            File.WriteAllText(Path.Combine(waypoints, "waypoints.txt"), "база");
            var service = new MinimapResetService();

            Assert.True(MinimapResetService.NeedsApplying(pack, instance));
            Assert.Equal(1, service.Apply(pack, instance));

            Assert.False(Directory.Exists(Path.Combine(instance, "xaero", "world-map")));
            Assert.True(File.Exists(Path.Combine(waypoints, "waypoints.txt")), "waypoints are not a drawing");

            // A map drawn after the reset belongs to the player.
            Directory.CreateDirectory(tiles);
            File.WriteAllText(Path.Combine(tiles, "cache_1.zip"), "drawn again");
            Assert.False(MinimapResetService.NeedsApplying(pack, instance));
            Assert.Equal(0, service.Apply(pack, instance));
            Assert.True(File.Exists(Path.Combine(tiles, "cache_1.zip")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ANewResetInThePack_AsksAgain()
    {
        var (pack, instance, root) = NewPair(token: "first");
        try
        {
            var service = new MinimapResetService();
            service.Apply(pack, instance);
            Assert.False(MinimapResetService.NeedsApplying(pack, instance));

            File.WriteAllText(
                Path.Combine(pack, "launcher", MinimapResetService.TokenFileName),
                "second, after the dimensions were reset");

            Assert.True(MinimapResetService.NeedsApplying(pack, instance));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void APackThatAsksForNothing_ChangesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "ll8-map-" + Guid.NewGuid().ToString("N"));
        var pack = Path.Combine(root, "pack");
        var instance = Path.Combine(root, "instance");
        Directory.CreateDirectory(Path.Combine(pack, "launcher"));
        var tiles = Path.Combine(instance, "xaero", "world-map");
        Directory.CreateDirectory(tiles);
        try
        {
            Assert.False(MinimapResetService.NeedsApplying(pack, instance));
            Assert.Equal(0, new MinimapResetService().Apply(pack, instance));
            Assert.True(Directory.Exists(tiles));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (string Pack, string Instance, string Root) NewPair(string token)
    {
        var root = Path.Combine(Path.GetTempPath(), "ll8-map-" + Guid.NewGuid().ToString("N"));
        var pack = Path.Combine(root, "pack");
        var instance = Path.Combine(root, "instance");
        Directory.CreateDirectory(Path.Combine(pack, "launcher"));
        Directory.CreateDirectory(instance);
        File.WriteAllText(Path.Combine(pack, "launcher", MinimapResetService.TokenFileName), token);
        return (pack, instance, root);
    }
}
