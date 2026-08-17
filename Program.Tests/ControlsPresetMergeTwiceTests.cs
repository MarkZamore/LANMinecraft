using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Applying the preset twice must leave the file the game can still read: a
/// second copy of one key_ line stops NeoForge before its loading window, with
/// "Duplicate key ... attempted merging values".
/// </summary>
public sealed class ControlsPresetMergeTwiceTests
{
    [Fact]
    public void ApplyingTwice_AddsNoSecondCopy()
    {
        var entries = new List<ControlsPresetEntry>
        {
            new("key.attack", "key.mouse.left"),
            new("key.rsrifle.reload", "key.keyboard.c"),
        };
        var options = "version:3955\nkey_key.attack:key.mouse.left\nfov:0.5\n";

        var (once, _) = ControlsPresetService.Merge(options, entries);
        var (twice, changedAgain) = ControlsPresetService.Merge(once, entries);

        Assert.Equal(0, changedAgain);
        Assert.Equal(
            once.Split('\n').Count(line => line.StartsWith("key_key.rsrifle.reload:", StringComparison.Ordinal)),
            twice.Split('\n').Count(line => line.StartsWith("key_key.rsrifle.reload:", StringComparison.Ordinal)));
        Assert.Single(twice.Split('\n').Where(line => line.StartsWith("key_key.rsrifle.reload:", StringComparison.Ordinal)));
    }

    /// <summary>A file that already carries a duplicate comes out with one line.</summary>
    [Fact]
    public void ADuplicateAlreadyThere_IsCollapsed()
    {
        var entries = new List<ControlsPresetEntry> { new("key.rsrifle.reload", "key.keyboard.c") };
        var options = "key_key.rsrifle.reload:key.keyboard.c\nkey_key.rsrifle.reload:key.keyboard.c\nfov:0.5\n";

        var (merged, _) = ControlsPresetService.Merge(options, entries);

        Assert.Single(merged.Split('\n').Where(line => line.StartsWith("key_key.rsrifle.reload:", StringComparison.Ordinal)));
    }
}
