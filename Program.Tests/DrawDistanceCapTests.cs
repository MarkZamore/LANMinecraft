using System.Text.RegularExpressions;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// How far the game is allowed to draw, written where a player can see it.
///
/// The server refuses to serve past a limit, and a client draws the smaller of
/// its own number and the server's. Left alone, that makes the settings screen
/// say thirty-two while the world shows sixteen, because the screen reads the
/// player's own number and only the renderer knows what the server allowed. So
/// the number in the file is brought down to meet the truth.
/// </summary>
public sealed class DrawDistanceCapTests
{
    [Theory]
    [InlineData("renderDistance:32", "renderDistance:16", 1)]
    [InlineData("simulationDistance:24", "simulationDistance:16", 1)]
    [InlineData("renderDistance:16", "renderDistance:16", 0)]
    [InlineData("renderDistance:8", "renderDistance:8", 0)]
    public void ADistanceAboveTheCap_ComesDownToIt(string line, string expected, int changed)
    {
        var (text, count) = PackInstanceService.CapDistances(line);

        Assert.Equal(expected, text);
        Assert.Equal(changed, count);
    }

    /// <summary>
    /// Nothing else in the file is this feature's business, and options.txt
    /// carries everything from key bindings to sound volumes.
    /// </summary>
    [Fact]
    public void EveryOtherSetting_IsLeftExactlyAsItWas()
    {
        const string options =
            "version:2865\r\nrenderDistance:32\r\nkey_key.attack:key.mouse.left\r\n" +
            "simulationDistance:32\r\nsoundCategory_master:0.7\r\n";

        var (text, changed) = PackInstanceService.CapDistances(options);

        Assert.Equal(2, changed);
        Assert.Contains("renderDistance:16", text, StringComparison.Ordinal);
        Assert.Contains("simulationDistance:16", text, StringComparison.Ordinal);
        Assert.Contains("version:2865", text, StringComparison.Ordinal);
        Assert.Contains("key_key.attack:key.mouse.left", text, StringComparison.Ordinal);
        Assert.Contains("soundCategory_master:0.7", text, StringComparison.Ordinal);
        // The game rewrites this file itself; handing it back with the line
        // endings swapped would show up as every line having changed.
        Assert.Contains("\r\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file with nothing to cap is returned as the very same text, so the
    /// caller can tell "nothing to do" from "rewritten identically" and leave
    /// the file's timestamp alone.
    /// </summary>
    [Fact]
    public void AFileAlreadyInsideTheCap_IsNotRewritten()
    {
        const string options = "renderDistance:12\nsimulationDistance:8\n";

        var (text, changed) = PackInstanceService.CapDistances(options);

        Assert.Equal(0, changed);
        Assert.Same(options, text);
    }

    /// <summary>
    /// A value the game did not write is not a number to be helpful about.
    /// </summary>
    [Theory]
    [InlineData("renderDistance:")]
    [InlineData("renderDistance:auto")]
    [InlineData("renderDistanceExtra:32")]
    public void AnythingThatIsNotOneOfTheseNumbers_IsUntouched(string line)
    {
        var (text, changed) = PackInstanceService.CapDistances(line);

        Assert.Equal(0, changed);
        Assert.Equal(line, text);
    }

    /// <summary>
    /// Shadows are drawn from the sun over a distance of their own, and cost a
    /// second pass over everything inside it. Past what the world is served,
    /// that is a shadow cast by ground nobody was sent. Iris keeps the setting
    /// in a file of its own, by an equals sign rather than a colon.
    /// </summary>
    [Theory]
    [InlineData("maxShadowRenderDistance=32", "maxShadowRenderDistance=16", 1)]
    [InlineData("maxShadowRenderDistance=8", "maxShadowRenderDistance=8", 0)]
    public void AShadowDistanceAboveTheCap_ComesDownToIt(string line, string expected, int changed)
    {
        var (text, count) = PackInstanceService.CapShadowDistance(line);

        Assert.Equal(expected, text);
        Assert.Equal(changed, count);
    }

    /// <summary>
    /// A properties file opens with a comment naming the day it was written,
    /// and a setting somebody commented out is not a setting.
    /// </summary>
    [Fact]
    public void CommentsInThePropertiesFile_AreNotSettings()
    {
        const string properties =
            "#Sun Aug 31 21:00:00 MSK 2026\n#maxShadowRenderDistance=32\nshaderPack=BSL.zip\n";

        var (text, changed) = PackInstanceService.CapShadowDistance(properties);

        Assert.Equal(0, changed);
        Assert.Same(properties, text);
    }

    /// <summary>
    /// The two halves of the same rule live in two languages: this one writes
    /// the number into the player's settings, and the javaagent refuses to
    /// serve past it. If they ever disagreed, the larger would be a setting
    /// that shows a distance nobody is sent, or a world served further than
    /// anyone is allowed to draw.
    /// </summary>
    [Fact]
    public void TheLauncherAndTheAgent_MeanTheSameDistance()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "Program", "IdentityAdapters", "Common", "PortableLanAutoPublishHooks.java"));
        var match = Regex.Match(source, @"SERVE_DISTANCE_LIMIT\s*=\s*(\d+)");

        Assert.True(match.Success, "The agent no longer names a serve distance limit.");
        Assert.Equal(PackInstanceService.FurthestChunks, int.Parse(match.Groups[1].Value));
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(relativeParts)}");
    }
}
