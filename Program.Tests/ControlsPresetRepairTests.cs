using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// A repeated key_ line stops NeoForge before its loading window, and the
/// player whose file has one cannot press the preset button to fix it - the
/// preset counts as applied. So the repair has to stand on its own.
/// </summary>
public sealed class ControlsPresetRepairTests
{
    [Fact]
    public void ARepeatedMapping_LeavesOnlyItsFirstLine()
    {
        var options = string.Join('\n',
            "version:3955",
            "key_key.attack:key.mouse.left",
            "key_key.rsrifle.reload:key.keyboard.r",
            "fov:0.5",
            "key_key.rsrifle.reload:key.keyboard.r",
            "");

        var (text, removed) = ControlsPresetService.WithoutDuplicateMappings(options);

        Assert.Equal(1, removed);
        Assert.Single(text.Split('\n'), line => line.StartsWith("key_key.rsrifle.reload:", StringComparison.Ordinal));
        Assert.Contains("fov:0.5", text, StringComparison.Ordinal);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    /// <summary>Two different values still collapse: the game reads the file as a map.</summary>
    [Fact]
    public void TheFirstValueWins_WhenTheCopiesDisagree()
    {
        var options = "key_key.jump:key.keyboard.space\nkey_key.jump:key.keyboard.b\n";

        var (text, removed) = ControlsPresetService.WithoutDuplicateMappings(options);

        Assert.Equal(1, removed);
        Assert.Contains("key_key.jump:key.keyboard.space", text, StringComparison.Ordinal);
        Assert.DoesNotContain("key.keyboard.b", text, StringComparison.Ordinal);
    }

    /// <summary>A clean file is returned untouched, byte for byte.</summary>
    [Fact]
    public void ACleanFile_IsNotRewritten()
    {
        var options = "key_key.attack:key.mouse.left\r\nkey_key.jump:key.keyboard.space\r\n";

        var (text, removed) = ControlsPresetService.WithoutDuplicateMappings(options);

        Assert.Equal(0, removed);
        Assert.Equal(options, text);
    }
}
